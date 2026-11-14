using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content.Assets;
using Apogee.Content.Xml;
using Apogee.Core.Assets;

namespace Apogee.Content.Import;

/// <summary>A shader compiled with one set of preprocessor defines, listed in a shader's <c>.import.xml</c> as <c>&lt;Variant&gt;&lt;Define name="" value=""/&gt;&lt;/Variant&gt;</c>.</summary>
public sealed record ShaderVariant(IReadOnlyDictionary<string, string> Defines);

/// <summary>What an <c>&lt;asset&gt;.import.xml</c> file next to an asset says about importing it.</summary>
public sealed record ImportSettings
{
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
    /// The text of a sidecar as the import key sees it: the same text without the root's <c>uid</c> attribute, so that minting a UID into a sidecar (or into a new one) does not change the key and
    /// make the asset import again, and every key made before UIDs existed is still the one a sidecar without a UID makes.
    /// <list type="number">
    /// <item>The empty text stays empty.</item>
    /// <item>A sidecar that says nothing but a UID (<c>&lt;Import uid="…"/&gt;</c>, which is what minting makes when there was no sidecar) is the same as no sidecar: empty. (A sidecar that already existed with no settings at all,
    /// <c>&lt;Import/&gt;</c>, keeps its own key, so adding a UID to it imports the asset once more: it says nothing, and nobody writes it.)</item>
    /// <item>Otherwise the <c>uid</c> attribute and the white space before it are taken out <em>textually</em> (only that attribute changes), and everything else is returned byte for byte: it is the
    /// text people wrote, and it was part of the key before UIDs existed.</item>
    /// </list>
    /// Text that is not valid XML is returned as it is (reading it as settings fails on its own).
    /// </summary>
    public static string KeyText(string sidecarText)
    {
        if (sidecarText.Length == 0)
        {
            return "";
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

        if (root.Name == ImportSidecar.RootName && root.Children.Count == 0 && root.Text.Length == 0 && root.Attributes.Count > 0 && root.Attributes.TrueForAll(a => a.Name == "uid"))
        {
            return "";
        }

        return RootTag.TryFind(sidecarText, out RootTag.Tag? tag) ? RootTag.Remove(sidecarText, tag, "uid") : sidecarText;
    }

    /// <summary>Reads settings from the text of a sidecar file; <paramref name="origin"/> names it in errors.</summary>
    /// <exception cref="ContentException">The file is not valid.</exception>
    public static ImportSettings Parse(string text, string origin)
    {
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

        return new ImportSettings
        {
            Uid = AssetUid.TryParse((record["uid"] as DefText)?.Value ?? "", out AssetUid uid) ? uid : AssetUid.None,
            Variants = variants.Count > 0 ? (IReadOnlyList<ShaderVariant>)variants : [],
            Srgb = (record["srgb"] as DefBool)?.Value ?? true,
            Mips = (record["mips"] as DefBool)?.Value ?? true,
            Optimize = (record["optimize"] as DefBool)?.Value ?? true,
            Compression = (record["compression"] as DefText)?.Value == "none" ? TextureCompression.None : TextureCompression.Uastc,
            NormalMap = (record["normalMap"] as DefBool)?.Value ?? false,
        };
    }
}
