using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Content.Xml;
using Weft.Core.Assets;
using Weft.Core.Logging;

namespace Weft.Content.Import;

/// <summary>One artifact of the import: the source it came from, what it was made from, and the file.</summary>
/// <param name="Source">The path of the asset relative to <c>res://</c> (<c>models/car.glb</c>).</param>
/// <param name="Artifact">The path of the result, relative to <c>res://</c> (<c>imported/models/car.glb.wmdl</c>).</param>
/// <param name="Key">A hash of everything that affects the result (see <see cref="AssetImporter"/>): when it is the same, the artifact is still good.</param>
/// <param name="Dependencies">The other files the import read (a glTF's buffers and textures), relative to <c>res://</c>.</param>
public sealed record ImportEntry(string Source, string Artifact, string Key, IReadOnlyList<string> Dependencies);

/// <summary>Where one asset stands: what an asset browser shows next to a file.</summary>
public enum AssetStatus
{
    /// <summary>No importer takes the file (a script, a text file): nothing to import.</summary>
    NotImportable,

    /// <summary>An importer takes it and there is no artifact yet (or its file is gone).</summary>
    NotImported,

    /// <summary>The artifact is made from the file as it is now.</summary>
    UpToDate,

    /// <summary>The file (or what the import read, or its <c>.wimp</c>) changed since the artifact was made.</summary>
    Stale,
}

/// <summary>What <see cref="AssetImporter.Run"/> did.</summary>
/// <param name="Imported">The assets that were imported.</param>
/// <param name="UpToDate">The assets whose artifact was already good.</param>
/// <param name="Removed">The artifacts that were deleted because their source is gone.</param>
/// <param name="Failed">The assets that could not be imported, with the reason.</param>
/// <param name="Reassigned">The assets that shared their UID with another (a copy of an asset and its <c>.wimp</c>) and were given a new one: the path relative to <c>res://</c>, the UID it had and the one it has now.</param>
public sealed record ImportReport(IReadOnlyList<string> Imported, IReadOnlyList<string> UpToDate, IReadOnlyList<string> Removed, IReadOnlyList<(string Source, string Message)> Failed, IReadOnlyList<(string Path, AssetUid Old, AssetUid New)>? Reassigned = null)
{
    public bool Success => Failed.Count == 0;

    /// <summary>The assets that were given a new UID because another had the same (see the constructor's parameter); empty when none.</summary>
    public IReadOnlyList<(string Path, AssetUid Old, AssetUid New)> Reassigned { get; init; } = Reassigned ?? [];
}

/// <summary>
/// Turns the assets of a game into the engine's own formats. It is a small registry: each <see cref="IAssetImporter"/> takes some extensions (models, images, shaders…) and writes an artifact for
/// each asset, in <c>res://imported/</c> beside an <c>index.xml</c> that says which source each came from (<see cref="ContentLoader"/> reads it). Artifacts are kept: an asset is imported
/// again only when its <em>key</em> changes, the SHA-256 of the importer's name and fingerprint, the asset's <c>.wimp</c> settings, the asset, and every file the import read for it (a model's
/// buffers and textures, the modules a shader imports). Artifacts whose source is gone are deleted. Importers run in the order of the list, and a file that an earlier import read (a model's
/// image) is not imported on its own. <c>res://</c> has to be writable.
/// <para>
/// Importing also gives assets their UID (<see cref="AssetUid"/>): an imported file gets one in its <c>.wimp</c> (made if there is none), an authored one (<see cref="AuthoredAssets"/>) in its root,
/// and <c>imported/assets.xml</c> lists them all for <see cref="AssetDatabase"/>. The UID is not part of the key (<see cref="ImportSettings.KeyText"/>), so minting one never imports anything again.
/// A UID that is already there is never changed, except to tell a copy from its original.
/// </para>
/// </summary>
public sealed class AssetImporter(VirtualFileSystem files, IReadOnlyList<IAssetImporter>? importers = null)
{
    /// <summary>Bumped when the key or the index changes, so that old artifacts are made again.</summary>
    public const int ImporterVersion = 4;

    public const string CacheFolder = "imported";

    private readonly IReadOnlyList<IAssetImporter> _importers = importers ?? Defaults;

    /// <summary>The importers of the content library: models, then images.</summary>
    public static IReadOnlyList<IAssetImporter> Defaults { get; } = [new ModelImporter(), new ImageImporter()];

    /// <summary>What the index in <paramref name="files"/> says has been imported (empty when there is none).</summary>
    public static IReadOnlyList<ImportEntry> LoadIndex(VirtualFileSystem files) => [.. ImportIndex.Load(files).Entries];

    /// <summary>Imports what needs it. <paramref name="force"/> imports everything again.</summary>
    public ImportReport Run(bool force = false)
    {
        ImportIndex index = ImportIndex.Load(files);
        var uids = new UidRun(AssetDatabase.Load(files));
        var imported = new List<string>();
        var upToDate = new List<string>();
        var failed = new List<(string, string)>();
        var kept = new Dictionary<string, ImportEntry>(StringComparer.Ordinal);
        var consumed = new HashSet<string>(StringComparer.Ordinal);

        List<string> sources = [.. files.EnumerateFiles("res://", "*", recursive: true)
            .Select(p => p["res://".Length..])
            .Where(p => !p.StartsWith(CacheFolder + "/", StringComparison.Ordinal) && !p.Split('/').Any(segment => segment.StartsWith('.')))];

        // In the order of the list: what an importer reads is part of its asset, and is not imported on its own by a later one.
        foreach (IAssetImporter importer in _importers)
        {
            foreach (string source in sources.Where(s => Has(s, importer.Extensions) && !consumed.Contains(s)))
            {
                Import(importer, source, index, force, imported, upToDate, failed, kept, consumed, uids);
            }
        }

        // Authored files are not imported (they are already in the engine's format), but they need a UID too.
        List<string> authored = [.. sources.Where(AuthoredAssets.IsAuthored)];
        foreach (string source in authored)
        {
            MintAuthoredUid(source, uids, failed);
        }

        var removed = new List<string>();
        foreach (ImportEntry stale in index.Entries.Where(e => !kept.ContainsKey(e.Source) && !failed.Any(f => f.Item1 == e.Source)))
        {
            files.Delete("res://" + stale.Artifact);
            removed.Add(stale.Artifact);
        }

        // A failed import keeps its old artifact and entry: the game still has something to load until the source is fixed.
        foreach (ImportEntry entry in index.Entries.Where(e => failed.Any(f => f.Item1 == e.Source)))
        {
            kept[entry.Source] = entry;
        }

        ImportIndex.Save(files, kept.Values.OrderBy(e => e.Source, StringComparer.Ordinal));

        // The UIDs as the files say now (what was just minted, and what failed imports still have), with copies told apart, then written to the project's list.
        Dictionary<string, AssetUid> current = CurrentUids(kept.Keys, authored);
        List<(string, AssetUid, AssetUid)> reassigned = ReassignCopies(current, uids);
        ReportLostUids(current, uids);
        AssetDatabase.Save(files, current);
        return new ImportReport(imported, upToDate, removed, failed, reassigned);
    }

    /// <summary>Where the asset <paramref name="source"/> (relative to <c>res://</c>: <c>models/car.glb</c>) stands, from the index and the file as it is now. It reads the files, so it is for a person looking at one, not for every file every frame.</summary>
    public AssetStatus StatusOf(string source)
    {
        if (ImporterOf(source) is not { } importer || !files.FileExists("res://" + source))
        {
            return AssetStatus.NotImportable;
        }

        ImportIndex index = ImportIndex.Load(files);
        if (index.Find(source) is not { } entry || !files.FileExists("res://" + entry.Artifact))
        {
            return AssetStatus.NotImported;
        }

        string settingsPath = "res://" + source + ImportSidecar.Suffix;
        string settingsText = files.FileExists(settingsPath) ? files.ReadAllText(settingsPath) : "";
        return KeyOf(importer, settingsText, files.ReadAllBytes("res://" + source), entry.Dependencies) == entry.Key ? AssetStatus.UpToDate : AssetStatus.Stale;
    }

    /// <summary>Imports one asset again whether or not it changed, and leaves the rest of the index as it is. A file no importer takes is reported as failed.</summary>
    public ImportReport Reimport(string source)
    {
        if (ImporterOf(source) is not { } importer)
        {
            return new ImportReport([], [], [], [(source, "no importer takes this file")]);
        }

        ImportIndex index = ImportIndex.Load(files);
        var imported = new List<string>();
        var upToDate = new List<string>();
        var failed = new List<(string, string)>();
        var kept = index.Entries.ToDictionary(e => e.Source, StringComparer.Ordinal);
        Dictionary<string, AssetUid> listed = AssetDatabase.Load(files);
        Import(importer, source, index, force: true, imported, upToDate, failed, kept, new HashSet<string>(StringComparer.Ordinal), new UidRun(listed));
        ImportIndex.Save(files, kept.Values.OrderBy(e => e.Source, StringComparer.Ordinal));

        // Only this asset's row of the project's list changes (the copies of the other assets are told apart by Run, which looks at all of them).
        if (SidecarUid(source) is { IsNone: false } uid)
        {
            listed["res://" + source] = uid;
        }
        else
        {
            listed.Remove("res://" + source);
        }

        AssetDatabase.Save(files, listed);
        return new ImportReport(imported, upToDate, [], failed);
    }

    private IAssetImporter? ImporterOf(string source) =>
        source.StartsWith(CacheFolder + "/", StringComparison.Ordinal) || source.Split('/').Any(segment => segment.StartsWith('.')) ? null : _importers.FirstOrDefault(i => Has(source, i.Extensions));

    private void Import(IAssetImporter importer, string source, ImportIndex index, bool force, List<string> imported, List<string> upToDate, List<(string, string)> failed, Dictionary<string, ImportEntry> kept, HashSet<string> consumed, UidRun uids)
    {
        try
        {
            string settingsPath = "res://" + source + ImportSidecar.Suffix;
            string settingsText = files.FileExists(settingsPath) ? files.ReadAllText(settingsPath) : "";
            ImportSettings settings = settingsText.Length > 0 ? ImportSettings.Parse(settingsText, settingsPath) : new ImportSettings();
            byte[] sourceBytes = files.ReadAllBytes("res://" + source);

            if (!force && index.Find(source) is { } previous && files.FileExists("res://" + previous.Artifact) && KeyOf(importer, settingsText, sourceBytes, previous.Dependencies) == previous.Key)
            {
                kept[source] = previous;
                consumed.UnionWith(previous.Dependencies);
                EnsureSidecar(source, sourceBytes, uids, []); // a project imported before UIDs, hashes or version 2 existed gets them here, without importing anything
                upToDate.Add(source);
                return;
            }

            var context = new ImportContext(files, source, sourceBytes, settings);
            if (importer.Import(context) is not { } result)
            {
                return;
            }

            List<string> dependencies = [.. context.Read.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            string artifactPath = $"{CacheFolder}/{source}{result.ArtifactExtension}";
            files.WriteAllBytes("res://" + artifactPath, result.Bytes);

            // The sidecar is written after the import (the UID, the hash of the source, the sub-assets that were minted), and the key is made from it as it is now: what is written is not part of the key,
            // but the text that people wrote may have changed its shape around it (a root that had no content has some now).
            EnsureSidecar(source, sourceBytes, uids, context.NewSubAssets);
            string writtenText = files.FileExists(settingsPath) ? files.ReadAllText(settingsPath) : "";
            kept[source] = new ImportEntry(source, artifactPath, KeyOf(importer, writtenText, sourceBytes, dependencies), dependencies);
            consumed.UnionWith(dependencies);
            imported.Add(source);
        }
        catch (Exception e) when (e is ContentException or System.IO.IOException or UnauthorizedAccessException)
        {
            failed.Add((source, e.Message));
        }
    }

    private const string LogCategory = "Content";

    /// <summary>What one run knows about UIDs: the list the last import wrote, and what this run did with it.</summary>
    private sealed class UidRun(IReadOnlyDictionary<string, AssetUid> listed)
    {
        /// <summary>The UID the last import recorded for each path (<c>res://…</c>).</summary>
        public IReadOnlyDictionary<string, AssetUid> Listed => listed;

        /// <summary>The paths that got a UID that was not in a file of theirs: a new one, or the one the list had for them.</summary>
        public HashSet<string> Given { get; } = new(StringComparer.Ordinal);

        /// <summary>The paths that got the UID the list had for them (a sidecar that was deleted): they must not take the UID from a file that has it now.</summary>
        public HashSet<string> Recovered { get; } = new(StringComparer.Ordinal);

        /// <summary>The UID for a path that has none: the list's, if it had one for the path (the sidecar was deleted: the asset stays what it was), or a new one.</summary>
        public AssetUid Make(string path)
        {
            Given.Add(path);
            if (listed.TryGetValue(path, out AssetUid recorded))
            {
                Recovered.Add(path);
                return recorded;
            }

            return AssetUid.New();
        }
    }

    // Not ReadAllText: that drops a byte order mark, and the file is written back.
    private string ReadText(string path) => Encoding.UTF8.GetString(files.ReadAllBytes(path));

    private AssetUid SidecarUid(string source) => files.FileExists("res://" + source + ImportSidecar.Suffix) ? ImportSidecar.ReadUid(ReadText("res://" + source + ImportSidecar.Suffix)) : AssetUid.None;

    /// <summary>
    /// Brings an imported asset's <c>.wimp</c> up to date after it was imported or found current: the UID if it has none (the one the project's list has for the path when the sidecar was deleted, so deleting
    /// a sidecar loses nothing, a new one otherwise), <c>version="2"</c> (so a version 1 sidecar becomes a version 2 one: the same file), <c>sourceHash</c> as the source is now, and a <c>&lt;SubAsset&gt;</c> for each of
    /// <paramref name="newSubAssets"/>. A sidecar that exists has its text kept (only those attributes and elements are added or replaced), and one that does not is made. The key ignores all of it, so nothing imports again.
    /// The file is written only when the text changes.
    /// </summary>
    private void EnsureSidecar(string source, byte[] sourceBytes, UidRun uids, IReadOnlyList<ImportSubAsset> newSubAssets)
    {
        string path = "res://" + source + ImportSidecar.Suffix;
        string text = files.FileExists(path) ? ReadText(path) : "";
        string updated = ImportSidecar.ReadUid(text).IsNone ? ImportSidecar.SetUid(text, uids.Make("res://" + source)) : text;
        updated = ImportSidecar.Refresh(updated, Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(), newSubAssets);
        if (updated != text)
        {
            files.WriteAllText(path, updated);
        }
    }

    /// <summary>Gives an authored file (a scene) a UID in its root element if it has none. A file that is not XML (a damaged document) has nowhere to put one and is left alone.</summary>
    private void MintAuthoredUid(string source, UidRun uids, List<(string, string)> failed)
    {
        string path = "res://" + source;
        try
        {
            byte[] bytes = files.ReadAllBytes(path);
            if (!AuthoredAssets.ReadUid(bytes).IsNone)
            {
                return;
            }

            string text = Encoding.UTF8.GetString(bytes);
            if (!text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('<'))
            {
                return;
            }

            files.WriteAllText(path, AuthoredAssets.SetUid(text, uids.Make(path)));
        }
        catch (Exception e) when (e is ContentException or System.IO.IOException or UnauthorizedAccessException)
        {
            failed.Add((source, e.Message));
        }
    }

    /// <summary>The UID of every imported asset in <paramref name="imported"/> and every authored file in <paramref name="authored"/> (relative paths) that has one, by <c>res://</c> path, as the files say now.</summary>
    private Dictionary<string, AssetUid> CurrentUids(IEnumerable<string> imported, IEnumerable<string> authored)
    {
        var current = new Dictionary<string, AssetUid>(StringComparer.Ordinal);
        foreach (string source in imported)
        {
            if (SidecarUid(source) is { IsNone: false } uid)
            {
                current["res://" + source] = uid;
            }
        }

        foreach (string source in authored)
        {
            using System.IO.Stream stream = files.OpenRead("res://" + source);
            if (AuthoredAssets.ReadUid(stream) is { IsNone: false } uid)
            {
                current["res://" + source] = uid;
            }
        }

        return current;
    }

    /// <summary>
    /// Gives a new UID to every asset that shares its UID with another one, which is what copying an asset together with its <c>.wimp</c> (or a folder of them) does. Of each group one file
    /// keeps the UID, and it is the original:
    /// <list type="number">
    /// <item>A path that was just given the UID the list had for it (its sidecar was deleted) never keeps it against a file that has it in a file of its own: that file moved, with its sidecar.</item>
    /// <item>Among the others, the path the list recorded for the UID (the file that had it before this import, wherever the copy sorts), and failing that the first path in ordinal order.</item>
    /// </list>
    /// The others get a new UID in their sidecar or root (only the attribute changes, so nothing imports again). If one cannot be written to it keeps the shared UID with a warning; the database
    /// then reports the group in <see cref="AssetDatabase.Duplicates"/>.
    /// </summary>
    private List<(string, AssetUid, AssetUid)> ReassignCopies(Dictionary<string, AssetUid> current, UidRun uids)
    {
        var reassigned = new List<(string, AssetUid, AssetUid)>();
        foreach (IGrouping<AssetUid, string> group in current.GroupBy(c => c.Value, c => c.Key).Where(g => g.Count() > 1).OrderBy(g => g.Key.Value))
        {
            List<string> paths = [.. group.Order(StringComparer.Ordinal)];
            List<string> candidates = [.. paths.Where(p => !uids.Recovered.Contains(p))];
            if (candidates.Count == 0)
            {
                candidates = paths;
            }

            string keeper = candidates.Find(p => uids.Listed.TryGetValue(p, out AssetUid recorded) && recorded == group.Key) ?? candidates[0];
            foreach (string path in paths.Where(p => p != keeper))
            {
                AssetUid replacement = AssetUid.New();
                try
                {
                    files.WriteAllText(
                        AuthoredAssets.IsAuthored(path) ? path : path + ImportSidecar.Suffix,
                        AuthoredAssets.IsAuthored(path) ? AuthoredAssets.SetUid(ReadText(path), replacement) : ImportSidecar.RenewSubAssetUids(ImportSidecar.SetUid(ReadText(path + ImportSidecar.Suffix), replacement))); // a copy shares no sub-asset UIDs with its original either
                }
                catch (Exception e) when (e is ContentException or System.IO.IOException or UnauthorizedAccessException)
                {
                    Log.Warn(LogCategory, $"{path} has the same UID as {keeper} ({group.Key}) and cannot be given another: {e.Message}");
                    continue;
                }

                current[path] = replacement;
                reassigned.Add((path["res://".Length..], group.Key, replacement));
                Log.Info(LogCategory, $"{path} had the UID of {keeper} (a copy?) and has a new one, {replacement}.");
            }
        }

        return reassigned;
    }

    /// <summary>Says so when UIDs disappeared in a run that made new ones: the likely cause is a file that was moved without its <c>.wimp</c>, which is a new asset to the engine.</summary>
    private static void ReportLostUids(Dictionary<string, AssetUid> current, UidRun uids)
    {
        HashSet<AssetUid> present = [.. current.Values];
        List<string> lost = [.. uids.Listed.Where(l => !present.Contains(l.Value)).Select(l => l.Key).Order(StringComparer.Ordinal)];
        List<string> made = [.. uids.Given.Except(uids.Recovered).Order(StringComparer.Ordinal)];
        if (lost.Count > 0 && made.Count > 0)
        {
            Log.Info(LogCategory, $"New UIDs were made for {string.Join(", ", made)} while {string.Join(", ", lost)} no longer exist: if one was moved without its .wimp it is a new asset now, and references to its old path or UID do not follow it. Move the .wimp together with the file.");
        }
    }

    /// <summary>The hash of everything the result depends on.</summary>
    private string KeyOf(IAssetImporter importer, string settingsText, byte[] sourceBytes, IReadOnlyList<string> dependencies)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(ReadOnlySpan<byte> bytes) => hash.AppendData(bytes);
        void AddText(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text + "\n"));

        AddText($"weft-import-{ImporterVersion}");
        AddText(importer.Name);
        AddText(importer.Fingerprint);
        AddText(ImportSettings.KeyText(settingsText)); // without the uid: minting one must not import the asset again
        Add(SHA256.HashData(sourceBytes));
        foreach (string dependency in dependencies)
        {
            AddText(dependency);
            Add(files.FileExists("res://" + dependency) ? SHA256.HashData(files.ReadAllBytes("res://" + dependency)) : []);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool Has(string path, IReadOnlyList<string> extensions) => extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}

/// <summary><c>res://imported/index.xml</c>: which source each artifact came from.</summary>
internal sealed class ImportIndex
{
    private readonly Dictionary<string, ImportEntry> _entries = new(StringComparer.Ordinal);

    public const string Path = "res://" + AssetImporter.CacheFolder + "/index.xml";

    public IReadOnlyCollection<ImportEntry> Entries => _entries.Values;

    public ImportEntry? Find(string source) => _entries.GetValueOrDefault(source);

    /// <summary>The root of the project a file belongs to, where its <c>imported/</c> folder is: <c>mod://id/</c> for a package's file, the scheme's root (<c>res://</c>, <c>shaders://</c>) for the others.</summary>
    public static bool TryRootOf(string path, out string root)
    {
        root = "";
        if (!VfsPath.TrySplit(path, out string scheme, out string rest))
        {
            return false;
        }

        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        root = scheme == "mod" && slash > 0 ? $"mod://{rest[..slash]}/" : scheme + "://";
        return true;
    }

    /// <summary>The index of the project at <paramref name="root"/> in <paramref name="files"/>; an empty one when there is none or it cannot be read (everything is then imported again).</summary>
    public static ImportIndex Load(VirtualFileSystem files, string root = "res://")
    {
        var index = new ImportIndex();
        string path = root + AssetImporter.CacheFolder + "/index.xml";
        if (!files.FileExists(path))
        {
            return index;
        }

        try
        {
            foreach (XmlNode node in XmlNode.Parse(files.ReadAllText(path), path).Children.Where(c => c.Name == "Import"))
            {
                if (node.Attribute("source") is { Length: > 0 } source && node.Attribute("artifact") is { Length: > 0 } artifact)
                {
                    index._entries[source] = new ImportEntry(source, artifact, node.Attribute("key") ?? "", [.. node.Children.Where(c => c.Name == "Dependency").Select(c => c.Attribute("path") ?? "")]);
                }
            }
        }
        catch (XmlParseException)
        {
            // A damaged index is as good as none.
        }

        return index;
    }

    public static void Save(VirtualFileSystem files, IEnumerable<ImportEntry> entries)
    {
        var root = new XmlNode("Imports");
        root.SetAttribute("version", AssetImporter.ImporterVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (ImportEntry entry in entries)
        {
            var node = new XmlNode("Import");
            node.SetAttribute("source", entry.Source);
            node.SetAttribute("artifact", entry.Artifact);
            node.SetAttribute("key", entry.Key);
            foreach (string dependency in entry.Dependencies)
            {
                var child = new XmlNode("Dependency");
                child.SetAttribute("path", dependency);
                node.Children.Add(child);
            }

            root.Children.Add(node);
        }

        files.WriteAllText(Path, root.ToXml());
    }
}

/// <summary>
/// Loads models and textures the way a game wants them: the imported artifact when there is one (<see cref="AssetImporter"/>), the source file otherwise. A game that was never imported loads
/// from its sources, so the same call works everywhere. A package's files have artifacts when the package shipped them (it was imported as a folder of its own, its <c>imported/</c> is at
/// <c>mod://id/imported</c>). It does not check that the artifact is current: importing is the step that does.
/// </summary>
public sealed class ContentLoader(VirtualFileSystem files)
{
    private readonly Dictionary<string, ImportIndex> _indexes = new(StringComparer.Ordinal);

    private readonly Lazy<AssetDatabase> _assets = new(() => new AssetDatabase(files));

    /// <summary>Which file has which UID, over the same files. The host sets its <see cref="AssetDatabase.Roots"/> (the mounted packages) once they are mounted.</summary>
    public AssetDatabase Assets => _assets.Value;

    /// <summary>What a script or a document names as a path: a <c>uid://</c> becomes the file that has it (<see cref="ContentException"/> when none does), anything else is returned as it is.</summary>
    public string ResolveAssetPath(string pathOrUid) => Assets.ResolvePath(pathOrUid);

    /// <summary>Reads the indexes (and the UIDs) again (after an import).</summary>
    public void Refresh()
    {
        _indexes.Clear();
        if (_assets.IsValueCreated)
        {
            _assets.Value.Refresh();
        }
    }

    /// <summary>The virtual path of the artifact that replaces the asset at <paramref name="path"/>, or null when it was not imported.</summary>
    public string? ArtifactPathFor(string path) => ImportedFor(VfsPath.Normalize(path))?.Path;

    /// <summary>The virtual paths of the files the artifact of the asset at <paramref name="path"/> was made from (a shader's imported modules), or an empty list when it was not imported.</summary>
    public IReadOnlyList<string> ArtifactDependenciesFor(string path)
    {
        string canonical = VfsPath.Normalize(path);
        return ImportedFor(canonical) is { } imported && ImportIndex.TryRootOf(canonical, out string root) ? [.. imported.Entry.Dependencies.Select(d => root + d)] : [];
    }

    private (ImportEntry Entry, string Path)? ImportedFor(string path)
    {
        if (!ImportIndex.TryRootOf(path, out string root) || !path.StartsWith(root, StringComparison.Ordinal))
        {
            return null;
        }

        if (!_indexes.TryGetValue(root, out ImportIndex? index))
        {
            _indexes[root] = index = ImportIndex.Load(files, root);
        }

        ImportEntry? entry = index.Find(path[root.Length..]);
        return entry is not null && files.FileExists(root + entry.Artifact) ? (entry, root + entry.Artifact) : null;
    }

    /// <summary>The model at <paramref name="path"/>. The textures of an imported one are transcoded for the GPU that <paramref name="options"/> describes (BC7, ASTC, or RGBA8).</summary>
    public ModelData LoadModel(string path, ContentOptions? options = null)
    {
        string canonical = VfsPath.Normalize(path);
        if (ImportedFor(canonical) is { } imported)
        {
            return ModelFile.Read(files.ReadAllBytes(imported.Path), canonical, options?.TextureCaps ?? TextureCaps.None);
        }

        options ??= new ContentOptions();
        return GltfLoader.Load(canonical, new ContentOptions
        {
            TextureCaps = options.TextureCaps,
            GenerateMips = options.GenerateMips,
            OptimizeMeshes = options.OptimizeMeshes,
            ReadFile = options.ReadFile ?? files.ReadAllBytes,
        });
    }

    /// <summary>The image at <paramref name="path"/> as a texture. An imported one has the color space and mips it was imported with (<paramref name="srgb"/> and <paramref name="mipmaps"/> are for the source), transcoded for <paramref name="caps"/>.</summary>
    public TextureData LoadTexture(string path, bool srgb, TextureCaps caps, SamplerData? sampler = null, bool mipmaps = true)
    {
        string canonical = VfsPath.Normalize(path);
        SamplerData use = sampler ?? SamplerData.Default;
        if (ImportedFor(canonical) is { } imported)
        {
            byte[] file = files.ReadAllBytes(imported.Path);
            return Ktx2Loader.Load(canonical, file, Ktx2Loader.IsSrgb(canonical, file), caps, use); // the artifact knows which it is
        }

        byte[] bytes = files.ReadAllBytes(canonical);
        return Ktx2Loader.IsKtx2(bytes) ? Ktx2Loader.Load(canonical, bytes, srgb, caps, use) : ImageDecoder.Decode(canonical, bytes, srgb, use, mipmaps);
    }
}
