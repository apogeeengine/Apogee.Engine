using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Content.Xml;
using Apogee.Core.Assets;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Ecs.Tests;

/// <summary>
/// What an instance's document says that the live entities cannot show, and saving must give back: elements that could not be applied, references to entities that were not made, assets that did not
/// load, a reference that is no reference; and the order and the numbers things are written in.
/// </summary>
public sealed class PrefabKeptTests : PrefabTestBase
{
    private string LampScene(string body = "") => $"""
        <Scene version="2">
          <Entity id="1" name="Player"><Transform Position="1 2 3" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
          <Entity id="2" name="Lamp" prefab="{Lamp}">{body}</Entity>
        </Scene>
        """;

    private string SavesTheSameAfterALoad(World world)
    {
        string text = world.SaveScene();
        using World other = OtherWorld();
        other.LoadScene(text);
        Assert.Equal(text, other.SaveScene());
        return text;
    }

    private static XmlNode Element(string text, string name) => XmlNode.Parse(text).Children.Single(c => c.Name == "Entity" && c.Attribute("name") == name);

    private static Transform At(double x, double y, double z) => new(new Double3(x, y, z), Quaternion.Identity, Vector3.One);

    private string LampWithANinth() => Read("prefabs/lamp.aprefab")
        .Replace("<Entity id=\"3\" name=\"Base\">", "<Entity id=\"9\" name=\"Ninth\"><Flicker speed=\"1\" lamp=\"1\"/></Entity><Entity id=\"3\" name=\"Base\">", StringComparison.Ordinal)
        .Replace("nextId=\"4\"", "nextId=\"10\"", StringComparison.Ordinal);

    // ---- A kept element does not win over what was done since

    [Fact]
    public void AKeptPatchDoesNotOverrideAComponentTheUserAddedSince()
    {
        // The lamp's Base has no Health, so the patch of it could not apply and was kept; then the user gives the Base a Health of its own.
        World.LoadScene(LampScene("<Override entity=\"3\"><Health Max=\"30\"/></Override>"));
        Entity lampBase = Child(World.Lookup("Lamp"), "Base");
        Assert.False(World.Has<Health>(lampBase));
        World.Set(lampBase, new Health { Max = 5 });

        string text = SavesTheSameAfterALoad(World);

        using World again = OtherWorld();
        again.LoadScene(text);
        Assert.Equal(5, again.Get<Health>(Child(again, again.Lookup("Lamp"), "Base")).Max);     // the live world is what was saved, not the old patch
        Assert.Equal("3", Element(text, "Lamp").Children[0].Attribute("entity"));                 // the kept element is the first of the element, so what the world says is applied after it
        Assert.Equal("Health", Element(text, "Lamp").Children[0].Child("Health")!.Name);
    }

    [Fact]
    public void AKeptRemovalDoesNotRemoveAComponentTheUserAddedSince()
    {
        World.LoadScene(LampScene("<Override entity=\"3\"><Frozen remove=\"true\"/></Override>"));    // the Base has no Frozen to remove
        Entity lampBase = Child(World.Lookup("Lamp"), "Base");
        World.Add<Frozen>(lampBase);

        string text = SavesTheSameAfterALoad(World);

        using World again = OtherWorld();
        again.LoadScene(text);
        Assert.True(again.Has<Frozen>(Child(again, again.Lookup("Lamp"), "Base")));
    }

    [Fact]
    public void ARootPatchKeptForAMissingPrefabDoesNotWinWhenThePrefabIsBack()
    {
        World.LoadScene(LampScene("<Transform Position=\"9 0 0\"/>"));
        string lampText = Read("prefabs/lamp.aprefab");
        string saved = World.SaveScene();
        Delete("prefabs/lamp.aprefab");
        Assets.Refresh();

        using World without = OtherWorld();
        without.LoadScene(saved);
        Entity root = without.Lookup("Lamp");
        Assert.False(without.Has<Transform>(root));
        without.Set(root, At(5, 0, 0));                                                          // the empty instance is edited while the prefab is away
        string during = without.SaveScene();

        Write("prefabs/lamp.aprefab", lampText);
        Assets.Refresh();
        using World back = OtherWorld();
        back.LoadScene(during);
        Assert.Equal(new Double3(5, 0, 0), back.Get<Transform>(back.Lookup("Lamp")).Position);   // the edit, not the position the scene had before it
    }

    // ---- References to what was not made

    [Fact]
    public void AReferenceIntoAMissingInstanceComesBackWithTheInstance()
    {
        string scene = $"""
            <Scene version="2">
              <Entity id="1" name="Gone"/>
              <Entity id="2" name="Lamp" prefab="{Lamp}"/>
              <Entity id="3" name="Switch"><Marks Target="2/2"/></Entity>
            </Scene>
            """;
        string lampText = Read("prefabs/lamp.aprefab");
        Delete("prefabs/lamp.aprefab");
        Assets.Refresh();
        using World without = OtherWorld();
        without.LoadScene(scene);
        Assert.True(without.Get<Marks>(without.Lookup("Switch")).Target.IsNull);                 // the bulb is not there to point at

        without.Destroy(without.Lookup("Gone"));                                                 // and the numbers of the scene move
        string text = without.SaveScene();

        Assert.Equal("1/2", Element(text, "Switch").Child("Marks")!.Attribute("Target"));        // still what it was, below the instance, which is entity 1 now
        Write("prefabs/lamp.aprefab", lampText);
        Assets.Refresh();
        using World back = OtherWorld();
        back.LoadScene(text);
        Assert.Equal(Child(back, back.Lookup("Lamp"), "Bulb"), back.Get<Marks>(back.Lookup("Switch")).Target);
    }

    [Fact]
    public void AKeptElementsReferencesFollowTheEntitiesWhenTheSceneIsNumberedAgain()
    {
        // The entity 9 the override is for is not in the lamp (yet): the element is kept, and it names the Player, which is entity 3.
        World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Gone"/>
              <Entity id="2" name="Spare"/>
              <Entity id="3" name="Player"/>
              <Entity id="4" name="Lamp" prefab="{Lamp}"><Override entity="9"><Flicker lamp="3"/></Override></Entity>
            </Scene>
            """);
        World.Destroy(World.Lookup("Gone"));                                                     // the Player is entity 2 from now on

        string text = World.SaveScene();

        Assert.Equal("2", Element(text, "Lamp").Child("Override")!.Child("Flicker")!.Attribute("lamp"));
        Write("prefabs/lamp.aprefab", LampWithANinth());                                         // the lamp has its entity 9 now
        using World back = OtherWorld();
        back.LoadScene(text);
        Assert.Equal(back.Lookup("Player").Id, EntityField(back, Child(back, back.Lookup("Lamp"), "Ninth"), "Flicker", "lamp"));

        // The same when a deletion comes between a save and the next load.
        string lampText = Read("prefabs/lamp.aprefab");
        Write("prefabs/lamp.aprefab", lampText.Replace("<Entity id=\"9\" name=\"Ninth\"><Flicker speed=\"1\" lamp=\"1\"/></Entity>", "", StringComparison.Ordinal));
        using World middle = OtherWorld();
        middle.LoadScene(text);
        middle.Destroy(middle.Lookup("Spare"));
        string later = middle.SaveScene();
        Assert.Equal("1", Element(later, "Lamp").Child("Override")!.Child("Flicker")!.Attribute("lamp"));
        Write("prefabs/lamp.aprefab", lampText);
        using World last = OtherWorld();
        last.LoadScene(later);
        Assert.Equal(last.Lookup("Player").Id, EntityField(last, Child(last, last.Lookup("Lamp"), "Ninth"), "Flicker", "lamp"));
    }

    [Fact]
    public void AnEntityAKeptElementAddsKeepsItsNameForTheFieldsThatPointAtIt()
    {
        World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Gone"/>
              <Entity id="2" name="Lamp" prefab="{Lamp}"><Override entity="9"><Entity id="7" name="Added"/></Override></Entity>
              <Entity id="3" name="Switch"><Marks Target="7"/></Entity>
            </Scene>
            """);
        Assert.True(World.Get<Marks>(World.Lookup("Switch")).Target.IsNull);                     // not made: its parent is not there
        World.Destroy(World.Lookup("Gone"));

        string text = SavesTheSameAfterALoad(World);

        XmlNode added = Element(text, "Lamp").Child("Override")!.Child("Entity")!;
        string number = added.Attribute("id")!;
        Assert.Equal(number, Element(text, "Switch").Child("Marks")!.Attribute("Target"));       // the number of the kept entity and what names it agree
        Assert.NotEqual("7", number);                                                            // it does not keep the number of the save that kept it (and cannot meet an entity's)
        Write("prefabs/lamp.aprefab", LampWithANinth());
        using World back = OtherWorld();
        back.LoadScene(text);
        Assert.Equal(Child(back, Child(back, back.Lookup("Lamp"), "Ninth"), "Added"), back.Get<Marks>(back.Lookup("Switch")).Target);
    }

    // ---- Saving a prefab

    [Fact]
    public void APrefabSavedWithAKeptElementInItStillLoads()
    {
        World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Group">
                <Entity id="2" name="Lamp" prefab="{Lamp}"><Override entity="9"><Entity id="2" name="Ghost"><Marks Target="2"/></Entity></Override></Entity>
              </Entity>
            </Scene>
            """);

        World.SavePrefabFile("res://prefabs/group.aprefab", World.Lookup("Group"));

        XmlNode saved = XmlNode.Parse(Read("prefabs/group.aprefab"));
        var ids = new List<string>();
        void Collect(XmlNode node)
        {
            if (node.Name == "Entity")
            {
                ids.Add(node.Attribute("id")!);
            }

            node.Children.ForEach(Collect);
        }

        Collect(saved.Child("Entity")!);
        Assert.Equal(ids.Count, ids.Distinct().Count());                                          // the kept entity has a number of the file's, not the scene's
        using World fresh = OtherWorld();
        Entity group = fresh.Instantiate("res://prefabs/group.aprefab");                         // it loads
        Assert.True(fresh.IsPrefabInstance(Child(fresh, group, "Lamp")));
        Assert.Contains("name=\"Ghost\"", Read("prefabs/group.aprefab"), StringComparison.Ordinal);   // and still holds what could not be applied
    }

    // ---- Budgets, references that are none, assets that did not load

    [Fact]
    public void ASceneOverTheLimitWithOnlyItsOwnEntitiesStillLoads()
    {
        World.MaxLoadEntities = 2;
        Assert.Equal(5, World.LoadScene("<Scene version=\"1\"><Entity id=\"1\"/><Entity id=\"2\"/><Entity id=\"3\"/><Entity id=\"4\"/><Entity id=\"5\"/></Scene>").Count);
    }

    [Fact]
    public void AnAssetThatDidNotLoadIsNotAnOverrideAndIsNotLost()
    {
        bool broken = true;
        var names = new Dictionary<uint, AssetRef> { [7] = new("res://models/box.glb", AssetUid.New()) };
        World Make() => RegisterModels(OtherWorld(), names, () => broken);
        Write("prefabs/crate.aprefab", $"<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"Crate\"><Furnished Count=\"1\" Model=\"{names[7]}\"/></Entity></Prefab>");

        using World world = Make();
        world.LoadScene("<Scene version=\"2\"><Entity id=\"1\" name=\"Crate\" prefab=\"res://prefabs/crate.aprefab\"/><Entity id=\"2\" name=\"Plain\"><Furnished Count=\"2\" Model=\"res://models/gone.glb\"/></Entity></Scene>");
        Assert.Equal(0u, world.Get<Furnished>(world.Lookup("Crate")).Model);                     // the model could not be loaded
        string text = world.SaveScene();

        Assert.Empty(Element(text, "Crate").Children);                                           // so the instance has no override of it
        Assert.Equal("res://models/gone.glb", Element(text, "Plain").Child("Furnished")!.Attribute("Model"));   // and a plain entity keeps what it said

        broken = false;                                                                          // the model is there now
        using World later = Make();
        later.LoadScene(text);
        Assert.Equal(7u, later.Get<Furnished>(later.Lookup("Crate")).Model);
    }

    private static World RegisterModels(World world, Dictionary<uint, AssetRef> names, Func<bool> broken)
    {
        world.RegisterAssetType("model", handle => names.TryGetValue(handle, out AssetRef named) ? named : null, reference => reference.Path.EndsWith("gone.glb", StringComparison.Ordinal) || broken() ? 0u : 7u);
        return world;
    }

    [Fact]
    public void ASavedInstanceKeepsItsUidWhenThePrefabIsMovedByHand()
    {
        World.LoadScene(LampScene());
        string text = World.SaveScene();
        Write("prefabs/moved.aprefab", Read("prefabs/lamp.aprefab"));
        Delete("prefabs/lamp.aprefab");
        Assets.Refresh();

        string after = World.SaveScene();

        XmlNode element = Element(after, "Lamp");
        Assert.Contains(LampUid.ToString(), element.Attribute("prefab"), StringComparison.Ordinal);
        Assert.Empty(element.Children);                                                          // still the same instance of the same prefab, not an instance of a prefab that is missing
        Assert.Equal(text.Replace("res://prefabs/lamp.aprefab", "res://prefabs/moved.aprefab", StringComparison.Ordinal), after);
    }

    [Fact]
    public void ANumberThatIsNotAnEntityIdIsSaidWhenItIsRead()
    {
        List<Entity> loaded = WithWarnings(() => World.LoadScene("<Scene version=\"1\"><Entity id=\"1\" name=\"A\"/><Entity id=\"2\" name=\"B\"><Marks Target=\"01\"/></Entity><Entity id=\"3\" name=\"C\"><Marks Target=\"-1\"/></Entity></Scene>"), out List<string> warnings);

        Assert.Equal(3, loaded.Count);
        Assert.Equal(2, warnings.Count(w => w.Contains("not an entity id", StringComparison.Ordinal)));
    }

    [Fact]
    public void ARemovalWrittenAsOneIsAsGoodAsOneWrittenAsTrue()
    {
        World.LoadScene(LampScene("<Override entity=\"3\" remove=\"1\"/>"));

        Assert.Equal(["Bulb"], NamesBelow(World, World.Lookup("Lamp")));
    }

    // ---- Numbers

    [Fact]
    public void TheFirstSaveAfterEntityIdsWereReusedIsTheSecondSave()
    {
        // Entity ids that were freed come back with another generation, which sorts after the new ones: what was made later is not always after what was made before.
        for (int i = 0; i < 3; i++)
        {
            World.Destroy(World.CreateEntity($"temp{i}"));
        }

        World.LoadScene(LampScene());
        Entity lamp = World.Lookup("Lamp");
        Entity first = World.CreateEntity("FirstAdded", Child(lamp, "Bulb"));
        Entity second = World.CreateEntity("SecondAdded", Child(lamp, "Base"));
        World.Set(first, At(1, 0, 0));
        World.Set(second, At(2, 0, 0));
        World.Set(World.CreateEntity("RootAdded", lamp), At(3, 0, 0));
        _ = World.CreateEntity("Marker");

        string text = SavesTheSameAfterALoad(World);

        Assert.Contains("name=\"FirstAdded\"", text, StringComparison.Ordinal);
    }
}
