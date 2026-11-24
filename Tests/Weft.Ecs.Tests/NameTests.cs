using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Weft.Ecs;
using Weft.Native.Tests;
using Weft.Reflection;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;

namespace Weft.Ecs.Tests;

/// <summary>
/// Names are unique among the children of one parent, and among the top-level entities, whatever else the world holds: the engine's own entities (there is one called "Empty") are not siblings of
/// anything, and a sibling that Flecs loses from its name index (two of one name in a scope take each other's place there) is what made two roots of one name.
/// </summary>
public sealed class NameTests : IDisposable
{
    private readonly World _world;

    public NameTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        _world = new World();
    }

    public void Dispose() => _world.Dispose();

    private Entity Named(string name, Entity parent = default)
    {
        Entity entity = _world.CreateEntity();
        if (!parent.IsNull)
        {
            _world.SetParent(entity, parent);
        }

        _world.SetName(entity, name);
        return entity;
    }

    private string?[] Names(IEnumerable<Entity> entities) => [.. entities.Select(_world.GetName)];

    private string?[] RootNames() => Names(_world.Roots());

    private static string Scene(params string[] names) => $"<Scene version=\"2\">{string.Concat(names.Select((name, i) => $"<Entity id=\"{i + 1}\" name=\"{name}\"/>"))}</Scene>";

    // ---- The engine's own entities are not siblings

    [Fact]
    public void ANameOfTheEnginesOwnModuleEntitiesIsFreeAtTheTopLevel()
    {
        // Flecs has an entity called "Empty" (in its core module), which a lookup by name finds: it is not a top-level entity, and not a user's.
        Assert.False(_world.Lookup("Empty").IsNull);
        Assert.Equal("Empty", _world.UniqueName("Empty"));

        Entity empty = Named("Empty");
        Assert.Equal("Empty", _world.GetName(empty));
        Assert.Equal("Empty 2", _world.GetName(Named("Empty")));
        Assert.Equal("Empty 3", _world.GetName(Named("Empty")));
    }

    [Fact]
    public void ASceneLoadedWithAnEntityCalledEmptyKeepsTheName()
    {
        List<Entity> first = _world.LoadScene(Scene("Empty", "Empty"));
        Assert.Equal(["Empty", "Empty 2"], Names(first));

        List<Entity> second = _world.LoadScene(Scene("Empty"));
        Assert.Equal(["Empty 3"], Names(second));
    }

    [Fact]
    public void AnEntityNamedLikeAnEngineEntityOfTheTopLevelGetsAnotherNameSoThatFlecsKeepsBoth()
    {
        // "Health" is a component of the tests, and Flecs keeps it in the index of the top level: a second entity of that name would take its place there.
        Entity health = _world.Component<Health>();
        Assert.Equal("Health", _world.GetName(health));
        Entity user = Named("Health");
        Assert.Equal("Health 2", _world.GetName(user));
        Assert.Equal(health, _world.Lookup("Health"));
        _world.Destroy(user);
        Assert.Equal(health, _world.Lookup("Health"));
    }

    // ---- Roots and children, the same rule

    [Fact]
    public void ARootAndAChildMayShareANameAndSiblingsMayNot()
    {
        Entity lamp = Named("Lamp");
        Entity corner = Named("Corner");
        Entity inner = Named("Lamp", corner);
        Entity other = Named("Other", corner);

        Assert.Equal("Lamp", _world.GetName(lamp));
        Assert.Equal("Lamp", _world.GetName(inner));
        Assert.Equal("Lamp 2", _world.GetName(Named("Lamp")));
        Assert.Equal("Lamp 2", _world.GetName(Named("Lamp", corner)));
        Assert.Equal("Other", _world.GetName(other));
        Assert.Equal("Lamp", _world.GetName(Named("Lamp", other)));
    }

    [Fact]
    public void RenamingToTheEntitysOwnNameOrToAFreeOneChangesNothingElse()
    {
        Entity a = Named("A");
        Entity b = Named("B");
        _world.SetName(a, "A");
        Assert.Equal("A", _world.GetName(a));
        _world.SetName(a, "B");
        Assert.Equal("B 2", _world.GetName(a));
        _world.SetName(b, "B 2"); // a has it now
        Assert.Equal("B 2 2", _world.GetName(b));
        Assert.Equal("B 2", _world.GetName(a));
    }

    // ---- Names that look alike

    [Fact]
    public void NamesThatDifferOnlyInCaseAreDifferentNames()
    {
        Entity lower = Named("lamp");
        Entity upper = Named("Lamp");
        Entity shout = Named("LAMP");
        Assert.Equal(["lamp", "Lamp", "LAMP"], Names([lower, upper, shout]));
        Assert.Equal("lamp 2", _world.GetName(Named("lamp")));
    }

    [Fact]
    public void NamesWithSpacesAndNumbersTakeTheFirstFreeSuffix()
    {
        Named("Lamp");
        Named("Lamp 2");
        Named("Lamp 10");
        Assert.Equal("Lamp 3", _world.UniqueName("Lamp"));
        Entity third = Named("Lamp");
        Assert.Equal("Lamp 3", _world.GetName(third));

        // "Lamp 2" is taken by the second: asking for it again goes on from it.
        Assert.Equal("Lamp 2 2", _world.GetName(Named("Lamp 2")));
        Assert.Equal("Lamp 10 2", _world.GetName(Named("Lamp 10")));
        Assert.Equal("Lamp 4", _world.GetName(Named("Lamp")));

        // Freeing one makes it the first free again.
        _world.Destroy(third);
        Assert.Equal("Lamp 3", _world.UniqueName("Lamp"));
    }

    [Fact]
    public void AnEmptyNameIsNoNameAndNeverClashes()
    {
        Entity a = Named("A");
        Entity b = Named("");
        Entity c = Named("");
        Assert.Null(_world.GetName(b));
        Assert.Null(_world.GetName(c));
        Assert.Equal("", _world.UniqueName(""));

        _world.SetName(a, "");
        Assert.Null(_world.GetName(a));
        Assert.Equal("A", _world.GetName(Named("A")));
        Assert.Equal(3, _world.Roots().Count(e => _world.GetName(e) is null));
    }

    [Fact]
    public void NamesThatFlecsReadsAsAnEntityIdOrAPathAreNamesToo()
    {
        Entity a = Named("#5");
        Entity b = Named("#5");
        Entity dotted = Named("a.b");
        Entity again = Named("a.b");
        Assert.Equal(["#5", "#5 2", "a.b", "a.b 2"], Names([a, b, dotted, again]));
    }

    // ---- Moving and copying

    [Fact]
    public void MovingANamedChildOutOfItsParentDoesNotLoseTheRootOfTheSameName()
    {
        // The bug: SetParent took the child to the top level on its way, where a root of its name was (Flecs let it take that root's place in the index, and lost the root when the child went on).
        Entity root = Named("Lamp");
        Entity corner = Named("Corner");
        Entity other = Named("Other");
        Entity child = Named("Lamp", corner);

        _world.SetParent(child, other);

        Assert.Equal(root, _world.Lookup("Lamp"));
        Assert.Equal("Lamp", _world.GetName(child));
        Assert.Equal(other, _world.GetParent(child));
        Assert.Equal("Lamp 2", _world.GetName(Named("Lamp")));
        Assert.Equal(["Lamp", "Corner", "Other", "Lamp 2"], RootNames());
    }

    [Fact]
    public void MovingAnEntityToTheTopLevelOrUnderAParentThatHasItsNameGivesItAFreeName()
    {
        Entity root = Named("Lamp");
        Entity corner = Named("Corner");
        Entity child = Named("Lamp", corner);

        _world.SetParent(child, default);
        Assert.Equal("Lamp 2", _world.GetName(child));
        Assert.Equal(root, _world.Lookup("Lamp"));

        Entity inner = Named("Lamp 2", corner);
        _world.SetParent(child, corner);
        Assert.Equal("Lamp 2 2", _world.GetName(child));
        Assert.Equal(["Lamp 2 2", "Lamp 2"], Names(_world.Children(corner)));
        Assert.Equal("Lamp 2", _world.GetName(inner));
    }

    [Fact]
    public void ACloneHasANameThatIsFreeAmongItsNewSiblings()
    {
        Entity lamp = Named("Lamp");
        Entity corner = Named("Corner");
        Entity inner = Named("Lamp", corner);

        Assert.Equal("Lamp (copy)", _world.GetName(_world.Clone(lamp)));
        Assert.Equal("Lamp (copy 2)", _world.GetName(_world.Clone(lamp)));
        Assert.Equal("Lamp (copy)", _world.GetName(_world.Clone(inner)));

        // Into another parent: the name there is free, so it is "(copy)" again.
        Entity into = Named("Into");
        Assert.Equal("Lamp (copy)", _world.GetName(_world.Clone(lamp, into)));
        Entity cornerCopy = _world.Clone(corner);
        Assert.Equal("Corner (copy)", _world.GetName(cornerCopy));
        Assert.Equal(["Lamp (copy)", "Lamp (copy) (copy)"], Names(_world.Children(cornerCopy)));
        Assert.Equal(["Lamp", "Corner", "Lamp (copy)", "Lamp (copy 2)", "Into", "Corner (copy)"], RootNames());
    }

    // ---- Loading

    [Fact]
    public void ASceneLoadedIntoAPopulatedWorldKeepsTheNamesOfBothFree()
    {
        Named("Lamp");
        Named("Lamp 2");
        Entity corner = Named("Corner");
        Named("Lamp", corner);

        const string Text = """
            <Scene version="2">
              <Entity id="1" name="Lamp"/>
              <Entity id="2" name="Lamp"/>
              <Entity id="3" name="Corner">
                <Entity id="4" name="Lamp"/>
                <Entity id="5" name="Lamp"/>
              </Entity>
            </Scene>
            """;
        List<Entity> loaded = _world.LoadScene(Text);
        Assert.Equal(["Lamp 3", "Lamp 4", "Corner 2", "Lamp", "Lamp 2"], Names(loaded));

        // Under a parent, among that parent's children.
        List<Entity> under = _world.LoadScene(Text, corner);
        Assert.Equal(["Lamp 2", "Lamp 3", "Corner", "Lamp", "Lamp 2"], Names(under));
        Assert.Equal(["Lamp", "Lamp 2", "Lamp 3", "Corner"], Names(_world.Children(corner)));
    }

    [Fact]
    public void ASceneWithManyEntitiesOfOneNameIsNotQuadratic()
    {
        // One name 20000 times: the n-th entity used to try n names ("x 2", "x 3"…), which is minutes; a name of its own for each takes about as long.
        string[] names = new string[20000];
        Array.Fill(names, "x");
        var time = Stopwatch.StartNew();
        List<Entity> loaded = _world.LoadScene(Scene(names));
        time.Stop();

        Assert.Equal(names.Length, loaded.Count);
        Assert.Equal("x", _world.GetName(loaded[0]));
        Assert.Equal("x 2", _world.GetName(loaded[1]));
        Assert.Equal("x 20000", _world.GetName(loaded[^1]));
        Assert.True(time.Elapsed < TimeSpan.FromSeconds(10), $"{time.Elapsed.TotalSeconds:0.0} s");
    }

    // ---- Snapshots

    [Fact]
    public void NamesAfterARestoreAreStillFoundByTheNextEntityOfThatName()
    {
        Entity lamp = Named("Lamp");
        Entity corner = Named("Corner");
        Entity inner = Named("Lamp", corner);
        WorldSnapshot snapshot = _world.Snapshot();

        _world.SetParent(inner, default);
        _world.SetName(corner, "Gone");
        _world.Restore(snapshot);

        Assert.Equal(["Lamp", "Corner"], RootNames());
        Assert.Equal(lamp, _world.Lookup("Lamp"));
        Assert.Equal("Lamp 2", _world.GetName(Named("Lamp")));
        Assert.Equal("Lamp 2", _world.GetName(Named("Lamp", corner)));
        Assert.Equal(corner, _world.GetParent(inner));
    }

    [Fact]
    public void ARestoreThatSwapsTwoNamesNeedsNoSuffix()
    {
        Entity a = Named("A");
        Entity b = Named("B");
        WorldSnapshot snapshot = _world.Snapshot();
        _world.SetName(a, "tmp");
        _world.SetName(b, "A");
        _world.SetName(a, "B");

        _world.Restore(snapshot);
        Assert.Equal(["A", "B"], Names([a, b]));
    }

    [Fact]
    public void ARestoreThatSwapsAParentAndItsChildDoesNotMakeThemEachOthersParent()
    {
        // B (the older id, so the first in the snapshot) was under A when the snapshot was taken; now A is under B. Setting B's parent first would make a cycle for a moment, which Flecs does not survive
        // (it crashed the process).
        Entity b = Named("B");
        Entity a = Named("A");
        _world.SetParent(b, a);
        WorldSnapshot snapshot = _world.Snapshot();
        _world.SetParent(b, default);
        _world.SetParent(a, b);
        Assert.Equal(a, _world.Children(b).Single());

        _world.Restore(snapshot);
        Assert.Equal(a, _world.GetParent(b));
        Assert.True(_world.GetParent(a).IsNull);
        Assert.Equal(["A"], RootNames());
    }

    [Fact]
    public void BringingBackADeletedBranchWhoseNameWasTakenGivesItAFreeName()
    {
        Entity lamp = Named("Lamp");
        Entity inner = Named("Bulb", lamp);
        Entity other = Named("Other");
        WorldSnapshot branch = _world.SnapshotTree(lamp);
        _world.Destroy(lamp);
        _world.SetName(other, "Lamp"); // the name is taken by the time the branch comes back (an undo out of order)

        _world.RestoreEntities(branch);
        Assert.Equal("Lamp 2", _world.GetName(lamp));
        Assert.Equal("Bulb", _world.GetName(inner));
        Assert.Equal("Lamp", _world.GetName(other));
        Assert.Equal(other, _world.Lookup("Lamp"));
    }

    // ---- Against a model

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(0, 60).Select(seed => new object[] { seed });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomEditsGiveTheNamesASimpleModelDoes(int seed)
    {
        _ = _world.Component<Health>(); // the engine's entities that Flecs keeps at the top level: the model knows their names
        var model = new NameModel(_world, new Random(seed));
        for (int step = 0; step < 150; step++)
        {
            model.Step();
            model.Check(step);
        }
    }
}

