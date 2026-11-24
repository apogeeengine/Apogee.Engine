using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Weft.Content;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Content.Import;
using Weft.Core.Assets;
using Weft.Core.Logging;
using Weft.Host.Editing;

namespace Weft.Editor.Assets;

/// <summary>One asset file the <see cref="AssetCatalog"/> lists.</summary>
public sealed class CatalogEntry
{
    private readonly Func<string, AssetRef> _refOf;
    private AssetRef? _ref;

    internal CatalogEntry(string path, string type, Func<string, AssetRef> refOf)
    {
        Path = path;
        Type = type;
        Name = VfsPath.FileName(path);
        _refOf = refOf;
    }

    /// <summary>The virtual path of the file (<c>res://models/car.glb</c>, <c>mod://id/props/lamp.glb</c>).</summary>
    public string Path { get; }

    /// <summary>The file's name, with its extension.</summary>
    public string Name { get; }

    /// <summary>The asset type of the file (see <see cref="AssetKinds"/>).</summary>
    public string Type { get; }

    /// <summary>
    /// The reference to the file, with its UID when it has one (<see cref="AssetDatabase.RefOf"/>). It is looked up the first time it is asked for, not when the catalog lists the file: a list of tens of thousands of
    /// files reads only the sidecars of the ones that are shown.
    /// </summary>
    public AssetRef Ref => _ref ??= _refOf(Path);
}

/// <summary>What a reference points at now.</summary>
public enum ReferenceState
{
    /// <summary>The reference is empty: it names nothing.</summary>
    Empty,

    /// <summary>The file at the reference's path is there.</summary>
    Found,

    /// <summary>The path has no file, but the UID names another one: the reference finds its asset where it moved to (and a save writes the new path).</summary>
    Moved,

    /// <summary>Neither the path nor the UID names a file.</summary>
    Missing,
}

/// <summary>Where a reference points: see <see cref="ReferenceState"/>, and the file it finds (null for <see cref="ReferenceState.Empty"/> and <see cref="ReferenceState.Missing"/>).</summary>
public readonly record struct ReferenceStatus(ReferenceState State, string? Path);

/// <summary>
/// The assets of the game by kind, for pickers: every file of the database's roots (<see cref="AssetDatabase.Roots"/>: <c>res://</c> first, then the packages' <c>mod://id/</c>) that has a kind
/// (<see cref="AssetKinds"/>), leaving out the import cache (<c>imported/</c>), hidden names (starting with a dot) and <c>.wimp</c> sidecars. A package under <c>res://packages/</c> that is a root of its
/// own is listed once, as <c>mod://</c>.
/// <para>
/// The listing is bounded (<see cref="MaxFiles"/>, <see cref="MaxDepth"/>, <see cref="MaxFolders"/>; <see cref="Truncated"/> says a limit was hit) and is made on a worker thread: <see cref="Entries"/> and
/// <see cref="Search"/> answer at once from the last complete listing, and start a new one when <see cref="Invalidate"/> was called (the hot reloader saw changes) or the last is older than
/// <see cref="RefreshSeconds"/>, so a popup over fifty thousand files never waits for the disk. Until the first listing is done they return nothing and <see cref="IsLoading"/> is true.
/// <see cref="Refresh"/> makes a listing on the calling thread (tests, and whatever needs the answer now). The members are safe to call from any thread.
/// </para>
/// </summary>
public sealed class AssetCatalog : IDisposable
{
    /// <summary>The most files a listing looks at; the rest are not listed (<see cref="Truncated"/>).</summary>
    public const int MaxFiles = 200_000;

    /// <summary>The deepest folder a listing enters.</summary>
    public const int MaxDepth = 24;

    /// <summary>The most folders a listing enters.</summary>
    public const int MaxFolders = 100_000;

    /// <summary>How old the listing may be, in seconds, before the next question starts a new one.</summary>
    public const double RefreshSeconds = 2.0;

    /// <summary>How long a notice (see <see cref="PostNotice"/>) stays, in seconds.</summary>
    public const double NoticeSeconds = 8.0;

    private static readonly string[] s_packageFolders = ["packages/", "dev_mods/"];

    private readonly VirtualFileSystem _files;
    private readonly AssetDatabase _assets;
    private readonly Func<double> _clock;
    private readonly int _maxFiles;
    private readonly int _maxFolders;
    private readonly int _maxDepth;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly Dictionary<string, ReferenceStatus> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssetPicker> _pickers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Text, double Until)> _notices = new(StringComparer.Ordinal);
    private string? _reveal;
    private Listing _current = Listing.Empty;
    private Task? _running;
    private bool _stale = true;
    private double _listedAt = double.NegativeInfinity;
    private double _statusesAt = double.NegativeInfinity;
    private int _kindsVersion = -1;
    private bool _disposed;

    /// <summary>A catalog of the assets of <paramref name="assets"/>' roots, read through <paramref name="files"/>. <paramref name="clock"/> gives seconds (a monotonic clock by default); tests pass their own.</summary>
    public AssetCatalog(VirtualFileSystem files, AssetDatabase assets, Func<double>? clock = null)
        : this(files, assets, clock, MaxFiles, MaxFolders, MaxDepth)
    {
    }

    // The limits are parameters so that a test can reach them without making two hundred thousand files.
    internal AssetCatalog(VirtualFileSystem files, AssetDatabase assets, Func<double>? clock, int maxFiles, int maxFolders, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(assets);
        _files = files;
        _assets = assets;
        _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _maxFiles = maxFiles;
        _maxFolders = maxFolders;
        _maxDepth = maxDepth;
    }

    /// <summary>The seconds on the catalog's clock: what the picker's notices age by.</summary>
    public double Now => _clock();

    /// <summary>Counts the listings made: it changes when the list a picker shows may have.</summary>
    public int Version
    {
        get
        {
            lock (_lock)
            {
                return _current.Number;
            }
        }
    }

    /// <summary>The first listing is not done yet (a listing is being made and nothing was listed before).</summary>
    public bool IsLoading
    {
        get
        {
            lock (_lock)
            {
                return _current.Number == 0 && !_disposed;
            }
        }
    }

    /// <summary>The last listing stopped at <see cref="MaxFiles"/>, <see cref="MaxFolders"/> or <see cref="MaxDepth"/>: some assets are not in it.</summary>
    public bool Truncated
    {
        get
        {
            lock (_lock)
            {
                return _current.Truncated;
            }
        }
    }

    /// <summary>Says that files changed (the hot reloader reported it): the next question starts a new listing, and what was learned about references (here and in the asset database) is forgotten.</summary>
    public void Invalidate()
    {
        _assets.Refresh();   // the database remembers the uids and places it found: a file that was deleted or moved is not what it says
        lock (_lock)
        {
            _stale = true;
            _statuses.Clear();
        }
    }

    /// <summary>The files of the asset type <paramref name="type"/> from the last listing: <c>res://</c> first, then each package in load order, each by path. Starts a new listing when one is due.</summary>
    public IReadOnlyList<CatalogEntry> Entries(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Listing listing = Current();
        return listing.ByType.TryGetValue(type, out CatalogEntry[]? entries) ? entries : [];
    }

    /// <summary>
    /// The files of the type whose name contains <paramref name="text"/> (not case-sensitive), then those that only have it in the folder part of their path; all of them when <paramref name="text"/> is empty.
    /// Each group is in the order of <see cref="Entries"/>.
    /// </summary>
    public List<CatalogEntry> Search(string type, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        IReadOnlyList<CatalogEntry> all = Entries(type);
        text = text.Trim();
        if (text.Length == 0)
        {
            return [.. all];
        }

        var byName = new List<CatalogEntry>();
        var byPath = new List<CatalogEntry>();
        foreach (CatalogEntry entry in all)
        {
            if (entry.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                byName.Add(entry);
            }
            else if (entry.Path.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                byPath.Add(entry);
            }
        }

        byName.AddRange(byPath);
        return byName;
    }

    /// <summary>The entry for the file at <paramref name="path"/> in the last listing, or null (it is not an asset of a kind, or the listing is older than the file).</summary>
    public CatalogEntry? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string canonical = VfsPath.IsVirtual(path) ? VfsPath.Normalize(path) : path;
        Listing listing = Current();
        return AssetKinds.TypeOfPath(canonical) is { } type && listing.ByType.TryGetValue(type, out CatalogEntry[]? entries) ? entries.FirstOrDefault(e => e.Path == canonical) : null;
    }

    /// <summary>
    /// Where <paramref name="reference"/> points now: its file is there, or the UID finds it elsewhere, or nothing has it. The answers are kept until <see cref="Invalidate"/> or <see cref="RefreshSeconds"/>, so
    /// the inspector may ask every frame.
    /// </summary>
    public ReferenceStatus StatusOf(AssetRef reference)
    {
        if (reference.IsEmpty)
        {
            return new ReferenceStatus(ReferenceState.Empty, null);
        }

        string key = reference.ToString();
        lock (_lock)
        {
            double now = _clock();
            if (now - _statusesAt > RefreshSeconds)
            {
                _statuses.Clear();
                _statusesAt = now;
            }

            if (_statuses.TryGetValue(key, out ReferenceStatus known))
            {
                return known;
            }
        }

        ReferenceStatus status = Look(reference);
        lock (_lock)
        {
            _statuses[key] = status;
        }

        return status;
    }

    /// <summary>The picker popup's model for the asset type <paramref name="type"/>: the same one each time, so that its filter text stays while the popup is open.</summary>
    public AssetPicker PickerFor(string type)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        lock (_lock)
        {
            if (!_pickers.TryGetValue(type, out AssetPicker? picker))
            {
                _pickers[type] = picker = new AssetPicker(this, type);
            }

            return picker;
        }
    }

    /// <summary>The reference to the file at <paramref name="path"/>, with its UID when it has one.</summary>
    public AssetRef RefOf(string path) => _assets.RefOf(path);

    /// <summary>
    /// Says something about an asset field that the person should read: a pick that could not be turned into an asset. It is shown under the field until it is replaced, <see cref="ClearNotice"/> removes it
    /// or <see cref="NoticeSeconds"/> have passed.
    /// </summary>
    /// <param name="key">What the notice is about (the component and field), so that a field shows its own.</param>
    /// <param name="text">The message.</param>
    public void PostNotice(string key, string text)
    {
        lock (_lock)
        {
            _notices[key] = (text, _clock() + NoticeSeconds);
        }
    }

    /// <summary>The message posted for <paramref name="key"/> that has not expired, or null.</summary>
    public string? NoticeFor(string key)
    {
        lock (_lock)
        {
            if (_notices.TryGetValue(key, out (string Text, double Until) notice))
            {
                if (_clock() <= notice.Until)
                {
                    return notice.Text;
                }

                _notices.Remove(key);
            }

            return null;
        }
    }

    /// <summary>Removes the message for <paramref name="key"/>.</summary>
    public void ClearNotice(string key)
    {
        lock (_lock)
        {
            _notices.Remove(key);
        }
    }

    /// <summary>Asks the asset browser to show <paramref name="path"/> (<i>Show in assets</i>): it opens the file's folder and marks the file the next time it is drawn.</summary>
    public void Reveal(string path)
    {
        lock (_lock)
        {
            _reveal = path;
        }
    }

    /// <summary>The path <see cref="Reveal"/> asked for, once: the asset browser takes it and it is forgotten.</summary>
    public string? TakeReveal()
    {
        lock (_lock)
        {
            string? path = _reveal;
            _reveal = null;
            return path;
        }
    }

    /// <summary>Makes a listing now, on the calling thread, and keeps it. A listing that is being made on the worker thread is waited for first.</summary>
    public void Refresh()
    {
        Wait();
        lock (_lock)
        {
            _stale = false;   // what Invalidate says from now on is newer than this listing
        }

        Listing listing = Build(_cancel.Token);
        lock (_lock)
        {
            Accept(listing);
        }
    }

    /// <summary>Waits until the listing on the worker thread, if there is one, is done. For tests and for shutting down.</summary>
    public void Wait()
    {
        Task? running;
        lock (_lock)
        {
            running = _running;
        }

        try
        {
            running?.Wait();
        }
        catch (AggregateException)
        {
            // What the worker could not do it logged; there is nothing more to wait for.
        }
    }

    /// <summary>Stops the worker thread's listing (it ends at the next folder) and keeps the catalog from starting another.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cancel.Cancel();
        Wait();
        _cancel.Dispose();
    }

    private Listing Current()
    {
        lock (_lock)
        {
            double now = _clock();
            if (!_disposed && _running is null && (_stale || now - _listedAt > RefreshSeconds || _kindsVersion != AssetKinds.Version))
            {
                _stale = false;
                _listedAt = now;
                CancellationToken token = _cancel.Token;
                _running = Task.Run(() => Work(token));
            }

            return _current;
        }
    }

    private void Work(CancellationToken token)
    {
        try
        {
            Listing listing = Build(token);
            lock (_lock)
            {
                if (!token.IsCancellationRequested)
                {
                    Accept(listing);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The catalog was disposed while it listed.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or ContentException or ObjectDisposedException or InvalidOperationException)
        {
            // A listing that fails (the folder went away, the session was disposed) leaves the old one; the next question tries again.
            Log.Warn("Editor", $"The asset list could not be read: {e.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _running = null;
            }
        }
    }

    // Under _lock.
    private void Accept(Listing listing)
    {
        _current = listing with { Number = _current.Number + 1 };
        _kindsVersion = listing.KindsVersion;
        _listedAt = _clock();
        _statuses.Clear();
    }

    private Listing Build(CancellationToken token)
    {
        int kindsVersion = AssetKinds.Version;
        List<string> roots = [.. _assets.Roots.Select(r => r.EndsWith('/') ? r : r + "/").Distinct(StringComparer.Ordinal)];
        var byType = new Dictionary<string, List<CatalogEntry>>(StringComparer.Ordinal);
        int count = 0;
        int folders = 0;
        bool truncated = false;
        foreach (string root in roots)
        {
            var pending = new Stack<(string Folder, int Depth)>();
            pending.Push((root, 0));
            var inRoot = new List<CatalogEntry>();
            while (pending.Count > 0 && !truncated)
            {
                token.ThrowIfCancellationRequested();
                (string folder, int depth) = pending.Pop();
                if (++folders > _maxFolders)
                {
                    truncated = true;
                    break;
                }

                foreach (string file in List(() => _files.EnumerateFiles(folder)))
                {
                    string name = VfsPath.FileName(file);
                    if (name.StartsWith('.') || name.EndsWith(ImportSidecar.Suffix, StringComparison.OrdinalIgnoreCase) || AssetKinds.TypeOfPath(name) is not { } type)
                    {
                        continue;
                    }

                    if (++count > _maxFiles)
                    {
                        truncated = true;
                        break;
                    }

                    inRoot.Add(new CatalogEntry(file, type, _assets.RefOf));
                }

                if (depth >= _maxDepth)
                {
                    if (List(() => _files.EnumerateDirectories(folder)).Any(d => !IsSkipped(root, roots, d)))
                    {
                        truncated = true;
                    }

                    continue;
                }

                foreach (string directory in List(() => _files.EnumerateDirectories(folder)))
                {
                    if (!IsSkipped(root, roots, directory))
                    {
                        pending.Push((directory, depth + 1));
                    }
                }
            }

            // The walk visits a folder before the ones below it, last sibling first: sorting makes the order by path, whatever the walk's.
            inRoot.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
            foreach (CatalogEntry entry in inRoot)
            {
                if (!byType.TryGetValue(entry.Type, out List<CatalogEntry>? list))
                {
                    byType[entry.Type] = list = [];
                }

                list.Add(entry);
            }
        }

        return new Listing(0, byType.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal), truncated, kindsVersion);
    }

    // A folder that the listing leaves out: the import cache and hidden folders, and a package's folder under res:// that is a root of its own (it is listed as mod://id/).
    private static bool IsSkipped(string root, List<string> roots, string directory)
    {
        string name = VfsPath.FileName(directory.TrimEnd('/'));
        string relative = directory[root.Length..].TrimEnd('/');
        if (name.StartsWith('.') || relative == AssetImporter.CacheFolder)
        {
            return true;
        }

        if (root == "res://")
        {
            foreach (string folder in s_packageFolders)
            {
                if (relative.StartsWith(folder, StringComparison.Ordinal) && relative.IndexOf('/', folder.Length) < 0 && roots.Contains($"mod://{relative[folder.Length..]}/"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<string> List(Func<List<string>> list)
    {
        try
        {
            return list();
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private ReferenceStatus Look(AssetRef reference)
    {
        if (reference.HasPath && VfsPath.IsVirtual(reference.Path))
        {
            string canonical = VfsPath.Normalize(reference.Path);
            if (_files.FileExists(canonical))
            {
                return new ReferenceStatus(ReferenceState.Found, canonical);
            }
        }

        // PathOf, not Resolve: Resolve says in the log that the file moved, and this is asked every frame.
        return _assets.PathOf(reference.Uid) is { } moved ? new ReferenceStatus(ReferenceState.Moved, moved) : new ReferenceStatus(ReferenceState.Missing, null);
    }

    private sealed record Listing(int Number, Dictionary<string, CatalogEntry[]> ByType, bool Truncated, int KindsVersion)
    {
        public static Listing Empty { get; } = new(0, [], false, -1);
    }
}
