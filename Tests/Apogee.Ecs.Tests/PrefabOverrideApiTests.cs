using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content.Xml;
using Apogee.Core.Assets;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Ecs.Tests;

/// <summary>
/// The overrides API an editor calls: list what an instance changed, revert it, apply it to the prefab file, break the link, make a prefab from entities. The prefab files here are the lamp (a root, a
/// Bulb with Health, Marks and Flicker, a Base) and the corner (a Corner with two nested lamps and a Bench) of <see cref="PrefabTestBase"/>.
/// </summary>
public sealed class PrefabOverrideApiTests : PrefabTestBase
{
    private string LampScene(string body = "") => $"""
        <Scene version="2">
          <Entity id="1" name="Player"><Transform Position="1 2 3" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
          <Entity id="2" name="Lamp" prefab="{Lamp}">{body}</Entity>
          <Entity id="3" name="Lamp 2" prefab="{Lamp}"/>
        </Scene>
        """;

    private Entity LoadLamp(string body = "")
    {
        World.LoadScene(LampScene(body));
        return World.Lookup("Lamp");
    }

    private static string Describe(PrefabOverride o) => $"{o.Kind} {o.Target}|{o.Component}|{o.Field}";

    private static string[] Listed(World world, Entity root) => [.. world.PrefabOverrides(root).Select(Describe)];

    [Fact]
    public void AnUnchangedInstanceHasNoOverrides()
    {
        Entity lamp = LoadLamp();

        Assert.Empty(World.PrefabOverrides(lamp));
        Assert.False(World.IsOverridden(Child(lamp, "Bulb"), "Health"));
    }

    [Fact]
    public void EveryKindOfOverrideIsListedWhereItIs()
    {
        Entity lamp = LoadLamp("""
            <Transform Position="5 0 0"/>
            <Override entity="2" name="Globe"><Health Max="40"/><Frozen append="true"/><Flicker remove="true"/></Override>
            <Override entity="3" remove="true"/>
            <Entity id="9" name="Cord"><Transform Position="0 0 1" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
            """);

        string[] listed = Listed(World, lamp);

        Assert.Equal(
        [
            "Field |Transform|Position",
            "Name 2||",
            "Field 2|Health|Max",
            "RemovedComponent 2|Flicker|",
            "AddedComponent 2|Frozen|",
            "RemovedEntity 3||",
            "AddedEntity ||",
        ], listed);
        Entity bulb = Child(lamp, "Globe");
        Assert.All(World.PrefabOverrides(lamp).Where(o => o.Target == "2"), o => Assert.Equal(bulb, o.Entity));
        Assert.Equal(Child(lamp, "Cord"), World.PrefabOverrides(lamp).Single(o => o.Kind == PrefabOverrideKind.AddedEntity).Entity);
        Assert.True(World.PrefabOverrides(lamp).Single(o => o.Kind == PrefabOverrideKind.RemovedEntity).Entity.IsNull);
    }

    [Fact]
    public void IsOverriddenSaysWhichFieldsAreChanged()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Health Max=\"40\"/><Frozen append=\"true\"/></Override>");
        Entity bulb = Child(lamp, "Bulb");

        Assert.True(World.IsOverridden(bulb, "Health"));
        Assert.True(World.IsOverridden(bulb, "Health", "Max"));
        Assert.False(World.IsOverridden(bulb, "Health", "Current"));
        Assert.True(World.IsOverridden(bulb, "Frozen"));
        Assert.False(World.IsOverridden(bulb, "Marks"));
        Assert.False(World.IsOverridden(Child(lamp, "Base"), "Transform"));
        Assert.False(World.IsOverridden(World.Lookup("Player"), "Transform"));   // not part of an instance

        World.Set(Child(lamp, "Base"), Transform.At(new Double3(1, 0, 0)));
        Assert.True(World.IsOverridden(Child(lamp, "Base"), "Transform", "Position"));
    }

    [Fact]
    public void ASetBackFieldIsNoLongerListed()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Health Max=\"40\"/></Override>");
        Entity bulb = Child(lamp, "Bulb");
        Health health = World.Get<Health>(bulb);
        health.Max = 9;
        World.Set(bulb, health);

        Assert.Empty(World.PrefabOverrides(lamp));
    }

    [Fact]
    public void WhatTheInstanceKeepsAsideIsListedAsUnapplied()
    {
        Entity lamp = LoadLamp("<Override entity=\"7\"><Health Max=\"40\"/></Override>");

        PrefabOverride kept = Assert.Single(World.PrefabOverrides(lamp));
        Assert.Equal(PrefabOverrideKind.Unapplied, kept.Kind);
        Assert.Equal("7", kept.Target);
    }

    [Fact]
    public void ListingAnEntityThatIsNotAnInstanceRootIsAnError()
    {
        LoadLamp();

        Assert.Throws<ArgumentException>(() => World.PrefabOverrides(World.Lookup("Player")));
        Assert.Throws<ArgumentException>(() => World.PrefabOverrides(Child(World.Lookup("Lamp"), "Bulb")));
    }

    // ---- Reverting

    [Fact]
    public void RevertingAFieldGivesThePrefabsValueAndKeepsTheEntity()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Health Current=\"1\" Max=\"40\"/></Override>");
        Entity bulb = Child(lamp, "Bulb");
        PrefabOverride max = World.PrefabOverrides(lamp).Single(o => o.Field == "Max");

        World.RevertOverride(lamp, max);

        Assert.Equal(9, World.Get<Health>(bulb).Max);
        Assert.Equal(1, World.Get<Health>(bulb).Current);                 // the other override stays
        Assert.Equal(["Field 2|Health|Current"], Listed(World, lamp));
        Assert.Equal(bulb, Child(lamp, "Bulb"));
    }

    [Fact]
    public void RevertingANameGivesTheNameTheFileHas()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\" name=\"Globe\"/>");
        Entity bulb = Child(lamp, "Globe");

        World.RevertOverride(lamp, World.PrefabOverrides(lamp).Single());

        Assert.Equal("Bulb", World.GetName(bulb));
        Assert.Empty(World.PrefabOverrides(lamp));
    }

    [Fact]
    public void RevertingAComponentAddedOrRemovedPutsItBackAsTheFileHasIt()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Frozen append=\"true\"/><Flicker remove=\"true\"/></Override>");
        Entity bulb = Child(lamp, "Bulb");
        Assert.True(World.Has<Frozen>(bulb));
        Assert.False(World.Has(bulb, World.FindComponent("Flicker")));

        World.RevertAllOverrides(lamp);

        Assert.False(World.Has<Frozen>(bulb));
        Assert.True(World.Has(bulb, World.FindComponent("Flicker")));
        Assert.Equal(lamp.Id, EntityField(World, bulb, "Flicker", "lamp"));      // the prefab's own reference, to the lamp's root
        Assert.Empty(World.PrefabOverrides(lamp));
    }

    [Fact]
    public void RevertingAnAddedEntityDestroysItWithWhatIsBelow()
    {
        Entity lamp = LoadLamp("<Entity id=\"9\" name=\"Cord\"><Entity id=\"10\" name=\"Plug\"/></Entity>");
        Entity cord = Child(lamp, "Cord");

        World.RevertOverride(lamp, World.PrefabOverrides(lamp).Single());

        Assert.False(World.IsAlive(cord));
        Assert.Equal(["Bulb", "Base"], NamesBelow(World, lamp));
        Assert.Empty(World.PrefabOverrides(lamp));
    }

    [Fact]
    public void WhatNamedARevertedEntityNamesNothingAfterIt()
    {
        Entity lamp = LoadLamp("<Entity id=\"9\" name=\"Cord\"/>");
        Entity cord = Child(lamp, "Cord");
        Entity watcher = World.CreateEntity("Watcher");
        World.Set(watcher, new Marks { Target = cord });

        World.RevertAllOverrides(lamp);

        Assert.False(World.IsAlive(cord));
        Assert.True(World.Get<Marks>(watcher).Target.IsNull);
    }

    [Fact]
    public void RevertingARemovedEntityMakesItAgain()
    {
        Entity lamp = LoadLamp("<Override entity=\"3\" remove=\"true\"/>");
        Assert.Equal(["Bulb"], NamesBelow(World, lamp));
        Entity bulb = Child(lamp, "Bulb");

        World.RevertOverride(lamp, World.PrefabOverrides(lamp).Single());

        Assert.Equal(["Bulb", "Base"], NamesBelow(World, lamp));
        Assert.Equal(bulb, Child(lamp, "Bulb"));
        Assert.True(World.IsPrefabMember(Child(lamp, "Base")));
        Assert.Equal(3u, World.Get<PrefabMember>(Child(lamp, "Base")).Id);
    }

    [Fact]
    public void RevertingAComponentOrAnEntityTakesOnlyItsOwn()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\" name=\"Globe\"><Health Max=\"40\"/><Marks Level=\"7\"/></Override><Override entity=\"3\"><Transform Position=\"1 0 0\"/></Override>");
        Entity bulb = Child(lamp, "Globe");

        World.RevertComponentOverrides(lamp, bulb, "Health");
        Assert.Equal(["Name 2||", "Field 2|Marks|Level", "Field 3|Transform|Position"], Listed(World, lamp));

        World.RevertEntityOverrides(lamp, bulb);
        Assert.Equal(["Field 3|Transform|Position"], Listed(World, lamp));
        Assert.Equal("Bulb", World.GetName(bulb));
    }

    [Fact]
    public void RevertingAllMakesTheInstanceWhatItsPrefabMakes()
    {
        Entity lamp = LoadLamp("""
            <Transform Position="5 0 0"/>
            <Override entity="2" name="Globe"><Health Max="40"/><Frozen append="true"/><Flicker remove="true"/></Override>
            <Override entity="3" remove="true"/>
            <Entity id="9" name="Cord"/>
            """);
        Entity bulb = Child(lamp, "Globe");

        World.RevertAllOverrides(lamp);

        // Where the instance is placed is not reverted by "all": it stays where it is.
        Assert.Equal(new Double3(5, 0, 0), World.Get<Transform>(lamp).Position);
        Assert.Equal(["Field |Transform|Position"], Listed(World, lamp));
        World.RevertOverride(lamp, World.PrefabOverrides(lamp).Single());

        using World fresh = OtherWorld();
        Entity expected = fresh.Instantiate(LampPath);
        fresh.SetName(expected, "Lamp");
        Assert.Equal(PrefabFuzzBase.DumpTree(fresh, expected), PrefabFuzzBase.DumpTree(World, lamp));
        Assert.True(World.IsAlive(bulb));
        Assert.DoesNotContain("<Override", World.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void RevertingWhatIsKeptAsideDropsItAndRevertAllLeavesItAlone()
    {
        Entity lamp = LoadLamp("<Override entity=\"7\"><Health Max=\"40\"/></Override><Override entity=\"2\"><Health Max=\"41\"/></Override>");

        World.RevertAllOverrides(lamp);
        Assert.Equal(["Unapplied 7||"], Listed(World, lamp));

        World.RevertOverride(lamp, World.PrefabOverrides(lamp).Single());
        Assert.Empty(World.PrefabOverrides(lamp));
        Assert.DoesNotContain("<Override", World.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void DroppingTheKeptAsideLeavesTheOverrides()
    {
        Entity lamp = LoadLamp("<Override entity=\"7\"><Health Max=\"40\"/></Override><Override entity=\"2\"><Health Max=\"41\"/></Override>");

        World.DropUnappliedOverrides(lamp);

        Assert.Equal(["Field 2|Health|Max"], Listed(World, lamp));
    }

    [Fact]
    public void RevertingSomethingThatIsNotAChangeAnyMoreIsRefused()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Health Max=\"40\"/></Override>");
        PrefabOverride max = World.PrefabOverrides(lamp).Single();
        World.RevertOverride(lamp, max);

        Assert.Throws<ArgumentException>(() => World.RevertOverride(lamp, max));
    }

    [Fact]
    public void AnInstanceInsideAnotherIsRevertedThroughTheOuterOne()
    {
        World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Corner" prefab="{Corner}">
                <Override entity="2/2"><Health Max="77"/></Override>
              </Entity>
            </Scene>
            """);
        Entity corner = World.Lookup("Corner");
        Entity nested = Child(corner, "Lamp");
        Entity bulb = Child(nested, "Bulb");
        Assert.Equal(77, World.Get<Health>(bulb).Max);

        Assert.Throws<InvalidOperationException>(() => World.RevertAllOverrides(nested));

        PrefabOverride max = World.PrefabOverrides(corner).Single();
        Assert.Equal("2/2", max.Target);
        Assert.Equal(bulb, max.Entity);
        World.RevertOverride(corner, max);

        Assert.Equal(9, World.Get<Health>(bulb).Max);
        Assert.Equal(9, World.Get<Health>(bulb).Current);       // the corner's own override of the lamp stays
        Assert.Empty(World.PrefabOverrides(corner));
    }

    [Fact]
    public void ARevertThatCannotBeMadeLeavesTheInstanceAsItWas()
    {
        Entity lamp = LoadLamp("<Override entity=\"3\" remove=\"true\"/><Override entity=\"2\"><Health Max=\"40\"/></Override><Entity id=\"9\" name=\"Cord\"/>");
        string before = PrefabFuzzBase.DumpTree(World, lamp);
        string saved = World.SaveScene();
        World.MaxLoadEntities = 3;     // the lamp is three entities: with Base made again and the Cord, the update would be over the budget

        Assert.Throws<InvalidOperationException>(() => World.RevertOverride(lamp, World.PrefabOverrides(lamp).Single(o => o.Kind == PrefabOverrideKind.RemovedEntity)));

        World.MaxLoadEntities = 262144;
        Assert.Equal(before, PrefabFuzzBase.DumpTree(World, lamp));
        Assert.Equal(saved, World.SaveScene());
    }

    // ---- Applying

    private const string LampFile = "prefabs/lamp.aprefab";
    private const string CornerFile = "prefabs/corner.aprefab";

    private Entity LoadCorner(string body = "")
    {
        World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Player"><Transform Position="1 2 3" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
              <Entity id="2" name="Corner" prefab="{Corner}">{body}</Entity>
            </Scene>
            """);
        return World.Lookup("Corner");
    }

    private XmlNode LampDocument() => XmlNode.Parse(Read(LampFile));

    private static XmlNode EntityElement(XmlNode document, int id)
    {
        XmlNode? Find(XmlNode node) =>
            node.Name == "Entity" && node.Attribute("id") == id.ToString(System.Globalization.CultureInfo.InvariantCulture) ? node : node.Children.Select(Find).FirstOrDefault(found => found is not null);

        return Find(document) ?? throw new InvalidOperationException($"no entity {id}");
    }

    [Fact]
    public void ApplyingAFieldWritesItToTheFileAndTheOtherInstancesFollow()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Health Max=\"40\"/></Override>");
        Entity other = World.Lookup("Lamp 2");
        Entity bulb = Child(lamp, "Bulb");
        string uid = LampDocument().Attribute("uid")!;

        PrefabUpdate? update = World.ApplyOverride(lamp, World.PrefabOverrides(lamp).Single());

        XmlNode document = LampDocument();
        Assert.Equal("40", EntityElement(document, 2).Child("Health")!.Attribute("Max"));
        Assert.Equal("5", EntityElement(document, 2).Child("Health")!.Attribute("Current"));
        Assert.Equal(uid, document.Attribute("uid"));
        Assert.Equal("4", document.Attribute("nextId"));
        Assert.Equal(40, World.Get<Health>(Child(other, "Bulb")).Max);          // the other instance follows
        Assert.Equal(bulb, Child(lamp, "Bulb"));                                // and the entity is the same
        Assert.Equal(40, World.Get<Health>(bulb).Max);
        Assert.Empty(World.PrefabOverrides(lamp));
        Assert.DoesNotContain("<Override", World.SaveScene(), StringComparison.Ordinal);
        Assert.Contains(lamp, update!.Updated);
        Assert.Contains(other, update.Updated);
        Assert.False(World.HasPendingPrefabUpdates);
    }

    [Fact]
    public void TheFileIsWrittenAsAPrefabThatLoadsInAnotherWorld()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\" name=\"Globe\"><Health Max=\"40\"/></Override><Entity id=\"9\" name=\"Cord\"/>");

        World.ApplyAllOverrides(lamp);

        using World another = OtherWorld();
        Entity made = another.Instantiate(LampPath);
        Assert.Equal(["Globe", "Base", "Cord"], NamesBelow(another, made));
        Assert.Equal(40, another.Get<Health>(Child(another, made, "Globe")).Max);
    }

    [Fact]
    public void ApplyingANameRenamesTheEntityInTheFile()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\" name=\"Globe\"/>");

        World.ApplyAllOverrides(lamp);

        Assert.Equal("Globe", EntityElement(LampDocument(), 2).Attribute("name"));
        Assert.Equal(["Globe", "Base"], NamesBelow(World, World.Lookup("Lamp 2")));
        Assert.Empty(World.PrefabOverrides(lamp));
    }

    [Fact]
    public void ApplyingAComponentAddedOrRemovedChangesTheFile()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Frozen append=\"true\"/><Flicker remove=\"true\"/></Override><Override entity=\"3\"><Marks Level=\"4\" append=\"true\"/></Override>");

        World.ApplyAllOverrides(lamp);

        XmlNode bulb = EntityElement(LampDocument(), 2);
        Assert.NotNull(bulb.Child("Frozen"));
        Assert.Null(bulb.Child("Flicker"));
        Assert.Equal("4", EntityElement(LampDocument(), 3).Child("Marks")!.Attribute("Level"));
        Entity otherBulb = Child(World.Lookup("Lamp 2"), "Bulb");
        Assert.True(World.Has<Frozen>(otherBulb));
        Assert.False(World.Has(otherBulb, World.FindComponent("Flicker")));
        Assert.Empty(World.PrefabOverrides(lamp));
    }

    [Fact]
    public void ApplyingAScriptsComponentDefinesItInTheFile()
    {
        Entity glow = World.DefineComponent("Glow", [("power", FieldKind.Float32), ("who", FieldKind.Entity)]);
        Entity lamp = LoadLamp();
        Entity bulb = Child(lamp, "Bulb");
        World.Add(bulb, glow);

        World.ApplyAllOverrides(lamp);

        XmlNode document = LampDocument();
        Assert.Contains(document.Child("Components")!.Children, c => c.Attribute("name") == "Glow");
        Assert.NotNull(EntityElement(document, 2).Child("Glow"));
        Assert.True(World.Has(Child(World.Lookup("Lamp 2"), "Bulb"), glow));
    }

    [Fact]
    public void ApplyingAnAddedEntityGivesItAStableIdInTheFile()
    {
        Entity lamp = LoadLamp("<Entity id=\"9\" name=\"Cord\"><Marks Target=\"2/3\"/><Entity id=\"10\" name=\"Plug\"/></Entity>");
        Entity cord = Child(lamp, "Cord");
        Entity plug = Child(cord, "Plug");
        Assert.Equal(Child(lamp, "Base"), World.Get<Marks>(cord).Target);

        World.ApplyAllOverrides(lamp);

        XmlNode document = LampDocument();
        XmlNode written = EntityElement(document, 4);
        Assert.Equal("Cord", written.Attribute("name"));
        Assert.Equal("3", written.Child("Marks")!.Attribute("Target"));       // the Base of the file
        Assert.Equal("Plug", EntityElement(document, 5).Attribute("name"));
        Assert.Equal("6", document.Attribute("nextId"));
        Assert.Equal(cord, Child(lamp, "Cord"));
        Assert.Equal(plug, Child(cord, "Plug"));
        Assert.Equal(4u, World.Get<PrefabMember>(cord).Id);
        Assert.Equal(5u, World.Get<PrefabMember>(plug).Id);
        Assert.Equal(Child(lamp, "Base"), World.Get<Marks>(cord).Target);
        Entity otherCord = Child(World.Lookup("Lamp 2"), "Cord");
        Assert.Equal(Child(World.Lookup("Lamp 2"), "Base"), World.Get<Marks>(otherCord).Target);     // another instance's own Base
        Assert.Empty(World.PrefabOverrides(lamp));
        Assert.Equal(["Bulb", "Base", "Cord"], NamesBelow(World, lamp));
        Assert.DoesNotContain("<Override", World.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntityAddedBelowAMemberKeepsItsIdWhenApplied()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Entity id=\"9\" name=\"Cord\"><Transform Position=\"1 0 0\"/></Entity></Override>");
        Entity cord = Child(Child(lamp, "Bulb"), "Cord");

        World.ApplyAllOverrides(lamp);

        Assert.Equal(["Cord"], NamesBelow(World, Child(lamp, "Bulb")));
        Assert.Equal(cord, Child(Child(lamp, "Bulb"), "Cord"));
        Assert.Equal(4u, World.Get<PrefabMember>(cord).Id);
        Assert.Equal(cord, Child(Child(lamp, "Bulb"), "Cord"));
        XmlNode bulb = EntityElement(LampDocument(), 2);
        Assert.Equal("Cord", bulb.Children.Single(c => c.Name == "Entity").Attribute("name"));
        Assert.Empty(World.PrefabOverrides(lamp));
    }

    [Fact]
    public void AnEntityRemovedFromTheInstanceIsRemovedFromThePrefabForEveryone()
    {
        Entity lamp = LoadLamp("<Override entity=\"3\" remove=\"true\"/>");

        World.ApplyAllOverrides(lamp);

        Assert.DoesNotContain("name=\"Base\"", Read(LampFile), StringComparison.Ordinal);
        Assert.Equal(["Bulb"], NamesBelow(World, World.Lookup("Lamp 2")));
        Assert.Empty(World.PrefabOverrides(lamp));
        string scene = World.SaveScene();
        Assert.DoesNotContain("<Override", scene, StringComparison.Ordinal);      // not kept aside either
    }

    [Fact]
    public void ApplyingToAReadOnlyFileWritesNothing()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Health Max=\"40\"/></Override>");
        string path = System.IO.Path.Combine(Folder, LampFile);
        string before = Read(LampFile);
        var info = new System.IO.FileInfo(path) { IsReadOnly = true };
        try
        {
            if (!new System.IO.FileInfo(path).IsReadOnly)
            {
                return; // this system cannot make a file read-only for this user
            }

            Assert.Throws<UnauthorizedAccessException>(() => World.ApplyAllOverrides(lamp));

            Assert.Equal(before, Read(LampFile));
            Assert.Equal(40, World.Get<Health>(Child(lamp, "Bulb")).Max);
            Assert.Single(World.PrefabOverrides(lamp));
            Assert.False(World.HasPendingPrefabUpdates);
        }
        finally
        {
            info.IsReadOnly = false;
        }
    }

    [Fact]
    public void ApplyingToAPrefabInASourceThatCannotBeWrittenWritesNothing()
    {
        using World reader = new() { Files = ReadOnlyFiles(), Assets = null };
        reader.LoadScene(LampScene("<Override entity=\"2\"><Health Max=\"40\"/></Override>"));
        Entity lamp = reader.Lookup("Lamp");
        string before = Read(LampFile);

        Assert.Throws<UnauthorizedAccessException>(() => reader.ApplyAllOverrides(lamp));

        Assert.Equal(before, Read(LampFile));
        Assert.Single(reader.PrefabOverrides(lamp));
    }

    private Apogee.Content.FileSystem.VirtualFileSystem ReadOnlyFiles()
    {
        var files = new Apogee.Content.FileSystem.VirtualFileSystem();
        files.Mount("res://", new Apogee.Content.FileSystem.DirectorySource(Folder, writable: false));
        return files;
    }

    [Fact]
    public void ApplyingToAFileThatChangedOnTheDiskIsRefused()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Health Max=\"40\"/></Override>");
        string edited = Read(LampFile).Replace("Regen=\"0.5\"", "Regen=\"0.75\"", StringComparison.Ordinal);
        Write(LampFile, edited);

        Assert.Throws<InvalidOperationException>(() => World.ApplyAllOverrides(lamp));

        Assert.Equal(edited, Read(LampFile));
        Assert.Equal(40, World.Get<Health>(Child(lamp, "Bulb")).Max);
        Assert.Single(World.PrefabOverrides(lamp));
        Assert.False(World.HasPendingPrefabUpdates);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("broken")]
    public void ApplyingWhenThePrefabFileIsMissingOrBrokenIsRefused(string what)
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Health Max=\"40\"/></Override>");
        if (what == "missing")
        {
            Delete(LampFile);
        }
        else
        {
            Write(LampFile, "<Prefab><Entity");
        }

        Assert.Throws<InvalidOperationException>(() => World.ApplyAllOverrides(lamp));

        Assert.Equal(what == "missing", !System.IO.File.Exists(System.IO.Path.Combine(Folder, LampFile)));
        Assert.Equal(40, World.Get<Health>(Child(lamp, "Bulb")).Max);
        Assert.False(World.HasPendingPrefabUpdates);
    }

    [Fact]
    public void ApplyingAnInstanceWhosePrefabWasNeverThereIsRefused()
    {
        World.LoadScene("<Scene version=\"2\"><Entity id=\"1\" name=\"Ghost\" prefab=\"res://prefabs/nothing.aprefab\"><Frozen append=\"true\"/></Entity></Scene>");
        Entity ghost = World.Lookup("Ghost");

        Assert.Throws<InvalidOperationException>(() => World.ApplyAllOverrides(ghost));
    }

    [Fact]
    public void WhatIsKeptAsideCannotBeApplied()
    {
        Entity lamp = LoadLamp("<Override entity=\"7\"><Health Max=\"40\"/></Override>");

        Assert.Throws<ArgumentException>(() => World.ApplyOverride(lamp, World.PrefabOverrides(lamp).Single()));
        Assert.Null(World.ApplyAllOverrides(lamp));     // nothing else to apply
    }

    [Fact]
    public void ApplyingWhileAnUpdateWaitsIsRefused()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Health Max=\"40\"/></Override>");
        Write(LampFile, Read(LampFile).Replace("Regen=\"0.5\"", "Regen=\"0.75\"", StringComparison.Ordinal));
        Assert.Equal(PrefabReloadStatus.Reloaded, World.Prefabs.Reload(LampPath).Status);

        Assert.Throws<InvalidOperationException>(() => World.ApplyAllOverrides(lamp));
        World.ApplyPrefabUpdates();

        Assert.NotNull(World.ApplyAllOverrides(lamp));
        Assert.Equal("40", EntityElement(LampDocument(), 2).Child("Health")!.Attribute("Max"));
        Assert.Equal("0.75", EntityElement(LampDocument(), 2).Child("Health")!.Attribute("Regen"));
    }

    [Fact]
    public void AFieldNamingAnEntityOutsideTheInstanceCannotBeApplied()
    {
        Entity lamp = LoadLamp();
        Entity bulb = Child(lamp, "Bulb");
        World.Set(bulb, new Marks { Level = 2, Target = World.Lookup("Player"), Alive = true, Mass = 1.5 });
        string before = Read(LampFile);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => World.ApplyAllOverrides(lamp));

        Assert.Contains("Marks.Target", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Read(LampFile));
        Assert.Equal(World.Lookup("Player"), World.Get<Marks>(bulb).Target);
        Assert.Single(World.PrefabOverrides(lamp));
    }

    [Fact]
    public void AFieldNamingAnotherEntityOfTheInstanceIsWrittenWithTheFilesId()
    {
        Entity lamp = LoadLamp();
        Entity bulb = Child(lamp, "Bulb");
        World.Set(bulb, new Marks { Level = 2, Target = Child(lamp, "Base"), Alive = true, Mass = 1.5 });

        World.ApplyAllOverrides(lamp);

        Assert.Equal("3", EntityElement(LampDocument(), 2).Child("Marks")!.Attribute("Target"));
        Entity otherBulb = Child(World.Lookup("Lamp 2"), "Bulb");
        Assert.Equal(Child(World.Lookup("Lamp 2"), "Base"), World.Get<Marks>(otherBulb).Target);
    }

    [Theory]
    [InlineData("corner")]
    [InlineData("lamp")]
    public void AnAddedEntityThatHoldsAnInstanceOfThePrefabItselfCannotBeApplied(string which)
    {
        // The corner holds lamps: a corner added to a corner would be a prefab that contains itself, and a corner added to a lamp too (the corner holds the lamp).
        Entity corner = LoadCorner();
        Entity target = which == "corner" ? corner : Child(corner, "Lamp");
        World.Instantiate(CornerPath, target);
        string files = Read(CornerFile) + Read(LampFile);
        string dump = PrefabFuzzBase.DumpTree(World, corner);

        Assert.Throws<InvalidOperationException>(() => World.ApplyAllOverrides(target));

        Assert.Equal(files, Read(CornerFile) + Read(LampFile));
        Assert.Equal(dump, PrefabFuzzBase.DumpTree(World, corner));
        Assert.False(World.HasPendingPrefabUpdates);
    }

    [Fact]
    public void ACornerAddedToALampCannotBeAppliedToTheLamp()
    {
        Entity lamp = LoadLamp();
        World.Instantiate(CornerPath, lamp);
        string files = Read(CornerFile) + Read(LampFile);

        Assert.Throws<InvalidOperationException>(() => World.ApplyAllOverrides(lamp));

        Assert.Equal(files, Read(CornerFile) + Read(LampFile));
        Assert.False(World.HasPendingPrefabUpdates);
    }

    [Fact]
    public void AnAddedEntityWhoseFieldsNameSomethingOutsideCannotBeApplied()
    {
        Entity lamp = LoadLamp("<Entity id=\"9\" name=\"Cord\"><Marks Target=\"1\"/></Entity>");
        Entity cord = Child(lamp, "Cord");
        World.Set(cord, new Marks { Target = World.Lookup("Player") });
        string before = Read(LampFile);

        Assert.Throws<InvalidOperationException>(() => World.ApplyAllOverrides(lamp));

        Assert.Equal(before, Read(LampFile));
        Assert.True(World.IsAlive(cord));
        Assert.Equal(["Bulb", "Base", "Cord"], NamesBelow(World, lamp));
    }

    [Fact]
    public void TheRootsOwnChangesAreWrittenIntoTheFilesRoot()
    {
        Entity lamp = LoadLamp("<Transform Position=\"5 0 0\"/><Marks Level=\"3\" append=\"true\"/>");

        World.ApplyAllOverrides(lamp);

        // Where the instance is placed is the scene's: "all" leaves it out, and so the other lamps stay where they are.
        XmlNode root = EntityElement(LampDocument(), 1);
        Assert.Equal("0 0 0", root.Child("Transform")!.Attribute("Position"));
        Assert.Equal("3", root.Child("Marks")!.Attribute("Level"));
        Assert.Equal(new Double3(0, 0, 0), World.Get<Transform>(World.Lookup("Lamp 2")).Position);
        Assert.Equal(new Double3(5, 0, 0), World.Get<Transform>(lamp).Position);
        Assert.Equal(["Field |Transform|Position"], Listed(World, lamp));

        // One override at a time may still make it the prefab's.
        World.ApplyOverride(lamp, World.PrefabOverrides(lamp).Single());

        Assert.Equal("5 0 0", EntityElement(LampDocument(), 1).Child("Transform")!.Attribute("Position"));
        Assert.Equal(new Double3(5, 0, 0), World.Get<Transform>(World.Lookup("Lamp 2")).Position);
        Assert.Empty(World.PrefabOverrides(lamp));
    }

    [Fact]
    public void TheRotationOfTheRootIsPlacementToo()
    {
        Entity lamp = LoadLamp("<Transform Rotation=\"0 1 0 0\" Scale=\"2 2 2\"/>");

        World.ApplyAllOverrides(lamp);

        Assert.Equal("2 2 2", EntityElement(LampDocument(), 1).Child("Transform")!.Attribute("Scale"));
        Assert.Equal("0 0 0 1", EntityElement(LampDocument(), 1).Child("Transform")!.Attribute("Rotation"));
        Assert.Equal(["Field |Transform|Rotation"], Listed(World, lamp));
    }

    [Fact]
    public void TwoInstancesWithDifferentOverridesKeepWhatWasNotApplied()
    {
        World.LoadScene($"""
            <Scene version="2">
              <Entity id="1" name="Lamp" prefab="{Lamp}"><Override entity="2"><Health Max="40"/></Override></Entity>
              <Entity id="2" name="Lamp 2" prefab="{Lamp}"><Override entity="2"><Health Max="50" Regen="2"/></Override></Entity>
            </Scene>
            """);
        Entity first = World.Lookup("Lamp");
        Entity second = World.Lookup("Lamp 2");

        World.ApplyOverride(first, World.PrefabOverrides(first).Single());

        Assert.Empty(World.PrefabOverrides(first));
        Assert.Equal(["Field 2|Health|Max", "Field 2|Health|Regen"], Listed(World, second));
        Assert.Equal(50, World.Get<Health>(Child(second, "Bulb")).Max);
        Assert.Equal(40, World.Get<Health>(Child(first, "Bulb")).Max);
        Assert.Equal("40", EntityElement(LampDocument(), 2).Child("Health")!.Attribute("Max"));
    }

    [Fact]
    public void AnOverrideInsideANestedPrefabCanBeAppliedToTheNestingPrefab()
    {
        Entity corner = LoadCorner("<Override entity=\"2/2\"><Health Max=\"77\"/></Override>");
        string lampBefore = Read(LampFile);

        World.ApplyOverride(corner, World.PrefabOverrides(corner).Single(), PrefabApplyTarget.InstancePrefab);

        Assert.Equal(lampBefore, Read(LampFile));
        XmlNode nested = EntityElement(XmlNode.Parse(Read(CornerFile)), 2);
        XmlNode change = nested.Children.Single(c => c.Name == "Override");
        Assert.Equal("2", change.Attribute("entity"));
        Assert.Equal("77", change.Child("Health")!.Attribute("Max"));
        Assert.Equal("9", change.Child("Health")!.Attribute("Current"));           // what was there stays
        Assert.Equal(9, World.Get<Health>(Child(Child(corner, "Lamp 2"), "Bulb")).Max); // the other lamp of the corner is the lamp's
        Assert.Equal(77, World.Get<Health>(Child(Child(corner, "Lamp"), "Bulb")).Max);
        Assert.Empty(World.PrefabOverrides(corner));
    }

    [Fact]
    public void AnOverrideInsideANestedPrefabCanBeAppliedToThePrefabThatDefinesIt()
    {
        Entity corner = LoadCorner("<Override entity=\"2/2\"><Health Max=\"77\"/></Override>");
        string cornerBefore = Read(CornerFile);

        World.ApplyOverride(corner, World.PrefabOverrides(corner).Single(), PrefabApplyTarget.SourcePrefab);

        Assert.Equal(cornerBefore, Read(CornerFile));
        Assert.Equal("77", EntityElement(LampDocument(), 2).Child("Health")!.Attribute("Max"));
        Assert.Equal(77, World.Get<Health>(Child(Child(corner, "Lamp 2"), "Bulb")).Max);       // every lamp has it now
        Assert.Equal(77, World.Get<Health>(Child(Child(corner, "Lamp"), "Bulb")).Max);
        Assert.Empty(World.PrefabOverrides(corner));
    }

    [Fact]
    public void AnOverrideTheNestingPrefabPinsStaysAfterApplyingToTheSource()
    {
        // The corner's own file says the first lamp's bulb has Current 9: applying a Current of 3 to the lamp's file leaves the corner's 9 in the way.
        Entity corner = LoadCorner("<Override entity=\"2/2\"><Health Current=\"3\"/></Override>");

        World.ApplyOverride(corner, World.PrefabOverrides(corner).Single(), PrefabApplyTarget.SourcePrefab);

        Assert.Equal("3", EntityElement(LampDocument(), 2).Child("Health")!.Attribute("Current"));
        Assert.Equal(3, World.Get<Health>(Child(Child(corner, "Lamp"), "Bulb")).Current);          // the live value is still what was applied
        Assert.Equal(["Field 2/2|Health|Current"], Listed(World, corner));                           // and it is still an override: the corner pins 9
    }

    [Fact]
    public void ANestedInstanceCanApplyItsOwnChangesToItsPrefab()
    {
        Entity corner = LoadCorner();
        Entity nested = Child(corner, "Lamp");
        Entity bulb = Child(nested, "Bulb");
        Marks marks = World.Get<Marks>(bulb);
        marks.Level = 7;
        World.Set(bulb, marks);

        Assert.Equal(["Field 2|Marks|Level"], Listed(World, nested));
        World.ApplyOverride(nested, World.PrefabOverrides(nested).Single());

        Assert.Equal("7", EntityElement(LampDocument(), 2).Child("Marks")!.Attribute("Level"));
        Assert.Equal(7, World.Get<Marks>(Child(Child(corner, "Lamp 2"), "Bulb")).Level);
        Assert.Equal(bulb, Child(nested, "Bulb"));
        Assert.Empty(World.PrefabOverrides(corner));
    }

    [Fact]
    public void ANestedInstanceDoesNotListWhatThePrefabItIsInSaysAsItsOwnChanges()
    {
        // The corner's file moves its first lamp, pins the Health of its bulb and removes the Base of the second lamp: the scene changed none of it.
        Entity corner = LoadCorner();
        Entity first = Child(corner, "Lamp");
        Entity second = Child(corner, "Lamp 2");

        Assert.Empty(World.PrefabOverrides(first));
        Assert.Empty(World.PrefabOverrides(second));
        Assert.False(World.IsOverridden(Child(first, "Bulb"), "Health", "Current"));
        Assert.False(World.IsOverridden(first, "Transform", "Position"));
        string lamp = Read(LampFile);

        Assert.Null(World.ApplyAllOverrides(first));
        Assert.Null(World.ApplyAllOverrides(second));

        Assert.Equal(lamp, Read(LampFile));
    }

    [Fact]
    public void ANestedInstanceListsWhatTheSceneChangedInItRelativeToIt()
    {
        Entity corner = LoadCorner("<Override entity=\"2/2\"><Health Max=\"77\"/></Override><Override entity=\"2/3\"><Frozen append=\"true\"/></Override><Override entity=\"3/2\"><Health Max=\"5\"/></Override>");
        Entity first = Child(corner, "Lamp");

        Assert.Equal(["Field 2|Health|Max", "AddedComponent 3|Frozen|"], Listed(World, first));
        Assert.Equal(["Field 2|Health|Max"], Listed(World, Child(corner, "Lamp 2")));
        Assert.Equal(Child(first, "Bulb"), World.PrefabOverrides(first)[0].Entity);
        Assert.True(World.IsOverridden(Child(first, "Bulb"), "Health", "Max"));
        Assert.False(World.IsOverridden(Child(Child(corner, "Lamp 2"), "Bulb"), "Marks"));
    }

    [Fact]
    public void TheNameOfANestedInstanceIsItsOwnAndAllLeavesItOut()
    {
        Entity corner = LoadCorner("<Override entity=\"2\" name=\"Left\"/>");
        Entity first = Child(corner, "Left");
        string lamp = Read(LampFile);

        Assert.Equal(["Name ||"], Listed(World, first));
        Assert.Null(World.ApplyAllOverrides(first));

        Assert.Equal(lamp, Read(LampFile));
        Assert.Single(World.PrefabOverrides(first));
    }

    [Fact]
    public void ANestedInstanceCanApplyAnEntityItAddedAndOneItRemoved()
    {
        Entity corner = LoadCorner();
        Entity nested = Child(corner, "Lamp");
        Entity bulb = Child(nested, "Bulb");
        Entity cord = World.CreateEntity("Cord", bulb);
        World.Destroy(Child(nested, "Base"));
        Assert.Equal(["AddedEntity 2||", "RemovedEntity 3||"], Listed(World, nested).Where(x => !x.Contains("Field", StringComparison.Ordinal)).ToArray());

        World.ApplyOverrides(nested, World.PrefabOverrides(nested).Where(o => o.Kind is PrefabOverrideKind.RemovedEntity or PrefabOverrideKind.AddedEntity), PrefabApplyTarget.InstancePrefab);

        XmlNode lamp = LampDocument();
        Assert.DoesNotContain("name=\"Base\"", Read(LampFile), StringComparison.Ordinal);
        Assert.Equal("Cord", EntityElement(lamp, 2).Children.Single(c => c.Name == "Entity").Attribute("name"));
        Assert.Equal(cord, Child(Child(Child(corner, "Lamp"), "Bulb"), "Cord"));               // the same entity, the lamp's now
        Assert.Equal(["Cord"], NamesBelow(World, Child(Child(corner, "Lamp 2"), "Bulb")));       // and the other lamp of the corner has it
        Assert.DoesNotContain(World.PrefabOverrides(corner), o => o.Kind is not PrefabOverrideKind.Field);
        string scene = World.SaveScene();
        Assert.DoesNotContain("Cord", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("remove=\"true\"", scene.Replace("<Override entity=\"3\" remove=\"true\"/>", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    // A corner whose first lamp has a changed Health (the lamp's file) and whose Bench has a Frozen (the corner's file): applying both to the files that define them writes two files.
    private Entity LoadCornerWithTwoFiles() => LoadCorner("<Override entity=\"2/2\"><Health Max=\"77\"/></Override><Override entity=\"4\"><Frozen append=\"true\"/></Override>");

    [Fact]
    public void AFailingWriteInTheSecondFileLeavesBothFilesAndTheWorldAsTheyWere()
    {
        Entity corner = LoadCornerWithTwoFiles();
        string files = Read(CornerFile) + Read(LampFile);
        string dump = Dump(World);
        int changes = 0;
        World.PrefabsChanged += _ => changes++;
        Faults.FailOn(2);

        IOException error = Assert.Throws<IOException>(() => World.ApplyAllOverrides(corner, PrefabApplyTarget.SourcePrefab));

        Assert.Contains("injected", error.Message, StringComparison.Ordinal);
        Assert.Equal(files, Read(CornerFile) + Read(LampFile));       // the first file was written, and put back
        Assert.Equal(dump, Dump(World));
        Assert.False(World.HasPendingPrefabUpdates);
        Assert.Equal(0, changes);                                    // nothing came of it: nothing to tell a tool that keeps a history
        Assert.Equal(2, World.PrefabOverrides(corner).Count);
    }

    [Fact]
    public void AFileThatCannotBePutBackIsNamedAndTheInstancesFollowIt()
    {
        Entity corner = LoadCornerWithTwoFiles();
        string lampBefore = Read(LampFile);
        Faults.FailOn(2, 3);     // the lamp's file cannot be written, and the corner's, written first, cannot be put back

        IOException error = Assert.Throws<IOException>(() => World.ApplyAllOverrides(corner, PrefabApplyTarget.SourcePrefab));

        Assert.Contains("prefabs/corner.aprefab", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("prefabs/lamp.aprefab could not", error.Message, StringComparison.Ordinal);
        Assert.Contains("<Frozen", Read(CornerFile), StringComparison.Ordinal);           // the corner holds what was applied
        Assert.Equal(lampBefore, Read(LampFile));
        Assert.False(World.HasPendingPrefabUpdates);

        // The world is what the disk says: the Bench has its Frozen from the file, so the scene holds no override of it, and the lamp's change is still the scene's.
        Assert.Equal(["Field 2/2|Health|Max"], Listed(World, corner));
        string saved = World.SaveScene();
        using World loaded = OtherWorld();
        loaded.LoadScene(saved);
        Assert.Equal(Dump(World), Dump(loaded));
        Assert.Equal(saved, loaded.SaveScene());
    }

    [Fact]
    public void ARollbackPinsNothingOnTheOtherInstancesThatCouldNotFollow()
    {
        // The lamp grows to four entities (the Cord is applied), and the budget is four. The first lamp has a Plug as well and cannot follow; the second has a Mast and cannot follow either, but could
        // be made from the old file: after the file is put back it must be exactly what it was, with no change pinned by having been compared with the new file.
        LoadLamp("<Entity id=\"9\" name=\"Cord\"/><Entity id=\"10\" name=\"Plug\"/>");
        Entity first = World.Lookup("Lamp");
        Entity second = World.Lookup("Lamp 2");
        World.CreateEntity("Mast", second);
        World.MaxLoadEntities = 4;
        string files = Read(LampFile);
        string dump = Dump(World);
        Assert.Equal(["AddedEntity ||", "AddedEntity ||"], Listed(World, first));
        Assert.Equal(["AddedEntity ||"], Listed(World, second));
        int changes = 0;
        World.PrefabsChanged += _ => changes++;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => World.ApplyOverride(first, World.PrefabOverrides(first).First(o => o.Entity == Child(first, "Cord"))));

        Assert.Contains("put back", error.Message, StringComparison.Ordinal);
        Assert.Equal(files, Read(LampFile));
        Assert.Equal(dump, Dump(World));
        Assert.Equal(["AddedEntity ||"], Listed(World, second));       // not "the new member removed" kept aside
        Assert.Equal(["AddedEntity ||", "AddedEntity ||"], Listed(World, first));
        Assert.False(World.HasPendingPrefabUpdates);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ApplyReturnsWhatTheFilesHeldAndWhetherWritingThemReformatsThem()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Health Max=\"40\"/></Override>");
        byte[] before = System.IO.File.ReadAllBytes(System.IO.Path.Combine(Folder, LampFile));
        PrefabOverride max = World.PrefabOverrides(lamp).Single();

        PrefabFileWrite planned = Assert.Single(World.PreviewApply(lamp, [max]));
        Assert.Equal(before, planned.Original.ToArray());
        Assert.True(planned.Reformatted);                                                       // the file was written by hand, in a layout of its own
        Assert.Equal(System.Text.Encoding.UTF8.GetString(before), Read(LampFile));                // and the preview wrote nothing

        PrefabUpdate? update = World.ApplyOverride(lamp, max);

        PrefabFileWrite written = Assert.Single(update!.Written);
        Assert.Equal(before, written.Original.ToArray());
        Assert.True(written.Reformatted);
        Assert.NotEqual(before, System.IO.File.ReadAllBytes(System.IO.Path.Combine(Folder, LampFile)));

        // Written by the engine now, it is in the engine's layout: the next apply changes only what it says.
        Entity bulb = Child(lamp, "Bulb");
        Health health = World.Get<Health>(bulb);
        health.Regen = 3;
        World.Set(bulb, health);
        PrefabFileWrite next = Assert.Single(World.PreviewApply(lamp, World.PrefabOverrides(lamp)));
        Assert.False(next.Reformatted);
    }

    [Fact]
    public void AnOverrideThatSaysWhatTheFileAlreadySaysWritesNothing()
    {
        // The corner pins the first lamp's Current at 9; the lamp's file says 5, and the scene sets 5: a change from the corner, and nothing to write into the lamp.
        Entity corner = LoadCorner("<Override entity=\"2/2\"><Health Current=\"5\"/></Override>");
        string lamp = Read(LampFile);
        PrefabOverride current = World.PrefabOverrides(corner).Single();

        Assert.Empty(World.PreviewApply(corner, [current], PrefabApplyTarget.SourcePrefab));
        Assert.Null(World.ApplyOverride(corner, current, PrefabApplyTarget.SourcePrefab));

        Assert.Equal(lamp, Read(LampFile));
        Assert.Single(World.PrefabOverrides(corner));
    }

    [Fact]
    public void ApplyingToALinkOfAReadOnlyFileIsRefused()
    {
        string real = System.IO.Path.Combine(Folder, "prefabs/real_lamp.aprefab");
        string link = System.IO.Path.Combine(Folder, LampFile);
        System.IO.File.Move(link, real);
        try
        {
            System.IO.File.CreateSymbolicLink(link, real);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            System.IO.File.Move(real, link);
            return; // this system does not let this user make a link
        }

        var target = new System.IO.FileInfo(real) { IsReadOnly = true };
        try
        {
            if (!new System.IO.FileInfo(real).IsReadOnly)
            {
                return; // nor make a file read-only
            }

            Entity lamp = LoadLamp("<Override entity=\"2\"><Health Max=\"40\"/></Override>");
            string before = System.IO.File.ReadAllText(real);

            Assert.Throws<UnauthorizedAccessException>(() => World.ApplyAllOverrides(lamp));

            Assert.Equal(before, System.IO.File.ReadAllText(real));
            Assert.True(new System.IO.FileInfo(link).LinkTarget is not null);        // and the link is still a link
        }
        finally
        {
            target.IsReadOnly = false;
        }
    }

    [Fact]
    public void RevertingAllOnAnInstanceWhosePrefabIsMissingIsRefused()
    {
        World.LoadScene("<Scene version=\"2\"><Entity id=\"1\" name=\"Ghost\" prefab=\"res://prefabs/nothing.aprefab\"><Frozen append=\"true\"/><Override entity=\"2\"><Health Max=\"1\"/></Override></Entity></Scene>");
        Entity ghost = World.Lookup("Ghost");
        Entity child = World.CreateEntity("Child", ghost);
        World.Add<Frozen>(ghost);

        Assert.Throws<InvalidOperationException>(() => World.RevertAllOverrides(ghost));

        Assert.True(World.Has<Frozen>(ghost));
        Assert.True(World.IsAlive(child));
        World.DropUnappliedOverrides(ghost);          // what is kept aside can still be let go of
        Assert.DoesNotContain("Health", World.SaveScene(), StringComparison.Ordinal);
    }

    private static string Dump(World world) => string.Join("\n", world.Roots().Select(root => PrefabFuzzBase.DumpTree(world, root)).Order(StringComparer.Ordinal));

    [Fact]
    public void AnInstanceThatCannotFollowTheFileLeavesBothAsTheyWere()
    {
        Entity lamp = LoadLamp("<Override entity=\"2\"><Health Max=\"40\"/></Override><Entity id=\"9\" name=\"Cord\"/>");
        string before = Read(LampFile);
        string dump = PrefabFuzzBase.DumpTree(World, lamp);
        World.MaxLoadEntities = 3;     // the lamp is three entities; with the Cord the instance is four: it cannot be planned

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => World.ApplyOverride(lamp, World.PrefabOverrides(lamp).Single(o => o.Kind == PrefabOverrideKind.Field)));

        Assert.Contains("put back", error.Message, StringComparison.Ordinal);
        World.MaxLoadEntities = 262144;
        Assert.Equal(before, Read(LampFile));
        Assert.Equal(dump, PrefabFuzzBase.DumpTree(World, lamp));
        Assert.Equal(2, World.PrefabOverrides(lamp).Count);
        Assert.False(World.HasPendingPrefabUpdates);
        Assert.Equal(["Bulb", "Base"], NamesBelow(World, World.Lookup("Lamp 2")));
    }

    // ---- Breaking the link

    private bool HasAnyLink(Entity root) => World.Subtree(root).Any(e => World.IsPrefabInstance(e) || World.Has<PrefabMember>(e));

    [Fact]
    public void BreakingTheLinkKeepsWhatIsSeenAndTheEntities()
    {
        Entity lamp = LoadLamp("<Transform Position=\"5 0 0\"/><Override entity=\"2\" name=\"Globe\"><Health Max=\"40\"/></Override><Entity id=\"9\" name=\"Cord\"/>");
        List<Entity> before = World.Subtree(lamp);
        string dump = PrefabFuzzBase.DumpTree(World, lamp).Replace(" [instance]", "", StringComparison.Ordinal);

        PrefabLinkReport report = World.BreakPrefabLink(lamp);

        Assert.Equal([lamp], report.Unlinked);
        Assert.Empty(report.Dropped);
        Assert.Equal(before, World.Subtree(lamp));
        Assert.Equal(dump, PrefabFuzzBase.DumpTree(World, lamp));
        Assert.False(HasAnyLink(lamp));
        Assert.True(World.IsPrefabInstance(World.Lookup("Lamp 2")));
        Assert.Throws<ArgumentException>(() => World.PrefabOverrides(lamp));
        string scene = World.SaveScene();
        Assert.Contains("name=\"Globe\"", scene, StringComparison.Ordinal);
        Assert.Single(scene.Split('\n'), line => line.Contains("prefab=", StringComparison.Ordinal));      // only Lamp 2 is still an instance
    }

    [Fact]
    public void BreakingTheLinkStopsTheInstanceFollowingItsPrefab()
    {
        Entity lamp = LoadLamp();
        World.BreakPrefabLink(lamp);

        Write(LampFile, Read(LampFile).Replace("Max=\"9\"", "Max=\"90\"", StringComparison.Ordinal));
        World.Prefabs.Reload(LampPath);
        World.ApplyPrefabUpdates();

        Assert.Equal(9, World.Get<Health>(Child(lamp, "Bulb")).Max);
        Assert.Equal(90, World.Get<Health>(Child(World.Lookup("Lamp 2"), "Bulb")).Max);
    }

    [Fact]
    public void BreakingTheLinkDropsWhatWasKeptAsideAndSaysSo()
    {
        Entity lamp = LoadLamp("<Override entity=\"7\"><Health Max=\"40\"/></Override>");

        PrefabLinkReport report = World.BreakPrefabLink(lamp);

        XmlNode dropped = Assert.Single(report.Dropped);
        Assert.Equal("7", dropped.Attribute("entity"));
        Assert.DoesNotContain("<Override", World.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void BreakingTheLinkLeavesNestedInstancesInstancesUnlessCompletely()
    {
        Entity corner = LoadCorner();
        Entity nested = Child(corner, "Lamp");
        Entity bulb = Child(nested, "Bulb");

        PrefabLinkReport report = World.BreakPrefabLink(corner);

        Assert.Equal([corner], report.Unlinked);
        Assert.False(World.IsPrefabInstance(corner));
        Assert.False(World.Has<PrefabMember>(corner));
        Assert.True(World.IsPrefabInstance(nested));
        Assert.True(World.IsPrefabInstance(Child(corner, "Lamp 2")));
        Assert.True(World.IsPrefabMember(bulb));
        Assert.False(World.Has<PrefabMember>(Child(corner, "Bench")));
        Assert.Equal(["Field 2|Health|Current"], Listed(World, nested).Where(x => x.Contains("Health", StringComparison.Ordinal)).ToArray()); // the corner's own change is the scene's now
        string saved = World.SaveScene();
        Assert.Contains("prefab=\"" + LampPath, saved, StringComparison.Ordinal);
        using World loaded = OtherWorld();
        loaded.LoadScene(saved);
        Assert.Equal(PrefabFuzzBase.DumpTree(World, corner), PrefabFuzzBase.DumpTree(loaded, loaded.Lookup("Corner")));
        Assert.Equal(saved, loaded.SaveScene());
    }

    [Fact]
    public void WhatWasKeptAsideForAPlaceInAnInstanceThatStaysIsKeptByIt()
    {
        // The lamp has no entity 7 and no Frozen: the corner keeps both aside. Unpacking the corner leaves the first lamp an instance, which is where they belong; the one for entity 9 belongs to nothing.
        Entity corner = LoadCorner("<Override entity=\"2/7\"><Health Max=\"40\"/></Override><Override entity=\"2\"><Frozen remove=\"true\"/></Override><Override entity=\"9\"><Health Max=\"1\"/></Override>");
        Entity nested = Child(corner, "Lamp");

        PrefabLinkReport report = World.BreakPrefabLink(corner);

        XmlNode dropped = Assert.Single(report.Dropped);
        Assert.Equal("9", dropped.Attribute("entity"));
        Assert.Equal(["Unapplied 7||", "Unapplied |Frozen|"], Listed(World, nested).Where(x => x.StartsWith("Unapplied", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray());
        string saved = World.SaveScene();
        Assert.Contains("<Override entity=\"7\">", saved, StringComparison.Ordinal);
        Assert.Contains("<Frozen remove=\"true\"/>", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("entity=\"2/7\"", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("entity=\"9\"", saved, StringComparison.Ordinal);
        using World loaded = OtherWorld();
        loaded.LoadScene(saved);
        Assert.Equal(saved, loaded.SaveScene());
    }

    [Fact]
    public void BreakingTheLinkCompletelyUnpacksTheNestedInstances()
    {
        Entity corner = LoadCorner();
        string dump = PrefabFuzzBase.DumpTree(World, corner).Replace(" [instance]", "", StringComparison.Ordinal);

        PrefabLinkReport report = World.BreakPrefabLink(corner, completely: true);

        Assert.Equal(3, report.Unlinked.Count);
        Assert.False(HasAnyLink(corner));
        Assert.Equal(dump, PrefabFuzzBase.DumpTree(World, corner));
        Assert.DoesNotContain("prefab=", World.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstanceInsideAnotherCannotBeUnlinkedAlone()
    {
        Entity corner = LoadCorner();

        Assert.Throws<InvalidOperationException>(() => World.BreakPrefabLink(Child(corner, "Lamp")));
        Assert.True(World.IsPrefabInstance(Child(corner, "Lamp")));
    }

    // ---- Making a prefab

    private Entity Crate()
    {
        Entity crate = World.CreateEntity("Crate");
        World.Set(crate, Transform.At(new Double3(1, 2, 3)));
        Entity lid = World.CreateEntity("Lid", crate);
        World.Set(lid, new Health { Current = 3, Max = 4, Regen = 0.25f });
        World.Set(lid, new Marks { Target = crate, Level = 5 });
        World.CreateEntity("Latch", lid);
        return crate;
    }

    [Fact]
    public void CreatingAPrefabMakesAnInstanceThatSavesAsOneElement()
    {
        Entity crate = Crate();
        Entity lid = Child(crate, "Lid");
        string dump = PrefabFuzzBase.DumpTree(World, crate).Replace("Crate", "Crate [instance]", StringComparison.Ordinal);

        Entity made = World.CreatePrefab(crate, "res://prefabs/crate.aprefab");

        Assert.Equal(crate, made);
        Assert.True(World.IsPrefabInstance(crate));
        Assert.True(World.IsPrefabMember(lid));
        Assert.Equal(["Lid"], NamesBelow(World, crate));
        Assert.Empty(World.PrefabOverrides(crate));
        Assert.False(World.HasPendingPrefabUpdates);
        string scene = World.SaveScene();
        Assert.Equal(1, scene.Split('\n').Count(line => line.Contains("prefab=\"res://prefabs/crate.aprefab|uid://", StringComparison.Ordinal)));
        Assert.DoesNotContain("<Health", scene, StringComparison.Ordinal);
        XmlNode file = XmlNode.Parse(Read("prefabs/crate.aprefab"));
        Assert.Equal("4", file.Attribute("nextId"));
        Assert.Equal("Crate", EntityElement(file, 1).Attribute("name"));
        Assert.Equal("1", EntityElement(file, 2).Child("Marks")!.Attribute("Target"));
        Assert.Equal(dump.Replace(" [instance]", "", StringComparison.Ordinal), PrefabFuzzBase.DumpTree(World, crate).Replace(" [instance]", "", StringComparison.Ordinal));
        using World loaded = OtherWorld();
        Entity again = loaded.Instantiate("res://prefabs/crate.aprefab");
        Assert.Equal(PrefabFuzzBase.DumpTree(World, crate), PrefabFuzzBase.DumpTree(loaded, again));
    }

    [Fact]
    public void ACreatedPrefabIsFollowedLikeAnyOther()
    {
        Entity crate = Crate();
        World.CreatePrefab(crate, "res://prefabs/crate.aprefab");
        Entity second = World.Instantiate("res://prefabs/crate.aprefab");

        Write("prefabs/crate.aprefab", Read("prefabs/crate.aprefab").Replace("Max=\"4\"", "Max=\"44\"", StringComparison.Ordinal));
        World.Prefabs.Reload("res://prefabs/crate.aprefab");
        World.ApplyPrefabUpdates();

        Assert.Equal(44, World.Get<Health>(Child(crate, "Lid")).Max);
        Assert.Equal(44, World.Get<Health>(Child(second, "Lid")).Max);
    }

    [Fact]
    public void CreatingAPrefabKeepsWhatCouldNotBeWrittenAsOverrides()
    {
        Entity crate = Crate();
        World.Set(Child(crate, "Lid"), new Marks { Target = World.CreateEntity("Elsewhere"), Level = 5 });

        World.CreatePrefab(crate, "res://prefabs/crate.aprefab");

        Assert.Equal(["Field 2|Marks|Target"], Listed(World, crate));
        Assert.Equal(World.Lookup("Elsewhere"), World.Get<Marks>(Child(crate, "Lid")).Target);
    }

    [Fact]
    public void CreatingAPrefabRefusesWhatWouldNotMakeOne()
    {
        Entity lamp = LoadLamp();
        Entity crate = Crate();
        World.CreatePrefab(crate, "res://prefabs/crate.aprefab");
        string crateText = Read("prefabs/crate.aprefab");

        Assert.Throws<InvalidOperationException>(() => World.CreatePrefab(lamp, "res://prefabs/other.aprefab"));                       // an instance already
        Assert.Throws<InvalidOperationException>(() => World.CreatePrefab(Child(lamp, "Bulb"), "res://prefabs/other.aprefab"));         // part of one
        Assert.Throws<InvalidOperationException>(() => World.CreatePrefab(World.CreateEntity("Another"), "res://prefabs/crate.aprefab")); // a file that is there
        Assert.Throws<ArgumentException>(() => World.CreatePrefab(World.CreateEntity("Wrong"), "res://prefabs/wrong.txt"));

        Assert.Equal(crateText, Read("prefabs/crate.aprefab"));
        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(Folder, "prefabs/other.aprefab")));
    }

    [Fact]
    public void CreatingAPrefabRefusesALoop()
    {
        // The tree holds a prefab that already names the file to be made (a prefab of a file that is not there yet): writing it would make a prefab that contains itself.
        Write("prefabs/holder.aprefab", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Prefab version="1" uid="{AssetUid.New()}" nextId="3">
              <Entity id="1" name="Holder"><Entity id="2" name="Inner" prefab="res://prefabs/crate.aprefab"/></Entity>
            </Prefab>
            """);
        Entity crate = World.CreateEntity("Crate");
        World.Instantiate("res://prefabs/holder.aprefab", crate);

        Assert.Throws<InvalidOperationException>(() => World.CreatePrefab(crate, "res://prefabs/crate.aprefab"));

        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(Folder, "prefabs/crate.aprefab")));
        Assert.False(World.IsPrefabInstance(crate));
    }

    [Fact]
    public void CreatingAPrefabInASourceThatCannotBeWrittenIsRefused()
    {
        using World reader = new() { Files = ReadOnlyFiles(), Assets = null };
        Entity crate = reader.CreateEntity("Crate");

        Assert.Throws<UnauthorizedAccessException>(() => reader.CreatePrefab(crate, "res://prefabs/crate.aprefab"));

        Assert.False(reader.IsPrefabInstance(crate));
    }
}
