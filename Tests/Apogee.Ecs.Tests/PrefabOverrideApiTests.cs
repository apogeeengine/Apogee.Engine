using System;
using System.Collections.Generic;
using System.Linq;
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
}
