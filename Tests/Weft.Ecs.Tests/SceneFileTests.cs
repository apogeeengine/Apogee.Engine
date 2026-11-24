using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Weft.Content;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Content.Xml;
using Weft.Core.Assets;
using Weft.Core.Numerics;
using Weft.Ecs;
using Weft.Native.Tests;
using Weft.Reflection;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;

namespace Weft.Ecs.Tests;

/// <summary>Scene files: the UID they get when first saved and keep after, and loading them by path or by <c>uid://</c>, text or cooked.</summary>
public sealed class SceneFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("weft-scene-files").FullName;
    private readonly VirtualFileSystem _files = new();
    private readonly AssetDatabase _assets;
    private readonly World _world;

    public SceneFileTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        _files.Mount("res://", new DirectorySource(_folder, writable: true));
        _assets = new AssetDatabase(_files);
        _world = new World { Files = _files, Assets = _assets };
    }

    public void Dispose()
    {
        _world.Dispose();
        Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private Entity Make(string name, Entity parent = default)
    {
        Entity e = _world.CreateEntity(name, parent);
        _world.Set(e, new Transform(new Double3(1, 2, 3), Quaternion.Identity, Vector3.One));
        return e;
    }

    private string Physical(string path) => Path.Combine(_folder, path);

    [Fact]
    public void SavingANewSceneFileMintsAUid()
    {
        Make("crate");

        _world.SaveSceneFile("res://scenes/level.wscn");

        string text = File.ReadAllText(Physical("scenes/level.wscn"));
        AssetUid uid = AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("scenes/level.wscn")));
        Assert.False(uid.IsNone);
        Assert.Contains($"<Scene version=\"1\" uid=\"{uid}\">", text, StringComparison.Ordinal);
        Assert.Equal(uid, _assets.UidOf("res://scenes/level.wscn")); // the database knows the new file without a refresh by the caller
        Assert.Equal("res://scenes/level.wscn", _assets.PathOf(uid));
    }

    [Fact]
    public void SavingOverAFileKeepsItsUid()
    {
        Make("crate");
        _world.SaveSceneFile("res://scenes/level.wscn");
        AssetUid first = AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("scenes/level.wscn")));

        Make("barrel");
        _world.SaveSceneFile("res://scenes/level.wscn");

        string text = File.ReadAllText(Physical("scenes/level.wscn"));
        Assert.Equal(first, AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("scenes/level.wscn"))));
        Assert.Contains("barrel", text, StringComparison.Ordinal); // the new content was written

        // A file written by hand keeps the UID it was given, too.
        var chosen = AssetUid.New();
        Directory.CreateDirectory(Physical("scenes"));
        File.WriteAllText(Physical("scenes/hand.wscn"), $"<!-- mine -->\n<Scene version='1' uid='{chosen}'/>");
        _world.SaveSceneFile("res://scenes/hand.wscn");
        Assert.Equal(chosen, AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("scenes/hand.wscn"))));
    }

    [Fact]
    public void SaveSceneTextHasNoUid()
    {
        Make("crate");
        _world.SaveSceneFile("res://scenes/level.wscn");

        Assert.DoesNotContain("uid", _world.SaveScene(), StringComparison.Ordinal);
        Assert.DoesNotContain("uid", _world.SaveScene(_world.Roots().Single()), StringComparison.Ordinal);
    }

    [Fact]
    public void ASceneFileOfOneEntityHoldsItAndWhatIsUnderIt()
    {
        Entity crate = Make("crate");
        Make("lid", crate);
        Make("other");

        _world.SaveSceneFile("res://prefab.wscn", crate);

        using var fresh = new World { Files = _files, Assets = _assets };
        List<Entity> loaded = fresh.LoadSceneFile("res://prefab.wscn");
        Assert.Equal(["crate", "lid"], loaded.Select(e => fresh.GetName(e)));
        Assert.False(AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("prefab.wscn"))).IsNone);
    }

    [Fact]
    public void LoadSceneFileAcceptsAUidUri()
    {
        Entity crate = Make("crate");
        Make("lid", crate);
        _world.SaveSceneFile("res://scenes/level.wscn");
        AssetUid uid = _assets.UidOf("res://scenes/level.wscn");

        using var fresh = new World { Files = _files, Assets = _assets };
        List<Entity> byPath = fresh.LoadSceneFile("res://scenes/level.wscn");
        List<Entity> byUid = fresh.LoadSceneFile(uid.ToString());

        Assert.Equal(2, byPath.Count);
        Assert.Equal(2, byUid.Count);
        Assert.Equal("crate", fresh.GetName(byPath[0]));
        Assert.Equal("crate 2", fresh.GetName(byUid[0])); // a second copy in the same world gets a free name
        Entity holder = fresh.CreateEntity("holder");
        Assert.Equal(holder, fresh.GetParent(fresh.LoadSceneFile(uid.ToString(), holder)[0]));

        Assert.Throws<ContentException>(() => fresh.LoadSceneFile("uid://0000000000zzz"));
        Assert.Throws<FileNotFoundException>(() => fresh.LoadSceneFile("res://scenes/none.wscn"));
        Assert.Throws<InvalidOperationException>(() => _ = new World().LoadSceneFile("res://scenes/level.wscn"));
    }

    [Fact]
    public void ARenamedSceneLoadsThroughItsUid()
    {
        Make("crate");
        _world.SaveSceneFile("res://scenes/level.wscn");
        AssetUid uid = _assets.UidOf("res://scenes/level.wscn");

        Directory.CreateDirectory(Physical("levels"));
        File.Move(Physical("scenes/level.wscn"), Physical("levels/first.wscn"));
        _assets.Refresh();

        using var fresh = new World { Files = _files, Assets = _assets };
        Assert.Equal("crate", fresh.GetName(Assert.Single(fresh.LoadSceneFile(uid.ToString()))));
        Assert.Throws<FileNotFoundException>(() => fresh.LoadSceneFile("res://scenes/level.wscn"));

        // Saving to the UID overwrites the file where it is now, and it is still the same asset.
        fresh.SaveSceneFile(uid.ToString());
        Assert.Equal(uid, AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("levels/first.wscn"))));
        Assert.False(File.Exists(Physical("scenes/level.wscn")));
    }

    [Fact]
    public void ACookedSceneFileLoadsLikeTheTextOne()
    {
        Make("crate");
        _world.SaveSceneFile("res://scenes/level.wscn");
        XmlNode document = XmlFile.Read(_files, "res://scenes/level.wscn");
        File.WriteAllBytes(Physical("scenes/level.wscn"), XmlBinary.Write(document));
        _assets.Refresh();

        using var fresh = new World { Files = _files, Assets = _assets };
        Assert.Equal("crate", fresh.GetName(Assert.Single(fresh.LoadSceneFile("res://scenes/level.wscn"))));
        Assert.Single(fresh.LoadSceneFile(_assets.UidOf("res://scenes/level.wscn").ToString())); // by its UID, read from the cooked root

        // Saved over, the cooked file keeps its UID too.
        fresh.SaveSceneFile("res://scenes/level.wscn");
        Assert.Equal(_assets.UidOf("res://scenes/level.wscn"), AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("scenes/level.wscn"))));
    }

    [Fact]
    public void AFileThatIsNotASceneIsAFormatError()
    {
        Directory.CreateDirectory(Physical("scenes"));
        File.WriteAllText(Physical("scenes/bad.wscn"), "not xml");
        File.WriteAllText(Physical("scenes/wrong.wscn"), "<Level version=\"1\"/>");

        Assert.Throws<FormatException>(() => _world.LoadSceneFile("res://scenes/bad.wscn"));
        Assert.Throws<FormatException>(() => _world.LoadSceneFile("res://scenes/wrong.wscn"));
    }
}
