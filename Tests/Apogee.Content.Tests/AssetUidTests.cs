using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Apogee.Content.Assets;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Core.Assets;
using Xunit;

namespace Apogee.Content.Tests;

/// <summary>UIDs in sidecars and authored files, the import key that ignores them, <c>imported/assets.xml</c> and the <see cref="AssetDatabase"/>.</summary>
public class AssetUidTests
{
    /// <summary>Takes <c>.dat</c> files and makes their text in capitals; no native library involved, so these tests are about UIDs and nothing else.</summary>
    private sealed class DatImporter : IAssetImporter
    {
        public string Name => "dat";

        public IReadOnlyList<string> Extensions { get; } = [".dat"];

        public string Fingerprint => "1";

        public ImportResult? Import(ImportContext context) => new(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(context.Bytes).ToUpperInvariant() + context.Settings.Srgb), ".up");
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

        public string Root => _folder.Root;

        public void Write(string path, string text)
        {
            string full = Path.Combine(_folder.Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        public string Read(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_folder.Root, path))); // keeps a byte order mark

        public bool Has(string path) => File.Exists(Path.Combine(_folder.Root, path));

        public void Move(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_folder.Root, to))!);
            File.Move(Path.Combine(_folder.Root, from), Path.Combine(_folder.Root, to));
        }

        public ImportReport Import(bool force = false) => new AssetImporter(Files, [new DatImporter()]).Run(force);

        public AssetUid UidInSidecar(string asset) => ImportSidecar.ReadUid(Read(asset + ".import.xml"));

        public string KeyOf(string source) => AssetImporter.LoadIndex(Files).Single(e => e.Source == source).Key;

        public void Dispose() => _folder.Dispose();
    }

    private const string Scene = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- a comment -->\n<Scene version=\"1\">\n  <Entity id=\"1\"/>\n</Scene>\n";

    [Fact]
    public void ImportMintsAUidIntoANewSidecar()
    {
        using var project = new Project();
        project.Write("a.dat", "one");

        ImportReport report = project.Import();

        Assert.Equal(["a.dat"], report.Imported);
        AssetUid uid = project.UidInSidecar("a.dat");
        Assert.False(uid.IsNone);
        Assert.Equal($"<Import uid=\"{uid}\"/>\n", project.Read("a.dat.import.xml"));
        Assert.Contains($"uid=\"{uid}\" path=\"res://a.dat\"", project.Read("imported/assets.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void ImportKeepsTheUidOfAnExistingSidecar()
    {
        using var project = new Project();
        AssetUid uid = AssetUid.New();
        string sidecar = $"<!-- mine -->\n<Import   srgb='false'\n  uid=\"{uid}\" />\n";
        project.Write("a.dat", "one");
        project.Write("a.dat.import.xml", sidecar);

        project.Import();
        project.Import(force: true);

        Assert.Equal(sidecar, project.Read("a.dat.import.xml")); // not one character changed
    }

    [Fact]
    public void ASidecarWithoutAUidGetsOneAndKeepsEverythingElse()
    {
        using var project = new Project();
        project.Write("a.dat", "one");
        project.Write("a.dat.import.xml", "\uFEFF<?xml version=\"1.0\"?>\r\n<!-- keep -->\r\n<Import srgb='false' mips=\"false\"\r\n        optimize=\"true\" >\r\n</Import>\r\n");

        project.Import();

        AssetUid uid = project.UidInSidecar("a.dat");
        Assert.False(uid.IsNone);
        Assert.Equal("\uFEFF<?xml version=\"1.0\"?>\r\n<!-- keep -->\r\n<Import srgb='false' mips=\"false\"\r\n        optimize=\"true\" uid=\"" + uid + "\" >\r\n</Import>\r\n", project.Read("a.dat.import.xml"));
    }

    [Theory]
    [InlineData("<Import srgb=\"false\"/>")]
    [InlineData("<Import srgb='false' mips='false'>\n</Import>\n")]
    [InlineData("\uFEFF<?xml version=\"1.0\"?>\r\n<!-- c -->\r\n<Import\r\n    srgb=\"false\"\r\n    mips=\"false\" />\r\n")]
    [InlineData("<Import><Variant><Define name=\"A\"/></Variant></Import>")]
    public void AddingAUidNeverChangesWhatTheKeySees(string sidecar)
    {
        string withUid = ImportSidecar.SetUid(sidecar, AssetUid.New());

        Assert.NotEqual(sidecar, withUid);
        Assert.Equal(sidecar, ImportSettings.KeyText(sidecar)); // the key sees the text as it always did
        Assert.Equal(sidecar, ImportSettings.KeyText(withUid)); // and the same text once the uid is in
    }

    [Fact]
    public void ASidecarThatSaysOnlyItsUidIsTheSameAsNoSidecar()
    {
        Assert.Equal("", ImportSettings.KeyText(""));
        Assert.Equal("", ImportSettings.KeyText($"<Import uid=\"{AssetUid.New()}\"/>\n"));
        Assert.Equal("<Import/>", ImportSettings.KeyText("<Import/>")); // an old, empty sidecar keeps the key it always had
        Assert.Equal("not xml <", ImportSettings.KeyText("not xml <"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MintingAUidDoesNotChangeTheKey(bool withSettings)
    {
        using var project = new Project();
        project.Write("a.dat", "one");
        if (withSettings)
        {
            project.Write("a.dat.import.xml", "<Import srgb=\"false\"/>");
        }

        ImportReport first = project.Import();
        string key = project.KeyOf("a.dat");
        Assert.False(project.UidInSidecar("a.dat").IsNone); // a UID was minted after the key was made...
        ImportReport second = project.Import();

        Assert.Equal(["a.dat"], first.Imported);
        Assert.Empty(second.Imported); // ...and the asset is still up to date
        Assert.Equal(["a.dat"], second.UpToDate);
        Assert.Equal(key, project.KeyOf("a.dat"));
        Assert.Equal(AssetStatus.UpToDate, new AssetImporter(project.Files, [new DatImporter()]).StatusOf("a.dat"));
    }

    [Fact]
    public void AProjectImportedBeforeUidsExistedGetsThemWithoutImportingAgain()
    {
        using var project = new Project();
        project.Write("a.dat", "one");
        project.Write("a.dat.import.xml", "<Import srgb=\"false\"/>");
        project.Import();
        project.Write("a.dat.import.xml", "<Import srgb=\"false\"/>"); // as a project from before UIDs had it, with its key
        File.Delete(Path.Combine(project.Root, "imported", "assets.xml"));

        ImportReport report = project.Import();

        Assert.Empty(report.Imported);
        Assert.False(project.UidInSidecar("a.dat").IsNone);
        Assert.True(project.Has("imported/assets.xml"));
    }

    [Fact]
    public void ChangingASettingStillImportsAgain()
    {
        using var project = new Project();
        project.Write("a.dat", "one");
        project.Import();
        AssetUid uid = project.UidInSidecar("a.dat");

        project.Write("a.dat.import.xml", $"<Import uid=\"{uid}\" srgb=\"false\"/>"); // the uid is ignored, the rest is not
        ImportReport report = project.Import();

        Assert.Equal(["a.dat"], report.Imported);
        Assert.Equal(uid, project.UidInSidecar("a.dat"));
        Assert.Empty(project.Import().Imported);

        project.Write("a.dat.import.xml", $"<Import uid=\"{AssetUid.New()}\" srgb=\"false\"/>"); // only the uid changed: nothing to import
        Assert.Empty(project.Import().Imported);
    }

    [Fact]
    public void ADeletedSidecarGetsItsUidBack()
    {
        using var project = new Project();
        project.Write("a.dat", "one");
        project.Import();
        AssetUid uid = project.UidInSidecar("a.dat");

        File.Delete(Path.Combine(project.Root, "a.dat.import.xml"));
        ImportReport report = project.Import();

        Assert.Equal(["a.dat"], report.UpToDate); // no sidecar and a sidecar that says only its uid are the same to the key
        Assert.Equal(uid, project.UidInSidecar("a.dat"));
        Assert.Empty(report.Reassigned);
    }

    [Fact]
    public void ACopiedAssetGetsANewUidAndTheOriginalKeepsItsOwn()
    {
        using var project = new Project();
        project.Write("models/car.dat", "one");
        project.Import();
        AssetUid original = project.UidInSidecar("models/car.dat");

        // The copy sorts before the original, so "first in order" would be the wrong rule: the list says who had the UID.
        project.Write("a_copy/car.dat", project.Read("models/car.dat"));
        project.Write("a_copy/car.dat.import.xml", project.Read("models/car.dat.import.xml"));
        ImportReport report = project.Import();

        Assert.Equal(original, project.UidInSidecar("models/car.dat"));
        AssetUid copy = project.UidInSidecar("a_copy/car.dat");
        Assert.NotEqual(original, copy);
        Assert.False(copy.IsNone);
        Assert.Equal([("a_copy/car.dat", original, copy)], report.Reassigned);
        Assert.Equal(["a_copy/car.dat"], report.Imported); // the copy is a new asset; giving it another uid imported nothing twice
        Assert.Empty(project.Import().Imported);
        Assert.Empty(project.Import().Reassigned);

        var database = new AssetDatabase(project.Files);
        Assert.Equal("res://models/car.dat", database.PathOf(original));
        Assert.Equal("res://a_copy/car.dat", database.PathOf(copy));
        Assert.Empty(database.Duplicates);
    }

    [Fact]
    public void WithoutAListTheFirstPathKeepsTheUidOfCopies()
    {
        using var project = new Project();
        AssetUid uid = AssetUid.New();
        project.Write("b.dat", "x");
        project.Write("b.dat.import.xml", $"<Import uid=\"{uid}\"/>");
        project.Write("a.dat", "x");
        project.Write("a.dat.import.xml", $"<Import uid=\"{uid}\"/>");

        ImportReport report = project.Import();

        Assert.Equal(uid, project.UidInSidecar("a.dat"));
        Assert.Equal("b.dat", Assert.Single(report.Reassigned).Path);
    }

    [Fact]
    public void ARecoveredUidNeverTakesTheUidOfAFileThatMovedWithIt()
    {
        using var project = new Project();
        project.Write("a.dat", "one");
        project.Import();
        AssetUid uid = project.UidInSidecar("a.dat");

        // a.dat moved to b.dat with its sidecar, and a new file took its place without one: the list still says a.dat had the uid.
        project.Move("a.dat", "b.dat");
        project.Move("a.dat.import.xml", "b.dat.import.xml");
        project.Write("a.dat", "something else");
        ImportReport report = project.Import();

        Assert.Equal(uid, project.UidInSidecar("b.dat"));
        Assert.NotEqual(uid, project.UidInSidecar("a.dat"));

        // a.dat was handed the list's uid first (its sidecar looked deleted), then lost it to the file that has it in a file of its own.
        Assert.Equal("a.dat", Assert.Single(report.Reassigned).Path);
        Assert.Equal("res://b.dat", new AssetDatabase(project.Files).PathOf(uid));
    }

    [Fact]
    public void ARenamedAssetResolvesThroughItsUid()
    {
        using var project = new Project();
        project.Write("models/car.dat", "one");
        project.Import();
        AssetRef reference = new AssetDatabase(project.Files).RefOf("res://models/car.dat");
        Assert.False(reference.Uid.IsNone);

        project.Move("models/car.dat", "vehicles/truck.dat");
        project.Move("models/car.dat.import.xml", "vehicles/truck.dat.import.xml");

        // The list still names the old file (nothing was imported since); the database notices that it is gone and looks at the files.
        var database = new AssetDatabase(project.Files);
        Assert.Equal("res://vehicles/truck.dat", database.Resolve(reference));
        Assert.Equal("res://vehicles/truck.dat", database.PathOf(reference.Uid));
        Assert.Equal(reference.Uid, database.UidOf("res://vehicles/truck.dat"));

        // The same when there is no list at all, and when only the UID is known.
        File.Delete(Path.Combine(project.Root, "imported", "assets.xml"));
        var withoutList = new AssetDatabase(project.Files);
        Assert.Equal("res://vehicles/truck.dat", withoutList.Resolve(AssetRef.FromUid(reference.Uid)));
        Assert.Null(withoutList.Resolve(new AssetRef("res://models/car.dat", AssetUid.New())));
        Assert.Null(withoutList.Resolve(default));
    }

    [Fact]
    public void ThePathWinsWhileTheFileIsThere()
    {
        using var project = new Project();
        project.Write("a.dat", "one");
        project.Write("b.dat", "two");
        project.Import();
        AssetUid other = project.UidInSidecar("b.dat");
        var database = new AssetDatabase(project.Files);

        Assert.Equal("res://a.dat", database.Resolve(new AssetRef("res://a.dat", other)));
        Assert.Equal("res://a.dat", database.Resolve(new AssetRef("res://x/../a.dat", AssetUid.None)));
        Assert.Null(database.Resolve(new AssetRef("not-a-virtual-path", AssetUid.None)));
    }

    [Fact]
    public void AnAuthoredFileGetsAUidInItsRootAndKeepsItsComments()
    {
        using var project = new Project();
        project.Write("scenes/start.ascene", Scene);
        project.Write("scenes/old.ascene", "{\"format\":\"apogee-scene\",\"version\":1}"); // not XML: nowhere to put a UID, left alone

        ImportReport report = project.Import();

        Assert.True(report.Success);
        string text = project.Read("scenes/start.ascene");
        AssetUid uid = AuthoredAssets.ReadUid(Encoding.UTF8.GetBytes(text));
        Assert.False(uid.IsNone);
        Assert.Equal(Scene.Replace("<Scene version=\"1\">", $"<Scene version=\"1\" uid=\"{uid}\">", StringComparison.Ordinal), text);
        Assert.Equal("{\"format\":\"apogee-scene\",\"version\":1}", project.Read("scenes/old.ascene"));

        project.Import(); // and nothing changes after
        Assert.Equal(text, project.Read("scenes/start.ascene"));
        var database = new AssetDatabase(project.Files);
        Assert.Equal(uid, database.UidOf("res://scenes/start.ascene"));
        Assert.Equal(AssetUid.None, database.UidOf("res://scenes/old.ascene"));
        Assert.Equal("res://scenes/start.ascene", database.PathOf(uid));
    }

    [Fact]
    public void AnAuthoredFilesUidIsKeptWhenItIsOverwrittenWithText()
    {
        AssetUid uid = AssetUid.New();
        string first = AuthoredAssets.SetUid(Scene, uid);

        Assert.Equal(uid, AuthoredAssets.ReadUid(Encoding.UTF8.GetBytes(first)));
        AssetUid next = AssetUid.New();
        string replaced = AuthoredAssets.SetUid(first, next);
        Assert.Equal(first.Replace(uid.ToString(), next.ToString(), StringComparison.Ordinal), replaced);
        Assert.Throws<ContentException>(() => AuthoredAssets.SetUid("{ \"json\": true }", uid));
        Assert.Throws<ContentException>(() => AuthoredAssets.SetUid("<Scene version=\"1\"", uid)); // a damaged tag is not edited
        Assert.Equal(AssetUid.None, AuthoredAssets.ReadUid("{ }"u8));
    }

    [Fact]
    public void AUidUriResolvesToItsPath()
    {
        using var project = new Project();
        project.Write("a.dat", "one");
        project.Import();
        AssetUid uid = project.UidInSidecar("a.dat");
        var loader = new ContentLoader(project.Files);

        Assert.Equal("res://a.dat", loader.ResolveAssetPath(uid.ToString()));
        Assert.Equal("res://a.dat", loader.ResolveAssetPath(uid.ToString().ToUpperInvariant()));
        Assert.Equal("res://x/y.png", loader.ResolveAssetPath("res://x/y.png")); // a path is returned as it is
    }

    [Fact]
    public void AnUnknownUidIsAContentError()
    {
        using var project = new Project();
        project.Write("a.dat", "one");
        project.Import();
        var database = new AssetDatabase(project.Files);

        ContentException unknown = Assert.Throws<ContentException>(() => database.ResolvePath("uid://0000000000001"));
        Assert.Contains("Unknown uid uid://0000000000001", unknown.Message, StringComparison.Ordinal);
        Assert.Throws<ContentException>(() => database.ResolvePath("uid://nonsense"));
    }

    [Fact]
    public void AReadOnlyRootIsNeverWritten()
    {
        using var project = new Project();
        project.Write("a.dat", "one");
        project.Import();
        AssetUid uid = project.UidInSidecar("a.dat");
        project.Write("copy.dat", "one");
        project.Write("copy.dat.import.xml", project.Read("a.dat.import.xml")); // a duplicate that the database can only report
        project.Write("plain.dat", "no sidecar");
        project.Write("scenes/s.ascene", Scene);
        Dictionary<string, string> before = Snapshot(project.Root);

        var readOnly = new VirtualFileSystem();
        readOnly.Mount("res://", new DirectorySource(project.Root));
        var database = new AssetDatabase(readOnly);

        Assert.Equal(uid, database.UidOf("res://a.dat"));
        Assert.Equal(AssetUid.None, database.UidOf("res://plain.dat")); // no UID, and none is made
        Assert.Equal(AssetUid.None, database.UidOf("res://scenes/s.ascene"));
        Assert.Equal("res://a.dat", database.PathOf(uid)); // the list's keeper
        (AssetUid duplicate, IReadOnlyList<string> paths) = Assert.Single(database.Duplicates);
        Assert.Equal(uid, duplicate);
        Assert.Equal(["res://a.dat", "res://copy.dat"], paths);
        Assert.Throws<UnauthorizedAccessException>(() => new AssetImporter(readOnly, [new DatImporter()]).Run());

        Assert.Equal(before, Snapshot(project.Root));
    }

    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(root, p), File.ReadAllText);

    [Fact]
    public void AModFileIsFoundUnderItsModPath()
    {
        using var project = new Project();
        project.Write("packages/cool/tex.dat", "one");
        project.Import();
        AssetUid uid = project.UidInSidecar("packages/cool/tex.dat");
        project.Files.Mount("mod://cool", new VfsSubtreeSource(project.Files, "res://packages/cool"));

        var alone = new AssetDatabase(project.Files);
        Assert.Equal("res://packages/cool/tex.dat", alone.PathOf(uid)); // mod://cool is not a root of this database

        var database = new AssetDatabase(project.Files) { Roots = { "mod://cool/" } };
        Assert.Equal("mod://cool/tex.dat", database.PathOf(uid));
        Assert.Equal(uid, database.UidOf("mod://cool/tex.dat"));
        Assert.Equal("res://packages/cool/tex.dat", database.Resolve(new AssetRef("res://packages/cool/tex.dat", uid))); // the path is still there

        // Without the list the scan finds it once, under the address the engine uses, and does not report it twice.
        File.Delete(Path.Combine(project.Root, "imported", "assets.xml"));
        database.Refresh();
        Assert.Equal("mod://cool/tex.dat", database.PathOf(uid));
        Assert.Empty(database.Duplicates);
    }

    [Fact]
    public void APackageImportedOnItsOwnIsFoundWhereItIsMounted()
    {
        using var mod = new Project();
        mod.Write("tex.dat", "one");
        mod.Import(); // its list says res://tex.dat: that is how the folder looked when it was imported
        AssetUid uid = mod.UidInSidecar("tex.dat");

        var game = new VirtualFileSystem();
        game.Mount("mod://solo", new DirectorySource(mod.Root));
        var database = new AssetDatabase(game);
        database.Roots.Clear();
        database.Roots.Add("mod://solo/");

        Assert.Equal("mod://solo/tex.dat", database.PathOf(uid));
        Assert.Equal(uid, database.UidOf("mod://solo/tex.dat"));
    }

    [Fact]
    public void TheFirstRootInLoadOrderWinsAUidTwoMountsShare()
    {
        using var first = new Project();
        using var second = new Project();
        AssetUid uid = AssetUid.New();
        first.Write("x.dat", "1");
        first.Write("x.dat.import.xml", $"<Import uid=\"{uid}\"/>");
        second.Write("y.dat", "2");
        second.Write("y.dat.import.xml", $"<Import uid=\"{uid}\"/>");
        var game = new VirtualFileSystem();
        game.Mount("mod://one", new DirectorySource(first.Root));
        game.Mount("mod://two", new DirectorySource(second.Root));
        var database = new AssetDatabase(game);
        database.Roots.Clear();
        database.Roots.Add("mod://one/");
        database.Roots.Add("mod://two/");

        Assert.Equal("mod://one/x.dat", database.PathOf(uid));
        Assert.Equal(["mod://one/x.dat", "mod://two/y.dat"], Assert.Single(database.Duplicates).Paths);
    }

    [Fact]
    public void TheCacheAndHiddenFoldersAreNotScanned()
    {
        using var project = new Project();
        AssetUid uid = AssetUid.New();
        project.Write("imported/x.dat.import.xml", $"<Import uid=\"{uid}\"/>");
        project.Write("imported/x.dat", "1");
        project.Write(".git/y.dat.import.xml", $"<Import uid=\"{uid}\"/>");
        project.Write(".git/y.dat", "1");

        Assert.Null(new AssetDatabase(project.Files).PathOf(uid));
    }

    [Fact]
    public void AssetsXmlRoundTrips()
    {
        using var project = new Project();
        AssetUid a = AssetUid.New();
        AssetUid b = AssetUid.New();
        var rows = new Dictionary<string, AssetUid> { ["res://models/b.glb"] = b, ["res://a.png"] = a, ["res://none.png"] = AssetUid.None };

        AssetDatabase.Save(project.Files, rows);
        string text = project.Read("imported/assets.xml");
        Dictionary<string, AssetUid> back = AssetDatabase.Load(project.Files);

        Assert.Equal(new Dictionary<string, AssetUid> { ["res://a.png"] = a, ["res://models/b.glb"] = b }, back); // a row without a uid is not written
        Assert.True(text.IndexOf("a.png", StringComparison.Ordinal) < text.IndexOf("b.glb", StringComparison.Ordinal)); // sorted by path
        Assert.Contains("<Assets version=\"1\">", text, StringComparison.Ordinal);

        DateTime written = File.GetLastWriteTimeUtc(Path.Combine(project.Root, "imported", "assets.xml"));
        AssetDatabase.Save(project.Files, rows.Reverse());
        Assert.Equal(text, project.Read("imported/assets.xml")); // the same assets make the same file
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(project.Root, "imported", "assets.xml"))); // which is not written again

        project.Write("imported/assets.xml", "<Assets version=\"1\"><Asset uid=\"junk\" path=\"res://x\"/><Asset uid=\"" + a + "\"/><Asset uid=\"" + b + "\" path=\"res://ok.png\"/></Assets>");
        Assert.Equal(new Dictionary<string, AssetUid> { ["res://ok.png"] = b }, AssetDatabase.Load(project.Files)); // bad rows are skipped
        project.Write("imported/assets.xml", "<Assets version=\"2\"><Asset uid=\"" + a + "\" path=\"res://x\"/></Assets>");
        Assert.Empty(AssetDatabase.Load(project.Files)); // a newer list is not guessed at
        project.Write("imported/assets.xml", "<Assets");
        Assert.Empty(AssetDatabase.Load(project.Files));
    }

    [Fact]
    public void ReimportingOneAssetUpdatesOnlyItsRow()
    {
        using var project = new Project();
        project.Write("a.dat", "one");
        project.Write("b.dat", "two");
        project.Import();
        AssetUid a = project.UidInSidecar("a.dat");
        string list = project.Read("imported/assets.xml");

        ImportReport report = new AssetImporter(project.Files, [new DatImporter()]).Reimport("b.dat");

        Assert.Equal(["b.dat"], report.Imported);
        Assert.Equal(list, project.Read("imported/assets.xml"));
        File.Delete(Path.Combine(project.Root, "b.dat.import.xml"));
        new AssetImporter(project.Files, [new DatImporter()]).Reimport("b.dat"); // the sidecar is made again, with the uid the list had
        Assert.Equal(list, project.Read("imported/assets.xml"));
        Assert.Equal(a, new AssetDatabase(project.Files).UidOf("res://a.dat"));
    }

    [Fact]
    public void AnImportWithoutUidsToReportHasNoReassignedAssets()
    {
        var report = new ImportReport([], [], [], []);

        Assert.Empty(report.Reassigned);
    }
}
