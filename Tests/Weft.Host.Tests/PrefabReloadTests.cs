using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Weft.Core.Numerics;
using Weft.Ecs;
using Weft.Native.Tests;
using Weft.Reflection;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;
using Lua = Weft.Native.Luau.Lua;

namespace Weft.Host.Tests;

/// <summary>A game that is run with watching follows its prefab files: a prefab that is edited on disk changes its instances in the running world.</summary>
public sealed class PrefabReloadTests : IDisposable
{
    private const string LampPath = "res://packages/core/prefabs/lamp.aprefab";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "weft-prefab-reload-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public PrefabReloadTests()
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a scratch folder: the next run's names differ
        }
    }

    private static string Lamp(double height) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Prefab version="1" nextId="3">
          <Entity id="1" name="Lamp">
            <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
            <Entity id="2" name="Bulb"><Transform Position="0 {height.ToString(System.Globalization.CultureInfo.InvariantCulture)} 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
          </Entity>
        </Prefab>
        """;

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void AWatchedSessionReloadsAChangedPrefab()
    {
        Write("game/packages/core/mod.xml", "<Mod id=\"core\" version=\"1.0.0\"/>");
        string file = Write("game/packages/core/prefabs/lamp.aprefab", Lamp(1.8));
        string user = Path.Combine(_root, "user").Replace('\\', '/');
        string scene = $"""
            <Scene version="2">
              <Entity id="1" name="One" prefab="{LampPath}"/>
              <Entity id="2" name="Two" prefab="{LampPath}"><Override entity="2"><Transform Position="0 5 0"/></Override></Entity>
            </Scene>
            """;

        using ScriptSession session = ScriptSession.StartGame(Path.Combine(_root, "game").Replace('\\', '/'), user, watch: true, runScripts: false, scene: scene);
        World world = session.World;
        Entity one = world.Lookup("One");
        Entity bulb = world.Children(one).Single();
        Entity two = world.Lookup("Two");
        Entity otherBulb = world.Children(two).Single();
        Assert.Equal(1.8, world.Get<Transform>(bulb).Position.Y);
        Assert.Equal(5.0, world.Get<Transform>(otherBulb).Position.Y);
        var updates = 0;
        world.PrefabsChanged += _ => updates++;

        File.WriteAllText(file, Lamp(3.5));
        var timeout = Stopwatch.StartNew();
        while (world.Get<Transform>(bulb).Position.Y != 3.5 && timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            session.Reload(); // the watcher waits until the file has been quiet for a moment
            Thread.Sleep(20);
        }

        Assert.Equal(3.5, world.Get<Transform>(bulb).Position.Y);
        Assert.Equal(bulb, world.Children(one).Single());                     // the same entity: the instance was changed in place
        Assert.Equal(5.0, world.Get<Transform>(otherBulb).Position.Y);       // what an instance changed is kept
        Assert.True(updates >= 1);

        // A file that is cut off in the middle keeps the version the world has.
        File.WriteAllText(file, "<Prefab version=\"1\"><Entity id=\"1\" name=\"Lamp\">");
        for (int i = 0; i < 40; i++)
        {
            session.Reload();
            Thread.Sleep(20);
        }

        Assert.Equal(3.5, world.Get<Transform>(bulb).Position.Y);
    }

    [Fact]
    public void AnUnwatchedSessionLeavesItsInstancesAlone()
    {
        Write("game/packages/core/mod.xml", "<Mod id=\"core\" version=\"1.0.0\"/>");
        string file = Write("game/packages/core/prefabs/lamp.aprefab", Lamp(1.8));
        string scene = $"<Scene version=\"2\"><Entity id=\"1\" name=\"One\" prefab=\"{LampPath}\"/></Scene>";

        using ScriptSession session = ScriptSession.StartGame(Path.Combine(_root, "game").Replace('\\', '/'), Path.Combine(_root, "user").Replace('\\', '/'), watch: false, runScripts: false, scene: scene);
        File.WriteAllText(file, Lamp(3.5));
        session.Reload();

        Assert.Null(session.Reloader);
        Assert.Equal(1.8, session.World.Get<Transform>(session.World.Children(session.World.Lookup("One")).Single()).Position.Y);
    }
}
