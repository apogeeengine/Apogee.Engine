using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Core.Logging;
using Apogee.DevTools;
using Apogee.Graphics;
using Apogee.Graphics.Shaders;
using Apogee.Native.CImGui;
using Apogee.Platform;
using Apogee.Rendering;
using Apogee.Rendering.Shaders;
using Sdl = Apogee.Native.Sdl;

namespace Apogee.Samples.Viewer;

/// <summary>What both hosts need: the scene, its lights and the camera, built from the options.</summary>
internal sealed class ViewerScene : IDisposable
{
    private readonly List<LightData> _scratch = [];
    private readonly LightShow? _show;

    public ViewerScene(SceneRenderer renderer, Options options, TextureCaps caps)
    {
        var contentOptions = new ContentOptions { TextureCaps = caps };
        ModelData model;
        if (options.Scene == "courtyard")
        {
            model = CourtyardScene.Build(options.NoModels ? null : CourtyardScene.FindAssets(options.AssetsDirectory), contentOptions);
        }
        else
        {
            model = GltfLoader.Load(options.Scene, contentOptions);
        }

        Scene = renderer.CreateScene(model);
        Camera = new Camera();
        if (options.Camera is { } start)
        {
            Camera.Position = start.Position;
            Camera.Yaw = start.Yaw;
            Camera.Pitch = start.Pitch;
        }
        else if (options.Scene == "courtyard")
        {
            Camera.Position = new Vector3(-20.5f, 2.3f, 0.6f);
            Camera.Yaw = -MathF.PI / 2f;
            Camera.Pitch = -0.04f;
        }
        else
        {
            Vector3 center = (model.BoundsMin + model.BoundsMax) * 0.5f;
            Vector3 size = model.BoundsMax - model.BoundsMin;
            Camera.Position = center + new Vector3(0, size.Y * 0.2f, MathF.Max(size.Length() * 0.8f, 1f));
            Camera.LookAt(center);
            Camera.Near = MathF.Max(size.Length() * 0.001f, 0.01f);
            Camera.ClusterFar = MathF.Max(size.Length() * 3f, 50f);
        }

        Model = model;
        if (options.Lights > 0)
        {
            Vector3 min = model.BoundsMin;
            Vector3 max = model.BoundsMax;
            _show = new LightShow(options.Lights, min, new Vector3(max.X, MathF.Min(max.Y, min.Y + 6f), max.Z));
        }

        Scene.SunColor = options.Scene == "courtyard" ? new Vector3(0.6f, 0.55f, 0.5f) : new Vector3(2.5f);
        Scene.SunDirection = Vector3.Normalize(new Vector3(0.35f, 1f, 0.25f));
        Scene.AmbientSky = new Vector3(0.1f, 0.12f, 0.17f);
        Scene.AmbientGround = new Vector3(0.05f, 0.045f, 0.04f);
    }

    public ModelData Model { get; }

    public RenderScene Scene { get; }

    public Camera Camera { get; }

    public int LightCount => _show?.Count ?? Scene.Lights.Count;

    /// <summary>Moves the animated lights to where they are at <paramref name="seconds"/>.</summary>
    public void Animate(float seconds)
    {
        if (_show is null)
        {
            return;
        }

        _show.Update(seconds, _scratch);
        Scene.Lights.Clear();
        Scene.Lights.AddRange(Model.Lights);
        Scene.Lights.AddRange(_scratch);
    }

    public void Dispose() => Scene.Dispose();
}

/// <summary>A window, a swapchain, the fly camera and the dev overlay.</summary>
internal static class Viewer
{
    public static int Run(Options options, string shaderCache)
    {
        PlatformHost.Initialize();
        try
        {
            using var window = new Window("Apogee viewer", options.Width, options.Height, WindowOptions.Resizable | WindowOptions.HighPixelDensity | GraphicsDevice.RequiredWindowOptions());
            using var device = GraphicsDevice.Create(new GraphicsDeviceOptions { ApplicationName = "Apogee viewer", Window = window, PreferredDeviceName = options.Gpu });
            using var swapchain = device.CreateSwapchain(options.VSync);
            using var loop = device.CreateFrameLoop(swapchain);
            // With --watch the shaders are the repository's source files and saving one rebuilds the pipelines that use it; otherwise the compiled ones next to the program.
            var shaderFiles = new VirtualFileSystem();
            ShaderLibrary.MountEngineShaders(shaderFiles, sources: options.Watch);
            var shaders = new ShaderLibrary(shaderFiles, new ShaderCompilerOptions { CacheDirectory = shaderCache });
            using VfsWatcher? shaderWatcher = options.Watch ? new VfsWatcher(shaderFiles) : null;
            using var renderer = new SceneRenderer(device, shaders);
            using var scene = new ViewerScene(renderer, options, new TextureCaps(device.SupportsBc, device.SupportsAstc));
            using var imgui = new ImGuiHost();
            imgui.Attach(window);
            using var overlayRenderer = new ImGuiRenderer(device, shaders);
            var overlay = new DevOverlay();
            var settings = new RenderSettings();
            using var input = new InputState(window.Id);
            var fly = new FlyCamera(scene.Camera);

            Log.Info("Viewer", $"{scene.Model.Name}: {scene.Model.Instances.Count} instances, {scene.Model.Textures.Count} textures, {scene.LightCount} lights on {device.Name}");

            var clock = Stopwatch.StartNew();
            double previous = 0;
            double cpuMs = 0;
            int frames = 0;
            while (!PlatformHost.QuitRequested && !window.CloseRequested && (options.Frames is null || frames < options.Frames))
            {
                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)(now - previous);
                previous = now;
                var cpuStart = Stopwatch.GetTimestamp();

                PlatformHost.PumpEvents();
                if (input.Pressed(Sdl.Scancode.Escape) && !imgui.WantsKeyboard)
                {
                    break;
                }

                if (input.Pressed(Sdl.Scancode.F1))
                {
                    overlay.Visible = !overlay.Visible;
                }

                (float dx, float dy) = input.TakeMouseDelta();
                bool look = input.RightButton && !imgui.WantsMouse;
                window.SetMouseLook(look);
                Vector3 move = imgui.WantsKeyboard ? Vector3.Zero : new Vector3(
                    (input.Down(Sdl.Scancode.D) ? 1 : 0) - (input.Down(Sdl.Scancode.A) ? 1 : 0),
                    (input.Down(Sdl.Scancode.E) ? 1 : 0) - (input.Down(Sdl.Scancode.Q) ? 1 : 0),
                    (input.Down(Sdl.Scancode.W) ? 1 : 0) - (input.Down(Sdl.Scancode.S) ? 1 : 0));
                fly.Update(dt, move, input.Down(Sdl.Scancode.Lshift), look ? dx : 0f, look ? dy : 0f, imgui.WantsMouse ? 0f : input.TakeWheel());
                scene.Animate((float)now);
                if (shaderWatcher is not null)
                {
                    shaders.NotifyChanged(shaderWatcher.TakeChanges());
                }

                imgui.BeginFrame(Math.Max(dt, 1e-4f));
                overlay.Draw(new DevOverlayData
                {
                    GpuName = device.Name,
                    CpuFrameMilliseconds = cpuMs,
                    GpuTimings = loop.Timings,
                    Plan = renderer.LastPlan,
                    Stats = renderer.LastStats,
                    Settings = settings,
                    Camera = scene.Camera,
                    Scene = scene.Scene,
                    ShaderErrors = shaders.Errors,
                });
                imgui.EndFrame();

                Frame? frame = loop.BeginFrame();
                if (frame is not null)
                {
                    renderer.Render(frame, scene.Scene, scene.Camera, RenderTarget.Window(frame.Backbuffer!.Value), settings, overlayRenderer);
                    loop.EndFrame(frame);
                    frames++;
                }

                cpuMs = (Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency;
            }

            loop.WaitIdle();
            window.SetMouseLook(false);
            if (device.ValidationErrorCount > 0)
            {
                string[] errors = device.TakeValidationErrors();
                Console.Error.WriteLine($"{errors.Length} validation error(s); the first:\n{errors[0]}");
                return 3;
            }

            return 0;
        }
        finally
        {
            PlatformHost.Shutdown();
        }
    }
}

/// <summary>Keys, the right mouse button, the pointer's movement and the wheel, collected from SDL events.</summary>
internal sealed unsafe class InputState : IDisposable
{
    private readonly uint _windowId;
    private readonly HashSet<Sdl.Scancode> _down = [];
    private readonly HashSet<Sdl.Scancode> _pressed = [];
    private float _dx;
    private float _dy;
    private float _wheel;

    public InputState(uint windowId)
    {
        _windowId = windowId;
        PlatformHost.RawEvent += OnEvent;
    }

    public bool RightButton { get; private set; }

    public bool Down(Sdl.Scancode key) => _down.Contains(key);

    /// <summary>True once per key press, after the events of the frame were pumped.</summary>
    public bool Pressed(Sdl.Scancode key) => _pressed.Remove(key);

    public (float X, float Y) TakeMouseDelta()
    {
        (float, float) delta = (_dx, _dy);
        _dx = _dy = 0f;
        return delta;
    }

    public float TakeWheel()
    {
        float wheel = _wheel;
        _wheel = 0f;
        return wheel;
    }

    private void OnEvent(in Sdl.Event e)
    {
        switch ((Sdl.EventType)e.Type)
        {
            case Sdl.EventType.KeyDown when e.Key.WindowID == _windowId && !e.Key.Repeat:
                _down.Add(e.Key.Scancode);
                _pressed.Add(e.Key.Scancode);
                break;
            case Sdl.EventType.KeyUp when e.Key.WindowID == _windowId:
                _down.Remove(e.Key.Scancode);
                break;
            case Sdl.EventType.MouseMotion when e.Motion.WindowID == _windowId:
                _dx += e.Motion.Xrel;
                _dy += e.Motion.Yrel;
                break;
            case Sdl.EventType.MouseButtonDown or Sdl.EventType.MouseButtonUp when e.Button.WindowID == _windowId && e.Button.Button == 3:
                RightButton = (Sdl.EventType)e.Type == Sdl.EventType.MouseButtonDown;
                break;
            case Sdl.EventType.MouseWheel when e.Wheel.WindowID == _windowId:
                _wheel += e.Wheel.Y;
                break;
            case Sdl.EventType.WindowFocusLost when e.Window.WindowID == _windowId:
                _down.Clear();
                RightButton = false;
                break;
        }
    }

    public void Dispose() => PlatformHost.RawEvent -= OnEvent;
}
