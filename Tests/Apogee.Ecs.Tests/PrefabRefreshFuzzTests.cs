using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Ecs.Tests;

/// <summary>
/// A seeded fuzz of what following a prefab promises: a scene of instances is edited at random, saved, and then its prefab files are changed at random; the world that follows the change in place is
/// the same as another world that loads the text saved before the change over the changed files, and saves the same text. Updating an instance is loading it again without making new entities.
/// </summary>
public sealed class PrefabRefreshFuzzTests : PrefabFuzzBase
{
    private const string BaseLamp = "<Entity id=\"3\" name=\"Base\"><Transform Position=\"0 0 0\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/></Entity>";
    private const string BulbHealth = "<Health Current=\"5\" Max=\"9\" Regen=\"0.5\"/>";

    // The changes a prefab author may make to the lamp: each is a text edit that applies to the original file, and they are independent of each other.
    private static readonly (string From, string To)[] LampChanges =
    [
        (BulbHealth, "<Health Current=\"6\" Max=\"90\" Regen=\"2\"/>"),
        (BaseLamp, ""),
        (BaseLamp, BaseLamp + "<Entity id=\"4\" name=\"Cord\"><Transform Position=\"0 0 1\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/><Marks Target=\"3\" Level=\"4\"/></Entity>"),
        ("name=\"Bulb\"", "name=\"Globe\""),
        (BulbHealth, ""),
        ("<Flicker speed=\"2\" lamp=\"1\"/>", "<Flicker speed=\"3\" lamp=\"3\"/><Frozen/>"),
        ("<Entity id=\"2\" name=\"Bulb\">", "<Entity id=\"2\" name=\"Bulb\"><Marks Target=\"1\" Alive=\"true\"/>"),
        ("<Entity id=\"3\" name=\"Base\">", "<Entity id=\"5\" name=\"Plate\"/><Entity id=\"3\" name=\"Base\">"),
    ];

    private static readonly (string From, string To)[] CornerChanges =
    [
        ("<Entity id=\"4\" name=\"Bench\">", "<Entity id=\"5\" name=\"Stool\"/><Entity id=\"4\" name=\"Bench\">"),
        ("<Entity id=\"4\" name=\"Bench\"><Transform Position=\"0 0 3\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/></Entity>", ""),
        ("<Transform Position=\"2 0 0\"/>", "<Transform Position=\"7 1 0\"/>"),
        ("<Override entity=\"2\"><Health Current=\"9\"/></Override>", "<Override entity=\"2\"><Health Current=\"4\" Max=\"5\"/></Override>"),
    ];

    private static string Edit(string text, (string From, string To)[] changes, Random random)
    {
        foreach (int pick in Enumerable.Range(0, 1 + random.Next(3)).Select(_ => random.Next(changes.Length)).Distinct())
        {
            (string from, string to) = changes[pick];
            if (text.Contains(from, StringComparison.Ordinal))
            {
                text = text.Replace(from, to, StringComparison.Ordinal);
            }
        }

        return text;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void FollowingAPrefabIsLoadingTheSavedSceneOverIt(int seed)
    {
        var random = new Random(seed + 7919);
        World.LoadScene(Base());
        Edits(World, new Random(seed), EditCount(seed));
        AssertNamesAreUnique(World);

        string saved = World.SaveScene();

        string lamp = Edit(Read("prefabs/lamp.aprefab"), LampChanges, random);
        string corner = Edit(Read("prefabs/corner.aprefab"), CornerChanges, random);
        Write("prefabs/lamp.aprefab", lamp);
        Write("prefabs/corner.aprefab", corner);
        if (World.Prefabs.Reload(LampPath).Status == PrefabReloadStatus.Failed || World.Prefabs.Reload(CornerPath).Status == PrefabReloadStatus.Failed)
        {
            return; // two edits that cannot both be made (an id twice): not a prefab, so nothing follows it, which another test says
        }

        PrefabUpdate? update = World.ApplyPrefabUpdates();

        using World loaded = OtherWorld();
        loaded.LoadScene(saved);
        Assert.True(update is null || update.Failed.Count == 0, update is null ? "" : string.Join("; ", update.Failed.Select(f => f.Message)));
        Assert.Equal(Dump(loaded), Dump(World));
        // The text is the same too, kept-aside elements and references included.
        Assert.Equal(loaded.SaveScene(), World.SaveScene());
    }
}
