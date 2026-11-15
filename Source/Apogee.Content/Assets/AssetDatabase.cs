using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;
using Apogee.Core.Assets;
using Apogee.Core.Logging;

namespace Apogee.Content.Assets;

/// <summary>
/// Which file an asset's UID names, and which UID a file has. The UID of an imported file is in its <c>.import.xml</c>, the UID of an authored one (a scene) is in its root element
/// (<see cref="AuthoredAssets"/>); the database reads them from the files, so it is always about what is on disk. To find a UID it first asks the project's list, <c>imported/assets.xml</c>
/// (written by <see cref="Import.AssetImporter"/>: one read per root), and when that has nothing (or is out of date: the file moved after the import) it scans the files of its
/// <see cref="Roots"/> once. It never writes: giving files UIDs is the importer's job, so a read-only root (a pack, a mod, a shipped game) is fine, and its assets with no UID just have none.
/// <para>
/// Results are remembered until <see cref="Refresh"/> (call it after an import, or when files change on disk). Safe to use from several threads.
/// </para>
/// </summary>
public sealed partial class AssetDatabase(VirtualFileSystem files)
{
    /// <summary>The name of the project's list of UIDs, in the <c>imported</c> folder of a root.</summary>
    public const string ListFileName = "assets.xml";

    /// <summary>The version written in the list. A list with a newer one is ignored (the scan finds the same things, only slower).</summary>
    public const int ListVersion = 1;

    private const string LogCategory = "Content";

    // Where the game's own packages and development mods live under res://: a file there is also at mod://<id>/ when that package is mounted (see PackageSet).
    private static readonly string[] s_packageFolders = ["packages/", "dev_mods/"];

    // A project is not nested deeper than this, or made of more folders than this; a link that leads back to a folder above it would otherwise make a tree without end.
    private const int MaxFolderDepth = 64;
    private const int MaxFolders = 100_000;

    private readonly Lock _lock = new();

    // The UID of each file looked at (None for "has none"), the file a UID names, and the files that share a UID.
    private readonly Dictionary<string, AssetUid> _uidOfPath = new(StringComparer.Ordinal);
    private readonly Dictionary<AssetUid, string> _pathOfUid = [];
    private readonly Dictionary<AssetUid, string> _listed = [];
    private readonly Dictionary<string, HashSet<string>> _shippedSources = new(StringComparer.Ordinal); // per root: the sources a cooked root's index names (empty for a root that is not cooked)
    private readonly HashSet<AssetUid> _shipped = []; // listed by a cooked list: the sources are not in the pack, so the files cannot vouch for them
    private List<(AssetUid Uid, IReadOnlyList<string> Paths)> _duplicates = [];
    private bool _seeded;
    private bool _scanned;

    /// <summary>
    /// The roots of the projects whose assets can be found: <c>res://</c> first, and the host adds <c>mod://&lt;id&gt;/</c> for each mounted package in load order. When two roots have the same
    /// UID the first one wins. Change it before the database is used, or call <see cref="Refresh"/> after.
    /// </summary>
    public IList<string> Roots { get; } = ["res://"];

    /// <summary>
    /// The groups of files that have the same UID (a copied asset and its <c>.import.xml</c>, or two mods that ship the same file), each with its paths and the first one the UID names.
    /// Importing a project makes a UID of its own for the copies; this is what is left when a root cannot be written to. It looks at every file of the roots once.
    /// </summary>
    public IReadOnlyList<(AssetUid Uid, IReadOnlyList<string> Paths)> Duplicates
    {
        get
        {
            lock (_lock)
            {
                Scan();
                return [.. _duplicates];
            }
        }
    }

    /// <summary>Forgets what was read, so that the next question reads the files again.</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            _uidOfPath.Clear();
            _pathOfUid.Clear();
            _listed.Clear();
            _shipped.Clear();
            _shippedSources.Clear();
            _duplicates = [];
            _seeded = false;
            _scanned = false;
        }
    }

    /// <summary>The UID of the file at <paramref name="path"/>, read from its <c>.import.xml</c> or, for an authored file, its root; <see cref="AssetUid.None"/> when the file does not exist or has none.</summary>
    public AssetUid UidOf(string path)
    {
        if (!TryCanonical(path, out string canonical))
        {
            return AssetUid.None;
        }

        lock (_lock)
        {
            return UidOfCanonical(canonical);
        }
    }

    /// <summary>The reference to the file at <paramref name="path"/>: the path, normalized, and its UID (none when it has none).</summary>
    public AssetRef RefOf(string path) => new(TryCanonical(path, out string canonical) ? canonical : path, UidOf(path));

    /// <summary>
    /// The file that <paramref name="uid"/> names, or null when no file of the roots has it. A file of a package that is mounted at <c>mod://id/</c> (and is in <see cref="Roots"/>) is
    /// given by that address, the one the rest of the engine uses for it, and not by its place under <c>res://packages/id/</c>.
    /// </summary>
    public string? PathOf(AssetUid uid)
    {
        if (uid.IsNone)
        {
            return null;
        }

        lock (_lock)
        {
            Seed();
            if (_pathOfUid.TryGetValue(uid, out string? listed) && (Verified(listed, uid) || _shipped.Contains(uid)))
            {
                return Preferred(listed);
            }

            // Not in the list, or the list is out of date (the file was moved after the last import): the files are the truth.
            Scan();
            return _pathOfUid.TryGetValue(uid, out string? found) ? Preferred(found) : null;
        }
    }

    /// <summary>
    /// The file the reference means: its path when that file exists (the path is what people wrote, and it wins; an imported asset of a cooked pack counts as existing, since the pack holds its artifact and
    /// not the source), otherwise the file that has its UID (logged, so that whoever saves the document that
    /// holds the reference writes the new path), otherwise null.
    /// </summary>
    public string? Resolve(AssetRef reference)
    {
        if (reference.HasPath && TryCanonical(reference.Path, out string canonical) && (files.FileExists(canonical) || IsShippedSource(canonical)))
        {
            return canonical;
        }

        if (PathOf(reference.Uid) is not { } moved)
        {
            return null;
        }

        if (reference.HasPath && !(TryCanonical(reference.Path, out string wrote) && wrote == moved))
        {
            Log.Info(LogCategory, $"{reference.Path} moved to {moved}: save to update the reference.");
        }

        return moved;
    }

    /// <summary>
    /// What a script or a document names, as a path: <c>uid://…</c> becomes the file that has it, anything else is returned as it is (it is not checked here, loading it says if it is wrong).
    /// </summary>
    /// <exception cref="ContentException">The text is a <c>uid://</c> that is not valid or that no file has.</exception>
    public string ResolvePath(string pathOrUid)
    {
        if (!pathOrUid.StartsWith(AssetUid.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return pathOrUid;
        }

        if (!AssetUid.TryParse(pathOrUid, out AssetUid uid))
        {
            throw new ContentException($"'{pathOrUid}' is not a valid asset uid: expected {AssetUid.Scheme} and {AssetUid.DigitCount} characters.");
        }

        return PathOf(uid) ?? throw new ContentException($"Unknown uid {uid}: no asset has it (the file may have been deleted, or imported without its .import.xml).");
    }

    // ---- The list: imported/assets.xml ----------------------------------------------------------------------------------------------

    /// <summary>
    /// What <c>imported/assets.xml</c> of <paramref name="root"/> says: each asset's path and UID. The paths are written as the folder was seen when it was imported (<c>res://models/car.glb</c>), and
    /// are put under <paramref name="root"/> here, so a package imported on its own (at <c>res://</c>) and mounted elsewhere (<c>mod://id/</c>) has the right paths. A missing, damaged or newer
    /// list is empty (what it holds can be found by scanning), and a row with no path or a bad UID is skipped.
    /// </summary>
    internal static Dictionary<string, AssetUid> Load(VirtualFileSystem files, string root = "res://")
    {
        var rows = new Dictionary<string, AssetUid>(StringComparer.Ordinal);
        string path = root + Import.AssetImporter.CacheFolder + "/" + ListFileName;
        if (!files.FileExists(path))
        {
            return rows;
        }

        try
        {
            XmlNode list = XmlFile.Read(files, path); // text, or cooked in a shipped pack
            if (list.Name != "Assets" || (int.TryParse(list.Attribute("version"), NumberStyles.None, CultureInfo.InvariantCulture, out int version) && version > ListVersion))
            {
                return rows;
            }

            foreach (XmlNode node in list.Children.Where(c => c.Name == "Asset"))
            {
                if (node.Attribute("path") is { Length: > 0 } assetPath && VfsPath.TrySplit(assetPath, out _, out string rest) && AssetUid.TryParse(node.Attribute("uid") ?? "", out AssetUid uid))
                {
                    rows[root + rest] = uid;
                }
            }
        }
        catch (ContentException)
        {
            // A damaged list (or one cooked by a newer engine) is as good as none.
        }

        return rows;
    }

    /// <summary>Writes <c>res://imported/assets.xml</c>, sorted by path so that the same assets always make the same file, and only when the text is not already what is there.</summary>
    internal static void Save(VirtualFileSystem files, IEnumerable<KeyValuePair<string, AssetUid>> rows)
    {
        var list = new XmlNode("Assets");
        list.SetAttribute("version", ListVersion.ToString(CultureInfo.InvariantCulture));
        foreach ((string path, AssetUid uid) in rows.Where(r => !r.Value.IsNone).OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var node = new XmlNode("Asset");
            node.SetAttribute("uid", uid.ToString());
            node.SetAttribute("path", path);
            list.Children.Add(node);
        }

        string target = "res://" + Import.AssetImporter.CacheFolder + "/" + ListFileName;
        string text = list.ToXml();
        if (!files.FileExists(target) || files.ReadAllText(target) != text)
        {
            files.WriteAllText(target, text);
        }
    }

    // ---- Reading the files ------------------------------------------------------------------------------------------------------------

    private void Seed()
    {
        if (_seeded)
        {
            return;
        }

        _seeded = true;
        foreach (string root in RootList())
        {
            // A shipped game's list is cooked (GamePacker), and its imported assets have lost their sources and sidecars: the list is the only record of their UIDs, so it is believed.
            bool cooked = IsCooked(root);
            foreach ((string path, AssetUid uid) in Load(files, root))
            {
                if (_listed.TryAdd(uid, path)) // the first root in the list wins
                {
                    _pathOfUid.TryAdd(uid, path);
                    if (cooked)
                    {
                        _shipped.Add(uid);
                    }
                }
            }
        }
    }

    /// <summary>Whether <paramref name="path"/> is a source that a cooked root imported: its file is not in the pack, but its artifact is, and the index says so.</summary>
    private bool IsShippedSource(string path)
    {
        if (!Import.ImportIndex.TryRootOf(path, out string root) || !path.StartsWith(root, StringComparison.Ordinal))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_shippedSources.TryGetValue(root, out HashSet<string>? sources))
            {
                sources = IsCooked(root) ? [.. Import.ImportIndex.Load(files, root).Entries.Select(e => e.Source)] : [];
                _shippedSources[root] = sources;
            }

            return sources.Contains(path[root.Length..]);
        }
    }

    private bool IsCooked(string root)
    {
        string path = root + Import.AssetImporter.CacheFolder + "/" + ListFileName;
        if (!files.FileExists(path))
        {
            return false;
        }

        using System.IO.Stream stream = files.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return XmlBinary.IsBinary(head[..stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)]);
    }

    /// <summary>Whether the file the list names is still there and still has that UID.</summary>
    private bool Verified(string path, AssetUid uid) => files.FileExists(path) && UidOfCanonical(path) == uid;

    private AssetUid UidOfCanonical(string path)
    {
        if (_uidOfPath.TryGetValue(path, out AssetUid known))
        {
            return known;
        }

        AssetUid uid = AssetUid.None;
        if (files.FileExists(path))
        {
            if (AuthoredAssets.IsAuthored(path))
            {
                using System.IO.Stream stream = files.OpenRead(path);
                uid = AuthoredAssets.ReadUid(stream);
            }
            else if (files.FileExists(path + ImportSidecar.Suffix))
            {
                uid = ImportSidecar.ReadUid(files.ReadAllText(path + ImportSidecar.Suffix));
            }
        }

        _uidOfPath[path] = uid;
        return uid;
    }

    /// <summary>Looks at every asset file of the roots, once: this is the full truth about which file has which UID, and where the duplicates are.</summary>
    private void Scan()
    {
        if (_scanned)
        {
            return;
        }

        _scanned = true;
        Seed();
        _pathOfUid.Clear(); // the list was only a guess; what is found below replaces it
        var groups = new Dictionary<AssetUid, List<string>>();
        List<string> roots = RootList();
        foreach (string file in EnumerateRootFiles())
        {
            string asset = file.EndsWith(ImportSidecar.Suffix, StringComparison.Ordinal) ? file[..^ImportSidecar.Suffix.Length] : AuthoredAssets.IsAuthored(file) ? file : "";
            if (asset.Length == 0)
            {
                continue;
            }

            AssetUid uid = UidOfCanonical(asset);
            if (uid.IsNone)
            {
                continue;
            }

            if (!groups.TryGetValue(uid, out List<string>? group))
            {
                groups[uid] = group = [];
            }

            group.Add(asset);
        }

        _duplicates = [];
        foreach ((AssetUid uid, List<string> paths) in groups)
        {
            // The file the project's list names keeps the UID (the others are the copies); with no list the first in load order does.
            string keeper = _listed.TryGetValue(uid, out string? listed) ? paths.Find(p => p == (ModAddressOf(listed, roots) ?? listed)) ?? paths[0] : paths[0];
            _pathOfUid[uid] = keeper;
            if (paths.Count > 1)
            {
                paths.Remove(keeper);
                paths.Insert(0, keeper);
                _duplicates.Add((uid, paths));
                Log.Warn(LogCategory, $"{uid} is on {paths.Count} files ({string.Join(", ", paths)}): {keeper} is used. Import the project to give the copies UIDs of their own.");
            }
        }
    }

    // ---- Paths ------------------------------------------------------------------------------------------------------------------------

    /// <summary>The virtual file system the database reads (for <see cref="AssetReferences"/> and <c>Move</c>, which work on the files of the same roots).</summary>
    internal VirtualFileSystem Files => files;

    /// <summary>
    /// Every file of the <see cref="Roots"/>, each once: the files of the import cache (<c>imported/</c>) and of hidden folders are left out, and so is a package's file under <c>res://packages/id/</c>
    /// when that package is a root of its own (it is listed under <c>mod://id/</c>). This is the set of files the database and the reference search both look at.
    /// </summary>
    internal IEnumerable<string> EnumerateRootFiles()
    {
        List<string> roots = RootList();
        foreach (string root in roots)
        {
            foreach (string file in files.EnumerateFiles(root, "*", recursive: true, MaxFolderDepth, MaxFolders))
            {
                string relative = file[root.Length..];
                if (!IsCacheOrHidden(relative) && !(root == "res://" && IsMountedPackageFile(relative, roots)))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>
    /// Every way the engine writes the path of a file: the path itself and, for a file of a package, its other address (<c>res://packages/id/x</c> is also <c>mod://id/x</c>, and the other way round;
    /// <c>mod://id/x</c> could be in either of the two package folders). Paths are canonical.
    /// </summary>
    internal static List<string> SpellingsOf(string canonical)
    {
        List<string> spellings = [canonical];
        if (canonical.StartsWith("mod://", StringComparison.Ordinal))
        {
            string inside = canonical["mod://".Length..];
            spellings.AddRange(s_packageFolders.Select(folder => "res://" + folder + inside));
            return spellings;
        }

        foreach (string folder in s_packageFolders)
        {
            string prefix = "res://" + folder;
            int slash = canonical.StartsWith(prefix, StringComparison.Ordinal) ? canonical.IndexOf('/', prefix.Length) : -1;
            if (slash > prefix.Length)
            {
                spellings.Add($"mod://{canonical[prefix.Length..slash]}/{canonical[(slash + 1)..]}");
            }
        }

        return spellings;
    }

    private List<string> RootList() => [.. Roots.Select(r => r.EndsWith('/') ? r : r + "/").Distinct(StringComparer.Ordinal)];

    private static bool TryCanonical(string path, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrEmpty(path) || !VfsPath.IsVirtual(path))
        {
            return false;
        }

        canonical = VfsPath.Normalize(path);
        return true;
    }

    private static bool IsCacheOrHidden(string relative)
    {
        if (relative.StartsWith(Import.AssetImporter.CacheFolder + "/", StringComparison.Ordinal))
        {
            return true;
        }

        return relative.Split('/').Any(segment => segment.StartsWith('.'));
    }

    /// <summary>Whether the file (relative to <c>res://</c>) is in a package folder whose package is a root of its own: it is then found there, under <c>mod://</c>, and not twice.</summary>
    private static bool IsMountedPackageFile(string relative, List<string> roots) => ModAddressOf("res://" + relative, roots) is not null;

    /// <summary>The <c>mod://id/…</c> address of a file under <c>res://packages/id/</c> (or <c>dev_mods</c>), when <c>mod://id/</c> is one of the roots.</summary>
    private static string? ModAddressOf(string path, List<string> roots)
    {
        if (!path.StartsWith("res://", StringComparison.Ordinal))
        {
            return null;
        }

        string relative = path["res://".Length..];
        foreach (string folder in s_packageFolders)
        {
            if (!relative.StartsWith(folder, StringComparison.Ordinal))
            {
                continue;
            }

            string inside = relative[folder.Length..];
            int slash = inside.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && roots.Contains($"mod://{inside[..slash]}/"))
            {
                return $"mod://{inside[..slash]}/{inside[(slash + 1)..]}";
            }
        }

        return null;
    }

    private string Preferred(string path) => ModAddressOf(path, RootList()) ?? path;
}
