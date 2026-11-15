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

        RewriteReport report = AssetReferences.Rewrite(project.Assets, "res://models/car.dat", "res://models/truck.dat");

        Assert.Equal(["res://scenes/good.ascene"], report.Documents);
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

    /// <summary>A folder source that fails when told to: the Nth write, or the Nth move, throws (once).</summary>
    private sealed class FlakySource(DirectorySource inner) : IFileSource
    {
        public int WritesBeforeFailure { get; set; } = int.MaxValue;

        public int MovesBeforeFailure { get; set; } = int.MaxValue;

        public string Description => inner.Description;

        public bool CanWrite => true;

        public FileKind Kind(string path) => inner.Kind(path);

        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);

        public Stream OpenRead(string path) => inner.OpenRead(path);

        public void List(string directory, List<(string Name, FileKind Kind)> into) => inner.List(directory, into);

        public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
        {
            if (WritesBeforeFailure-- <= 0)
            {
                WritesBeforeFailure = int.MaxValue; // it fails once, and the repair that follows works
                throw new IOException("the disk is full");
            }

            inner.WriteAllBytes(path, bytes);
        }

        public void Delete(string path) => inner.Delete(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void Move(string source, string target)
        {
            if (MovesBeforeFailure-- <= 0)
            {
                MovesBeforeFailure = int.MaxValue;
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
