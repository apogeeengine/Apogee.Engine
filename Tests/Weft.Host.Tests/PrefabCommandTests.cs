using System;
using System.IO;
using System.Linq;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Core.Numerics;
using Weft.Ecs;
using Weft.Host.Editing;
using Weft.Native.Tests;
using Weft.Reflection;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;

namespace Weft.Host.Tests;

[Component]
public struct Watcher
{
    public Entity Target;
}

/// <summary>The edits of the prefab overrides API as steps of an undo history: revert, unpack, make a prefab, instantiate.</summary>
public sealed class PrefabCommandTests : IDisposable
{
    private const string LampPath = "res://prefabs/lamp.wprf";

    private readonly string _folder = Directory.CreateTempSubdirectory("weft-prefab-commands").FullName;
    private readonly World _world;
    private readonly EditHistory _history = new();

    public PrefabCommandTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        string full = Path.Combine(_folder, "prefabs");
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "lamp.wprf"), """
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

        Assert.Throws<ArgumentException>(() => _history.Execute(PrefabCommands.Revert(_world, lamp, [new PrefabOverride(PrefabOverrideKind.Field, "2", "Hp", "Value", default, new Weft.Content.Xml.XmlNode("Hp"))])));

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
    public void UnpackingGivesTheReportOfWhatWasLeftWithNothingToApplyTo()
    {
        File.WriteAllText(Path.Combine(_folder, "prefabs/corner.wprf"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Prefab version="1" uid="uid://m3m5n7p9q1r3s" nextId="3">
              <Entity id="1" name="Corner"><Entity id="2" name="Lamp" prefab="{LampPath}"/></Entity>
            </Prefab>
            """);
        _world.LoadScene("<Scene version=\"2\"><Entity id=\"1\" name=\"Corner\" prefab=\"res://prefabs/corner.wprf\"><Override entity=\"2/7\"><Hp Value=\"3\"/></Override><Override entity=\"9\"><Hp Value=\"4\"/></Override></Entity></Scene>");
        Entity corner = _world.Lookup("Corner");
        Entity lamp = Child(corner, "Lamp");
        UnpackPrefabCommand command = PrefabCommands.BreakLink(_world, corner);

        _history.Execute(command);

        Assert.NotNull(command.Report);
        Assert.Equal([corner], command.Report.Unlinked);
        Assert.Equal("9", Assert.Single(command.Report.Dropped).Attribute("entity"));          // the one with no home; the one for the lamp went with it
        Assert.True(_world.IsPrefabInstance(lamp));
        Assert.Contains("<Override entity=\"7\">", _world.SaveScene(), StringComparison.Ordinal);

        _history.Undo();
        Assert.True(_world.IsPrefabInstance(corner));
        Assert.Contains("entity=\"9\"", _world.SaveScene(), StringComparison.Ordinal);       // what was dropped is back with the link
    }

    [Fact]
    public void MakingAPrefabIsUndoneAndRedoneAndTheFileStays()
    {
        Entity crate = _world.CreateEntity("Crate");
        Entity lid = _world.CreateEntity("Lid", crate);
        _world.Set(lid, new Hp { Value = 4 });

        _history.Execute(PrefabCommands.CreatePrefab(_world, crate, "res://prefabs/crate.wprf"));

        Assert.True(_world.IsPrefabInstance(crate));
        Assert.True(_world.IsPrefabMember(lid));
        Assert.True(File.Exists(Path.Combine(_folder, "prefabs/crate.wprf")));

        _history.Undo();

        Assert.False(_world.IsPrefabInstance(crate));
        Assert.False(_world.Has<PrefabMember>(lid));
        Assert.True(File.Exists(Path.Combine(_folder, "prefabs/crate.wprf")));      // an asset of the game: an undo does not delete it
        Assert.Equal(4, _world.Get<Hp>(lid).Value);

        _history.Redo();

        Assert.True(_world.IsPrefabInstance(crate));
        Assert.True(_world.IsPrefabMember(lid));
        Assert.Empty(_world.PrefabOverrides(crate));
    }

    [Fact]
    public void RedoingAnInstanceWhosePrefabChangedInBetweenFollowsThePrefab()
    {
        Entity crate = _world.CreateEntity("Crate");
        Entity lid = _world.CreateEntity("Lid", crate);
        _world.Set(lid, new Hp { Value = 4 });
        _history.Execute(PrefabCommands.CreatePrefab(_world, crate, "res://prefabs/crate.wprf"));
        _history.Undo();

        // The file is edited while the entities are plain: nothing follows it.
        string path = Path.Combine(_folder, "prefabs/crate.wprf");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Value=\"4\"", "Value=\"40\"", StringComparison.Ordinal));
        Assert.Equal(PrefabReloadStatus.Reloaded, _world.Prefabs.Reload("res://prefabs/crate.wprf").Status);
        _world.ApplyPrefabUpdates();
        Assert.False(_world.HasPendingPrefabUpdates);

        _history.Redo();

        Assert.True(_world.HasPendingPrefabUpdates);           // the instance that came back was made from the file as it was
        _world.ApplyPrefabUpdates();
        Assert.Equal(40, _world.Get<Hp>(lid).Value);
    }

    [Fact]
    public void UndoingAnUnpackOfAnInstanceWhosePrefabChangedInBetweenFollowsThePrefab()
    {
        Entity lamp = LoadLamp();
        Entity bulb = Child(lamp, "Bulb");
        _history.Execute(PrefabCommands.BreakLink(_world, lamp));

        string path = Path.Combine(_folder, "prefabs/lamp.wprf");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Value=\"1\"", "Value=\"11\"", StringComparison.Ordinal));
        Assert.Equal(PrefabReloadStatus.Reloaded, _world.Prefabs.Reload(LampPath).Status);
        _world.ApplyPrefabUpdates();
        Assert.Equal(1, _world.Get<Hp>(bulb).Value);            // plain entities do not follow

        _history.Undo();

        Assert.True(_world.HasPendingPrefabUpdates);
        _world.ApplyPrefabUpdates();
        Assert.Equal(11, _world.Get<Hp>(bulb).Value);
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
