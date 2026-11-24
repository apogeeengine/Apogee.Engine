using System;
using System.IO;
using System.Linq;
using System.Numerics;
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
public struct Hp
{
    public int Value;
    public float Armor;
}

[Component]
public struct Burning;

/// <summary>The undo and redo history of an editor, over a headless world.</summary>
public sealed class EditHistoryTests : IDisposable
{
    private readonly World _world;
    private readonly EditHistory _history = new();

    public EditHistoryTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        _world = new World();
    }

    public void Dispose() => _world.Dispose();

    private Entity Make(string name, Entity parent = default, Double3 at = default)
    {
        Entity e = _world.CreateEntity(name, parent);
        _world.Set(e, Transform.At(at));
        return e;
    }

    private static byte[] Bytes(Hp value)
    {
        byte[] bytes = new byte[System.Runtime.CompilerServices.Unsafe.SizeOf<Hp>()];
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes, in value);
        return bytes;
    }

    [Fact]
    public void ACommandIsUndoneAndRedoneAndANewOneDropsTheRedoList()
    {
        Entity e = Make("e");
        Entity hp = _world.Component<Hp>();
        _world.Set(e, new Hp { Value = 1 });

        _history.Execute(SetComponentCommand.Create(_world, e, hp, Bytes(new Hp { Value = 5 }), "Set Hp")!);
        _history.Seal();
        _history.Execute(SetComponentCommand.Create(_world, e, hp, Bytes(new Hp { Value = 9 }), "Set Hp")!);
        Assert.Equal(9, _world.Get<Hp>(e).Value);

        Assert.True(_history.Undo());
        Assert.Equal(5, _world.Get<Hp>(e).Value);
        Assert.True(_history.CanRedo);
        Assert.Equal("Set Hp", _history.RedoName);

        Assert.True(_history.Redo());
        Assert.Equal(9, _world.Get<Hp>(e).Value);

        _history.Undo();
        _history.Undo();
        Assert.Equal(1, _world.Get<Hp>(e).Value);
        Assert.False(_history.CanUndo);
        Assert.False(_history.Undo());

        _history.Execute(SetComponentCommand.Create(_world, e, hp, Bytes(new Hp { Value = 3 }), "Set Hp")!);
        Assert.False(_history.CanRedo);
        Assert.False(_history.Redo());
    }

    [Fact]
    public void ADragIsOneStepUntilItIsSealedAndAnUnchangedValueIsNoCommand()
    {
        Entity e = Make("e");
        Entity hp = _world.Component<Hp>();
        _world.Set(e, new Hp { Value = 0 });
        int changes = 0;
        _history.Changed += () => changes++;

        for (int v = 1; v <= 10; v++)
        {
            _history.Execute(SetComponentCommand.Create(_world, e, hp, Bytes(new Hp { Value = v }), "Set Hp")!);   // ten frames of a slider
        }

        Assert.Single(_history.Steps);
        Assert.Equal(10, changes);                       // every change was reported
        _history.Seal();                                // the mouse was let go
        _history.Execute(SetComponentCommand.Create(_world, e, hp, Bytes(new Hp { Value = 50 }), "Set Hp")!);
        Assert.Equal(2, _history.Steps.Count);

        _history.Undo();
        Assert.Equal(10, _world.Get<Hp>(e).Value);       // the whole drag went at once, back to where the next gesture started
        _history.Undo();
        Assert.Equal(0, _world.Get<Hp>(e).Value);

        Assert.Null(SetComponentCommand.Create(_world, e, hp, Bytes(new Hp { Value = 0 }), "Set Hp"));   // nothing to do
    }

    [Fact]
    public void ComponentsAreAddedAndRemovedWithTheirValuesKept()
    {
        Entity e = Make("e");
        Entity hp = _world.Component<Hp>();
        Entity burning = _world.Component<Burning>();

        _history.Execute(new AddComponentCommand(_world, e, hp, Bytes(new Hp { Value = 4, Armor = 1.5f })));
        _history.Execute(new AddComponentCommand(_world, e, burning));
        Assert.Equal(4, _world.Get<Hp>(e).Value);
        Assert.True(_world.Has<Burning>(e));

        _history.Seal();
        _history.Execute(new RemoveComponentCommand(_world, e, hp));
        Assert.False(_world.Has<Hp>(e));

        _history.Undo();
        Assert.Equal(1.5f, _world.Get<Hp>(e).Armor);    // the value came back with it
        _history.Undo();
        Assert.False(_world.Has<Burning>(e));
        _history.Undo();
        Assert.False(_world.Has<Hp>(e));
        _history.Redo();
        Assert.Equal(4, _world.Get<Hp>(e).Value);
    }

    [Fact]
    public void CreatingAnEntityAndDeletingABranchAreUndoneUnderTheSameIds()
    {
        Entity a = Make("a");
        Entity b = Make("b", a);
        _world.Set(b, new Hp { Value = 8 });

        var create = new CreateEntityCommand(_world, "Create Light", () => _world.Clone(a));
        _history.Execute(create);
        Entity copy = create.Entity;
        Assert.Equal(2, _world.Roots().Count);

        _history.Undo();
        Assert.False(_world.IsAlive(copy));
        _history.Redo();
        Assert.True(_world.IsAlive(copy));                // the same id again
        Assert.Equal("a (copy)", _world.GetName(copy));
        Assert.Equal(8, _world.Get<Hp>(_world.Children(copy).Single()).Value);

        _history.Execute(new DestroyEntityCommand(_world, a));
        Assert.False(_world.IsAlive(a));
        Assert.False(_world.IsAlive(b));
        Assert.Equal("Delete a", _history.UndoName);

        _history.Undo();
        Assert.True(_world.IsAlive(b));
        Assert.Equal(a, _world.GetParent(b));
        Assert.Equal(8, _world.Get<Hp>(b).Value);
        _history.Redo();
        Assert.False(_world.IsAlive(a));
    }

    [Fact]
    public void CreatingSeveralBranchesIsOneStepUndoneAndRedoneUnderTheSameIds()
    {
        Entity a = Make("a");
        Entity b = Make("b", a);
        _world.Set(b, new Hp { Value = 5 });
        Entity c = Make("c");

        var create = new CreateEntitiesCommand(_world, "Duplicate 2 entities", () => [_world.Clone(a), _world.Clone(c)]);
        _history.Execute(create);
        Assert.Equal(2, create.Entities.Count);
        Assert.Equal(4, _world.Roots().Count);
        Assert.Equal("Duplicate 2 entities", _history.UndoName);
        Entity[] made = [.. create.Entities];
        Entity madeChild = _world.Children(made[0]).Single();

        Assert.True(_history.Undo());                       // one step takes both away, with the branch
        Assert.Equal(2, _world.Roots().Count);
        Assert.All(made, e => Assert.False(_world.IsAlive(e)));
        Assert.False(_world.IsAlive(madeChild));
        Assert.False(_history.CanUndo);

        Assert.True(_history.Redo());                       // and one step brings them back under the same ids
        Assert.All(made, e => Assert.True(_world.IsAlive(e)));
        Assert.Equal(madeChild, _world.Children(made[0]).Single());
        Assert.Equal(5, _world.Get<Hp>(madeChild).Value);

        var none = new CreateEntitiesCommand(_world, "nothing", () => []);
        none.Apply();
        none.Undo();
        Assert.Empty(none.Entities);
        Assert.False(none.TryMerge(create));
    }

    [Fact]
    public void ReparentingKeepsTheEntityWhereItIsAndIsRefusedUnderItself()
    {
        Entity parent = Make("parent", at: new Double3(10, 0, 0));
        _world.Set(parent, new Transform(new Double3(10, 0, 0), Quaternion.Identity, new Vector3(2, 2, 2)));
        Entity child = Make("child", at: new Double3(4, 6, 8));
        Entity grandchild = Make("grand", child);

        _history.Execute(new ReparentCommand(_world, child, parent));
        Assert.Equal(parent, _world.GetParent(child));
        Trs world = _world.ComputeWorldTransform(child);
        Assert.True(Double3.Distance(world.Position, new Double3(4, 6, 8)) < 1e-4);      // still where it was
        Assert.Equal(new Double3(-3, 3, 4), _world.Get<Transform>(child).Position);      // its local position changed to keep it there
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), _world.Get<Transform>(child).Scale);

        _history.Undo();
        Assert.True(_world.GetParent(child).IsNull);
        Assert.Equal(new Double3(4, 6, 8), _world.Get<Transform>(child).Position);
        Assert.Equal(Vector3.One, _world.Get<Transform>(child).Scale);

        Assert.False(ReparentCommand.IsAllowed(_world, child, grandchild));   // under its own child
        Assert.False(ReparentCommand.IsAllowed(_world, child, child));
        Assert.True(ReparentCommand.IsAllowed(_world, grandchild, parent));
        Assert.True(ReparentCommand.IsAllowed(_world, grandchild, default));
        Assert.Throws<InvalidOperationException>(() => new ReparentCommand(_world, child, grandchild).Apply());
    }

    [Fact]
    public void AMemberCannotBeReparented()
    {
        string folder = Directory.CreateTempSubdirectory("weft-member").FullName;
        try
        {
            var files = new VirtualFileSystem();
            files.Mount("res://", new DirectorySource(folder, writable: true));
            File.WriteAllText(Path.Combine(folder, "pair.aprefab"), "<Prefab version=\"1\" nextId=\"4\"><Entity id=\"1\" name=\"Pair\"><Entity id=\"2\" name=\"Left\"/><Entity id=\"3\" name=\"Right\"/></Entity></Prefab>");
            using var world = new World { Files = files };
            Entity pair = world.Instantiate("res://pair.aprefab");
            Entity left = world.Children(pair)[0];
            Entity right = world.Children(pair)[1];
            Entity other = world.CreateEntity("other");

            // An entity of an instance is named by its place in it: it does not move out, to the top level or under a sibling...
            Assert.False(ReparentCommand.IsAllowed(world, left, other));
            Assert.False(ReparentCommand.IsAllowed(world, left, default));
            Assert.False(ReparentCommand.IsAllowed(world, left, right));
            Assert.Throws<InvalidOperationException>(() => new ReparentCommand(world, left, other).Apply());
            Assert.Equal(pair, world.GetParent(left));

            // ...while the instance itself moves like any entity, and entities of the scene can go under a member.
            Assert.True(ReparentCommand.IsAllowed(world, pair, other));
            Assert.True(ReparentCommand.IsAllowed(world, other, left));
            _history.Execute(new ReparentCommand(world, pair, other));
            Assert.Equal(other, world.GetParent(pair));
            Assert.False(ReparentCommand.IsAllowed(world, other, left));         // now left is below it: the rule against cycles still holds on top
            _history.Undo();
            Assert.True(world.GetParent(pair).IsNull);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void RenamingIsUndoneAndNamesMustBeFreeAmongSiblings()
    {
        Entity a = Make("a");
        Entity b = Make("b");

        Assert.False(RenameCommand.IsFree(_world, a, "b"));
        Assert.True(RenameCommand.IsFree(_world, a, "a"));       // its own name is free for it
        Assert.True(RenameCommand.IsFree(_world, a, "c"));

        _history.Execute(new RenameCommand(_world, a, "c"));
        Assert.Equal("c", _world.GetName(a));
        _history.Undo();
        Assert.Equal("a", _world.GetName(a));
        _history.Redo();
        Assert.Equal("c", _world.GetName(a));
        Assert.Equal("b", _world.GetName(b));
    }

    [Fact]
    public void ACompositeIsOneStepAndTheHistoryIsBounded()
    {
        Entity e = Make("e");
        Entity hp = _world.Component<Hp>();
        var both = new CompositeCommand("Set up", [new AddComponentCommand(_world, e, hp, Bytes(new Hp { Value = 2 })), new RenameCommand(_world, e, "renamed")]);

        _history.Execute(both);
        Assert.Single(_history.Steps);
        Assert.Equal("renamed", _world.GetName(e));
        _history.Undo();
        Assert.Equal("e", _world.GetName(e));
        Assert.False(_world.Has<Hp>(e));

        _history.Limit = 3;
        for (int i = 0; i < 6; i++)
        {
            _history.Seal();
            _history.Execute(new RenameCommand(_world, e, $"n{i}"));
        }

        Assert.Equal(3, _history.Steps.Count);                  // the oldest are forgotten
        _history.Clear();
        Assert.False(_history.CanUndo);
    }

    [Fact]
    public void TheSavePointFollowsUndoRedoAndMerges()
    {
        Entity e = Make("e");
        Entity hp = _world.Component<Hp>();
        _world.Set(e, new Hp { Value = 1 });
        void Edit(int value, bool seal = true)
        {
            _history.Execute(SetComponentCommand.Create(_world, e, hp, Bytes(new Hp { Value = value }), "Set Hp")!);
            if (seal)
            {
                _history.Seal();
            }
        }

        Assert.True(_history.IsAtSavePoint);                    // a new history is at its save point
        Edit(2);
        Assert.False(_history.IsAtSavePoint);
        _history.MarkSaved();
        Assert.True(_history.IsAtSavePoint);

        Edit(3);
        Assert.False(_history.IsAtSavePoint);
        _history.Undo();
        Assert.True(_history.IsAtSavePoint);                    // back at the saved state: clean
        _history.Undo();
        Assert.False(_history.IsAtSavePoint);                   // before it: modified again
        _history.Redo();
        Assert.True(_history.IsAtSavePoint);
        _history.Redo();
        Assert.False(_history.IsAtSavePoint);

        // A drag after the save is a step of its own (the save ended the gesture) and merges only within itself.
        _history.Undo();                                        // at the save point again
        Edit(10, seal: false);
        Edit(11, seal: false);
        Edit(12, seal: false);
        Assert.Equal(2, _history.Steps.Count);                  // the saved edit and the drag
        Assert.False(_history.IsAtSavePoint);                   // a merge made a new state, not the saved one
        _history.Undo();
        Assert.True(_history.IsAtSavePoint);
        Assert.Equal(2, _world.Get<Hp>(e).Value);

        // A new edit after undoing from the save point cannot come back to it: the saved state's redo list is gone.
        _history.Undo();
        Edit(7);
        _history.Redo();
        Assert.False(_history.IsAtSavePoint);
        _history.Undo();
        Assert.False(_history.IsAtSavePoint);
    }

    [Fact]
    public void ClearKeepsTheSavePointWhereItWasAndAnOldSavePointDropsOutOfTheLimit()
    {
        Entity e = Make("e");
        _history.Execute(new RenameCommand(_world, e, "a"));
        _history.Seal();
        _history.Clear();                                       // modified, and the history forgot how to go back
        Assert.False(_history.IsAtSavePoint);
        _history.MarkSaved();
        Assert.True(_history.IsAtSavePoint);
        _history.Clear();                                       // saved, and still saved
        Assert.True(_history.IsAtSavePoint);

        _history.Limit = 3;
        _history.MarkSaved();
        for (int i = 0; i < 3; i++)
        {
            _history.Execute(new RenameCommand(_world, e, $"n{i}"));
            _history.Seal();
        }

        while (_history.Undo())
        {
        }

        Assert.True(_history.IsAtSavePoint);                    // the saved state is still the oldest the history can reach
        _history.Redo();
        _history.Redo();
        _history.Redo();
        _history.Execute(new RenameCommand(_world, e, "n3"));   // a fourth step pushes the first out: the saved state is gone
        _history.Seal();
        while (_history.Undo())
        {
        }

        Assert.False(_history.IsAtSavePoint);
        _history.MarkSaved();
        Assert.True(_history.IsAtSavePoint);
    }

    [Fact]
    public void PlayAndStopLeaveTheWorldAsItWasWhileTheHistoryStillWorks()
    {
        // What the editor does around a play session: snapshot, let the game change things, restore. The ids in the history stay valid.
        Entity e = Make("e");
        Entity hp = _world.Component<Hp>();
        _world.Set(e, new Hp { Value = 5 });
        _history.Execute(SetComponentCommand.Create(_world, e, hp, Bytes(new Hp { Value = 6 }), "Set Hp")!);

        WorldSnapshot before = _world.Snapshot();
        _world.Set(e, new Hp { Value = 100 });
        Entity spawned = _world.CreateEntity("spawned");
        _world.Destroy(Make("temp"));

        _world.Restore(before);

        Assert.False(_world.IsAlive(spawned));
        Assert.Equal(6, _world.Get<Hp>(e).Value);
        _history.Undo();
        Assert.Equal(5, _world.Get<Hp>(e).Value);              // the command still points at the same entity
    }

    private Entity[] Three() => [Make("a", at: new Double3(1, 0, 0)), Make("b", at: new Double3(2, 0, 0)), Make("c", at: new Double3(3, 0, 0))];

    private SetTransformsCommand? Shift(Entity[] entities, double by, string name = "Move 3 entities") =>
        SetTransformsCommand.Create(_world, entities.Select(e => (e, Transform.At(new Double3(_world.Get<Transform>(e).Position.X + by, 0, 0)))), name);

    [Fact]
    public void TransformsOfOneDragAreOneStep()
    {
        Entity[] drag = Three();
        double[] start = [1, 2, 3];

        // Five frames of one drag, each a command over the same three entities: one step, which undoes all three to where they started and redoes to where the last frame left them.
        for (int frame = 1; frame <= 5; frame++)
        {
            _history.Execute(Shift(drag, 0.5)!);
        }

        Assert.Equal(["Move 3 entities"], _history.Steps);
        Assert.Equal([3.5, 4.5, 5.5], drag.Select(e => _world.Get<Transform>(e).Position.X));

        _history.Undo();
        Assert.Equal(start, drag.Select(e => _world.Get<Transform>(e).Position.X));
        Assert.False(_history.CanUndo);

        _history.Redo();
        Assert.Equal([3.5, 4.5, 5.5], drag.Select(e => _world.Get<Transform>(e).Position.X));
    }

    [Fact]
    public void TransformsMergeOnlyWithinOneGestureOverTheSameEntities()
    {
        Entity[] all = Three();

        _history.Execute(Shift(all, 1)!);
        _history.Seal();                                           // the drag ended: the next one is a step of its own
        _history.Execute(Shift(all, 1)!);
        Assert.Equal(2, _history.Steps.Count);

        _history.Seal();
        _history.Execute(Shift(all[..2], 1)!);                     // another set of entities
        _history.Execute(Shift(all, 1)!);
        Assert.Equal(4, _history.Steps.Count);

        // The entities in another order are the same set.
        _history.Seal();
        _history.Execute(Shift(all, 1)!);
        _history.Execute(Shift([all[2], all[0], all[1]], 1)!);
        Assert.Equal(5, _history.Steps.Count);
        Assert.Equal([7.0, 8.0, 8.0], all.Select(e => _world.Get<Transform>(e).Position.X));
    }

    [Fact]
    public void ATransformsCommandForWhatIsAlreadyThereIsNothingAndADeadEntityIsSkipped()
    {
        Entity[] all = Three();
        Assert.Null(SetTransformsCommand.Create(_world, all.Select(e => (e, _world.Get<Transform>(e))), "Move"));

        Entity noTransform = _world.CreateEntity("bare");
        Assert.Null(SetTransformsCommand.Create(_world, [(noTransform, Transform.Identity)], "Move"));      // nothing to set a Transform on

        SetTransformsCommand command = Shift(all, 10)!;
        _history.Execute(command);
        _world.Destroy(all[1]);
        _history.Undo();                                           // the dead one is left alone, the others come back
        Assert.False(_world.IsAlive(all[1]));
        Assert.Equal(1, _world.Get<Transform>(all[0]).Position.X);
        Assert.Equal(3, _world.Get<Transform>(all[2]).Position.X);
    }

    [Fact]
    public void ATransformsCommandMergesOnlyWithTheSameSetAndTheSameName()
    {
        Entity[] all = Three();
        Entity other = Make("d", at: new Double3(9, 0, 0));

        _history.Execute(Shift(all, 1)!);
        _history.Execute(Shift([all[0], all[1], other], 1)!);       // the same number of entities, but not the same ones
        Assert.Equal(2, _history.Steps.Count);

        _history.Seal();
        _history.Execute(Shift(all, 1)!);
        _history.Execute(Shift(all, 1, "Rotate 3 entities")!);      // a rotation does not fold into a move
        Assert.Equal(4, _history.Steps.Count);
    }

    [Fact]
    public void AMergedDragLeavesTheSavePointAndUndoingItComesBack()
    {
        Entity[] drag = Three();
        _history.MarkSaved();

        for (int frame = 0; frame < 4; frame++)
        {
            _history.Execute(Shift(drag, 0.25)!);
        }

        Assert.False(_history.IsAtSavePoint);                        // each merged frame is a new state
        _history.Seal();
        Assert.False(_history.IsAtSavePoint);

        _history.Undo();
        Assert.True(_history.IsAtSavePoint);
        _history.Redo();
        Assert.False(_history.IsAtSavePoint);
    }
}

