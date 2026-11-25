using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Weft.Content.Assets;
using Weft.Content.Xml;
using Weft.Core.Assets;

namespace Weft.Content.Import;

/// <summary>A shader compiled with one set of preprocessor defines, listed in a shader's <c>.wimp</c> as <c>&lt;Variant&gt;&lt;Define name="" value=""/&gt;&lt;/Variant&gt;</c>.</summary>
public sealed record ShaderVariant(IReadOnlyDictionary<string, string> Defines);

/// <summary>A file the import makes from an asset (a model's material or texture), listed in its <c>.wimp</c> as <c>&lt;SubAsset name="material/paint" uid="uid://…"/&gt;</c>: the name says which it is, the UID is minted the first time and stays with the name.</summary>
/// <param name="Name">What it is, unique in the sidecar.</param>
/// <param name="Uid">Its identity.</param>
public sealed record ImportSubAsset(string Name, AssetUid Uid);

/// <summary>A model's material slot replaced by a material asset, <c>&lt;MaterialRemap slot="paint" ref="materials/paint.wmat"/&gt;</c>.</summary>
/// <param name="Slot">The material slot of the model (the name of its glTF material).</param>
/// <param name="Ref">The material asset, as a path or a <c>uid://</c>.</param>
public sealed record MaterialRemap(string Slot, string Ref);

/// <summary>A named attachment point of a model, <c>&lt;Socket name="hand" node="arm.R" position="0 0.1 0" rotation="0 0 0 1"/&gt;</c>.</summary>
/// <param name="Name">The socket's name, unique in the sidecar.</param>
/// <param name="Node">The node of the model it is attached to; empty is the model's root.</param>
/// <param name="Position">Where it is, relative to the node.</param>
/// <param name="Rotation">How it is turned, relative to the node (x y z w in the file).</param>
public sealed record ImportSocket(string Name, string Node, Vector3 Position, Quaternion Rotation);

/// <summary>A part of a source's animation, <c>&lt;Clip name="walk" loop="true" start="0" end="1.2"/&gt;</c>. Read and kept; nothing uses it yet.</summary>
/// <param name="Name">The clip's name, unique in the sidecar.</param>
/// <param name="Loop">Whether it repeats.</param>
/// <param name="Start">Where it starts in the source's animation, in seconds, or null for its start.</param>
/// <param name="End">Where it ends, in seconds, or null for its end.</param>
public sealed record ImportClip(string Name, bool Loop, float? Start, float? End);

/// <summary>A level of detail of a model, <c>&lt;Lod screenSize="0.25" reduction="0.5"/&gt;</c>. Read and kept; nothing uses it yet.</summary>
/// <param name="ScreenSize">The share of the screen's height (above 0, up to 1) the model has to fill to use this level.</param>
/// <param name="Reduction">The share of the triangles this level takes away (0 to 1).</param>
public sealed record ImportLod(float ScreenSize, float Reduction);

/// <summary>The collision shape a model asks for, <c>&lt;Collision shape="convex"/&gt;</c>. Read and kept; nothing uses it yet.</summary>
public enum CollisionShape
{
    /// <summary>No collision.</summary>
    None,

    /// <summary>The bounding box.</summary>
    Box,

    /// <summary>The convex hull.</summary>
    Convex,

    /// <summary>The triangles themselves.</summary>
    Mesh,
}

/// <summary>What an <c>&lt;asset&gt;.wimp</c> file next to an asset says about importing it, and what the importer wrote there (its UID, the hash of the source, its sub-assets).</summary>
public sealed record ImportSettings
{
    /// <summary>The largest sidecar read, in characters. A real one is a few hundred; a bigger one is refused rather than parsed.</summary>
    public const int MaxLength = 1 << 20;

    /// <summary>The attributes of the root that the importer writes: they are not settings, so they are not part of the import key.</summary>
    private static readonly string[] s_writtenAttributes = ["uid", "version", "sourceHash"];

    private static readonly Lazy<SchemaSet> s_schema = new(() =>
    {
        using Stream stream = typeof(ImportSettings).Assembly.GetManifestResourceStream("Import/import.xsd") ?? throw new InvalidOperationException("The embedded schema of import settings is missing.");
        using var reader = new StreamReader(stream);
        var set = new SchemaSet();
        set.LoadText(reader.ReadToEnd(), "import.xsd");
        return set;
    });

    /// <summary>The UID of the asset (the <c>uid</c> attribute), or <see cref="AssetUid.None"/> before one was minted. It says who the asset is, not how to import it, so it is not part of the import key (<see cref="KeyText"/>).</summary>
    public AssetUid Uid { get; init; }

    /// <summary>The version of the sidecar (the <c>version</c> attribute), 1 when it is missing. The engine reads 1 and 2 (<see cref="ImportSidecar.CurrentVersion"/>) and writes a 1 as a 2 the next time it imports the asset.</summary>
    public int Version { get; init; } = 1;

    /// <summary>The SHA-256 of the source as the last import read it, in lower-case hexadecimal (the <c>sourceHash</c> attribute), or empty before an import wrote it. Like <see cref="Uid"/> it is not part of the import key.</summary>
    public string SourceHash { get; init; } = "";

    /// <summary>The files the import made from the asset and the UIDs they were given (a model's textures and materials).</summary>
    public IReadOnlyList<ImportSubAsset> SubAssets { get; init; } = [];

    /// <summary>Models: material slots replaced by material assets.</summary>
    public IReadOnlyList<MaterialRemap> MaterialRemaps { get; init; } = [];

    /// <summary>Models: attachment points.</summary>
    public IReadOnlyList<ImportSocket> Sockets { get; init; } = [];

    /// <summary>Animation clips, read and kept; nothing uses them yet.</summary>
    public IReadOnlyList<ImportClip> Clips { get; init; } = [];

    /// <summary>Levels of detail, read and kept; nothing uses them yet.</summary>
    public IReadOnlyList<ImportLod> Lods { get; init; } = [];

    /// <summary>The collision shape, read and kept; nothing uses it yet. Null when the sidecar does not say.</summary>
    public CollisionShape? Collision { get; init; }

    /// <summary>Textures: the colors are sRGB.</summary>
    public bool Srgb { get; init; } = true;

    /// <summary>Textures: how they are stored, GPU-compressed (the default) or as RGBA8.</summary>
    public TextureCompression Compression { get; init; } = TextureCompression.Uastc;

    /// <summary>Textures: the image is a normal map. It is linear whatever <see cref="Srgb"/> says, and is encoded with the settings that keep normals accurate.</summary>
    public bool NormalMap { get; init; }

    /// <summary>Textures: build the mip chain.</summary>
    public bool Mips { get; init; } = true;

    /// <summary>Models: optimize the meshes.</summary>
    public bool Optimize { get; init; } = true;

    /// <summary>Shaders: the variants to compile besides the default one, which has no defines.</summary>
    public IReadOnlyList<ShaderVariant> Variants { get; init; } = [];

    /// <summary>
    /// The text of a sidecar as the import key sees it: the same text without what the importer itself writes, so that writing it (a UID, the version, the hash of the source, the sub-assets) does not
    /// change the key and make the asset import again, and every key made before those existed is still the one a sidecar without them makes.
    /// <list type="number">
    /// <item>The empty text stays empty.</item>
    /// <item>A sidecar that says nothing but what the importer writes (<c>&lt;Import uid="…" version="2" sourceHash="…"&gt;</c> with <c>&lt;SubAsset/&gt;</c> children, which is what the first import makes when there was no sidecar)
    /// is the same as no sidecar: empty. (A sidecar that already existed with no settings at all, <c>&lt;Import/&gt;</c>, keeps its own key, so writing into it imports the asset once more: it says nothing, and nobody writes it.)</item>
    /// <item>Otherwise the <c>uid</c>, <c>version</c> and <c>sourceHash</c> attributes and the <c>&lt;SubAsset&gt;</c> elements, each with the white space before it, are taken out <em>textually</em> (only they change), and
    /// everything else is returned byte for byte: it is the text people wrote, and it was part of the key before any of this existed.</item>
    /// </list>
    /// Text that is not valid XML, or is longer than <see cref="MaxLength"/>, is returned as it is (reading it as settings fails on its own).
    /// </summary>
    public static string KeyText(string sidecarText)
    {
        if (sidecarText.Length == 0 || sidecarText.Length > MaxLength)
        {
            return sidecarText;
        }

        XmlNode root;
        try
        {
            root = XmlNode.Parse(sidecarText.TrimStart('\uFEFF'), "");
        }
        catch (XmlParseException)
        {
            return sidecarText;
        }

        if (root.Name == ImportSidecar.RootName && root.Text.Length == 0 && root.Attributes.TrueForAll(a => Array.IndexOf(s_writtenAttributes, a.Name) >= 0)
            && root.Children.TrueForAll(c => c.Name == ImportSidecar.SubAssetName) && root.Attributes.Count + root.Children.Count > 0)
        {
            return "";
        }

        string text = sidecarText;
        foreach (string name in s_writtenAttributes)
        {
            if (RootTag.TryFind(text, out RootTag.Tag? tag))
            {
                text = RootTag.Remove(text, tag, name);
            }
        }

        return ImportSidecar.RemoveSubAssets(text);
    }

    /// <summary>Reads settings from the text of a sidecar file; <paramref name="origin"/> names it in errors.</summary>
    /// <exception cref="ContentException">The file is not valid: too long, not what the schema says, written by a newer engine, or it names one thing twice (two sub-assets with the same name or UID, two sockets with the same name…).</exception>
    public static ImportSettings Parse(string text, string origin)
    {
        if (text.Length > MaxLength)
        {
            throw new ContentException($"{origin}: the import settings are {text.Length} characters long, more than the {MaxLength} allowed.");
        }

        XmlNode node = XmlNode.Parse(text, origin);
        DecodeResult result = s_schema.Value.Decode(node, origin);
        if (result.Problems.Count > 0)
        {
            throw new ContentException(string.Join("\n", result.Problems.Select(p => p.ToString())));
        }

        var record = (DefRecord)result.Value;
        var variants = new List<ShaderVariant>();
        foreach (DefValue entry in record["Variant"] is DefList list ? list.Items : [])
        {
            var defines = new Dictionary<string, string>(StringComparer.Ordinal);
            // A <Variant> has only <Define> children, so the schema decodes it as the list of them.
            foreach (DefValue define in entry is DefList defineList ? defineList.Items : [])
            {
                var item = (DefRecord)define;
                defines[(item["name"] as DefText)?.Value ?? ""] = (item["value"] as DefText)?.Value ?? "";
            }

            variants.Add(new ShaderVariant(defines));
        }

        int version = (int)Math.Min((record["version"] as DefNumber)?.Value ?? 1, int.MaxValue);
        if (version > ImportSidecar.CurrentVersion)
        {
            throw new ContentException($"{origin}: the import settings are version {version}, and this engine reads up to version {ImportSidecar.CurrentVersion}: they were written by a newer engine.");
        }

        AssetUid uid = AssetUid.TryParse(Text(record, "uid"), out AssetUid own) ? own : AssetUid.None;
        List<ImportSubAsset> subAssets = [.. Items(record, "SubAsset").Select(i => new ImportSubAsset(Text(i, "name"), AssetUid.TryParse(Text(i, "uid"), out AssetUid sub) ? sub : AssetUid.None))];
        List<MaterialRemap> remaps = [.. Items(record, "MaterialRemap").Select(i => new MaterialRemap(Text(i, "slot"), Text(i, "ref")))];
        List<ImportSocket> sockets = [.. Items(record, "Socket").Select(i => new ImportSocket(Text(i, "name"), Text(i, "node"), Position(i["position"]), Rotation(i["rotation"])))];
        List<ImportClip> clips = [.. Items(record, "Clip").Select(i => new ImportClip(Text(i, "name"), (i["loop"] as DefBool)?.Value ?? false, Number(i["start"]), Number(i["end"])))];
        List<ImportLod> lods = [.. Items(record, "Lod").Select(i => new ImportLod(Number(i["screenSize"]) ?? 1, Number(i["reduction"]) ?? 0))];

        RejectRepeats(origin, "sub-asset name", subAssets.Select(s => s.Name));
        RejectRepeats(origin, "UID", subAssets.Select(s => s.Uid.ToString()).Concat(uid.IsNone ? [] : [uid.ToString()]));
        RejectRepeats(origin, "material slot", remaps.Select(r => r.Slot));
        RejectRepeats(origin, "socket name", sockets.Select(s => s.Name));
        RejectRepeats(origin, "clip name", clips.Select(c => c.Name));
        foreach (string name in subAssets.Select(s => s.Name).Concat(remaps.Select(r => r.Slot)).Concat(sockets.Select(s => s.Name)).Concat(clips.Select(c => c.Name)))
        {
            if (name.Any(char.IsControl) || name != name.Trim())
            {
                throw new ContentException($"{origin}: the name '{name}' has a control character, or white space at one end.");
            }
        }

        foreach (ImportSocket socket in sockets)
        {
            if (!float.IsFinite(socket.Position.X + socket.Position.Y + socket.Position.Z) || socket.Rotation.LengthSquared() < 1e-12f || !float.IsFinite(socket.Rotation.LengthSquared()))
            {
                throw new ContentException($"{origin}: the socket '{socket.Name}' has a position that is too large or a rotation that is not one (four numbers, not all zero).");
            }
        }

        return new ImportSettings
        {
            Uid = uid,
            Version = version,
            SourceHash = Text(record, "sourceHash"),
            SubAssets = Keep(subAssets),
            MaterialRemaps = Keep(remaps),
            Sockets = Keep(sockets),
            Clips = Keep(clips),
            Lods = Keep(lods),
            Collision = record["Collision"] is DefRecord collision && Enum.TryParse(Text(collision, "shape"), ignoreCase: true, out CollisionShape shape) ? shape : null,
            Variants = variants.Count > 0 ? (IReadOnlyList<ShaderVariant>)variants : [],
            Srgb = (record["srgb"] as DefBool)?.Value ?? true,
            Mips = (record["mips"] as DefBool)?.Value ?? true,
            Optimize = (record["optimize"] as DefBool)?.Value ?? true,
            Compression = (record["compression"] as DefText)?.Value == "none" ? TextureCompression.None : TextureCompression.Uastc,
            NormalMap = (record["normalMap"] as DefBool)?.Value ?? false,
        };
    }

    private static IEnumerable<DefRecord> Items(DefRecord record, string name) => record[name] switch
    {
        DefList list => list.Items.OfType<DefRecord>(),
        DefRecord single => [single],
        _ => [],
    };

    /// <summary>The list, or the shared empty one, so that settings that say nothing are equal to <c>new ImportSettings()</c>.</summary>
    private static T[] Keep<T>(List<T> list) => list.Count == 0 ? [] : list.ToArray();

    private static string Text(DefRecord record, string name) => (record[name] as DefText)?.Value ?? "";

    private static float? Number(DefValue? value) => value is DefNumber number ? (float)number.Value : null;

    private static Vector3 Position(DefValue? value) => value is DefVector v ? new Vector3((float)v.X, (float)v.Y, (float)v.Z) : Vector3.Zero;

    private static Quaternion Rotation(DefValue? value) =>
        value is DefList { Items.Count: 4 } list && list.Items.All(i => i is DefNumber)
            ? new Quaternion(Component(list, 0), Component(list, 1), Component(list, 2), Component(list, 3))
            : Quaternion.Identity;

    private static float Component(DefList list, int index) => (float)((DefNumber)list.Items[index]).Value;

    private static void RejectRepeats(string origin, string what, IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (!seen.Add(value))
            {
                throw new ContentException($"{origin}: the {what} '{value}' is used twice.");
            }
        }
    }
}
