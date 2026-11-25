using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Content.Xml;
using Weft.Core.Assets;

namespace Weft.Content.Materials;

/// <summary>
/// A material document (<c>.wmat</c>) as it is written: the shader template, typed parameters, textures and a few switches, and optionally a <see cref="Parent"/> material that this one changes. It says
/// only what the file says, so a child lists only what it overrides; <see cref="Resolve"/> lays the chain of parents under it and gives the whole material. Nothing here draws: a renderer reads
/// <see cref="ResolvedMaterial"/>.
/// <para>
/// Read with <see cref="Parse(ReadOnlySpan{byte}, string)"/> or <see cref="Load"/>, which check the document against the embedded <c>Materials/material.xsd</c> and the rules a schema cannot say (a name is used once, a
/// reference names the kind of file it should, a material without a parent has a shader), and refuse hostile input: more than <see cref="MaxBytes"/>, more than <see cref="MaxParameters"/> parameters, a chain of
/// parents longer than <see cref="MaxParentDepth"/> or one that loops.
/// </para>
/// </summary>
public sealed class MaterialAsset
{
    /// <summary>The largest document read, in bytes. A real one is a few hundred; a bigger one is refused rather than parsed.</summary>
    public const int MaxBytes = 1 << 20;

    /// <summary>The most parameters (of every type together) one document may hold.</summary>
    public const int MaxParameters = 256;

    /// <summary>The most textures one document may hold.</summary>
    public const int MaxTextures = 64;

    /// <summary>The most parents a material may have above it: its parent, that one's parent, and so on.</summary>
    public const int MaxParentDepth = 8;

    /// <summary>The alpha cutoff of a material that does not say.</summary>
    public const float DefaultAlphaCutoff = 0.5f;

    private static readonly Lazy<SchemaSet> s_schema = new(() =>
    {
        using Stream stream = typeof(MaterialAsset).Assembly.GetManifestResourceStream(AssetFormats.Material.SchemaResource!) ?? throw new InvalidOperationException("The embedded schema of materials is missing.");
        using var reader = new StreamReader(stream);
        var set = new SchemaSet();
        set.LoadText(reader.ReadToEnd(), "material.xsd");
        return set;
    });

    /// <summary>The <c>uid</c> of the document, or <see cref="AssetUid.None"/> when it has none yet (the importer gives it one).</summary>
    public AssetUid Uid { get; init; }

    /// <summary>The version of the format the document is written in, 1 when it does not say. This engine reads 1.</summary>
    public int Version { get; init; } = AssetFormats.Material.Version;

    /// <summary>The shader template, a <c>.slang</c> asset. Empty when the document leaves it to its <see cref="Parent"/>.</summary>
    public AssetRef Shader { get; init; }

    /// <summary>The material this one is an instance of, a <c>.wmat</c> asset, or the empty reference.</summary>
    public AssetRef Parent { get; init; }

    /// <summary>How the alpha of the base color is used, or null when the document leaves it to its parent (and, at the top, to <see cref="AlphaMode.Opaque"/>).</summary>
    public AlphaMode? AlphaMode { get; init; }

    /// <summary>With <see cref="AlphaMode.Mask"/>, the alpha under which a pixel is not drawn, 0 to 1; null when the document does not say (<see cref="DefaultAlphaCutoff"/> at the top).</summary>
    public float? AlphaCutoff { get; init; }

    /// <summary>Whether both faces of a triangle are drawn; null when the document does not say (false at the top).</summary>
    public bool? DoubleSided { get; init; }

    /// <summary>The parameters the document sets, in file order. A name is used once.</summary>
    public IReadOnlyList<MaterialParameter> Parameters { get; init; } = [];

    /// <summary>The textures the document sets, in file order. A name is used once.</summary>
    public IReadOnlyList<MaterialTexture> Textures { get; init; } = [];

    // ---- Reading ------------------------------------------------------------------------------------------------------------------

    /// <summary>Reads the material at <paramref name="path"/>, text or cooked, never reading more than <see cref="MaxBytes"/> of it.</summary>
    /// <exception cref="ContentException">The file is too big, not a material, or breaks a rule of the format.</exception>
    /// <exception cref="FileNotFoundException">No source has the file.</exception>
    public static MaterialAsset Load(VirtualFileSystem files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        using Stream stream = files.OpenRead(path);
        if (stream.CanSeek && stream.Length > MaxBytes)
        {
            throw TooBig(path);
        }

        using var copy = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (copy.Length + read > MaxBytes)
            {
                throw TooBig(path);
            }

            copy.Write(chunk, 0, read);
        }

        return Parse(copy.GetBuffer().AsSpan(0, (int)copy.Length), path);
    }

    /// <summary>Reads the material in <paramref name="text"/>; <paramref name="origin"/> names it in errors.</summary>
    /// <exception cref="ContentException">The text is too long, not a material, or breaks a rule of the format.</exception>
    public static MaterialAsset Parse(string text, string origin)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxBytes)
        {
            throw TooBig(origin);
        }

        return Parse(Encoding.UTF8.GetBytes(text), origin);
    }

    /// <summary>Reads the material in <paramref name="bytes"/>: UTF-8 text (with or without a byte order mark), or cooked XML as a shipped pack holds it.</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="origin">The path to name in errors.</param>
    /// <exception cref="ContentException">The bytes are too many, not well-formed or damaged, not a material, or break a rule of the format (the message says which and where).</exception>
    public static MaterialAsset Parse(ReadOnlySpan<byte> bytes, string origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (bytes.Length > MaxBytes)
        {
            throw TooBig(origin);
        }

        AuthoredDocument document;
        try
        {
            document = AuthoredDocument.Read(bytes, AssetFormats.Material, origin, s_schema.Value);
        }
        catch (FormatException e)
        {
            throw new ContentException(e.Message, e);
        }

        XmlNode root = document.Root;
        var parameters = new List<MaterialParameter>();
        var textures = new List<MaterialTexture>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var textureNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (XmlNode child in root.Children)
        {
            string name = child.Attribute("name") ?? "";
            string value = child.Attribute("value") ?? "";
            if (child.Name == "Texture")
            {
                if (!textureNames.Add(name))
                {
                    throw new ContentException($"{origin} (line {child.Line}): the texture '{name}' is used twice.");
                }

                textures.Add(new MaterialTexture(name, ParseRef(child.Attribute("ref") ?? "", $"the texture '{name}'", origin, child.Line, IsTexture, "an image, " + AssetFormats.Ktx2.Extension + " or " + TextureExtension)));
                continue;
            }

            if (!names.Add(name))
            {
                throw new ContentException($"{origin} (line {child.Line}): the parameter '{name}' is used twice.");
            }

            parameters.Add(ParseParameter(child.Name, name, value, origin, child.Line));
        }

        if (parameters.Count > MaxParameters)
        {
            throw new ContentException($"{origin}: {parameters.Count} parameters, more than the {MaxParameters} allowed.");
        }

        AssetRef shader = root.HasAttribute("shader") ? ParseRef(root.Attribute("shader")!, "the shader", origin, root.Line, IsShader, "a " + AssetFormats.Slang.Extension + " shader") : default;
        AssetRef parent = root.HasAttribute("parent") ? ParseRef(root.Attribute("parent")!, "the parent", origin, root.Line, IsMaterial, "a " + AssetFormats.Material.Extension + " material") : default;
        if (shader.IsEmpty && parent.IsEmpty)
        {
            throw new ContentException($"{origin}: the material has no shader and no parent to take one from.");
        }

        return new MaterialAsset
        {
            Uid = document.Uid,
            Version = document.Version == 0 ? AssetFormats.Material.Version : document.Version,
            Shader = shader,
            Parent = parent,
            AlphaMode = root.Attribute("alphaMode") switch { "opaque" => Content.AlphaMode.Opaque, "mask" => Content.AlphaMode.Mask, "blend" => Content.AlphaMode.Blend, _ => null },
            AlphaCutoff = root.HasAttribute("alphaCutoff") ? Number(root.Attribute("alphaCutoff")!, "alphaCutoff", origin, root.Line) : null,
            DoubleSided = root.HasAttribute("doubleSided") ? Flag(root.Attribute("doubleSided")!) : null,
            Parameters = parameters,
            Textures = textures,
        };
    }

    private static ContentException TooBig(string origin) => new($"{origin}: the material is larger than the {MaxBytes / 1024} KB allowed.");

    private static MaterialParameter ParseParameter(string element, string name, string value, string origin, int line)
    {
        if (element is "Int")
        {
            return MaterialParameter.Int(name, int.Parse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        }

        if (element is "Bool")
        {
            return MaterialParameter.Bool(name, Flag(value));
        }

        string[] parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        float[] numbers = [.. parts.Select(p => Number(p, name, origin, line))];
        return element switch
        {
            "Float" => MaterialParameter.Float(name, numbers[0]),
            "Vec2" => MaterialParameter.Vec2(name, new Vector2(numbers[0], numbers[1])),
            "Vec3" => MaterialParameter.Vec3(name, new Vector3(numbers[0], numbers[1], numbers[2])),
            "Vec4" => MaterialParameter.Vec4(name, new Vector4(numbers[0], numbers[1], numbers[2], numbers[3])),
            "Color" => MaterialParameter.Color(name, new Vector4(numbers[0], numbers[1], numbers[2], numbers.Length > 3 ? numbers[3] : 1f)),
            _ => throw new ContentException($"{origin} (line {line}): <{element}> is not a parameter."),
        };
    }

    // The schema already checked the text is a number; a number that is too large for a float is not one the shader can use.
    private static float Number(string text, string what, string origin, int line)
    {
        float number = (float)double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return float.IsFinite(number) ? number : throw new ContentException($"{origin} (line {line}): '{text.Trim()}' in {what} is too large for a number.");
    }

    private static bool Flag(string text) => text.Trim() is "true" or "1";

    private static AssetRef ParseRef(string text, string what, string origin, int line, Func<AssetRef, bool> accepts, string expected)
    {
        if (!AssetRef.TryParse(text, out AssetRef reference) || reference.IsEmpty)
        {
            throw new ContentException($"{origin} (line {line}): {what} is '{text}', which is not an asset reference (a path, a uid://, or path|uid://).");
        }

        return accepts(reference) ? reference : throw new ContentException($"{origin} (line {line}): {what} is '{text}', which is not the kind of file it should be ({expected}).");
    }

    // The ".wtex" row is added to the table by the texture format; until then its extension is spelled here.
    private const string TextureExtension = ".wtex";

    private static bool Has(AssetRef reference, string extension) =>
        !reference.HasPath || reference.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsShader(AssetRef reference) => Has(reference, AssetFormats.Slang.Extension);

    private static bool IsMaterial(AssetRef reference) => Has(reference, AssetFormats.Material.Extension);

    private static bool IsTexture(AssetRef reference) =>
        !reference.HasPath || AssetFormats.ExtensionsOfType("texture").Append(AssetFormats.Ktx2.Extension).Append(TextureExtension).Any(e => reference.Path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    // ---- Writing ------------------------------------------------------------------------------------------------------------------

    /// <summary>The document as XML elements, the form <see cref="ToXml"/> writes and <see cref="Parse(string, string)"/> reads back.</summary>
    public XmlNode ToNode()
    {
        var root = new XmlNode(AssetFormats.Material.RootElement!);
        root.SetAttribute("version", Version.ToString(CultureInfo.InvariantCulture));
        if (!Uid.IsNone)
        {
            root.SetAttribute("uid", Uid.ToString());
        }

        if (!Shader.IsEmpty)
        {
            root.SetAttribute("shader", Shader.ToString());
        }

        if (!Parent.IsEmpty)
        {
            root.SetAttribute("parent", Parent.ToString());
        }

        if (AlphaMode is { } alpha)
        {
            root.SetAttribute("alphaMode", alpha.ToString().ToLowerInvariant());
        }

        if (AlphaCutoff is { } cutoff)
        {
            root.SetAttribute("alphaCutoff", Text(cutoff));
        }

        if (DoubleSided is { } doubleSided)
        {
            root.SetAttribute("doubleSided", doubleSided ? "true" : "false");
        }

        foreach (MaterialParameter parameter in Parameters)
        {
            var node = new XmlNode(parameter.Type.ToString());
            node.SetAttribute("name", parameter.Name);
            node.SetAttribute("value", ValueText(parameter));
            root.Children.Add(node);
        }

        foreach (MaterialTexture texture in Textures)
        {
            var node = new XmlNode("Texture");
            node.SetAttribute("name", texture.Name);
            node.SetAttribute("ref", texture.Ref.ToString());
            root.Children.Add(node);
        }

        return root;
    }

    /// <summary>The document as text, ready to be saved as a <c>.wmat</c> file.</summary>
    public string ToXml() => ToNode().ToXml();

    private static string Text(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string ValueText(MaterialParameter parameter)
    {
        Vector4 v = parameter.Value;
        return parameter.Type switch
        {
            MaterialParameterType.Float => Text(v.X),
            MaterialParameterType.Vec2 => $"{Text(v.X)} {Text(v.Y)}",
            MaterialParameterType.Vec3 => $"{Text(v.X)} {Text(v.Y)} {Text(v.Z)}",
            MaterialParameterType.Vec4 or MaterialParameterType.Color => $"{Text(v.X)} {Text(v.Y)} {Text(v.Z)} {Text(v.W)}",
            MaterialParameterType.Int => parameter.Integer.ToString(CultureInfo.InvariantCulture),
            _ => parameter.AsBool ? "true" : "false",
        };
    }

    // ---- Resolving ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole material: this document laid over its parent, that over its own, up to the top of the chain. A child overrides only what it lists: a parameter or texture of the same name is replaced
    /// (in its place, so the order stays the parent's), a new one is added after, and a switch (<see cref="AlphaMode"/>, <see cref="AlphaCutoff"/>, <see cref="DoubleSided"/>, <see cref="Shader"/>) that the child leaves out
    /// is the parent's. A parameter keeps the type its first definition gave it.
    /// </summary>
    /// <param name="assets">Where the parents are found (by path, or by UID when their file moved).</param>
    /// <param name="path">Where this document is, as a virtual path: what a cycle through it is recognised by.</param>
    /// <exception cref="ContentException">A parent does not exist or is not a valid material, the chain is longer than <see cref="MaxParentDepth"/> or comes back to a material it already went through, or a child changes the type of a parameter.</exception>
    public ResolvedMaterial Resolve(AssetDatabase assets, string path)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(path);
        if (!VfsPath.TryParse(path, out string own, out string? problem))
        {
            throw new ContentException($"'{path}' is not a valid path: {problem}.");
        }

        var chain = new List<(string Path, MaterialAsset Material)> { (own, this) };
        var seen = new HashSet<string>(StringComparer.Ordinal) { own };
        MaterialAsset current = this;
        while (!current.Parent.IsEmpty)
        {
            string from = chain[^1].Path;
            if (chain.Count > MaxParentDepth)
            {
                throw new ContentException($"{own}: more than {MaxParentDepth} parents deep (a chain that long is most likely a loop that got past the check): {string.Join(" -> ", chain.Select(c => c.Path))}.");
            }

            string parentPath = assets.Resolve(current.Parent) ?? throw new ContentException($"{from}: its parent {current.Parent} does not exist.");
            if (!seen.Add(parentPath))
            {
                throw new ContentException($"{own}: the parents go round in a circle: {string.Join(" -> ", chain.Select(c => c.Path))} -> {parentPath}.");
            }

            if (!AssetFormats.Material.Matches(parentPath))
            {
                throw new ContentException($"{from}: its parent {parentPath} is not a {AssetFormats.Material.Extension} file.");
            }

            current = Load(assets.Files, parentPath);
            chain.Add((parentPath, current));
        }

        return Merge(chain);
    }

    private static ResolvedMaterial Merge(List<(string Path, MaterialAsset Material)> chain)
    {
        var parameters = new List<MaterialParameter>();
        var textures = new List<MaterialTexture>();
        AssetRef shader = default;
        AlphaMode alphaMode = Content.AlphaMode.Opaque;
        float cutoff = DefaultAlphaCutoff;
        bool doubleSided = false;
        var where = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            (string path, MaterialAsset material) = chain[i];
            shader = material.Shader.IsEmpty ? shader : material.Shader;
            alphaMode = material.AlphaMode ?? alphaMode;
            cutoff = material.AlphaCutoff ?? cutoff;
            doubleSided = material.DoubleSided ?? doubleSided;
            foreach (MaterialParameter parameter in material.Parameters)
            {
                int at = parameters.FindIndex(p => p.Name == parameter.Name);
                if (at < 0)
                {
                    parameters.Add(parameter);
                    where[parameter.Name] = path;
                }
                else if (parameters[at].Type != parameter.Type)
                {
                    throw new ContentException($"{path}: the parameter '{parameter.Name}' is a {parameters[at].Type} in {where[parameter.Name]}, and cannot be a {parameter.Type} here.");
                }
                else
                {
                    parameters[at] = parameter;
                }
            }

            foreach (MaterialTexture texture in material.Textures)
            {
                int at = textures.FindIndex(t => t.Name == texture.Name);
                if (at < 0)
                {
                    textures.Add(texture);
                }
                else
                {
                    textures[at] = texture;
                }
            }
        }

        if (shader.IsEmpty)
        {
            throw new ContentException($"{chain[0].Path}: neither the material nor its parents name a shader.");
        }

        return new ResolvedMaterial(shader, alphaMode, cutoff, doubleSided, parameters, textures, [.. chain.Select(c => c.Path)]);
    }
}

/// <summary>A material with its parents laid under it (<see cref="MaterialAsset.Resolve"/>): every setting has a value.</summary>
/// <param name="Shader">The shader template, the nearest the chain names.</param>
/// <param name="AlphaMode">How alpha is used.</param>
/// <param name="AlphaCutoff">The cutoff of <see cref="AlphaMode.Mask"/>, 0 to 1.</param>
/// <param name="DoubleSided">Whether both faces are drawn.</param>
/// <param name="Parameters">The parameters, the top of the chain's first, each with the value of the nearest material that sets it.</param>
/// <param name="Textures">The textures, in the same way.</param>
/// <param name="Chain">The paths of the documents it was made from: the material itself first, its parent next, and so on to the top.</param>
public sealed record ResolvedMaterial(
    AssetRef Shader,
    AlphaMode AlphaMode,
    float AlphaCutoff,
    bool DoubleSided,
    IReadOnlyList<MaterialParameter> Parameters,
    IReadOnlyList<MaterialTexture> Textures,
    IReadOnlyList<string> Chain)
{
    /// <summary>The parameter called <paramref name="name"/>, or null.</summary>
    public MaterialParameter? Parameter(string name)
    {
        foreach (MaterialParameter parameter in Parameters)
        {
            if (parameter.Name == name)
            {
                return parameter;
            }
        }

        return null;
    }

    /// <summary>The texture called <paramref name="name"/>, or null.</summary>
    public MaterialTexture? Texture(string name)
    {
        foreach (MaterialTexture texture in Textures)
        {
            if (texture.Name == name)
            {
                return texture;
            }
        }

        return null;
    }
}
