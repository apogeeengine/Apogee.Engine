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

/// <summary>What an editor needs of a world: the hierarchy, components of an entity, copies, snapshots, and scenes as text.</summary>
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

    // ---- Scenes ----

    [Fact]
    public void ASceneRoundTripsEntitiesComponentsHierarchyAndReferences()
    {
        Entity a = Make("root");
        _world.Set(a, new Health { Current = 7, Max = 9, Regen = 0.25f });
        Entity child = Make("child", a);
        Entity target = Make("target");
        _world.Set(child, new Marks { Alive = true, Level = 3, Target = target, Mass = 12.5 });
        _world.Add<Frozen>(child);
        _world.Set(child, new Transform(new Double3(1e12 + 0.5, -2, 3), new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), new Vector3(2, 3, 4)));

        string text = _world.SaveScene();

        using var other = new World();
        List<Entity> loaded = other.LoadScene(text);

        Assert.Equal(3, loaded.Count);
        Entity root2 = other.Lookup("root");
        Entity child2 = other.Lookup("root.child");
        Entity target2 = other.Lookup("target");
        Assert.Equal(7, other.Get<Health>(root2).Current);
        Assert.Equal(0.25f, other.Get<Health>(root2).Regen);
        Assert.Equal(root2, other.GetParent(child2));
        Marks marks = other.Get<Marks>(child2);
        Assert.True(marks.Alive);
        Assert.Equal(3, marks.Level);
        Assert.Equal(12.5, marks.Mass);
        Assert.Equal(target2, marks.Target);                         // the reference follows the entity to its new id
        Assert.True(other.Has<Frozen>(child2));
        Transform t = other.Get<Transform>(child2);
        Assert.Equal(new Double3(1e12 + 0.5, -2, 3), t.Position);    // a double position keeps its precision
        Assert.Equal(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), t.Rotation);
        Assert.Equal(new Vector3(2, 3, 4), t.Scale);

        // Saved again from the new world, the same text: nothing was lost or invented (WorldTransform is derived, so it is not in it).
        Assert.DoesNotContain("WorldTransform", text, StringComparison.Ordinal);
        Assert.Equal(text, other.SaveScene());
    }

    [Fact]
    public void ASceneDefinesTheComponentsAScriptMadeAndLoadsUnderAParent()
    {
        Entity script = _world.DefineComponent("Stamina", [("max", FieldKind.Float32), ("count", FieldKind.Int32), ("big", FieldKind.UInt64), ("ok", FieldKind.Bool)]);
        Entity e = Make("runner");
        unsafe
        {
            byte* data = _world.EnsurePointer(e, script, 24);
            *(float*)(data + _world.GetComponentInfo(script)!.FindField("max")!.Offset) = 1.5f;
            *(int*)(data + _world.GetComponentInfo(script)!.FindField("count")!.Offset) = 4;
            *(ulong*)(data + _world.GetComponentInfo(script)!.FindField("big")!.Offset) = ulong.MaxValue;   // too big for a JSON number
            *(data + _world.GetComponentInfo(script)!.FindField("ok")!.Offset) = 1;
        }

        string text = _world.SaveScene();
        Assert.Contains("\"Stamina\"", text, StringComparison.Ordinal);

        using var fresh = new World();                // a world that has never heard of Stamina
        Entity holder = fresh.CreateEntity("holder");
        List<Entity> loaded = fresh.LoadScene(text, holder);

        Entity runner = Assert.Single(loaded);
        Assert.Equal(holder, fresh.GetParent(runner));
        Entity stamina = fresh.FindComponent("Stamina");
        Assert.False(stamina.IsNull);
        ComponentInfo info = fresh.GetComponentInfo(stamina)!;
        unsafe
        {
            byte* data = fresh.GetPointer(runner, stamina);
            Assert.Equal(1.5f, *(float*)(data + info.FindField("max")!.Offset));
            Assert.Equal(4, *(int*)(data + info.FindField("count")!.Offset));
            Assert.Equal(ulong.MaxValue, *(ulong*)(data + info.FindField("big")!.Offset));
            Assert.Equal(1, *(data + info.FindField("ok")!.Offset));
        }
    }

    [Fact]
    public void ARuntimeHandleIsSavedAsWhatItNamesThroughAConverterAndIgnoredComponentsAreLeftOut()
    {
        _world.RegisterSceneConverter("Health", "Current", value => value == 0 ? null : $"hp#{value}", text => ulong.Parse(text.AsSpan(3), System.Globalization.CultureInfo.InvariantCulture));
        _world.IgnoreInScenes("Velocity");
        Entity e = Make("e");
        _world.Set(e, new Health { Current = 42, Max = 5 });
        _world.Set(e, new Velocity { Value = new Vector3(1, 1, 1) });

        string text = _world.SaveScene();
        Assert.Contains("\"hp#42\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Velocity", text, StringComparison.Ordinal);

        using var fresh = new World();
        fresh.RegisterSceneConverter("Health", "Current", value => $"hp#{value}", text => ulong.Parse(text.AsSpan(3), System.Globalization.CultureInfo.InvariantCulture));
        fresh.LoadScene(text);
        Assert.Equal(42, fresh.Get<Health>(fresh.Lookup("e")).Current);
        Assert.False(fresh.Has<Velocity>(fresh.Lookup("e")));
    }

    [Fact]
    public void ASceneWithUnknownThingsLoadsWhatItCanAndABadFileIsAnError()
    {
        const string text = """
            { "format": "apogee-scene", "version": 1, "components": {},
              "entities": [
                { "id": 1, "name": "x", "components": { "NoSuchThing": { "a": 1 }, "Health": { "Current": 3, "Nope": 1, "Max": "not a number" } } },
                { "id": 2, "name": "x", "parent": 99, "components": { "Frozen": {} } }
              ] }
            """;
        List<Entity> loaded = _world.LoadScene(text);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(3, _world.Get<Health>(loaded[0]).Current);   // the known field of a known component was read
        Assert.Equal(0, _world.Get<Health>(loaded[0]).Max);       // the one that was not a number stayed zero
        Assert.True(_world.Has<Frozen>(loaded[1]));
        Assert.Equal("x", _world.GetName(loaded[0]));
        Assert.Equal("x 2", _world.GetName(loaded[1]));            // the name was taken: a free one

        Assert.Throws<FormatException>(() => _world.LoadScene("not json"));
        Assert.Throws<FormatException>(() => _world.LoadScene("{ \"format\": \"something else\" }"));
        Assert.Throws<FormatException>(() => _world.LoadScene("{ \"format\": \"apogee-scene\", \"version\": 99, \"entities\": [] }"));
    }

    [Fact]
    public void OneEntityAndItsBranchCanBeSavedAndLoadedAlone()
    {
        Entity a = Make("a");
        Entity b = Make("b", a);
        Make("unrelated");

        string text = _world.SaveScene(a);
        using var other = new World();
        List<Entity> loaded = other.LoadScene(text);

        Assert.Equal(2, loaded.Count);
        Assert.False(other.Lookup("a").IsNull);
        Assert.False(other.Lookup("a.b").IsNull);
        Assert.True(other.Lookup("unrelated").IsNull);
        Assert.Equal(b, _world.Children(a).Single());
    }
}
