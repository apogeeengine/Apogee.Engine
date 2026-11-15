using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Apogee.Content.Assets;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Content.Xml;
using Apogee.Core.Assets;
using Xunit;

namespace Apogee.Content.Tests;

/// <summary>Finding the places that name an asset, rewriting them on a rename, and <see cref="AssetDatabase.Move"/>, which does that and moves the files.</summary>
public class AssetReferencesTests
{
    /// <summary>Takes <c>.dat</c> files (a "model") and makes their text in capitals; no native library involved.</summary>
    private sealed class DatImporter : IAssetImporter
    {
        public string Name => "dat";

        public IReadOnlyList<string> Extensions { get; } = [".dat"];

        public string Fingerprint => "1";

        public ImportResult? Import(ImportContext context) => new(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(context.Bytes).ToUpperInvariant()), ".up");
    }

    /// <summary>Takes <c>.dep</c> files and reads <c>textures/t.dat</c> for each, as a glTF reads its images: that file becomes its dependency.</summary>
    private sealed class DepImporter : IAssetImporter
    {
        public string Name => "dep";

        public IReadOnlyList<string> Extensions { get; } = [".dep"];

        public string Fingerprint => "1";

        public ImportResult? Import(ImportContext context) => new(context.ReadFile("res://textures/t.dat"), ".up");
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

        public void WriteBytes(string path, byte[] bytes)
        {
            string full = Path.Combine(_folder.Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        public string Read(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_folder.Root, path))); // keeps a byte order mark

        public bool Has(string path) => File.Exists(Path.Combine(_folder.Root, path));

        public ImportReport Import() => new AssetImporter(Files, [new DatImporter(), new DepImporter()]).Run();

        /// <summary>Imports and returns the UID of <paramref name="asset"/> (a path relative to the project).</summary>
        public AssetUid Imported(string asset)
        {
            Import();
            return ImportSidecar.ReadUid(Read(asset + ".import.xml"));
        }

        public void Dispose() => _folder.Dispose();
    }

    private static string SceneOf(AssetUid uid, params string[] entities) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Scene version=\"1\" uid=\"{uid}\">\n{string.Join("", entities.Select(e => "  " + e + "\n"))}</Scene>\n";

    private static string Model(string reference) => $"<ModelRenderer Model=\"{reference}\"/>";

    // ---- Find ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void FindListsEverySceneAttributeThatNamesTheAsset()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        project.Write("models/bike.dat", "bike");
        AssetUid car = project.Imported("models/car.dat");
        AssetUid bike = ImportSidecar.ReadUid(project.Read("models/bike.dat.import.xml"));
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}"), Model($"res://models/bike.dat|{bike}"), "<Other Mesh=\"res://models/car.dat\" Spare=\"res://models/car.dat|" + car + "\"/>"));
        project.Write("scenes/b.ascene", SceneOf(AssetUid.New(), "<Entity id=\"1\">", "  " + Model("res://models/car.dat"), "</Entity>"));
        project.Write("data/units.xml", "<Units>\n  <Unit mesh=\"res://models/car.dat\"/>\n</Units>\n");
        project.Write("data/other.xml", "<Units><Unit mesh=\"res://models/bike.dat\"/></Units>");
        AssetRef target = project.Assets.RefOf("res://models/car.dat");

        IReadOnlyList<AssetReferenceSite> found = AssetReferences.Find(project.Assets, target, ["res://data"]);

        Assert.Equal(
            [
                ("res://data/units.xml", 2, "Unit", "mesh"),
                ("res://scenes/a.ascene", 3, "ModelRenderer", "Model"),
                ("res://scenes/a.ascene", 5, "Other", "Mesh"),
                ("res://scenes/a.ascene", 5, "Other", "Spare"),
                ("res://scenes/b.ascene", 4, "ModelRenderer", "Model"),
            ],
            found.Select(s => (s.Document, s.Line, s.Element, s.Attribute)).OrderBy(t => t.Item1, StringComparer.Ordinal).ThenBy(t => t.Item2).ThenBy(t => t.Item4, StringComparer.Ordinal).ToList());
        Assert.Equal(new AssetRef("res://models/car.dat", car), found.First(s => s.Attribute == "Spare").Value);
        Assert.Equal(new AssetRef("res://models/car.dat", AssetUid.None), found.First(s => s.Document.EndsWith("b.ascene", StringComparison.Ordinal)).Value);

        // The definition folders are only looked at when they are named.
        Assert.DoesNotContain(AssetReferences.Find(project.Assets, target), s => s.Document.StartsWith("res://data", StringComparison.Ordinal));
    }

    [Fact]
    public void FindMatchesByUidEvenWhenThePathIsStale()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        project.Write("models/copy.dat", "copy");
        AssetUid car = project.Imported("models/car.dat");
        project.Write("scenes/a.ascene", SceneOf(
            AssetUid.New(),
            Model($"res://long/gone/car.dat|{car}"), // the path is stale: the UID finds it
            Model(car.ToString()), // a UID alone
            Model($"res://models/copy.dat|{car}"), // the path leads to another file that exists, and the path wins
            Model("not a path at all|" + car)));
        AssetRef target = project.Assets.RefOf("res://models/car.dat");

        IReadOnlyList<AssetReferenceSite> found = AssetReferences.Find(project.Assets, target);

        Assert.Equal([3, 4, 6], found.Select(s => s.Line).Order());
        Assert.DoesNotContain(found, s => s.Value.Path == "res://models/copy.dat");

        // The same asset asked for by its UID alone, after it moved: the old path in the scene is just a hint.
        Assert.Equal(3, AssetReferences.Find(project.Assets, AssetRef.FromUid(car)).Count);
    }

    [Fact]
    public void TwoFilesWithTheSameUidOnlyTheOneTheEngineResolvesToOwnsAStaleReference()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid shared = project.Imported("models/car.dat");
        // A copy that was not imported yet: the same file and the same sidecar, so the UID is on two files, and the project's list says the original is the one the UID names.
        project.Write("models/copy.dat", "car");
        project.Write("models/copy.dat.import.xml", project.Read("models/car.dat.import.xml"));
        Assert.Equal("res://models/car.dat", project.Assets.PathOf(shared));
        string scene = SceneOf(
            AssetUid.New(),
            Model($"res://gone/car.dat|{shared}"), // stale: the engine resolves it to the original
            Model($"res://models/copy.dat|{shared}"), // the copy's own path
            Model($"res://models/car.dat|{shared}"));
        project.Write("scenes/a.ascene", scene);

        // Renaming the copy touches only the reference that spells its path.
        RewriteReport copy = AssetReferences.Rewrite(project.Assets, "res://models/copy.dat", "res://x/copy2.dat", dryRun: true);

        Assert.Equal([4], copy.Changes.Select(c => c.Site.Line));
        Assert.Equal(new AssetRef("res://x/copy2.dat", shared), Assert.Single(copy.Changes).NewValue);

        // Renaming the original takes the stale reference with it, and leaves the copy's.
        RewriteReport original = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://x/truck.dat");

        Assert.Equal([3, 5], original.Changes.Select(c => c.Site.Line).Order());
        string after = project.Read("scenes/a.ascene");
        Assert.Contains($"Model=\"res://x/truck.dat|{shared}\"", after, StringComparison.Ordinal);
        Assert.Contains($"Model=\"res://models/copy.dat|{shared}\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("res://gone/car.dat", after, StringComparison.Ordinal);
    }

    [Fact]
    public void FindIsEmptyForNothingToLookFor()
    {
        using var project = new Project();
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model("res://models/car.dat")));

        Assert.Empty(AssetReferences.Find(project.Assets, default));
    }

    // ---- Rewrite ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void RewriteKeepsCommentsFormattingAndTheUid()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        AssetUid scene = AssetUid.New();
        string before =
            "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<!-- res://models/car.dat is the car -->\r\n"
            + $"<Scene version=\"1\" uid=\"{scene}\">\r\n"
            + "\t<Entity id='1' name='car &amp; co'>\r\n"
            + $"\t\t<ModelRenderer   Model = 'res://models/car.dat|{car}'   Tint=\"1 1 1\"/>\r\n"
            + "\t\t<!-- <Fake Model=\"res://models/car.dat\"/> -->\r\n"
            + "\t\t<Note Text=\"a &gt; b, see res://models/car.dat.txt\"/>\r\n"
            + "\t</Entity>\r\n"
            + "</Scene>\r\n";
        project.Write("scenes/a.ascene", before);

        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://vehicles/truck.dat");

        string expected = before.Replace($"Model = 'res://models/car.dat|{car}'", $"Model = 'res://vehicles/truck.dat|{car}'", StringComparison.Ordinal);
        Assert.Equal(expected, project.Read("scenes/a.ascene")); // every other character is as it was, including the byte order mark and the CRLF
        Assert.Equal(["res://scenes/a.ascene"], report.Documents);
        Assert.True(report.Applied);
        ReferenceChange change = Assert.Single(report.Changes);
        Assert.Equal(new AssetRef("res://vehicles/truck.dat", car), change.NewValue);
        Assert.Equal(new AssetRef("res://models/car.dat", car), change.Site.Value);
    }

    [Fact]
    public void RewriteLeavesSimilarPathsAlone()
    {
        using var project = new Project();
        project.Write("models/a.dat", "a");
        AssetUid a = project.Imported("models/a.dat");
        AssetUid other = AssetUid.New();
        project.Write("scenes/s.ascene", SceneOf(
            AssetUid.New(),
            "<A p=\"res://models/a.dat\"/>",
            "<A p=\"res://models/a.dat.bak\"/>",
            "<A p=\"res://models/a.dat2\"/>",
            "<A p=\"res://models/sub/a.dat\"/>",
            "<A p=\"res://models/A.dat\"/>",
            "<A p=\"res://models\"/>",
            "<A p=\"models/a.dat\"/>",
            "<A p=\"res://models/a.dat|" + other + "\"/>",
            $"<A p=\"res://other/a.dat|{a}\"/>"));

        AssetReferences.Rewrite(project.Assets, "res://models/a.dat", "res://x/b.dat");

        Assert.Equal(
            SceneOf(
                ReadUid(project.Read("scenes/s.ascene")),
                "<A p=\"res://x/b.dat\"/>",
                "<A p=\"res://models/a.dat.bak\"/>",
                "<A p=\"res://models/a.dat2\"/>",
                "<A p=\"res://models/sub/a.dat\"/>",
                "<A p=\"res://models/A.dat\"/>",
                "<A p=\"res://models\"/>",
                "<A p=\"models/a.dat\"/>",
                "<A p=\"res://x/b.dat|" + other + "\"/>", // the path is the asset's, and the path wins: the UID in the reference is kept as it is
                $"<A p=\"res://x/b.dat|{a}\"/>"), // the UID is the asset's and its path leads nowhere
            project.Read("scenes/s.ascene"));
    }

    private static AssetUid ReadUid(string document) => AuthoredAssets.ReadUid(Encoding.UTF8.GetBytes(document));

    [Fact]
    public void OnlyThePathPartOfAReferenceChangesAndTheUidIsKeptCharacterForCharacter()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        string upper = car.ToString().ToUpperInvariant(); // "UID://4K2M…": the same UID, written the way another tool wrote it
        Assert.True(AssetRef.TryParse($"res://models/car.dat|{upper}", out AssetRef parsed) && parsed.Uid == car);
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model($"res://models/car.dat|{upper}"), Model($"res://models/car.dat|{car}")));

        AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://x/truck.dat");

        string after = project.Read("scenes/a.ascene");
        Assert.Contains($"Model=\"res://x/truck.dat|{upper}\"", after, StringComparison.Ordinal);
        Assert.Contains($"Model=\"res://x/truck.dat|{car}\"", after, StringComparison.Ordinal);
    }

    [Fact]
    public void AUidAloneAndAnUpToDateReferenceAreLeftAlone()
    {
        using var project = new Project();
        project.Write("models/a.dat", "a");
        AssetUid a = project.Imported("models/a.dat");
        string scene = SceneOf(AssetUid.New(), Model(a.ToString()), Model("res://x/b.dat"));
        project.Write("scenes/s.ascene", scene);

        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://models/a.dat", "res://x/b.dat");

        Assert.Equal(scene, project.Read("scenes/s.ascene"));
        Assert.Empty(report.Changes);
        Assert.Empty(report.Documents);
        Assert.Equal(2, report.Unchanged.Count);
    }

    [Fact]
    public void ValuesAreEscapedAndUnescapedAsXmlSays()
    {
        using var project = new Project();
        project.Write("models/a&b.dat", "a");
        AssetUid a = project.Imported("models/a&b.dat");
        project.Write("scenes/s.ascene", $"<Scene version=\"1\" uid=\"{AssetUid.New()}\"><A p=\"res://models/a&amp;b.dat|{a}\" q='res://models/a&#38;b.dat'/></Scene>");

        AssetReferences.Rewrite(project.Assets, "res://models/a&b.dat", "res://x/it's.dat");

        XmlNode after = XmlNode.Parse(project.Read("scenes/s.ascene"));
        Assert.Equal($"res://x/it's.dat|{a}", after.Children[0].Attribute("p"));
        Assert.Equal("res://x/it's.dat", after.Children[0].Attribute("q"));
        Assert.Contains("q='res://x/it&apos;s.dat'", project.Read("scenes/s.ascene"), StringComparison.Ordinal); // the quote that wraps the value is the one that is escaped
    }

    [Fact]
    public void AGroupOfDocumentsIsChangedTogetherAndAPreviewChangesNothing()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        string a = SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}"));
        string b = SceneOf(AssetUid.New(), Model("res://models/car.dat"));
        project.Write("scenes/a.ascene", a);
        project.Write("scenes/b.ascene", b);

        RewriteReport preview = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat", dryRun: true);

        Assert.False(preview.Applied);
        Assert.Equal(2, preview.Changes.Count);
        Assert.Equal(["res://scenes/a.ascene", "res://scenes/b.ascene"], preview.Documents.Order(StringComparer.Ordinal));
        Assert.Equal(a, project.Read("scenes/a.ascene"));
        Assert.Equal(b, project.Read("scenes/b.ascene"));

        RewriteReport done = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat");

        Assert.True(done.Applied);
        Assert.Equal(preview.Changes, done.Changes);
        Assert.Equal(a.Replace("car.dat", "truck.dat", StringComparison.Ordinal), project.Read("scenes/a.ascene"));
        Assert.Equal(b.Replace("car.dat", "truck.dat", StringComparison.Ordinal), project.Read("scenes/b.ascene"));
    }

    [Fact]
    public void RewriteRefusesRequestsThatMakeNoSense()
    {
        using var project = new Project();
        project.Write("models/a.dat", "a");
        project.Write("models/b.dat", "b");

        Assert.Throws<ContentException>(() => AssetReferences.Rewrite(project.Assets, "res://models/a.dat", "res://models/a.dat")); // the same
        Assert.Throws<ContentException>(() => AssetReferences.Rewrite(project.Assets, "res://models/a.dat", "res://models/b.dat")); // two assets would become one
        Assert.Throws<ContentException>(() => AssetReferences.Rewrite(project.Assets, "res://models/x.dat", "res://models/y.dat")); // neither exists
        Assert.Throws<ContentException>(() => AssetReferences.Rewrite(project.Assets, "res://models/a.dat", "models/c.dat")); // not a path
        Assert.Throws<ContentException>(() => AssetReferences.Rewrite(project.Assets, "res://models/a.dat", "res://models/c.dat", ["not a folder"]));
    }

    // ---- Packages -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void APackageFileIsFoundUnderBothAddressesAndKeepsTheFormItWasWrittenIn()
    {
        using var project = new Project();
        project.Write("packages/core/models/car.dat", "car");
        AssetUid car = project.Imported("packages/core/models/car.dat");
        project.Write("packages/core/scenes/a.ascene", SceneOf(AssetUid.New(), Model("mod://core/models/car.dat")));
        project.Write("scenes/b.ascene", SceneOf(AssetUid.New(), Model("res://packages/core/models/car.dat"), Model($"mod://core/models/car.dat|{car}")));

        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://packages/core/models/car.dat", "res://packages/core/vehicles/truck.dat");

        Assert.Contains("Model=\"mod://core/vehicles/truck.dat\"", project.Read("packages/core/scenes/a.ascene"), StringComparison.Ordinal);
        string b = project.Read("scenes/b.ascene");
        Assert.Contains("Model=\"res://packages/core/vehicles/truck.dat\"", b, StringComparison.Ordinal);
        Assert.Contains($"Model=\"mod://core/vehicles/truck.dat|{car}\"", b, StringComparison.Ordinal);
        Assert.Equal(3, report.Changes.Count);
    }

    // ---- Roots that cannot be written -------------------------------------------------------------------------------------------------

    [Fact]
    public void AReadOnlyRootIsReportedNotWritten()
    {
        using var project = new Project();
        using var mod = new TempFolder();
        project.Files.Mount("mod://pack", new DirectorySource(mod.Root, writable: false));
        project.Assets.Roots.Add("mod://pack/");
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        string modScene = SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}"));
        mod.Write("scenes/m.ascene", modScene);
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model("res://models/car.dat")));

        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat");

        Assert.Equal(["res://scenes/a.ascene"], report.Documents);
        SkippedDocument skipped = Assert.Single(report.Skipped);
        Assert.Equal("mod://pack/scenes/m.ascene", skipped.Document);
        Assert.Contains("read-only", skipped.Reason, StringComparison.Ordinal);
        Assert.Equal(modScene, File.ReadAllText(Path.Combine(mod.Root, "scenes", "m.ascene")));
        Assert.Equal(2, report.Changes.Count + report.Skipped.Count); // the reference in the pack was found too, and its UID still finds the asset
    }

    [Fact]
    public void ASceneThatIsASymbolicLinkIsRewrittenThroughTheLink()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");
        using var project = new Project();
        using var outside = new TempFolder();
        project.Write("models/car.dat", "car");
        string real = outside.Write("shared.ascene", SceneOf(AssetUid.New(), Model("res://models/car.dat")));
        Directory.CreateDirectory(Path.Combine(project.Root, "scenes"));
        string link = Path.Combine(project.Root, "scenes", "linked.ascene");
        File.CreateSymbolicLink(link, real);

        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat");

        Assert.Equal(["res://scenes/linked.ascene"], report.Documents);
        Assert.NotNull(new FileInfo(link).LinkTarget); // the link is still there...
        Assert.Contains("res://models/truck.dat", File.ReadAllText(real), StringComparison.Ordinal); // ...and the file it leads to was rewritten
    }

    // ---- Files that cannot be trusted ---------------------------------------------------------------------------------------------------

    [Fact]
    public void HostileOrUnreadableDocumentsAreSkippedWithAReasonAndLeftAsTheyWere()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        byte[] notUtf8 = [.. Encoding.UTF8.GetBytes("<Scene version=\"1\"><A p=\"res://models/car.dat\"/></Scene>"), 0xFF, 0xFE, 0xC3];
        string notWellFormed = "<Scene version=\"1\"><A p=\"res://models/car.dat\"></Scene>";
        string withDoctype = "<!DOCTYPE Scene [<!ENTITY a \"b\">]><Scene version=\"1\"><A p=\"res://models/car.dat\"/></Scene>";
        byte[] cooked = XmlBinary.Write(XmlNode.Parse("<Scene version=\"1\"><A p=\"res://models/car.dat\"/></Scene>"));
        project.WriteBytes("scenes/utf.ascene", notUtf8);
        project.Write("scenes/bad.ascene", notWellFormed);
        project.Write("scenes/dtd.ascene", withDoctype);
        project.WriteBytes("scenes/cooked.ascene", cooked);
        project.Write("scenes/good.ascene", SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}")));

        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat", allowSkipped: true);

        Assert.Equal(["res://scenes/good.ascene"], report.Documents);
        Assert.All(report.Skipped, s => Assert.True(s.Blocking)); // these could have been written, and their references were not
        Dictionary<string, string> reasons = report.Skipped.ToDictionary(s => s.Document, s => s.Reason);
        Assert.Contains("UTF-8", reasons["res://scenes/utf.ascene"], StringComparison.Ordinal);
        Assert.Contains("well-formed", reasons["res://scenes/bad.ascene"], StringComparison.Ordinal);
        Assert.Contains("well-formed", reasons["res://scenes/dtd.ascene"], StringComparison.Ordinal);
        Assert.Contains("cooked", reasons["res://scenes/cooked.ascene"], StringComparison.Ordinal);
        Assert.Equal(notUtf8, File.ReadAllBytes(Path.Combine(project.Root, "scenes", "utf.ascene")));
        Assert.Equal(notWellFormed, project.Read("scenes/bad.ascene"));
        Assert.Equal(withDoctype, project.Read("scenes/dtd.ascene"));
        Assert.Equal(cooked, File.ReadAllBytes(Path.Combine(project.Root, "scenes", "cooked.ascene")));
    }

    [Fact]
    public void AWritableDocumentThatWasSkippedRefusesTheRenameBeforeAnythingIsWritten()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        string broken = "<Scene version=\"1\"><A p=\"res://models/car.dat\"></Scene>";
        string good = SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}"));
        project.Write("scenes/broken.ascene", broken);
        project.Write("scenes/good.ascene", good);

        RewriteReport preview = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat", dryRun: true);
        var rewriteError = Assert.Throws<ContentException>(() => AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat"));
        var moveError = Assert.Throws<ContentException>(() => project.Assets.Move("res://models/car.dat", "res://vehicles/truck.dat"));

        SkippedDocument skipped = Assert.Single(preview.Skipped); // a preview does not throw: it says what would be refused
        Assert.True(skipped.Blocking);
        Assert.Contains("res://scenes/broken.ascene", rewriteError.Message, StringComparison.Ordinal);
        Assert.Contains("well-formed", rewriteError.Message, StringComparison.Ordinal);
        Assert.Contains("res://scenes/broken.ascene", moveError.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", moveError.Message, StringComparison.Ordinal);
        Assert.Equal(good, project.Read("scenes/good.ascene"));
        Assert.Equal(broken, project.Read("scenes/broken.ascene"));
        Assert.True(project.Has("models/car.dat")); // nothing moved
        Assert.False(project.Has("vehicles/truck.dat"));

        // The editor asked the user, who agreed: the good scene is rewritten, the broken one is reported and left alone.
        MoveReport moved = project.Assets.Move("res://models/car.dat", "res://vehicles/truck.dat", allowSkipped: true);

        Assert.True(moved.Applied);
        Assert.True(Assert.Single(moved.References.Skipped).Blocking);
        Assert.Contains("res://vehicles/truck.dat", project.Read("scenes/good.ascene"), StringComparison.Ordinal);
        Assert.Equal(broken, project.Read("scenes/broken.ascene"));
        Assert.True(project.Has("vehicles/truck.dat"));
    }

    [Fact]
    public void ADocumentThatCannotBeReadAsUtf8AndDoesNotNameTheAssetIsNotInTheWay()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model("res://models/car.dat")));
        project.WriteBytes("scenes/latin1.ascene", [.. Encoding.UTF8.GetBytes("<Scene version=\"1\"><Name text=\"caf"), 0xE9, .. Encoding.UTF8.GetBytes("\"/></Scene>")]);

        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat");

        Assert.Empty(report.Skipped);
        Assert.Equal(["res://scenes/a.ascene"], report.Documents);
    }

    [Fact]
    public void ADocumentThatIsTooBigIsSkipped()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        // A sparse file: it takes no room on a file system that supports them, and the size is all that is looked at.
        using (FileStream big = File.Create(Path.Combine(project.Root, "big.ascene")))
        {
            big.SetLength(AssetReferences.MaxDocumentBytes + 1);
        }

        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat", dryRun: true);

        SkippedDocument skipped = Assert.Single(report.Skipped);
        Assert.Equal("res://big.ascene", skipped.Document);
        Assert.Contains("larger than", skipped.Reason, StringComparison.Ordinal);
    }

    // ---- All or nothing -----------------------------------------------------------------------------------------------------------------

    /// <summary>A folder source that fails when told to: the Nth write, or the Nth move, throws (once, or for good with <see cref="Sticky"/>).</summary>
    private sealed class FlakySource(DirectorySource inner) : IFileSource
    {
        private bool _failed;

        public int WritesBeforeFailure { get; set; } = int.MaxValue;

        public int MovesBeforeFailure { get; set; } = int.MaxValue;

        /// <summary>After the first failure everything of that kind keeps failing (a disk that stays full, a file that stays locked).</summary>
        public bool Sticky { get; set; }

        /// <summary>What a failing write throws.</summary>
        public Func<Exception> WriteFailure { get; set; } = () => new IOException("the disk is full");

        /// <summary>Runs before a write, with the path and the number of writes before this one: a person saving a file in between.</summary>
        public Action<string, int>? BeforeWrite { get; set; }

        private int _writes;

        public string Description => inner.Description;

        public bool CanWrite => true;

        public FileKind Kind(string path) => inner.Kind(path);

        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);

        public Stream OpenRead(string path) => inner.OpenRead(path);

        public void List(string directory, List<(string Name, FileKind Kind)> into) => inner.List(directory, into);

        public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
        {
            BeforeWrite?.Invoke(path, _writes++);
            if (_failed && Sticky)
            {
                throw WriteFailure();
            }

            if (WritesBeforeFailure-- <= 0)
            {
                _failed = true;
                WritesBeforeFailure = Sticky ? 0 : int.MaxValue; // it fails once, and the repair that follows works
                throw WriteFailure();
            }

            inner.WriteAllBytes(path, bytes);
        }

        public void Delete(string path) => inner.Delete(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void Move(string source, string target)
        {
            if (MovesBeforeFailure-- <= 0)
            {
                MovesBeforeFailure = Sticky ? 0 : int.MaxValue;
                throw new IOException("the file is locked");
            }

            inner.Move(source, target);
        }
    }

    private static (VirtualFileSystem Files, FlakySource Source) FlakyProject(TempFolder folder)
    {
        var source = new FlakySource(new DirectorySource(folder.Root, writable: true));
        var files = new VirtualFileSystem();
        files.Mount("res://", source);
        return (files, source);
    }

    [Fact]
    public void AWriteThatFailsPutsTheFilesAlreadyWrittenBack()
    {
        using var folder = new TempFolder();
        folder.Write("models/car.dat", "car");
        string a = SceneOf(AssetUid.New(), Model("res://models/car.dat"));
        string b = SceneOf(AssetUid.New(), Model("res://models/car.dat"));
        string c = SceneOf(AssetUid.New(), Model("res://models/car.dat"));
        folder.Write("scenes/a.ascene", a);
        folder.Write("scenes/b.ascene", b);
        folder.Write("scenes/c.ascene", c);
        (VirtualFileSystem files, FlakySource source) = FlakyProject(folder);
        source.WritesBeforeFailure = 2; // the third write fails, after two files have been changed

        var error = Assert.Throws<ContentException>(() => AssetReferences.Rewrite(new AssetDatabase(files), "res://models/car.dat", "res://models/truck.dat"));

        Assert.Contains("nothing was changed", error.Message, StringComparison.Ordinal);
        Assert.Equal(a, File.ReadAllText(Path.Combine(folder.Root, "scenes", "a.ascene")));
        Assert.Equal(b, File.ReadAllText(Path.Combine(folder.Root, "scenes", "b.ascene")));
        Assert.Equal(c, File.ReadAllText(Path.Combine(folder.Root, "scenes", "c.ascene")));
    }

    private static (TempFolder Folder, VirtualFileSystem Files, FlakySource Source, string[] Scenes) ThreeScenes()
    {
        var folder = new TempFolder();
        folder.Write("models/car.dat", "car");
        string[] scenes = ["a", "b", "c"];
        for (int i = 0; i < scenes.Length; i++)
        {
            folder.Write($"scenes/{scenes[i]}.ascene", SceneOf(AssetUid.New(), Model("res://models/car.dat")));
        }

        (VirtualFileSystem files, FlakySource source) = FlakyProject(folder);
        return (folder, files, source, scenes);
    }

    private static string ReadAllText(TempFolder folder, string relative) => File.ReadAllText(Path.Combine(folder.Root, relative));

    private static string ReadScene(TempFolder folder, string name) => File.ReadAllText(Path.Combine(folder.Root, "scenes", name + ".ascene"));

    [Fact]
    public void AFileThatCannotBePutBackIsNamedAndStaysValid()
    {
        (TempFolder folder, VirtualFileSystem files, FlakySource source, _) = ThreeScenes();
        using (folder)
        {
            string c = ReadScene(folder, "c");
            source.WritesBeforeFailure = 2; // the third write fails...
            source.Sticky = true; // ...and so does every write after it, the repairs included

            var error = Assert.Throws<ContentException>(() => AssetReferences.Rewrite(new AssetDatabase(files), "res://models/car.dat", "res://models/truck.dat"));

            Assert.Contains("could not be put back", error.Message, StringComparison.Ordinal);
            Assert.Contains("res://scenes/a.ascene", error.Message, StringComparison.Ordinal);
            Assert.Contains("res://scenes/b.ascene", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("res://scenes/c.ascene", error.Message, StringComparison.Ordinal); // never written, nothing to put back
            Assert.IsType<IOException>(error.InnerException);
            Assert.Equal(c, ReadScene(folder, "c"));
            Assert.Contains("res://models/truck.dat", ReadScene(folder, "a"), StringComparison.Ordinal); // whole, new text: valid, only half done
            XmlNode.Parse(ReadScene(folder, "b"));
        }
    }

    [Fact]
    public void AnExceptionThatIsNotAnIoExceptionStillPutsTheFilesBack()
    {
        (TempFolder folder, VirtualFileSystem files, FlakySource source, _) = ThreeScenes();
        using (folder)
        {
            string a = ReadScene(folder, "a");
            source.WritesBeforeFailure = 1;
            source.WriteFailure = () => new InvalidOperationException("a source of its own failed");

            var error = Assert.Throws<ContentException>(() => AssetReferences.Rewrite(new AssetDatabase(files), "res://models/car.dat", "res://models/truck.dat"));

            Assert.Contains("nothing was changed", error.Message, StringComparison.Ordinal);
            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.Equal(a, ReadScene(folder, "a")); // written first, put back
        }
    }

    [Fact]
    public void AFileSavedSinceItWasWrittenIsNotOverwrittenByTheRepair()
    {
        (TempFolder folder, VirtualFileSystem files, FlakySource source, _) = ThreeScenes();
        using (folder)
        {
            string saved = SceneOf(AssetUid.New(), Model("res://models/car.dat"), "<Edited/>");
            source.WritesBeforeFailure = 1; // the second write fails
            source.BeforeWrite = (path, count) =>
            {
                if (count == 1)
                {
                    // the editor's auto-save writes a.ascene (already rewritten by this code) just before b's write goes wrong
                    File.WriteAllText(Path.Combine(folder.Root, "scenes", "a.ascene"), saved);
                }
            };

            var error = Assert.Throws<ContentException>(() => AssetReferences.Rewrite(new AssetDatabase(files), "res://models/car.dat", "res://models/truck.dat"));

            Assert.Equal(saved, ReadScene(folder, "a")); // what the person saved stays
            Assert.Contains("saved since", error.Message, StringComparison.Ordinal);
            Assert.Contains("res://scenes/a.ascene", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AFileSavedBetweenTheCheckAndItsOwnWriteStopsTheRewriteAndPutsTheEarlierOnesBack()
    {
        (TempFolder folder, VirtualFileSystem files, FlakySource source, _) = ThreeScenes();
        using (folder)
        {
            string a = ReadScene(folder, "a");
            string saved = SceneOf(AssetUid.New(), Model("res://models/car.dat"), "<Edited/>");
            source.BeforeWrite = (path, count) =>
            {
                if (count == 0)
                {
                    // everything was checked; while a is being written, the editor saves c
                    File.WriteAllText(Path.Combine(folder.Root, "scenes", "c.ascene"), saved);
                }
            };

            var error = Assert.Throws<ContentException>(() => AssetReferences.Rewrite(new AssetDatabase(files), "res://models/car.dat", "res://models/truck.dat"));

            Assert.Contains("res://scenes/c.ascene was saved", error.Message, StringComparison.Ordinal);
            Assert.Equal(saved, ReadScene(folder, "c")); // not overwritten
            Assert.Equal(a, ReadScene(folder, "a")); // written, then put back
            Assert.Contains("res://models/car.dat", ReadScene(folder, "b"), StringComparison.Ordinal); // never reached
        }
    }

    [Fact]
    public void ADocumentSavedWhileTheReferencesWerePreparedStopsTheRewrite()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model("res://models/car.dat")));
        project.Write("scenes/b.ascene", SceneOf(AssetUid.New(), Model("res://models/car.dat")));
        AssetReferences.ReferencePlan plan = AssetReferences.Plan(project.Assets, AssetUid.None, "res://models/car.dat", "res://models/truck.dat", null, includeScripts: false);
        string edited = SceneOf(AssetUid.New(), Model("res://models/car.dat"), "<Added/>");
        project.Write("scenes/b.ascene", edited); // the editor saved it in between

        var error = Assert.Throws<ContentException>(() => AssetReferences.Apply(project.Files, plan.Edits));

        Assert.Contains("changed", error.Message, StringComparison.Ordinal);
        Assert.Contains("res://models/car.dat", project.Read("scenes/a.ascene"), StringComparison.Ordinal); // nothing was written, not even the first
        Assert.Equal(edited, project.Read("scenes/b.ascene"));
    }

    // ---- Reading goes wrong ---------------------------------------------------------------------------------------------------------

    /// <summary>A folder source whose reads and listings throw what a test tells them to.</summary>
    private sealed class FaultySource(DirectorySource inner) : IFileSource
    {
        /// <summary>Called with the operation (<c>list</c>, <c>read</c>, <c>open</c>) and the path; it throws to make that fail.</summary>
        public Action<string, string>? Fault { get; set; }

        public string Description => inner.Description;

        public bool CanWrite => true;

        public FileKind Kind(string path) => inner.Kind(path);

        public byte[] ReadAllBytes(string path)
        {
            Fault?.Invoke("read", path);
            return inner.ReadAllBytes(path);
        }

        public Stream OpenRead(string path)
        {
            Fault?.Invoke("open", path);
            return inner.OpenRead(path);
        }

        public void List(string directory, List<(string Name, FileKind Kind)> into)
        {
            Fault?.Invoke("list", directory);
            inner.List(directory, into);
        }

        public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => inner.WriteAllBytes(path, bytes);

        public void Delete(string path) => inner.Delete(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void Move(string source, string target) => inner.Move(source, target);
    }

    private static (AssetDatabase Assets, FaultySource Source) FaultyProject(TempFolder folder)
    {
        var source = new FaultySource(new DirectorySource(folder.Root, writable: true));
        var files = new VirtualFileSystem();
        files.Mount("res://", source);
        return (new AssetDatabase(files), source);
    }

    [Theory]
    [InlineData("list", "scenes", typeof(DirectoryNotFoundException), "FRM")] // a folder deleted while the scan was under way
    [InlineData("list", "scenes", typeof(UnauthorizedAccessException), "FRM")] // a folder that cannot be read
    [InlineData("list", "scenes", typeof(NotSupportedException), "FRM")] // whatever else a source of its own throws
    [InlineData("open", "scenes/a.ascene", typeof(InvalidOperationException), "FRM")]
    [InlineData("read", "models/car.dat.import.xml", typeof(IOException), "RM")] // the UID of the asset
    [InlineData("read", "imported/index.xml", typeof(IOException), "M")] // the import index
    public void ASourceThatFailsWhileReadingGivesAContentExceptionAndChangesNothing(string operation, string at, Type exception, string entryPoints)
    {
        using var folder = new TempFolder();
        folder.Write("models/car.dat", "car");
        string scene = SceneOf(AssetUid.New(), Model("res://models/car.dat"));
        folder.Write("scenes/a.ascene", scene);
        (AssetDatabase assets, FaultySource source) = FaultyProject(folder);
        new AssetImporter(assets.Files, [new DatImporter()]).Run();
        AssetRef target = assets.RefOf("res://models/car.dat");
        source.Fault = (op, path) =>
        {
            if (op == operation && path == at)
            {
                throw (Exception)Activator.CreateInstance(exception, "it went wrong")!;
            }
        };

        if (entryPoints.Contains('F', StringComparison.Ordinal))
        {
            Assert.Throws<ContentException>(() => AssetReferences.Find(assets, target));
        }

        if (entryPoints.Contains('R', StringComparison.Ordinal))
        {
            Assert.Throws<ContentException>(() => AssetReferences.Rewrite(assets, "res://models/car.dat", "res://models/truck.dat"));
        }

        var error = Assert.Throws<ContentException>(() => assets.Move("res://models/car.dat", "res://models/truck.dat"));

        Assert.Contains("Nothing was changed", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.InnerException);
        Assert.Equal(scene, File.ReadAllText(Path.Combine(folder.Root, "scenes", "a.ascene")));
        Assert.True(File.Exists(Path.Combine(folder.Root, "models", "car.dat")));
    }

    [Fact]
    public void AFolderThatCannotBeReadIsAContentExceptionAndNothingIsChanged()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A folder is made unreadable with Unix modes here.");
            return;
        }

        using var project = new Project();
        project.Write("models/car.dat", "car");
        string scene = SceneOf(AssetUid.New(), Model("res://models/car.dat"));
        project.Write("scenes/a.ascene", scene);
        string locked = Path.Combine(project.Root, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.SkipWhen(CanList(locked), "The folder is still readable (running as root?).");

            Assert.Throws<ContentException>(() => AssetReferences.Find(project.Assets, project.Assets.RefOf("res://models/car.dat")));
            Assert.Throws<ContentException>(() => AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat"));
            Assert.Throws<ContentException>(() => project.Assets.Move("res://models/car.dat", "res://models/truck.dat"));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(scene, project.Read("scenes/a.ascene"));
        Assert.True(project.Has("models/car.dat"));
    }

    private static bool CanList(string folder)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(folder);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void ALinkThatLeadsBackToAFolderAboveItEndsInAContentExceptionAndNotInATreeWithoutEnd()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");
        using var project = new Project();
        project.Write("models/car.dat", "car");
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model("res://models/car.dat")));
        // Two links to the folder they are in: below each of them the same tree again, twice, without end.
        File.CreateSymbolicLink(Path.Combine(project.Root, "scenes", "again"), ".");
        File.CreateSymbolicLink(Path.Combine(project.Root, "scenes", "twice"), ".");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = Assert.Throws<ContentException>(() => AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat"));

        Assert.Contains("folders", error.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        Assert.Contains("res://models/car.dat", project.Read("scenes/a.ascene"), StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestWithoutPathsIsANullArgumentAndNotAContentException()
    {
        using var project = new Project();

        Assert.Throws<ArgumentNullException>(() => AssetReferences.Rewrite(project.Assets, null!, "res://a.dat"));
        Assert.Throws<ArgumentNullException>(() => AssetReferences.Rewrite(project.Assets, "res://a.dat", null!));
        Assert.Throws<ArgumentNullException>(() => AssetReferences.Rewrite(null!, "res://a.dat", "res://b.dat"));
        Assert.Throws<ArgumentNullException>(() => AssetReferences.Find(null!, default));
        Assert.Throws<ArgumentNullException>(() => project.Assets.Move(null!, "res://b.dat"));
        Assert.Throws<ArgumentNullException>(() => project.Assets.Move("res://a.dat", null!));
    }

    // ---- Scripts ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ScriptMentionsAreReportedOnly()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        string script = "local m = models:load(\"res://models/car.dat\")\n-- see res://models/car.dat.bak and res://models/car.dat.\nlocal u = assets:resolve(\"" + car + "\")\nlocal both = \"res://models/car.dat|" + car + "\"\nlocal fresh = \"res://models/truck.dat\"\n";
        project.Write("scripts/main.luau", script);
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model("res://models/car.dat")));

        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat");

        Assert.Equal(script, project.Read("scripts/main.luau")); // never rewritten
        Assert.Equal([1, 2, 4], report.ScriptMentions.Select(s => s.Line)); // the old path, as a literal, as a sentence end, and with its UID (the UID alone follows the file and needs no one)
        Assert.All(report.ScriptMentions, s => Assert.Equal(("", ""), (s.Element, s.Attribute)));
        Assert.Equal(new AssetRef("res://models/car.dat", car), report.ScriptMentions[2].Value);

        // Find lists them when asked, the UID mention too.
        IReadOnlyList<AssetReferenceSite> found = AssetReferences.Find(project.Assets, project.Assets.RefOf("res://models/car.dat"), includeScripts: true);
        Assert.Equal([1, 2, 3, 4], found.Where(s => s.Attribute == "").Select(s => s.Line).Order());
        Assert.DoesNotContain(AssetReferences.Find(project.Assets, project.Assets.RefOf("res://models/car.dat")), s => s.Attribute == "");
    }

    [Fact]
    public void AHugeScriptFullOfThePathIsSearchedInLinearTime()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        const int lines = 500_000;
        // A mention of the path on every line, then one with its UID, and the UID alone: about 40 MB of text, the sort of file a hostile mod can ship.
        string script = string.Concat(Enumerable.Repeat($"local m = \"res://models/car.dat\" local n = \"res://models/car.dat|{car}\" local u = \"{car}\"\n", lines));
        project.Write("scripts/huge.luau", script);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat", dryRun: true);
        IReadOnlyList<AssetReferenceSite> found = AssetReferences.Find(project.Assets, project.Assets.RefOf("res://models/car.dat"), includeScripts: true);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}"); // quadratic, it took hours
        Assert.Equal(6_667, report.ScriptMentions.Count); // the first 10,000 mentions, minus the UIDs alone (they follow the file and need no one); the rest are not listed
        Assert.Equal(1, report.ScriptMentions[0].Line);
        Assert.Equal(1, report.ScriptMentions[1].Line);
        Assert.Equal(2, report.ScriptMentions[2].Line);
        Assert.Equal(10_000, found.Count);
        Assert.Equal(new AssetRef("res://models/car.dat", car), report.ScriptMentions[1].Value);
    }

    [Fact]
    public void TheMemoryOfAPlanIsBoundedAndTheDocumentsOverTheBoundAreSkippedWithTheReason()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        foreach (string name in new[] { "a", "b", "c" })
        {
            project.Write($"scenes/{name}.ascene", SceneOf(AssetUid.New(), Model("res://models/car.dat")));
        }

        int one = project.Files.ReadAllBytes("res://scenes/a.ascene").Length;
        AssetReferences.ReferencePlan plan = AssetReferences.Plan(project.Assets, AssetUid.None, "res://models/car.dat", "res://models/truck.dat", null, includeScripts: false, maxPlannedBytes: (4L * one) + 8);

        Assert.Equal(["res://scenes/a.ascene", "res://scenes/b.ascene"], plan.Edits.Select(e => e.Path)); // two documents, old and new text, fit
        SkippedDocument skipped = Assert.Single(plan.Report.Skipped);
        Assert.Equal("res://scenes/c.ascene", skipped.Document);
        Assert.True(skipped.Blocking);
        Assert.Contains("memory", skipped.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentThatNamesTheAssetAbsurdlyOftenIsSkipped()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), [.. Enumerable.Repeat(Model("res://models/car.dat"), AssetReferences.MaxReferencesPerDocument + 1)]));

        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat", dryRun: true);

        SkippedDocument skipped = Assert.Single(report.Skipped);
        Assert.Contains("more than", skipped.Reason, StringComparison.Ordinal);
        Assert.Empty(report.Documents);
    }

    // ---- Move ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void MoveMovesTheSidecarAndRewritesReferences()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        string sidecar = project.Read("models/car.dat.import.xml");
        string key = AssetImporter.LoadIndex(project.Files).Single().Key;
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}")));

        MoveReport report = project.Assets.Move("res://models/car.dat", "res://vehicles/truck.dat");

        Assert.True(report.Applied);
        Assert.Equal(["res://vehicles/truck.dat", "res://vehicles/truck.dat.import.xml", "res://imported/vehicles/truck.dat.up"], report.Files);
        Assert.False(project.Has("models/car.dat"));
        Assert.False(project.Has("models/car.dat.import.xml"));
        Assert.False(project.Has("imported/models/car.dat.up"));
        Assert.Equal("car", project.Read("vehicles/truck.dat"));
        Assert.Equal(sidecar, project.Read("vehicles/truck.dat.import.xml")); // the UID went with it
        Assert.Equal("CAR", project.Read("imported/vehicles/truck.dat.up"));
        Assert.Contains($"Model=\"res://vehicles/truck.dat|{car}\"", project.Read("scenes/a.ascene"), StringComparison.Ordinal);
        Assert.Contains($"uid=\"{car}\" path=\"res://vehicles/truck.dat\"", project.Read("imported/assets.xml"), StringComparison.Ordinal);
        ImportEntry entry = Assert.Single(AssetImporter.LoadIndex(project.Files));
        Assert.Equal(("vehicles/truck.dat", "imported/vehicles/truck.dat.up", key), (entry.Source, entry.Artifact, entry.Key));
        Assert.Empty(report.Warnings);

        // The artifact is still good: importing finds nothing to do.
        ImportReport import = project.Import();
        Assert.Empty(import.Imported);
        Assert.Contains("vehicles/truck.dat", import.UpToDate);
        Assert.Equal(car, project.Assets.UidOf("res://vehicles/truck.dat"));
    }

    [Fact]
    public void ARenamedModelUsedByTwoScenesStillResolvesThroughItsUid()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}")));
        project.Write("scenes/b.ascene", SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}"), Model(car.ToString())));
        AssetRef oldReference = project.Assets.RefOf("res://models/car.dat");

        MoveReport report = project.Assets.Move("res://models/car.dat", "res://vehicles/truck.dat");

        Assert.Equal(3, report.References.Changes.Count + report.References.Unchanged.Count);
        // The reference the scenes held before the rename still finds the model (through its UID), and the scenes now say where it is.
        var fresh = new AssetDatabase(project.Files);
        Assert.Equal("res://vehicles/truck.dat", fresh.Resolve(oldReference));
        foreach (string scene in new[] { "scenes/a.ascene", "scenes/b.ascene" })
        {
            XmlNode root = XmlNode.Parse(project.Read(scene));
            foreach (XmlNode renderer in root.Children)
            {
                AssetRef written = AssetRef.Parse(renderer.Attribute("Model"));
                Assert.Equal("res://vehicles/truck.dat", fresh.Resolve(written));
                Assert.True(written.Uid == car && (!written.HasPath || written.Path == "res://vehicles/truck.dat"));
            }
        }

        // Even a document that was not rewritten (the editor skipped it) resolves, because the UID travels with the sidecar.
        Assert.Equal("res://vehicles/truck.dat", fresh.Resolve(new AssetRef("res://models/car.dat", car)));
    }

    [Fact]
    public void RewriteCanRunAfterTheFilesMoved()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}"), Model("res://models/car.dat")));
        Directory.CreateDirectory(Path.Combine(project.Root, "vehicles"));
        File.Move(Path.Combine(project.Root, "models", "car.dat"), Path.Combine(project.Root, "vehicles", "truck.dat"));
        File.Move(Path.Combine(project.Root, "models", "car.dat.import.xml"), Path.Combine(project.Root, "vehicles", "truck.dat.import.xml"));

        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://vehicles/truck.dat");

        Assert.Equal(2, report.Changes.Count);
        Assert.Contains($"\"res://vehicles/truck.dat|{car}\"", project.Read("scenes/a.ascene"), StringComparison.Ordinal);
        Assert.Contains("Model=\"res://vehicles/truck.dat\"", project.Read("scenes/a.ascene"), StringComparison.Ordinal);
    }

    [Fact]
    public void MoveOfAnAuthoredSceneKeepsItsUidAndRewritesTheScenesThatNameIt()
    {
        using var project = new Project();
        AssetUid door = AssetUid.New();
        string doorScene = $"<?xml version=\"1.0\"?>\n<!-- keep me -->\n<Scene version=\"1\" uid=\"{door}\">\n  <Entity id=\"1\"><Portal target=\"res://scenes/door.ascene|{door}\"/></Entity>\n</Scene>\n";
        project.Write("scenes/door.ascene", doorScene); // a scene that refers to itself: the edit is made at its old path, then the file moves
        project.Write("scenes/hall.ascene", SceneOf(AssetUid.New(), $"<Portal target=\"res://scenes/door.ascene|{door}\"/>"));

        MoveReport report = project.Assets.Move("res://scenes/door.ascene", "res://levels/door.ascene");

        Assert.Equal(["res://levels/door.ascene"], report.Files);
        Assert.Equal(doorScene.Replace("res://scenes/door.ascene", "res://levels/door.ascene", StringComparison.Ordinal), project.Read("levels/door.ascene"));
        Assert.Equal(door, project.Assets.UidOf("res://levels/door.ascene"));
        Assert.Contains($"target=\"res://levels/door.ascene|{door}\"", project.Read("scenes/hall.ascene"), StringComparison.Ordinal);
        Assert.False(project.Has("scenes/door.ascene"));
    }

    [Fact]
    public void MoveRefusesWhatWouldDamageSomething()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        project.Write("models/bike.dat", "bike");
        project.Import();
        project.Write("models/stale.dat.import.xml", "<Import/>");
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model("res://models/car.dat")));

        Assert.Throws<ContentException>(() => project.Assets.Move("res://models/car.dat", "res://models/bike.dat")); // exists
        Assert.Throws<ContentException>(() => project.Assets.Move("res://models/car.dat", "res://models/stale.dat")); // an orphan sidecar is there
        Assert.Throws<ContentException>(() => project.Assets.Move("res://models", "res://meshes")); // a folder
        Assert.Throws<ContentException>(() => project.Assets.Move("res://models/car.dat.import.xml", "res://models/x.dat.import.xml")); // follows its asset
        Assert.Throws<ContentException>(() => project.Assets.Move("res://models/car.dat", "res://imported/car.dat")); // the cache
        Assert.Throws<ContentException>(() => project.Assets.Move("res://models/car.dat", "res://.hidden/car.dat"));
        Assert.Throws<ContentException>(() => project.Assets.Move("res://models/missing.dat", "res://models/m.dat"));
        Assert.Throws<ContentException>(() => project.Assets.Move("mod://x/a.dat", "res://models/m.dat")); // only the game folder
        Assert.True(project.Has("models/car.dat"));
        Assert.Contains("res://models/car.dat", project.Read("scenes/a.ascene"), StringComparison.Ordinal);
    }

    [Fact]
    public void MoveRefusesATargetInsideItsSourceOrInsideAFileBeforeAnythingIsWritten()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        project.Write("models/bike.dat", "bike");
        string scene = SceneOf(AssetUid.New(), Model("res://models/car.dat"));
        project.Write("scenes/a.ascene", scene);

        var inside = Assert.Throws<ContentException>(() => project.Assets.Move("res://models/car.dat", "res://models/car.dat/sub/car.dat"));
        var file = Assert.Throws<ContentException>(() => project.Assets.Move("res://models/car.dat", "res://models/bike.dat/car.dat"));

        Assert.Contains("inside", inside.Message, StringComparison.Ordinal);
        Assert.Contains("is a file", file.Message, StringComparison.Ordinal);
        Assert.Equal(scene, project.Read("scenes/a.ascene")); // the documents were not written and put back: they were never touched
        Assert.True(project.Has("models/car.dat"));
    }

    [Theory]
    [InlineData("game.xml", "res://game.xml", "res://elsewhere/game.xml")]
    [InlineData("packages/core/mod.xml", "res://packages/core/mod.xml", "res://packages/core/x/mod.xml")]
    [InlineData("data/a.dat", "res://data/a.dat", "res://game.xml")]
    [InlineData("data/a.dat", "res://data/a.dat", "res://data/mod.xml")]
    [InlineData("packages/core/imported/index.xml", "res://packages/core/imported/index.xml", "res://x/index.xml")]
    [InlineData("data/a.dat", "res://data/a.dat", "res://packages/core/imported/a.dat")]
    [InlineData("data/a.dat", "res://data/a.dat", "res://dev_mods/core/imported/a.dat")]
    public void MoveRefusesTheManifestsAndAPackagesOwnCache(string file, string from, string to)
    {
        using var project = new Project();
        project.Write(file, "x");

        Assert.Throws<ContentException>(() => project.Assets.Move(from, to));

        Assert.True(project.Has(file));
    }

    [Fact]
    public void RewriteRefusesAFolderAsTheNewPathAndAsTheOldOne()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        project.Write("models/sub/x.dat", "x");

        var target = Assert.Throws<ContentException>(() => AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/sub"));
        var source = Assert.Throws<ContentException>(() => AssetReferences.Rewrite(project.Assets, "res://models/sub", "res://models/other"));

        Assert.Contains("folder", target.Message, StringComparison.Ordinal);
        Assert.Contains("folder", source.Message, StringComparison.Ordinal);
    }

    /// <summary>A folder that finds a file under any case, the way the file system of Windows and (by default) macOS does; the listings keep the case the file was made with.</summary>
    private sealed class CaseInsensitiveSource(DirectorySource inner) : IFileSource
    {
        public string Description => inner.Description;

        public bool CanWrite => true;

        private string Real(string path)
        {
            string real = "";
            foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var entries = new List<(string Name, FileKind Kind)>();
                inner.List(real, entries);
                string found = entries.Find(e => string.Equals(e.Name, segment, StringComparison.OrdinalIgnoreCase)).Name ?? segment;
                real = real.Length == 0 ? found : real + "/" + found;
            }

            return real;
        }

        public FileKind Kind(string path) => inner.Kind(Real(path));

        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(Real(path));

        public Stream OpenRead(string path) => inner.OpenRead(Real(path));

        public void List(string directory, List<(string Name, FileKind Kind)> into) => inner.List(Real(directory), into);

        public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => inner.WriteAllBytes(Real(path), bytes);

        public void Delete(string path) => inner.Delete(Real(path));

        public void CreateDirectory(string path) => inner.CreateDirectory(Real(path));

        public void Move(string source, string target) => inner.Move(Real(source), target);
    }

    [Fact]
    public void ARenameThatOnlyChangesTheCaseOnAFileSystemThatIgnoresCaseSaysSo()
    {
        using var folder = new TempFolder();
        folder.Write("models/Car.dat", "car");
        var files = new VirtualFileSystem();
        files.Mount("res://", new CaseInsensitiveSource(new DirectorySource(folder.Root, writable: true)));
        var assets = new AssetDatabase(files);
        Assert.True(files.FileExists("res://models/car.dat")); // the same file under another case

        var rewrite = Assert.Throws<ContentException>(() => AssetReferences.Rewrite(assets, "res://models/Car.dat", "res://models/car.dat"));
        var move = Assert.Throws<ContentException>(() => assets.Move("res://models/Car.dat", "res://models/car.dat"));

        Assert.Contains("only in case", rewrite.Message, StringComparison.Ordinal);
        Assert.Contains("two steps", move.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder.Root, "models", "Car.dat")));

        // Two real files whose names differ in case (a file system that tells them apart) are still "two assets would become one".
        using var distinct = new Project();
        distinct.Write("models/a.dat", "a");
        distinct.Write("models/A.dat", "b");
        if (distinct.Files.FileExists("res://models/A.dat") && Directory.GetFiles(Path.Combine(distinct.Root, "models")).Length == 2)
        {
            Assert.Contains("two assets", Assert.Throws<ContentException>(() => AssetReferences.Rewrite(distinct.Assets, "res://models/a.dat", "res://models/A.dat")).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AMovePreviewChangesNothing()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        string scene = SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}"));
        project.Write("scenes/a.ascene", scene);
        string list = project.Read("imported/assets.xml");

        MoveReport preview = project.Assets.Move("res://models/car.dat", "res://vehicles/truck.dat", dryRun: true);

        Assert.False(preview.Applied);
        Assert.Equal(3, preview.Files.Count);
        Assert.Single(preview.References.Changes);
        Assert.True(project.Has("models/car.dat"));
        Assert.True(project.Has("models/car.dat.import.xml"));
        Assert.False(project.Has("vehicles/truck.dat"));
        Assert.Equal(scene, project.Read("scenes/a.ascene"));
        Assert.Equal(list, project.Read("imported/assets.xml"));
    }

    [Fact]
    public void AMoveThatFailsPutsTheFilesAndTheDocumentsBack()
    {
        using var folder = new TempFolder();
        folder.Write("models/car.dat", "car");
        (VirtualFileSystem files, FlakySource source) = FlakyProject(folder);
        var importer = new AssetImporter(files, [new DatImporter()]);
        importer.Run();
        AssetUid car = ImportSidecar.ReadUid(File.ReadAllText(Path.Combine(folder.Root, "models", "car.dat.import.xml")));
        string scene = SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}"));
        folder.Write("scenes/a.ascene", scene);
        source.MovesBeforeFailure = 1; // the asset moves, its sidecar cannot

        var error = Assert.Throws<ContentException>(() => new AssetDatabase(files).Move("res://models/car.dat", "res://vehicles/truck.dat"));

        Assert.Contains("nothing was changed", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder.Root, "models", "car.dat")));
        Assert.True(File.Exists(Path.Combine(folder.Root, "models", "car.dat.import.xml")));
        Assert.False(File.Exists(Path.Combine(folder.Root, "vehicles", "truck.dat")));
        Assert.Equal(scene, File.ReadAllText(Path.Combine(folder.Root, "scenes", "a.ascene")));
    }

    [Fact]
    public void AMoveThatCannotBeUndoneKeepsTheDocumentsNamingTheNewPathAndRunningItAgainFinishesIt()
    {
        using var folder = new TempFolder();
        folder.Write("models/car.dat", "car");
        (VirtualFileSystem files, FlakySource source) = FlakyProject(folder);
        new AssetImporter(files, [new DatImporter()]).Run();
        AssetUid car = ImportSidecar.ReadUid(File.ReadAllText(Path.Combine(folder.Root, "models", "car.dat.import.xml")));
        folder.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}"), Model("res://models/car.dat")));
        source.MovesBeforeFailure = 1; // the asset moves, its sidecar cannot...
        source.Sticky = true; // ...and the asset cannot go back either

        var error = Assert.Throws<ContentException>(() => new AssetDatabase(files).Move("res://models/car.dat", "res://vehicles/truck.dat"));

        Assert.Contains("run the same move again", error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder.Root, "vehicles", "truck.dat"))); // the asset is where the documents say
        Assert.True(File.Exists(Path.Combine(folder.Root, "models", "car.dat.import.xml")));
        Assert.Equal(2, ReadAllText(folder, "scenes/a.ascene").Split("res://vehicles/truck.dat").Length - 1);

        source.Sticky = false;
        source.MovesBeforeFailure = int.MaxValue; // the lock is gone
        MoveReport done = new AssetDatabase(files).Move("res://models/car.dat", "res://vehicles/truck.dat");

        Assert.True(done.Applied);
        Assert.Equal(["res://vehicles/truck.dat.import.xml", "res://imported/vehicles/truck.dat.up"], done.Files);
        Assert.False(File.Exists(Path.Combine(folder.Root, "models", "car.dat.import.xml")));
        Assert.Equal(car, new AssetDatabase(files).UidOf("res://vehicles/truck.dat"));
        Assert.Equal("vehicles/truck.dat", Assert.Single(AssetImporter.LoadIndex(files)).Source);
        Assert.Empty(done.References.Changes);
        Assert.Equal(2, done.References.Unchanged.Count);
    }

    [Fact]
    public void RunningTheSameMoveAgainFinishesOneThatWasInterrupted()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        string scene = SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}"));
        project.Write("scenes/a.ascene", scene);

        // The process died after the documents were written and before the first file moved.
        AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://vehicles/truck.dat");
        Assert.True(project.Has("models/car.dat"));

        MoveReport done = project.Assets.Move("res://models/car.dat", "res://vehicles/truck.dat");

        Assert.Empty(done.References.Changes); // the documents already say it
        Assert.Single(done.References.Unchanged);
        Assert.False(project.Has("models/car.dat"));
        Assert.Equal("car", project.Read("vehicles/truck.dat"));
        Assert.Equal(car, project.Assets.UidOf("res://vehicles/truck.dat"));
        Assert.Equal("vehicles/truck.dat", Assert.Single(AssetImporter.LoadIndex(project.Files)).Source);
        Assert.Contains($"Model=\"res://vehicles/truck.dat|{car}\"", project.Read("scenes/a.ascene"), StringComparison.Ordinal);
    }

    [Fact]
    public void RunningTheSameMoveAgainFinishesOneThatDiedBetweenTheAssetAndItsSidecar()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        AssetUid car = project.Imported("models/car.dat");
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model($"res://models/car.dat|{car}")));
        AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://vehicles/truck.dat");
        Directory.CreateDirectory(Path.Combine(project.Root, "vehicles"));
        File.Move(Path.Combine(project.Root, "models", "car.dat"), Path.Combine(project.Root, "vehicles", "truck.dat")); // only the asset had moved

        MoveReport done = project.Assets.Move("res://models/car.dat", "res://vehicles/truck.dat");

        Assert.Equal(["res://vehicles/truck.dat.import.xml", "res://imported/vehicles/truck.dat.up"], done.Files);
        Assert.False(project.Has("models/car.dat.import.xml"));
        Assert.Equal(car, project.Assets.UidOf("res://vehicles/truck.dat"));
        Assert.Equal("CAR", project.Read("imported/vehicles/truck.dat.up"));
        Assert.Contains($"uid=\"{car}\" path=\"res://vehicles/truck.dat\"", project.Read("imported/assets.xml"), StringComparison.Ordinal);
        Assert.Empty(project.Import().Imported); // nothing imports again

        // Once it is finished, the same call is an ordinary mistake again.
        Assert.Throws<ContentException>(() => project.Assets.Move("res://models/car.dat", "res://vehicles/truck.dat"));
    }

    [Fact]
    public void AMovedFileThatTheGameManifestNamesIsReportedAndNotRewritten()
    {
        using var project = new Project();
        string game = "<Game name=\"G\" organization=\"O\" version=\"1.0.0\">\n  <EntryScene path=\"scenes/main.ascene\"/>\n  <Icon path=\"icon.png\"/>\n</Game>\n";
        project.Write("game.xml", game);
        project.Write("scenes/main.ascene", SceneOf(AssetUid.New()));
        project.Write("scenes/other.ascene", SceneOf(AssetUid.New()));
        project.Write("icon.png", "png");

        MoveReport entry = project.Assets.Move("res://scenes/main.ascene", "res://levels/main.ascene");
        MoveReport icon = project.Assets.Move("res://icon.png", "res://art/icon.png");
        MoveReport unrelated = project.Assets.Move("res://scenes/other.ascene", "res://levels/other.ascene");

        AssetReferenceSite site = Assert.Single(entry.References.Unrewritable);
        Assert.Equal(("res://game.xml", 2, "EntryScene", "path"), (site.Document, site.Line, site.Element, site.Attribute));
        Assert.Equal("res://scenes/main.ascene", site.Value.Path);
        Assert.Contains("res://game.xml:2", Assert.Single(entry.Warnings), StringComparison.Ordinal);
        Assert.Contains("EntryScene", entry.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("res://game.xml:3", Assert.Single(icon.Warnings), StringComparison.Ordinal);
        Assert.Empty(unrelated.Warnings);
        Assert.Equal(game, project.Read("game.xml")); // never rewritten
    }

    [Fact]
    public void AMovedScriptThatAPackageManifestNamesIsReported()
    {
        using var project = new Project();
        project.Write("packages/core/mod.xml", "<Mod id=\"core\" name=\"c\" version=\"1.0.0\" apiVersion=\"1\">\n  <Script path=\"main.luau\"/>\n  <Content path=\"data\"/>\n</Mod>\n");
        project.Write("packages/core/main.luau", "return 1");
        project.Write("packages/core/util.luau", "return 2");

        RewriteReport preview = AssetReferences.Rewrite(project.Assets, "res://packages/core/main.luau", "res://packages/core/ui/main.luau", dryRun: true);
        MoveReport other = project.Assets.Move("res://packages/core/util.luau", "res://packages/core/lib/util.luau");

        AssetReferenceSite site = Assert.Single(preview.Unrewritable);
        Assert.Equal(("res://packages/core/mod.xml", 2, "Script"), (site.Document, site.Line, site.Element));
        Assert.Empty(other.References.Unrewritable);
        Assert.Empty(other.Warnings);
    }

    [Fact]
    public void TextInsideAnElementThatNamesTheAssetIsReportedAndNotRewritten()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        string defs = "<Units>\n  <Unit name=\"car\" mesh=\"res://models/car.dat\">\n    <Model>res://models/car.dat</Model>\n    <Tag>fragile</Tag>\n  </Unit>\n</Units>\n";
        project.Write("data/units.xml", defs);

        MoveReport move = project.Assets.Move("res://models/car.dat", "res://models/truck.dat", ["res://data"]);

        Assert.Single(move.References.Changes); // the attribute is rewritten...
        AssetReferenceSite text = Assert.Single(move.References.Unrewritable); // ...the text of the element is only reported
        Assert.Equal(("res://data/units.xml", 3, "Model", ""), (text.Document, text.Line, text.Element, text.Attribute));
        Assert.Contains("<Model>res://models/car.dat</Model>", project.Read("data/units.xml"), StringComparison.Ordinal);
        Assert.Contains("mesh=\"res://models/truck.dat\"", project.Read("data/units.xml"), StringComparison.Ordinal);
        Assert.Contains("the text of <Model>", Assert.Single(move.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void MoveWarnsWhenOneAssetReadsAnotherByPath()
    {
        using var project = new Project();
        project.Write("textures/t.dat", "texture");
        project.Write("models/m.dep", "model");
        project.Import();
        Assert.Equal(["textures/t.dat"], AssetImporter.LoadIndex(project.Files).Single(e => e.Source == "models/m.dep").Dependencies);

        MoveReport texture = project.Assets.Move("res://textures/t.dat", "res://textures/skin.dat");
        MoveReport model = project.Assets.Move("res://models/m.dep", "res://meshes/m.dep");

        Assert.Contains("models/m.dep reads textures/t.dat", Assert.Single(texture.Warnings), StringComparison.Ordinal);
        Assert.Contains("relative path", Assert.Single(model.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void AMoveInAProjectThatWasNeverImportedStillWorks()
    {
        using var project = new Project();
        project.Write("models/car.dat", "car");
        project.Write("scenes/a.ascene", SceneOf(AssetUid.New(), Model("res://models/car.dat")));

        MoveReport report = project.Assets.Move("res://models/car.dat", "res://models/truck.dat");

        Assert.Equal(["res://models/truck.dat"], report.Files);
        Assert.False(project.Has("imported/assets.xml")); // a move does not make the project's list
        Assert.Contains("Model=\"res://models/truck.dat\"", project.Read("scenes/a.ascene"), StringComparison.Ordinal);
    }

    // ---- The text editor ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheScannerFindsEveryAttributeAndSkipsWhatLooksLikeOne()
    {
        const string text = "<?xml version=\"1.0\"?>\n<!-- <A fake=\"1\"/> -->\n<R a=\"1\" b = '>'>\n  <![CDATA[ <B nope=\"2\"/> ]]>\n  <C c=\"x>y\" d='\"'/>\n  <?pi <D q=\"1\"/> ?>\n</R>\n";

        List<ReferenceText.Span>? spans = ReferenceText.Scan(text);

        Assert.NotNull(spans);
        Assert.Equal([("R", "a"), ("R", "b"), ("C", "c"), ("C", "d")], spans.Select(s => (s.Element, s.Name)));
        Assert.Equal([">", "x>y"], [text[spans[1].ValueStart..spans[1].ValueEnd], text[spans[2].ValueStart..spans[2].ValueEnd]]);
    }

    [Theory]
    [InlineData("<!DOCTYPE a><a/>")]
    [InlineData("<a b=")]
    [InlineData("<a b=\"x>")]
    [InlineData("<a b=c/>")]
    [InlineData("<a")]
    [InlineData("<!-- never closed")]
    [InlineData("<a b\"1\"/>")]
    public void TheScannerRefusesDamagedMarkup(string text) => Assert.Null(ReferenceText.Scan(text));

    [Fact]
    public void TheEditorRefusesWhatItCannotDoSafely()
    {
        // An entity it does not know cannot be compared with the parse, so the value is not touched.
        const string unknown = "<!-- -->\n<a p=\"x&#xZZ;y\"/>";
        Assert.Null(ReferenceText.Decode("x&unknown;y"));
        Assert.Equal("a b\n", ReferenceText.Decode("a\tb&#10;"));
        Assert.Equal("a \"&'<", ReferenceText.Decode("a&#32;&quot;&amp;&apos;&lt;"));
        Assert.Throws<XmlParseException>(() => XmlNode.Parse(unknown));
        Assert.Equal("a&amp;&lt;&quot;'&#10;", ReferenceText.Encode("a&<\"'\n", '"'));
        Assert.Equal("&apos;\"", ReferenceText.Encode("'\"", '\''));
    }

    [Fact]
    public void AnEditThatDoesNotLineUpWithTheParseIsRefused()
    {
        XmlNode tree = XmlNode.Parse("<a p=\"1\"><b q=\"2\"/></a>");

        // The parse says the second attribute is q, the text handed in has them the other way round.
        Assert.Throws<ContentException>(() => ReferenceText.Replace("<a q=\"2\"><b p=\"1\"/></a>", tree, new Dictionary<int, string> { [0] = "x" }, "t"));
        // The text has an attribute the parse does not know.
        Assert.Throws<ContentException>(() => ReferenceText.Replace("<a p=\"1\" z=\"0\"><b q=\"2\"/></a>", tree, new Dictionary<int, string> { [0] = "x" }, "t"));
        // The value in the text is not the value in the parse.
        Assert.Throws<ContentException>(() => ReferenceText.Replace("<a p=\"9\"><b q=\"2\"/></a>", tree, new Dictionary<int, string> { [0] = "x" }, "t"));
        // An attribute that is not there.
        Assert.Throws<ContentException>(() => ReferenceText.Replace("<a p=\"1\"><b q=\"2\"/></a>", tree, new Dictionary<int, string> { [5] = "x" }, "t"));
        // And the good case, for contrast.
        Assert.Equal("<a p=\"x\"><b q=\"2\"/></a>", ReferenceText.Replace("<a p=\"1\"><b q=\"2\"/></a>", tree, new Dictionary<int, string> { [0] = "x" }, "t"));
    }
}
