using System;
using System.Collections.Generic;
using System.Linq;
using Weft.Reflection;
using Xunit;

namespace Weft.Ecs.Tests;

/// <summary>
/// A seeded fuzz of what following a prefab promises: a scene of instances is edited at random, saved, and then its prefab files are changed at random; the world that follows the change in place is
/// the same as another world that loads the text saved before the change over the changed files, and saves the same text. Updating an instance is loading it again without making new entities.
/// </summary>
public sealed class PrefabRefreshFuzzTests : PrefabFuzzBase
{
    // Copies are made by the test itself, while a change waits (see the second theory); the random edits make none, so that what a reference waits for is one entity.
    protected override bool MayClone => false;

    private const string BaseLamp = "<Entity id=\"3\" name=\"Base\"><Transform Position=\"0 0 0\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/></Entity>";
    private const string BulbHealth = "<Health Current=\"5\" Max=\"9\" Regen=\"0.5\"/>";

    // The changes a prefab author may make to the lamp: each is a text edit that applies to the original file, and they are independent of each other.
    private static readonly (string From, string To)[] LampChanges =
    [
        (BulbHealth, "<Health Current=\"116\" Max=\"137\" Regen=\"2.75\"/>"),
        (BaseLamp, ""),
        (BaseLamp, BaseLamp + "<Entity id=\"4\" name=\"Cord\"><Transform Position=\"0 0 1\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/><Marks Target=\"3\" Level=\"4\"/></Entity>"),
        ("name=\"Bulb\"", "name=\"Globe\""),
        (BulbHealth, ""),
        ("<Flicker speed=\"2\" lamp=\"1\"/>", "<Flicker speed=\"3\" lamp=\"2\"/><Furnished Count=\"3\"/>"),
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

        string lamp = Edit(Read("prefabs/lamp.wprf"), LampChanges, random);
        string corner = Edit(Read("prefabs/corner.wprf"), CornerChanges, random);
        Write("prefabs/lamp.wprf", lamp);
        Write("prefabs/corner.wprf", corner);
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

    // ---- Two rounds, from a world that already holds what an update keeps aside

    // The base scene with a lamp whose element holds an entity for a member the lamp does not have (kept aside), and a plain entity that waits for it and is waited for.
    private string Base2() => Base().Replace("</Scene>", $"""
          <Entity id="20" name="Ghosted" prefab="{Lamp}"><Override entity="9"><Entity id="21" name="Ghost"><Marks Target="22"/></Entity></Override></Entity>
          <Entity id="22" name="Watcher"><Marks Target="21"/></Entity>
        </Scene>
        """, StringComparison.Ordinal);

    // Edits to the files that are more than one replacement, which only apply when the file still looks like it did.
    private static string RenumberLampRoot(string text) =>
        text.Contains("<Entity id=\"3\" name=\"Base\">", StringComparison.Ordinal) && text.Contains("<Entity id=\"1\" name=\"Lamp\">", StringComparison.Ordinal) && !text.Contains("id=\"9\"", StringComparison.Ordinal)
            ? text.Replace("<Entity id=\"3\" name=\"Base\">", "<Entity id=\"9\" name=\"Base\">", StringComparison.Ordinal).Replace("<Entity id=\"1\" name=\"Lamp\">", "<Entity id=\"3\" name=\"Lamp\">", StringComparison.Ordinal)
                .Replace("lamp=\"1\"", "lamp=\"3\"", StringComparison.Ordinal).Replace("Target=\"1\"", "Target=\"3\"", StringComparison.Ordinal).Replace("nextId=\"4\"", "nextId=\"10\"", StringComparison.Ordinal)
            : text;

    private string SwapTheCornersLamp(string text) =>
        text.Replace($"<Entity id=\"2\" name=\"Lamp\" prefab=\"{Lamp}\">", "<Entity id=\"2\" name=\"Lamp\" prefab=\"res://prefabs/lantern.wprf\">", StringComparison.Ordinal);

    private static Entity At(World world, string path)
    {
        Entity current = default;
        foreach (string name in path.Split('/'))
        {
            Entity next = (current.IsNull ? world.Roots() : world.Children(current)).FirstOrDefault(e => world.GetName(e) == name);
            if (next.IsNull)
            {
                return default;
            }

            current = next;
        }

        return current;
    }

    private static List<long> Ids(World world) => [.. world.UserEntities().Select(e => (long)e.Id).Order()];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TwoRoundsOfPrefabChangesAreLoadsOfTheSavedSceneOverThem(int seed)
    {
        var random = new Random(seed + 104729);
        Write("prefabs/lantern.wprf", "<Prefab version=\"1\" nextId=\"4\"><Entity id=\"2\" name=\"Lantern\"><Transform Position=\"0 0 0\" Rotation=\"0 0 0 1\" Scale=\"1 1 1\"/><Entity id=\"1\" name=\"Glass\"/><Entity id=\"3\" name=\"Frame\"/></Entity></Prefab>");
        World.LoadScene(Base2());

        for (int round = 0; round < 2; round++)
        {
            Edits(World, new Random(seed + round), 1 + (EditCount(seed) / 2));
            AssertNamesAreUnique(World);
            if (World.UserEntities().Any(e => PathOf(World, e).Count(c => c == '/') > 9))
            {
                return; // instances in instances in instances: a prefab nests 16 deep at most, and what is deeper is missing where it is made (the limits have their own tests)
            }

            string saved = World.SaveScene();
            List<long> before = Ids(World);
            List<string> paths = [.. World.UserEntities().Select(e => PathOf(World, e))];
            if (paths.Count == 0)
            {
                return;
            }

            // The files change (the lamp, the corner, now and then both through the renumbering and the lantern, ), the world is told of each.
            string lampBefore = Read("prefabs/lamp.wprf");
            string cornerBefore = Read("prefabs/corner.wprf");
            string lamp = Edit(lampBefore, LampChanges, random);
            string corner = Edit(cornerBefore, CornerChanges, random);
            bool reuses = false;                     // an id of a file now means another entity: an override follows the id, so what it did cannot be undone by putting the file back
            if (random.Next(4) == 0)
            {
                lamp = RenumberLampRoot(lamp);
                reuses = true;
            }

            if (random.Next(4) == 0)
            {
                corner = SwapTheCornersLamp(corner);
                reuses = true;
            }

            Write("prefabs/lamp.wprf", lamp);
            Write("prefabs/corner.wprf", corner);
            bool usable = true;
            foreach (string path in new[] { LampPath, CornerPath })
            {
                usable &= World.Prefabs.Reload(path).Status != PrefabReloadStatus.Failed;
            }

            if (!usable)
            {
                return; // two edits that cannot both be made (an id twice): not a prefab, so nothing follows it, which another test says
            }

            // What a world with the new files makes of the saved text, and what is done to both while the change waits to be applied.
            using World loaded = OtherWorld();
            loaded.LoadScene(saved);
            bool opsDone = false;
            var copies = new List<(Entity Copy, Entity Original)>();
            for (int op = reuses ? 0 : random.Next(3); op > 0; op--)
            {
                string at = Pick(random, paths);
                if (At(World, at).IsNull || At(loaded, at).IsNull)
                {
                    continue;
                }

                opsDone = true;
                switch (random.Next(3))
                {
                    case 0:
                        _ = World.Instantiate(LampPath, At(World, at));
                        _ = loaded.Instantiate(LampPath, At(loaded, at));
                        break;
                    case 1:
                        // A copy keeps what it was copied with (entity fields are not remapped), so what the updated copy is cannot be compared with a copy of the updated original: it is checked after the update
                        // (it has the members its prefab has, none twice) and taken away before the worlds are compared.
                        if (!World.IsPrefabMember(At(World, at)))
                        {
                            copies.Add((World.Clone(At(World, at)), At(World, at)));
                        }

                        break;
                    default:
                        World.Restore(World.Snapshot());
                        break;
                }
            }

            PrefabUpdate? update = World.ApplyPrefabUpdates();
            foreach ((Entity copy, Entity original) in copies)
            {
                if (World.IsAlive(copy) && World.IsAlive(original) && World.IsPrefabInstance(original) && World.IsPrefabInstance(copy))
                {
                    Assert.Equal(World.Children(original).Count - (World.Children(original).Count(e => !World.IsPrefabMember(e))), World.Children(copy).Count(World.IsPrefabMember));
                }
            }

            if (update is not null)
            {
                // Entities are the same ones: what was there and was not dropped is there, with its id; what is new was made by the update (or by what was done while it waited).
                List<long> after = Ids(World);
                var expected = new HashSet<long>(before);
                expected.ExceptWith(update.Destroyed.Select(e => (long)e.Id));
                Assert.All(expected.Where(id => !after.Contains(id)), id => Assert.Fail($"entity {id} was lost without being reported destroyed"));
                Assert.All(update.Created, e => Assert.True(after.Contains((long)e.Id), "an entity that was made is gone"));
            }

            foreach ((Entity copy, _) in copies)
            {
                if (World.IsAlive(copy))
                {
                    World.Destroy(copy);
                }
            }

            Assert.True(update is null || update.Failed.Count == 0, update is null ? "" : string.Join("; ", update.Failed.Select(f => f.Message)));
            Assert.Equal(Dump(loaded), Dump(World));

            // What the world saves is what it is, and what it keeps aside is not lost: with the prefabs as they were, it is the scene as it was saved (a scene does not say in what order siblings were made, so
            // the text of what was made in another order is another text, and this compares what is in it).
            using (World again = OtherWorld())
            {
                again.LoadScene(World.SaveScene());
                Assert.Equal(Dump(World), Dump(again));
            }

            if (!opsDone && !reuses)
            {
                Write("prefabs/lamp.wprf", lampBefore);
                Write("prefabs/corner.wprf", cornerBefore);
                _ = World.Prefabs.Reload(LampPath);
                _ = World.Prefabs.Reload(CornerPath);
                World.ApplyPrefabUpdates();
                using World back = OtherWorld();
                back.LoadScene(saved);
                Assert.Equal(Dump(back), Dump(World));
            }

        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void SavingAnInstanceOverItsPrefabChangesNothingInIt(int seed)
    {
        World.LoadScene(Base2());
        Edits(World, new Random(seed), 1 + (EditCount(seed) / 2));
        AssertNamesAreUnique(World);
        List<Entity> lamps = [.. World.UserEntities().Where(e => World.IsPrefabInstance(e) && World.PrefabOf(e)!.StartsWith(LampPath, StringComparison.Ordinal) && !World.IsPrefabMember(e))];
        if (lamps.Count == 0)
        {
            return;
        }

        Entity root = lamps[seed % lamps.Count];
        List<Entity> subtree = World.Subtree(root);
        try
        {
            World.SavePrefabFile(LampPath, root);
        }
        catch (InvalidOperationException)
        {
            return; // an instance that holds the lamp itself (an entity added under it) cannot be saved as the lamp
        }

        PrefabUpdate? update = World.ApplyPrefabUpdates();

        Assert.NotNull(update);
        if (update.Destroyed.Contains(root))
        {
            return; // it was added below an entity of another prefab that the lamp it was saved as no longer has (the corner's lamp): taken away with its parent, kept aside there
        }

        Assert.Equal(subtree, World.Subtree(root));                                              // the instance it was saved from has what it was saved with: nothing made, nothing destroyed in it
        Assert.DoesNotContain(update.Created, e => subtree.Contains(World.GetParent(e)));
        Assert.True(update.Failed.Count == 0, string.Join("; ", update.Failed.Select(f => f.Message)));
        using World again = OtherWorld();
        again.LoadScene(World.SaveScene());
        Assert.Equal(Dump(again), Dump(World));
    }
}
