using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Apogee.Content.Assets;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Content.Packs;
using Apogee.Content.Xml;
using Apogee.Core.Assets;
using Xunit;

namespace Apogee.Content.Tests;

/// <summary><see cref="GamePacker"/>: what a pack of a game folder holds, plain and cooked, and the patch packs over it.</summary>
public class GamePackerTests
{
    /// <summary>Takes <c>.dat</c> files and makes their text in capitals; no native library involved, so these tests are about packs and nothing else.</summary>
    private sealed class DatImporter : IAssetImporter
    {
        public string Name => "dat";

        public IReadOnlyList<string> Extensions { get; } = [".dat"];

        public string Fingerprint => "1";

        public ImportResult? Import(ImportContext context) => new(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(context.Bytes).ToUpperInvariant()), ".up");
    }

    private const string Scene = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- kept in the source, not in the pack -->\n<Scene version=\"1\">\n  <Entity id=\"1\" name=\"ground\">\n    <Transform Position=\"0 -0.5 0\"/>\n    <Entity id=\"2\"/>\n  </Entity>\n</Scene>\n";

    private sealed class Game : IDisposable
    {
        private readonly TempFolder _folder = new();

        public Game()
        {
            Files = new VirtualFileSystem();
            Files.Mount("res://", new DirectorySource(Root, writable: true));
        }

        public VirtualFileSystem Files { get; }

        public string Root => Path.Combine(_folder.Root, "game");

        public string Out(string name) => Path.Combine(_folder.Root, "out", name);

        public void Write(string path, string text)
        {
            string full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        public ImportReport Import() => new AssetImporter(Files, [new DatImporter()]).Run();

        /// <summary>A game with a model-like asset, a scene, a script and a dot-folder.</summary>
        public void Populate()
        {
            Write("models/a.dat", "one");
            Write("scenes/start.ascene", Scene);
            Write("packages/core/main.luau", "print('hi')");
            Write(".git/config", "left out");
        }

        public PackRequest Request(string name = "game.pack", bool cooked = false) => new() { Folder = Root, Output = Out(name), Cooked = cooked };

        public void Dispose() => _folder.Dispose();
    }

    private static VirtualFileSystem Mount(PackSource pack)
    {
        var files = new VirtualFileSystem();
        files.Mount("res://", pack);
        return files;
    }

    private static List<string> Listed(PackSource pack) => [.. Mount(pack).EnumerateFiles("res://", recursive: true).Select(p => p["res://".Length..])];

    [Fact]
    public void APlainPackHoldsEveryFile()
    {
        using var game = new Game();
        game.Populate();
        game.Import();

        PackReport report = GamePacker.Pack(game.Request());

        Assert.Equal(game.Out("game.pack"), Assert.Single(report.Parts));
        Assert.Equal(0, report.CookedDocuments);
        Assert.Equal((report.Added, 0, 0), (Listed(PackSource.Open(report.Parts[0])).Count, report.Unchanged, report.Removed));
        using var pack = PackSource.Open(report.Parts[0]);
        List<string> paths = Listed(pack);
        Assert.Contains("models/a.dat", paths);
        Assert.Contains("models/a.dat.import.xml", paths);
        Assert.Contains("imported/index.xml", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith(".git", StringComparison.Ordinal)); // names that start with a dot are left out
        Assert.Equal(paths, GamePacker.PathsOf(game.Request()));
        Assert.Equal(File.ReadAllText(Path.Combine(game.Root, "scenes", "start.ascene")), Encoding.UTF8.GetString(Mount(pack).ReadAllBytes("res://scenes/start.ascene"))); // plain: the text as it is
        Assert.True(report.PackedBytes > 0 && report.RawBytes > 0);
    }

    [Fact]
    public void ACookedPackDropsSourcesAndKeepsArtifacts()
    {
        using var game = new Game();
        game.Populate();
        game.Import();
        ImportEntry entry = Assert.Single(AssetImporter.LoadIndex(game.Files));

        PackReport report = GamePacker.Pack(game.Request(cooked: true));

        using var pack = PackSource.Open(report.Parts[0]);
        List<string> paths = Listed(pack);
        Assert.DoesNotContain("models/a.dat", paths);
        Assert.DoesNotContain("models/a.dat.import.xml", paths);
        Assert.Contains(entry.Artifact, paths);
        Assert.Contains("imported/index.xml", paths);
        Assert.Contains("packages/core/main.luau", paths);
        Assert.Equal(paths, GamePacker.PathsOf(game.Request(cooked: true)));
        Assert.Equal("ONE", Encoding.UTF8.GetString(Mount(pack).ReadAllBytes("res://" + entry.Artifact)));
    }

    [Fact]
    public void ACookedPackHoldsAuthoredDocumentsAsBinaryAtTheirPaths()
    {
        using var game = new Game();
        game.Populate();
        game.Write("dev_mods/tool/scenes/mine.ascene", Scene);
        game.Write("notes.xml", "<Notes/>");
        game.Import();

        PackReport report = GamePacker.Pack(game.Request(cooked: true));

        using var pack = PackSource.Open(report.Parts[0]);
        VirtualFileSystem files = Mount(pack);
        Assert.True(XmlBinary.IsBinary(files.ReadAllBytes("res://scenes/start.ascene")));
        Assert.True(XmlBinary.IsBinary(files.ReadAllBytes("res://imported/assets.xml")));
        Assert.Equal(2, report.CookedDocuments);
        Assert.Equal(File.ReadAllText(Path.Combine(game.Root, "dev_mods", "tool", "scenes", "mine.ascene")), Encoding.UTF8.GetString(files.ReadAllBytes("res://dev_mods/tool/scenes/mine.ascene"))); // dev mods are never cooked
        Assert.Equal("<Notes/>", files.ReadAllText("res://notes.xml")); // other XML is left as it is
        Assert.StartsWith("<Imports", files.ReadAllText("res://imported/index.xml"), StringComparison.Ordinal); // the index stays text
    }

    [Fact]
    public void ACookedDocumentReadsBackEqualToItsSource()
    {
        using var game = new Game();
        game.Populate();
        game.Import(); // gives the scene a uid
        string source = File.ReadAllText(Path.Combine(game.Root, "scenes", "start.ascene"));

        PackReport report = GamePacker.Pack(game.Request(cooked: true));

        using var pack = PackSource.Open(report.Parts[0]);
        XmlNode cooked = XmlFile.Read(Mount(pack), "res://scenes/start.ascene");
        XmlNode text = XmlNode.Parse(source);
        Assert.Equal(text.ToXml(), cooked.ToXml());
        AssertSame(text, cooked);
        Assert.NotEqual(AssetUid.None, AuthoredAssets.ReadUid(Mount(pack).ReadAllBytes("res://scenes/start.ascene"))); // the uid is still in the root
    }

    private static void AssertSame(XmlNode expected, XmlNode actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Line, actual.Line);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Attributes, actual.Attributes);
        Assert.Equal(expected.Children.Count, actual.Children.Count);
        for (int i = 0; i < expected.Children.Count; i++)
        {
            AssertSame(expected.Children[i], actual.Children[i]);
        }
    }

    [Fact]
    public void ACookedPackKeepsTheUidsOfDroppedSidecars()
    {
        using var game = new Game();
        game.Populate();
        game.Import();
        AssetUid assetUid = ImportSidecar.ReadUid(File.ReadAllText(Path.Combine(game.Root, "models", "a.dat.import.xml")));
        AssetUid sceneUid = AuthoredAssets.ReadUid(File.ReadAllBytes(Path.Combine(game.Root, "scenes", "start.ascene")));
        Assert.False(assetUid.IsNone || sceneUid.IsNone);

        PackReport report = GamePacker.Pack(game.Request(cooked: true));

        using var pack = PackSource.Open(report.Parts[0]);
        VirtualFileSystem files = Mount(pack);
        Assert.False(files.FileExists("res://models/a.dat.import.xml"));
        var database = new AssetDatabase(files);
        Assert.Equal("res://models/a.dat", database.PathOf(assetUid)); // from the cooked list: the sidecar and the source are not there
        Assert.Equal("res://scenes/start.ascene", database.PathOf(sceneUid));
        Assert.Equal("res://models/a.dat", database.ResolvePath(assetUid.ToString()));
    }

    [Fact]
    public void ACookedPackResolvesAPathOnlyReferenceToAnImportedAssetWhoseSourceIsNotThere()
    {
        using var game = new Game();
        game.Populate();
        game.Import();
        PackReport report = GamePacker.Pack(game.Request(cooked: true));

        using var pack = PackSource.Open(report.Parts[0]);
        VirtualFileSystem files = Mount(pack);
        Assert.False(files.FileExists("res://models/a.dat")); // the source stayed out of the pack
        var database = new AssetDatabase(files);
        Assert.Equal("res://models/a.dat", database.Resolve(AssetRef.FromPath("res://models/a.dat")));
        Assert.Null(database.Resolve(AssetRef.FromPath("res://models/never.dat"))); // only what the index names
        Assert.Null(database.Resolve(AssetRef.FromPath("res://models/a.dat.up")));

        // The folder before cooking is not a cooked root: a source that is gone there is gone.
        File.Delete(Path.Combine(game.Root, "models", "a.dat"));
        Assert.Null(new AssetDatabase(game.Files).Resolve(AssetRef.FromPath("res://models/a.dat")));
    }

    [Fact]
    public void ExcludedPrefixesAreLeftOut()
    {
        using var game = new Game();
        game.Populate();
        game.Write("dev_mods/tool/mod.xml", "<Mod/>");
        game.Write("dev_modsx/keep.txt", "kept: the prefix needs its slash to stop at the folder");
        game.Write("data/old.pack", "x");
        game.Write("game.xml", "<Game/>");
        game.Import();
        PackRequest request = game.Request() with { Exclude = ["dev_mods/", "data\\", "game.xml"] };

        PackReport report = GamePacker.Pack(request);

        using var pack = PackSource.Open(report.Parts[0]);
        List<string> paths = Listed(pack);
        Assert.DoesNotContain(paths, p => p.StartsWith("dev_mods/", StringComparison.Ordinal) || p.StartsWith("data/", StringComparison.Ordinal) || p == "game.xml");
        Assert.Contains("dev_modsx/keep.txt", paths);
        Assert.Equal(paths, GamePacker.PathsOf(request));
        Assert.Throws<ArgumentException>(() => GamePacker.PathsOf(game.Request() with { Exclude = [""] }));
    }

    [Fact]
    public void CookedWithoutAnImportIsAContentError()
    {
        using var game = new Game();
        game.Populate();

        ContentException error = Assert.Throws<ContentException>(() => GamePacker.Pack(game.Request(cooked: true)));
        Assert.Contains("nothing imported", error.Message, StringComparison.Ordinal);
        Assert.Throws<ContentException>(() => GamePacker.PathsOf(game.Request(cooked: true)));
        Assert.False(File.Exists(game.Out("game.pack")));
        Assert.Throws<ContentException>(() => GamePacker.Pack(game.Request() with { Folder = Path.Combine(game.Root, "nope") }));
    }

    [Fact]
    public void NothingToCookIsNotAnErrorWhenAsked()
    {
        using var game = new Game();
        game.Write("packages/core/main.luau", "print('hi')");
        game.Write("scenes/start.ascene", Scene);
        PackRequest request = game.Request(cooked: true) with { AllowNothingImported = true };

        PackReport report = GamePacker.Pack(request);

        using var pack = PackSource.Open(report.Parts[0]);
        Assert.Equal(["packages/core/main.luau", "scenes/start.ascene"], Listed(pack));
        Assert.True(XmlBinary.IsBinary(Mount(pack).ReadAllBytes("res://scenes/start.ascene"))); // the scene is cooked all the same
        Assert.Equal(1, report.CookedDocuments);
        Assert.Equal(["packages/core/main.luau", "scenes/start.ascene"], GamePacker.PathsOf(request));
    }

    [Fact]
    public void ADocumentThatDoesNotParseIsAContentError()
    {
        using var game = new Game();
        game.Populate();
        game.Import();
        game.Write("scenes/broken.ascene", "{\"format\":\"apogee-scene\"}");

        ContentException error = Assert.ThrowsAny<ContentException>(() => GamePacker.Pack(game.Request(cooked: true)));
        Assert.Contains("broken.ascene", error.Message, StringComparison.Ordinal);
        Assert.Equal(["imported/assets.xml", "imported/index.xml"], GamePacker.PathsOf(game.Request(cooked: true)).Where(p => p.StartsWith("imported/", StringComparison.Ordinal) && !p.EndsWith(".up", StringComparison.Ordinal)).ToArray()); // PathsOf does not read documents
        Assert.Contains("scenes/broken.ascene", GamePacker.PathsOf(game.Request(cooked: true)));
    }

    [Fact]
    public void APathThatCannotBeInAPackIsAContentErrorNamingIt()
    {
        using var game = new Game();
        game.Write("good.txt", "x");
        game.Write("bad|name.txt", "x");
        if (!File.Exists(Path.Combine(game.Root, "bad|name.txt")))
        {
            return;
        }

        ContentException error = Assert.Throws<ContentException>(() => GamePacker.Pack(game.Request()));
        Assert.Contains("bad|name.txt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APatchPackOverACookedBaseHoldsOnlyChanges()
    {
        using var game = new Game();
        game.Populate();
        game.Write("old.txt", "to be removed");
        game.Import();
        PackReport first = GamePacker.Pack(game.Request("game.pack", cooked: true));

        // Nothing changed: the cooked documents are the same bytes, so the patch is empty.
        PackReport same = GamePacker.Pack(game.Request("same.pack", cooked: true) with { Bases = [first.Parts[0]] });
        Assert.Equal((0, first.Added, 0, 0), (same.Added, same.Unchanged, same.Removed, same.CookedDocuments));

        game.Write("scenes/start.ascene", Scene.Replace("ground", "floor", StringComparison.Ordinal));
        File.Delete(Path.Combine(game.Root, "old.txt"));
        game.Import(); // the changed scene keeps no uid of its own, so it gets a new one
        PackReport patch = GamePacker.Pack(game.Request("patch.pack", cooked: true) with { Bases = [first.Parts[0]] });

        Assert.Equal((1, 1), (patch.Removed, patch.CookedDocuments)); // the scene (it keeps its uid: the list remembered it)
        using var pack = PackSource.Open(patch.Parts[0]);
        Assert.Equal(["scenes/start.ascene"], Listed(pack).Where(p => p != "old.txt"));
        Assert.Equal(FileKind.Removed, ((IFileSource)pack).Kind("old.txt"));
        Assert.True(XmlBinary.IsBinary(Mount(pack).ReadAllBytes("res://scenes/start.ascene")));
    }

    [Fact]
    public void PackingTwiceMakesTheSameBytesAndAnEncryptedPackReadsBack()
    {
        using var game = new Game();
        game.Populate();
        game.Import();

        PackReport a = GamePacker.Pack(game.Request("a.pack", cooked: true));
        PackReport b = GamePacker.Pack(game.Request("b.pack", cooked: true));
        using (PackSource packA = PackSource.Open(a.Parts[0]))
        using (PackSource packB = PackSource.Open(b.Parts[0])) // deterministic: cooking adds nothing random
        {
            List<string> paths = Listed(packA);
            Assert.Equal(paths, Listed(packB));
            foreach (string path in paths)
            {
                Assert.Equal(Mount(packA).ReadAllBytes("res://" + path), Mount(packB).ReadAllBytes("res://" + path));
            }
        }


        PackKey key = PackKey.Parse("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        PackReport sealedPack = GamePacker.Pack(game.Request("sealed.pack", cooked: true) with { Key = key, CompressionLevel = 3, SplitBytes = 1024 });
        using var pack = PackSource.Open(sealedPack.Parts[0], key);
        Assert.True(pack.IsEncrypted);
        Assert.True(XmlBinary.IsBinary(Mount(pack).ReadAllBytes("res://scenes/start.ascene")));
        Assert.True(sealedPack.Parts.Count > 1);
    }
}
