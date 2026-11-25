using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Core.Assets;

namespace Weft.Content.Import;

/// <summary>What an importer made: the artifact's bytes and the extension that is added to the source's name (<c>.wmdl</c>).</summary>
public sealed record ImportResult(byte[] Bytes, string ArtifactExtension);

/// <summary>
/// One kind of asset <see cref="AssetImporter"/> can turn into an artifact: a name, the extensions it takes, a fingerprint (part of the key, so changing what it writes imports again) and a function
/// from source to artifact. Importers do not touch the index, the keys or the files: they read through <see cref="ImportContext"/>, which records what they read.
/// </summary>
public interface IAssetImporter
{
    /// <summary>Names the importer in the key.</summary>
    string Name { get; }

    /// <summary>The extensions it takes, with the dot, in lower case.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Everything that changes what it writes and is not in the asset or its <c>.wimp</c>: its own version, the version and the options of the tools it calls.</summary>
    string Fingerprint { get; }

    /// <summary>The artifact of the asset, or null when the asset is not one (a shader module that only other shaders import): it then has no entry in the index.</summary>
    /// <exception cref="ContentException">The asset cannot be imported.</exception>
    ImportResult? Import(ImportContext context);
}

/// <summary>An asset being imported: its bytes, its settings, and the files it reads, which become the artifact's dependencies.</summary>
public sealed class ImportContext
{
    private readonly VirtualFileSystem _files;

    internal ImportContext(VirtualFileSystem files, string source, byte[] bytes, ImportSettings settings)
    {
        _files = files;
        Source = source;
        Bytes = bytes;
        Settings = settings;
        foreach (ImportSubAsset sub in settings.SubAssets)
        {
            _subAssets[sub.Name] = sub.Uid;
        }
    }

    private readonly Dictionary<string, AssetUid> _subAssets = new(StringComparer.Ordinal);

    /// <summary>The path of the asset relative to <c>res://</c>.</summary>
    public string Source { get; }

    /// <summary>The path of the asset: <c>res://</c> and <see cref="Source"/>.</summary>
    public string SourcePath => "res://" + Source;

    public byte[] Bytes { get; }

    public ImportSettings Settings { get; }

    internal List<string> Read { get; } = [];

    /// <summary>The sub-assets this import asked for that its <c>.wimp</c> did not list yet: the importer writes them there after the import.</summary>
    internal List<ImportSubAsset> NewSubAssets { get; } = [];

    /// <summary>
    /// The UID of a file this import makes from the asset (<c>material/paint</c>, <c>texture/albedo</c>): the one the <c>.wimp</c> lists for the name, or a new one that is written to it after the import. The same
    /// name gets the same UID in every later import, so a reference to the file survives re-importing the asset.
    /// </summary>
    /// <exception cref="ContentException">The name is empty, longer than 256 characters, or has a control character or white space at its ends.</exception>
    public AssetUid SubAsset(string name)
    {
        if (name.Length is 0 or > 256 || name != name.Trim() || name.Any(char.IsControl))
        {
            throw new ContentException($"'{Source}': '{name}' is not a name a sub-asset can have (1 to 256 characters, no control characters, no white space at the ends).");
        }

        if (!_subAssets.TryGetValue(name, out AssetUid uid))
        {
            uid = AssetUid.New();
            _subAssets[name] = uid;
            NewSubAssets.Add(new ImportSubAsset(name, uid));
        }

        return uid;
    }

    /// <summary>Reads a file of the project (or of a mounted package); one under <c>res://</c> other than the asset becomes a dependency.</summary>
    public byte[] ReadFile(string path)
    {
        string normalized = VfsPath.Normalize(path);
        byte[] bytes = _files.ReadAllBytes(normalized);
        Depend(normalized);
        return bytes;
    }

    /// <summary>Says that the import depends on a file without reading it through <see cref="ReadFile"/> (a tool read it).</summary>
    public void Depend(string path)
    {
        string normalized = VfsPath.Normalize(path);
        if (normalized != SourcePath && normalized.StartsWith("res://", StringComparison.Ordinal))
        {
            Read.Add(normalized["res://".Length..]);
        }
    }

    /// <summary>The file on disk behind a virtual path, for tools that read files themselves; null when it is not a plain file (a zip, a pack).</summary>
    public string? PhysicalPath(string path) => _files.TryGetPhysicalPath(path);

    /// <summary>The folder on disk that <c>res://</c> is; null when the project is not a plain folder.</summary>
    public string? PhysicalRoot
    {
        get
        {
            if (PhysicalPath(SourcePath) is not { } physical)
            {
                return null;
            }

            string full = Path.GetFullPath(physical);
            string tail = Path.DirectorySeparatorChar + Source.Replace('/', Path.DirectorySeparatorChar);
            return full.EndsWith(tail, StringComparison.Ordinal) ? full[..^tail.Length] : null;
        }
    }
}

/// <summary>glTF models into <see cref="ModelFile"/>s, their textures GPU-compressed unless the settings say otherwise.</summary>
public sealed class ModelImporter : IAssetImporter
{
    public string Name => "model";

    public IReadOnlyList<string> Extensions { get; } = AssetFormats.ExtensionsOfType("model");

    public string Fingerprint => "2; " + BasisEncoder.Settings;

    /// <summary>
    /// The names a model's materials and textures have as sub-assets: <c>material/&lt;name&gt;</c> (<c>material/&lt;index&gt;</c> for one with no name) and <c>texture/&lt;name&gt;</c>. A name that an earlier one
    /// already took gets <c>#&lt;index&gt;</c> after it, so every material and texture has a name of its own, and the same one every time the model is imported.
    /// </summary>
    internal static List<string> SubAssetNames(ModelData model)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        for (int i = 0; i < model.Materials.Count; i++)
        {
            names.Add(Unique(taken, "material/", model.Materials[i].Name, i));
        }

        for (int i = 0; i < model.Textures.Count; i++)
        {
            names.Add(Unique(taken, "texture/", model.Textures[i].Name, i));
        }

        return names;
    }

    private static string Unique(HashSet<string> taken, string prefix, string name, int index)
    {
        string label = name.Trim();
        string candidate = prefix + (label.Length == 0 || label.Any(char.IsControl) ? index.ToString(CultureInfo.InvariantCulture) : label);
        if (candidate.Length > 240)
        {
            candidate = candidate[..240].TrimEnd();
        }

        string result = candidate;
        for (int attempt = 0; !taken.Add(result); attempt++)
        {
            result = candidate + "#" + index.ToString(CultureInfo.InvariantCulture) + (attempt == 0 ? "" : "." + attempt.ToString(CultureInfo.InvariantCulture));
        }

        return result;
    }

    public ImportResult? Import(ImportContext context)
    {
        ModelData model = GltfLoader.Load(context.SourcePath, new ContentOptions
        {
            TextureCaps = TextureCaps.None,
            OptimizeMeshes = context.Settings.Optimize,
            ReadFile = context.ReadFile,
        });
        foreach (string name in SubAssetNames(model))
        {
            context.SubAsset(name); // the UIDs of the model's materials and textures are minted now and kept in its .wimp, so that they are the same in every import
        }

        return new ImportResult(ModelFile.Write(model, context.Settings.Compression), AssetFormats.Model.Extension);
    }
}

/// <summary>PNG and JPEG images into KTX 2 files with their mip chains, GPU-compressed (<see cref="BasisEncoder"/>) unless the settings say otherwise.</summary>
public sealed class ImageImporter : IAssetImporter
{
    public string Name => "image";

    public IReadOnlyList<string> Extensions { get; } = AssetFormats.ExtensionsOfType("texture");

    public string Fingerprint => "2; " + BasisEncoder.Settings;

    public ImportResult? Import(ImportContext context)
    {
        ImportSettings settings = context.Settings;
        TextureData texture = ImageDecoder.Decode(context.Source, context.Bytes, settings.Srgb && !settings.NormalMap, SamplerData.Default, settings.Mips);
        bool compress = settings.Compression == TextureCompression.Uastc && Math.Min(texture.Width, texture.Height) >= BasisEncoder.MinimumSize;
        return new ImportResult(compress ? BasisEncoder.Encode(texture, settings.NormalMap) : Ktx2Writer.Write(texture, Ktx2Writer.ImportLevel), AssetFormats.Ktx2.Extension);
    }
}
