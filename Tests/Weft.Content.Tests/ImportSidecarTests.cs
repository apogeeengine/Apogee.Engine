using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Content.Import;
using Weft.Core.Assets;
using Xunit;

namespace Weft.Content.Tests;

/// <summary>Version 2 of the import sidecar: the hash of the source, sub-asset UIDs, remaps, sockets, the elements that are kept and not used, and what is refused.</summary>
public class ImportSidecarTests
{
    /// <summary>Takes <c>.sub</c> files, one sub-asset name per line, and asks for a UID for each. The artifact is the text in capitals.</summary>
    private sealed class SubImporter : IAssetImporter
    {
        public string Name => "sub";

        public IReadOnlyList<string> Extensions { get; } = [".sub"];

        public string Fingerprint => "1";

        public ImportResult? Import(ImportContext context)
        {
            string text = Encoding.UTF8.GetString(context.Bytes);
            foreach (string name in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                context.SubAsset(name.Trim());
            }

            return new ImportResult(Encoding.UTF8.GetBytes(text.ToUpperInvariant() + context.Settings.Srgb), ".up");
        }
    }

    private sealed class Project : IDisposable
    {
        private readonly TempFolder _folder = new();

        public Project()
        {
            Files = new VirtualFileSystem();
            Files.Mount("res://", new DirectorySource(_folder.Root, writable: true));
        }

        public VirtualFileSystem Files { get; }

        public void Write(string path, string text)
        {
            string full = Path.Combine(_folder.Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        public string Read(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_folder.Root, path)));

        public ImportReport Import(bool force = false) => new AssetImporter(Files, [new SubImporter()]).Run(force);

        public ImportSettings Sidecar(string asset) => ImportSettings.Parse(Read(asset + ".wimp").TrimStart('\uFEFF'), asset + ".wimp");

        public AssetStatus Status(string asset) => new AssetImporter(Files, [new SubImporter()]).StatusOf(asset);

        public void Dispose() => _folder.Dispose();
    }

    private static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private const string FullSidecar = """
        <?xml version="1.0" encoding="utf-8"?>
        <!-- written by hand -->
        <Import version="2" uid="uid://4k2m9x7q1w3ze" sourceHash="0000000000000000000000000000000000000000000000000000000000000000" srgb="false">
          <SubAsset name="material/paint" uid="uid://0123456789abc"/>
          <SubAsset name="texture/albedo" uid="uid://0123456789abd"/>
          <MaterialRemap slot="paint" ref="materials/paint.wmat"/>
          <Socket name="hand" node="arm.R" position="0 0.1 -2" rotation="0 0 0 1"/>
          <Clip name="walk" loop="true" start="0" end="1.5"/>
          <Lod screenSize="0.25" reduction="0.5"/>
          <Collision shape="convex"/>
          <Variant><Define name="A"/></Variant>
        </Import>
        """;

    [Fact]
    public void AVersion2SidecarIsReadInFull()
    {
        ImportSettings settings = ImportSettings.Parse(FullSidecar, "x.wimp");

        Assert.Equal(2, settings.Version);
        Assert.Equal("uid://4k2m9x7q1w3ze", settings.Uid.ToString());
        Assert.Equal(new string('0', 64), settings.SourceHash);
        Assert.Equal(["material/paint", "texture/albedo"], settings.SubAssets.Select(s => s.Name));
        Assert.Equal("uid://0123456789abd", settings.SubAssets[1].Uid.ToString());
        Assert.Equal([new MaterialRemap("paint", "materials/paint.wmat")], settings.MaterialRemaps);
        Assert.Equal([new ImportSocket("hand", "arm.R", new Vector3(0, 0.1f, -2), Quaternion.Identity)], settings.Sockets);
        Assert.Equal([new ImportClip("walk", true, 0, 1.5f)], settings.Clips);
        Assert.Equal([new ImportLod(0.25f, 0.5f)], settings.Lods);
        Assert.Equal(CollisionShape.Convex, settings.Collision);
        Assert.False(settings.Srgb);
        Assert.Single(settings.Variants);
    }

    [Fact]
    public void ASidecarWithoutAVersionIsVersion1AndSaysNothingNew()
    {
        ImportSettings settings = ImportSettings.Parse("<Import srgb=\"false\"/>", "x.wimp");

        Assert.Equal(1, settings.Version);
        Assert.Equal("", settings.SourceHash);
        Assert.Empty(settings.SubAssets);
        Assert.Empty(settings.Sockets);
        Assert.Null(settings.Collision);
        Assert.Equal(new ImportSettings { Srgb = false }, settings);
    }

    [Fact]
    public void SocketsTakeTheirDefaults()
    {
        ImportSettings settings = ImportSettings.Parse("<Import><Socket name=\"muzzle\"/></Import>", "x.wimp");

        Assert.Equal([new ImportSocket("muzzle", "", Vector3.Zero, Quaternion.Identity)], settings.Sockets);
    }

    [Fact]
    public void RefreshingAFullSidecarKeepsEveryOtherByte()
    {
        string hash = HashOf("source");
        string refreshed = ImportSidecar.Refresh(FullSidecar, hash, [new ImportSubAsset("texture/normal", AssetUid.New())]);

        // Only the hash changed, and one element was added after the last one before the end tag, on its own line, indented like the others.
        Assert.Equal(FullSidecar.Replace(new string('0', 64), hash, StringComparison.Ordinal).Replace("  <Variant><Define name=\"A\"/></Variant>\n", "  <Variant><Define name=\"A\"/></Variant>\n  <SubAsset name=\"texture/normal\" uid=\"" + ImportSettings.Parse(refreshed, "x").SubAssets[2].Uid + "\"/>\n", StringComparison.Ordinal), refreshed.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(ImportSettings.KeyText(FullSidecar), ImportSettings.KeyText(refreshed));
        Assert.Equal(ImportSettings.Parse(FullSidecar, "x").SubAssets, ImportSettings.Parse(refreshed, "x").SubAssets.Take(2));
    }

    [Fact]
    public void ElementsTheConsumersDoNotUseAreKeptByteForByteThroughImports()
    {
        using var project = new Project();
        string sidecar = "\uFEFF<?xml version=\"1.0\"?>\r\n<!-- mine, keep it -->\r\n<Import   srgb='false'>\r\n\t<Clip   name=\"walk\"  loop=\"true\"/>\r\n  <!-- lods -->\r\n  <Lod screenSize=\"0.5\"  reduction=\"0.5\"/>\r\n\t<Collision shape=\"mesh\"   />\r\n</Import>\r\n";
        project.Write("a.sub", "material/a\ntexture/b\n");
        project.Write("a.sub.wimp", sidecar);

        project.Import();
        string first = project.Read("a.sub.wimp");
        project.Write("a.sub", "material/a\ntexture/b\nmaterial/c\n");
        project.Import();
        string second = project.Read("a.sub.wimp");

        foreach (string kept in new[] { "<!-- mine, keep it -->\r\n", "\t<Clip   name=\"walk\"  loop=\"true\"/>\r\n  <!-- lods -->\r\n  <Lod screenSize=\"0.5\"  reduction=\"0.5\"/>\r\n\t<Collision shape=\"mesh\"   />\r\n" })
        {
            Assert.Contains(kept, first, StringComparison.Ordinal);
            Assert.Contains(kept, second, StringComparison.Ordinal);
        }

        Assert.StartsWith("\uFEFF<?xml version=\"1.0\"?>\r\n<!-- mine, keep it -->\r\n<Import   srgb='false' uid=\"", first, StringComparison.Ordinal);
        Assert.Contains("\r\n", first, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", first.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        ImportSettings settings = project.Sidecar("a.sub");
        Assert.Equal(3, settings.SubAssets.Count);
        Assert.Equal([new ImportClip("walk", true, null, null)], settings.Clips);
        Assert.Equal(CollisionShape.Mesh, settings.Collision);
        Assert.Equal(ImportSettings.KeyText(first), ImportSettings.KeyText(second));
    }

    [Fact]
    public void SubAssetUidsSurviveReimport()
    {
        using var project = new Project();
        project.Write("a.sub", "material/a\ntexture/b\n");
        project.Import();
        Dictionary<string, AssetUid> first = project.Sidecar("a.sub").SubAssets.ToDictionary(s => s.Name, s => s.Uid);

        Assert.Equal(["material/a", "texture/b"], first.Keys.Order());
        Assert.Equal(first.Count, first.Values.Distinct().Count());
        Assert.All(first.Values, uid => Assert.False(uid.IsNone));

        project.Import(force: true);
        project.Write("a.sub", "texture/b\nmaterial/a\nmaterial/new\n"); // reordered, one more
        project.Import();
        new AssetImporter(project.Files, [new SubImporter()]).Reimport("a.sub");

        Dictionary<string, AssetUid> later = project.Sidecar("a.sub").SubAssets.ToDictionary(s => s.Name, s => s.Uid);
        Assert.Equal(3, later.Count);
        Assert.Equal(first["material/a"], later["material/a"]);
        Assert.Equal(first["texture/b"], later["texture/b"]);
        Assert.DoesNotContain(later["material/new"], first.Values);
    }

    [Fact]
    public void ASubAssetThatIsNotAskedForAnyMoreKeepsItsUid()
    {
        using var project = new Project();
        project.Write("a.sub", "material/a\ntexture/b\n");
        project.Import();
        AssetUid b = project.Sidecar("a.sub").SubAssets.Single(s => s.Name == "texture/b").Uid;

        project.Write("a.sub", "material/a\n");
        project.Import();
        project.Write("a.sub", "material/a\ntexture/b\n");
        project.Import();

        Assert.Equal(b, project.Sidecar("a.sub").SubAssets.Single(s => s.Name == "texture/b").Uid);
    }

    [Fact]
    public void SourceHashChangesWithTheSource()
    {
        using var project = new Project();
        project.Write("a.sub", "one\n");
        project.Import();
        string first = project.Sidecar("a.sub").SourceHash;
        AssetUid uid = project.Sidecar("a.sub").Uid;

        Assert.Equal(HashOf("one\n"), first);
        Assert.Matches("^[0-9a-f]{64}$", first);

        project.Write("a.sub", "two\n");
        Assert.Equal(AssetStatus.Stale, project.Status("a.sub"));
        project.Import();

        Assert.Equal(HashOf("two\n"), project.Sidecar("a.sub").SourceHash);
        Assert.NotEqual(first, project.Sidecar("a.sub").SourceHash);
        Assert.Equal(uid, project.Sidecar("a.sub").Uid);
        Assert.Equal(AssetStatus.UpToDate, project.Status("a.sub"));
    }

    [Fact]
    public void AStaleHandWrittenHashIsRefreshedWithoutImportingAgain()
    {
        using var project = new Project();
        project.Write("a.sub", "one\n");
        project.Import();
        string text = project.Read("a.sub.wimp");
        project.Write("a.sub.wimp", text.Replace(HashOf("one\n"), new string('f', 64), StringComparison.Ordinal));

        ImportReport report = project.Import();

        Assert.Empty(report.Imported);
        Assert.Equal(HashOf("one\n"), project.Sidecar("a.sub").SourceHash);
    }

    [Theory]
    [InlineData("<Import srgb=\"false\"/>")]
    [InlineData("<!-- c -->\n<Import mips=\"false\" >\n</Import>\n")]
    [InlineData("<Import uid=\"uid://0123456789abc\"/>")]
    public void AVersion1SidecarBecomesVersion2OnTheNextImportAndKeepsItsUidAndSettings(string v1)
    {
        using var project = new Project();
        project.Write("a.sub", "material/a\n");
        project.Write("a.sub.wimp", v1);
        ImportSettings before = ImportSettings.Parse(v1, "x");
        project.Import();
        project.Write("a.sub.wimp", v1 + "\n"); // as a project from before version 2 left it (the file is the one it was; only a newline is added so that it is a change)
        project.Write("a.sub", "material/a\nmaterial/b\n");

        ImportReport report = project.Import();

        ImportSettings after = project.Sidecar("a.sub");
        Assert.Equal(["a.sub"], report.Imported);
        Assert.Equal(2, after.Version);
        Assert.Equal((before.Srgb, before.Mips), (after.Srgb, after.Mips));
        if (!before.Uid.IsNone)
        {
            Assert.Equal(before.Uid, after.Uid);
        }

        Assert.Equal(HashOf("material/a\nmaterial/b\n"), after.SourceHash);
        Assert.Empty(project.Import().Imported); // what was written is not what the key is made of
    }

    [Fact]
    public void ARealVersion1SidecarIsRewrittenWithoutImportingTheAssetAgain()
    {
        using var project = new Project();
        project.Write("a.sub", "\n"); // asks for no sub-asset, so the sidecar keeps the shape version 1 gave it
        project.Import();
        AssetUid uid = project.Sidecar("a.sub").Uid;
        project.Write("a.sub.wimp", $"<Import uid=\"{uid}\" srgb=\"false\"/>"); // version 1 as it was written before: a setting changed, so one import
        project.Import();
        project.Write("a.sub.wimp", $"<Import srgb=\"false\" uid=\"{uid}\"/>"); // and the text of version 1 again, with no version, no hash and no sub-assets

        ImportReport report = project.Import();

        Assert.Empty(report.Imported);
        Assert.Equal(["a.sub"], report.UpToDate);
        ImportSettings settings = project.Sidecar("a.sub");
        Assert.Equal((2, uid, HashOf("\n")), (settings.Version, settings.Uid, settings.SourceHash));
        Assert.Contains("srgb=\"false\" uid=\"" + uid + "\" version=\"2\" sourceHash=\"", project.Read("a.sub.wimp"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnImportKeyDoesNotSeeWhatTheImporterWrites()
    {
        string plain = "<Import srgb=\"false\"/>";
        string uid = "uid://0123456789abc";
        string written = ImportSidecar.Refresh(ImportSidecar.SetUid(plain, AssetUid.TryParse(uid, out AssetUid parsed) ? parsed : AssetUid.None), HashOf("x"), [new ImportSubAsset("material/a", AssetUid.New())]);

        Assert.NotEqual(plain, written);
        Assert.Equal("<Import srgb=\"false\">\n</Import>", ImportSettings.KeyText(written)); // stable: this is what every later key is made from
        Assert.Equal("", ImportSettings.KeyText(ImportSidecar.Refresh(ImportSidecar.SetUid("", parsed), HashOf("x"), [new ImportSubAsset("material/a", AssetUid.New())])));
        Assert.Equal("", ImportSettings.KeyText($"<Import version=\"2\" uid=\"{uid}\" sourceHash=\"{HashOf("x")}\"/>"));
    }

    [Fact]
    public void ACopyOfAnAssetAndItsSidecarSharesNoUidsWithTheOriginal()
    {
        using var project = new Project();
        project.Write("a.sub", "material/a\ntexture/b\n");
        project.Import();
        project.Write("b.sub", "material/a\ntexture/b\n");
        project.Write("b.sub.wimp", project.Read("a.sub.wimp"));

        ImportReport report = project.Import();

        ImportSettings a = project.Sidecar("a.sub");
        ImportSettings b = project.Sidecar("b.sub");
        Assert.Single(report.Reassigned);
        Assert.NotEqual(a.Uid, b.Uid);
        Assert.Equal(a.SubAssets.Select(s => s.Name), b.SubAssets.Select(s => s.Name));
        Assert.Empty(a.SubAssets.Select(s => s.Uid).Intersect(b.SubAssets.Select(s => s.Uid)));
        Assert.Empty(project.Import().Imported);
    }

    [Fact]
    public void AModelsMaterialsAndTexturesGetNamedSubAssetsThatStayTheSame()
    {
        using var project = new Project();
        var builder = new GltfBuilder
        {
            Images = "[{\"uri\":\"" + GltfBuilder.PngUri(8, 4, (x, y) => ((byte)(x * 30), (byte)(y * 60), 128, 255)) + "\",\"name\":\"albedo\"}]",
            Textures = "[{\"source\":0}]",
            Materials = "[{\"name\":\"paint\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}},{\"name\":\"paint\"},{}]",
            Nodes = "[{\"mesh\":0},{\"mesh\":1},{\"mesh\":2}]",
            Scenes = "[{\"nodes\":[0,1,2]}]",
        };
        builder.AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2], material: 0).AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2], material: 1).AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2], material: 2);
        project.Write("m.gltf", Encoding.UTF8.GetString(builder.ToBytes()));
        var importer = new AssetImporter(project.Files, [new ModelImporter()]);

        importer.Run();
        byte[] artifact = project.Files.ReadAllBytes("res://imported/m.gltf.wmdl");
        ImportSettings first = ImportSettings.Parse(project.Files.ReadAllText("res://m.gltf.wimp"), "m");
        importer.Run(force: true);

        ImportSettings second = ImportSettings.Parse(project.Files.ReadAllText("res://m.gltf.wimp"), "m");
        Assert.Equal(["material/2", "material/paint", "material/paint#1", "texture/albedo"], first.SubAssets.Select(s => s.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(first.SubAssets, second.SubAssets);
        Assert.Equal(artifact, project.Files.ReadAllBytes("res://imported/m.gltf.wmdl")); // the model's file does not depend on any of it
    }

    // ---- Hostile sidecars --------------------------------------------------------------------------------------------------

    private static string Error(string sidecar) => Assert.Throws<ContentException>(() => ImportSettings.Parse(sidecar, "evil.wimp")).Message;

    [Fact]
    public void AHugeSidecarIsRefusedBeforeItIsParsed()
    {
        string huge = "<Import>" + string.Concat(Enumerable.Repeat("<SubAsset name=\"a\" uid=\"uid://0123456789abc\"/>", 30_000)) + "</Import>";

        Assert.Contains("more than the 1048576 allowed", Error(huge), StringComparison.Ordinal);
        Assert.Equal(huge, ImportSettings.KeyText(huge)); // not parsed for the key either
    }

    [Fact]
    public void TooManySubAssetsAreRefused()
    {
        string many = "<Import>" + string.Concat(Enumerable.Range(0, 4097).Select(i => $"<SubAsset name=\"n{i}\" uid=\"{AssetUid.New()}\"/>")) + "</Import>";

        Assert.Contains("at most 4096", Error(many), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateNamesAreRefused()
    {
        string uid1 = AssetUid.New().ToString();
        string uid2 = AssetUid.New().ToString();

        Assert.Contains("the sub-asset name 'material/a' is used twice", Error($"<Import><SubAsset name=\"material/a\" uid=\"{uid1}\"/><SubAsset name=\"material/a\" uid=\"{uid2}\"/></Import>"), StringComparison.Ordinal);
        Assert.Contains($"the UID '{uid1}' is used twice", Error($"<Import><SubAsset name=\"a\" uid=\"{uid1}\"/><SubAsset name=\"b\" uid=\"{uid1}\"/></Import>"), StringComparison.Ordinal);
        Assert.Contains($"the UID '{uid1}' is used twice", Error($"<Import uid=\"{uid1}\"><SubAsset name=\"a\" uid=\"{uid1}\"/></Import>"), StringComparison.Ordinal);
        Assert.Contains("the material slot 'p' is used twice", Error("<Import><MaterialRemap slot=\"p\" ref=\"a.wmat\"/><MaterialRemap slot=\"p\" ref=\"b.wmat\"/></Import>"), StringComparison.Ordinal);
        Assert.Contains("the socket name 'hand' is used twice", Error("<Import><Socket name=\"hand\"/><Socket name=\"hand\"/></Import>"), StringComparison.Ordinal);
        Assert.Contains("the clip name 'walk' is used twice", Error("<Import><Clip name=\"walk\"/><Clip name=\"walk\"/></Import>"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<Import><SubAsset name=\"a\" uid=\"not-a-uid\"/></Import>", "does not match the pattern")]
    [InlineData("<Import><SubAsset name=\"a\" uid=\"uid://0123456789abc0\"/></Import>", "does not match the pattern")]
    [InlineData("<Import><SubAsset name=\"a\"/></Import>", "needs the attribute 'uid'")]
    [InlineData("<Import><SubAsset uid=\"uid://0123456789abc\"/></Import>", "needs the attribute 'name'")]
    [InlineData("<Import uid=\"uid://ILLEGAL\"/>", "does not match the pattern")]
    [InlineData("<Import sourceHash=\"ABCDEF\"/>", "does not match the pattern")]
    [InlineData("<Import version=\"0\"/>", "minimum")]
    [InlineData("<Import version=\"two\"/>", "not a whole number")]
    [InlineData("<Import><Collision shape=\"sphere\"/></Import>", "is not one of")]
    [InlineData("<Import><Lod screenSize=\"2\" reduction=\"0.5\"/></Import>", "maximum")]
    [InlineData("<Import><Socket name=\"s\" rotation=\"0 0 1\"/></Import>", "items")]
    [InlineData("<Import><Socket name=\"s\" rotation=\"0 0 0 0\"/></Import>", "not one")]
    [InlineData("<Import><Socket name=\"s\" position=\"1 2\"/></Import>", "not a vector")]
    [InlineData("<Import><Mystery/></Import>", "is not allowed in <Import>")]
    [InlineData("<Import><SubAsset name=\" a\" uid=\"uid://0123456789abc\"/></Import>", "white space")]
    public void HostileValuesAreRefusedWithAClearError(string sidecar, string expected)
    {
        string message = Error(sidecar);

        Assert.Contains("evil.wimp", message, StringComparison.Ordinal);
        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASidecarFromANewerEngineIsRefused()
    {
        Assert.Contains("version 3, and this engine reads up to version 2", Error("<Import version=\"3\"/>"), StringComparison.Ordinal);
    }

    [Fact]
    public void AHostileSidecarFailsItsAssetAndLeavesTheFileAlone()
    {
        using var project = new Project();
        string evil = "<Import><SubAsset name=\"a\" uid=\"nope\"/></Import>";
        project.Write("a.sub", "material/a\n");
        project.Write("a.sub.wimp", evil);

        ImportReport report = project.Import();

        Assert.False(report.Success);
        Assert.Contains("does not match the pattern", report.Failed.Single().Message, StringComparison.Ordinal);
        Assert.Equal(evil, project.Read("a.sub.wimp"));
    }

    [Fact]
    public void TheTextEditsRefuseDocumentsTheyCannotEdit()
    {
        Assert.Throws<ContentException>(() => ImportSidecar.Refresh("<Other/>", HashOf("x"), []));
        Assert.Throws<ContentException>(() => ImportSidecar.Refresh("not xml", HashOf("x"), []));
        Assert.Throws<ContentException>(() => ImportSidecar.Refresh("<Import><Clip name=\"a\"></Import>", HashOf("x"), [new ImportSubAsset("a", AssetUid.New())]));
    }

    [Fact]
    public void ANameWithMarkupInItIsEscapedAndComesBack()
    {
        string text = ImportSidecar.Refresh("<Import/>", HashOf("x"), [new ImportSubAsset("material/a\"<&>b", AssetUid.New())]);

        Assert.Equal("material/a\"<&>b", ImportSettings.Parse(text, "x").SubAssets.Single().Name);
    }
}
