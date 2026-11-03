using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content.Xml;

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
            Variants = variants.Count > 0 ? (IReadOnlyList<ShaderVariant>)variants : [],
            Srgb = (record["srgb"] as DefBool)?.Value ?? true,
            Mips = (record["mips"] as DefBool)?.Value ?? true,
            Optimize = (record["optimize"] as DefBool)?.Value ?? true,
            Compression = (record["compression"] as DefText)?.Value == "none" ? TextureCompression.None : TextureCompression.Uastc,
            NormalMap = (record["normalMap"] as DefBool)?.Value ?? false,
        };
    }
}
