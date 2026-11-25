using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using Weft.Content.Assets;
using Weft.Content.Import;
using Weft.Content.Packs;
using Weft.Content.Xml;
using Weft.Core.Assets;
using Xunit;

namespace Weft.Content.Tests;

/// <summary>The table of file formats, and the authored-document reader that goes by it.</summary>
public class AssetFormatsTests
{
    private static string Ascii(uint magic) => Encoding.ASCII.GetString(BitConverter.GetBytes(magic));

    [Fact]
    public void EveryBuiltInRowIsWhatTheManualSays()
    {
        (AssetFormat Format, string Extension, AssetFormKind Kind, string? Magic)[] expected =
        [
            (AssetFormats.Scene, ".wscn", AssetFormKind.Authored, null),
            (AssetFormats.Prefab, ".wprf", AssetFormKind.Authored, null),
            (AssetFormats.Sidecar, ".wimp", AssetFormKind.Sidecar, null),
            (AssetFormats.Game, ".wgam", AssetFormKind.Manifest, null),
            (AssetFormats.Mod, ".wmod", AssetFormKind.Manifest, null),
            (AssetFormats.Pack, ".wpak", AssetFormKind.Pack, "WPAK"),
            (AssetFormats.Model, ".wmdl", AssetFormKind.Imported, "WMDL"),
            (AssetFormats.Shader, ".wshd", AssetFormKind.Imported, "WSHD"),
            (AssetFormats.Ktx2, ".ktx2", AssetFormKind.Imported, null),
            (AssetFormats.CookedXml, "", AssetFormKind.Cooked, "WXMB"),
            (AssetFormats.Glb, ".glb", AssetFormKind.Source, null),
            (AssetFormats.Gltf, ".gltf", AssetFormKind.Source, null),
            (AssetFormats.GltfBuffer, ".bin", AssetFormKind.Source, null),
            (AssetFormats.Png, ".png", AssetFormKind.Source, null),
            (AssetFormats.Jpg, ".jpg", AssetFormKind.Source, null),
            (AssetFormats.Jpeg, ".jpeg", AssetFormKind.Source, null),
            (AssetFormats.Slang, ".slang", AssetFormKind.Source, null),
        ];
        foreach ((AssetFormat format, string extension, AssetFormKind kind, string? magic) in expected)
        {
            Assert.Equal(extension, format.Extension);
            Assert.Equal(kind, format.Kind);
            Assert.False(string.IsNullOrEmpty(format.Label));
            Assert.Contains(format, AssetFormats.All);
            if (magic is not null)
            {
                Assert.Equal(magic, Ascii(format.Magic));
            }
            else if (extension != ".ktx2")
            {
                Assert.Equal(0u, format.Magic);
            }

            // A format with an extension is found by it, in any case, and by no other row's.
            if (extension.Length > 0)
            {
                Assert.Same(format, AssetFormats.FindExtension(extension));
                Assert.Same(format, AssetFormats.FindExtension(extension.ToUpperInvariant()));
                Assert.Same(format, AssetFormats.Find("res://some/folder/file" + extension));
            }
        }

        foreach (AssetFormat plain in new[] { AssetFormats.Luau, AssetFormats.XmlData, AssetFormats.Wav, AssetFormats.Ogg, AssetFormats.Flac, AssetFormats.Mp3, AssetFormats.Ttf, AssetFormats.Otf, AssetFormats.Rml, AssetFormats.Rcss })
        {
            Assert.Equal(AssetFormKind.Plain, plain.Kind);
            Assert.Same(plain, AssetFormats.FindExtension(plain.Extension));
        }
    }

    [Fact]
    public void XmlRowsNameTheirRootAndTheirSchema()
    {
        foreach (AssetFormat format in AssetFormats.All.Where(f => f.Kind is AssetFormKind.Authored or AssetFormKind.Manifest or AssetFormKind.Sidecar))
        {
            Assert.False(string.IsNullOrEmpty(format.RootElement), format.Extension);
            Assert.False(string.IsNullOrEmpty(format.SchemaResource), format.Extension);
        }

        Assert.Equal(["Scene", "Prefab"], AssetFormats.All.Where(f => f.Kind == AssetFormKind.Authored).Select(f => f.RootElement));
        Assert.Equal(2, AssetFormats.Scene.Version);
        Assert.Equal(1, AssetFormats.Prefab.Version);

        // The schemas that belong to this assembly are embedded where the rows say.
        foreach (AssetFormat format in new[] { AssetFormats.Sidecar, AssetFormats.Game, AssetFormats.Mod })
        {
            using System.IO.Stream? stream = typeof(AssetFormats).Assembly.GetManifestResourceStream(format.SchemaResource!);
            Assert.NotNull(stream);
        }
    }

    [Fact]
    public void OneExtensionIsOneFormat()
    {
        string[] extensions = [.. AssetFormats.All.Select(f => f.Extension).Where(e => e.Length > 0)];
        Assert.Equal(extensions.Length, extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(extensions, e => Assert.Equal(e.ToLowerInvariant(), e));
        Assert.All(extensions, e => Assert.StartsWith(".", e, StringComparison.Ordinal));

        // Only cooked XML has no extension.
        Assert.Equal([AssetFormats.CookedXml], AssetFormats.All.Where(f => f.Extension.Length == 0));
    }

    [Fact]
    public void LookupIsByTheLastExtensionAndNotCaseSensitive()
    {
        Assert.Same(AssetFormats.Scene, AssetFormats.Find("res://scenes/Start.WSCN"));
        Assert.Same(AssetFormats.Sidecar, AssetFormats.Find("res://models/car.glb.wimp"));
        Assert.Same(AssetFormats.Model, AssetFormats.Find(@"C:\game\imported\models\car.glb.wmdl"));
        Assert.Same(AssetFormats.Glb, AssetFormats.Find("CAR.GLB"));
        Assert.Null(AssetFormats.Find("res://models/car"));
        Assert.Null(AssetFormats.Find("res://models/.glb")); // a name that only starts with a dot has no extension
        Assert.Null(AssetFormats.Find("res://dir.glb/car"));
        Assert.Null(AssetFormats.Find("res://scenes/start.ascene")); // the names before the w-prefix are nothing now
        Assert.Null(AssetFormats.FindExtension(".nothing"));
        Assert.Null(AssetFormats.FindExtension(""));
        Assert.True(AssetFormats.Scene.Matches("a/B.WsCn"));
        Assert.False(AssetFormats.CookedXml.Matches("a/b.wscn"));
    }

    [Fact]
    public void MagicsAreUniqueAndFoundInTheirBytes()
    {
        uint[] magics = [.. AssetFormats.All.Where(f => f.Magic != 0).Select(f => f.Magic)];
        Assert.Equal(magics.Length, magics.Distinct().Count());
        Assert.Equal(["WPAK", "WMDL", "WSHD", "WXMB"], AssetFormats.All.Where(f => f.Kind is AssetFormKind.Pack or AssetFormKind.Cooked || f == AssetFormats.Model || f == AssetFormats.Shader).Select(f => Ascii(f.Magic)));

        Assert.Same(AssetFormats.CookedXml, AssetFormats.FindMagic("WXMB...."u8));
        Assert.Same(AssetFormats.Pack, AssetFormats.FindMagic("WPAK"u8));
        Assert.Same(AssetFormats.Ktx2, AssetFormats.FindMagic(Ktx2Loader.Magic));
        Assert.Null(AssetFormats.FindMagic("WXM"u8));
        Assert.Null(AssetFormats.FindMagic("<Scene version=\"1\"/>"u8));
    }

    [Fact]
    public void TheCodeThatWritesAFormatUsesItsRow()
    {
        // XML cooked by XmlBinary starts with the row's magic and its version.
        byte[] cooked = XmlBinary.Write(XmlNode.Parse("<Scene version=\"1\"/>"));
        Assert.Equal(AssetFormats.CookedXml.Magic, BinaryPrimitives.ReadUInt32LittleEndian(cooked));
        Assert.Equal((ushort)AssetFormats.CookedXml.Version, BinaryPrimitives.ReadUInt16LittleEndian(cooked.AsSpan(4)));
        Assert.Equal(XmlBinary.Version, AssetFormats.CookedXml.Version);
        Assert.True(XmlBinary.IsBinary(cooked));

        Assert.Equal(AssetFormats.Model.Magic, ModelFile.Magic);
        Assert.Equal(ModelFile.Version, (uint)AssetFormats.Model.Version);

        Assert.Equal(AssetFormats.Pack.Magic, PackFormat.PlainMagic);
        Assert.Equal(AssetFormats.Pack.Magic, PackFormat.MagicFor(null));
        Assert.Equal(PackFormat.Version, (uint)AssetFormats.Pack.Version);

        // KTX 2 keeps its 12-byte identifier; the row has the first four bytes of it.
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(Ktx2Loader.Magic), AssetFormats.Ktx2.Magic);

        // The importers make the artifacts the rows name, from the sources the rows name.
        Assert.Equal(AssetFormats.ExtensionsOfType("model"), new ModelImporter().Extensions);
        Assert.Equal(AssetFormats.ExtensionsOfType("texture"), new ImageImporter().Extensions);
        Assert.Equal([".glb", ".gltf"], new ModelImporter().Extensions);
        Assert.Equal([".png", ".jpg", ".jpeg"], new ImageImporter().Extensions);
        Assert.Equal(AssetFormats.Sidecar.Extension, ImportSidecar.Suffix);
    }

    [Fact]
    public void AuthoredAndImportedAreListedFromTheTable()
    {
        Assert.Equal([".wscn", ".wprf"], AssetFormats.ExtensionsOf(AssetFormKind.Authored));
        Assert.Equal(AuthoredAssets.Extensions, AssetFormats.ExtensionsOf(AssetFormKind.Authored));
        Assert.True(AssetFormats.IsAuthored("res://a/b.WPRF"));
        Assert.False(AssetFormats.IsAuthored("res://a/b.wimp"));
        Assert.False(AssetFormats.IsAuthored("res://a/b.xml"));
        Assert.Equal([AssetFormats.Model, AssetFormats.Shader, AssetFormats.Ktx2], AssetFormats.ImportedArtifacts);
        Assert.Equal([".wscn"], AssetFormats.ExtensionsOfType("scene"));
        Assert.Equal([".slang"], AssetFormats.ExtensionsOfType("shader"));
        Assert.Empty(AssetFormats.ExtensionsOfType("nobody"));

        // Authored XML is what a cooked pack cooks; nothing else is.
        Assert.Equal(AssetFormats.All.Where(f => f.Kind == AssetFormKind.Authored), AssetFormats.All.Where(f => f.Cook == CookRule.Xml));
    }

    [Fact]
    public void RegisteringARowChecksItAndKeepsOneExtensionToOneFormat()
    {
        Assert.Throws<ArgumentException>(() => AssetFormats.Register(new AssetFormat("fmttest", AssetFormKind.Plain, "No dot")));
        Assert.Throws<ArgumentException>(() => AssetFormats.Register(new AssetFormat(".FmtTest", AssetFormKind.Plain, "Upper")));
        Assert.Throws<ArgumentException>(() => AssetFormats.Register(new AssetFormat(".", AssetFormKind.Plain, "Bare")));
        Assert.Throws<ArgumentException>(() => AssetFormats.Register(new AssetFormat(".a.b", AssetFormKind.Plain, "Two dots")));
        Assert.Throws<ArgumentException>(() => AssetFormats.Register(new AssetFormat(".fmttest", AssetFormKind.Plain, "")));
        InvalidOperationException same = Assert.Throws<InvalidOperationException>(() => AssetFormats.Register(new AssetFormat(".wscn", AssetFormKind.Plain, "Again")));
        Assert.Contains(".wscn", same.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => AssetFormats.Register(new AssetFormat(".fmttest", AssetFormKind.Plain, "Same magic", Magic: AssetFormats.Pack.Magic)));
        Assert.Null(AssetFormats.FindExtension(".fmttest")); // nothing of the refused rows was kept

        AssetFormat added = AssetFormats.Register(new AssetFormat(".fmttest", AssetFormKind.Plain, "Test format", Magic: 0x54464D54));
        Assert.Same(added, AssetFormats.FindExtension(".FMTTEST"));
        Assert.Same(added, AssetFormats.FindMagic("TMFT"u8));
        Assert.Throws<InvalidOperationException>(() => AssetFormats.Register(new AssetFormat(".fmttest", AssetFormKind.Plain, "Twice")));
    }

    [Fact]
    public void AnAuthoredDocumentIsReadFromTextOrCookedAndChecked()
    {
        AssetUid uid = AssetUid.New();
        string text = $"<Scene version=\"2\" uid=\"{uid}\"><Entity id=\"1\"/></Scene>";

        AuthoredDocument fromText = AuthoredDocument.Read(Encoding.UTF8.GetBytes(text), AssetFormats.Scene, "res://a.wscn");
        Assert.Equal("Scene", fromText.Root.Name);
        Assert.Equal(2, fromText.Version);
        Assert.Equal(uid, fromText.Uid);

        AuthoredDocument fromBom = AuthoredDocument.Read([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)], AssetFormats.Scene, "res://a.wscn");
        Assert.Equal(uid, fromBom.Uid);

        AuthoredDocument cooked = AuthoredDocument.Read(XmlBinary.Write(XmlNode.Parse(text)), AssetFormats.Scene, "res://a.wscn");
        Assert.Equal(uid, cooked.Uid);
        Assert.Equal(2, cooked.Version);
        Assert.Equal(AuthoredAssets.ReadUid(Encoding.UTF8.GetBytes(text)), cooked.Uid);

        // A missing version is accepted (0); a missing or invalid UID is none.
        AuthoredDocument bare = AuthoredDocument.Read("<Prefab><Entity id=\"1\"/></Prefab>"u8, AssetFormats.Prefab, "p.wprf");
        Assert.Equal(0, bare.Version);
        Assert.Equal(AssetUid.None, bare.Uid);
        Assert.Equal(AssetUid.None, AuthoredDocument.Read("<Prefab uid=\"nonsense\"/>"u8, AssetFormats.Prefab, "p.wprf").Uid);
    }

    [Fact]
    public void AnAuthoredDocumentThatIsNotOfTheFormatIsRefusedWithWhatIsWrong()
    {
        FormatException root = Assert.Throws<FormatException>(() => AuthoredDocument.Read("<Level/>"u8, AssetFormats.Scene, "res://a.wscn"));
        Assert.Equal("res://a.wscn is not a scene: its root is <Level>, not <Scene>", root.Message);
        Assert.Equal("the document is not a prefab: its root is <Scene>, not <Prefab>", Assert.Throws<FormatException>(() => AuthoredDocument.Check(XmlNode.Parse("<Scene/>"), AssetFormats.Prefab)).Message);

        Assert.Equal("the scene res://a.wscn has the version 'two', which is not a number", Assert.Throws<FormatException>(() => AuthoredDocument.Read("<Scene version=\"two\"/>"u8, AssetFormats.Scene, "res://a.wscn")).Message);
        Assert.Equal("the scene res://a.wscn is version 3; this engine reads up to 2", Assert.Throws<FormatException>(() => AuthoredDocument.Read("<Scene version=\"3\"/>"u8, AssetFormats.Scene, "res://a.wscn")).Message);
        Assert.Equal("the prefab is version 2; this engine reads up to 1", Assert.Throws<FormatException>(() => AuthoredDocument.Check(XmlNode.Parse("<Prefab version=\"2\"/>"), AssetFormats.Prefab)).Message);

        Assert.Throws<XmlParseException>(() => AuthoredDocument.Read("not xml {"u8, AssetFormats.Scene, "res://a.wscn"));
        byte[] damaged = XmlBinary.Write(XmlNode.Parse("<Scene version=\"1\"/>"))[..10];
        Assert.Throws<ContentException>(() => AuthoredDocument.Read(damaged, AssetFormats.Scene, "res://a.wscn"));

        // Only an XML format has a root to check.
        Assert.Throws<ArgumentException>(() => AuthoredDocument.Check(XmlNode.Parse("<Scene/>"), AssetFormats.Model));
    }

    [Fact]
    public void AnAuthoredDocumentIsCheckedAgainstASchemaWhenOneIsGiven()
    {
        var schema = new SchemaSet();
        schema.LoadText("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Scene">
                <xs:complexType>
                  <xs:attribute name="version" type="xs:int" use="required"/>
                  <xs:attribute name="uid" type="xs:string"/>
                </xs:complexType>
              </xs:element>
            </xs:schema>
            """, "test.xsd");

        Assert.Equal(1, AuthoredDocument.Read("<Scene version=\"1\"/>"u8, AssetFormats.Scene, "res://a.wscn", schema).Version);
        FormatException problem = Assert.Throws<FormatException>(() => AuthoredDocument.Read("<Scene version=\"1\" colour=\"red\"/>"u8, AssetFormats.Scene, "res://a.wscn", schema));
        Assert.Contains("does not match its schema", problem.Message, StringComparison.Ordinal);
        Assert.Contains("colour", problem.Message, StringComparison.Ordinal);
    }
}
