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
using Flecs = Weft.Native.Flecs.Ecs;

namespace Weft.Ecs.Tests;

/// <summary>
/// A world with files for the tests of prefab instances in scenes: two prefab files (a lamp, and a corner that holds two lamps), a library and an asset database over a scratch folder, and the
/// helpers to write files, read them back and listen to warnings.
/// </summary>
public abstract class PrefabTestBase : IDisposable
{
    protected sealed class Sink : ILogSink
    {
        public List<string> Warnings { get; } = [];

        public void Write(in LogEntry entry)
        {
            if (entry.Level == LogLevel.Warning)
            {
                lock (Warnings)
                {
                    Warnings.Add(entry.Message);
                }
            }
        }
    }

    protected const string LampPath = "res://prefabs/lamp.aprefab";
    protected const string CornerPath = "res://prefabs/corner.aprefab";

    private readonly string _folder = Directory.CreateTempSubdirectory("weft-prefab-scenes").FullName;

    protected PrefabTestBase()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        Files = new VirtualFileSystem();
        Faults = new FaultySource(new DirectorySource(_folder, writable: true));
        Files.Mount("res://", Faults);
        Assets = new AssetDatabase(Files);
        World = new World { Files = Files, Assets = Assets };
        Write("prefabs/lamp.aprefab", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Prefab version="1" uid="{LampUid}" nextId="4">
              <Components>
                <Component name="Flicker"><Field name="speed" kind="Float32"/><Field name="lamp" kind="Entity"/></Component>
              </Components>
              <Entity id="1" name="Lamp">
                <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
                <Entity id="2" name="Bulb">
                  <Transform Position="0 1.8 0" Rotation="0 0 0 1" Scale="1 1 1"/>
                  <Health Current="5" Max="9" Regen="0.5"/>
                  <Marks Alive="true" Level="2" Target="1" Mass="1.5"/>
                  <Flicker speed="2" lamp="1"/>
                </Entity>
                <Entity id="3" name="Base"><Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
              </Entity>
            </Prefab>
            """);
        Write("prefabs/corner.aprefab", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Prefab version="1" uid="{CornerUid}" nextId="5">
              <Entity id="1" name="Corner">
                <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
                <Entity id="2" name="Lamp" prefab="{LampPath}|{LampUid}">
                  <Transform Position="2 0 0"/>
                  <Override entity="2"><Health Current="9"/></Override>
                </Entity>
                <Entity id="3" name="Lamp 2" prefab="{LampPath}|{LampUid}">
                  <Transform Position="-2 0 0"/>
                  <Override entity="3" remove="true"/>
                </Entity>
                <Entity id="4" name="Bench"><Transform Position="0 0 3" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
              </Entity>
            </Prefab>
            """);
    }

    protected VirtualFileSystem Files { get; }

    /// <summary>The source of <c>res://</c>: writes through it can be made to fail, to see what a failing disk leaves.</summary>
    protected FaultySource Faults { get; }

    /// <summary>The folder <c>res://</c> is.</summary>
    protected string Folder => _folder;

    protected AssetDatabase Assets { get; }

    protected World World { get; }

    protected AssetUid LampUid { get; } = AssetUid.New();

    protected AssetUid CornerUid { get; } = AssetUid.New();

    public void Dispose()
    {
        World.Dispose();
        Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The prefab as a scene's instance element names it: path and UID.</summary>
    protected string Lamp => $"{LampPath}|{LampUid}";

    protected string Corner => $"{CornerPath}|{CornerUid}";

    protected void Write(string path, string text)
    {
        string full = Path.Combine(_folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    protected void Delete(string path) => File.Delete(Path.Combine(_folder, path));

    protected string Read(string path) => File.ReadAllText(Path.Combine(_folder, path));

    /// <summary>A second world over the same files, with a library of its own (its handles number differently).</summary>
    protected World OtherWorld() => new() { Files = Files, Assets = Assets };

    protected static Entity Child(World world, Entity parent, string name) => world.Children(parent).Single(c => world.GetName(c) == name);

    protected Entity Child(Entity parent, string name) => Child(World, parent, name);

    /// <summary>The names of the children of an entity, in order.</summary>
    protected static string[] NamesBelow(World world, Entity parent) => [.. world.Children(parent).Select(c => world.GetName(c) ?? "-")];

    protected static T WithWarnings<T>(Func<T> action, out List<string> warnings)
    {
        var sink = new Sink();
        Log.AddSink(sink);
        try
        {
            return action();
        }
        finally
        {
            Log.RemoveSink(sink);
            warnings = sink.Warnings;
        }
    }

    protected static unsafe ulong EntityField(World world, Entity entity, string component, string field)
    {
        Entity id = world.FindComponent(component);
        ComponentInfo info = world.GetComponentInfo(id)!;
        return *(ulong*)(world.GetPointer(entity, id) + info.FindField(field)!.Offset);
    }
}
