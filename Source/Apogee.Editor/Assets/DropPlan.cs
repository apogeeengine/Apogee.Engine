using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Apogee.Content.Assets;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;

namespace Apogee.Editor.Assets;

/// <summary>What to do when a dropped file would land on a file the project already has.</summary>
public enum ClashPolicy
{
    /// <summary>The dropped file gets a free name (<c>car 2.glb</c>); a model with files of its own goes into a new folder. Nothing that exists is touched. The default.</summary>
    KeepBoth,

    /// <summary>The dropped file replaces the one in the project. Only when the user chose it, and only for a file (never a folder); the project's own <c>.import.xml</c> stays, so the asset keeps its UID.</summary>
    Replace,

    /// <summary>The dropped file is left out.</summary>
    Skip,
}

/// <summary>What the plan needs to know about the project's files; <see cref="ProjectDropTargets"/> answers from the game's file system, tests from a list.</summary>
public interface IDropTargets
{
    /// <summary>Something is at the virtual path (a file, a folder, or a link whose target is gone).</summary>
    bool Exists(string virtualPath);

    /// <summary>What is at the virtual path is a folder.</summary>
    bool IsFolder(string virtualPath);

    /// <summary>What is at the virtual path is a link (even one whose target is gone): a write there would go through it, to wherever it leads.</summary>
    bool IsLink(string virtualPath);

    /// <summary>A file can be written at the virtual path: it is in a writable root (the game's folder), not in a read-only one (a package's <c>mod://</c> folder, a pack).</summary>
    bool CanWrite(string virtualPath);
}

/// <summary>One file the drop will put in the project.</summary>
/// <param name="Source">The file on the disk (a full operating system path).</param>
/// <param name="Target">Where it goes: a virtual path under <c>res://</c>.</param>
/// <param name="IsImportable">An importer takes it and it is not part of another file: the job imports it after the copy.</param>
/// <param name="IsCompanion">It comes with another file (a glTF's buffers and images, a file's <c>.import.xml</c>): it is copied, and not imported on its own.</param>
/// <param name="Replaces">It overwrites a file the project has (the user chose <see cref="ClashPolicy.Replace"/>).</param>
/// <param name="Length">Its size in bytes when the plan was made.</param>
public sealed record PlannedFile(string Source, string Target, bool IsImportable, bool IsCompanion, bool Replaces, long Length);

/// <summary>A dropped file or folder the plan did not take, and why (the console says it).</summary>
public sealed record RefusedFile(string Path, string Reason);

/// <summary>
/// What a drop of files from the operating system will do, decided before anything is copied: which file goes where in the project, which are imported afterwards, which are refused and why. It reads the
/// dropped files (to list a folder, to find what a glTF refers to) and changes nothing; the copy is <see cref="ImportJob"/>'s. The rules:
/// <list type="bullet">
/// <item>Files go into the target folder, a dropped folder is recreated under it. Only the target folder's own root, <c>res://</c> and writable, is accepted: never a package's folder or a pack.</item>
/// <item>Only files the game uses are taken: what an importer takes (models, images, shaders), scenes and prefabs, and a short list of plain files (scripts, definitions, sounds, fonts, UI documents). Anything else is refused by name.</item>
/// <item>A name the project cannot use (see <see cref="VfsPath.IsValidSegment"/>: a colon, a Windows device name, a trailing dot), a hidden file, a link (symbolic link or junction), an empty file, a file over <see cref="MaxFileBytes"/> or one that pushes the drop over <see cref="MaxTotalBytes"/> is refused. The target path is made only of validated names, so no dropped name can climb out of the folder.</item>
/// <item>A glTF brings the files its <c>uri</c>s name (beside it or below it, never above); one that names a missing file, an absolute path or a path that climbs is refused whole. A file's <c>.import.xml</c> beside it comes along.</item>
/// <item>A name the project already has is never overwritten unless <see cref="ClashPolicy.Replace"/> was chosen (see there).</item>
/// <item>A folder drop reads at most <see cref="MaxFiles"/> files and <see cref="MaxDepth"/> levels.</item>
/// </list>
/// </summary>
public sealed class DropPlan
{
    /// <summary>The most files one drop reads (a dropped folder counts every file in it).</summary>
    public const int MaxFiles = 10_000;

    /// <summary>The deepest level of folders a dropped folder is followed to.</summary>
    public const int MaxDepth = 64;

    /// <summary>The largest file taken. The importers read a file whole, so a file this big is a mistake (a disc image dropped on the wrong window), not an asset.</summary>
    public const long MaxFileBytes = 512L * 1024 * 1024;

    /// <summary>The most bytes one drop takes in all.</summary>
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    // A .gltf is text. One bigger than this is a mistake or has its data embedded: the .glb form is the one for that.
    private const long MaxGltfBytes = 64L * 1024 * 1024;

    private static readonly string[] s_plainExtensions = [".luau", ".xml", ".wav", ".ogg", ".flac", ".mp3", ".ttf", ".otf", ".rml", ".rcss"];

    private static readonly string[] s_companionExtensions = [".bin", ".png", ".jpg", ".jpeg"];

    private readonly List<PlannedFile> _files = [];
    private readonly List<RefusedFile> _refused = [];
    private readonly List<string> _skipped = [];
    private readonly List<string> _clashes = [];

    private DropPlan()
    {
    }

    /// <summary>The files to copy, in order.</summary>
    public IReadOnlyList<PlannedFile> Files => _files;

    /// <summary>What was not taken, and why.</summary>
    public IReadOnlyList<RefusedFile> Refused => _refused;

    /// <summary>The names left out because the project has them and the policy was <see cref="ClashPolicy.Skip"/>.</summary>
    public IReadOnlyList<string> Skipped => _skipped;

    /// <summary>The project's files (virtual paths) that a dropped file wanted: the question for the user. The same whatever the policy.</summary>
    public IReadOnlyList<string> Clashes => _clashes;

    /// <summary>Some dropped file has the name of one the project has.</summary>
    public bool HasClashes => _clashes.Count > 0;

    /// <summary>The files an importer will take after the copy.</summary>
    public IEnumerable<PlannedFile> Importable => _files.Where(f => f.IsImportable);

    /// <summary>The bytes the copy will write.</summary>
    public long TotalBytes => _files.Sum(f => f.Length);

    /// <summary>The kinds of file a drop takes, for the message that refuses another.</summary>
    public static string AcceptedKinds(IEnumerable<string> importable) =>
        string.Join(", ", importable.Concat(AuthoredAssets.Extensions).Concat(s_plainExtensions).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal));

    /// <summary>
    /// Plans the drop of <paramref name="osPaths"/> (files and folders, operating system paths) into the virtual folder <paramref name="targetFolder"/>.
    /// </summary>
    /// <param name="targets">What the project has.</param>
    /// <param name="importableExtensions">The extensions an importer takes, with the dot (<c>ScriptSession.Importers</c>).</param>
    /// <param name="policy">What to do with a name the project has.</param>
    /// <param name="gameFolder">The game folder on the disk, when known: a dropped folder that is it, or holds it, is refused (it would copy the project into itself).</param>
    public static DropPlan Make(IReadOnlyList<string> osPaths, string targetFolder, IDropTargets targets, IReadOnlyCollection<string> importableExtensions, ClashPolicy policy = ClashPolicy.KeepBoth, string? gameFolder = null)
    {
        ArgumentNullException.ThrowIfNull(osPaths);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(importableExtensions);
        var plan = new DropPlan();
        new Builder(plan, targets, importableExtensions, policy, gameFolder).Run(osPaths, targetFolder);
        return plan;
    }

    /// <summary>The working state of one <see cref="Make"/>.</summary>
    private sealed class Builder(DropPlan plan, IDropTargets targets, IReadOnlyCollection<string> importable, ClashPolicy policy, string? gameFolder)
    {
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);   // virtual paths the plan has given out
        private readonly HashSet<string> _consumed = new(StringComparer.Ordinal); // sources that another file carries
        private int _seen;
        private int _folders;
        private bool _truncated;                                                  // a limit was hit and said so: nothing more is read
        private long _bytes;

        /// <summary>A file of the drop and where it lands (relative to the folder of its unit, with <c>/</c>).</summary>
        private sealed record Item(string Source, string Relative, bool Companion, bool IsImportable, long Length);

        /// <summary>A file with what it brings, placed (and renamed) as one.</summary>
        private sealed record Unit(string Folder, string PrimaryName, List<Item> Members);

        public void Run(IReadOnlyList<string> osPaths, string targetFolder)
        {
            if (!TryTargetFolder(targetFolder, out string folder, out string? problem))
            {
                foreach (string path in osPaths)
                {
                    plan._refused.Add(new RefusedFile(path, problem!));
                }

                return;
            }

            var candidates = new List<(string Source, string Folder)>();
            foreach (string given in osPaths)
            {
                string full;
                try
                {
                    full = Path.GetFullPath(given);
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    plan._refused.Add(new RefusedFile(given, "it is not a usable path"));
                    continue;
                }

                if (IsLink(full))
                {
                    plan._refused.Add(new RefusedFile(full, "it is a link: drop the file or folder it points to"));
                }
                else if (Directory.Exists(full))
                {
                    string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
                    if (name.StartsWith('.'))
                    {
                        plan._refused.Add(new RefusedFile(full, "it is a hidden folder"));
                    }
                    else if (HoldsGameFolder(full))
                    {
                        plan._refused.Add(new RefusedFile(full, "it is the game folder, or holds it: dropping it would copy the game into itself"));
                    }
                    else if (Name(name, full) is not null)
                    {
                        Collect(full, Join(folder, name), 0, candidates);
                    }
                }
                else if (File.Exists(full))
                {
                    candidates.Add((full, folder));
                }
                else
                {
                    plan._refused.Add(new RefusedFile(full, "it does not exist"));
                }
            }

            // The files that bring others first (a glTF with its buffers, a file with its settings), so that what they carry is not taken a second time on its own.
            List<Unit> units = [.. candidates.Where(c => IsGltf(c.Source)).Select(c => Group(c.Source, c.Folder)).OfType<Unit>()];
            foreach ((string source, string target) in candidates.Where(c => !IsGltf(c.Source) && !_consumed.Contains(c.Source)))
            {
                if (Single(source, target) is { } unit)
                {
                    units.Add(unit);
                }
            }

            foreach (Unit unit in units)
            {
                Place(unit);
            }
        }

        /// <summary>The target folder as a canonical virtual path, when files can be put there.</summary>
        private bool TryTargetFolder(string targetFolder, out string folder, out string? problem)
        {
            folder = targetFolder;
            problem = null;
            string? parseProblem = null;
            if (!targetFolder.StartsWith("res://", StringComparison.Ordinal) || !VfsPath.TryParse(targetFolder, out folder, out parseProblem))
            {
                folder = targetFolder;
                problem = $"files can only be dropped into the game's own folders (res://), not {targetFolder}" + (parseProblem is null ? "" : $" ({parseProblem})");
                return false;
            }

            if (IsCache(folder))
            {
                problem = $"{folder} holds what the importer makes: drop into a folder of the game's own files";
                return false;
            }

            if (!targets.CanWrite(Join(folder, "x")))
            {
                problem = $"{folder} is read-only (a package or a pack): drop into a folder of the game itself";
                return false;
            }

            if (targets.Exists(folder) && !targets.IsFolder(folder))
            {
                problem = $"{folder} is a file, not a folder";
                return false;
            }

            return true;
        }

        /// <summary>Lists the files of <paramref name="directory"/> into <paramref name="found"/> (with the virtual folder each lands in), within the limits.</summary>
        private void Collect(string directory, string virtualFolder, int depth, List<(string, string)> found)
        {
            if (_truncated)
            {
                return;
            }

            if (IsCache(virtualFolder))
            {
                plan._refused.Add(new RefusedFile(directory, $"{virtualFolder} would hold what the importer makes: rename the folder"));
                return;
            }

            if (++_folders > MaxFiles)
            {
                Truncate(directory, "folders");
                return;
            }

            if (depth > MaxDepth)
            {
                plan._refused.Add(new RefusedFile(directory, $"the folders go deeper than {MaxDepth} levels"));
                return;
            }

            List<string> entries;
            try
            {
                entries = [.. Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal)];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                plan._refused.Add(new RefusedFile(directory, "it cannot be read: " + e.Message));
                return;
            }

            foreach (string entry in entries)
            {
                if (_truncated)
                {
                    return;
                }

                string name = Path.GetFileName(entry);
                if (name.StartsWith('.'))
                {
                    continue; // hidden: version control, editor leftovers, our own temporary files
                }

                if (IsLink(entry))
                {
                    plan._refused.Add(new RefusedFile(entry, "it is a link: links are not followed"));
                }
                else if (Directory.Exists(entry))
                {
                    if (Name(name, entry) is not null)
                    {
                        Collect(entry, Join(virtualFolder, name), depth + 1, found);
                    }
                }
                else if (++_seen > MaxFiles)
                {
                    Truncate(directory, "files");
                    return;
                }
                else
                {
                    found.Add((entry, virtualFolder));
                }
            }
        }

        /// <summary>A limit was reached: said once, and the walk ends everywhere.</summary>
        private void Truncate(string where, string what)
        {
            _truncated = true;
            plan._refused.Add(new RefusedFile(where, $"the drop holds more than {MaxFiles} {what}: only the first were taken"));
        }

        /// <summary>The importer's own folder (<c>res://imported</c>): its index and UID list are not for dropping files into or over.</summary>
        private static bool IsCache(string virtualPath) => VfsPath.IsUnder(virtualPath, "res://" + AssetImporter.CacheFolder);

        /// <summary>The folder <paramref name="dropped"/> is the game folder or has it inside (compared as the file system does, links resolved).</summary>
        private bool HoldsGameFolder(string dropped)
        {
            if (gameFolder is null)
            {
                return false;
            }

            StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            static string Slash(string path) => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
            try
            {
                string game = Path.GetFullPath(gameFolder);
                return Slash(game).StartsWith(Slash(Path.GetFullPath(dropped)), comparison)
                    || Slash(Apogee.Editor.Editing.VirtualPaths.RealPath(game)).StartsWith(Slash(Apogee.Editor.Editing.VirtualPaths.RealPath(Path.GetFullPath(dropped))), comparison);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        private static bool IsGltf(string path) => path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

        /// <summary>A file that is not a glTF: itself, and its <c>.import.xml</c> when it has one beside it.</summary>
        private Unit? Single(string source, string folder)
        {
            string name = Path.GetFileName(source);
            if (name.EndsWith(ImportSidecar.Suffix, StringComparison.OrdinalIgnoreCase))
            {
                plan._refused.Add(new RefusedFile(source, "these are the import settings of another file: drop that file, and they come with it"));
                return null;
            }

            if (MakeItem(source, name, companion: false) is not { } primary)
            {
                return null;
            }

            var members = new List<Item> { primary };
            AddSidecar(source, name, members);
            return new Unit(folder, name, members);
        }

        /// <summary>A glTF with the buffers and images it names.</summary>
        private Unit? Group(string source, string folder)
        {
            string name = Path.GetFileName(source);
            if (_consumed.Contains(source) || MakeItem(source, name, companion: false) is not { } primary)
            {
                return null;
            }

            long bytesBefore = _bytes - primary.Length;
            string? problem = null;
            List<string> uris = [];
            try
            {
                if (primary.Length > MaxGltfBytes)
                {
                    problem = "it is too large for a .gltf (use the .glb form)";
                }
                else
                {
                    uris = ExternalUris(source);
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                problem = "it is not a readable glTF: " + e.Message;
            }

            var members = new List<Item> { primary };
            string directory = Path.GetDirectoryName(source)!;
            var relatives = new List<string>();
            foreach (string uri in uris)
            {
                if (RelativeUri(uri) is not { } decoded)
                {
                    problem ??= $"it names {uri}, which is not a file beside it or below it";
                }
                else if (!relatives.Contains(decoded, PathComparer))
                {
                    relatives.Add(decoded); // a.bin and a%2Ebin are one file
                }
            }

            foreach (string relative in relatives)
            {
                if (problem is not null)
                {
                    break;
                }

                string file = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (LinkOnTheWay(directory, relative) || !File.Exists(file))
                {
                    problem = $"it names {relative}, which is missing (or is a link)";
                }
                else if (MakeItem(file, relative, companion: true) is { } companion)
                {
                    members.Add(companion);
                }
                else
                {
                    problem = $"{relative}, which it needs, was refused (see above)";
                }
            }

            if (problem is not null)
            {
                _bytes = bytesBefore; // nothing of a refused model is taken, so none of it counts
                plan._refused.Add(new RefusedFile(source, problem));
                return null;
            }

            foreach (Item member in members)
            {
                _consumed.Add(member.Source);
                _consumed.Add(member.Source + ImportSidecar.Suffix); // the settings of a texture a model carries are not used: the model's own are
            }

            AddSidecar(source, name, members);
            return new Unit(folder, name, members);
        }

        private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>The file, or a folder between <paramref name="directory"/> and it, is a link (a folder linked to somewhere else would bring in files from anywhere).</summary>
        private static bool LinkOnTheWay(string directory, string relative)
        {
            string path = directory;
            foreach (string segment in relative.Split('/'))
            {
                path = Path.Combine(path, segment);
                if (IsLink(path))
                {
                    return true;
                }
            }

            return false;
        }

        private void AddSidecar(string source, string relative, List<Item> members)
        {
            string sidecar = source + ImportSidecar.Suffix;
            if (!IsLink(sidecar) && File.Exists(sidecar) && MakeItem(sidecar, relative + ImportSidecar.Suffix, companion: true) is { } member)
            {
                members.Add(member);
                _consumed.Add(sidecar);
            }
        }

        /// <summary>The file as an item of a unit, or null (refused, with the reason in the plan) when the project does not take it.</summary>
        private Item? MakeItem(string source, string relative, bool companion)
        {
            string name = Path.GetFileName(source);
            if (name.StartsWith('.'))
            {
                plan._refused.Add(new RefusedFile(source, "it is a hidden file"));
                return null;
            }

            foreach (string segment in relative.Split('/'))
            {
                if (Name(segment, source) is null)
                {
                    return null;
                }
            }

            bool isSidecar = name.EndsWith(ImportSidecar.Suffix, StringComparison.OrdinalIgnoreCase);
            bool takesImport = importable.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            if (!companion && !takesImport && !AuthoredAssets.IsAuthored(name) && !s_plainExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                plan._refused.Add(new RefusedFile(source, $"the game does not use this kind of file (it takes {AcceptedKinds(importable)})"));
                return null;
            }

            if (companion && !isSidecar && !s_companionExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                plan._refused.Add(new RefusedFile(source, "a model can bring only .bin buffers and PNG or JPEG images"));
                return null;
            }

            long length;
            try
            {
                length = new FileInfo(source).Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                plan._refused.Add(new RefusedFile(source, "it cannot be read: " + e.Message));
                return null;
            }

            // A file with no bytes is no asset; it is also what a pipe, a device and the like report, and reading one of those would never end.
            if (length == 0)
            {
                plan._refused.Add(new RefusedFile(source, "it is empty (or not a regular file)"));
                return null;
            }

            if (length > MaxFileBytes)
            {
                plan._refused.Add(new RefusedFile(source, $"it is larger than {MaxFileBytes / (1024 * 1024)} MB"));
                return null;
            }

            if (_bytes + length > MaxTotalBytes)
            {
                plan._refused.Add(new RefusedFile(source, $"the drop is larger than {MaxTotalBytes / (1024 * 1024 * 1024)} GB in all"));
                return null;
            }

            _bytes += length;
            return new Item(source, relative, companion || isSidecar, takesImport && !companion && !isSidecar, length);
        }

        /// <summary>Decides where a unit lands and adds its files to the plan.</summary>
        private void Place(Unit unit)
        {
            List<string> wanted = [.. unit.Members.Select(m => Join(unit.Folder, m.Relative))];
            if (wanted.Any(IsCache))
            {
                plan._refused.Add(new RefusedFile(unit.Members[0].Source, $"it would land in {unit.Folder}, which holds what the importer makes"));
                return;
            }

            bool existing = wanted.Any(targets.Exists);
            bool taken = wanted.Any(_taken.Contains);
            if (existing)
            {
                plan._clashes.Add(wanted[0]);
            }

            if (!existing && !taken)
            {
                Add(unit, unit.Folder, replace: false);
            }
            else if (!taken && policy == ClashPolicy.Skip)
            {
                plan._skipped.Add(wanted[0]);
            }
            else if (!taken && policy == ClashPolicy.Replace && !wanted.Any(targets.IsFolder) && !wanted.Any(targets.IsLink))
            {
                Add(unit, unit.Folder, replace: true);
            }
            else
            {
                KeepBoth(unit);
            }
        }

        /// <summary>A free name: <c>name 2.ext</c> for one file (and its settings), a new folder <c>name 2</c> for a model with files of its own (their names are in the model, so they cannot change).</summary>
        private void KeepBoth(Unit unit)
        {
            string stem = Path.GetFileNameWithoutExtension(unit.PrimaryName);
            string extension = Path.GetExtension(unit.PrimaryName);
            bool group = unit.Members.Any(m => m.Companion && !m.Relative.EndsWith(ImportSidecar.Suffix, StringComparison.OrdinalIgnoreCase));
            for (int n = 2; ; n++)
            {
                if (group)
                {
                    string folder = Join(unit.Folder, n == 2 && !Occupied(Join(unit.Folder, stem)) ? stem : $"{stem} {n}");
                    if (!Occupied(folder) && unit.Members.All(m => !Occupied(Join(folder, m.Relative))))
                    {
                        Add(unit, folder, replace: false);
                        return;
                    }
                }
                else
                {
                    string renamed = $"{stem} {n}{extension}";
                    var members = unit.Members.Select((m, i) => i == 0 ? m with { Relative = renamed } : m with { Relative = renamed + ImportSidecar.Suffix }).ToList();
                    if (members.All(m => !Occupied(Join(unit.Folder, m.Relative))))
                    {
                        Add(unit with { Members = members }, unit.Folder, replace: false);
                        return;
                    }
                }
            }
        }

        private bool Occupied(string path) => _taken.Contains(path) || targets.Exists(path);

        private void Add(Unit unit, string folder, bool replace)
        {
            foreach (Item member in unit.Members)
            {
                string target = Join(folder, member.Relative);
                bool exists = targets.Exists(target);
                if (replace && exists && member.Relative.EndsWith(ImportSidecar.Suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // the project's settings stay, with the asset's UID in them
                }

                _taken.Add(target);
                plan._files.Add(new PlannedFile(member.Source, target, member.IsImportable, member.Companion, replace && exists, member.Length));
            }
        }

        /// <summary>The name if the project can use it as one segment of a path; otherwise null and the reason is refused.</summary>
        private string? Name(string name, string path)
        {
            if (!VfsPath.IsValidSegment(name, out string? problem))
            {
                plan._refused.Add(new RefusedFile(path, "the game cannot use this name: " + problem));
                return null;
            }

            return name;
        }
    }

    private static string Join(string folder, string name) => folder.EndsWith('/') ? folder + name : folder + "/" + name;

    /// <summary>A symbolic link or a junction: its target may be anywhere.</summary>
    internal static bool IsLink(string path)
    {
        try
        {
            // Symbolic links and junctions. Not every reparse point: a cloud file that is not downloaded yet (OneDrive) is one, and is an ordinary file for our purposes.
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true; // what cannot even be inspected is not copied
        }
    }

    /// <summary>The <c>uri</c> of every buffer and image of the glTF at <paramref name="path"/> that is not embedded data.</summary>
    private static List<string> ExternalUris(string path)
    {
        var uris = new List<string>();
        using FileStream stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        foreach (string list in new[] { "buffers", "images" })
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(list, out JsonElement items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("uri", out JsonElement uri) && uri.ValueKind == JsonValueKind.String
                        && uri.GetString() is { } text && !text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        uris.Add(text);
                    }
                }
            }
        }

        return uris;
    }

    /// <summary>
    /// The file a glTF's <c>uri</c> names as a path relative to the glTF, with <c>/</c>; null when it is not one that stays beside the glTF or below it (an absolute path, a drive, a scheme, a
    /// <c>..</c> segment). The uri is percent-encoded, as the glTF standard has it.
    /// </summary>
    internal static string? RelativeUri(string uri)
    {
        string path;
        try
        {
            path = Uri.UnescapeDataString(uri);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (path.Length == 0 || path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':', StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        string[] segments = path.Split('/');
        return segments.Any(s => s.Length == 0 || s is "." or "..") ? null : string.Join('/', segments);
    }
}
