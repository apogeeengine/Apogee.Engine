using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;

namespace Apogee.Content.Import;

/// <summary>One artifact of the import: the source it came from, what it was made from, and the file.</summary>
/// <param name="Source">The path of the asset relative to <c>res://</c> (<c>models/car.glb</c>).</param>
/// <param name="Artifact">The path of the result, relative to <c>res://</c> (<c>imported/models/car.glb.amodel</c>).</param>
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

    /// <summary>The file (or what the import read, or its <c>.import.xml</c>) changed since the artifact was made.</summary>
    Stale,
}

/// <summary>What <see cref="AssetImporter.Run"/> did.</summary>
public sealed record ImportReport(IReadOnlyList<string> Imported, IReadOnlyList<string> UpToDate, IReadOnlyList<string> Removed, IReadOnlyList<(string Source, string Message)> Failed)
{
    public bool Success => Failed.Count == 0;
}

/// <summary>
/// Turns the assets of a game into the engine's own formats. It is a small registry: each <see cref="IAssetImporter"/> takes some extensions (models, images, shaders…) and writes an artifact for
/// each asset, in <c>res://imported/</c> beside an <c>index.xml</c> that says which source each came from (<see cref="ContentLoader"/> reads it). Artifacts are kept: an asset is imported
/// again only when its <em>key</em> changes, the SHA-256 of the importer's name and fingerprint, the asset's <c>.import.xml</c> settings, the asset, and every file the import read for it (a model's
/// buffers and textures, the modules a shader imports). Artifacts whose source is gone are deleted. Importers run in the order of the list, and a file that an earlier import read (a model's
/// image) is not imported on its own. <c>res://</c> has to be writable.
/// </summary>
public sealed class AssetImporter(VirtualFileSystem files, IReadOnlyList<IAssetImporter>? importers = null)
{
    /// <summary>Bumped when the key or the index changes, so that old artifacts are made again.</summary>
    public const int ImporterVersion = 3;

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
                Import(importer, source, index, force, imported, upToDate, failed, kept, consumed);
            }
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
        return new ImportReport(imported, upToDate, removed, failed);
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

        string settingsPath = "res://" + source + ".import.xml";
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
        Import(importer, source, index, force: true, imported, upToDate, failed, kept, new HashSet<string>(StringComparer.Ordinal));
        ImportIndex.Save(files, kept.Values.OrderBy(e => e.Source, StringComparer.Ordinal));
        return new ImportReport(imported, upToDate, [], failed);
    }

    private IAssetImporter? ImporterOf(string source) =>
        source.StartsWith(CacheFolder + "/", StringComparison.Ordinal) || source.Split('/').Any(segment => segment.StartsWith('.')) ? null : _importers.FirstOrDefault(i => Has(source, i.Extensions));

    private void Import(IAssetImporter importer, string source, ImportIndex index, bool force, List<string> imported, List<string> upToDate, List<(string, string)> failed, Dictionary<string, ImportEntry> kept, HashSet<string> consumed)
    {
        try
        {
            string settingsPath = "res://" + source + ".import.xml";
            string settingsText = files.FileExists(settingsPath) ? files.ReadAllText(settingsPath) : "";
            ImportSettings settings = settingsText.Length > 0 ? ImportSettings.Parse(settingsText, settingsPath) : new ImportSettings();
            byte[] sourceBytes = files.ReadAllBytes("res://" + source);

            if (!force && index.Find(source) is { } previous && files.FileExists("res://" + previous.Artifact) && KeyOf(importer, settingsText, sourceBytes, previous.Dependencies) == previous.Key)
            {
                upToDate.Add(source);
                kept[source] = previous;
                consumed.UnionWith(previous.Dependencies);
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
            kept[source] = new ImportEntry(source, artifactPath, KeyOf(importer, settingsText, sourceBytes, dependencies), dependencies);
            consumed.UnionWith(dependencies);
            imported.Add(source);
        }
        catch (Exception e) when (e is ContentException or System.IO.IOException or UnauthorizedAccessException)
        {
            failed.Add((source, e.Message));
        }
    }

    /// <summary>The hash of everything the result depends on.</summary>
    private string KeyOf(IAssetImporter importer, string settingsText, byte[] sourceBytes, IReadOnlyList<string> dependencies)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(ReadOnlySpan<byte> bytes) => hash.AppendData(bytes);
        void AddText(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text + "\n"));

        AddText($"apogee-import-{ImporterVersion}");
        AddText(importer.Name);
        AddText(importer.Fingerprint);
        AddText(settingsText);
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

    /// <summary>Reads the indexes again (after an import).</summary>
    public void Refresh() => _indexes.Clear();

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
