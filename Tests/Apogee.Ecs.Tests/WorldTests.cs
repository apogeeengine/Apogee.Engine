using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Apogee.Ecs;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Ecs.Tests;

[Component]
public struct Position
{
    public Vector3 Value;
}

[Component]
public struct Velocity
{
    public Vector3 Value;
}

[Component]
public struct Health
{
    public int Current;
    public int Max;
    public float Regen;
}

[Component]
public struct Marks
{
    public bool Alive;
    public byte Level;
    public Entity Target;
    public double Mass;
}

[Component]
public struct Frozen
{
}

public unsafe class WorldTests
{
    private static World NewWorld()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        return new World();
    }

    // ---- Components -----------------------------------------------------------------------------------------------------

    [Fact]
    public void AComponentIsRegisteredWithItsFieldsForFlecs()
    {
        using World world = NewWorld();
        Entity health = world.Component<Health>();

        Assert.Equal(health, world.Lookup("Health"));
        Assert.Equal(health, world.Component<Health>()); // the same entity every time
        ComponentInfo info = world.GetComponentInfo(health)!;
        Assert.Equal(12, info.Size);

        // Flecs' reflection knows the fields, so it can print a value (and a script or a tool could inspect one).
        string json = world.ToJson(new Health { Current = 5, Max = 10, Regen = 0.5f });
        Assert.Contains("\"Current\":5", json);
        Assert.Contains("\"Max\":10", json);
        Assert.Contains("\"Regen\":0.5", json);
    }

    [Fact]
    public void FieldsOfEveryKindAreDescribed()
    {
        using World world = NewWorld();
        var marks = new Marks { Alive = true, Level = 7, Target = new Entity(42), Mass = 70.5 };
        string json = world.ToJson(marks);
        Assert.Contains("\"Alive\":true", json);
        Assert.Contains("\"Level\":7", json);
        Assert.Contains("\"Mass\":70.5", json);

        string vector = world.ToJson(new Position { Value = new Vector3(1, 2, 3) });
        Assert.Contains("\"x\":1", vector);
        Assert.Contains("\"z\":3", vector);
    }

    [Fact]
    public void ComponentsAreSetReadAndRemoved()
    {
        using World world = NewWorld();
        Entity e = world.CreateEntity();

        Assert.False(world.Has<Health>(e));
        Assert.False(world.TryGet(e, out Health _));

        world.Set(e, new Health { Current = 3, Max = 9 });
        Assert.True(world.Has<Health>(e));
        Assert.Equal(3, world.Get<Health>(e).Current);
        Assert.True(world.TryGet(e, out Health read));
        Assert.Equal(9, read.Max);

        world.Ensure<Health>(e).Current = 8;
        world.Modified<Health>(e);
        Assert.Equal(8, world.Get<Health>(e).Current);

        world.Remove<Health>(e);
        Assert.False(world.Has<Health>(e));
        Assert.Throws<KeyNotFoundException>(() => world.Get<Health>(e));
    }

    [Fact]
    public void ATagIsAddedAndRemovedWithoutData()
    {
        using World world = NewWorld();
        Entity e = world.CreateEntity();

        world.Add<Frozen>(e);
        Assert.True(world.Has<Frozen>(e));
        Assert.True(world.GetComponentInfo(world.Component<Frozen>())!.IsTag);
        world.Remove<Frozen>(e);
        Assert.False(world.Has<Frozen>(e));
    }

    [Fact]
    public void ComponentsAreRegisteredPerWorld()
    {
        using World a = NewWorld();
        using World b = NewWorld();
        a.Component<Position>();
        Entity inB = b.Component<Health>(); // registered in another order
        Assert.True(b.Lookup("Position").IsNull);
        Assert.Equal(inB, b.Lookup("Health"));
        Assert.Equal(a.Lookup("Position"), a.Component<Position>());
    }

    // ---- Components defined at run time ----------------------------------------------------------------------------------

    [Fact]
    public void ARuntimeComponentIsLaidOutByFieldName()
    {
        using World world = NewWorld();
        Entity spin = world.DefineComponent("Spin", [("speed", FieldKind.Float32), ("on", FieldKind.Bool), ("axis", FieldKind.Vector3)]);
        ComponentInfo info = world.GetComponentInfo(spin)!;

        Assert.Equal(["axis", "on", "speed"], info.Fields.Select(f => f.Name));
        Assert.Equal([0, 12, 16], info.Fields.Select(f => f.Offset));
        Assert.Equal(20, info.Size);
        Assert.Equal(4, info.Alignment);
        Assert.Null(info.ClrType);

        // The same schema, in any order, is the same component; another under the same name is an error.
        Assert.Equal(spin, world.DefineComponent("Spin", [("axis", FieldKind.Vector3), ("speed", FieldKind.Float32), ("on", FieldKind.Bool)]));
        Assert.Throws<InvalidOperationException>(() => world.DefineComponent("Spin", [("speed", FieldKind.Float64)]));
    }

    [Fact]
    public void ARuntimeComponentIsReadAndWrittenThroughValues()
    {
        using World world = NewWorld();
        Entity spin = world.DefineComponent("Spin", [("speed", FieldKind.Float32), ("on", FieldKind.Bool), ("axis", FieldKind.Vector3)]);
        ComponentInfo info = world.GetComponentInfo(spin)!;
        Entity e = world.CreateEntity();

        byte* data = world.EnsurePointer(e, spin, info.Size);
        FieldAccess.Write(data, info.FindField("speed")!, Value.Float(2.5));
        FieldAccess.Write(data, info.FindField("on")!, Value.Bool(true));
        FieldAccess.Write(data, info.FindField("axis")!, Value.Vector(new Vector3(1, 2, 3)));
        world.Modified(e, spin);

        byte* read = world.GetPointer(e, spin);
        Assert.Equal(2.5, FieldAccess.Read(read, info.FindField("speed")!).ToDouble());
        Assert.True(FieldAccess.Read(read, info.FindField("on")!).ToBool());
        Assert.Equal(new Vector3(1, 2, 3), FieldAccess.Read(read, info.FindField("axis")!).ToVector3());

        string json = world.ToJson(spin, read);
        Assert.Contains("\"speed\":2.5", json);
        Assert.Contains("\"on\":true", json);
    }

    // ---- Entities --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnEntityHasANameAndAParent()
    {
        using World world = NewWorld();
        Entity parent = world.CreateEntity("parent");
        Entity child = world.CreateEntity("child", parent);

        Assert.Equal("parent", world.GetName(parent));
        Assert.Equal(parent, world.Lookup("parent"));
        Assert.Equal(child, world.Lookup("parent.child"));
        Assert.Equal(parent, world.GetParent(child));
        Assert.Equal(parent, world.CreateEntity("parent")); // a name is found again, not duplicated
        Assert.True(world.Lookup("nothing").IsNull);
    }

    [Fact]
    public void ADestroyedEntityStaysDeadWhenItsNumberIsReused()
    {
        using World world = NewWorld();
        Entity first = world.CreateEntity();
        world.Destroy(first);
        Assert.False(world.IsAlive(first));

        List<Entity> later = [.. Enumerable.Range(0, 20).Select(_ => world.CreateEntity())];

        Assert.DoesNotContain(first, later);
        Assert.False(world.IsAlive(first));
        Assert.All(later, e => Assert.True(world.IsAlive(e)));
    }

    // ---- Queries --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AQueryVisitsEveryEntityThatHasTheComponents()
    {
        using World world = NewWorld();
        for (int i = 0; i < 5; i++)
        {
            Entity e = world.CreateEntity();
            world.Set(e, new Position { Value = new Vector3(i, 0, 0) });
            if (i % 2 == 0)
            {
                world.Set(e, new Velocity { Value = Vector3.One });
            }
        }

        using Query<Position, Velocity> moving = world.Query<Position, Velocity>();
        using Query<Position> all = world.Query<Position>();
        Assert.Equal(3, moving.Count());
        Assert.Equal(5, all.Count());

        float sum = 0;
        moving.Each((Entity e, ref Position p, ref Velocity v) =>
        {
            p.Value += v.Value;
            sum += p.Value.X;
        });
        Assert.Equal(1 + 3 + 5, sum); // positions 0, 2, 4 moved by one
    }

    [Fact]
    public void AQueryCanExcludeAComponent()
    {
        using World world = NewWorld();
        Entity position = world.Component<Position>();
        Entity frozen = world.Component<Frozen>();
        for (int i = 0; i < 4; i++)
        {
            Entity e = world.CreateEntity();
            world.Set(e, new Position());
            if (i < 3)
            {
                world.Add<Frozen>(e);
            }
        }

        using Query query = world.CreateQuery([new QueryTerm(position), new QueryTerm(frozen, QueryOperator.Not)]);
        Assert.Equal(1, query.Count());
    }

    [Fact]
    public void StructuralChangesInsideAnIterationAreAppliedAfterIt()
    {
        using World world = NewWorld();
        for (int i = 0; i < 10; i++)
        {
            Entity e = world.CreateEntity();
            world.Set(e, new Position());
            world.Set(e, new Velocity());
        }

        using Query<Position, Velocity> query = world.Query<Position, Velocity>();
        int visited = 0;
        query.Each((Entity e, ref Position p, ref Velocity v) =>
        {
            visited++;
            world.Remove<Velocity>(e); // would move the entity to another table mid-iteration
        });

        Assert.Equal(10, visited);
        Assert.Equal(0, query.Count());
    }

    [Fact]
    public void AnExceptionInAnIterationEndsItCleanly()
    {
        using World world = NewWorld();
        world.Set(world.CreateEntity(), new Position());
        using Query<Position> query = world.Query<Position>();

        Assert.Throws<InvalidOperationException>(() => query.Each((Entity e, ref Position p) => throw new InvalidOperationException("stop")));

        Assert.Equal(1, query.Count()); // the query and the world still work
        world.Set(world.CreateEntity(), new Position());
    }

    // ---- Systems --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASystemRunsEveryFrameWithTheDeltaTime()
    {
        using World world = NewWorld();
        Entity e = world.CreateEntity();
        world.Set(e, new Position());
        world.Set(e, new Velocity { Value = new Vector3(2, 0, 0) });
        world.AddSystem("Move", Phase.OnUpdate, (Entity _, ref Position p, ref Velocity v) => p.Value += v.Value * world.DeltaTime);

        world.Progress(0.5f);
        world.Progress(0.25f);

        Assert.Equal(1.5f, world.Get<Position>(e).Value.X);
        Assert.Equal(0.25f, world.DeltaTime);
    }

    [Fact]
    public void SystemsRunInTheirPhasesAndThenInTheOrderTheyWereAdded()
    {
        using World world = NewWorld();
        world.Set(world.CreateEntity(), new Position());
        var order = new List<string>();
        world.AddSystem("Store", Phase.OnStore, (Entity _, ref Position p) => order.Add("store"));
        world.AddSystem("Post", Phase.PostUpdate, (Entity _, ref Position p) => order.Add("post"));
        world.AddSystem("UpdateA", Phase.OnUpdate, (Entity _, ref Position p) => order.Add("updateA"));
        world.AddSystem("UpdateB", Phase.OnUpdate, (Entity _, ref Position p) => order.Add("updateB"));
        world.AddSystem("Pre", Phase.PreUpdate, (Entity _, ref Position p) => order.Add("pre"));
        world.AddSystem("Load", Phase.OnLoad, (Entity _, ref Position p) => order.Add("load"));

        world.Progress(1);

        Assert.Equal(["load", "pre", "updateA", "updateB", "post", "store"], order);
    }

    [Fact]
    public void ARawSystemSeesTheTableAndItsFields()
    {
        using World world = NewWorld();
        Entity position = world.Component<Position>();
        Entity health = world.Component<Health>();
        for (int i = 0; i < 3; i++)
        {
            Entity e = world.CreateEntity();
            world.Set(e, new Position { Value = new Vector3(i, 0, 0) });
            world.Set(e, new Health { Current = i * 10 });
        }

        int rows = 0;
        float sumX = 0;
        int sumHealth = 0;
        world.AddSystem("Sum", [new QueryTerm(position), new QueryTerm(health)], Phase.OnUpdate, (in TableView view) =>
        {
            rows += view.Count;
            foreach (Position p in view.Field<Position>(0))
            {
                sumX += p.Value.X;
            }

            foreach (Health h in view.Field<Health>(1))
            {
                sumHealth += h.Current;
            }
        });

        world.Progress(0);

        Assert.Equal(3, rows);
        Assert.Equal(3, sumX);
        Assert.Equal(30, sumHealth);
    }

    [Fact]
    public void AFailingSystemDoesNotStopTheOthersAndTheFrameThrowsAfterwards()
    {
        using World world = NewWorld();
        world.Set(world.CreateEntity(), new Position());
        int ran = 0;
        world.AddSystem("Bad", Phase.OnUpdate, (Entity _, ref Position p) => throw new InvalidOperationException("one"));
        world.AddSystem("Good", Phase.OnUpdate, (Entity _, ref Position p) => ran++);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => world.Progress(1));

        Assert.Equal("one", failure.Message);
        Assert.Equal(1, ran); // the system after the failing one still ran
        Assert.Throws<InvalidOperationException>(() => world.Progress(1)); // the world carries on, and reports the same fault again
        Assert.Equal(2, ran);
    }

    [Fact]
    public void SeveralFailuresInOneFrameArriveTogether()
    {
        using World world = NewWorld();
        world.Set(world.CreateEntity(), new Position());
        world.AddSystem("BadA", Phase.OnUpdate, (Entity _, ref Position p) => throw new InvalidOperationException("a"));
        world.AddSystem("BadB", Phase.PostUpdate, (Entity _, ref Position p) => throw new ArgumentException("b"));

        AggregateException failures = Assert.Throws<AggregateException>(() => world.Progress(1));

        Assert.Equal(["a", "b"], failures.InnerExceptions.Select(e => e.Message));
    }

    [Fact]
    public void ADestroyedSystemStopsRunning()
    {
        using World world = NewWorld();
        world.Set(world.CreateEntity(), new Position());
        int ran = 0;
        Entity system = world.AddSystem("Count", Phase.OnUpdate, (Entity _, ref Position p) => ran++);

        world.Progress(1);
        world.Destroy(system);
        world.Progress(1);

        Assert.Equal(1, ran);
    }

    // ---- Observers ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ObserversSeeComponentsBeingAddedSetAndRemoved()
    {
        using World world = NewWorld();
        var log = new List<string>();
        world.AddObserver("Added", [new QueryTerm(world.Component<Health>())], ObserverEvent.OnAdd, (in TableView view) => log.Add($"add{view.Count}"));
        world.AddObserver<Health>("Set", ObserverEvent.OnSet, (Entity _, ref Health h) => log.Add($"set{h.Current}"));
        world.AddObserver<Health>("Removed", ObserverEvent.OnRemove, (Entity _, ref Health h) => log.Add($"remove{h.Current}"));

        Entity e = world.CreateEntity();
        world.Set(e, new Health { Current = 4 });
        world.Remove<Health>(e);

        Assert.Equal(["add1", "set4", "remove4"], log);
    }

    // ---- The world as an object -------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADestroyedWorldCannotBeUsed()
    {
        World world = NewWorld();
        world.AddSystem("Idle", Phase.OnUpdate, (Entity _, ref Position p) => { }); // contexts are freed with the world
        using Query<Position> query = world.Query<Position>();
        world.AddObserver<Health>("Watch", ObserverEvent.OnSet, (Entity _, ref Health h) => { });
        world.CreateEntity();

        world.Destroy();

        Assert.True(world.IsDestroyed);
        Assert.Throws<ObjectDisposedException>(() => world.CreateEntity());
        world.Destroy(); // twice is harmless
    }

    [Fact]
    public void TheWorldIsReflectedForScripts()
    {
        using World world = NewWorld();
        ClassInfo info = world.Class;
        Assert.Equal("World", info.Name);
        Assert.True(info.Creatable);

        var results = new Value[1];
        info.FindMethod("create_entity")!.Invoke(world, [Value.String("scripted")], results);
        Entity made = results[0].ToEntity();
        Assert.Equal(made, world.Lookup("scripted"));

        info.FindMethod("is_alive")!.Invoke(world, [Value.Entity(made)], results);
        Assert.True(results[0].ToBool());

        info.FindMethod("lookup")!.Invoke(world, [Value.String("scripted")], results);
        Assert.Equal(made, results[0].ToEntity());

        info.FindMethod("progress")!.Invoke(world, [Value.Float(0.5)], []);
        Assert.Equal(0.5f, info.FindProperty("delta_time")!.Getter(world).ToSingle());
        Assert.Null(info.FindProperty("delta_time")!.Setter);
    }
}
