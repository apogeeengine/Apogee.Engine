using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Core.Assets;
using Apogee.Reflection;
using Apogee.Core.Logging;
using Apogee.Native.Tests;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Ecs.Tests;

/// <summary>Snapshots name assets by reference: restored into another world (a new session after a play run), a field holds that world's handle for the same asset.</summary>
public sealed class SnapshotAssetTests
{
    private sealed class Sink : ILogSink
    {
        public List<string> Warnings { get; } = [];

        public void Write(in LogEntry entry)
        {
            if (entry.Level == LogLevel.Warning && entry.Category == "Scene")
            {
                lock (Warnings)
                {
                    Warnings.Add(entry.Message);
                }
            }
        }
    }

    // A fake "model" library: handles are numbers of one world, handed out by path in the order the paths are asked for.
    private sealed class Models(params string[] known)
    {
        private readonly List<string> _paths = [.. known];

        public uint Handle(string path)
        {
            int at = _paths.IndexOf(path);
            if (at < 0)
            {
                _paths.Add(path);
                at = _paths.Count - 1;
            }

            return (uint)at + 1;
        }

        public string PathOf(uint handle) => _paths[(int)handle - 1];

        public World NewWorld(Func<string, bool>? exists = null)
        {
            NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
            var world = new World();
            world.RegisterAssetType(
                "model",
                handle => handle >= 1 && handle <= _paths.Count ? AssetRef.FromPath(_paths[(int)handle - 1]) : null,
                reference => exists is null || exists(reference.Path) ? Handle(reference.Path) : 0);
            return world;
        }
    }

    [Fact]
    public void ASnapshotRestoredInAnotherWorldNamesTheSameAssets()
    {
        // The two worlds number their models differently: the car is 1 in the first and 2 in the second.
        var first = new Models("res://models/car.glb", "res://models/tree.glb");
        var second = new Models("res://models/tree.glb", "res://models/car.glb");
        using World before = first.NewWorld();
        Entity car = before.CreateEntity("car");
        before.Set(car, new Furnished { Count = 2, Model = first.Handle("res://models/car.glb") });
        Entity tree = before.CreateEntity("tree", car);
        before.Set(tree, new Furnished { Count = 3, Model = first.Handle("res://models/tree.glb") });
        Entity bare = before.CreateEntity("bare");
        before.Set(bare, new Furnished { Count = 4 });
        WorldSnapshot snapshot = before.Snapshot();

        using World after = second.NewWorld();
        after.Restore(snapshot);

        Assert.Equal("res://models/car.glb", second.PathOf(after.Get<Furnished>(car).Model));
        Assert.Equal(2u, after.Get<Furnished>(car).Model);
        Assert.Equal("res://models/tree.glb", second.PathOf(after.Get<Furnished>(tree).Model));
        Assert.Equal(0u, after.Get<Furnished>(bare).Model);
        Assert.Equal(2, after.Get<Furnished>(car).Count);   // the other fields are untouched

        // The snapshot is not changed by a restore: it can go into a third world, or the first again.
        using World third = new Models("res://models/car.glb").NewWorld();
        third.RestoreEntities(snapshot);
        Assert.Equal(1u, third.Get<Furnished>(car).Model);
        Assert.Equal(2u, third.Get<Furnished>(tree).Model);
    }

    [Fact]
    public void ARestoreInTheSameWorldKeepsTheBytes()
    {
        var models = new Models();
        using World world = models.NewWorld();
        Entity e = world.CreateEntity("e");
        world.Set(e, new Furnished { Count = 1, Model = 77 });   // a handle the library cannot name: the bytes still come back as they were
        WorldSnapshot snapshot = world.Snapshot();
        world.Set(e, new Furnished { Count = 9, Model = models.Handle("res://models/other.glb") });

        world.Restore(snapshot);

        Assert.Equal(77u, world.Get<Furnished>(e).Model);
        Assert.Equal(1, world.Get<Furnished>(e).Count);
    }

    [Fact]
    public void AnAssetTheOtherWorldCannotFindIsZeroWithAWarning()
    {
        var sink = new Sink();
        Log.AddSink(sink);
        try
        {
            var first = new Models("res://models/car.glb", "res://models/gone.glb");
            using World before = first.NewWorld();
            Entity a = before.CreateEntity("a");
            before.Set(a, new Furnished { Count = 1, Model = 1 });
            Entity b = before.CreateEntity("b");
            before.Set(b, new Furnished { Count = 2, Model = 2 });
            Entity unnamed = before.CreateEntity("unnamed");
            before.Set(unnamed, new Furnished { Count = 3, Model = 50 });   // names nothing at all
            WorldSnapshot snapshot = before.Snapshot();

            using World after = new Models().NewWorld(exists: path => path.EndsWith("car.glb", StringComparison.Ordinal));
            after.Restore(snapshot);

            Assert.Equal(1u, after.Get<Furnished>(a).Model);
            Assert.Equal(0u, after.Get<Furnished>(b).Model);
            Assert.Equal(2, after.Get<Furnished>(b).Count);
            Assert.Equal(0u, after.Get<Furnished>(unnamed).Model);
            Assert.Contains(sink.Warnings, w => w.Contains("gone.glb", StringComparison.Ordinal));
            Assert.Contains(sink.Warnings, w => w.Contains("handle 50", StringComparison.Ordinal));
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    public void AWorldWithoutTheAssetTypeLeavesTheFieldEmptyAndSaysSoOnce()
    {
        var sink = new Sink();
        Log.AddSink(sink);
        try
        {
            var first = new Models("res://models/car.glb");
            using World before = first.NewWorld();
            for (int i = 0; i < 3; i++)
            {
                Entity e = before.CreateEntity("e" + i);
                before.Set(e, new Furnished { Count = i, Model = 1 });
            }

            NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
            using var bare = new World();
            bare.Restore(before.Snapshot());

            Assert.All(bare.Roots(), e => Assert.Equal(0u, bare.Get<Furnished>(e).Model));
            Assert.Single(sink.Warnings, w => w.Contains("Furnished.Model", StringComparison.Ordinal));
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    public void UndoOfADeleteStillRestoresModels()
    {
        var models = new Models("res://models/car.glb");
        using World world = models.NewWorld();
        Entity car = world.CreateEntity("car");
        world.Set(car, new Furnished { Count = 5, Model = models.Handle("res://models/car.glb") });
        Entity wheel = world.CreateEntity("wheel", car);
        world.Set(wheel, new Furnished { Model = models.Handle("res://models/wheel.glb") });
        WorldSnapshot branch = world.SnapshotTree(car);

        world.Destroy(car);
        Assert.False(world.IsAlive(car));
        world.RestoreEntities(branch);

        Assert.Equal("res://models/car.glb", models.PathOf(world.Get<Furnished>(car).Model));
        Assert.Equal("res://models/wheel.glb", models.PathOf(world.Get<Furnished>(wheel).Model));
        Assert.Equal(5, world.Get<Furnished>(car).Count);
        Assert.Equal(car, world.GetParent(wheel));
    }
}
