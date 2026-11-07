using System;
using System.Linq;
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

        // Scenes are read and written from the registry's field descriptions with Utf8JsonWriter and JsonDocument, which must work with no reflection: save, load into a new world, compare.
        string scene = world.SaveScene();
        using var loaded = new World();
        Entity restored = loaded.LoadScene(scene).Single();
        if (loaded.GetName(restored) != "smoke" || loaded.Get<Ticks>(restored).Count != 3 || !world.IsUserEntity(entity) || world.Roots().Count != 1)
        {
            throw new InvalidOperationException("The scene smoke test failed.");
        }

        Log.Info("Runtime", $"Scene: {scene.Length} characters, loaded back");
    }
}
