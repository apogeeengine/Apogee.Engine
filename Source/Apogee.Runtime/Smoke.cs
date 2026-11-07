using System;
using System.Runtime.InteropServices;
using Apogee.Core.Logging;
using Apogee.Native.Vulkan;
using Flecs = Apogee.Native.Flecs.Ecs;
using ImGui = Apogee.Native.CImGui.ImGui;
using Lua = Apogee.Native.Luau.Lua;
using SDL = Apogee.Native.Sdl.SDL;
using Jolt = Apogee.Native.Jolt.Jolt;

namespace Apogee.Runtime;

/// <summary>What the runtime does without a command.</summary>
internal static unsafe class Smoke
{
    public static void Run()
    {
        Log.Info("Runtime", $"SDL {SDL.GetVersion()}, Dear ImGui {Marshal.PtrToStringUTF8((IntPtr)ImGui.GetVersion())}");

        Apogee.Native.Flecs.World* world = Flecs.Init();
        Log.Info("Runtime", $"Flecs world {(world != null ? "created" : "failed")}");
        int finished = Flecs.Fini(world);
        Log.Debug("Runtime", $"Flecs fini returned {finished}");

        EcsSmoke.Run();
        ScriptSmoke.Run();

        Apogee.Native.Luau.State* lua = Lua.NewVm(0);
        Log.Info("Runtime", $"Luau state {(lua != null ? "created" : "failed")}");
        Lua.CloseVm(lua);

        JoltSmoke();
        Log.Info("Runtime", Apogee.Interop.NativeLibraries.TryLoad(typeof(Apogee.Native.OpenAL.OpenAL).Assembly, "openal", out _)
            ? "OpenAL Soft is installed"
            : "OpenAL Soft is not installed (ThirdParty/openal-soft/lib): audio stays silent");

        Log.Info("Runtime", Apogee.Interop.NativeLibraries.TryLoad(typeof(Apogee.Native.Net.Net).Assembly, "apogee_net", out _)
            ? "GameNetworkingSockets is installed"
            : "GameNetworkingSockets is not installed (ThirdParty/gns/lib): networking stays off");

        Log.Info("Runtime", Apogee.Interop.NativeLibraries.TryLoad(typeof(Apogee.Native.Steam.Steam).Assembly, "steam_api", out _)
            ? $"Steam API library loaded (client {(Apogee.Native.Steam.Steam.IsSteamRunning() ? "running" : "not running")})"
            : "Steam API library not found: Steam features stay off");

        var global = new VkGlobalCommands();
        uint version;
        VkResult result = global.EnumerateInstanceVersion(&version);
        Log.Info("Runtime", $"Vulkan {result}, instance version {version >> 22}.{(version >> 12) & 0x3ff}");
    }

    /// <summary>Jolt under NativeAOT: our C API through function pointers, the thread pool, a double-precision body that falls a step.</summary>
    private static void JoltSmoke()
    {
        if (!Jolt.Init())
        {
            Log.Error("Runtime", "Jolt did not initialize");
            return;
        }

        var desc = new Apogee.Native.Jolt.AjWorldDesc
        {
            MaxBodies = 16,
            MaxBodyPairs = 64,
            MaxContactConstraints = 64,
            TempBufferBytes = 1024 * 1024,
            Threads = 1,
            EventCapacity = 16,
            VelocitySteps = 10,
            PositionSteps = 2,
            Gravity = new Apogee.Native.Jolt.AjVec3 { Y = -9.81f },
        };
        Apogee.Native.Jolt.AjWorld* world = Jolt.WorldCreate(&desc);
        Apogee.Native.Jolt.AjShape* sphere = Jolt.ShapeSphere(0.5f);
        var body = new Apogee.Native.Jolt.AjBodyDesc
        {
            Shape = sphere,
            Position = new Apogee.Native.Jolt.AjRVec3 { X = 1e9, Y = 1e9, Z = 0 },
            Rotation = new Apogee.Native.Jolt.AjQuat { W = 1 },
            Motion = Apogee.Native.Jolt.AjMotionType.Dynamic,
            Layers = 1,
            Mask = Jolt.LayerBits,
            Friction = 0.5f,
            GravityFactor = 1,
            AllowSleeping = true,
        };
        uint id = Jolt.BodyCreate(world, &body);
        uint error = Jolt.WorldUpdate(world, 1f / 60);
        if (error != 0)
        {
            Log.Error("Runtime", $"Jolt update error {error}");
        }

        Apogee.Native.Jolt.AjRVec3 fallen;
        Apogee.Native.Jolt.AjQuat rotation;
        Jolt.BodyGetPose(world, id, &fallen, &rotation);
        Log.Info("Runtime", $"Jolt: a body at 1000000000 fell {body.Position.Y - fallen.Y:0.0000} m in one step");

        Jolt.ShapeRelease(sphere);
        Jolt.WorldDestroy(world);
        Jolt.Shutdown();
    }
}
