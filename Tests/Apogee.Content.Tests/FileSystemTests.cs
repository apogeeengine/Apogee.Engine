using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Apogee.Content.FileSystem;
using Xunit;

namespace Apogee.Content.Tests;

public class VfsPathTests
{
    [Theory]
    [InlineData("res://a/b.txt", "res://a/b.txt")]
    [InlineData("res://a/b/", "res://a/b")]
    [InlineData("res://", "res://")]
    [InlineData("user://saves/slot_1.xml", "user://saves/slot_1.xml")]
    [InlineData("mod://my_mod/ui/main.luau", "mod://my_mod/ui/main.luau")]
    [InlineData("res://.luaurc", "res://.luaurc")]
    public void AValidPathIsCanonical(string path, string expected) => Assert.Equal(expected, VfsPath.Parse(path));

    [Theory]
    [InlineData("a/b.txt")]                 // no scheme
    [InlineData("/etc/passwd")]             // absolute
    [InlineData("C:/Windows/win.ini")]      // a drive is not a scheme
    [InlineData("RES://a")]                 // schemes are lowercase
    [InlineData("res://a/../b")]
    [InlineData("res://../b")]
    [InlineData("res://a/./b")]
    [InlineData("res://a//b")]
    [InlineData("res://a\\b")]
    [InlineData("res://a:stream")]          // an NTFS alternate data stream
    [InlineData("res://a/b.")]              // Windows drops a trailing dot
    [InlineData("res://a/b ")]
    [InlineData("res://nul")]               // a device name on Windows
    [InlineData("res://CON.txt")]
    [InlineData("res://a/*.png")]
    [InlineData("res://a/b?")]
    [InlineData("res://a/\u0001")]
    public void ANamedPathThatCouldMeanSomethingElseSomewhereIsRefused(string path)
    {
        Assert.False(VfsPath.TryParse(path, out _, out string? problem));
        Assert.NotNull(problem);
        Assert.Throws<VfsException>(() => VfsPath.Parse(path));
    }

    [Fact]
    public void OnlyASchemeOfLowercaseLettersDigitsAndUnderscoresIsOne()
    {
        Assert.True(VfsPath.IsVirtual("res://x"));
        Assert.True(VfsPath.IsVirtual("my_scheme2://x"));
        Assert.False(VfsPath.IsVirtual("C://x"));
        Assert.False(VfsPath.IsVirtual("2fast://x"));
        Assert.False(VfsPath.IsVirtual("http:/x"));
        Assert.False(VfsPath.IsVirtual("plain/path"));
        Assert.False(VfsPath.IsVirtual("res:"));
    }

    [Fact]
    public void NormalizeResolvesDotsAndStopsAtTheRootOfTheScheme()
    {
        Assert.Equal("res://b", VfsPath.Normalize("res://a/../b"));
        Assert.Equal("res://b", VfsPath.Normalize("res://a/../../../b"));
        Assert.Equal("res://a/c", VfsPath.Normalize("res://a/./b/../c/"));
        Assert.Equal("res://", VfsPath.Normalize("res://.."));
        Assert.Equal("res://a/b", VfsPath.Normalize("res://a\\b"));
    }

    [Fact]
    public void PlainPathsKeepTheirBehaviour()
    {
        Assert.Equal("/a/c", VfsPath.Normalize("/a/b/../c/"));
        Assert.Equal("a/c", VfsPath.Normalize("a\\b\\..\\c"));
        Assert.Equal("/a", VfsPath.DirectoryOf("/a/b.luau"));
        Assert.Equal("/", VfsPath.DirectoryOf("/b.luau"));
        Assert.Null(VfsPath.ParentOf("/"));
        Assert.Equal("/a", VfsPath.ParentOf("/a/b"));
        Assert.Equal("/a/b", VfsPath.Combine("/a", "b"));
        Assert.Equal("/b", VfsPath.Combine("/a", "/b"));
    }

    [Fact]
    public void DirectoriesAndCombiningWorkOnVirtualPaths()
    {
        Assert.Equal("res://a", VfsPath.DirectoryOf("res://a/b.txt"));
        Assert.Equal("res://", VfsPath.DirectoryOf("res://b.txt"));
        Assert.Equal("res://", VfsPath.DirectoryOf("res://"));
        Assert.Null(VfsPath.ParentOf("res://"));
        Assert.Equal("res://", VfsPath.ParentOf("res://a"));
        Assert.Equal("res://a", VfsPath.ParentOf("res://a/b"));
        Assert.Equal("res://a/b", VfsPath.Combine("res://a", "b"));
        Assert.Equal("res://b", VfsPath.Combine("res://", "b"));
        Assert.Equal("res://b", VfsPath.Combine("res://a", "/b"));
        Assert.Equal("user://x", VfsPath.Combine("res://a", "user://x"));
        Assert.Equal("b.txt", VfsPath.FileName("res://a/b.txt"));
    }

    [Fact]
    public void IsUnderMeansTheSameFolderOrBelowNotAShareOfTheName()
    {
        Assert.True(VfsPath.IsUnder("res://a/b", "res://a"));
        Assert.True(VfsPath.IsUnder("res://a", "res://a"));
        Assert.True(VfsPath.IsUnder("res://a", "res://"));
        Assert.False(VfsPath.IsUnder("res://ab", "res://a"));
        Assert.False(VfsPath.IsUnder("user://a", "res://"));
    }
}

/// <summary>A folder of files for a test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "apogee-vfs-" + Guid.NewGuid().ToString("N"));

    public string Write(string relative, string text)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a failure.
        }
    }
}

public class DirectorySourceTests
{
    [Fact]
    public void ItReadsListsAndReportsWhatIsThere()
    {
        using var folder = new TempFolder();
        folder.Write("a.txt", "hello");
        folder.Write("sub/b.txt", "bee");
        var source = new DirectorySource(folder.Root);

        Assert.Equal(FileKind.File, source.Kind("a.txt"));
        Assert.Equal(FileKind.Directory, source.Kind("sub"));
        Assert.Equal(FileKind.Directory, source.Kind(""));
        Assert.Equal(FileKind.None, source.Kind("missing"));
        Assert.Equal("hello", Encoding.UTF8.GetString(source.ReadAllBytes("a.txt")));
        using (Stream stream = source.OpenRead("sub/b.txt"))
        {
            Assert.Equal(3, stream.Length);
        }

        var entries = new List<(string Name, FileKind Kind)>();
        source.List("", entries);
        Assert.Equal([("a.txt", FileKind.File), ("sub", FileKind.Directory)], entries.OrderBy(e => e.Name, StringComparer.Ordinal));
        Assert.Equal(Path.Combine(folder.Root, "a.txt"), source.PhysicalPath("a.txt"));
        Assert.Null(source.PhysicalPath("sub"));
    }

    [Fact]
    public void AMissingFileIsFileNotFound()
    {
        using var folder = new TempFolder();
        Assert.Throws<FileNotFoundException>(() => new DirectorySource(folder.Root).ReadAllBytes("nope.txt"));
        Assert.Throws<FileNotFoundException>(() => new DirectorySource(folder.Root).OpenRead("nope.txt"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("a//b")]
    [InlineData("..")]
    public void APathCannotLeaveTheFolder(string path)
    {
        using var folder = new TempFolder();
        var source = new DirectorySource(folder.Root, writable: true);

        Assert.Throws<VfsException>(() => source.Kind(path));
        Assert.Throws<VfsException>(() => source.ReadAllBytes(path));
        Assert.Throws<VfsException>(() => source.WriteAllBytes(path, [1]));
    }

    [Fact]
    public void WritingIsOnlyForAWritableSourceAndKeepsNoTemporaryFile()
    {
        using var folder = new TempFolder();
        var readOnly = new DirectorySource(folder.Root);
        Assert.False(readOnly.CanWrite);
        Assert.Throws<UnauthorizedAccessException>(() => readOnly.WriteAllBytes("a.txt", [1]));
        Assert.Throws<UnauthorizedAccessException>(() => readOnly.Delete("a.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => readOnly.CreateDirectory("d"));

        var source = new DirectorySource(folder.Root, writable: true);
        source.WriteAllBytes("deep/er/a.bin", [1, 2, 3]);
        source.WriteAllBytes("deep/er/a.bin", [4, 5]);

        Assert.Equal([4, 5], source.ReadAllBytes("deep/er/a.bin"));
        Assert.Equal(["a.bin"], Directory.EnumerateFiles(Path.Combine(folder.Root, "deep", "er")).Select(Path.GetFileName));

        source.CreateDirectory("empty");
        Assert.Equal(FileKind.Directory, source.Kind("empty"));
        source.Delete("deep");
        Assert.Equal(FileKind.None, source.Kind("deep"));
        source.Delete("deep"); // nothing there: nothing happens
        Assert.Throws<UnauthorizedAccessException>(() => source.Delete(""));
    }
}

public class ZipSourceTests
{
    private static ZipSource MakeZip(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return new ZipSource(stream, "test.zip");
    }

    [Fact]
    public void ItReadsFilesAndMakesTheFoldersTheZipImplies()
    {
        using ZipSource zip = MakeZip(("mod.xml", "<Mod/>"), ("scripts/ui/main.luau", "print(1)"), ("scripts/util.luau", "return 1"));

        Assert.Equal(FileKind.File, zip.Kind("mod.xml"));
        Assert.Equal(FileKind.Directory, zip.Kind("scripts"));
        Assert.Equal(FileKind.Directory, zip.Kind("scripts/ui"));
        Assert.Equal(FileKind.None, zip.Kind("scripts/missing"));
        Assert.Equal("print(1)", Encoding.UTF8.GetString(zip.ReadAllBytes("scripts/ui/main.luau")));
        using (var reader = new StreamReader(zip.OpenRead("mod.xml")))
        {
            Assert.Equal("<Mod/>", reader.ReadToEnd());
        }

        var entries = new List<(string Name, FileKind Kind)>();
        zip.List("scripts", entries);
        Assert.Equal([("ui", FileKind.Directory), ("util.luau", FileKind.File)], entries.OrderBy(e => e.Name, StringComparer.Ordinal));
        Assert.Throws<FileNotFoundException>(() => zip.ReadAllBytes("nope"));
        Assert.False(((IFileSource)zip).CanWrite);
    }

    [Fact]
    public void EntriesThatCouldNameAPlaceOutsideTheZipAreSkipped()
    {
        using ZipSource zip = MakeZip(("ok.txt", "fine"), ("../evil.txt", "x"), ("a/../../evil2.txt", "x"), ("/abs.txt", "x"), ("nul.txt", "x"));

        Assert.Equal(FileKind.File, zip.Kind("ok.txt"));
        var entries = new List<(string Name, FileKind Kind)>();
        zip.List("", entries);
        Assert.Equal(["ok.txt"], entries.Select(e => e.Name));
    }

    [Fact]
    public void BackslashesFromWindowsToolsAreSlashes()
    {
        using ZipSource zip = MakeZip(("scripts\\main.luau", "x"));
        Assert.Equal(FileKind.File, zip.Kind("scripts/main.luau"));
    }

    [Fact]
    public void ANotZipIsAnIoException()
    {
        var error = Assert.Throws<IOException>(() => new ZipSource(new MemoryStream(new byte[64]), "junk.zip"));
        Assert.Contains("junk.zip", error.Message);
    }
}

public class VirtualFileSystemTests
{
    [Fact]
    public void AFileIsReadFromTheSourceMountedLast()
    {
        using var low = new TempFolder();
        using var high = new TempFolder();
        low.Write("both.txt", "low");
        low.Write("only_low.txt", "low only");
        high.Write("both.txt", "high");
        var vfs = new VirtualFileSystem();
        vfs.Mount("res://", new DirectorySource(low.Root));
        vfs.Mount("res://", new DirectorySource(high.Root));

        Assert.Equal("high", vfs.ReadAllText("res://both.txt"));
        Assert.Equal("low only", vfs.ReadAllText("res://only_low.txt"));
        Assert.True(vfs.FileExists("res://both.txt"));
        Assert.False(vfs.FileExists("res://nothing.txt"));
        Assert.Equal(Path.Combine(high.Root, "both.txt"), vfs.TryGetPhysicalPath("res://both.txt"));
        Assert.Equal(Path.Combine(low.Root, "only_low.txt"), vfs.TryGetPhysicalPath("res://only_low.txt"));
        Assert.Throws<FileNotFoundException>(() => vfs.ReadAllBytes("res://nothing.txt"));
    }

    [Fact]
    public void ListingsAreTheUnionOfTheSourcesSortedByOrdinalOrder()
    {
        using var low = new TempFolder();
        using var high = new TempFolder();
        low.Write("b.txt", "");
        low.Write("textures/one.png", "");
        low.Write("textures/two.png", "");
        high.Write("a.txt", "");
        high.Write("B.txt", "");
        high.Write("textures/three.png", "");
        high.Write("textures/notes.txt", "");
        var vfs = new VirtualFileSystem();
        vfs.Mount("res://", new DirectorySource(low.Root));
        vfs.Mount("res://", new DirectorySource(high.Root));

        Assert.Equal(["res://B.txt", "res://a.txt", "res://b.txt"], vfs.EnumerateFiles("res://"));
        Assert.Equal(["res://textures/one.png", "res://textures/three.png", "res://textures/two.png"], vfs.EnumerateFiles("res://textures", "*.png"));
        Assert.Equal(["res://textures"], vfs.EnumerateDirectories("res://"));
        Assert.Equal(
            ["res://B.txt", "res://a.txt", "res://b.txt", "res://textures/notes.txt", "res://textures/one.png", "res://textures/three.png", "res://textures/two.png"],
            vfs.EnumerateFiles("res://", recursive: true));
        Assert.Equal(["res://textures/two.png"], vfs.EnumerateFiles("res://textures", "t??.png"));
    }

    [Fact]
    public void PatternsUseStarAndQuestionMarkAndAreCaseSensitive()
    {
        using var folder = new TempFolder();
        foreach (string name in new[] { "a.luau", "ab.luau", "main.test.luau", "x", "Readme.TXT" })
        {
            folder.Write(name, "");
        }

        var vfs = new VirtualFileSystem();
        vfs.Mount("res://", new DirectorySource(folder.Root));

        Assert.Equal(["res://a.luau", "res://ab.luau", "res://main.test.luau"], vfs.EnumerateFiles("res://", "*.luau"));
        Assert.Equal(["res://main.test.luau"], vfs.EnumerateFiles("res://", "*.test.luau"));
        Assert.Equal(["res://a.luau"], vfs.EnumerateFiles("res://", "a.?uau"));
        Assert.Equal(["res://Readme.TXT"], vfs.EnumerateFiles("res://", "*.TXT"));
        Assert.Empty(vfs.EnumerateFiles("res://", "*.txt"));
        Assert.Equal(5, vfs.EnumerateFiles("res://", "*").Count);
    }

    /// <summary>A source that takes files away from the ones below it, as a patch pack does.</summary>
    private sealed class RemovingSource(params string[] removed) : IFileSource
    {
        public string Description => "removals";

        public FileKind Kind(string path) => removed.Contains(path) ? FileKind.Removed : FileKind.None;

        public byte[] ReadAllBytes(string path) => throw new FileNotFoundException(path);

        public Stream OpenRead(string path) => throw new FileNotFoundException(path);

        public void List(string directory, List<(string Name, FileKind Kind)> into)
        {
            foreach (string path in removed.Where(r => (r.Contains('/') ? r[..r.LastIndexOf('/')] : "") == directory))
            {
                into.Add((path[(path.LastIndexOf('/') + 1)..], FileKind.Removed));
            }
        }
    }

    [Fact]
    public void ASourceCanRemoveAFileFromTheOnesBelowIt()
    {
        using var folder = new TempFolder();
        folder.Write("keep.txt", "keep");
        folder.Write("gone.txt", "gone");
        folder.Write("dir/also_gone.txt", "gone");
        var vfs = new VirtualFileSystem();
        vfs.Mount("res://", new DirectorySource(folder.Root));
        vfs.Mount("res://", new RemovingSource("gone.txt", "dir/also_gone.txt"));

        Assert.True(vfs.FileExists("res://keep.txt"));
        Assert.False(vfs.FileExists("res://gone.txt"));
        Assert.Equal(FileKind.None, vfs.KindOf("res://gone.txt"));
        Assert.Throws<FileNotFoundException>(() => vfs.ReadAllBytes("res://gone.txt"));
        Assert.Equal(["res://keep.txt"], vfs.EnumerateFiles("res://"));
        Assert.Empty(vfs.EnumerateFiles("res://dir"));
        Assert.Null(vfs.TryGetPhysicalPath("res://gone.txt"));

        // A source mounted after the removal puts the file back.
        using var restored = new TempFolder();
        restored.Write("gone.txt", "back");
        vfs.Mount("res://", new DirectorySource(restored.Root));
        Assert.Equal("back", vfs.ReadAllText("res://gone.txt"));
    }

    [Fact]
    public void PrefixesNestAndTheFoldersAboveAMountExist()
    {
        using var alpha = new TempFolder();
        using var beta = new TempFolder();
        alpha.Write("mod.xml", "a");
        beta.Write("mod.xml", "b");
        beta.Write("scripts/main.luau", "return 1");
        var vfs = new VirtualFileSystem();
        vfs.Mount("mod://alpha", new DirectorySource(alpha.Root));
        vfs.Mount("mod://beta", new DirectorySource(beta.Root));

        Assert.Equal("a", vfs.ReadAllText("mod://alpha/mod.xml"));
        Assert.Equal("b", vfs.ReadAllText("mod://beta/mod.xml"));
        Assert.Equal("return 1", vfs.ReadAllText("mod://beta/scripts/main.luau"));
        Assert.True(vfs.DirectoryExists("mod://"));
        Assert.True(vfs.DirectoryExists("mod://alpha"));
        Assert.Equal(["mod://alpha", "mod://beta"], vfs.EnumerateDirectories("mod://"));
        Assert.Equal(["mod://alpha/mod.xml"], vfs.EnumerateFiles("mod://alpha"));
        Assert.False(vfs.Exists("mod://gamma"));
        Assert.False(vfs.Exists("res://mod.xml")); // nothing is mounted at res://
    }

    [Fact]
    public void ModsCanBeMountedInsideTheGamesOwnFolders()
    {
        using var game = new TempFolder();
        using var mod = new TempFolder();
        game.Write("data/x.txt", "game");
        mod.Write("y.txt", "mod");
        var vfs = new VirtualFileSystem();
        vfs.Mount("res://", new DirectorySource(game.Root));
        vfs.Mount("res://mods/cool", new DirectorySource(mod.Root));

        Assert.Equal(["res://data", "res://mods"], vfs.EnumerateDirectories("res://"));
        Assert.Equal(["res://mods/cool"], vfs.EnumerateDirectories("res://mods"));
        Assert.Equal("mod", vfs.ReadAllText("res://mods/cool/y.txt"));
        Assert.Equal("game", vfs.ReadAllText("res://data/x.txt"));
    }

    [Fact]
    public void OnlyAWritableSourceTakesWrites()
    {
        using var game = new TempFolder();
        using var user = new TempFolder();
        var vfs = new VirtualFileSystem();
        vfs.Mount("res://", new DirectorySource(game.Root));
        vfs.Mount("user://", new DirectorySource(user.Root, writable: true));

        vfs.WriteAllText("user://saves/slot.txt", "saved");
        vfs.WriteAllBytes("user://saves/slot.bin", [9]);

        Assert.Equal("saved", vfs.ReadAllText("user://saves/slot.txt"));
        Assert.Equal("saved", File.ReadAllText(Path.Combine(user.Root, "saves", "slot.txt")));
        Assert.Equal(["user://saves/slot.bin", "user://saves/slot.txt"], vfs.EnumerateFiles("user://saves"));
        vfs.Delete("user://saves/slot.bin");
        Assert.False(vfs.FileExists("user://saves/slot.bin"));
        vfs.CreateDirectory("user://empty");
        Assert.True(vfs.DirectoryExists("user://empty"));

        var readOnly = Assert.Throws<UnauthorizedAccessException>(() => vfs.WriteAllText("res://hack.txt", "x"));
        Assert.Contains("read-only", readOnly.Message);
        Assert.Throws<UnauthorizedAccessException>(() => vfs.Delete("res://anything"));
        Assert.Throws<UnauthorizedAccessException>(() => vfs.WriteAllText("mod://nothing/x.txt", "x")); // not mounted at all
        Assert.Empty(Directory.EnumerateFileSystemEntries(game.Root));
    }

    [Theory]
    [InlineData("res://a/../../b")]
    [InlineData("res://..")]
    [InlineData("../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("res://a\\b")]
    [InlineData("")]
    public void APathThatIsNotValidIsRefusedEverywhere(string path)
    {
        using var folder = new TempFolder();
        var vfs = new VirtualFileSystem();
        vfs.Mount("res://", new DirectorySource(folder.Root, writable: true));

        Assert.Equal(FileKind.None, vfs.KindOf(path));
        Assert.False(vfs.FileExists(path));
        Assert.Throws<VfsException>(() => vfs.ReadAllBytes(path));
        Assert.Throws<VfsException>(() => vfs.OpenRead(path));
        Assert.Throws<VfsException>(() => vfs.WriteAllText(path, "x"));
        Assert.Throws<VfsException>(() => vfs.EnumerateFiles(path));
        Assert.Null(vfs.TryGetPhysicalPath(path));
    }

    [Fact]
    public void ReadingAFolderAsAFileIsAnError()
    {
        using var folder = new TempFolder();
        folder.Write("dir/x.txt", "");
        var vfs = new VirtualFileSystem();
        vfs.Mount("res://", new DirectorySource(folder.Root));

        Assert.Throws<UnauthorizedAccessException>(() => vfs.ReadAllBytes("res://dir"));
    }

    [Fact]
    public void TextIsUtf8WithoutAByteOrderMark()
    {
        using var folder = new TempFolder();
        File.WriteAllBytes(Path.Combine(folder.Root, "bom.txt"), [.. Encoding.UTF8.Preamble.ToArray(), .. Encoding.UTF8.GetBytes("héllo")]);
        File.WriteAllBytes(Path.Combine(folder.Root, "plain.txt"), Encoding.UTF8.GetBytes("héllo"));
        var vfs = new VirtualFileSystem();
        vfs.Mount("res://", new DirectorySource(folder.Root));

        Assert.Equal("héllo", vfs.ReadAllText("res://bom.txt"));
        Assert.Equal("héllo", vfs.ReadAllText("res://plain.txt"));
    }

    [Fact]
    public void ASourceCanBeTakenOutAgainAndZipsMountLikeFolders()
    {
        using var folder = new TempFolder();
        folder.Write("a.txt", "from folder");
        using var zip = new ZipSource(MemoryZip("a.txt", "from zip"), "mod.zip");
        var vfs = new VirtualFileSystem();
        var directory = new DirectorySource(folder.Root);
        vfs.Mount("res://", directory);
        vfs.Mount("res://", zip);

        Assert.Equal("from zip", vfs.ReadAllText("res://a.txt"));
        Assert.Null(vfs.TryGetPhysicalPath("res://a.txt")); // the zip has it, and a zip has no real path
        Assert.True(vfs.Unmount(zip));
        Assert.False(vfs.Unmount(zip));
        Assert.Equal("from folder", vfs.ReadAllText("res://a.txt"));
        Assert.Single(vfs.Mounts);
    }

    private static MemoryStream MemoryZip(string name, string content)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void TheUserFolderOfAGameIsUnderTheLocalApplicationData()
    {
        string folder = VirtualFileSystem.DefaultUserDirectory("Acme", "Truck");

        Assert.EndsWith(Path.Combine("Acme", "Truck"), folder);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder);
    }

    [Fact]
    public async System.Threading.Tasks.Task ManyThreadsCanReadWhileSourcesAreMounted()
    {
        using var folder = new TempFolder();
        for (int i = 0; i < 20; i++)
        {
            folder.Write($"f{i}.txt", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var vfs = new VirtualFileSystem();
        vfs.Mount("res://", new DirectorySource(folder.Root));
        var stop = new System.Threading.CancellationTokenSource();
        var failures = new List<Exception>();
        var readers = Enumerable.Range(0, 4).Select(t => System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    for (int i = 0; i < 20; i++)
                    {
                        Assert.Equal(i.ToString(System.Globalization.CultureInfo.InvariantCulture), vfs.ReadAllText($"res://f{i}.txt"));
                    }

                    _ = vfs.EnumerateFiles("res://", "*.txt");
                }
            }
            catch (Exception e)
            {
                lock (failures)
                {
                    failures.Add(e);
                }
            }
        }, TestContext.Current.CancellationToken)).ToArray();

        for (int i = 0; i < 200; i++)
        {
            var extra = new DirectorySource(folder.Root);
            vfs.Mount($"mod://m{i}", extra);
            vfs.Unmount(extra);
        }

        stop.Cancel();
        await System.Threading.Tasks.Task.WhenAll(readers);
        Assert.Empty(failures);
    }
}

public class ModelsInTheFileSystemTests
{
    private static (byte[] Gltf, byte[] Bin) ExternalBuffer()
    {
        byte[] embedded = new GltfBuilder().AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2]).ToBytes();
        string json = Encoding.UTF8.GetString(embedded);
        var match = System.Text.RegularExpressions.Regex.Match(json, "\"uri\":\"data:application/octet-stream;base64,([^\"]+)\"");
        Assert.True(match.Success);
        return (Encoding.UTF8.GetBytes(json.Replace(match.Value, "\"uri\":\"scene.bin\"", StringComparison.Ordinal)), Convert.FromBase64String(match.Groups[1].Value));
    }

    [Fact]
    public void AModelAndItsExternalBufferLoadFromTheVirtualFileSystem()
    {
        (byte[] gltf, byte[] bin) = ExternalBuffer();
        using var folder = new TempFolder();
        File.WriteAllBytes(Path.Combine(folder.Root, "scene.gltf"), gltf);
        File.WriteAllBytes(Path.Combine(folder.Root, "scene.bin"), bin);
        var vfs = new VirtualFileSystem();
        vfs.Mount("res://models", new DirectorySource(folder.Root));

        ModelData model = GltfLoader.Load("res://models/scene.gltf", new ContentOptions { ReadFile = vfs.ReadAllBytes });

        Assert.Equal("scene", model.Name);
        Assert.Single(model.Meshes);
        Assert.Equal(3, model.Meshes[0].Vertices.Length);
    }

    [Fact]
    public void TheBufferComesOutOfAZipOrAPackTheSameWay()
    {
        (byte[] gltf, byte[] bin) = ExternalBuffer();
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in new[] { ("models/scene.gltf", gltf), ("models/scene.bin", bin) })
            {
                using Stream entry = archive.CreateEntry(name).Open();
                entry.Write(data);
            }
        }

        stream.Position = 0;
        using var zip = new ZipSource(stream, "models.zip");
        var vfs = new VirtualFileSystem();
        vfs.Mount("mod://pack", zip);

        ModelData model = GltfLoader.Load("mod://pack/models/scene.gltf", new ContentOptions { ReadFile = vfs.ReadAllBytes });

        Assert.Single(model.Meshes);
    }

    [Fact]
    public void ABufferThatIsMissingSaysWhy()
    {
        (byte[] gltf, _) = ExternalBuffer();
        var vfs = new VirtualFileSystem();
        vfs.Mount("res://", new DirectorySource(Path.GetTempPath()));
        byte[] file = gltf;

        ContentException error = Assert.Throws<ContentException>(() =>
            GltfLoader.Load(file, "res://models", new ContentOptions { ReadFile = vfs.ReadAllBytes }, "scene"));

        Assert.Contains("cannot be loaded", error.Message);
        Assert.Contains("scene.bin", error.Message);
    }

    [Fact]
    public void AReferenceWithDotsStaysInsideTheMount()
    {
        (byte[] gltf, byte[] bin) = ExternalBuffer();
        string json = Encoding.UTF8.GetString(gltf).Replace("scene.bin", "../../../shared/scene.bin", StringComparison.Ordinal);
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Root, "shared"));
        File.WriteAllBytes(Path.Combine(folder.Root, "shared", "scene.bin"), bin);
        var vfs = new VirtualFileSystem();
        vfs.Mount("res://", new DirectorySource(folder.Root));

        // Four levels up from res://models/deep cannot go above res://, so the file is found at res://shared/scene.bin.
        ModelData model = GltfLoader.Load(Encoding.UTF8.GetBytes(json), "res://models/deep", new ContentOptions { ReadFile = vfs.ReadAllBytes }, "scene");

        Assert.Single(model.Meshes);
    }
}
