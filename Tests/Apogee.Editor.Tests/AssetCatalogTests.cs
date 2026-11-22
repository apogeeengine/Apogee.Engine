using System;
using System.IO;
using System.Linq;
using Apogee.Content.FileSystem;
using Apogee.Content.Assets;
using Apogee.Core.Assets;
using Apogee.Editor.Assets;
using Apogee.Host.Editing;
using Xunit;

namespace Apogee.Editor.Tests;

/// <summary>The catalog of assets by kind that the pickers list: what it includes and leaves out, in which order, how it is bounded and when it looks again. Files only: no session, no window.</summary>
public sealed class AssetCatalogTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("apogee-catalog").FullName;
    private readonly string _modFolder = Directory.CreateTempSubdirectory("apogee-catalog-mod").FullName;
    private readonly VirtualFileSystem _files = new();
    private readonly AssetDatabase _assets;
    private double _now;

    public AssetCatalogTests()
    {
        _files.Mount("res://", new DirectorySource(_folder, writable: true));
        _assets = new AssetDatabase(_files);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
        Directory.Delete(_modFolder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static void Write(string root, string path, string text = "x")
    {
        string full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private AssetUid Imported(string path)
    {
        var uid = AssetUid.New();
        Write(_folder, path);
        Write(_folder, path + ".import.xml", $"<Import uid=\"{uid}\"/>");
        return uid;
    }

    private AssetCatalog Catalog(int maxFiles = AssetCatalog.MaxFiles, int maxFolders = AssetCatalog.MaxFolders, int maxDepth = AssetCatalog.MaxDepth) =>
        new(_files, _assets, () => _now, maxFiles, maxFolders, maxDepth);

    [Fact]
    public void AssetsAreListedByKindWithTheirUids()
    {
        AssetUid car = Imported("models/car.glb");
        AssetUid wall = Imported("textures/wall.png");
        Write(_folder, "scenes/level.ascene", "<Scene version=\"1\" uid=\"uid://0000000000abc\"/>");
        Write(_folder, "notes/readme.txt");

        using AssetCatalog catalog = Catalog();
        catalog.Refresh();

        CatalogEntry model = Assert.Single(catalog.Entries("model"));
        Assert.Equal("res://models/car.glb", model.Path);
        Assert.Equal("car.glb", model.Name);
        Assert.Equal(new AssetRef("res://models/car.glb", car), model.Ref);
        Assert.Equal(new AssetRef("res://textures/wall.png", wall), Assert.Single(catalog.Entries("texture")).Ref);
        CatalogEntry scene = Assert.Single(catalog.Entries("scene"));
        Assert.Equal("uid://0000000000abc", scene.Ref.Uid.ToString());       // an authored asset carries its own uid
        Assert.Empty(catalog.Entries("nothing"));
        Assert.False(catalog.IsLoading);
        Assert.False(catalog.Truncated);
    }

    [Fact]
    public void ImportedAndSidecarFilesAreNotAssets()
    {
        Imported("models/car.glb");
        Write(_folder, "imported/models/car.glb");                    // the import cache
        Write(_folder, "imported/car.amodel");
        Write(_folder, ".hidden/secret.glb");                          // a folder that starts with a dot
        Write(_folder, "models/.draft.glb");                           // a file that does
        Write(_folder, "models/notes.import.xml");
        Write(_folder, "deep/imported/keep.glb");                      // only the cache at the top of a root is the cache

        using AssetCatalog catalog = Catalog();
        catalog.Refresh();

        Assert.Equal(["res://deep/imported/keep.glb", "res://models/car.glb"], catalog.Entries("model").Select(e => e.Path));
    }

    [Fact]
    public void ModRootsAreListedAfterRes()
    {
        Imported("models/zebra.glb");
        Write(_folder, "packages/lamp/models/lamp.glb");               // the same file, reached through res://: listed once, as mod://
        Write(_folder, "packages/other/models/other.glb");             // a package that is not a root stays under res://
        Write(_modFolder, "models/lamp.glb");
        Write(_modFolder, "models/a-first.glb");
        _files.Mount("mod://lamp", new DirectorySource(_modFolder, writable: false));
        _assets.Roots.Add("mod://lamp/");

        using AssetCatalog catalog = Catalog();
        catalog.Refresh();

        string[] paths = [.. catalog.Entries("model").Select(e => e.Path)];
        Assert.Equal(
            [
                "res://models/zebra.glb",
                "res://packages/other/models/other.glb",
                "mod://lamp/models/a-first.glb",
                "mod://lamp/models/lamp.glb",
            ],
            paths);
    }

    [Fact]
    public void AnExtensionIsMatchedWithoutRegardToCase()
    {
        Write(_folder, "models/Shouting.GLB");
        Write(_folder, "textures/Wall.PNG");

        using AssetCatalog catalog = Catalog();
        catalog.Refresh();

        Assert.Equal("res://models/Shouting.GLB", Assert.Single(catalog.Entries("model")).Path);
        Assert.Single(catalog.Entries("texture"));
    }

    [Fact]
    public void SearchPutsNameMatchesBeforePathMatches()
    {
        Write(_folder, "props/crate.glb");
        Write(_folder, "crates/barrel.glb");
        Write(_folder, "crates/big/CRATE-large.glb");
        Write(_folder, "props/lamp.glb");

        using AssetCatalog catalog = Catalog();
        catalog.Refresh();

        Assert.Equal(["res://crates/big/CRATE-large.glb", "res://props/crate.glb", "res://crates/barrel.glb"], catalog.Search("model", "crate").Select(e => e.Path));
        Assert.Equal(4, catalog.Search("model", "").Count);
        Assert.Equal(4, catalog.Search("model", "   ").Count);
        Assert.Empty(catalog.Search("model", "nothing like it"));
        Assert.Empty(catalog.Search("texture", "crate"));
        Assert.Equal("res://props/lamp.glb", catalog.Find("res://props/lamp.glb")?.Path);
        Assert.Null(catalog.Find("res://props/none.glb"));
    }

    [Fact]
    public void TheListingIsBounded()
    {
        for (int i = 0; i < 12; i++)
        {
            Write(_folder, $"models/m{i:00}.glb");
        }

        Write(_folder, "a/b/c/deep.glb");
        using AssetCatalog files = Catalog(maxFiles: 5);
        files.Refresh();
        Assert.Equal(5, files.Entries("model").Count);
        Assert.True(files.Truncated);

        using AssetCatalog depth = Catalog(maxDepth: 1);
        depth.Refresh();
        Assert.DoesNotContain(depth.Entries("model"), e => e.Name == "deep.glb");
        Assert.Equal(12, depth.Entries("model").Count);
        Assert.True(depth.Truncated);                                  // it says that something is beyond the limit

        using AssetCatalog folders = Catalog(maxFolders: 2);
        folders.Refresh();
        Assert.True(folders.Truncated);

        using AssetCatalog all = Catalog();
        all.Refresh();
        Assert.Equal(13, all.Entries("model").Count);
        Assert.False(all.Truncated);
    }

    [Fact]
    public void TheListingStartsOnDemandOffTheCallingThreadAndLooksAgainWhenInvalidatedOrOld()
    {
        Write(_folder, "models/one.glb");
        using AssetCatalog catalog = Catalog();

        // Nothing is listed yet: the first question starts the listing on a worker and gets the empty list, so a popup never waits for the disk.
        Assert.True(catalog.IsLoading);
        _ = catalog.Entries("model");
        catalog.Wait();
        Assert.False(catalog.IsLoading);
        Assert.Equal(["res://models/one.glb"], catalog.Entries("model").Select(e => e.Path));
        int first = catalog.Version;

        // A file appears: within two seconds the catalog does not look again (the answer is the list it has) ...
        Write(_folder, "models/two.glb");
        _now += 1.0;
        _ = catalog.Entries("model");
        catalog.Wait();
        Assert.Equal(first, catalog.Version);
        Assert.Single(catalog.Entries("model"));

        // ... and after two seconds the next question starts a listing, and the one after it has the file.
        _now += AssetCatalog.RefreshSeconds + 0.5;
        _ = catalog.Entries("model");
        catalog.Wait();
        Assert.True(catalog.Version > first);
        Assert.Equal(2, catalog.Entries("model").Count);

        // The reloader saying something changed makes the next question look at once.
        Write(_folder, "models/three.glb");
        _ = catalog.Entries("model");
        catalog.Wait();
        Assert.Equal(2, catalog.Entries("model").Count);
        catalog.Invalidate();
        _ = catalog.Entries("model");
        catalog.Wait();
        Assert.Equal(3, catalog.Entries("model").Count);
    }

    [Fact]
    public void ANewKindIsListedWhenItIsRegistered()
    {
        Write(_folder, "things/a.catalogthing");
        using AssetCatalog catalog = Catalog();
        catalog.Refresh();
        Assert.Empty(catalog.Entries("catalogthing"));

        AssetKinds.Register("catalogthing", [".catalogthing"], "Thing");
        catalog.Refresh();
        Assert.Equal("res://things/a.catalogthing", Assert.Single(catalog.Entries("catalogthing")).Path);
    }

    [Fact]
    public void AReferenceIsFoundMovedOrMissing()
    {
        AssetUid car = Imported("models/car.glb");
        using AssetCatalog catalog = Catalog();

        Assert.Equal(new ReferenceStatus(ReferenceState.Empty, null), catalog.StatusOf(default));
        Assert.Equal(new ReferenceStatus(ReferenceState.Found, "res://models/car.glb"), catalog.StatusOf(new AssetRef("res://models/car.glb", car)));
        Assert.Equal(ReferenceState.Missing, catalog.StatusOf(new AssetRef("res://models/gone.glb", AssetUid.New())).State);
        Assert.Equal(ReferenceState.Missing, catalog.StatusOf(AssetRef.FromPath("res://models/gone.glb")).State);

        // The path names no file, but the uid does: the reference finds the asset where it moved (and says where).
        ReferenceStatus moved = catalog.StatusOf(new AssetRef("res://old/car.glb", car));
        Assert.Equal(ReferenceState.Moved, moved.State);
        Assert.Equal("res://models/car.glb", moved.Path);
        Assert.Equal(new ReferenceStatus(ReferenceState.Moved, "res://models/car.glb"), catalog.StatusOf(AssetRef.FromUid(car)));
    }

    [Fact]
    public void ThePickerFiltersAndKeepsItsListUntilTheFilterOrTheListingChanges()
    {
        Write(_folder, "models/crate.glb");
        Write(_folder, "models/barrel.glb");
        using AssetCatalog catalog = Catalog();
        catalog.Refresh();
        AssetPicker picker = catalog.PickerFor("model");
        Assert.Same(picker, catalog.PickerFor("model"));
        Assert.Equal("Model", picker.Label);

        picker.Open();
        Assert.Equal(2, picker.Results.Count);
        System.Collections.Generic.IReadOnlyList<CatalogEntry> all = picker.Results;
        Assert.Same(all, picker.Results);                                // the same list each frame: nothing is matched again

        picker.Filter = "barr";
        Assert.Equal(["res://models/barrel.glb"], picker.Results.Select(e => e.Path));

        picker.Open();                                                   // opening again starts from everything
        Assert.Equal("", picker.Filter);
        Assert.Equal(2, picker.Results.Count);

        Write(_folder, "models/crane.glb");
        catalog.Refresh();
        Assert.Equal(3, picker.Results.Count);                          // a new listing is seen
    }

    [Fact]
    public void NoticesAreKeptPerKeyAndExpire()
    {
        using AssetCatalog catalog = Catalog();
        catalog.PostNotice("A.b", "no good");
        Assert.Equal("no good", catalog.NoticeFor("A.b"));
        Assert.Null(catalog.NoticeFor("A.c"));
        _now += AssetCatalog.NoticeSeconds + 1;
        Assert.Null(catalog.NoticeFor("A.b"));

        catalog.PostNotice("A.b", "again");
        catalog.ClearNotice("A.b");
        Assert.Null(catalog.NoticeFor("A.b"));

        catalog.Reveal("res://models/car.glb");
        Assert.Equal("res://models/car.glb", catalog.TakeReveal());
        Assert.Null(catalog.TakeReveal());
    }
}
