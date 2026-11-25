using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Content.Import;
using Weft.Content.Materials;
using Weft.Content.Packs;
using Weft.Content.Xml;
using Weft.Core.Assets;
using Xunit;

namespace Weft.Content.Tests;

/// <summary>Material documents (<c>.wmat</c>): reading and writing, the schema, parents, references and the cooked pack.</summary>
public class MaterialAssetTests
{
    /// <summary>Takes the files a material names (an image, a shader) and keeps them as they are, so that they get UIDs without any native library.</summary>
    private sealed class FileImporter : IAssetImporter
    {
        public string Name => "file";

        public IReadOnlyList<string> Extensions { get; } = [".png", ".slang"];

        public string Fingerprint => "1";

        public ImportResult? Import(ImportContext context) => new(context.Bytes.ToArray(), ".up");
    }

    private sealed class Project : IDisposable
    {
        private readonly TempFolder _folder = new();

        public Project()
        {
            Files = new VirtualFileSystem();
            Files.Mount("res://", new DirectorySource(_folder.Root, writable: true));
            Assets = new AssetDatabase(Files);
        }

        public VirtualFileSystem Files { get; }

        public AssetDatabase Assets { get; }

        public string Root => _folder.Root;

        public void Write(string path, string text) => _folder.Write(path, text);

        public string Read(string path) => File.ReadAllText(Path.Combine(_folder.Root, path));

        public ImportReport Import() => new AssetImporter(Files, [new FileImporter()]).Run();

        public AssetUid UidOf(string path) => AuthoredAssets.ReadUid(File.ReadAllBytes(Path.Combine(_folder.Root, path)));

        public void Dispose() => _folder.Dispose();
    }

    private const string Lit = "res://shaders/lit.slang";

    private const string Example = """
        <Material version="1" uid="uid://4k2m9x7q1w3ze" shader="res://shaders/lit.slang" parent="res://materials/base.wmat" alphaMode="mask" alphaCutoff="0.35" doubleSided="true">
          <Color name="baseColor" value="0.8 0.2 0.1 1"/>
          <Float name="roughness" value="0.4"/>
          <Float name="metallic" value="0"/>
          <Vec2 name="uvScale" value="2 2"/>
          <Vec3 name="emissive" value="0 0.5 1"/>
          <Vec4 name="tint" value="1 1 1 0.5"/>
          <Int name="layers" value="3"/>
          <Bool name="receiveShadows" value="false"/>
          <Texture name="baseColor" ref="res://textures/brick.png"/>
          <Texture name="normal" ref="res://textures/brick_n.ktx2|uid://4k2m9x7q1w3zf"/>
        </Material>
        """;

    private static MaterialAsset Parse(string text) => MaterialAsset.Parse(text, "m.wmat");

    private static string Refusal(string text) => Assert.Throws<ContentException>(() => Parse(text)).Message;

    private static string Doc(string attributes = "", string children = "") => $"<Material shader=\"{Lit}\" {attributes}>{children}</Material>";

    // ---- The format ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ItIsAnAuthoredFormatInTheTableWithItsSchemaEmbedded()
    {
        AssetFormat format = AssetFormats.Material;

        Assert.Same(format, AssetFormats.Find("res://materials/rock.WMAT"));
        Assert.Equal(AssetFormKind.Authored, format.Kind);
        Assert.Equal("Material", format.RootElement);
        Assert.Equal(1, format.Version);
        Assert.Equal(CookRule.Xml, format.Cook);
        Assert.Contains(".wmat", AuthoredAssets.Extensions);
        Assert.True(AuthoredAssets.IsAuthored("res://materials/rock.wmat"));
        Assert.Equal([".wmat"], AssetFormats.ExtensionsOfType("material"));
        using Stream? schema = typeof(MaterialAsset).Assembly.GetManifestResourceStream(format.SchemaResource!);
        Assert.NotNull(schema);
    }

    [Fact]
    public void TheDocumentOfTheManualReadsAsItIsWritten()
    {
        MaterialAsset material = Parse(Example);

        Assert.Equal(AssetUid.Parse("uid://4k2m9x7q1w3ze"), material.Uid);
        Assert.Equal(1, material.Version);
        Assert.Equal(new AssetRef(Lit, AssetUid.None), material.Shader);
        Assert.Equal(new AssetRef("res://materials/base.wmat", AssetUid.None), material.Parent);
        Assert.Equal(AlphaMode.Mask, material.AlphaMode);
        Assert.Equal(0.35f, material.AlphaCutoff);
        Assert.True(material.DoubleSided);
        Assert.Equal(
            [
                MaterialParameter.Color("baseColor", new Vector4(0.8f, 0.2f, 0.1f, 1f)),
                MaterialParameter.Float("roughness", 0.4f),
                MaterialParameter.Float("metallic", 0f),
                MaterialParameter.Vec2("uvScale", new Vector2(2, 2)),
                MaterialParameter.Vec3("emissive", new Vector3(0, 0.5f, 1)),
                MaterialParameter.Vec4("tint", new Vector4(1, 1, 1, 0.5f)),
                MaterialParameter.Int("layers", 3),
                MaterialParameter.Bool("receiveShadows", false),
            ],
            material.Parameters);
        Assert.Equal(2, material.Textures.Count);
        Assert.Equal(new MaterialTexture("baseColor", new AssetRef("res://textures/brick.png", AssetUid.None)), material.Textures[0]);
        Assert.Equal(AssetUid.Parse("uid://4k2m9x7q1w3zf"), material.Textures[1].Ref.Uid);
    }

    [Fact]
    public void WhatIsLeftOutStaysUnsaid()
    {
        MaterialAsset material = Parse(Doc());

        Assert.Equal(AssetUid.None, material.Uid);
        Assert.Equal(1, material.Version);
        Assert.True(material.Parent.IsEmpty);
        Assert.Null(material.AlphaMode);
        Assert.Null(material.AlphaCutoff);
        Assert.Null(material.DoubleSided);
        Assert.Empty(material.Parameters);
        Assert.Empty(material.Textures);
    }

    [Fact]
    public void ADocumentWritesAndReadsBackTheSame()
    {
        MaterialAsset material = new()
        {
            Uid = AssetUid.New(),
            Shader = new AssetRef(Lit, AssetUid.New()),
            Parent = new AssetRef("res://materials/base.wmat", AssetUid.New()),
            AlphaMode = AlphaMode.Blend,
            AlphaCutoff = 0.1f,
            DoubleSided = false,
            Parameters =
            [
                MaterialParameter.Float("roughness", 1f / 3f),
                MaterialParameter.Color("baseColor", new Vector4(0.1f, 0.123456789f, 1e-5f, 0.5f)),
                MaterialParameter.Vec2("a", new Vector2(-1, 2)),
                MaterialParameter.Vec3("b", new Vector3(1e6f, 0, -0.25f)),
                MaterialParameter.Vec4("c", new Vector4(1, 2, 3, 4)),
                MaterialParameter.Int("n", int.MinValue),
                MaterialParameter.Bool("on", true),
            ],
            Textures = [new MaterialTexture("normal", new AssetRef("res://t/n.wtex", AssetUid.New())), new MaterialTexture("detail", AssetRef.FromUid(AssetUid.New()))],
        };

        MaterialAsset back = Parse(material.ToXml());

        Assert.Equal(material.Uid, back.Uid);
        Assert.Equal(material.Shader, back.Shader);
        Assert.Equal(material.Parent, back.Parent);
        Assert.Equal(material.AlphaMode, back.AlphaMode);
        Assert.Equal(material.AlphaCutoff, back.AlphaCutoff);
        Assert.Equal(material.DoubleSided, back.DoubleSided);
        Assert.Equal(material.Parameters, back.Parameters);
        Assert.Equal(material.Textures, back.Textures);
        Assert.Equal(material.ToXml(), back.ToXml());
    }

    [Fact]
    public void AColorWithoutAlphaIsOpaque()
    {
        Assert.Equal(MaterialParameter.Color("c", new Vector4(0.5f, 0.25f, 1f, 1f)), Parse(Doc(children: "<Color name=\"c\" value=\"0.5 0.25 1\"/>")).Parameters.Single());
    }

    [Fact]
    public void AParameterAndATextureMayShareAName()
    {
        MaterialAsset material = Parse(Doc(children: "<Float name=\"normal\" value=\"1\"/><Texture name=\"normal\" ref=\"res://n.png\"/>"));

        Assert.Single(material.Parameters);
        Assert.Single(material.Textures);
    }

    [Fact]
    public void TheOrderOfTheElementsIsFree()
    {
        MaterialAsset material = Parse(Doc(children: "<Texture name=\"t\" ref=\"res://t.png\"/><Bool name=\"b\" value=\"1\"/><Float name=\"f\" value=\"1\"/><Texture name=\"u\" ref=\"res://u.png\"/>"));

        Assert.Equal(["b", "f"], material.Parameters.Select(p => p.Name));
        Assert.Equal(["t", "u"], material.Textures.Select(t => t.Name));
        Assert.True(material.Parameters[0].AsBool);
    }

    [Fact]
    public void ABomAndCommentsAreFine()
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("<!-- rock -->\n" + Doc())];

        Assert.Equal(new AssetRef(Lit, AssetUid.None), MaterialAsset.Parse(bytes, "m.wmat").Shader);
    }

    // ---- Refusals -----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<Scene/>", "not a material")]
    [InlineData("<Material shader=\"res://s.slang\" version=\"2\"/>", "version 2")]
    [InlineData("<Material shader=\"res://s.slang\" version=\"x\"/>", "not a number")]
    [InlineData("<Material shader=\"res://s.slang\" colour=\"red\"/>", "colour")]
    [InlineData("<Material shader=\"res://s.slang\" uid=\"uid://nonsense\"/>", "uid")]
    [InlineData("<Material shader=\"res://s.slang\" alphaMode=\"glass\"/>", "alphaMode")]
    [InlineData("<Material shader=\"res://s.slang\" alphaCutoff=\"2\"/>", "alphaCutoff")]
    [InlineData("<Material shader=\"res://s.slang\" alphaCutoff=\"-0.1\"/>", "alphaCutoff")]
    [InlineData("<Material shader=\"res://s.slang\" doubleSided=\"maybe\"/>", "doubleSided")]
    [InlineData("<Material shader=\"res://s.slang\"><Shiny name=\"a\" value=\"1\"/></Material>", "Shiny")]
    [InlineData("<Material shader=\"res://s.slang\"><Float value=\"1\"/></Material>", "name")]
    [InlineData("<Material shader=\"res://s.slang\"><Float name=\"a\"/></Material>", "value")]
    [InlineData("<Material shader=\"res://s.slang\"><Float name=\"a\" value=\"fast\"/></Material>", "fast")]
    [InlineData("<Material shader=\"res://s.slang\"><Float name=\"a\" value=\"NaN\"/></Material>", "NaN")]
    [InlineData("<Material shader=\"res://s.slang\"><Float name=\"1a\" value=\"1\"/></Material>", "1a")]
    [InlineData("<Material shader=\"res://s.slang\"><Float name=\"a b\" value=\"1\"/></Material>", "a b")]
    [InlineData("<Material shader=\"res://s.slang\"><Vec2 name=\"a\" value=\"1 2 3\"/></Material>", "1 2 3")]
    [InlineData("<Material shader=\"res://s.slang\"><Vec3 name=\"a\" value=\"1 2\"/></Material>", "1 2")]
    [InlineData("<Material shader=\"res://s.slang\"><Vec4 name=\"a\" value=\"1 2 3\"/></Material>", "1 2 3")]
    [InlineData("<Material shader=\"res://s.slang\"><Color name=\"a\" value=\"1 2\"/></Material>", "1 2")]
    [InlineData("<Material shader=\"res://s.slang\"><Color name=\"a\" value=\"1 2 3 4 5\"/></Material>", "1 2 3 4 5")]
    [InlineData("<Material shader=\"res://s.slang\"><Int name=\"a\" value=\"1.5\"/></Material>", "1.5")]
    [InlineData("<Material shader=\"res://s.slang\"><Int name=\"a\" value=\"3000000000\"/></Material>", "3000000000")]
    [InlineData("<Material shader=\"res://s.slang\"><Bool name=\"a\" value=\"yes\"/></Material>", "yes")]
    [InlineData("<Material shader=\"res://s.slang\"><Texture name=\"a\"/></Material>", "ref")]
    [InlineData("<Material shader=\"res://s.slang\"><Texture name=\"a\" ref=\"\"/></Material>", "ref")]
    public void TheSchemaRefusesWhatItDoesNotDescribe(string text, string mention)
    {
        string message = Refusal(text);

        Assert.Contains(mention, message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANumberTooLargeForAFloatIsRefused()
    {
        Assert.Contains("too large", Refusal(Doc(children: "<Float name=\"a\" value=\"1e300\"/>")), StringComparison.Ordinal);
    }

    [Fact]
    public void ANotWellFormedDocumentIsAContentError()
    {
        Assert.IsAssignableFrom<ContentException>(Assert.Throws<XmlParseException>(() => Parse("<Material shader=\"res://s.slang\">")));
    }

    [Fact]
    public void WhatTheSchemaCannotSayIsRefusedToo()
    {
        Assert.Contains("'a' is used twice", Refusal(Doc(children: "<Float name=\"a\" value=\"1\"/><Int name=\"a\" value=\"2\"/>")), StringComparison.Ordinal);
        Assert.Contains("texture 'a' is used twice", Refusal(Doc(children: "<Texture name=\"a\" ref=\"res://a.png\"/><Texture name=\"a\" ref=\"res://b.png\"/>")), StringComparison.Ordinal);
        Assert.Contains("no shader and no parent", Refusal("<Material/>"), StringComparison.Ordinal);
        Assert.Contains("not an asset reference", Refusal("<Material shader=\"res://a.slang|nonsense\"/>"), StringComparison.Ordinal);
        Assert.Contains("the shader", Refusal("<Material shader=\"res://a.png\"/>"), StringComparison.Ordinal);
        Assert.Contains("the parent", Refusal("<Material parent=\"res://a.png\"/>"), StringComparison.Ordinal);
        Assert.Contains("the texture 'a'", Refusal(Doc(children: "<Texture name=\"a\" ref=\"res://a.glb\"/>")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("res://t.png")]
    [InlineData("res://t.JPG")]
    [InlineData("res://t.jpeg")]
    [InlineData("res://t.ktx2")]
    [InlineData("res://t.wtex")]
    [InlineData("uid://4k2m9x7q1w3ze")]
    [InlineData("res://t.png|uid://4k2m9x7q1w3ze")]
    public void ATextureIsAnImageOrATextureAssetOrJustAUid(string reference)
    {
        Assert.Equal(reference, Parse(Doc(children: $"<Texture name=\"a\" ref=\"{reference}\"/>")).Textures.Single().Ref.ToString());
    }

    [Fact]
    public void AMaterialWithAParentNeedsNoShader()
    {
        MaterialAsset material = Parse("<Material parent=\"res://materials/base.wmat\"><Float name=\"roughness\" value=\"0.9\"/></Material>");

        Assert.True(material.Shader.IsEmpty);
        Assert.Equal("res://materials/base.wmat", material.Parent.Path);
    }

    // ---- Hostile input ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADocumentOverTheSizeLimitIsRefusedBeforeItIsParsed()
    {
        string text = Doc() + new string(' ', MaterialAsset.MaxBytes);

        Assert.Contains("larger than", Refusal(text), StringComparison.Ordinal);
        Assert.Contains("larger than", Assert.Throws<ContentException>(() => MaterialAsset.Parse(Encoding.UTF8.GetBytes(text).AsSpan(), "m.wmat")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadNeverReadsMoreThanTheLimit()
    {
        using var project = new Project();
        project.Write("big.wmat", Doc() + new string('x', MaterialAsset.MaxBytes));

        Assert.Contains("larger than", Assert.Throws<ContentException>(() => MaterialAsset.Load(project.Files, "res://big.wmat")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TooManyParametersOfAllTypesTogetherAreRefused()
    {
        string many = string.Concat(Enumerable.Range(0, 200).Select(i => $"<Float name=\"f{i}\" value=\"1\"/>")) + string.Concat(Enumerable.Range(0, 100).Select(i => $"<Int name=\"i{i}\" value=\"1\"/>"));

        Assert.Contains("300 parameters", Refusal(Doc(children: many)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLimitsOfTheSchemaHoldPerElement()
    {
        string floats = string.Concat(Enumerable.Range(0, MaterialAsset.MaxParameters + 1).Select(i => $"<Float name=\"f{i}\" value=\"1\"/>"));
        string textures = string.Concat(Enumerable.Range(0, MaterialAsset.MaxTextures + 1).Select(i => $"<Texture name=\"t{i}\" ref=\"res://t{i}.png\"/>"));

        Assert.NotEmpty(Refusal(Doc(children: floats)));
        Assert.NotEmpty(Refusal(Doc(children: textures)));
        string full = string.Concat(Enumerable.Range(0, MaterialAsset.MaxParameters).Select(i => $"<Float name=\"f{i}\" value=\"1\"/>"));
        Assert.Equal(MaterialAsset.MaxParameters, Parse(Doc(children: full)).Parameters.Count);
    }

    [Fact]
    public void AnOverlongNameOrReferenceIsRefused()
    {
        Assert.NotEmpty(Refusal(Doc(children: $"<Float name=\"{new string('a', 65)}\" value=\"1\"/>")));
        Assert.NotEmpty(Refusal(Doc(children: $"<Texture name=\"a\" ref=\"res://{new string('a', 1100)}.png\"/>")));
    }

    [Fact]
    public void DeeplyNestedAndDoctypeDocumentsAreRefused()
    {
        string deep = "<Material shader=\"res://s.slang\">" + string.Concat(Enumerable.Repeat("<A>", 500)) + string.Concat(Enumerable.Repeat("</A>", 500)) + "</Material>";

        Assert.IsAssignableFrom<ContentException>(Record.Exception(() => Parse(deep)));
        Assert.IsAssignableFrom<ContentException>(Record.Exception(() => Parse("<!DOCTYPE Material [<!ENTITY x \"y\">]><Material shader=\"res://s.slang\" name=\"&x;\"/>")));
    }

    [Fact]
    public void GarbageIsAContentErrorNotACrash()
    {
        byte[] cooked = XmlBinary.Write(XmlNode.Parse(Doc()));

        Assert.IsAssignableFrom<ContentException>(Record.Exception(() => MaterialAsset.Parse(cooked.AsSpan(0, cooked.Length - 3), "m.wmat")));
        Assert.IsAssignableFrom<ContentException>(Record.Exception(() => MaterialAsset.Parse([0xFF, 0xFE, 0x00, 0x01], "m.wmat")));
        Assert.IsAssignableFrom<ContentException>(Record.Exception(() => MaterialAsset.Parse([], "m.wmat")));
    }

    // ---- Parents ------------------------------------------------------------------------------------------------------------------

    private static string Base => $"""
        <Material version="1" shader="{Lit}" alphaMode="mask" alphaCutoff="0.25">
          <Color name="baseColor" value="1 1 1 1"/>
          <Float name="roughness" value="0.8"/>
          <Float name="metallic" value="0"/>
          <Texture name="baseColor" ref="res://textures/white.png"/>
          <Texture name="normal" ref="res://textures/flat.png"/>
        </Material>
        """;

    [Fact]
    public void AChildOverridesOnlyWhatItListsAndTheRestComesFromItsParent()
    {
        using var project = new Project();
        project.Write("materials/base.wmat", Base);
        project.Write("materials/rust.wmat", """
            <Material parent="res://materials/base.wmat" doubleSided="true">
              <Float name="roughness" value="0.95"/>
              <Float name="rust" value="1"/>
              <Texture name="baseColor" ref="res://textures/rust.png"/>
            </Material>
            """);

        ResolvedMaterial rust = MaterialAsset.Load(project.Files, "res://materials/rust.wmat").Resolve(project.Assets, "res://materials/rust.wmat");

        Assert.Equal(new AssetRef(Lit, AssetUid.None), rust.Shader);
        Assert.Equal(AlphaMode.Mask, rust.AlphaMode);
        Assert.Equal(0.25f, rust.AlphaCutoff);
        Assert.True(rust.DoubleSided);
        Assert.Equal(["baseColor", "roughness", "metallic", "rust"], rust.Parameters.Select(p => p.Name)); // the parent's order, then what is new
        Assert.Equal(0.95f, rust.Parameter("roughness")!.Value.AsFloat);
        Assert.Equal(Vector4.One, rust.Parameter("baseColor")!.Value.Value);
        Assert.Null(rust.Parameter("missing"));
        Assert.Equal("res://textures/rust.png", rust.Texture("baseColor")!.Value.Ref.Path);
        Assert.Equal("res://textures/flat.png", rust.Texture("normal")!.Value.Ref.Path);
        Assert.Equal(["res://materials/rust.wmat", "res://materials/base.wmat"], rust.Chain);
    }

    [Fact]
    public void ASwitchTheChildSetsToTheDefaultStillOverrides()
    {
        using var project = new Project();
        project.Write("materials/base.wmat", Base);
        project.Write("materials/solid.wmat", "<Material parent=\"res://materials/base.wmat\" alphaMode=\"opaque\" alphaCutoff=\"0.5\" doubleSided=\"false\"/>");

        ResolvedMaterial solid = MaterialAsset.Load(project.Files, "res://materials/solid.wmat").Resolve(project.Assets, "res://materials/solid.wmat");

        Assert.Equal(AlphaMode.Opaque, solid.AlphaMode);
        Assert.Equal(0.5f, solid.AlphaCutoff);
        Assert.False(solid.DoubleSided);
    }

    [Fact]
    public void AMaterialWithNoParentHasTheDefaultsForWhatItLeavesOut()
    {
        using var project = new Project();
        project.Write("materials/plain.wmat", Doc());

        ResolvedMaterial plain = MaterialAsset.Load(project.Files, "res://materials/plain.wmat").Resolve(project.Assets, "res://materials/plain.wmat");

        Assert.Equal(AlphaMode.Opaque, plain.AlphaMode);
        Assert.Equal(MaterialAsset.DefaultAlphaCutoff, plain.AlphaCutoff);
        Assert.False(plain.DoubleSided);
        Assert.Equal(["res://materials/plain.wmat"], plain.Chain);
    }

    [Fact]
    public void ChainsOfParentsAreLaidOverOneAnotherFromTheTop()
    {
        using var project = new Project();
        project.Write("m/a.wmat", $"<Material shader=\"{Lit}\"><Float name=\"x\" value=\"1\"/><Float name=\"y\" value=\"1\"/><Float name=\"z\" value=\"1\"/></Material>");
        project.Write("m/b.wmat", "<Material parent=\"res://m/a.wmat\"><Float name=\"y\" value=\"2\"/><Float name=\"z\" value=\"2\"/></Material>");
        project.Write("m/c.wmat", "<Material parent=\"res://m/b.wmat\"><Float name=\"z\" value=\"3\"/></Material>");

        ResolvedMaterial c = MaterialAsset.Load(project.Files, "res://m/c.wmat").Resolve(project.Assets, "res://m/c.wmat");

        Assert.Equal([1f, 2f, 3f], c.Parameters.Select(p => p.AsFloat));
        Assert.Equal(["res://m/c.wmat", "res://m/b.wmat", "res://m/a.wmat"], c.Chain);
    }

    [Fact]
    public void AChildCannotChangeTheTypeOfAParameter()
    {
        using var project = new Project();
        project.Write("m/a.wmat", Base.Replace("res://textures", "res://t", StringComparison.Ordinal));
        project.Write("m/b.wmat", "<Material parent=\"res://m/a.wmat\"><Vec3 name=\"roughness\" value=\"1 1 1\"/></Material>");

        ContentException error = Assert.Throws<ContentException>(() => MaterialAsset.Load(project.Files, "res://m/b.wmat").Resolve(project.Assets, "res://m/b.wmat"));

        Assert.Contains("'roughness' is a Float in res://m/a.wmat", error.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be a Vec3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACycleIsRefusedWhicheverMaterialItIsReachedFrom()
    {
        using var project = new Project();
        project.Write("m/a.wmat", "<Material parent=\"res://m/b.wmat\"/>");
        project.Write("m/b.wmat", "<Material parent=\"res://m/c.wmat\"/>");
        project.Write("m/c.wmat", "<Material parent=\"res://m/a.wmat\"/>");
        project.Write("m/self.wmat", "<Material parent=\"res://m/self.wmat\"/>");
        project.Write("m/tail.wmat", "<Material parent=\"res://m/b.wmat\"/>");

        foreach (string name in new[] { "a", "b", "c", "self", "tail" })
        {
            string path = $"res://m/{name}.wmat";
            ContentException error = Assert.Throws<ContentException>(() => MaterialAsset.Load(project.Files, path).Resolve(project.Assets, path));
            Assert.Contains("circle", error.Message, StringComparison.Ordinal);
        }

        Assert.Contains("res://m/a.wmat -> res://m/b.wmat -> res://m/c.wmat -> res://m/a.wmat", Assert.Throws<ContentException>(() => MaterialAsset.Load(project.Files, "res://m/a.wmat").Resolve(project.Assets, "res://m/a.wmat")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACycleThroughUidsAndOddSpellingsIsStillACycle()
    {
        using var project = new Project();
        project.Write("m/a.wmat", "<Material parent=\"res://m/../m/b.wmat\"/>");
        project.Write("m/b.wmat", "<Material parent=\"res://m//a.wmat\"/>");

        Assert.Throws<ContentException>(() => MaterialAsset.Load(project.Files, "res://m/a.wmat").Resolve(project.Assets, "res://m/a.wmat"));
    }

    [Fact]
    public void TheParentChainStopsAtTheDepthLimit()
    {
        using var project = new Project();
        int depth = MaterialAsset.MaxParentDepth;
        project.Write("m/m0.wmat", $"<Material shader=\"{Lit}\"><Float name=\"x\" value=\"7\"/></Material>");
        for (int i = 1; i <= depth + 1; i++)
        {
            project.Write($"m/m{i}.wmat", $"<Material parent=\"res://m/m{i - 1}.wmat\"/>");
        }

        // A material with MaxParentDepth parents above it is the longest that is read.
        string longest = $"res://m/m{depth}.wmat";
        ResolvedMaterial ok = MaterialAsset.Load(project.Files, longest).Resolve(project.Assets, longest);
        Assert.Equal(depth + 1, ok.Chain.Count);
        Assert.Equal(7f, ok.Parameter("x")!.Value.AsFloat);

        string tooDeep = $"res://m/m{depth + 1}.wmat";
        ContentException error = Assert.Throws<ContentException>(() => MaterialAsset.Load(project.Files, tooDeep).Resolve(project.Assets, tooDeep));
        Assert.Contains($"more than {depth} parents deep", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingParentSaysWhichMaterialAskedForIt()
    {
        using var project = new Project();
        project.Write("m/a.wmat", "<Material parent=\"res://m/gone.wmat\"/>");

        ContentException error = Assert.Throws<ContentException>(() => MaterialAsset.Load(project.Files, "res://m/a.wmat").Resolve(project.Assets, "res://m/a.wmat"));

        Assert.StartsWith("res://m/a.wmat: its parent res://m/gone.wmat does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AParentThatIsNotAValidMaterialIsNamedInTheError()
    {
        using var project = new Project();
        project.Write("m/a.wmat", "<Material parent=\"res://m/b.wmat\"/>");
        project.Write("m/b.wmat", "<Material><Float name=\"a\" value=\"1\"/></Material>");

        ContentException error = Assert.Throws<ContentException>(() => MaterialAsset.Load(project.Files, "res://m/a.wmat").Resolve(project.Assets, "res://m/a.wmat"));

        Assert.Contains("no shader and no parent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePathGivenToResolveMustBeValid()
    {
        using var project = new Project();

        Assert.Throws<ContentException>(() => Parse(Doc()).Resolve(project.Assets, "not a path"));
    }

    // ---- References, moves ---------------------------------------------------------------------------------------------------------

    private static Project Textured()
    {
        var project = new Project();
        project.Write("textures/brick.png", "brick");
        project.Write("textures/brick_n.png", "normal");
        project.Write("shaders/lit.slang", "shader");
        project.Write("materials/base.wmat", $"<Material version=\"1\" shader=\"{Lit}\">\n  <Float name=\"roughness\" value=\"0.5\"/>\n</Material>\n");
        project.Import();
        AssetUid brick = ImportSidecar.ReadUid(project.Read("textures/brick.png.wimp"));
        AssetUid normal = ImportSidecar.ReadUid(project.Read("textures/brick_n.png.wimp"));
        AssetUid baseUid = project.UidOf("materials/base.wmat");
        project.Write("materials/wall.wmat", $"<Material version=\"1\" parent=\"res://materials/base.wmat|{baseUid}\">\n  <Texture name=\"baseColor\" ref=\"res://textures/brick.png|{brick}\"/>\n  <Texture name=\"normal\" ref=\"res://textures/brick_n.png\"/>\n</Material>\n");
        project.Import();
        _ = normal;
        return project;
    }

    [Fact]
    public void ImportGivesAMaterialItsUidInTheDocument()
    {
        using Project project = Textured();

        AssetUid uid = project.UidOf("materials/wall.wmat");

        Assert.False(uid.IsNone);
        Assert.Equal(uid, MaterialAsset.Load(project.Files, "res://materials/wall.wmat").Uid);
        Assert.Equal(uid, project.Assets.UidOf("res://materials/wall.wmat"));
        Assert.StartsWith("<Material version=\"1\"", project.Read("materials/wall.wmat"), StringComparison.Ordinal); // what was written is kept, the uid is added to the root
        Assert.Contains($"uid=\"{uid}\"", project.Read("materials/wall.wmat"), StringComparison.Ordinal);
    }

    [Fact]
    public void FindListsTheTextureAndParentReferencesOfMaterials()
    {
        using Project project = Textured();

        IReadOnlyList<AssetReferenceSite> texture = AssetReferences.Find(project.Assets, project.Assets.RefOf("res://textures/brick.png"));
        IReadOnlyList<AssetReferenceSite> parent = AssetReferences.Find(project.Assets, project.Assets.RefOf("res://materials/base.wmat")).Where(s => s.Attribute != "uid").ToList(); // a document's own uid is a mention of it too
        IReadOnlyList<AssetReferenceSite> shader = AssetReferences.Find(project.Assets, project.Assets.RefOf(Lit));

        AssetReferenceSite brick = Assert.Single(texture);
        Assert.Equal(("res://materials/wall.wmat", "Texture", "ref"), (brick.Document, brick.Element, brick.Attribute));
        AssetReferenceSite parentSite = Assert.Single(parent);
        Assert.Equal(("res://materials/wall.wmat", "Material", "parent"), (parentSite.Document, parentSite.Element, parentSite.Attribute));
        Assert.Equal(["res://materials/base.wmat"], shader.Select(s => s.Document));
        Assert.Equal("shader", Assert.Single(shader).Attribute);
    }

    [Fact]
    public void MovingATextureRewritesTheReferenceAndKeepsTheUid()
    {
        using Project project = Textured();
        string before = project.Read("materials/wall.wmat");
        AssetUid brick = project.Assets.UidOf("res://textures/brick.png");

        MoveReport moved = project.Assets.Move("res://textures/brick.png", "res://textures/walls/brick.png");

        Assert.True(moved.Applied);
        Assert.Equal(["res://materials/wall.wmat"], moved.References.Documents);
        string after = project.Read("materials/wall.wmat");
        Assert.Equal(before.Replace("res://textures/brick.png|", "res://textures/walls/brick.png|", StringComparison.Ordinal), after);
        MaterialAsset wall = MaterialAsset.Load(project.Files, "res://materials/wall.wmat");
        Assert.Equal(new AssetRef("res://textures/walls/brick.png", brick), wall.Textures[0].Ref);
        Assert.Equal(project.UidOf("materials/wall.wmat"), wall.Uid);
    }

    [Fact]
    public void MovingAParentRewritesTheChildAndTheChainStillResolves()
    {
        using Project project = Textured();
        AssetUid baseUid = project.UidOf("materials/base.wmat");

        project.Assets.Move("res://materials/base.wmat", "res://materials/common/base.wmat");

        MaterialAsset wall = MaterialAsset.Load(project.Files, "res://materials/wall.wmat");
        Assert.Equal(new AssetRef("res://materials/common/base.wmat", baseUid), wall.Parent);
        Assert.Equal(baseUid, project.UidOf("materials/common/base.wmat"));
        ResolvedMaterial resolved = wall.Resolve(project.Assets, "res://materials/wall.wmat");
        Assert.Equal(["res://materials/wall.wmat", "res://materials/common/base.wmat"], resolved.Chain);
        Assert.Equal(0.5f, resolved.Parameter("roughness")!.Value.AsFloat);
    }

    [Fact]
    public void MovingTheShaderRewritesTheMaterialsThatNameIt()
    {
        using Project project = Textured();

        project.Assets.Move(Lit, "res://shaders/pbr/lit.slang");

        Assert.Equal("res://shaders/pbr/lit.slang", MaterialAsset.Load(project.Files, "res://materials/base.wmat").Shader.Path);
    }

    [Fact]
    public void AParentThatWasMovedByHandIsFoundByItsUid()
    {
        using Project project = Textured();
        AssetUid baseUid = project.UidOf("materials/base.wmat");
        Directory.CreateDirectory(Path.Combine(project.Root, "elsewhere"));
        File.Move(Path.Combine(project.Root, "materials", "base.wmat"), Path.Combine(project.Root, "elsewhere", "root.wmat"));
        project.Assets.Refresh();

        ResolvedMaterial resolved = MaterialAsset.Load(project.Files, "res://materials/wall.wmat").Resolve(project.Assets, "res://materials/wall.wmat");

        Assert.Equal(["res://materials/wall.wmat", "res://elsewhere/root.wmat"], resolved.Chain);
        Assert.Equal(baseUid, project.Assets.UidOf("res://elsewhere/root.wmat"));
    }

    // ---- Cooked packs -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ACookedPackHoldsAMaterialAsBinaryXmlThatReadsBackTheSame()
    {
        using Project project = Textured();
        using var outFolder = new TempFolder();
        string output = Path.Combine(outFolder.Root, "game.wpak");
        {
            PackReport report = GamePacker.Pack(new PackRequest { Folder = project.Root, Output = output, Cooked = true });

            using var pack = PackSource.Open(report.Parts[0]);
            var files = new VirtualFileSystem();
            files.Mount("res://", pack);
            byte[] cooked = files.ReadAllBytes("res://materials/wall.wmat");
            Assert.True(XmlBinary.IsBinary(cooked));
            Assert.Equal(3, report.CookedDocuments); // the two materials and the UID list

            MaterialAsset fromPack = MaterialAsset.Load(files, "res://materials/wall.wmat");
            MaterialAsset fromFolder = MaterialAsset.Load(project.Files, "res://materials/wall.wmat");
            Assert.Equal(fromFolder.ToXml(), fromPack.ToXml());
            Assert.Equal(project.UidOf("materials/wall.wmat"), fromPack.Uid);
            Assert.Equal(fromPack.Uid, AuthoredAssets.ReadUid(cooked));

            // The chain resolves inside the pack, where the parent is cooked too.
            var assets = new AssetDatabase(files);
            ResolvedMaterial resolved = fromPack.Resolve(assets, "res://materials/wall.wmat");
            Assert.Equal(["res://materials/wall.wmat", "res://materials/base.wmat"], resolved.Chain);
            Assert.Equal(0.5f, resolved.Parameter("roughness")!.Value.AsFloat);
        }
    }

    [Fact]
    public void AMaterialParsedFromCookedBytesIsTheOneParsedFromText()
    {
        byte[] cooked = XmlBinary.Write(XmlNode.Parse(Example));

        MaterialAsset fromCooked = MaterialAsset.Parse(cooked, "m.wmat");

        Assert.Equal(Parse(Example).ToXml(), fromCooked.ToXml());
        Assert.Equal(AssetUid.Parse("uid://4k2m9x7q1w3ze"), AuthoredAssets.ReadUid(cooked));
    }
}
