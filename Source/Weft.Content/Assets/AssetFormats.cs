using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Weft.Content.Assets;

/// <summary>What a file of a format is to the engine: who writes it and what happens to it.</summary>
public enum AssetFormKind
{
    /// <summary>An XML document that people and tools write and that is its own source: a scene, a prefab. It carries its UID in the root element and has no sidecar.</summary>
    Authored,

    /// <summary>A manifest that is read from where it is, by name: <c>game.wgam</c>, <c>mod.wmod</c>. Not an asset.</summary>
    Manifest,

    /// <summary>The import settings (and the UID) of the source file it sits beside.</summary>
    Sidecar,

    /// <summary>A file made by other tools that an importer turns into an artifact (a glTF model, a PNG, a Slang module), or a file such a source needs (a glTF buffer).</summary>
    Source,

    /// <summary>What an importer makes: a binary file in the <c>imported/</c> folder at the path of its source.</summary>
    Imported,

    /// <summary>A pack of a game's files.</summary>
    Pack,

    /// <summary>XML cooked into a binary tree: it keeps the path (and so the extension) of its source, so it has no extension of its own.</summary>
    Cooked,

    /// <summary>A file of a format everyone knows that the engine reads as it is (a script, a sound, a font, a definition).</summary>
    Plain,
}

/// <summary>What <c>--pack --cooked</c> does with the files of a format.</summary>
public enum CookRule
{
    /// <summary>The file is packed as it is.</summary>
    None,

    /// <summary>The file is XML and is packed as cooked XML (<see cref="Weft.Content.Xml.XmlBinary"/>) at the same path.</summary>
    Xml,
}

/// <summary>
/// One row of the table of file formats (<see cref="AssetFormats"/>): what the extension is, what the file holds, how it is checked and what the engine does with it.
/// </summary>
/// <param name="Extension">With the dot, in lower case (<c>.wscn</c>). Empty for cooked XML, which has none. One extension is one format.</param>
/// <param name="Kind">What the file is to the engine.</param>
/// <param name="Label">What a person reads (<c>Scene</c>).</param>
/// <param name="RootElement">For an XML format, the name of the root element (<c>Scene</c>); null for the rest.</param>
/// <param name="Version">The newest version of the format this engine reads (the <c>version</c> attribute of an XML document, the version field of a binary one); 0 for a format that has none.</param>
/// <param name="SchemaResource">The logical name of the embedded <c>.xsd</c> that describes an XML format (<c>Scene/scene.xsd</c>), or null.</param>
/// <param name="AssetType">The type name that asset fields use for it (<c>[Property(Asset = "…")]</c>: <c>model</c>, <c>texture</c>, <c>scene</c>), or null when none does.</param>
/// <param name="Magic">For a binary format, its first four bytes as a little-endian integer (<c>WPAK</c> is <c>0x4B415057</c>); 0 when it has none.</param>
/// <param name="Cook">What a cooked pack does with it.</param>
public sealed record AssetFormat(
    string Extension,
    AssetFormKind Kind,
    string Label,
    string? RootElement = null,
    int Version = 0,
    string? SchemaResource = null,
    string? AssetType = null,
    uint Magic = 0,
    CookRule Cook = CookRule.None)
{
    /// <summary>Whether <paramref name="path"/> ends with this format's extension (not case-sensitive). Never true for a format without one.</summary>
    public bool Matches(string path) => Extension.Length > 0 && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The one table of the engine's file formats, read by everything that needs to know an extension or a magic: which files are authored assets, which are imported from what, which the editor lists
/// by kind, which a hot reload watches, what a pack cooks. <c>Docs/manual/content/formats.md</c> describes each format; this table is what the code goes by. The built-in rows are registered when
/// the table is first used; a library with a format of its own adds a row with <see cref="Register"/>. Safe to use from several threads.
/// </summary>
public static class AssetFormats
{
    private static readonly Lock s_lock = new();
    private static readonly List<AssetFormat> s_all = [];
    private static readonly Dictionary<string, AssetFormat> s_byExtension = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<uint, AssetFormat> s_byMagic = [];

    /// <summary>A scene (<c>.wscn</c>): version 2 is the newest, the one that holds a prefab instance.</summary>
    public static readonly AssetFormat Scene = Register(new(".wscn", AssetFormKind.Authored, "Scene", "Scene", 2, "Scene/scene.xsd", AssetType: "scene", Cook: CookRule.Xml));

    /// <summary>A prefab (<c>.wprf</c>).</summary>
    public static readonly AssetFormat Prefab = Register(new(".wprf", AssetFormKind.Authored, "Prefab", "Prefab", 1, "Scene/prefab.xsd", Cook: CookRule.Xml));

    /// <summary>The import settings of a source file (<c>x.glb.wimp</c>).</summary>
    public static readonly AssetFormat Sidecar = Register(new(".wimp", AssetFormKind.Sidecar, "Import settings", "Import", 0, "Import/import.xsd"));

    /// <summary>A game's manifest (<c>game.wgam</c>).</summary>
    public static readonly AssetFormat Game = Register(new(".wgam", AssetFormKind.Manifest, "Game manifest", "Game", 0, "Packages/game.xsd"));

    /// <summary>A package's or mod's manifest (<c>mod.wmod</c>).</summary>
    public static readonly AssetFormat Mod = Register(new(".wmod", AssetFormKind.Manifest, "Package manifest", "Mod", 0, "Packages/mod.xsd"));

    /// <summary>A pack (<c>.wpak</c>). Its magic is this one without a key; an encrypted pack has four bytes of a hash of the key instead.</summary>
    public static readonly AssetFormat Pack = Register(new(".wpak", AssetFormKind.Pack, "Pack", Version: 1, Magic: Fourcc('W', 'P', 'A', 'K')));

    /// <summary>An imported model (<c>x.glb.wmdl</c>).</summary>
    public static readonly AssetFormat Model = Register(new(".wmdl", AssetFormKind.Imported, "Imported model", Version: 1, Magic: Fourcc('W', 'M', 'D', 'L')));

    /// <summary>A compiled shader bundle (<c>x.slang.wshd</c>).</summary>
    public static readonly AssetFormat Shader = Register(new(".wshd", AssetFormKind.Imported, "Compiled shader", Version: 1, Magic: Fourcc('W', 'S', 'H', 'D')));

    /// <summary>
    /// An imported image (<c>x.png.ktx2</c>), a standard KTX 2 file. Its magic here is the first four bytes of the 12-byte identifier (<c>0xAB 'K' 'T' 'X'</c>); <see cref="Ktx2Loader.IsKtx2"/> checks all twelve.
    /// </summary>
    public static readonly AssetFormat Ktx2 = Register(new(".ktx2", AssetFormKind.Imported, "Imported image", Magic: 0x58544BAB));

    /// <summary>XML cooked into a tree (<see cref="Weft.Content.Xml.XmlBinary"/>): it has the extension of its source.</summary>
    public static readonly AssetFormat CookedXml = Register(new("", AssetFormKind.Cooked, "Cooked XML", Version: 1, Magic: Fourcc('W', 'X', 'M', 'B')));

    /// <summary>A binary glTF model, a source.</summary>
    public static readonly AssetFormat Glb = Register(new(".glb", AssetFormKind.Source, "glTF binary model", AssetType: "model"));

    /// <summary>A glTF model with its buffers and images beside it, a source.</summary>
    public static readonly AssetFormat Gltf = Register(new(".gltf", AssetFormKind.Source, "glTF model", AssetType: "model"));

    /// <summary>A buffer a <c>.gltf</c> model refers to: not an asset of its own, but a change to it imports the model again.</summary>
    public static readonly AssetFormat GltfBuffer = Register(new(".bin", AssetFormKind.Source, "glTF buffer"));

    /// <summary>A PNG image, a source.</summary>
    public static readonly AssetFormat Png = Register(new(".png", AssetFormKind.Source, "PNG image", AssetType: "texture"));

    /// <summary>A JPEG image, a source.</summary>
    public static readonly AssetFormat Jpg = Register(new(".jpg", AssetFormKind.Source, "JPEG image", AssetType: "texture"));

    /// <summary>A JPEG image, a source (the long extension).</summary>
    public static readonly AssetFormat Jpeg = Register(new(".jpeg", AssetFormKind.Source, "JPEG image", AssetType: "texture"));

    /// <summary>A Slang shader module, a source.</summary>
    public static readonly AssetFormat Slang = Register(new(".slang", AssetFormKind.Source, "Slang shader", AssetType: "shader"));

    /// <summary>A Luau script.</summary>
    public static readonly AssetFormat Luau = Register(new(".luau", AssetFormKind.Plain, "Luau script"));

    /// <summary>An XML file that is none of the formats above: the definitions of a package, a settings file.</summary>
    public static readonly AssetFormat XmlData = Register(new(".xml", AssetFormKind.Plain, "XML"));

    /// <summary>A WAV sound.</summary>
    public static readonly AssetFormat Wav = Register(new(".wav", AssetFormKind.Plain, "WAV sound"));

    /// <summary>An Ogg Vorbis sound.</summary>
    public static readonly AssetFormat Ogg = Register(new(".ogg", AssetFormKind.Plain, "Ogg sound"));

    /// <summary>A FLAC sound.</summary>
    public static readonly AssetFormat Flac = Register(new(".flac", AssetFormKind.Plain, "FLAC sound"));

    /// <summary>An MP3 sound.</summary>
    public static readonly AssetFormat Mp3 = Register(new(".mp3", AssetFormKind.Plain, "MP3 sound"));

    /// <summary>A TrueType font.</summary>
    public static readonly AssetFormat Ttf = Register(new(".ttf", AssetFormKind.Plain, "TrueType font"));

    /// <summary>An OpenType font.</summary>
    public static readonly AssetFormat Otf = Register(new(".otf", AssetFormKind.Plain, "OpenType font"));

    /// <summary>A RmlUi document.</summary>
    public static readonly AssetFormat Rml = Register(new(".rml", AssetFormKind.Plain, "UI document"));

    /// <summary>A RmlUi style sheet.</summary>
    public static readonly AssetFormat Rcss = Register(new(".rcss", AssetFormKind.Plain, "UI style sheet"));

    /// <summary>Every format, in the order they were registered.</summary>
    public static IReadOnlyList<AssetFormat> All
    {
        get
        {
            lock (s_lock)
            {
                return [.. s_all];
            }
        }
    }

    /// <summary>
    /// Adds a format. One extension is one format and one magic is one format.
    /// </summary>
    /// <exception cref="ArgumentException">The extension is not lower case with a dot (or empty), or the label is empty.</exception>
    /// <exception cref="InvalidOperationException">The extension or the magic belongs to another format already.</exception>
    public static AssetFormat Register(AssetFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        string extension = format.Extension;
        if (extension.Length > 0 && (extension.Length < 2 || extension[0] != '.' || extension.Any(char.IsUpper) || extension.IndexOfAny(['/', '\\', '.'], 1) >= 0))
        {
            throw new ArgumentException($"'{extension}' is not an extension with its dot, in lower case.", nameof(format));
        }

        ArgumentException.ThrowIfNullOrEmpty(format.Label);
        lock (s_lock)
        {
            if (extension.Length > 0 && s_byExtension.TryGetValue(extension, out AssetFormat? other))
            {
                throw new InvalidOperationException($"{extension} is the extension of '{other.Label}' already: it cannot also be '{format.Label}'.");
            }

            if (format.Magic != 0 && s_byMagic.TryGetValue(format.Magic, out other))
            {
                throw new InvalidOperationException($"The magic of '{format.Label}' is the magic of '{other.Label}'.");
            }

            s_all.Add(format);
            if (extension.Length > 0)
            {
                s_byExtension[extension] = format;
            }

            if (format.Magic != 0)
            {
                s_byMagic[format.Magic] = format;
            }
        }

        return format;
    }

    private static uint Fourcc(char a, char b, char c, char d) => a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    /// <summary>The format of the file at <paramref name="path"/> by its extension (the last one, not case-sensitive; <c>x.glb.wimp</c> is a sidecar), or null. A name that only starts with a dot has no extension.</summary>
    public static AssetFormat? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOfAny(['/', '\\']);
        return dot <= slash + 1 ? null : FindExtension(path[dot..]);
    }

    /// <summary>The format with <paramref name="extension"/> (with the dot, not case-sensitive), or null.</summary>
    public static AssetFormat? FindExtension(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        lock (s_lock)
        {
            return s_byExtension.GetValueOrDefault(extension);
        }
    }

    /// <summary>The binary format whose magic is the first four bytes of <paramref name="bytes"/>, or null. A pack encrypted with a key has no recognizable magic.</summary>
    public static AssetFormat? FindMagic(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return null;
        }

        lock (s_lock)
        {
            return s_byMagic.GetValueOrDefault(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        }
    }

    /// <summary>Whether <paramref name="path"/> names an authored asset (a scene, a prefab) by its extension, not case-sensitive.</summary>
    public static bool IsAuthored(string path) => Find(path) is { Kind: AssetFormKind.Authored };

    /// <summary>The extensions of the formats of <paramref name="kind"/>, in the order they were registered.</summary>
    public static IReadOnlyList<string> ExtensionsOf(AssetFormKind kind)
    {
        lock (s_lock)
        {
            return [.. s_all.Where(f => f.Kind == kind && f.Extension.Length > 0).Select(f => f.Extension)];
        }
    }

    /// <summary>The extensions of the formats whose <see cref="AssetFormat.AssetType"/> is <paramref name="assetType"/>, in the order they were registered.</summary>
    public static IReadOnlyList<string> ExtensionsOfType(string assetType)
    {
        lock (s_lock)
        {
            return [.. s_all.Where(f => f.AssetType == assetType).Select(f => f.Extension)];
        }
    }

    /// <summary>The formats of what importers make (<see cref="AssetFormKind.Imported"/>).</summary>
    public static IReadOnlyList<AssetFormat> ImportedArtifacts
    {
        get
        {
            lock (s_lock)
            {
                return [.. s_all.Where(f => f.Kind == AssetFormKind.Imported)];
            }
        }
    }
}
