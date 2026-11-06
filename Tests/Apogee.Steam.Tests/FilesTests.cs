using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content.FileSystem;
using Apogee.Content.Packages;
using Xunit;

namespace Apogee.Steam.Tests;

public sealed class FilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apogee-steam-" + Guid.NewGuid().ToString("N"));

    public FilesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Item(string folderName, string modId, string? extraFile = null)
    {
        string folder = Path.Combine(_root, "workshop", folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mod.xml"), $"""<Mod id="{modId}" name="{modId}" version="1.0.0"><Script path="main.luau"/></Mod>""");
        File.WriteAllText(Path.Combine(folder, "main.luau"), "-- " + modId);
        if (extraFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, extraFile))!);
            File.WriteAllText(Path.Combine(folder, extraFile), "data");
        }

        return folder;
    }

    [Fact]
    public void WorkshopItemsAreListedUnderTheirModIds()
    {
        string a = Item("3001", "big_crates", "data/x.xml");
        string b = Item("3002", "fast_trucks");
        var source = new WorkshopSource([("3001", a), ("3002", b)]);

        var root = new List<(string Name, FileKind Kind)>();
        source.List("", root);
        Assert.Equal(["big_crates", "fast_trucks"], root.Select(e => e.Name).Order());
        Assert.All(root, e => Assert.Equal(FileKind.Directory, e.Kind));

        Assert.Equal(FileKind.File, source.Kind("big_crates/mod.xml"));
        Assert.Equal(FileKind.Directory, source.Kind("big_crates/data"));
        Assert.Equal(FileKind.None, source.Kind("3001"));            // the item's id is not its name
        Assert.Equal(FileKind.None, source.Kind("big_crates/nothing"));
        Assert.Equal("data", System.Text.Encoding.UTF8.GetString(source.ReadAllBytes("big_crates/data/x.xml")));
        Assert.Throws<FileNotFoundException>(() => source.ReadAllBytes("unknown/mod.xml"));
        Assert.False(((IFileSource)source).CanWrite);
        Assert.Equal(Path.Combine(a, "mod.xml"), source.PhysicalPath("big_crates/mod.xml"));
    }

    [Fact]
    public void AFolderWithoutAModManifestIsLeftOut()
    {
        string notAMod = Path.Combine(_root, "workshop", "3003");
        Directory.CreateDirectory(notAMod);
        string good = Item("3004", "good_mod");
        var source = new WorkshopSource([("3003", notAMod), ("3004", good)]);

        var root = new List<(string Name, FileKind Kind)>();
        source.List("", root);
        Assert.Equal(["good_mod"], root.Select(e => e.Name));
    }

    [Fact]
    public void TwoItemsWithOneModIdKeepTheFirst()
    {
        string first = Item("3005", "same");
        string second = Item("3006", "same");
        var source = new WorkshopSource([("3005", first), ("3006", second)]);
        Assert.Equal(Path.Combine(first, "main.luau"), source.PhysicalPath("same/main.luau"));
    }

    [Fact]
    public void TheGamesPackageFinderFindsWorkshopMods()
    {
        var files = new VirtualFileSystem();
        files.Mount("workshop://", new WorkshopSource([("3001", Item("3001", "big_crates")), ("3002", Item("3002", "fast_trucks"))]));
        files.Mount("user://", new DirectorySource(Directory.CreateDirectory(Path.Combine(_root, "user")).FullName, writable: true));
        Directory.CreateDirectory(Path.Combine(_root, "user", "mods", "own_mod"));
        File.WriteAllText(Path.Combine(_root, "user", "mods", "own_mod", "mod.xml"), """<Mod id="own_mod" name="own" version="1.0.0"/>""");

        using PackageSet packages = PackageSet.Load(files, new PackageOptions { ExtraUserModsPaths = ["workshop://"] });

        Assert.Equal(["big_crates", "fast_trucks", "own_mod"], packages.Packages.Select(p => p.Id).Order());
        Assert.All(packages.Packages, p => Assert.True(p.Enabled, p.Reason));
        Assert.True(files.FileExists("mod://big_crates/main.luau"));
        Assert.Equal(PackageKind.User, packages.Packages.First(p => p.Id == "big_crates").Kind);
    }

    [Fact]
    public void TheCloudIsAPlainFolderWhenSteamIsNotThere()
    {
        using SteamSystem steam = new(new SteamOptions { Enabled = false });
        IFileSource cloud = steam.CreateCloudSource(Path.Combine(_root, "cloud"));
        var files = new VirtualFileSystem();
        files.Mount("cloud://", cloud);

        files.WriteAllText("cloud://slot1/save.json", "{\"day\":3}");

        Assert.True(files.FileExists("cloud://slot1/save.json"));
        Assert.Equal("{\"day\":3}", files.ReadAllText("cloud://slot1/save.json"));
        Assert.True(File.Exists(Path.Combine(_root, "cloud", "slot1", "save.json")));
    }
}
