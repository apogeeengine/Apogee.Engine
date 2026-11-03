using System;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Ecs.Tests;

public unsafe class TransformTests
{
    private static World NewWorld()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        return new World();
    }

    private static Entity Spawn(World world, Transform transform, Entity parent = default)
    {
        Entity entity = world.CreateEntity();
        if (!parent.IsNull)
        {
            world.Add(entity, World.Pair(new Entity(Flecs.ChildOf), parent));
        }

        world.Set(entity, transform);
        return entity;
    }

    [Fact]
    public void ARootEntitysWorldTransformIsItsOwn()
    {
        using World world = NewWorld();
        Entity root = Spawn(world, new Transform(new Double3(1_000_000, 2, -3_000_000), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), new Vector3(2)));

        world.Progress(0.016f);

        WorldTransform result = world.Get<WorldTransform>(root);
        Assert.Equal(new Double3(1_000_000, 2, -3_000_000), result.Position);
        Assert.Equal(new Vector3(2), result.Scale);
    }

    [Fact]
    public void AddingATransformAddsItsWorldTransform()
    {
        using World world = NewWorld();
        Entity entity = world.CreateEntity();

        world.Add<Transform>(entity);

        Assert.True(world.Has<WorldTransform>(entity));
    }

    [Fact]
    public void TheWorldTransformCanBeComputedWithoutAFrame()
    {
        using World world = NewWorld();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        Entity root = world.CreateEntity();
        Entity child = world.CreateEntity(parent: root);
        Entity grandchild = world.CreateEntity(parent: child);
        world.Set(root, Transform.At(new Double3(5_000_000, 100, 0)));
        world.Set(child, new Transform(new Double3(10, 0, 0), turn, new Vector3(2, 2, 2)));
        world.Set(grandchild, Transform.At(new Double3(0, 0, 1)));

        Trs computed = world.ComputeWorldTransform(grandchild); // no Progress yet: WorldTransform is still zeros

        Assert.True(Double3.Distance(computed.Position, new Double3(5_000_012, 100, 0)) < 1e-4, computed.Position.ToString());
        Assert.Equal(new Vector3(2, 2, 2), computed.Scale);
        world.Progress(0.016f);
        Assert.Equal(world.Get<WorldTransform>(grandchild).Position, computed.Position); // the same as the system's
        Assert.Equal(Trs.Identity.Position, world.ComputeWorldTransform(world.CreateEntity()).Position); // no Transform: the identity
    }

    [Fact]
    public void AnEntityCanBeReparentedAndMadeARootAgain()
    {
        using World world = NewWorld();
        Entity a = world.CreateEntity();
        Entity b = world.CreateEntity();
        Entity child = world.CreateEntity(parent: a);
        world.Set(a, Transform.At(new Double3(10, 0, 0)));
        world.Set(b, Transform.At(new Double3(0, 20, 0)));
        world.Set(child, Transform.At(new Double3(1, 1, 1)));
        world.Progress(0.016f);
        Assert.Equal(new Double3(11, 1, 1), world.Get<WorldTransform>(child).Position);

        world.SetParent(child, b);
        world.Progress(0.016f);
        Assert.Equal(b, world.GetParent(child));
        Assert.Equal(new Double3(1, 21, 1), world.Get<WorldTransform>(child).Position);

        world.SetParent(child, default);
        world.Progress(0.016f);
        Assert.Equal(0ul, world.GetParent(child).Id);
        Assert.Equal(new Double3(1, 1, 1), world.Get<WorldTransform>(child).Position);
    }

    [Fact]
    public void ChildrenFollowTheirParentsThroughThreeLevels()
    {
        using World world = NewWorld();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);

        // Created grandchild first, to prove that order of creation does not matter: parents are always computed before children.
        Entity root = world.CreateEntity();
        Entity child = world.CreateEntity();
        Entity grandchild = world.CreateEntity();
        world.Add(grandchild, World.Pair(new Entity(Flecs.ChildOf), child));
        world.Add(child, World.Pair(new Entity(Flecs.ChildOf), root));
        world.Set(grandchild, Transform.At(new Double3(0, 0, 1)));
        world.Set(child, new Transform(new Double3(10, 0, 0), turn, new Vector3(2, 2, 2)));
        world.Set(root, Transform.At(new Double3(5_000_000, 100, 0)));

        world.Progress(0.016f);

        // child: root + (10, 0, 0). grandchild: child's world, then (0, 0, 1) scaled by 2 and turned a quarter about +Y: (z, y, -x) → (2, 0, 0).
        Assert.Equal(new Double3(5_000_010, 100, 0), world.Get<WorldTransform>(child).Position);
        Double3 far = world.Get<WorldTransform>(grandchild).Position;
        Assert.True(Double3.Distance(far, new Double3(5_000_012, 100, 0)) < 1e-4, far.ToString());
        Assert.Equal(new Vector3(2, 2, 2), world.Get<WorldTransform>(grandchild).Scale);
    }

    [Fact]
    public void MovingTheParentMovesTheChildOnTheNextFrame()
    {
        using World world = NewWorld();
        Entity parent = Spawn(world, Transform.At(Double3.Zero));
        Entity child = Spawn(world, Transform.At(new Double3(1, 0, 0)), parent);
        world.Progress(0.016f);
        Assert.Equal(1, world.Get<WorldTransform>(child).Position.X);

        world.Ensure<Transform>(parent).Position = new Double3(100, 0, 0);
        world.Progress(0.016f);

        Assert.Equal(101, world.Get<WorldTransform>(child).Position.X);
    }

    [Fact]
    public void ReparentingAChildUsesTheNewParent()
    {
        using World world = NewWorld();
        Entity a = Spawn(world, Transform.At(new Double3(100, 0, 0)));
        Entity b = Spawn(world, Transform.At(new Double3(-100, 0, 0)));
        Entity child = Spawn(world, Transform.At(new Double3(1, 0, 0)), a);
        world.Progress(0.016f);
        Assert.Equal(101, world.Get<WorldTransform>(child).Position.X);

        world.Remove(child, World.Pair(new Entity(Flecs.ChildOf), a));
        world.Add(child, World.Pair(new Entity(Flecs.ChildOf), b));
        world.Progress(0.016f);

        Assert.Equal(-99, world.Get<WorldTransform>(child).Position.X);
    }

    [Fact]
    public void TheComponentsAreDescribedForFlecsAndKeepTheirFullPrecision()
    {
        using World world = NewWorld();
        Entity id = world.Component<Transform>();
        ComponentInfo info = world.GetComponentInfo(id)!;

        Assert.Equal(56, info.Size); // 24 + 16 + 12, padded to the 8-byte alignment of the position
        Assert.Equal(8, info.Alignment);
        Assert.Equal(FieldKind.Double3, info.FindField("Position")!.Kind);
        Assert.Equal(FieldKind.Quaternion, info.FindField("Rotation")!.Kind);
        Assert.Equal(FieldKind.Vector3, info.FindField("Scale")!.Kind);

        string json = world.ToJson(new Transform(new Double3(1234567.890123456, 0, 0), Quaternion.Identity, Vector3.One));
        Assert.Contains("1234567.890123456", json);
        Assert.Contains("\"Rotation\":{\"x\":0, \"y\":0, \"z\":0, \"w\":1}", json);
    }

    [Fact]
    public void FieldAccessNarrowsAPositionForScriptsAndWidensItBack()
    {
        using World world = NewWorld();
        Entity id = world.Component<Transform>();
        ComponentInfo info = world.GetComponentInfo(id)!;
        var transform = Transform.Identity;
        byte* data = (byte*)&transform;

        FieldAccess.Write(data, info.FindField("Position")!, Value.Vector(new Vector3(1.5f, -2, 3)));
        FieldAccess.Write(data, info.FindField("Rotation")!, Value.Quat(new Quaternion(0, 1, 0, 0)));

        Assert.Equal(new Double3(1.5, -2, 3), transform.Position);
        Assert.Equal(new Quaternion(0, 1, 0, 0), transform.Rotation);
        Assert.Equal(new Vector3(1.5f, -2, 3), FieldAccess.Read(data, info.FindField("Position")!).ToVector3());
        Assert.Equal(new Quaternion(0, 1, 0, 0), FieldAccess.Read(data, info.FindField("Rotation")!).ToQuaternion());
        Assert.Throws<ValueException>(() => FieldAccess.Write(data, info.FindField("Rotation")!, Value.Vector(Vector3.One)));
    }
}
