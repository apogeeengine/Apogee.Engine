using System;
using System.Linq;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Host.Editing;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Host.Tests;

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
}
