using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Ecs;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Ecs.Tests;

/// <summary>
/// A seeded fuzz of what reverting and applying overrides promise, from the scene and the random edits the other prefab fuzzes use (corners that hold lamps, an added entity, overrides of every kind):
/// reverting everything makes an instance what a new one of its prefab is, whatever was done to it; a revert of some of it, and an apply of some of it into either prefab, leave a world that saves,
/// loads in another world over the changed files, and is the same as that one; and what is refused leaves the files and the world as they were.
/// </summary>
public sealed class PrefabOverrideFuzzTests : PrefabFuzzBase
{
    // The edits make no copies: a copy of an instance that keeps data aside has the same numbers for it, which a reference that waits for one of them cannot tell apart (as in PrefabRefreshFuzzTests).
    protected override bool MayClone => false;

    private List<Entity> Outermost() => [.. World.UserEntities().Where(e => World.IsPrefabInstance(e) && !World.IsPrefabMember(e)).OrderBy(e => e.Id)];

    private List<Entity> AllInstances() => [.. World.UserEntities().Where(e => World.IsPrefabInstance(e)).OrderBy(e => e.Id)];

    private string Files2() => Read("prefabs/lamp.aprefab") + Read("prefabs/corner.aprefab");

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RevertingEverythingMakesAnInstanceWhatANewOneIs(int seed)
    {
        World.LoadScene(Base());
        Edits(World, new Random(seed), EditCount(seed));
        AssertNamesAreUnique(World);

        foreach (Entity root in Outermost())
        {
            if (!World.IsAlive(root))
            {
                continue;
            }

            string name = World.GetName(root)!;
            string prefab = World.PrefabOf(root)!;
            World.RevertAllOverrides(root);

            // Where the instance is placed is what "all" leaves: that and nothing else.
            Assert.All(World.PrefabOverrides(root), o => Assert.True(o.Kind == PrefabOverrideKind.Unapplied || (o.Kind == PrefabOverrideKind.Field && o is { Target: "", Component: "Transform", Field: "Position" or "Rotation" }), $"{o.Kind} {o.Target} {o.Component} {o.Field} is left"));
            World.RevertOverrides(root, World.PrefabOverrides(root).Where(static o => o.Kind != PrefabOverrideKind.Unapplied));
            Assert.All(World.PrefabOverrides(root), o => Assert.Equal(PrefabOverrideKind.Unapplied, o.Kind));
            using World fresh = OtherWorld();
            Entity expected = fresh.Instantiate(prefab);
            fresh.SetName(expected, name);
            Assert.Equal(DumpTree(fresh, expected), DumpTree(World, root));
        }

        AssertNamesAreUnique(World);
        string saved = World.SaveScene();
        using World loaded = OtherWorld();
        loaded.LoadScene(saved);
        Assert.Equal(Dump(World), Dump(loaded));
        Assert.Equal(saved, loaded.SaveScene());
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RevertingSomeOverridesLeavesAWorldThatSavesAndLoadsTheSame(int seed)
    {
        var random = new Random(seed * 17 + 3);
        World.LoadScene(Base());
        Edits(World, new Random(seed), EditCount(seed));
        AssertNamesAreUnique(World);

        foreach (Entity root in Outermost())
        {
            if (!World.IsAlive(root))
            {
                continue;
            }

            List<PrefabOverride> all = [.. World.PrefabOverrides(root)];
            List<PrefabOverride> chosen = [.. all.Where(_ => random.Next(2) == 0)];
            World.RevertOverrides(root, chosen);

            // What was not reverted is still there (an entity that was destroyed with a reverted one is the exception, and the entity of a revert is the same).
            List<PrefabOverride> left = [.. World.PrefabOverrides(root)];
            Assert.True(left.Count <= all.Count, $"{left.Count} overrides left of {all.Count}");
        }

        AssertNamesAreUnique(World);
        string saved = World.SaveScene();
        using World loaded = OtherWorld();
        loaded.LoadScene(saved);
        Assert.Equal(Dump(World), Dump(loaded));
        Assert.Equal(saved, loaded.SaveScene());
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ApplyingOverridesLeavesTheWorldThatItsSavedSceneGivesOverTheNewFiles(int seed)
    {
        var random = new Random(seed * 31 + 5);
        World.LoadScene(Base());
        Edits(World, new Random(seed), EditCount(seed));
        AssertNamesAreUnique(World);

        foreach (Entity root in Outermost())
        {
            if (!World.IsAlive(root))
            {
                continue;
            }

            List<PrefabOverride> applicable = [.. World.PrefabOverrides(root).Where(static o => o.Kind != PrefabOverrideKind.Unapplied)];
            if (applicable.Count == 0 || random.Next(4) == 0)
            {
                continue;
            }

            List<PrefabOverride> chosen = [.. applicable.Where(_ => random.Next(2) == 0)];
            if (chosen.Count == 0)
            {
                chosen.Add(applicable[random.Next(applicable.Count)]);
            }

            PrefabApplyTarget target = random.Next(2) == 0 ? PrefabApplyTarget.InstancePrefab : PrefabApplyTarget.SourcePrefab;
            string files = Files2();
            string world = Dump(World);
            string scene = World.SaveScene();
            try
            {
                PrefabUpdate? update = World.ApplyOverrides(root, chosen, target);
                Assert.True(update is null || update.Failed.Count == 0, update is null ? "" : string.Join("; ", update.Failed.Select(f => f.Message)));
            }
            catch (InvalidOperationException)
            {
                // Refused (an Entity field that names something outside the prefab, the file the instance cannot follow…): nothing is different.
                Assert.Equal(files, Files2());
                Assert.Equal(world, Dump(World));
                Assert.Equal(scene, World.SaveScene());
                Assert.False(World.HasPendingPrefabUpdates);
                continue;
            }

            Assert.False(World.HasPendingPrefabUpdates);
            AssertNamesAreUnique(World);
            string saved = World.SaveScene();
            using World loaded = OtherWorld();
            loaded.LoadScene(saved);
            Assert.Equal(Dump(World), Dump(loaded));
            Assert.Equal(saved, loaded.SaveScene());
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ApplyingFromInstancesInsideInstancesLeavesTheSameWorld(int seed)
    {
        var random = new Random(seed * 37 + 11);
        World.LoadScene(Base());
        Edits(World, new Random(seed), EditCount(seed));
        AssertNamesAreUnique(World);

        foreach (Entity root in AllInstances())
        {
            if (!World.IsAlive(root))
            {
                continue;
            }

            List<PrefabOverride> applicable = [.. World.PrefabOverrides(root).Where(static o => o.Kind != PrefabOverrideKind.Unapplied)];
            if (applicable.Count == 0 || random.Next(4) == 0)
            {
                continue;
            }

            List<PrefabOverride> chosen = [.. applicable.Where(_ => random.Next(2) == 0)];
            if (chosen.Count == 0)
            {
                chosen.Add(applicable[random.Next(applicable.Count)]);
            }

            PrefabApplyTarget target = random.Next(2) == 0 ? PrefabApplyTarget.InstancePrefab : PrefabApplyTarget.SourcePrefab;
            string files = Files2();
            string world = Dump(World);
            string scene = World.SaveScene();
            try
            {
                PrefabUpdate? update = World.ApplyOverrides(root, chosen, target);
                Assert.True(update is null || update.Failed.Count == 0, update is null ? "" : string.Join("; ", update.Failed.Select(f => f.Message)));
            }
            catch (InvalidOperationException)
            {
                // Refused (an Entity field that names something outside the prefab, the file the instance cannot follow…): nothing is different.
                Assert.Equal(files, Files2());
                Assert.Equal(world, Dump(World));
                Assert.Equal(scene, World.SaveScene());
                Assert.False(World.HasPendingPrefabUpdates);
                continue;
            }

            Assert.False(World.HasPendingPrefabUpdates);
            AssertNamesAreUnique(World);
            string saved = World.SaveScene();
            using World loaded = OtherWorld();
            loaded.LoadScene(saved);
            Assert.Equal(Dump(World), Dump(loaded));
            Assert.Equal(saved, loaded.SaveScene());
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ApplyingToTheInstancesPrefabKeepsTheInstanceAsItWasAndTakesTheOverridesOffIt(int seed)
    {
        var random = new Random(seed * 13 + 1);
        World.LoadScene(Base());
        Edits(World, new Random(seed), EditCount(seed));
        AssertNamesAreUnique(World);

        foreach (Entity root in Outermost())
        {
            if (!World.IsAlive(root))
            {
                continue;
            }

            List<PrefabOverride> applicable = [.. World.PrefabOverrides(root).Where(static o => o.Kind != PrefabOverrideKind.Unapplied)];
            if (applicable.Count == 0)
            {
                continue;
            }

            List<PrefabOverride> chosen = [.. applicable.Where(_ => random.Next(2) == 0)];
            if (chosen.Count == 0)
            {
                chosen.Add(applicable[0]);
            }

            string looks = DumpTree(World, root);
            string sceneBefore = World.SaveScene();
            string filesBefore = Files2();
                try
            {
                World.ApplyOverrides(root, chosen, PrefabApplyTarget.InstancePrefab);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (!World.IsAlive(root))
            {
                continue; // it was held by another instance, whose entity the file took away
            }

            // It looks as it did, unless the file is also the prefab of something it holds (an instance of the same prefab it added, which follows the file), it is held by another instance (which follows
            // the file and may lose what it holds), or it names entities of other instances (that follow it too).
            bool alone = !World.Subtree(root).Any(e => e != root && World.IsPrefabInstance(e) && !World.IsPrefabMember(e)) && !Ancestors(root).Any(e => World.IsPrefabInstance(e) || World.Has<PrefabMember>(e)) && !looks.Contains("=outside", StringComparison.Ordinal);
            if (alone && DumpTree(World, root) != looks)
            {
                Assert.Fail($"applied {string.Join("; ", chosen.Select(Key))}\n{sceneBefore}\n{filesBefore}\n--- before\n{looks}\n--- after\n{DumpTree(World, root)}");
            }

            var now = new HashSet<string>(World.PrefabOverrides(root).Select(Key));
            foreach (PrefabOverride applied in chosen)
            {
                // (The name of an instance that an apply made part of another is the other's to say: it is still there, and is the instance's own.)
                if (applied is { Kind: PrefabOverrideKind.Name, Target: "" })
                {
                    continue;
                }

                Assert.DoesNotContain(Key(applied), now);
            }
        }
    }

    // What an override is about, whichever listing it is in (the address of the instance is not part of it).
    private static string Key(PrefabOverride o) => $"{o.Kind}|{o.Target}|{o.Component}|{o.Field}|{(o.Kind == PrefabOverrideKind.AddedEntity ? o.Entity.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")}";

    [Theory]
    [MemberData(nameof(Seeds))]
    public void AWriteThatFailsLeavesTheFilesAndTheWorldAsTheyWereOrTheWorldFollowingTheDisk(int seed)
    {
        var random = new Random(seed * 41 + 7);
        World.LoadScene(Base());
        Edits(World, new Random(seed), EditCount(seed));
        AssertNamesAreUnique(World);

        foreach (Entity root in AllInstances())
        {
            if (!World.IsAlive(root))
            {
                continue;
            }

            List<PrefabOverride> applicable = [.. World.PrefabOverrides(root).Where(static o => o.Kind != PrefabOverrideKind.Unapplied)];
            if (applicable.Count == 0)
            {
                continue;
            }

            List<PrefabOverride> chosen = [.. applicable.Where(_ => random.Next(2) == 0)];
            if (chosen.Count == 0)
            {
                chosen.Add(applicable[0]);
            }

            // Some of the first four writes fail: the ones of the files, the put-backs of what was written.
            Faults.FailOn([.. Enumerable.Range(1, 4).Where(_ => random.Next(2) == 0)]);
            string files = Files2();
            string world = Dump(World);
            int changes = 0;
            void Count(PrefabUpdate _) => changes++;
            World.PrefabsChanged += Count;
            bool failed = false;
            try
            {
                World.ApplyOverrides(root, chosen, random.Next(2) == 0 ? PrefabApplyTarget.InstancePrefab : PrefabApplyTarget.SourcePrefab);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                failed = true;
            }
            finally
            {
                World.PrefabsChanged -= Count;
                Faults.FailOn();
            }

            Assert.False(World.HasPendingPrefabUpdates);
            if (failed && files == Files2())
            {
                // Nothing is on the disk that was not: the world is as it was, and nobody is told that it changed.
                Assert.Equal(world, Dump(World));
                Assert.Equal(0, changes);
            }

            // Whatever came of it, the world is what the disk says: its scene loads over the files into the same world.
            AssertNamesAreUnique(World);
            string saved = World.SaveScene();
            using World loaded = OtherWorld();
            loaded.LoadScene(saved);
            Assert.Equal(Dump(World), Dump(loaded));
            Assert.Equal(saved, loaded.SaveScene());
        }
    }

    private IEnumerable<Entity> Ancestors(Entity entity)
    {
        for (Entity above = World.GetParent(entity); !above.IsNull; above = World.GetParent(above))
        {
            yield return above;
        }
    }
}
