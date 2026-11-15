using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Content;
using Apogee.Content.Assets;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;
using Apogee.Core.Assets;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Ecs.Tests;

/// <summary>Scene files: the UID they get when first saved and keep after, and loading them by path or by <c>uid://</c>, text or cooked.</summary>
public sealed class SceneFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("apogee-scene-files").FullName;
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

        _world.SaveSceneFile("res://scenes/level.ascene");

        string text = File.ReadAllText(Physical("scenes/level.ascene"));
        AssetUid uid = AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("scenes/level.ascene")));
        Assert.False(uid.IsNone);
        Assert.Contains($"<Scene version=\"1\" uid=\"{uid}\">", text, StringComparison.Ordinal);
        Assert.Equal(uid, _assets.UidOf("res://scenes/level.ascene")); // the database knows the new file without a refresh by the caller
        Assert.Equal("res://scenes/level.ascene", _assets.PathOf(uid));
    }

    [Fact]
    public void SavingOverAFileKeepsItsUid()
    {
        Make("crate");
        _world.SaveSceneFile("res://scenes/level.ascene");
        AssetUid first = AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("scenes/level.ascene")));

        Make("barrel");
        _world.SaveSceneFile("res://scenes/level.ascene");

        string text = File.ReadAllText(Physical("scenes/level.ascene"));
        Assert.Equal(first, AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("scenes/level.ascene"))));
        Assert.Contains("barrel", text, StringComparison.Ordinal); // the new content was written

        // A file written by hand keeps the UID it was given, too.
        var chosen = AssetUid.New();
        Directory.CreateDirectory(Physical("scenes"));
        File.WriteAllText(Physical("scenes/hand.ascene"), $"<!-- mine -->\n<Scene version='1' uid='{chosen}'/>");
        _world.SaveSceneFile("res://scenes/hand.ascene");
        Assert.Equal(chosen, AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("scenes/hand.ascene"))));
    }

    [Fact]
    public void SaveSceneTextHasNoUid()
    {
        Make("crate");
        _world.SaveSceneFile("res://scenes/level.ascene");

        Assert.DoesNotContain("uid", _world.SaveScene(), StringComparison.Ordinal);
        Assert.DoesNotContain("uid", _world.SaveScene(_world.Roots().Single()), StringComparison.Ordinal);
    }

    [Fact]
    public void ASceneFileOfOneEntityHoldsItAndWhatIsUnderIt()
    {
        Entity crate = Make("crate");
        Make("lid", crate);
        Make("other");

        _world.SaveSceneFile("res://prefab.ascene", crate);

        using var fresh = new World { Files = _files, Assets = _assets };
        List<Entity> loaded = fresh.LoadSceneFile("res://prefab.ascene");
        Assert.Equal(["crate", "lid"], loaded.Select(e => fresh.GetName(e)));
        Assert.False(AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("prefab.ascene"))).IsNone);
    }

    [Fact]
    public void LoadSceneFileAcceptsAUidUri()
    {
        Entity crate = Make("crate");
        Make("lid", crate);
        _world.SaveSceneFile("res://scenes/level.ascene");
        AssetUid uid = _assets.UidOf("res://scenes/level.ascene");

        using var fresh = new World { Files = _files, Assets = _assets };
        List<Entity> byPath = fresh.LoadSceneFile("res://scenes/level.ascene");
        List<Entity> byUid = fresh.LoadSceneFile(uid.ToString());

        Assert.Equal(2, byPath.Count);
        Assert.Equal(2, byUid.Count);
        Assert.Equal("crate", fresh.GetName(byPath[0]));
        Assert.Equal("crate 2", fresh.GetName(byUid[0])); // a second copy in the same world gets a free name
        Entity holder = fresh.CreateEntity("holder");
        Assert.Equal(holder, fresh.GetParent(fresh.LoadSceneFile(uid.ToString(), holder)[0]));

        Assert.Throws<ContentException>(() => fresh.LoadSceneFile("uid://0000000000zzz"));
        Assert.Throws<FileNotFoundException>(() => fresh.LoadSceneFile("res://scenes/none.ascene"));
        Assert.Throws<InvalidOperationException>(() => _ = new World().LoadSceneFile("res://scenes/level.ascene"));
    }

    [Fact]
    public void ARenamedSceneLoadsThroughItsUid()
    {
        Make("crate");
        _world.SaveSceneFile("res://scenes/level.ascene");
        AssetUid uid = _assets.UidOf("res://scenes/level.ascene");

        Directory.CreateDirectory(Physical("levels"));
        File.Move(Physical("scenes/level.ascene"), Physical("levels/first.ascene"));
        _assets.Refresh();

        using var fresh = new World { Files = _files, Assets = _assets };
        Assert.Equal("crate", fresh.GetName(Assert.Single(fresh.LoadSceneFile(uid.ToString()))));
        Assert.Throws<FileNotFoundException>(() => fresh.LoadSceneFile("res://scenes/level.ascene"));

        // Saving to the UID overwrites the file where it is now, and it is still the same asset.
        fresh.SaveSceneFile(uid.ToString());
        Assert.Equal(uid, AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("levels/first.ascene"))));
        Assert.False(File.Exists(Physical("scenes/level.ascene")));
    }

    [Fact]
    public void ACookedSceneFileLoadsLikeTheTextOne()
    {
        Make("crate");
        _world.SaveSceneFile("res://scenes/level.ascene");
        XmlNode document = XmlFile.Read(_files, "res://scenes/level.ascene");
        File.WriteAllBytes(Physical("scenes/level.ascene"), XmlBinary.Write(document));
        _assets.Refresh();

        using var fresh = new World { Files = _files, Assets = _assets };
        Assert.Equal("crate", fresh.GetName(Assert.Single(fresh.LoadSceneFile("res://scenes/level.ascene"))));
        Assert.Single(fresh.LoadSceneFile(_assets.UidOf("res://scenes/level.ascene").ToString())); // by its UID, read from the cooked root

        // Saved over, the cooked file keeps its UID too.
        fresh.SaveSceneFile("res://scenes/level.ascene");
        Assert.Equal(_assets.UidOf("res://scenes/level.ascene"), AuthoredAssets.ReadUid(File.ReadAllBytes(Physical("scenes/level.ascene"))));
    }

    [Fact]
    public void AFileThatIsNotASceneIsAFormatError()
    {
        Directory.CreateDirectory(Physical("scenes"));
        File.WriteAllText(Physical("scenes/bad.ascene"), "not xml");
        File.WriteAllText(Physical("scenes/wrong.ascene"), "<Level version=\"1\"/>");

        Assert.Throws<FormatException>(() => _world.LoadSceneFile("res://scenes/bad.ascene"));
        Assert.Throws<FormatException>(() => _world.LoadSceneFile("res://scenes/wrong.ascene"));
    }
}
