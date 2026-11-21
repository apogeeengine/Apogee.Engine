using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Apogee.Content.Xml;
using Apogee.Core.Assets;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Ecs.Tests;

/// <summary>Instances of prefabs in scenes and in other prefabs: how they load with their overrides, what an entity field can point at, and what snapshots and copies do with them.</summary>
public sealed class PrefabInstanceTests : PrefabTestBase
{
    // Every kind of override of the manual's example, on the corner prefab (which holds two lamps).
    private string Scene() => $"""
        <Scene version="2">
          <Entity id="1" name="Player"><Transform Position="1 2 3" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
          <Entity id="2" name="Corner" prefab="{Corner}">
            <Transform Position="10 0 4"/>
            <Health Max="200" Current="200" append="true"/>
            <Override entity="4" name="Old bench"><Transform remove="true"/></Override>
            <Override entity="2"><Health Max="50" Current="50" append="true"/></Override>
            <Override entity="2/2"><Health Current="1"/><Flicker lamp="1"/></Override>
            <Override entity="3" remove="true"/>
            <Override entity="2/3">
              <Entity id="3" name="Sign"><Transform Position="0 0.5 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
            </Override>
            <Entity id="4" name="Trash can"><Transform Position="5 0 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
          </Entity>
          <Entity id="5" name="Switch"><Marks Target="2/2"/></Entity>
        </Scene>
        """;

    private Entity Named(string path) => World.Lookup(path);

    [Fact]
    public void ASceneInstanceLoadsWithItsOverrides()
    {
        List<Entity> loaded = World.LoadScene(Scene());

        Entity corner = Named("Corner");
        Entity lamp = Child(corner, "Lamp");
        Entity bulb = Child(lamp, "Bulb");
        Assert.Equal(["Player", "Corner", "Lamp", "Bulb", "Base", "Sign", "Old bench", "Trash can", "Switch"], loaded.Select(e => World.GetName(e)));   // in the order of the document: members, then what was added
        Assert.Equal(["Lamp", "Old bench", "Trash can"], NamesBelow(World, corner));                                                                // "Lamp 2" was removed, the bench renamed

        Assert.Equal(new Double3(10, 0, 4), World.Get<Transform>(corner).Position);                    // a modified field...
        Assert.Equal(Vector3.One, World.Get<Transform>(corner).Scale);                                 // ...and the ones it did not name
        Assert.Equal(200, World.Get<Health>(corner).Max);                                              // an added component
        Assert.Equal(new Double3(2, 0, 0), World.Get<Transform>(lamp).Position);                       // the corner's own patch of its lamp
        Assert.Equal(50, World.Get<Health>(lamp).Current);                                             // added to the lamp's root by the scene
        Assert.Equal(1, World.Get<Health>(bulb).Current);                                              // the scene's patch wins over the corner's (9)
        Assert.Equal(9, World.Get<Health>(bulb).Max);                                                  // and what it did not name is the lamp's
        Assert.Equal(0.5f, World.Get<Health>(bulb).Regen);
        Assert.Equal(0, loaded.Count(e => World.GetName(e) == "Lamp 2"));
        Assert.Equal(["Sign"], NamesBelow(World, Child(lamp, "Base")));                                // an entity added under a member
        Assert.Equal(new Double3(0, 0.5, 0), World.Get<Transform>(Child(Child(lamp, "Base"), "Sign")).Position);
        Assert.Equal(new Double3(5, 0, 0), World.Get<Transform>(Child(corner, "Trash can")).Position); // added directly under the root

        Assert.True(World.IsPrefabInstance(corner));
        Assert.Equal(CornerPath + "|" + CornerUid, World.PrefabOf(corner));
        Assert.True(World.IsPrefabInstance(lamp));                                                     // the nested instance is an instance too
        Assert.False(World.IsPrefabInstance(bulb));
    }

    [Fact]
    public void TheBenchLosesItsTransformAndTheNameOverrideApplies()
    {
        World.LoadScene(Scene());

        // The override renamed entity 4 of the corner; the same entity, with its Transform removed.
        Entity corner = Named("Corner");
        Entity bench = World.Children(corner).Single(c => World.GetName(c) == "Old bench");
        Assert.False(World.Has<Transform>(bench));
    }

    [Fact]
    public void ASceneEntityCanPointIntoAnInstance()
    {
        World.LoadScene(Scene());

        Entity lamp = Child(Named("Corner"), "Lamp");
        Assert.Equal(lamp, World.Get<Marks>(Named("Switch")).Target);                       // "2/2": entity 2 of the instance numbered 2
        Assert.Equal(lamp, World.Get<Marks>(Child(lamp, "Bulb")).Target);                   // the lamp's own "1" is its root, in this instance
    }

    [Fact]
    public void AnOverrideCanPointToASceneEntity()
    {
        World.LoadScene(Scene());

        Entity bulb = Child(Child(Named("Corner"), "Lamp"), "Bulb");
        Assert.Equal(Named("Player").Id, EntityField(World, bulb, "Flicker", "lamp"));      // lamp="1" in the scene's override: entity 1 of the scene, not the lamp's root
        Assert.Equal(Child(Named("Corner"), "Lamp").Id, World.Get<Marks>(bulb).Target.Id);  // the prefab's own reference is still inside
    }

    [Fact]
    public void NestedMembersHaveIdsOfTheirOwnPrefab()
    {
        World.LoadScene(Scene());
        Entity corner = Named("Corner");
        Entity lamp = Child(corner, "Lamp");
        uint cornerHandle = World.Prefabs.Load(CornerPath);
        uint lampHandle = World.Prefabs.Load(LampPath);

        Assert.Equal(new PrefabInstance { Prefab = cornerHandle }, World.Get<PrefabInstance>(corner));
        Assert.Equal(new PrefabMember { Id = 1, Prefab = cornerHandle }, World.Get<PrefabMember>(corner));   // the root: entity 1 of its own prefab
        Assert.Equal(new PrefabMember { Id = 4, Prefab = cornerHandle }, World.Get<PrefabMember>(Child(corner, "Old bench")));
        Assert.Equal(new PrefabMember { Id = 2, Prefab = cornerHandle }, World.Get<PrefabMember>(lamp));     // the corner's entity 2...
        Assert.Equal(new PrefabInstance { Prefab = lampHandle }, World.Get<PrefabInstance>(lamp));            // ...which is the root of a lamp
        Assert.Equal(new PrefabMember { Id = 2, Prefab = lampHandle }, World.Get<PrefabMember>(Child(lamp, "Bulb")));   // its Bulb: entity 2 of the lamp, not of the corner
        Assert.Equal(new PrefabMember { Id = 3, Prefab = lampHandle }, World.Get<PrefabMember>(Child(lamp, "Base")));
        Assert.False(World.Has<PrefabMember>(Named("Player")));                                               // entities the scene holds are not from a prefab file
        Assert.False(World.Has<PrefabMember>(Child(corner, "Trash can")));

        Assert.False(World.IsPrefabMember(corner));       // an instance root can be moved
        Assert.True(World.IsPrefabMember(lamp));          // a nested root cannot: its place in the corner names it
        Assert.True(World.IsPrefabMember(Child(lamp, "Bulb")));
        Assert.False(World.IsPrefabMember(Child(corner, "Trash can")));
        Assert.False(World.IsPrefabMember(Named("Player")));
    }

    [Fact]
    public void UnappliedOverridesSurviveSnapshotAndRestore()
    {
        // The corner lost nothing, but the scene was written for a lamp that had an entity 9 and a component the lamp no longer has.
        World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Corner" prefab="{Corner}">
                <Override entity="2/9"><Health Current="3"/></Override>
                <Override entity="2/2"><Flicker lamp="1"/><Frozen/><Health Max="1"/></Override>
              </Entity>
            </Scene>
            """);
        string before = World.SaveScene();
        Assert.Contains("entity=\"2/9\"", before, StringComparison.Ordinal);
        WorldSnapshot snapshot = World.Snapshot();

        // Another world: a new session after a play run. The snapshot carries what was kept aside.
        using World other = OtherWorld();
        other.Restore(snapshot);

        Assert.Equal(before, other.SaveScene());

        // And an undo of a delete in the same world brings it back too.
        Entity corner = World.Lookup("Corner");
        WorldSnapshot tree = World.SnapshotTree(corner);
        World.Destroy(corner);
        World.RestoreEntities(tree);
        Assert.Equal(before, World.SaveScene());
    }

    [Fact]
    public void CloningAnInstanceRootMakesAnotherInstance()
    {
        World.LoadScene(Scene());
        Entity corner = Named("Corner");

        Entity copy = World.Clone(corner);

        Assert.True(World.IsPrefabInstance(copy));
        Assert.Equal(World.PrefabOf(corner), World.PrefabOf(copy));
        Assert.Equal("Corner (copy)", World.GetName(copy));
        Assert.Equal(World.Subtree(corner).Count, World.Subtree(copy).Count);
        Entity copiedLamp = World.Children(copy).Single(c => World.GetName(c)!.StartsWith("Lamp", StringComparison.Ordinal));
        Assert.True(World.IsPrefabInstance(copiedLamp));
        Assert.True(World.IsPrefabMember(copiedLamp));                                  // still a member of the copied corner
        Assert.Equal(World.Get<PrefabMember>(Child(corner, "Old bench")), World.Get<PrefabMember>(Child(copy, "Old bench (copy)")));
        Assert.False(World.IsPrefabMember(copy));
    }

    [Fact]
    public void CloningAMemberMakesPlainEntities()
    {
        World.LoadScene(Scene());
        Entity lamp = Child(Named("Corner"), "Lamp");
        Entity bulb = Child(lamp, "Bulb");

        Entity copy = World.Clone(bulb);                  // a branch of an instance, its instance not copied

        Assert.False(World.Has<PrefabMember>(copy));
        Assert.False(World.IsPrefabMember(copy));
        Assert.Equal(9, World.Get<Health>(copy).Max);     // the components are copied

        // The same for a nested instance whose outer instance is left behind: it is an instance, but not a member of the corner.
        Entity nested = World.Clone(lamp, default);
        Assert.True(World.IsPrefabInstance(nested));
        Assert.False(World.Has<PrefabMember>(nested));
        Assert.True(World.IsPrefabMember(Child(nested, "Bulb (copy)")));              // its own members still belong to it
        Assert.Equal(World.Get<PrefabMember>(bulb), World.Get<PrefabMember>(Child(nested, "Bulb (copy)")));
    }

    [Fact]
    public void AWorldWithoutFilesLeavesInstancesEmptyAndSaysSo()
    {
        using var bare = new World();

        List<Entity> loaded = WithWarnings(() => bare.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Lamp" prefab="{Lamp}"><Transform Position="3 0 0"/><Entity id="2" name="Extra"/></Entity>
            </Scene>
            """), out List<string> warnings);

        Entity lamp = Assert.Single(loaded);
        Assert.Contains(warnings, w => w.Contains("this world has no files", StringComparison.Ordinal) || w.Contains("cannot be used", StringComparison.Ordinal));
        Assert.Empty(bare.Children(lamp));
        Assert.Equal(Lamp, bare.PrefabOf(lamp));                        // it still says what it is an instance of

        // Saved, it is the same instance with everything it held: nothing was lost by not having the files.
        string text = bare.SaveScene();
        XmlNode saved = XmlNode.Parse(text);
        XmlNode element = saved.Child("Entity")!;
        Assert.Equal(Lamp, element.Attribute("prefab"));
        Assert.Equal("Position", element.Child("Transform")!.Attributes.Single().Name);
        Assert.Equal("Extra", element.Child("Entity")!.Attribute("name"));
        Assert.Equal("2", saved.Attribute("version"));

        // Instantiating needs files; nothing is made without them.
        Assert.Throws<InvalidOperationException>(() => bare.Instantiate(LampPath));
    }

    [Fact]
    public void HandWrittenPrefabComponentsAreSkipped()
    {
        // The two components that link an entity to its prefab are the engine's: neither a plain entity nor an instance's patches may forge them.
        List<Entity> loaded = WithWarnings(() => World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Forged"><PrefabInstance Prefab="1"/><PrefabMember Id="1" Prefab="1"/></Entity>
              <Entity id="2" name="Lamp" prefab="{Lamp}"><PrefabMember Id="9" Prefab="7" append="true"/><Override entity="2"><PrefabInstance Prefab="1" append="true"/></Override></Entity>
            </Scene>
            """), out List<string> warnings);

        Entity forged = loaded[0];
        Assert.False(World.IsPrefabInstance(forged));
        Assert.False(World.Has<PrefabMember>(forged));
        Entity lamp = Named("Lamp");
        Assert.Equal(1u, World.Get<PrefabMember>(lamp).Id);                       // the real one: entity 1 of the lamp
        Assert.False(World.Has<PrefabInstance>(Child(lamp, "Bulb")));
        Assert.Equal(4, warnings.Count(w => w.Contains("made by the engine", StringComparison.Ordinal)));
    }

    [Fact]
    public void ABadInstanceIdGetsAnAddressOfItsOwn()
    {
        List<Entity> loaded = WithWarnings(() => World.LoadScene($"""
            <Scene version="2">
              <Entity id="2" name="First" prefab="{Lamp}"/>
              <Entity id="2" name="Second" prefab="{Lamp}"/>
              <Entity id="two" name="Third" prefab="{Lamp}"/>
              <Entity name="Fourth" prefab="{Lamp}"/>
              <Entity id="9" name="Pointer"><Marks Target="2"/></Entity>
            </Scene>
            """), out List<string> warnings);

        Assert.Equal(4 * 3 + 1, loaded.Count);                                                  // every instance was made whole
        Assert.Equal(World.Lookup("First"), World.Get<Marks>(World.Lookup("Pointer")).Target);  // "2" is the first that claimed it
        Assert.Contains(warnings, w => w.Contains("used twice", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("'two'", StringComparison.Ordinal));
        foreach (string name in new[] { "First", "Second", "Third", "Fourth" })
        {
            // Each lamp's own references stay inside it.
            Entity root = World.Lookup(name);
            Assert.Equal(root, World.Get<Marks>(Child(root, "Bulb")).Target);
        }
    }

    [Fact]
    public void AReferenceThatIsNotAnAssetReferenceKeepsItsElement()
    {
        string scene = $"""
            <Scene version="2">
              <Entity id="1" name="Broken" prefab="a|b|c"><Transform Position="1 0 0"/><Override entity="2"><Health Current="3"/></Override><Entity id="2" name="Under"/></Entity>
              <Entity id="3" name="Hollow" prefab=""/>
              <Entity id="4" name="Damaged" prefab="{LampPath}|uid://abc-def"><Override entity="3" remove="true"/></Entity>
            </Scene>
            """;
        List<Entity> loaded = WithWarnings(() => World.LoadScene(scene), out List<string> warnings);

        // The instance is there, empty, as an instance of a prefab that is missing: it is the reference that cannot be read.
        Assert.Equal(["Broken", "Hollow", "Damaged"], loaded.Select(e => World.GetName(e)));
        Assert.All(loaded, e => Assert.True(World.IsPrefabInstance(e)));
        Assert.All(loaded, e => Assert.Empty(World.Children(e)));
        Assert.Equal(3, warnings.Count(w => w.Contains("not an asset reference", StringComparison.Ordinal)));

        // Saved, it is the same element: the text of the reference as it was written, and everything the element held.
        string text = World.SaveScene();
        XmlNode saved = XmlNode.Parse(text);
        XmlNode broken = saved.Children.Single(c => c.Attribute("name") == "Broken");
        Assert.Equal("a|b|c", broken.Attribute("prefab"));
        Assert.Equal(["Transform", "Override", "Entity"], broken.Children.Select(c => c.Name));
        Assert.Equal("", saved.Children.Single(c => c.Attribute("name") == "Hollow").Attribute("prefab"));
        XmlNode damaged = saved.Children.Single(c => c.Attribute("name") == "Damaged");
        Assert.Equal($"{LampPath}|uid://abc-def", damaged.Attribute("prefab"));
        Assert.Equal("true", damaged.Child("Override")!.Attribute("remove"));
        using World again = OtherWorld();
        again.LoadScene(text);
        Assert.Equal(text, again.SaveScene());
    }

    [Fact]
    public void AFieldThatSpellsAMadeUpAddressNamesNothing()
    {
        // "~1" is how the expander calls an entity nothing can name; no document can spell it, and a field that tries is no reference.
        List<Entity> loaded = World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Lamp" prefab="{Lamp}">
                <Override entity="2"><Marks Target="~1"/></Override>
                <Entity name="Nameless"/>
              </Entity>
            </Scene>
            """);

        Assert.Equal(["Lamp", "Bulb", "Base", "Nameless"], loaded.Select(e => World.GetName(e)));
        Assert.True(World.Get<Marks>(loaded[1]).Target.IsNull);
    }

    [Fact]
    public void ASceneIsVersionOneOrTwoAndNoMore()
    {
        World.LoadScene($"<Scene version=\"1\"><Entity id=\"1\" name=\"Old\" prefab=\"{Lamp}\"/></Scene>");   // an instance in a version 1 file is not refused
        Assert.True(World.IsPrefabInstance(World.Lookup("Old")));
        Assert.Throws<FormatException>(() => World.LoadScene("<Scene version=\"3\"/>"));
    }

    [Fact]
    public void TheLimitsAreOnTheWorldAndALoadThatBreaksThemMakesNothing()
    {
        Assert.Equal(16, World.MaxPrefabDepth);
        Assert.Equal(262_144, World.MaxLoadEntities);
        Assert.Throws<ArgumentOutOfRangeException>(() => World.MaxLoadEntities = -1);

        World.MaxLoadEntities = 8;
        string threeLamps = $"<Scene version=\"2\"><Entity id=\"1\" prefab=\"{Lamp}\"/><Entity id=\"2\" prefab=\"{Lamp}\"/><Entity id=\"3\" prefab=\"{Lamp}\"/></Scene>";   // 9 entities
        FormatException tooBig = Assert.Throws<FormatException>(() => World.LoadScene(threeLamps));
        Assert.Contains("entities", tooBig.Message, StringComparison.Ordinal);
        Assert.Empty(World.UserEntities());                                                                                                  // nothing was made

        // The limit is on what instances make, which a few small files can multiply; a scene's own entities are the size of the file and are not counted, as before there were instances.
        Assert.Equal(9, World.LoadScene("<Scene version=\"1\">" + string.Concat(Enumerable.Repeat("<Entity id=\"1\"/>", 9)) + "</Scene>").Count);
        foreach (Entity entity in World.UserEntities())
        {
            World.Destroy(entity);
        }

        // Each call takes a new budget: two lamps in two calls is fine where two in one load (6 + the scene's own) would not be.
        World.MaxLoadEntities = 3;
        _ = World.Instantiate(LampPath);
        _ = World.Instantiate(LampPath);
        Assert.Equal(6, World.UserEntities().Count);
        World.MaxLoadEntities = 2;
        Assert.Throws<FormatException>(() => World.Instantiate(LampPath));
        Assert.Equal(6, World.UserEntities().Count);
    }

    [Fact]
    public void AWorldMadeOfManyInstancesCanBeSavedWhateverTheLimitIs()
    {
        // Each Instantiate had a budget of its own; the save gives each instance one too, instead of one for all of them.
        World.MaxLoadEntities = 3;
        for (int i = 0; i < 5; i++)
        {
            _ = World.Instantiate(LampPath);
        }

        string text = World.SaveScene();

        Assert.Equal(5, XmlNode.Parse(text).Children.Count(c => c.Attribute("prefab") is not null));
        using World again = OtherWorld();
        Assert.Equal(15, again.LoadScene(text).Count);    // (a load has one budget for all it expands, so it takes the default limit; a save is one budget for each instance)
    }

    [Fact]
    public void ACycleIsFoundWhateverWayItsPrefabsAreSpelled()
    {
        // The real library resolves a path, a UID and both to one file, so a prefab that names itself by UID is the prefab being expanded, not a new one.
        AssetUid a = AssetUid.New();
        AssetUid b = AssetUid.New();
        Write("cycles/a.aprefab", $"<Prefab version=\"1\" uid=\"{a}\" nextId=\"4\"><Entity id=\"1\" name=\"A\"><Entity id=\"2\" name=\"ViaUid\" prefab=\"{a}\"/><Entity id=\"3\" name=\"ViaB\" prefab=\"res://cycles/b.aprefab|{b}\"/></Entity></Prefab>");
        Write("cycles/b.aprefab", $"<Prefab version=\"1\" uid=\"{b}\" nextId=\"3\"><Entity id=\"1\" name=\"B\"><Entity id=\"2\" name=\"BackToA\" prefab=\"res://cycles/a.aprefab\"/></Entity></Prefab>");
        Assets.Refresh();

        Entity root = WithWarnings(() => World.Instantiate("res://cycles/a.aprefab"), out List<string> warnings);

        Assert.Equal(["ViaUid", "ViaB"], NamesBelow(World, root));
        Assert.Empty(World.Children(Child(root, "ViaUid")));                                   // the repeat is one empty entity
        Assert.Equal(["BackToA"], NamesBelow(World, Child(root, "ViaB")));
        Assert.Empty(World.Children(Child(Child(root, "ViaB"), "BackToA")));
        Assert.Contains(warnings, w => w.Contains("contains itself", StringComparison.Ordinal));
        Assert.True(World.IsPrefabInstance(Child(root, "ViaUid")));                             // and still says what it was meant to be
        Assert.Equal(World.PrefabOf(root), World.PrefabOf(Child(root, "ViaUid")));
    }

    [Fact]
    public void TheEntitiesOfTheSceneKeepTheirNumbersWhenAnInstanceClaimsOneToo()
    {
        // The scene's own entities hold the numbers it was last written with; an entity an instance adds may carry an older one (it was kept aside for a prefab that was missing).
        List<Entity> loaded = WithWarnings(() => World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="One" prefab="{Lamp}"><Entity id="4" name="Added"/></Entity>
              <Entity id="4" name="Plain"/>
              <Entity id="5" name="Pointer"><Marks Target="4"/></Entity>
            </Scene>
            """), out List<string> warnings);

        Assert.Equal(World.Lookup("Plain"), World.Get<Marks>(World.Lookup("Pointer")).Target);
        Assert.False(World.Lookup("One.Added").IsNull);                              // the entity was made all the same
        Assert.Contains(warnings, w => w.Contains("address 4 is used twice", StringComparison.Ordinal));
        Assert.Equal(6, loaded.Count);
    }

    [Fact]
    public void ADeepChainOfFilesStopsAtTheDepthLimit()
    {
        const int Files = 20;
        for (int i = 0; i < Files; i++)
        {
            string inner = i + 1 < Files ? $"<Entity id=\"2\" name=\"Next\" prefab=\"res://chain/p{i + 1}.aprefab\"/>" : "";
            Write($"chain/p{i}.aprefab", $"<Prefab version=\"1\" nextId=\"3\"><Entity id=\"1\" name=\"P{i}\">{inner}</Entity></Prefab>");
        }

        Entity root = WithWarnings(() => World.Instantiate("res://chain/p0.aprefab"), out List<string> warnings);

        int depth = 0;
        for (Entity current = root; World.Children(current).Count > 0; current = World.Children(current)[0])
        {
            depth++;
        }

        Assert.Equal(World.MaxPrefabDepth, depth);                                          // 16 prefabs nested: the 17th is one empty entity
        Assert.Contains(warnings, w => w.Contains("nested more than 16", StringComparison.Ordinal));
        Assert.Equal(World.MaxPrefabDepth + 1, World.Subtree(root).Count);
    }

    [Fact]
    public void ANestedPrefabInstantiatesWithItsOwnInstances()
    {
        Entity corner = World.Instantiate(CornerPath);

        Assert.Equal(["Lamp", "Lamp 2", "Bench"], NamesBelow(World, corner));
        Entity lamp = Child(corner, "Lamp");
        Assert.Equal(new Double3(2, 0, 0), World.Get<Transform>(lamp).Position);
        Assert.Equal(9, World.Get<Health>(Child(lamp, "Bulb")).Current);                          // the corner's override of the lamp's bulb
        Assert.Equal(["Bulb"], NamesBelow(World, Child(corner, "Lamp 2")));                       // this one lost its Base
        Assert.True(World.IsPrefabInstance(lamp));
        Assert.Equal(World.Lookup("Corner"), corner);
    }

    [Fact]
    public void InstantiateRefusesAPrefabWhoseRootIsAnInstance()
    {
        Write("prefabs/variant.aprefab", $"<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"Variant\" prefab=\"{Lamp}\"/></Prefab>");

        Assert.Throws<FormatException>(() => World.Instantiate("res://prefabs/variant.aprefab"));
        Assert.Empty(World.UserEntities());
    }

    [Fact]
    public void AFailedLoadLeavesNothingBehind()
    {
        // A load that fails leaves no entity and no component it defined behind.
        World.MaxLoadEntities = 1;
        Assert.Throws<FormatException>(() => World.LoadScene($"""
            <Scene version="2">
              <Components><Component name="Late"><Field name="n" kind="Int32"/></Component></Components>
              <Entity id="1" prefab="{Lamp}"/>
            </Scene>
            """));
        Assert.True(World.FindComponent("Late").IsNull);
        Assert.Empty(World.UserEntities());
    }

    [Fact]
    public void ADuplicateInstanceNeverMeetsAnotherInstancesAddresses()
    {
        // An entity an instance adds claims an id of the scene; another instance's added entity with the same id must not take its place.
        World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="One" prefab="{Lamp}"><Entity id="7" name="AddedOne"/></Entity>
              <Entity id="2" name="Two" prefab="{Lamp}"><Entity id="7" name="AddedTwo"><Marks Target="7"/></Entity></Entity>
            </Scene>
            """);

        Assert.Equal(World.Lookup("One.AddedOne"), World.Get<Marks>(World.Lookup("Two.AddedTwo")).Target);   // "7" is the first that claimed it
    }

    [Fact]
    public void AnInstanceInAPrefabKeepsReferencesInsideItself()
    {
        Write("prefabs/pair.aprefab", $"""
            <Prefab version="1" nextId="5">
              <Entity id="1" name="Pair">
                <Entity id="2" name="Left" prefab="{Lamp}"><Override entity="2"><Marks Target="3"/></Override></Entity>
                <Entity id="3" name="Right" prefab="{Lamp}"/>
              </Entity>
            </Prefab>
            """);

        Entity first = World.Instantiate("res://prefabs/pair.aprefab");
        Entity second = World.Instantiate("res://prefabs/pair.aprefab");

        // "3" in the pair's override is the pair's entity 3, the right lamp of the same pair.
        Assert.Equal(Child(first, "Right"), World.Get<Marks>(Child(Child(first, "Left"), "Bulb")).Target);
        Assert.Equal(Child(second, "Right"), World.Get<Marks>(Child(Child(second, "Left"), "Bulb")).Target);
        Assert.NotEqual(Child(first, "Right"), World.Get<Marks>(Child(Child(second, "Left"), "Bulb")).Target);
        // The lamps' own references (Right's bulb points at Right's root) stay inside their instance.
        Assert.Equal(Child(first, "Right"), World.Get<Marks>(Child(Child(first, "Right"), "Bulb")).Target);
    }

    [Fact]
    public void LoadSceneFileFindsInstancesThroughTheirUid()
    {
        Write("scenes/s.ascene", $"<Scene version=\"2\"><Entity id=\"1\" name=\"ByUid\" prefab=\"{LampUid}\"/></Scene>");

        List<Entity> loaded = World.LoadSceneFile("res://scenes/s.ascene");

        Assert.Equal(3, loaded.Count);
        Assert.Equal(Lamp, World.PrefabOf(loaded[0]));                      // the instance now names the file and its UID
    }
}
