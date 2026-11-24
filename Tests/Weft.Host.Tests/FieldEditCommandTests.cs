using System;
using System.Collections.Generic;
using System.Numerics;
using Weft.Ecs;
using Weft.Host.Editing;
using Weft.Native.Tests;
using Weft.Reflection;
using Weft.Rendering;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;

namespace Weft.Host.Tests;

/// <summary>An inspector edit of several entities: one field written on each of them, as one step that the frames of a drag merge into. Over a headless world.</summary>
public sealed class FieldEditCommandTests : IDisposable
{
    private readonly World _world;
    private readonly EditHistory _history = new();
    private readonly Entity _hp;
    private readonly ComponentInfo _info;

    public FieldEditCommandTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        _world = new World();
        _hp = _world.Component<Hp>();
        _info = _world.GetComponentInfo(_hp)!;
    }

    public void Dispose() => _world.Dispose();

    private Entity Make(string name, int value, float armor)
    {
        Entity e = _world.CreateEntity(name);
        _world.Set(e, new Hp { Value = value, Armor = armor });
        return e;
    }

    private static byte[] Int(int value) => BitConverter.GetBytes(value);

    private int ValueOf(Entity e) => _world.Get<Hp>(e).Value;

    private float ArmorOf(Entity e) => _world.Get<Hp>(e).Armor;

    private IEditCommand Set(IReadOnlyList<Entity> entities, int value) =>
        FieldEditCommands.SetBytes(_world, entities, _hp, _info.FindField("Value")!.Offset, Int(value), "Edit Hp.Value")!;

    [Fact]
    public void AnEditWritesOnlyThatFieldOnEveryEntity()
    {
        Entity a = Make("a", 1, 0.5f);
        Entity b = Make("b", 2, 1.5f);

        _history.Execute(Set([a, b], 9));

        Assert.Equal(9, ValueOf(a));
        Assert.Equal(9, ValueOf(b));
        Assert.Equal(0.5f, ArmorOf(a));    // the other field of each keeps its own value
        Assert.Equal(1.5f, ArmorOf(b));

        _history.Undo();
        Assert.Equal(1, ValueOf(a));
        Assert.Equal(2, ValueOf(b));
        _history.Redo();
        Assert.Equal(9, ValueOf(b));
    }

    [Fact]
    public void OneEntityGivesAPlainSetAndSeveralGiveAComposite()
    {
        Entity a = Make("a", 1, 0);
        Entity b = Make("b", 2, 0);

        Assert.IsType<SetComponentCommand>(Set([a], 5));
        Assert.IsType<CompositeCommand>(Set([a, b], 5));
    }

    [Fact]
    public void NothingChangingGivesNoCommandAndAnEntityWithoutTheComponentIsLeftOut()
    {
        Entity a = Make("a", 4, 0);
        Entity bare = _world.CreateEntity("bare");

        Assert.Null(FieldEditCommands.SetBytes(_world, [a, bare], _hp, 0, Int(4), "same"));

        IEditCommand? set = FieldEditCommands.SetBytes(_world, [a, bare], _hp, 0, Int(6), "set");
        Assert.IsType<SetComponentCommand>(set);   // only one entity has it
        set!.Apply();
        Assert.Equal(6, ValueOf(a));
        Assert.False(_world.Has<Hp>(bare));
    }

    [Fact]
    public void CompositesOfTheSameTargetsMerge()
    {
        Entity a = Make("a", 1, 0);
        Entity b = Make("b", 2, 0);

        // The frames of one drag: each is a composite of the same two entities; the last value stays and undo goes back to the start.
        _history.Execute(Set([a, b], 3));
        _history.Execute(Set([a, b], 4));
        _history.Execute(Set([b, a], 5));    // the order of the targets does not matter

        Assert.Single(_history.Steps);
        Assert.Equal(5, ValueOf(a));
        _history.Undo();
        Assert.Equal(1, ValueOf(a));
        Assert.Equal(2, ValueOf(b));
        _history.Redo();
        Assert.Equal(5, ValueOf(a));
        Assert.Equal(5, ValueOf(b));
    }

    [Fact]
    public void AFrameThatLeavesAnEntityUnchangedStillMerges()
    {
        Entity a = Make("a", 7, 0);
        Entity b = Make("b", 2, 0);

        _history.Execute(Set([a, b], 7));    // a already has 7: it is a part with nothing to change, but it is still a part
        _history.Execute(Set([a, b], 8));

        Assert.Single(_history.Steps);
        _history.Undo();
        Assert.Equal(7, ValueOf(a));
        Assert.Equal(2, ValueOf(b));
    }

    [Fact]
    public void SealingEndsTheGestureAndDifferentTargetsDoNotMerge()
    {
        Entity a = Make("a", 1, 0);
        Entity b = Make("b", 2, 0);
        Entity c = Make("c", 3, 0);

        _history.Execute(Set([a, b], 10));
        _history.Execute(Set([a, c], 11));      // other targets: a step of its own
        _history.Seal();
        _history.Execute(Set([a, c], 12));      // sealed: another step
        Assert.Equal(3, _history.Steps.Count);

        _history.Undo();
        Assert.Equal(11, ValueOf(a));
        _history.Undo();
        Assert.Equal(10, ValueOf(a));
        Assert.Equal(3, ValueOf(c));
    }

    [Fact]
    public void CompositesThatAreNotOnlyFieldEditsNeverMerge()
    {
        Entity a = Make("a", 1, 0);
        Entity b = Make("b", 2, 0);

        var first = new CompositeCommand("one", [Set([a], 5), new RenameCommand(_world, a, "renamed")]);
        IEditCommand second = new CompositeCommand("two", [Set([a], 6), new RenameCommand(_world, a, "again")]);
        Assert.False(first.TryMerge(second));

        // And a composite of field edits does not take a single edit, or one with another number of parts.
        IEditCommand pair = Set([a, b], 7);
        Assert.False(pair.TryMerge(Set([a], 8)));
        Assert.False(Set([a], 8).TryMerge(pair));
    }

    [Fact]
    public void ARegionsEditWritesSeveralPlacesAtOnce()
    {
        Entity a = Make("a", 1, 0.25f);
        Entity b = Make("b", 2, 0.75f);

        IEditCommand command = FieldEditCommands.SetRegions(_world, [a, b], _hp, [(0, Int(8)), (_info.FindField("Armor")!.Offset, BitConverter.GetBytes(2f))], "Edit Hp")!;
        command.Apply();

        Assert.Equal((8, 2f), (ValueOf(a), ArmorOf(a)));
        Assert.Equal((8, 2f), (ValueOf(b), ArmorOf(b)));
        Assert.Throws<ArgumentOutOfRangeException>(() => FieldEditCommands.SetBytes(_world, [a], _hp, 6, Int(1), "past the end"));
    }
}

/// <summary>What a component starts as: a table by name, seeded with the engine's components.</summary>
public sealed class ComponentDefaultsTests
{
    private static ComponentInfo Info(string name, int size) => new(name, null, size, 4, size == 0 ? [] : [new FieldDef("Value", FieldKind.Int32, 0)]);

    [Fact]
    public void ARegisteredDefaultIsCopiedInAndReadBack()
    {
        byte[] bytes = [1, 2, 3, 4];
        ComponentDefaults.Register("DefaultsCopy", bytes);
        bytes[0] = 99;    // the table has its own copy

        Assert.True(ComponentDefaults.TryGet("DefaultsCopy", out ReadOnlySpan<byte> read));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, read.ToArray());
        Assert.False(ComponentDefaults.TryGet("DefaultsNobodyRegistered", out _));

        Assert.True(ComponentDefaults.Remove("DefaultsCopy"));
        Assert.False(ComponentDefaults.TryGet("DefaultsCopy", out _));
        Assert.False(ComponentDefaults.Remove("DefaultsCopy"));
    }

    [Fact]
    public void AComponentStartsAsItsDefaultOrAsZerosOrNothingForATag()
    {
        ComponentInfo plain = Info("DefaultsPlain", 4);
        Assert.Equal(new byte[4], ComponentDefaults.ValueFor(plain));       // nothing registered: zeros, as a script component starts

        ComponentDefaults.Register("DefaultsPlain", [7, 0, 0, 0]);
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, ComponentDefaults.ValueFor(plain));
        Assert.NotSame(ComponentDefaults.ValueFor(plain), ComponentDefaults.ValueFor(plain));    // a copy each time

        // A default of another size (the layout changed since) is not used.
        ComponentDefaults.Register("DefaultsPlain", [7, 0]);
        Assert.Equal(new byte[4], ComponentDefaults.ValueFor(plain));
        ComponentDefaults.Remove("DefaultsPlain");

        Assert.Null(ComponentDefaults.ValueFor(Info("DefaultsTag", 0)));
    }

    [Fact]
    public void TheEngineDefaultsAreAUsableTransformCameraAndLight()
    {
        ComponentDefaults.RegisterEngine();
        ComponentDefaults.RegisterEngine();    // again: harmless

        Transform identity = Transform.Identity;
        Assert.True(ComponentDefaults.TryGet(nameof(Transform), out ReadOnlySpan<byte> transform));
        Assert.Equal(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<Transform>(in identity)).ToArray(), transform.ToArray());
        Assert.Equal(Vector3.One, System.Runtime.InteropServices.MemoryMarshal.Read<Transform>(transform).Scale);

        Assert.True(ComponentDefaults.TryGet(nameof(CameraComponent), out ReadOnlySpan<byte> camera));
        Assert.Equal(60f, System.Runtime.InteropServices.MemoryMarshal.Read<CameraComponent>(camera).FovDegrees);

        Assert.True(ComponentDefaults.TryGet(nameof(Light), out ReadOnlySpan<byte> light));
        Light point = System.Runtime.InteropServices.MemoryMarshal.Read<Light>(light);
        Assert.Equal(Vector3.One, point.Color);
        Assert.Equal(80f, point.Intensity);
        Assert.Equal((byte)Weft.Content.LightKind.Point, point.Kind);
    }
}
