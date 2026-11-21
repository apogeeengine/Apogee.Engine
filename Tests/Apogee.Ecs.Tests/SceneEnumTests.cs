using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Ecs;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Ecs.Tests;

public enum Gait : byte
{
    Idle,
    Walk,
    Run,
}

[Flags]
public enum Layers : uint
{
    None = 0,
    World = 1,
    Water = 2,
    Fog = 4,
}

/// <summary>A component with enum fields: a declared one, a flags one and a plain integer that names an enum.</summary>
[Component]
public struct Strider
{
    public Gait Gait;

    public Layers Mask;

    [Property(Enum = typeof(Gait))]
    public byte Raw;

    public int Count;
}

/// <summary>Enum fields of components: still integers in memory, in scenes, in snapshots and clones, whatever an inspector shows.</summary>
public sealed class SceneEnumTests : IDisposable
{
    private readonly World _world;

    public SceneEnumTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        _world = new World();
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public void TheRegistryKnowsWhichEnumAFieldNames()
    {
        ComponentInfo info = TypeRegistry.GetComponent<Strider>();
        Assert.Equal([("Gait", FieldKind.UInt8, "Gait"), ("Mask", FieldKind.UInt32, "Layers"), ("Raw", FieldKind.UInt8, "Gait"), ("Count", FieldKind.Int32, null)],
            info.Fields.Select(f => (f.Name, f.Kind, f.Enum)));
        Assert.Equal(["idle", "walk", "run"], TypeRegistry.GetEnum("Gait").Members.Select(m => m.Name));
        Assert.True(TypeRegistry.GetEnum("Layers").IsFlags);
    }

    [Fact]
    public void AnEnumFieldStillSavesAsANumber()
    {
        Entity e = _world.CreateEntity("runner");
        _world.Set(e, new Strider { Gait = Gait.Run, Mask = Layers.World | Layers.Fog, Raw = 1, Count = 3 });

        string text = _world.SaveScene();

        // Numbers, never member names: a member can be renamed without breaking a file.
        Assert.Contains("Gait=\"2\"", text, StringComparison.Ordinal);
        Assert.Contains("Mask=\"5\"", text, StringComparison.Ordinal);
        Assert.Contains("Raw=\"1\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Run\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Fog", text, StringComparison.Ordinal);

        using var fresh = new World();
        Entity loaded = Assert.Single(fresh.LoadScene(text));
        Strider back = fresh.Get<Strider>(loaded);
        Assert.Equal(Gait.Run, back.Gait);
        Assert.Equal(Layers.World | Layers.Fog, back.Mask);
        Assert.Equal(1, back.Raw);
        Assert.Equal(3, back.Count);
    }

    [Fact]
    public void AValueWithNoMemberSurvivesASaveAndALoad()
    {
        Entity e = _world.CreateEntity("odd");
        _world.Set(e, new Strider { Gait = (Gait)9, Mask = (Layers)0xFFFFFFFF });

        using var fresh = new World();
        Entity loaded = Assert.Single(fresh.LoadScene(_world.SaveScene()));
        Assert.Equal((Gait)9, fresh.Get<Strider>(loaded).Gait);
        Assert.Equal((Layers)0xFFFFFFFF, fresh.Get<Strider>(loaded).Mask);   // the top bit of the biggest integer a flags enum can have
    }

    [Fact]
    public void ASceneThatNamesAMemberIsNotAFileThisEngineWrites()
    {
        // Text that is not a number leaves the field as it was and the scene loads on.
        List<Entity> loaded = _world.LoadScene("""
            <Scene version="1">
              <Entity id="1" name="a"><Strider Gait="run" Count="4"/></Entity>
            </Scene>
            """);
        Strider read = _world.Get<Strider>(loaded[0]);
        Assert.Equal(Gait.Idle, read.Gait);
        Assert.Equal(4, read.Count);
    }

    [Fact]
    public void SnapshotsAndClonesKeepTheNumber()
    {
        Entity e = _world.CreateEntity("a");
        _world.Set(e, new Strider { Gait = Gait.Walk, Mask = Layers.Water });

        Entity copy = _world.Clone(e);
        Assert.Equal(Gait.Walk, _world.Get<Strider>(copy).Gait);
        Assert.Equal(Layers.Water, _world.Get<Strider>(copy).Mask);

        // A snapshot taken, the component overwritten, and the snapshot restored: the numbers come back.
        WorldSnapshot snapshot = _world.Snapshot();
        _world.Set(e, new Strider { Gait = Gait.Run, Mask = Layers.None });
        _world.RestoreEntities(snapshot);
        Assert.Equal(Gait.Walk, _world.Get<Strider>(e).Gait);
        Assert.Equal(Layers.Water, _world.Get<Strider>(e).Mask);
    }
}
