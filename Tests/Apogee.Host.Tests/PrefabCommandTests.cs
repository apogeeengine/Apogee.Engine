using System;
using System.IO;
using System.Linq;
using Apogee.Content.Assets;
using Apogee.Content.FileSystem;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Host.Editing;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Host.Tests;

[Component]
public struct Watcher
{
    public Entity Target;
}

/// <summary>The edits of the prefab overrides API as steps of an undo history: revert, unpack, make a prefab, instantiate.</summary>
public sealed class PrefabCommandTests : IDisposable
{
    private const string LampPath = "res://prefabs/lamp.aprefab";

    private readonly string _folder = Directory.CreateTempSubdirectory("apogee-prefab-commands").FullName;
    private readonly World _world;
    private readonly EditHistory _history = new();

    public PrefabCommandTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        string full = Path.Combine(_folder, "prefabs");
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "lamp.aprefab"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Prefab version="1" uid="uid://k3m5n7p9q1r3s" nextId="4">
              <Entity id="1" name="Lamp">
                <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
                <Entity id="2" name="Bulb"><Transform Position="0 1 0" Rotation="0 0 0 1" Scale="1 1 1"/><Hp Value="1" Armor="0.5"/></Entity>
                <Entity id="3" name="Base"><Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
              </Entity>
            </Prefab>
            """);
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(_folder, writable: true));
        _world = new World { Files = files, Assets = new AssetDatabase(files) };
    }

    public void Dispose()
    {
        _world.Dispose();
        Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private Entity LoadLamp(string body = "")
    {
        _world.LoadScene($"<Scene version=\"2\"><Entity id=\"1\" name=\"Lamp\" prefab=\"{LampPath}\">{body}</Entity></Scene>");
        return _world.Lookup("Lamp");
    }

    private Entity Child(Entity parent, string name) => _world.Children(parent).Single(c => _world.GetName(c) == name);

    [Fact]
    public void ARevertIsUndoneAndRedoneWithTheSameEntities()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Hp Value=\"9\"/></Override><Override entity=\"3\" remove=\"true\"/><Entity id=\"4\" name=\"Cord\"/>");
        Entity bulb = Child(lamp, "Bulb");
        Entity cord = Child(lamp, "Cord");
        string saved = _world.SaveScene();

        _history.Execute(PrefabCommands.RevertAll(_world, lamp));

        Assert.Equal(1, _world.Get<Hp>(bulb).Value);
        Assert.False(_world.IsAlive(cord));
        Assert.Equal(["Bulb", "Base"], _world.Children(lamp).Select(c => _world.GetName(c)).ToArray());
        Assert.Empty(_world.PrefabOverrides(lamp));
        Entity made = Child(lamp, "Base");

        _history.Undo();

        Assert.Equal(9, _world.Get<Hp>(bulb).Value);
        Assert.True(_world.IsAlive(cord));
        Assert.Equal(cord, Child(lamp, "Cord"));
        Assert.False(_world.IsAlive(made));
        Assert.Equal(["Bulb", "Cord"], _world.Children(lamp).Select(c => _world.GetName(c)).ToArray());
        Assert.Equal(saved, _world.SaveScene());

        _history.Redo();

        Assert.Equal(1, _world.Get<Hp>(bulb).Value);
        Assert.False(_world.IsAlive(cord));
        Assert.Equal(made, Child(lamp, "Base"));
        Assert.Empty(_world.PrefabOverrides(lamp));
    }

    [Fact]
    public void ARevertOfSomeOverridesIsOneStep()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\" name=\"Globe\"><Hp Value=\"9\" Armor=\"3\"/></Override>");
        Entity bulb = Child(lamp, "Globe");
        PrefabOverride armor = _world.PrefabOverrides(lamp).Single(o => o.Field == "Armor");

        _history.Execute(PrefabCommands.Revert(_world, lamp, [armor]));

        Assert.Equal(0.5f, _world.Get<Hp>(bulb).Armor);
        Assert.Equal(9, _world.Get<Hp>(bulb).Value);
        Assert.Equal("Revert Override", _history.UndoName);
        _history.Undo();
        Assert.Equal(3f, _world.Get<Hp>(bulb).Armor);
        Assert.Equal(2, _world.PrefabOverrides(lamp).Count(o => o.Kind == PrefabOverrideKind.Field));
    }

    [Fact]
    public void WhatNamedARevertedEntityNamesItAgainWhenTheRevertIsUndone()
    {
        Entity lamp = LoadLamp("<Entity id=\"4\" name=\"Cord\"/>");
        Entity cord = Child(lamp, "Cord");
        Entity pointer = _world.CreateEntity("Spy");
        _world.Set(pointer, new Watcher { Target = cord });

        _history.Execute(PrefabCommands.RevertAll(_world, lamp));
        Assert.False(_world.IsAlive(cord));
        Assert.True(_world.Get<Watcher>(pointer).Target.IsNull);

        _history.Undo();
        Assert.Equal(cord, _world.Get<Watcher>(pointer).Target);

        _history.Redo();
        Assert.True(_world.Get<Watcher>(pointer).Target.IsNull);
    }

    [Fact]
    public void ARevertThatIsRefusedIsNotAStep()
    {
        Entity lamp = LoadLamp();

        Assert.Throws<ArgumentException>(() => _history.Execute(PrefabCommands.Revert(_world, lamp, [new PrefabOverride(PrefabOverrideKind.Field, "2", "Hp", "Value", default, new Apogee.Content.Xml.XmlNode("Hp"))])));

        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void UnpackingIsUndoneAndRedone()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Hp Value=\"9\"/></Override><Override entity=\"7\"><Hp Value=\"3\"/></Override>");
        string saved = _world.SaveScene();
        Entity bulb = Child(lamp, "Bulb");

        _history.Execute(PrefabCommands.BreakLink(_world, lamp));

        Assert.False(_world.IsPrefabInstance(lamp));
        Assert.False(_world.Has<PrefabMember>(bulb));
        Assert.DoesNotContain("prefab=", _world.SaveScene(), StringComparison.Ordinal);

        _history.Undo();

        Assert.True(_world.IsPrefabInstance(lamp));
        Assert.True(_world.IsPrefabMember(bulb));
        Assert.Equal(saved, _world.SaveScene());                // what was kept aside is back too
        Assert.Equal(9, _world.Get<Hp>(bulb).Value);

        _history.Redo();

        Assert.False(_world.IsPrefabInstance(lamp));
        Assert.Equal(9, _world.Get<Hp>(bulb).Value);
    }

    [Fact]
    public void MakingAPrefabIsUndoneAndRedoneAndTheFileStays()
    {
        Entity crate = _world.CreateEntity("Crate");
        Entity lid = _world.CreateEntity("Lid", crate);
        _world.Set(lid, new Hp { Value = 4 });

        _history.Execute(PrefabCommands.CreatePrefab(_world, crate, "res://prefabs/crate.aprefab"));

        Assert.True(_world.IsPrefabInstance(crate));
        Assert.True(_world.IsPrefabMember(lid));
        Assert.True(File.Exists(Path.Combine(_folder, "prefabs/crate.aprefab")));

        _history.Undo();

        Assert.False(_world.IsPrefabInstance(crate));
        Assert.False(_world.Has<PrefabMember>(lid));
        Assert.True(File.Exists(Path.Combine(_folder, "prefabs/crate.aprefab")));      // an asset of the game: an undo does not delete it
        Assert.Equal(4, _world.Get<Hp>(lid).Value);

        _history.Redo();

        Assert.True(_world.IsPrefabInstance(crate));
        Assert.True(_world.IsPrefabMember(lid));
        Assert.Empty(_world.PrefabOverrides(crate));
    }

    [Fact]
    public void InstantiatingIsUndoneAndRedoneUnderTheSameIds()
    {
        Entity parent = _world.CreateEntity("Room");
        InstantiatePrefabCommand command = PrefabCommands.Instantiate(_world, LampPath, parent);

        _history.Execute(command);

        Entity lamp = command.Instance;
        Entity bulb = Child(lamp, "Bulb");
        Assert.True(_world.IsPrefabInstance(lamp));
        Assert.Equal(parent, _world.GetParent(lamp));

        _history.Undo();
        Assert.False(_world.IsAlive(lamp));
        Assert.False(_world.IsAlive(bulb));

        _history.Redo();
        Assert.True(_world.IsAlive(lamp));
        Assert.Equal(bulb, Child(lamp, "Bulb"));
        Assert.True(_world.IsPrefabInstance(lamp));
        Assert.Equal(1, _world.Get<Hp>(bulb).Value);
    }

    [Fact]
    public void ACommandThatWouldDestroyItsRootIsRefused()
    {
        Entity lamp = LoadLamp();
        var command = new SubtreeEditCommand(_world, "Destroy", lamp, world => world.Destroy(lamp));

        Assert.Throws<InvalidOperationException>(() => _history.Execute(command));
    }
}
