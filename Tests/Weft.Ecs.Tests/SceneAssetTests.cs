using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Core.Assets;
using Weft.Core.Logging;
using Weft.Native.Tests;
using Weft.Reflection;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;

namespace Weft.Ecs.Tests;

/// <summary>Asset fields in scenes: saved as the path and the UID of what they name, and loaded by path, then by UID.</summary>
public sealed class SceneAssetTests : IDisposable
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

    private readonly string _folder = Directory.CreateTempSubdirectory("weft-scene-assets").FullName;
    private readonly VirtualFileSystem _files = new();
    private readonly AssetDatabase _assets;
    private readonly AssetUid _carUid = AssetUid.New();

    // A fake "model" library: handles are numbers in this run, handed out per path.
    private readonly Dictionary<string, uint> _handles = new(StringComparer.Ordinal);

    public SceneAssetTests()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        _files.Mount("res://", new DirectorySource(_folder, writable: true));
        _assets = new AssetDatabase(_files);
        Write("models/car.glb", "not really a model");
        Write("models/car.glb.wimp", $"<Import uid=\"{_carUid}\"/>");
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string path, string text)
    {
        string full = Path.Combine(_folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private uint Handle(string path)
    {
        if (!_handles.TryGetValue(path, out uint handle))
        {
            _handles[path] = handle = (uint)_handles.Count + 1;
        }

        return handle;
    }

    private World NewWorld(bool registered = true)
    {
        var world = new World();
        if (registered)
        {
            world.RegisterAssetType(
                "model",
                handle => _handles.Where(h => h.Value == handle).Select(h => (AssetRef?)_assets.RefOf(h.Key)).FirstOrDefault(),
                reference => _assets.Resolve(reference) is { } path ? Handle(path) : 0);
        }

        return world;
    }

    private string PathOf(uint handle) => _handles.First(h => h.Value == handle).Key;

    [Fact]
    public void AnAssetFieldIsSavedAsPathAndUid()
    {
        using World world = NewWorld();
        Entity e = world.CreateEntity("car");
        world.Set(e, new Furnished { Count = 2, Model = Handle("res://models/car.glb") });

        string text = world.SaveScene();

        Assert.Contains($"Model=\"res://models/car.glb|{_carUid}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Model=\"1\"", text, StringComparison.Ordinal); // not the number of this run
    }

    [Fact]
    public void AnAssetFieldLoadsFromItsPath()
    {
        using World world = NewWorld();
        List<Entity> loaded = world.LoadScene("""
            <Scene version="1">
              <Entity id="1" name="old"><Furnished Count="1" Model="res://models/car.glb"/></Entity>
              <Entity id="2" name="none"><Furnished Count="2" Model=""/></Entity>
              <Entity id="3" name="absent"><Furnished Count="3"/></Entity>
            </Scene>
            """);

        Assert.Equal("res://models/car.glb", PathOf(world.Get<Furnished>(loaded[0]).Model)); // a scene from before UIDs: the path alone
        Assert.Equal(0u, world.Get<Furnished>(loaded[1]).Model);
        Assert.Equal(2, world.Get<Furnished>(loaded[1]).Count);
        Assert.Equal(0u, world.Get<Furnished>(loaded[2]).Model);

        // Saved again, it holds the UID too.
        Assert.Contains($"res://models/car.glb|{_carUid}", world.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMovedAssetLoadsThroughItsUid()
    {
        string text;
        using (World saving = NewWorld())
        {
            Entity e = saving.CreateEntity("car");
            saving.Set(e, new Furnished { Model = Handle("res://models/car.glb") });
            text = saving.SaveScene();
        }

        // The model and its settings are renamed and moved together: the path in the scene leads nowhere, the UID leads to the file.
        Directory.CreateDirectory(Path.Combine(_folder, "props"));
        File.Move(Path.Combine(_folder, "models", "car.glb"), Path.Combine(_folder, "props", "vehicle.glb"));
        File.Move(Path.Combine(_folder, "models", "car.glb.wimp"), Path.Combine(_folder, "props", "vehicle.glb.wimp"));
        _assets.Refresh();

        using World loading = NewWorld();
        Entity car = Assert.Single(loading.LoadScene(text));
        Assert.Equal("res://props/vehicle.glb", PathOf(loading.Get<Furnished>(car).Model));

        // Saving it again writes the new path beside the same UID.
        Assert.Contains($"res://props/vehicle.glb|{_carUid}", loading.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAssetThatCannotBeFoundLeavesTheFieldEmptyAndTheSceneLoads()
    {
        var sink = new Sink();
        Log.AddSink(sink);
        try
        {
            using World world = NewWorld();
            List<Entity> loaded = world.LoadScene("""
                <Scene version="1">
                  <Entity id="1" name="lost"><Furnished Count="7" Model="res://models/gone.glb|uid://0000000000zzz"/></Entity>
                </Scene>
                """);

            Assert.Equal(0u, world.Get<Furnished>(loaded[0]).Model);
            Assert.Equal(7, world.Get<Furnished>(loaded[0]).Count);
            Assert.Contains(sink.Warnings, w => w.Contains("gone.glb", StringComparison.Ordinal));
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    public void AnUnregisteredAssetTypeIsSkippedWithAWarning()
    {
        var sink = new Sink();
        Log.AddSink(sink);
        try
        {
            using World world = NewWorld(registered: false);
            Entity a = world.CreateEntity("a");
            world.Set(a, new Furnished { Count = 3, Model = 9 });
            Entity b = world.CreateEntity("b");
            world.Set(b, new Furnished { Count = 4, Model = 10 });

            string text = world.SaveScene();

            Assert.DoesNotContain("Model=", text, StringComparison.Ordinal); // left out, not the number of this run
            Assert.Contains("Count=\"3\"", text, StringComparison.Ordinal);
            string[] warnings = [.. sink.Warnings.Where(w => w.Contains("Furnished.Model", StringComparison.Ordinal))];
            Assert.Single(warnings); // once, however many entities
            Assert.Contains("'model'", warnings[0], StringComparison.Ordinal);

            // Loading leaves the field empty too.
            using World fresh = NewWorld(registered: false);
            List<Entity> loaded = fresh.LoadScene("""
                <Scene version="1">
                  <Entity id="1" name="x"><Furnished Count="5" Model="res://models/car.glb"/></Entity>
                </Scene>
                """);
            Assert.Equal(0u, fresh.Get<Furnished>(loaded[0]).Model);
            Assert.Equal(5, fresh.Get<Furnished>(loaded[0]).Count);
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    public void AScriptComponentsAssetFieldWithoutATypeIsLeftOut()
    {
        using World world = NewWorld();
        Entity decorated = world.DefineComponent("Decorated", [("look", FieldKind.AssetRef)]);
        Entity e = world.CreateEntity("e");
        unsafe
        {
            byte* data = world.EnsurePointer(e, decorated, 4);
            *(uint*)data = 5;
        }

        Assert.DoesNotContain("look=\"", world.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void AssetHandlesConvertThroughTheRegisteredType()
    {
        using World world = NewWorld();
        uint car = Handle("res://models/car.glb");
        AssetRef carRef = _assets.RefOf("res://models/car.glb");

        // Handle to reference: what a scene would save for it.
        Assert.Equal(carRef, world.AssetRefOf("model", car));
        Assert.Equal(_carUid, world.AssetRefOf("model", car)!.Value.Uid);

        // Reference to handle: the same handle back, from the full reference, from the path alone and from the UID alone.
        Assert.Equal(car, world.AssetHandleOf("model", carRef));
        Assert.Equal(car, world.AssetHandleOf("model", AssetRef.FromPath("res://models/car.glb")));
        Assert.Equal(car, world.AssetHandleOf("model", AssetRef.FromUid(_carUid)));
    }

    [Fact]
    public void AnUnknownTypeAHandleOfZeroAndAnEmptyReferenceConvertToNothing()
    {
        var sink = new Sink();
        Log.AddSink(sink);
        try
        {
            using World world = NewWorld();
            uint car = Handle("res://models/car.glb");
            AssetRef carRef = _assets.RefOf("res://models/car.glb");

            Assert.Null(world.AssetRefOf("texture", car));        // nobody registered it
            Assert.Equal(0u, world.AssetHandleOf("texture", carRef));
            Assert.Null(world.AssetRefOf("model", 0));            // 0 is no asset: the converter is not asked
            Assert.Null(world.AssetRefOf("model", 99));           // a handle the library does not know
            Assert.Equal(0u, world.AssetHandleOf("model", default));
            Assert.Equal(0u, world.AssetHandleOf("model", AssetRef.FromPath("res://models/gone.glb")));   // the converter finds nothing
            Assert.Null(world.AssetRefOf("", car));
            Assert.Equal(0u, world.AssetHandleOf("", carRef));

            using World bare = NewWorld(registered: false);
            Assert.Null(bare.AssetRefOf("model", car));
            Assert.Equal(0u, bare.AssetHandleOf("model", carRef));

            Assert.Empty(sink.Warnings);                          // asking is not a scene: no warning per call
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    public void TheTextOfAReferenceThatDidNotLoadCanBeReadAndForgotten()
    {
        using World world = NewWorld();
        Entity lost = world.LoadScene("""
            <Scene version="1">
              <Entity id="1" name="lost"><Furnished Count="7" Model="res://models/gone.glb|uid://0000000000zzz"/></Entity>
            </Scene>
            """)[0];

        Assert.Equal("res://models/gone.glb|uid://0000000000zzz", world.UnresolvedAssetOf(lost, "Furnished", "Model"));
        Assert.Null(world.UnresolvedAssetOf(lost, "Furnished", "Count"));
        Assert.Contains("gone.glb", world.SaveScene(), StringComparison.Ordinal);       // saving brings the reference back

        // An editor that clears the field forgets the text: the next save leaves the attribute out.
        Assert.True(world.SetUnresolvedAsset(lost, "Furnished", "Model", null));
        Assert.False(world.SetUnresolvedAsset(lost, "Furnished", "Model", null));
        Assert.Null(world.UnresolvedAssetOf(lost, "Furnished", "Model"));
        string cleared = world.SaveScene();
        Assert.DoesNotContain("gone.glb", cleared, StringComparison.Ordinal);
        Assert.Contains("Count=\"7\"", cleared, StringComparison.Ordinal);

        // And undoing it puts the same text back.
        Assert.True(world.SetUnresolvedAsset(lost, "Furnished", "Model", "res://models/gone.glb|uid://0000000000zzz"));
        Assert.Contains("res://models/gone.glb|uid://0000000000zzz", world.SaveScene(), StringComparison.Ordinal);
    }

    [Fact]
    public void AConverterThatThrowsGivesHandleZeroAndAWarning()
    {
        var sink = new Sink();
        Log.AddSink(sink);
        try
        {
            using var world = new World();
            world.RegisterAssetType("model", _ => null, _ => throw new FileNotFoundException("no such model"));
            Assert.Equal(0u, world.AssetHandleOf("model", AssetRef.FromPath("res://models/car.glb")));
            Assert.Contains(sink.Warnings, w => w.Contains("no such model", StringComparison.Ordinal));
            Assert.Null(world.AssetRefOf("model", 3));   // a converter that names nothing
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }
}
