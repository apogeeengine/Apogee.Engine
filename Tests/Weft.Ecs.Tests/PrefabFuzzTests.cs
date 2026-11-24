using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Weft.Core.Numerics;
using Weft.Ecs;
using Weft.Reflection;
using Xunit;

namespace Weft.Ecs.Tests;

/// <summary>
/// A seeded fuzz of what saving a scene with prefab instances promises: from two corners (each with two lamps, one of them with an override of its own and an entity added to it), a lamp, a player and a
/// switch, a few random edits (odd numbers, references, components, names, deletions, new entities, copies, instances, reused entity ids, snapshots, an entity moved by code) are made; the scene is
/// saved, loaded into another world, and that world is the same as the first and saves the same text. A second variant breaks the prefab in between, and the scene it saves still gives everything back
/// when the prefab is mended.
/// </summary>
public sealed class PrefabFuzzTests : PrefabFuzzBase
{
    // ---- The two promises

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ASceneEditedAtRandomSavesLoadsAndSavesTheSame(int seed)
    {
        World.LoadScene(Base());
        Edits(World, new Random(seed), EditCount(seed));
        AssertNamesAreUnique(World);

        string saved = World.SaveScene();

        using World loaded = OtherWorld();
        loaded.LoadScene(saved);
        Same(Dump(World), Dump(loaded));
        Same(saved, loaded.SaveScene());
    }

    private static void Same(string expected, string actual) => Assert.Equal(expected, actual);

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ASceneSavedWhileItsPrefabIsBrokenGivesEverythingBackWhenItIsMended(int seed)
    {
        World.LoadScene(Base());
        Edits(World, new Random(seed), EditCount(seed));
        AssertNamesAreUnique(World);

        string saved = World.SaveScene();
        string mended = Read("prefabs/lamp.aprefab");

        // The lamp loses its Base and the Bulb its Health, and the scene is loaded, edited a little (in the part that is not the lamp, in both worlds), and saved again against that.
        Write("prefabs/lamp.aprefab", mended
            .Replace("<Entity id=\"3\" name=\"Base\"><Transform Position=\"0 0 0\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/></Entity>", "", StringComparison.Ordinal)
            .Replace("<Health Current=\"5\" Max=\"9\" Regen=\"0.5\"/>", "", StringComparison.Ordinal));
        using World broken = OtherWorld();
        broken.LoadScene(saved);
        EditThePartThatIsNotALamp(World, seed);
        EditThePartThatIsNotALamp(broken, seed);
        string whileBroken = broken.SaveScene();

        // The same text from the same world is the same text again.
        using World again = OtherWorld();
        again.LoadScene(whileBroken);
        Same(whileBroken, again.SaveScene());

        Write("prefabs/lamp.aprefab", mended);
        using World back = OtherWorld();
        back.LoadScene(whileBroken);
        Same(Dump(World), Dump(back));
    }

    private static void EditThePartThatIsNotALamp(World world, int seed)
    {
        Entity player = world.Lookup("Player");
        if (!player.IsNull)
        {
            world.Set(player, Transform.At(new Double3(seed, -0.0, 1e7)));
        }

        Entity switchEntity = world.Lookup("Switch");
        if (!switchEntity.IsNull)
        {
            world.Add<Frozen>(switchEntity);
        }
    }
}
