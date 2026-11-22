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
/// Saving instances of prefabs as what they change: the override elements worked out from the live entities, compared as the loader would make them, and the cases a prefab that moved on, went
/// missing or was replaced by a cycle makes of them.
/// </summary>
public sealed class PrefabDiffTests : PrefabTestBase
{
    private string LampScene(string body = "") => $"""
        <Scene version="2">
          <Entity id="1" name="Player"><Transform Position="1 2 3" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
          <Entity id="2" name="Lamp" prefab="{Lamp}">{body}</Entity>
          <Entity id="3" name="Switch"><Marks Target="2"/></Entity>
        </Scene>
        """;

    // Saves, reads what was saved into another world, and saves that: the same text, or the trip lost something.
    private string SavesTheSameAfterALoad(World world)
    {
        string text = world.SaveScene();
        using World other = OtherWorld();
        other.LoadScene(text);
        Assert.Equal(text, other.SaveScene());
        return text;
    }

    private static XmlNode Element(string text, string name) => XmlNode.Parse(text).Children.Single(c => c.Name == "Entity" && c.Attribute("name") == name);

    private static string[] Kinds(XmlNode element) => [.. element.Children.Select(c => c.Name == "Override" ? $"Override {c.Attribute("entity")}" : c.Name)];

    private static Transform At(double x, double y, double z) => new(new Double3(x, y, z), Quaternion.Identity, Vector3.One);

    [Fact]
    public void AnUnchangedInstanceIsOneElement()
    {
        World.LoadScene(LampScene());

        string text = SavesTheSameAfterALoad(World);

        XmlNode lamp = Element(text, "Lamp");
        Assert.Equal(Lamp, lamp.Attribute("prefab"));
        Assert.Equal("2", lamp.Attribute("id"));
        Assert.Empty(lamp.Children);                                  // nothing it changes: the prefab says all the rest
        Assert.Equal("2", XmlNode.Parse(text).Attribute("version"));
        Assert.Equal(["Player", "Lamp", "Switch"], XmlNode.Parse(text).Children.Select(c => c.Attribute("name")));
    }

    [Fact]
    public void OnlyTheChangedFieldIsWritten()
    {
        World.LoadScene(LampScene());
        Entity bulb = Child(World.Lookup("Lamp"), "Bulb");
        World.Set(bulb, World.Get<Health>(bulb) with { Current = 3 });
        World.Set(World.Lookup("Lamp"), At(4, 0, 0));

        string text = SavesTheSameAfterALoad(World);

        XmlNode lamp = Element(text, "Lamp");
        Assert.Equal(["Transform", "Override 2"], Kinds(lamp));
        Assert.Equal(["Position"], lamp.Child("Transform")!.Attributes.Select(a => a.Name));    // the rotation and scale are the prefab's
        Assert.Equal("4 0 0", lamp.Child("Transform")!.Attribute("Position"));
        XmlNode over = lamp.Child("Override")!;
        Assert.Equal(["Health"], over.Children.Select(c => c.Name));
        Assert.Equal([("Current", "3")], over.Child("Health")!.Attributes.Select(a => (a.Name, a.Value)));
        Assert.Null(over.Attribute("name"));
    }

    [Fact]
    public void AFloatSpelledDifferentlyInThePrefabIsNoOverride()
    {
        // The prefab spells numbers its own way, a field it leaves out is zero, and a bool may be 1: what matters is the value the loader makes.
        Write("prefabs/spelled.aprefab", """
            <Prefab version="1" nextId="3">
              <Entity id="1" name="Spelled">
                <Transform Position="1.0 2.50 0e0" Rotation="0 0 0 1.0" Scale="1.000 1 1"/>
                <Health Current="05" Regen="0.50"/>
                <Marks Alive="1" Level="2" Mass="1.50"/>
              </Entity>
            </Prefab>
            """);
        World.LoadScene("<Scene version=\"2\"><Entity id=\"1\" name=\"Spelled\" prefab=\"res://prefabs/spelled.aprefab\"/></Scene>");

        string text = SavesTheSameAfterALoad(World);

        Assert.Empty(Element(text, "Spelled").Children);

        // A change is still a change, and the text is the canonical spelling.
        World.Set(World.Lookup("Spelled"), World.Get<Health>(World.Lookup("Spelled")) with { Max = 1 });
        Assert.Equal([("Max", "1")], Element(World.SaveScene(), "Spelled").Child("Health")!.Attributes.Select(a => (a.Name, a.Value)));
    }

    [Fact]
    public void AnAssetRefWithOrWithoutItsUidIsTheSame()
    {
        AssetUid box = AssetUid.New();
        AssetUid other = AssetUid.New();
        Write("models/box.glb", "x");
        Write("models/other.glb", "x");
        var names = new Dictionary<uint, AssetRef> { [7] = new("res://models/box.glb", box), [8] = new("res://models/other.glb", other) };
        World.RegisterAssetType("model", handle => names.TryGetValue(handle, out AssetRef named) ? named : null, reference => reference.Path.EndsWith("other.glb", StringComparison.Ordinal) ? 8u : 7u);

        // The prefab spells the model as a path alone, as a UID alone, and as both: the world names it with both.
        foreach (string spelled in new[] { "res://models/box.glb", box.ToString(), $"res://models/box.glb|{box}" })
        {
            Write("prefabs/crate.aprefab", $"<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"Crate\"><Furnished Count=\"1\" Model=\"{spelled}\"/></Entity></Prefab>");
            using World world = OtherWorld();
            world.RegisterAssetType("model", handle => names.TryGetValue(handle, out AssetRef named) ? named : null, reference => reference.Path.EndsWith("other.glb", StringComparison.Ordinal) ? 8u : 7u);
            world.LoadScene("<Scene version=\"2\"><Entity id=\"1\" name=\"Crate\" prefab=\"res://prefabs/crate.aprefab\"/></Scene>");

            Assert.Equal(7u, world.Get<Furnished>(world.Lookup("Crate")).Model);
            Assert.Empty(Element(world.SaveScene(), "Crate").Children);          // no override whichever way the prefab spells it
        }

        // Another model is an override, written as the world names it.
        Write("prefabs/crate.aprefab", "<Prefab version=\"1\" nextId=\"2\"><Entity id=\"1\" name=\"Crate\"><Furnished Count=\"1\" Model=\"res://models/box.glb\"/></Entity></Prefab>");
        using World changed = OtherWorld();
        changed.RegisterAssetType("model", handle => names.TryGetValue(handle, out AssetRef named) ? named : null, reference => reference.Path.EndsWith("other.glb", StringComparison.Ordinal) ? 8u : 7u);
        changed.LoadScene("<Scene version=\"2\"><Entity id=\"1\" name=\"Crate\" prefab=\"res://prefabs/crate.aprefab\"/></Scene>");
        changed.Set(changed.Lookup("Crate"), new Furnished { Count = 1, Model = 8 });
        XmlNode furnished = Element(changed.SaveScene(), "Crate").Child("Furnished")!;
        Assert.Equal([("Model", $"res://models/other.glb|{other}")], furnished.Attributes.Select(a => (a.Name, a.Value)));

        Assert.True(PrefabDiff.SameAsset(null, null, Assets));
        Assert.False(PrefabDiff.SameAsset(new AssetRef("res://models/box.glb", box), null, Assets));
        Assert.False(PrefabDiff.SameAsset(new AssetRef("res://models/box.glb", box), new AssetRef("res://models/box.glb", other), Assets));   // two UIDs: two assets, whatever the paths say
        Assert.True(PrefabDiff.SameAsset(new AssetRef("res://models/box.glb", box), AssetRef.FromPath("res://models/box.glb"), Assets));
        Assert.True(PrefabDiff.SameAsset(AssetRef.FromPath("res://nowhere.glb"), AssetRef.FromPath("res://nowhere.glb"), null));       // no database: the paths
        Assert.False(PrefabDiff.SameAsset(AssetRef.FromUid(box), AssetRef.FromPath("res://nowhere.glb"), null));
    }

    [Theory]
    [InlineData("field")]
    [InlineData("name")]
    [InlineData("added component")]
    [InlineData("removed component")]
    [InlineData("added entity")]
    [InlineData("removed entity")]
    [InlineData("nested")]
    [InlineData("cross-instance reference")]
    public void EachOverrideKindRoundTrips(string kind)
    {
        if (kind == "nested")
        {
            World.LoadScene($"<Scene version=\"2\"><Entity id=\"1\" name=\"Corner\" prefab=\"{Corner}\"/></Scene>");
            Entity bulb = Child(Child(World.Lookup("Corner"), "Lamp"), "Bulb");
            World.Set(bulb, World.Get<Health>(bulb) with { Max = 40 });
            string saved = SavesTheSameAfterALoad(World);
            XmlNode over = Element(saved, "Corner").Child("Override")!;
            Assert.Equal("2/2", over.Attribute("entity"));                                  // the bulb of the corner's lamp
            Assert.Equal([("Max", "40")], over.Child("Health")!.Attributes.Select(a => (a.Name, a.Value)));
            using World loaded = OtherWorld();
            loaded.LoadScene(saved);
            Entity back = Child(Child(loaded.Lookup("Corner"), "Lamp"), "Bulb");
            Assert.Equal(40, loaded.Get<Health>(back).Max);
            Assert.Equal(9, loaded.Get<Health>(back).Current);                              // the corner's own override of it is still there
            return;
        }

        World.LoadScene(LampScene());
        Entity lamp = World.Lookup("Lamp");
        Entity bulbOfLamp = Child(lamp, "Bulb");
        Entity lampBase = Child(lamp, "Base");
        string text;
        switch (kind)
        {
            case "field":
                World.Set(bulbOfLamp, World.Get<Health>(bulbOfLamp) with { Regen = 2.25f });
                text = SavesTheSameAfterALoad(World);
                Assert.Equal("2.25", Element(text, "Lamp").Child("Override")!.Child("Health")!.Attribute("Regen"));
                break;
            case "name":
                World.SetName(lampBase, "Foot");
                text = SavesTheSameAfterALoad(World);
                Assert.Equal("Foot", Element(text, "Lamp").Child("Override")!.Attribute("name"));
                break;
            case "added component":
                World.Add<Frozen>(lampBase);
                text = SavesTheSameAfterALoad(World);
                Assert.Equal("true", Element(text, "Lamp").Child("Override")!.Child("Frozen")!.Attribute("append"));
                break;
            case "removed component":
                World.Remove<Marks>(bulbOfLamp);
                text = SavesTheSameAfterALoad(World);
                Assert.Equal("true", Element(text, "Lamp").Child("Override")!.Child("Marks")!.Attribute("remove"));
                break;
            case "added entity":
                Entity lens = World.CreateEntity("Lens", bulbOfLamp);
                World.Set(lens, At(0, 1, 0));
                text = SavesTheSameAfterALoad(World);
                Assert.Equal("Lens", Element(text, "Lamp").Child("Override")!.Child("Entity")!.Attribute("name"));
                break;
            case "removed entity":
                World.Destroy(lampBase);
                text = SavesTheSameAfterALoad(World);
                Assert.Equal("true", Element(text, "Lamp").Child("Override")!.Attribute("remove"));
                break;
            default:
                // A scene entity points into the instance and the instance points back at the scene: both survive being written as addresses.
                unsafe
                {
                    Entity flicker = World.FindComponent("Flicker");
                    *(ulong*)(World.GetPointer(bulbOfLamp, flicker) + World.GetComponentInfo(flicker)!.FindField("lamp")!.Offset) = World.Lookup("Player").Id;
                }

                World.Set(World.Lookup("Switch"), new Marks { Target = lampBase });
                text = SavesTheSameAfterALoad(World);
                Assert.Equal("1", Element(text, "Lamp").Child("Override")!.Child("Flicker")!.Attribute("lamp"));
                Assert.Equal("2/3", Element(text, "Switch").Child("Marks")!.Attribute("Target"));
                break;
        }

        // What was changed is what a world that loads the text has.
        using World loaded2 = OtherWorld();
        loaded2.LoadScene(text);
        Entity lamp2 = loaded2.Lookup("Lamp");
        switch (kind)
        {
            case "field": Assert.Equal(2.25f, loaded2.Get<Health>(Child(loaded2, lamp2, "Bulb")).Regen); break;
            case "name": Assert.Equal(["Bulb", "Foot"], NamesBelow(loaded2, lamp2)); break;
            case "added component": Assert.True(loaded2.Has<Frozen>(Child(loaded2, lamp2, "Base"))); break;
            case "removed component": Assert.False(loaded2.Has<Marks>(Child(loaded2, lamp2, "Bulb"))); break;
            case "added entity": Assert.Equal(["Lens"], NamesBelow(loaded2, Child(loaded2, lamp2, "Bulb"))); break;
            case "removed entity": Assert.Equal(["Bulb"], NamesBelow(loaded2, lamp2)); break;
            default:
                Assert.Equal(loaded2.Lookup("Player").Id, EntityField(loaded2, Child(loaded2, lamp2, "Bulb"), "Flicker", "lamp"));
                Assert.Equal(Child(loaded2, lamp2, "Base"), loaded2.Get<Marks>(loaded2.Lookup("Switch")).Target);
                break;
        }
    }

    [Fact]
    public void ASavedSceneReloadsAndSavesToTheSameText()
    {
        World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Player"><Transform Position="1 2 3" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
              <Entity id="2" name="Corner" prefab="{Corner}">
                <Transform Position="10 0 4"/>
                <Health Max="200" Current="200" append="true"/>
                <Override entity="4" name="Old bench"><Transform remove="true"/></Override>
                <Override entity="2"><Health Max="50" Current="50" append="true"/></Override>
                <Override entity="2/2"><Health Current="1"/><Flicker lamp="1"/></Override>
                <Override entity="3" remove="true"/>
                <Override entity="2/3"><Entity id="3" name="Sign"><Transform Position="0 0.5 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity></Override>
                <Entity id="4" name="Trash can"><Transform Position="5 0 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
              </Entity>
              <Entity id="5" name="Switch"><Marks Target="2/2"/></Entity>
            </Scene>
            """);

        string first = SavesTheSameAfterALoad(World);

        // Every override of the document is still an override of the text, and nothing else is.
        XmlNode corner = Element(first, "Corner");
        Assert.Equal(["Transform", "Health", "Override 2", "Override 2/2", "Override 2/3", "Override 3", "Override 4", "Entity"], Kinds(corner));
        Assert.Equal("true", corner.Child("Health")!.Attribute("append"));
        Assert.Equal("2/2", Element(first, "Switch").Child("Marks")!.Attribute("Target"));
        Assert.DoesNotContain("<Components>", first, StringComparison.Ordinal);                                // the lamp's file defines Flicker: a patch of it carries no schema
    }

    [Fact]
    public void ASavedScenesNumbersMayChangeAndReferencesFollow()
    {
        // The scene numbers its entities again at every save: what points into an instance, and what an instance points to, follows the entities and not the numbers.
        World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Gone"/>
              <Entity id="2" name="A" prefab="{Lamp}"><Override entity="2"><Flicker lamp="4"/></Override></Entity>
              <Entity id="3" name="B" prefab="{Lamp}"><Override entity="2"><Marks Target="2/3"/></Override></Entity>
              <Entity id="4" name="Scene entity"><Marks Target="3/3"/></Entity>
            </Scene>
            """);
        Entity a = World.Lookup("A");
        Entity b = World.Lookup("B");
        Assert.Equal(World.Lookup("Scene entity").Id, EntityField(World, Child(a, "Bulb"), "Flicker", "lamp"));
        Assert.Equal(Child(a, "Base"), World.Get<Marks>(Child(b, "Bulb")).Target);
        Assert.Equal(Child(b, "Base"), World.Get<Marks>(World.Lookup("Scene entity")).Target);
        World.Destroy(World.Lookup("Gone"));                                                     // every number after it moves down

        string text = SavesTheSameAfterALoad(World);

        Assert.Equal("1", Element(text, "A").Attribute("id"));
        using World loaded = OtherWorld();
        loaded.LoadScene(text);
        Assert.Equal(loaded.Lookup("Scene entity").Id, EntityField(loaded, Child(loaded, loaded.Lookup("A"), "Bulb"), "Flicker", "lamp"));
        Assert.Equal(Child(loaded, loaded.Lookup("A"), "Base"), loaded.Get<Marks>(Child(loaded, loaded.Lookup("B"), "Bulb")).Target);
        Assert.Equal(Child(loaded, loaded.Lookup("B"), "Base"), loaded.Get<Marks>(loaded.Lookup("Scene entity")).Target);
    }

    [Fact]
    public void AFieldSetBackToThePrefabValueIsNoLongerAnOverride()
    {
        World.LoadScene(LampScene("<Override entity=\"2\"><Health Current=\"3\"/></Override>"));
        Entity bulb = Child(World.Lookup("Lamp"), "Bulb");
        Assert.Contains("Override 2", Kinds(Element(World.SaveScene(), "Lamp")));

        World.Set(bulb, World.Get<Health>(bulb) with { Current = 5 });          // the lamp's own value

        Assert.Empty(Element(SavesTheSameAfterALoad(World), "Lamp").Children);
    }

    [Fact]
    public void AReparentedMemberIsSavedAsRemovedAndAdded()
    {
        World.LoadScene(LampScene());
        Entity lamp = World.Lookup("Lamp");
        Entity bulb = Child(lamp, "Bulb");
        Entity lampBase = Child(lamp, "Base");
        World.SetParent(lampBase, bulb);                                         // code that did not mind the rule

        string text = SavesTheSameAfterALoad(World);

        XmlNode element = Element(text, "Lamp");
        Assert.Equal(["Override 2", "Override 3"], Kinds(element));
        Assert.Equal("Base", element.Children[0].Child("Entity")!.Attribute("name"));       // a plain copy, under the bulb
        Assert.Equal("true", element.Children[1].Attribute("remove"));                      // and the member that is gone
        using World loaded = OtherWorld();
        loaded.LoadScene(text);
        Entity lamp2 = loaded.Lookup("Lamp");
        Assert.Equal(["Bulb"], NamesBelow(loaded, lamp2));
        Entity moved = Child(loaded, Child(loaded, lamp2, "Bulb"), "Base");
        Assert.False(loaded.IsPrefabMember(moved));                                         // it is an entity of the scene now
    }

    [Fact]
    public void ABranchOfAnInstanceCopiesAsPlainEntities()
    {
        World.LoadScene(LampScene());
        Entity bulb = Child(World.Lookup("Lamp"), "Bulb");

        string text = World.SaveScene(bulb);                                     // what the clipboard writes for a selected member

        XmlNode scene = XmlNode.Parse(text);
        Assert.Equal("1", scene.Attribute("version"));
        XmlNode root = scene.Child("Entity")!;
        Assert.Null(root.Attribute("prefab"));
        Assert.Equal("Bulb", root.Attribute("name"));
        Assert.Equal(["Flicker", "Health", "Marks", "Transform"], root.Children.Select(c => c.Name).Order());
        Assert.Null(root.Child("Marks")!.Attribute("Target"));                   // its target (the lamp's root) is not in the branch
        using World loaded = OtherWorld();
        List<Entity> made = loaded.LoadScene(text);
        Assert.False(loaded.IsPrefabInstance(made[0]));
        Assert.False(loaded.Has<PrefabMember>(made[0]));
    }

    [Fact]
    public void AnInstanceCopiedWithItsBranchIsAnInstanceInTheClipboardText()
    {
        World.LoadScene(LampScene("<Override entity=\"3\" remove=\"true\"/>"));
        Entity lamp = World.Lookup("Lamp");

        string text = World.SaveScene(lamp);

        XmlNode root = XmlNode.Parse(text).Child("Entity")!;
        Assert.Equal(Lamp, root.Attribute("prefab"));
        Assert.Equal("1", root.Attribute("id"));
        Assert.Equal(["Override 3"], Kinds(root));
        using World loaded = OtherWorld();
        List<Entity> made = loaded.LoadScene(text);
        Assert.Equal(2, made.Count);
        Assert.True(loaded.IsPrefabInstance(made[0]));
    }

    [Fact]
    public void ASceneWithoutInstancesStaysVersion1AndByteEqual()
    {
        Entity a = World.CreateEntity("a");
        World.Set(a, At(1, 2, 3));
        Entity b = World.CreateEntity("b", a);
        World.Set(b, new Health { Current = 2, Max = 4 });
        World.Set(World.CreateEntity("c"), new Marks { Level = 3, Target = b });

        string text = World.SaveScene();

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Scene version=\"1\">", text, StringComparison.Ordinal);
        using World loaded = OtherWorld();
        loaded.LoadScene(text);
        Assert.Equal(text, loaded.SaveScene());
    }

    [Fact]
    public void UnappliedOverridesAreWrittenBackVerbatim()
    {
        // A scene written against a lamp that had an entity 9: an override of it, a component patch the bulb no longer has, an added entity whose parent is removed, a bad address.
        const string Kept = """
            <Override entity="9"><Health Current="3"/></Override>
            <Override entity="2"><Frozen/><Health Max="30"/></Override>
            <Override entity="3" remove="true"/>
            <Override entity="3"><Entity id="4" name="Orphan"><Transform Position="1 1 1"/></Entity></Override>
            <Override entity="two"><Health Max="1"/></Override>
            <Override><Health Max="1"/></Override>
            <Override entity="2" colour="red"><Health Max="1"/></Override>
            """;
        List<Entity> loaded = WithWarnings(() => World.LoadScene(LampScene(Kept)), out List<string> warnings);

        Entity lamp = loaded[1];
        Assert.Equal(30, World.Get<Health>(Child(lamp, "Bulb")).Max);                                   // what could apply did
        Assert.Equal(["Bulb"], NamesBelow(World, lamp));
        Assert.True(warnings.Count >= 5);

        string text = World.SaveScene();

        XmlNode element = Element(text, "Lamp");
        Assert.Equal(["Override 2", "Override 3"], Kinds(element)[6..]);                               // what applies is written as what it is, after what was kept: a change and a removal
        Assert.Equal([("Max", "30")], element.Children[6].Child("Health")!.Attributes.Select(a => (a.Name, a.Value)));
        Assert.Equal("true", element.Children[7].Attribute("remove"));

        // What was kept is what the document held, first and in its own order and spelling: the part of an override that did not apply stays under an override of the same entity.
        string[] expected =
        [
            "<Override entity=\"9\"><Health Current=\"3\"/></Override>",
            "<Override entity=\"2\"><Frozen/></Override>",
            "<Override entity=\"3\"><Entity id=\"4\" name=\"Orphan\"><Transform Position=\"1 1 1\"/></Entity></Override>",
            "<Override entity=\"two\"><Health Max=\"1\"/></Override>",
            "<Override><Health Max=\"1\"/></Override>",
            "<Override entity=\"2\" colour=\"red\"><Health Max=\"1\"/></Override>",
        ];
        Assert.Equal(expected.Select(x => XmlNode.Parse(x).ToXml()), element.Children.Take(6).Select(c => c.ToXml()));

        // An element that applies now, to an entity a later one removes, is kept again where the removal puts it: the second text has the same elements in another order, and the third is the second.
        using World second = OtherWorld();
        second.LoadScene(text);
        string settled = second.SaveScene();
        Assert.Equal(expected.Select(x => XmlNode.Parse(x).ToXml()).Order(), Element(settled, "Lamp").Children.Take(6).Select(c => c.ToXml()).Order());
        using World third = OtherWorld();
        third.LoadScene(settled);
        Assert.Equal(settled, third.SaveScene());

        // The prefab gets the entity and the component back: they apply again, and nothing was lost to make it so.
        Write("prefabs/lamp.aprefab", Read("prefabs/lamp.aprefab").Replace("<Entity id=\"3\" name=\"Base\">", "<Entity id=\"9\" name=\"Ninth\"><Health Current=\"1\"/></Entity><Entity id=\"3\" name=\"Base\"><Frozen/>", StringComparison.Ordinal));
        using World again = OtherWorld();
        again.LoadScene(text);
        Entity lamp2 = again.Lookup("Lamp");
        Assert.Equal(3, again.Get<Health>(Child(again, lamp2, "Ninth")).Current);
    }

    [Fact]
    public void AMissingPrefabKeepsItsInstanceDataOnSave()
    {
        const string Body = """
            <Transform Position="9 0 0"/>
            <Override entity="2"><Health Current="3"/></Override>
            <Entity id="4" name="Added"><Marks Target="1"/></Entity>
            """;
        World.LoadScene(LampScene(Body));
        string before = World.SaveScene();
        string lampText = Read("prefabs/lamp.aprefab");

        // The lamp is gone (a disabled mod, a bad merge): the scene loads with an empty instance and saves with everything it held.
        Delete("prefabs/lamp.aprefab");
        Assets.Refresh();
        using World without = OtherWorld();
        List<Entity> loaded = WithWarnings(() => without.LoadScene(before), out List<string> warnings);
        Assert.Equal(["Player", "Lamp", "Switch"], loaded.Select(e => without.GetName(e)));
        Assert.Contains(warnings, w => w.Contains("cannot be used", StringComparison.Ordinal));
        Assert.Empty(without.Children(without.Lookup("Lamp")));

        string during = without.SaveScene();
        Assert.Equal(Kinds(Element(before, "Lamp")), Kinds(Element(during, "Lamp")));    // the same element, with everything it held
        using World same = OtherWorld();
        same.LoadScene(during);
        Assert.Equal(during, same.SaveScene());

        // The lamp is back: the instance is what it was.
        Write("prefabs/lamp.aprefab", lampText);
        Assets.Refresh();
        using World back = OtherWorld();
        back.LoadScene(during);
        Entity lamp = back.Lookup("Lamp");
        Assert.Equal(new Double3(9, 0, 0), back.Get<Transform>(lamp).Position);
        Assert.Equal(3, back.Get<Health>(Child(back, lamp, "Bulb")).Current);
        Assert.Equal(lamp.Id, EntityField(back, Child(back, lamp, "Bulb"), "Flicker", "lamp"));
        Assert.Equal(back.Lookup("Player"), back.Get<Marks>(Child(back, lamp, "Added")).Target);
    }

    private static void AssertMatchesSchema(string schemaFile, string resource, XmlNode document)
    {
        var schemas = new SchemaSet();
        using (Stream stream = typeof(World).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"{resource} is not embedded"))
        using (var reader = new StreamReader(stream))
        {
            schemas.LoadText(reader.ReadToEnd(), schemaFile);
        }

        DecodeResult result = schemas.Decode(document, schemaFile);
        Assert.True(result.Declared);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void TheWrittenSceneMatchesItsSchema()
    {
        World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Player"/>
              <Entity id="2" name="Corner" prefab="{Corner}">
                <Transform Position="10 0 4"/>
                <Override entity="4" name="Old bench"><Transform remove="true"/></Override>
                <Override entity="2/2"><Health Current="1"/><Flicker lamp="1"/></Override>
                <Override entity="3" remove="true"/>
                <Override entity="2/3"><Entity id="3" name="Sign"/></Override>
                <Entity id="4" name="Trash can"/>
              </Entity>
            </Scene>
            """);
        XmlNode document = World.SaveSceneDocument(World.Roots());

        AssertMatchesSchema("scene.xsd", "Scene/scene.xsd", document);
    }

    [Fact]
    public void ANestedPrefabSavesWithStableIds()
    {
        Entity corner = World.Instantiate(CornerPath);
        World.Destroy(Child(corner, "Bench"));                                                // id 4 goes
        Entity lampBase = Child(Child(corner, "Lamp"), "Base");
        World.Set(World.CreateEntity("Sign", lampBase), At(0, 0.5, 0));                       // an entity added under a member of the nested lamp
        World.Set(World.CreateEntity("Pole", corner), At(0, 0, 1));

        World.SavePrefabFile(CornerPath, corner);

        XmlNode saved = XmlNode.Parse(Read("prefabs/corner.aprefab"));
        Assert.Equal(CornerUid.ToString(), saved.Attribute("uid"));
        Assert.Equal("7", saved.Attribute("nextId"));
        XmlNode root = saved.Child("Entity")!;
        Assert.Equal("1", root.Attribute("id"));
        Assert.Equal([("2", "Lamp"), ("3", "Lamp 2"), ("6", "Pole")], root.Children.Where(c => c.Name == "Entity").Select(c => (c.Attribute("id")!, c.Attribute("name")!)));
        XmlNode lamp = root.Children.Single(c => c.Attribute("id") == "2");
        Assert.Equal(Lamp, lamp.Attribute("prefab"));
        Assert.Equal("2 0 0", lamp.Child("Transform")!.Attribute("Position"));                // what the corner had always changed in its lamp is still written
        Assert.Equal(["Transform", "Override 2", "Override 3"], Kinds(lamp));                 // the bulb's Health, and the new sign under the base
        Assert.Equal("9", lamp.Children[1].Child("Health")!.Attribute("Current"));
        XmlNode sign = lamp.Children[2].Child("Entity")!;
        Assert.Equal(("5", "Sign"), (sign.Attribute("id")!, sign.Attribute("name")!));        // numbered in the corner's file, after the highest it ever had
        Assert.Equal("true", root.Children.Single(c => c.Attribute("id") == "3").Child("Override")!.Attribute("remove"));
        AssertMatchesSchema("prefab.xsd", "Scene/prefab.xsd", saved);

        // The corner's entities are members of the file: saved again they keep their ids, and a deleted id is never taken again.
        Assert.Equal(new PrefabMember { Id = 5, Prefab = World.Prefabs.Load(CornerPath) }, World.Get<PrefabMember>(Child(lampBase, "Sign")));
        Assert.Equal(new PrefabMember { Id = 2, Prefab = World.Prefabs.Load(CornerPath) }, World.Get<PrefabMember>(Child(corner, "Lamp")));
        World.Destroy(Child(corner, "Pole"));
        _ = World.CreateEntity("Beam", corner);
        World.SavePrefabFile(CornerPath, corner);
        XmlNode again = XmlNode.Parse(Read("prefabs/corner.aprefab"));
        Assert.Equal("8", again.Attribute("nextId"));
        Assert.Equal([("2", "Lamp"), ("3", "Lamp 2"), ("7", "Beam")], again.Child("Entity")!.Children.Where(c => c.Name == "Entity").Select(c => (c.Attribute("id")!, c.Attribute("name")!)));

        // And it is the same corner when it is made again.
        using World fresh = OtherWorld();
        Entity made = fresh.Instantiate(CornerPath);
        Assert.Equal(["Lamp", "Lamp 2", "Beam"], NamesBelow(fresh, made));
        Assert.Equal(new Double3(2, 0, 0), fresh.Get<Transform>(Child(fresh, made, "Lamp")).Position);
        Assert.Equal(9, fresh.Get<Health>(Child(fresh, Child(fresh, made, "Lamp"), "Bulb")).Current);
        Assert.Equal(["Sign"], NamesBelow(fresh, Child(fresh, Child(fresh, made, "Lamp"), "Base")));
        Assert.Equal(["Bulb"], NamesBelow(fresh, Child(fresh, made, "Lamp 2")));
    }

    [Fact]
    public void PlayTextKeepsInstances()
    {
        World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Corner" prefab="{Corner}"><Override entity="2/2"><Health Current="1"/></Override><Entity id="2" name="Extra"/></Entity>
              <Entity id="3" name="Switch"><Marks Target="1/2"/></Entity>
            </Scene>
            """);

        // The editor's Play saves the text, a second session loads it, and Stop puts the snapshot back in a third: the instance is the same in all of them.
        string play = World.SaveScene();
        using World session = OtherWorld();
        session.LoadScene(play);
        Assert.Equal(play, session.SaveScene());

        WorldSnapshot before = World.Snapshot();
        using World stopped = OtherWorld();
        _ = stopped.Prefabs.Load(LampPath);                                  // numbers its prefabs another way than the world the snapshot is from
        stopped.Restore(before);
        Assert.Equal(play, stopped.SaveScene());
        Assert.Equal(stopped.PrefabOf(stopped.Lookup("Corner")), World.PrefabOf(World.Lookup("Corner")));
    }

    [Fact]
    public void APrefabThatChangedSinceTheSceneWasSavedStillLoadsAndSaves()
    {
        World.LoadScene(LampScene("<Transform Position=\"7 0 0\"/><Override entity=\"3\" name=\"Foot\"><Frozen append=\"true\"/></Override><Override entity=\"2\"><Health Current=\"3\"/></Override>"));
        string saved = World.SaveScene();

        // The lamp moved on: its Base is gone, it has a Lens now, and the bulb has other values.
        string lamp = Read("prefabs/lamp.aprefab");
        Write("prefabs/lamp.aprefab", lamp
            .Replace("<Entity id=\"3\" name=\"Base\"><Transform Position=\"0 0 0\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/></Entity>", "<Entity id=\"4\" name=\"Lens\"><Frozen/></Entity>", StringComparison.Ordinal)
            .Replace("<Health Current=\"5\" Max=\"9\" Regen=\"0.5\"/>", "<Health Current=\"5\" Max=\"90\" Regen=\"0.75\"/>", StringComparison.Ordinal)
            .Replace("nextId=\"4\"", "nextId=\"5\"", StringComparison.Ordinal));
        using World newer = OtherWorld();
        List<Entity> loaded = WithWarnings(() => newer.LoadScene(saved), out List<string> warnings);

        Entity instance = newer.Lookup("Lamp");
        Assert.Equal(["Bulb", "Lens"], NamesBelow(newer, instance));
        Assert.Equal(new Double3(7, 0, 0), newer.Get<Transform>(instance).Position);
        Health bulb = newer.Get<Health>(Child(newer, instance, "Bulb"));
        Assert.Equal((3, 90, 0.75f), (bulb.Current, bulb.Max, bulb.Regen));               // the scene's override kept, the prefab's new values taken
        Assert.Contains(warnings, w => w.Contains("no entity 3", StringComparison.Ordinal));

        // Saved again, what the scene said about the entity that is gone is still there; what is new in the prefab is not an override.
        string text = SavesTheSameAfterALoad(newer);
        XmlNode element = Element(text, "Lamp");
        Assert.Equal(["Override 3", "Transform", "Override 2"], Kinds(element));                  // what is kept first, then what the world says
        Assert.Equal(XmlNode.Parse("<Override entity=\"3\" name=\"Foot\"><Frozen append=\"true\"/></Override>").ToXml(), element.Children[0].ToXml());
        Assert.Equal(["Health"], element.Children[2].Children.Select(c => c.Name));
        Assert.Equal([("Current", "3")], element.Children[2].Child("Health")!.Attributes.Select(a => (a.Name, a.Value)));

        // The old lamp comes back (the mod is enabled again): the kept override applies.
        Write("prefabs/lamp.aprefab", lamp);
        using World older = OtherWorld();
        older.LoadScene(text);
        Assert.Equal(["Bulb", "Foot"], NamesBelow(older, older.Lookup("Lamp")));
        Assert.True(older.Has<Frozen>(Child(older, older.Lookup("Lamp"), "Foot")));
        Assert.Equal(loaded.Count, newer.UserEntities().Count);
    }

    [Fact]
    public unsafe void ANewFieldOfAScriptComponentIsNotAnOverride()
    {
        World.LoadScene(LampScene("<Override entity=\"2\"><Flicker speed=\"9\"/></Override>"));
        string saved = World.SaveScene();

        // The lamp's Flicker has a field it did not have when the scene was written.
        Write("prefabs/lamp.aprefab", Read("prefabs/lamp.aprefab")
            .Replace("<Field name=\"lamp\" kind=\"Entity\"/>", "<Field name=\"lamp\" kind=\"Entity\"/><Field name=\"phase\" kind=\"Float32\"/>", StringComparison.Ordinal)
            .Replace("<Flicker speed=\"2\" lamp=\"1\"/>", "<Flicker speed=\"2\" lamp=\"1\" phase=\"0.5\"/>", StringComparison.Ordinal));
        using World newer = OtherWorld();
        newer.LoadScene(saved);

        Entity bulb = Child(newer, newer.Lookup("Lamp"), "Bulb");
        Entity flicker = newer.FindComponent("Flicker");
        ComponentInfo info = newer.GetComponentInfo(flicker)!;
        byte* data = newer.GetPointer(bulb, flicker);
        Assert.Equal(9f, *(float*)(data + info.FindField("speed")!.Offset));      // the scene's override
        Assert.Equal(0.5f, *(float*)(data + info.FindField("phase")!.Offset));    // the prefab's new field, as the prefab has it

        string text = SavesTheSameAfterALoad(newer);
        XmlNode flickerPatch = Element(text, "Lamp").Child("Override")!.Child("Flicker")!;
        Assert.Equal([("speed", "9")], flickerPatch.Attributes.Select(a => (a.Name, a.Value)));   // only what the scene changes
        Assert.DoesNotContain("<Components>", text, StringComparison.Ordinal);                     // the component's layout is the prefab's to define: the scene does not pin an old one
    }

    [Fact]
    public void AnInstanceWhoseFileBecomesACycleStillSavesAndLoads()
    {
        World.LoadScene(LampScene());
        string lamp = Read("prefabs/lamp.aprefab");

        // The file is replaced by one that holds an instance of itself, and the world is told it changed.
        Write("prefabs/lamp.aprefab", lamp.Replace("<Entity id=\"3\" name=\"Base\"><Transform Position=\"0 0 0\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/></Entity>", $"<Entity id=\"4\" name=\"Again\" prefab=\"{LampPath}\"/>", StringComparison.Ordinal));
        World.Prefabs.Written(World.Prefabs.Load(LampPath));
        World.ApplyPrefabUpdates();                                                                // (an update cannot make a prefab that contains itself: the instance stays as it was)
        string text = WithWarnings(() => World.SaveScene(), out List<string> warnings);

        Assert.Contains(warnings, w => w.Contains("contains itself", StringComparison.Ordinal));
        XmlNode element = Element(text, "Lamp");
        Assert.Equal("Base", element.Child("Entity")!.Attribute("name"));                        // the base is no longer the prefab's: it is the scene's now
        Assert.Contains(element.Children, c => c.Attribute("entity") == "4" && c.Attribute("remove") == "true");   // and the cycle is not something the instance keeps

        using World again = OtherWorld();
        again.LoadScene(text);
        Assert.Equal(["Bulb", "Base"], NamesBelow(again, again.Lookup("Lamp")));
        Assert.Equal(text, again.SaveScene());
    }

    [Fact]
    public void APrefabCannotBeSavedHoldingItself()
    {
        Entity holder = World.CreateEntity("Holder");
        _ = World.Instantiate(LampPath, holder);
        string before = Read("prefabs/lamp.aprefab");

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => World.SavePrefabFile(LampPath, holder));

        Assert.Contains("itself", e.Message, StringComparison.Ordinal);
        Assert.Equal(before, Read("prefabs/lamp.aprefab"));                                      // nothing was written

        // Under another name it is a prefab like any other, with the lamp inside it as an instance.
        World.SavePrefabFile("res://prefabs/holder.aprefab", holder);
        XmlNode saved = XmlNode.Parse(Read("prefabs/holder.aprefab"));
        Assert.Equal(Lamp, saved.Child("Entity")!.Child("Entity")!.Attribute("prefab"));
        using World fresh = OtherWorld();
        Entity made = fresh.Instantiate("res://prefabs/holder.aprefab");
        Assert.True(fresh.IsPrefabInstance(Child(fresh, made, "Lamp")));
        Assert.Equal(["Bulb", "Base"], NamesBelow(fresh, Child(fresh, made, "Lamp")));
    }

    [Fact]
    public void ACloneOfAnInstanceSavesAsAnInstance()
    {
        World.LoadScene(LampScene("<Override entity=\"3\" remove=\"true\"/>"));
        Entity lamp = World.Lookup("Lamp");

        Entity copy = World.Clone(lamp);

        string text = SavesTheSameAfterALoad(World);
        XmlNode scene = XmlNode.Parse(text);
        XmlNode copied = scene.Children.Single(c => c.Attribute("name") == "Lamp (copy)");
        Assert.Equal(Lamp, copied.Attribute("prefab"));
        Assert.Contains(copied.Children, c => c.Attribute("entity") == "2" && c.Attribute("name") == "Bulb (copy)");   // the copies' names are overrides, as the names of members are
        Assert.Contains(copied.Children, c => c.Attribute("entity") == "3" && c.Attribute("remove") == "true");        // so is what the original had removed
        Assert.Equal(World.Subtree(lamp).Count, World.Subtree(copy).Count);
    }

    [Fact]
    public void ACopyOfAMembersTagIsAnAdditionNotAPairedEntity()
    {
        World.LoadScene(LampScene());
        Entity lamp = World.Lookup("Lamp");
        Entity bulb = Child(lamp, "Bulb");
        Entity twin = World.Clone(bulb, lamp);
        World.Set(twin, World.Get<PrefabMember>(bulb));                                          // the same member id again, as a script that copies components would make

        string text = SavesTheSameAfterALoad(World);

        XmlNode element = Element(text, "Lamp");
        XmlNode added = Assert.Single(element.Children, c => c.Name == "Entity");
        Assert.Equal("Bulb (copy)", added.Attribute("name"));                                     // the second one is the addition; the first is the prefab's bulb, unchanged
        Assert.DoesNotContain(element.Children, c => c.Name == "Override");
    }

    [Fact]
    public void AnInstanceAddedToAnInstanceSavesAsAnInstanceOfItsOwn()
    {
        World.LoadScene(LampScene());
        Entity lamp = World.Lookup("Lamp");
        Entity inner = World.Instantiate(LampPath, Child(lamp, "Base"));
        World.Set(inner, At(0, 5, 0));
        World.Set(Child(inner, "Bulb"), World.Get<Health>(Child(inner, "Bulb")) with { Max = 70 });

        string text = SavesTheSameAfterALoad(World);

        XmlNode over = Element(text, "Lamp").Child("Override")!;
        Assert.Equal("3", over.Attribute("entity"));
        XmlNode added = over.Child("Entity")!;
        Assert.Equal(Lamp, added.Attribute("prefab"));                                            // an instance within the override, with its own changes
        Assert.Equal("0 5 0", added.Child("Transform")!.Attribute("Position"));
        Assert.Equal("Override 2", Kinds(added)[1]);
        using World loaded = OtherWorld();
        loaded.LoadScene(text);
        Entity innerLoaded = Child(loaded, Child(loaded, loaded.Lookup("Lamp"), "Base"), "Lamp");
        Assert.True(loaded.IsPrefabInstance(innerLoaded));
        Assert.Equal(70, loaded.Get<Health>(Child(loaded, innerLoaded, "Bulb")).Max);
        Assert.Equal(innerLoaded, loaded.Get<Marks>(Child(loaded, innerLoaded, "Bulb")).Target);  // its own references stay inside it
    }

    [Fact]
    public void WhatTheWorldCannotDescribeIsNeitherRemovedNorKept()
    {
        Write("prefabs/lamp.aprefab", Read("prefabs/lamp.aprefab").Replace("<Entity id=\"3\" name=\"Base\">", "<Entity id=\"3\" name=\"Base\"><Mystery a=\"1\"/><Frozen/>", StringComparison.Ordinal));
        World.IgnoreInScenes("Frozen");                                                           // only means something in this run
        World.LoadScene(LampScene());
        Entity lampBase = Child(World.Lookup("Lamp"), "Base");
        Assert.True(World.Has<Frozen>(lampBase));

        World.Remove<Frozen>(lampBase);                                                           // not saved in scenes, so not an override either way
        World.Add<Frozen>(Child(World.Lookup("Lamp"), "Bulb"));

        string text = SavesTheSameAfterALoad(World);

        Assert.Empty(Element(text, "Lamp").Children);                                             // Mystery is unknown here and not in what is written: it is not a removal
    }

    [Fact]
    public void ASnapshotRestoredAfterTheSceneChangedKeepsWhatWasKeptAside()
    {
        World.LoadScene(LampScene("<Override entity=\"9\"><Health Current=\"3\"/></Override>"));
        WorldSnapshot snapshot = World.Snapshot();
        World.Destroy(World.Lookup("Lamp"));
        Assert.DoesNotContain("entity=\"9\"", World.SaveScene(), StringComparison.Ordinal);

        World.Restore(snapshot);

        Assert.Contains("entity=\"9\"", World.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstanceOfAPrefabTheLibraryDoesNotKnowIsSavedAsPlainEntities()
    {
        // A handle nobody can name (code set the component by hand): there is nothing to write as an instance, and nothing is thrown.
        Entity entity = World.CreateEntity("Odd");
        World.Set(entity, new PrefabInstance { Prefab = 4242 });
        World.Set(entity, new Health { Current = 2 });

        string text = WithWarnings(() => World.SaveScene(), out List<string> warnings);

        Assert.Null(Element(text, "Odd").Attribute("prefab"));
        Assert.Equal("2", Element(text, "Odd").Child("Health")!.Attribute("Current"));
        Assert.Equal("1", XmlNode.Parse(text).Attribute("version"));
        Assert.Contains(warnings, w => w.Contains("4242", StringComparison.Ordinal));
    }
}
