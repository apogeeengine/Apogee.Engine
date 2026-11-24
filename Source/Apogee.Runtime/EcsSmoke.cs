using System;
using System.IO;
using System.Linq;
using Apogee.Content.Assets;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;
using Apogee.Core.Logging;
using Apogee.Ecs;
using Apogee.Reflection;

namespace Apogee.Runtime;

/// <summary>A component of the smoke test: a counter.</summary>
[Component]
public struct Ticks
{
#pragma warning disable CA1051 // The fields of a component are its memory layout.
    public int Count;
#pragma warning restore CA1051
}

/// <summary>
/// Runs the reflected parts under NativeAOT: the generated registration (a module initializer), a component registered from it,
/// a system whose callback Flecs calls through an UnmanagedCallersOnly function, and the reflected World class.
/// </summary>
internal static class EcsSmoke
{
    public static void Run()
    {
        using var world = new World();
        Entity entity = world.CreateEntity("smoke");
        world.Set(entity, new Ticks());
        world.AddSystem<Ticks>("Tick", Phase.OnUpdate, static (Entity _, ref Ticks ticks) => ticks.Count++);

        for (int frame = 0; frame < 3; frame++)
        {
            world.Progress(1f / 60);
        }

        int count = world.Get<Ticks>(entity).Count;
        string json = world.ToJson(world.Get<Ticks>(entity));
        ClassInfo worldClass = TypeRegistry.GetClass("World");
        Log.Info("Runtime", $"ECS: {count} ticks {json}, World has {worldClass.Methods.Length} script methods");

        if (count != 3 || worldClass.FindMethod("progress") is null)
        {
            throw new InvalidOperationException("The ECS smoke test failed.");
        }

        // Scenes are XML, read and written from the registry's field descriptions through XmlNode and FieldText, which must work with no reflection: save, load into a new world, compare.
        string scene = world.SaveScene();
        using var loaded = new World();
        Entity restored = loaded.LoadScene(scene).Single();
        if (!scene.StartsWith("<?xml", StringComparison.Ordinal) || loaded.GetName(restored) != "smoke" || loaded.Get<Ticks>(restored).Count != 3 || !world.IsUserEntity(entity) || world.Roots().Count != 1)
        {
            throw new InvalidOperationException("The scene smoke test failed.");
        }

        Log.Info("Runtime", $"Scene: {scene.Length} characters, loaded back");

        // Cooked XML is a plain byte format with no reflection: write a tree, read it back, compare the text of both.
        XmlNode tree = XmlNode.Parse("<Scene version=\"1\"><Entity id=\"1\" name=\"smoke\">caf\u00e9</Entity><Empty/></Scene>", "smoke.ascene");
        byte[] cooked = XmlBinary.Write(tree);
        XmlNode back = XmlFile.Read(cooked, "smoke.ascene");
        if (!XmlBinary.IsBinary(cooked) || back.ToXml() != tree.ToXml() || back.Children[0].Line != tree.Children[0].Line)
        {
            throw new InvalidOperationException("The cooked XML smoke test failed.");
        }

        Log.Info("Runtime", $"Cooked XML: {cooked.Length} bytes, read back");

        PrefabRoundTrip();
    }

    // A prefab is a file of the same kind: instantiate it, edit an entity of the instance, save the scene (the edit is written as an override of the prefab), load that into a new world, and do it all again
    // from the prefab cooked, which is how a shipped game holds it. Nothing here may need reflection: the expander and the diff read the registry's field descriptions and XmlNode.
    private static void PrefabRoundTrip()
    {
        const string Lamp = """
            <?xml version="1.0" encoding="utf-8"?>
            <Prefab version="1" nextId="4">
              <Entity id="1" name="Lamp">
                <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
                <Ticks Count="2"/>
                <Entity id="2" name="Bulb"><Transform Position="0 1.8 0" Rotation="0 0 0 1" Scale="1 1 1"/><Ticks Count="1"/></Entity>
                <Entity id="3" name="Base"><Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
              </Entity>
            </Prefab>
            """;

        string folder = Directory.CreateTempSubdirectory("apogee-prefab-smoke").FullName;
        try
        {
            string file = Path.Combine(folder, "lamp.aprefab");
            File.WriteAllText(file, Lamp);
            var files = new VirtualFileSystem();
            files.Mount("res://", new DirectorySource(folder, writable: true));
            var assets = new AssetDatabase(files);

            string text = SceneWithAnEditedLamp(files, assets);
            File.WriteAllBytes(file, XmlBinary.Write(XmlNode.Parse(Lamp, "lamp.aprefab")));   // the same prefab, cooked
            string cookedText = SceneWithAnEditedLamp(files, assets);

            using var again = new World { Files = files, Assets = assets };
            Entity lamp = again.LoadScene(text).First();
            Entity bulb = again.Children(lamp).First(c => again.GetName(c) == "Bulb");
            if (!text.Contains("<Override", StringComparison.Ordinal) || text != cookedText || again.SaveScene() != text || again.Get<Ticks>(bulb).Count != 5 || again.Get<Ticks>(lamp).Count != 2
                || again.PrefabOf(lamp) is null || again.Children(lamp).Count != 2)
            {
                throw new InvalidOperationException("The prefab smoke test failed.");
            }

            Log.Info("Runtime", $"Prefab: an instance saved as {text.Length} characters of scene with an override, the same from the cooked file");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string SceneWithAnEditedLamp(VirtualFileSystem files, AssetDatabase assets)
    {
        using var world = new World { Files = files, Assets = assets };
        Entity lamp = world.Instantiate("res://lamp.aprefab");
        Entity bulb = world.Children(lamp).First(c => world.GetName(c) == "Bulb");
        world.Set(bulb, new Ticks { Count = 5 });
        return world.SaveScene();
    }
}
