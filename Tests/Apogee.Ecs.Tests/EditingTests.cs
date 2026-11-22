using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Ecs.Tests;

/// <summary>What an editor needs of a world: the hierarchy, components of an entity, copies and snapshots (scenes are in SceneXmlTests).</summary>
public sealed class EditingTests : IDisposable
{
    private readonly World _world;

    public EditingTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        _world = new World();
    }

    public void Dispose() => _world.Dispose();

    private Entity Make(string name, Entity parent = default)
    {
        Entity e = _world.CreateEntity(name, parent);
        _world.Set(e, new Transform(new Double3(1, 2, 3), Quaternion.Identity, Vector3.One));
        return e;
    }

    // ---- The hierarchy and what an entity has ----

    [Fact]
    public void TheEnginesOwnEntitiesAreNotUserEntities()
    {
        Assert.Empty(_world.Roots());
        Entity a = Make("a");
        Assert.Equal([a], _world.Roots());
        Assert.True(_world.IsUserEntity(a));

        // The components, the entities that describe their fields, the systems and the types are the engine's.
        Assert.False(_world.IsUserEntity(_world.Component<Transform>()));
        Assert.False(_world.IsUserEntity(_world.Component<Health>()));
        Assert.False(_world.IsUserEntity(_world.Lookup("Vector3")));
        Assert.False(_world.IsUserEntity(_world.Lookup("TransformSystem")));
        Assert.False(_world.IsUserEntity(default));
        Assert.False(_world.IsUserEntity(new Entity(0xFFFFFF)));

        // An entity marked EditorOnly, and what is under it, is not part of the scene.
        Entity helper = _world.CreateEntity("helper");
        _world.Add<EditorOnly>(helper);
        Entity under = _world.CreateEntity("under", helper);
        Assert.False(_world.IsUserEntity(helper));
        Assert.False(_world.IsUserEntity(under));
        Assert.Equal([a], _world.Roots());
    }

    [Fact]
    public void ChildrenAndRootsComeInCreationOrderAndSubtreesStartWithTheirParent()
    {
        Entity a = Make("a");
        Entity c = Make("c", a);
        Entity b = Make("b", a);
        Entity d = Make("d", c);
        Entity other = Make("other");

        Assert.Equal([a, other], _world.Roots());
        Assert.Equal([c, b], _world.Children(a));       // the order they were made in, not by name
        Assert.Equal([d], _world.Children(c));
        Assert.Empty(_world.Children(d));
        Assert.Equal([a, c, d, b], _world.Subtree(a));  // parents first, depth first
    }

    [Fact]
    public void ComponentsOfListsWhatTheRegistryDescribesAndNotRelationships()
    {
        Entity parent = Make("p");
        Entity e = Make("e", parent);
        _world.Set(e, new Health { Current = 5, Max = 9 });
        _world.Add<Frozen>(e);

        string[] names = [.. _world.ComponentsOf(e).Select(c => c.Info.Name).Order()];
        Assert.Equal(["Frozen", "Health", "Transform", "WorldTransform"], names);   // not (ChildOf, p) or the name
        Assert.True(_world.ComponentsOf(e).Single(c => c.Info.Name == "Frozen").Info.IsTag);
        Assert.Contains(_world.Components, c => c.Name == "Health");
        Assert.Empty(_world.ComponentsOf(new Entity(0xFFFFFF)));
    }

    // ---- Copies ----

    [Fact]
    public void ACloneHasTheComponentsAndTheChildrenAndNamesThatStayUnique()
    {
        Entity a = Make("a");
        _world.Set(a, new Health { Current = 3, Max = 4, Regen = 0.5f });
        _world.Add<Frozen>(a);
        Entity child = Make("child", a);
        _world.Set(child, new Position { Value = new Vector3(7, 8, 9) });

        Entity copy = _world.Clone(a);

        Assert.NotEqual(a, copy);
        Assert.Equal("a (copy)", _world.GetName(copy));
        Assert.Equal(3, _world.Get<Health>(copy).Current);
        Assert.Equal(0.5f, _world.Get<Health>(copy).Regen);
        Assert.True(_world.Has<Frozen>(copy));
        Assert.Equal(new Double3(1, 2, 3), _world.Get<Transform>(copy).Position);

        Entity copiedChild = Assert.Single(_world.Children(copy));
        Assert.Equal("child (copy)", _world.GetName(copiedChild));
        Assert.Equal(new Vector3(7, 8, 9), _world.Get<Position>(copiedChild).Value);
        Assert.Equal(a, _world.GetParent(child));   // the original is untouched

        // Cloning again gets the next free name, and a clone can go under another parent.
        Assert.Equal("a (copy 2)", _world.GetName(_world.Clone(a)));
        Entity elsewhere = Make("elsewhere");
        Entity moved = _world.Clone(child, elsewhere);
        Assert.Equal(elsewhere, _world.GetParent(moved));
        Assert.Equal("child (copy)", _world.GetName(moved));
    }

    [Fact]
    public void ACloneHasTheBytesOfTheOriginalWhenItJoinsTheTableOfTheOriginalAndTheTableGrows()
    {
        // Setting the last component on the copy moves it into the original's table, which grows and moves its memory: the bytes come from the original's table before that, not through a pointer into it.
        // The original is alone in its table at first, and then the table has 1, 2, 3... of them: whichever way the table grows, each copy is what its original is.
        Entity target = Make("target");
        var originals = new List<Entity>();
        for (int i = 0; i < 40; i++)
        {
            Entity e = Make($"e{i}");
            _world.Set(e, new Marks { Alive = true, Level = (byte)(i + 1), Target = target, Mass = i + 0.5 });
            _world.Set(e, new Health { Current = 1000 + i, Max = 100 + i, Regen = 1.5f });
            originals.Add(e);
            foreach (Entity original in originals)
            {
                Entity copy = _world.Clone(original);
                Health health = _world.Get<Health>(original);
                Assert.Equal(health, _world.Get<Health>(copy));
                Marks marks = _world.Get<Marks>(original);
                Assert.Equal(marks.Level, _world.Get<Marks>(copy).Level);
                Assert.Equal(target, _world.Get<Marks>(copy).Target);
                Assert.Equal(marks.Mass, _world.Get<Marks>(copy).Mass);
                _world.Destroy(copy);
            }
        }
    }

    // ---- Snapshots ----

    [Fact]
    public void RestoringASnapshotBringsBackWhatWasChangedAndDestroyedAndRemovesWhatWasAddedUnderTheSameIds()
    {
        Entity a = Make("a");
        _world.Set(a, new Health { Current = 10, Max = 10 });
        Entity b = Make("b", a);
        Entity gone = Make("gone");
        WorldSnapshot before = _world.Snapshot();
        Assert.Equal(3, before.Count);

        // Play: change, remove, destroy, create.
        _world.Set(a, new Health { Current = 1, Max = 10 });
        _world.Remove<Transform>(a);
        _world.Add<Frozen>(b);
        _world.SetName(b, "renamed");
        _world.SetParent(b, default);
        _world.Destroy(gone);
        Entity extra = Make("extra");

        _world.Restore(before);

        Assert.True(_world.IsAlive(gone));                      // the same id is alive again
        Assert.False(_world.IsAlive(extra));
        Assert.Equal(10, _world.Get<Health>(a).Current);
        Assert.True(_world.Has<Transform>(a));
        Assert.False(_world.Has<Frozen>(b));
        Assert.Equal("b", _world.GetName(b));
        Assert.Equal(a, _world.GetParent(b));
        Assert.Equal("gone", _world.GetName(gone));
        Assert.Equal([a, gone], _world.Roots());
        Assert.Equal(new Double3(1, 2, 3), _world.Get<Transform>(gone).Position);
    }

    [Fact]
    public void ARestoredNameDoesNotClashWithAnEntityThatTookItInTheMeantime()
    {
        Entity a = Make("a");
        Entity b = Make("b");
        WorldSnapshot before = _world.Snapshot();

        _world.SetName(a, "tmp");
        _world.SetName(b, "a");   // b took a's name
        _world.SetName(a, "b");   // and a took b's: they swapped

        _world.Restore(before);
        Assert.Equal("a", _world.GetName(a));
        Assert.Equal("b", _world.GetName(b));
    }

    [Fact]
    public void ASubtreeSnapshotRestoresADeletedBranchWithoutTouchingTheRest()
    {
        Entity a = Make("a");
        Entity b = Make("b", a);
        Entity c = Make("c", b);
        Entity keep = Make("keep");
        _world.Set(c, new Health { Current = 2, Max = 3 });
        WorldSnapshot branch = _world.SnapshotTree(a);
        Assert.Equal([a, b, c], branch.Ids);

        _world.Set(keep, new Health { Current = 99, Max = 99 });
        _world.Destroy(a);                       // a and its children go
        Assert.False(_world.IsAlive(c));

        _world.RestoreEntities(branch);

        Assert.Equal([a, keep], _world.Roots());
        Assert.Equal([b], _world.Children(a));
        Assert.Equal(2, _world.Get<Health>(c).Current);
        Assert.Equal(99, _world.Get<Health>(keep).Current);   // not touched
    }

    [Fact]
    public void ASnapshotCanBeRestoredIntoAnotherWorldUnderTheSameIds()
    {
        // What the editor does after a play run: the session (and its world) is made again, and the entities of the scene come back as they were.
        Entity script = _world.DefineComponent("Stamina", [("max", FieldKind.Float32)]);
        Entity a = Make("a");
        _world.Set(a, new Health { Current = 10, Max = 12 });
        Entity b = Make("b", a);
        _world.Add<Frozen>(b);
        _world.Add(b, script);
        WorldSnapshot before = _world.Snapshot();

        using var fresh = new World();
        fresh.Restore(before);

        Assert.True(fresh.IsAlive(a));
        Assert.True(fresh.IsAlive(b));
        Assert.Equal("a", fresh.GetName(a));
        Assert.Equal(a, fresh.GetParent(b));
        Assert.Equal(10, fresh.Get<Health>(a).Current);
        Assert.True(fresh.Has<Frozen>(b));
        Assert.True(fresh.Has(b, fresh.FindComponent("Stamina")));    // the component a script defined came with it
        Assert.Equal([a], fresh.Roots());
    }
}
