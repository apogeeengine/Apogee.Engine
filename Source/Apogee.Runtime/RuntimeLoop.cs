using Apogee.Core.Graphics;
using Apogee.Host;
using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Apogee.Content;
using Apogee.Core.Logging;
using Apogee.DevTools;
using Apogee.Graphics;
using Apogee.Platform;
using Apogee.Rendering;
using Apogee.UI;
using Apogee.Rendering.Graph;
using Apogee.Rendering.Shaders;
using Sdl = Apogee.Native.Sdl;

namespace Apogee.Runtime;

/// <summary>
/// The frame loops of <c>--run</c>. Each frame steps the world, then runs an ImGui frame in which the scripts' <c>devtools.on_gui</c> functions draw,
/// then (unless there is no GPU) draws the result: over the world seen through its main camera when it has one (<see cref="WorldView"/>), over a cleared background otherwise. The three loops differ only in where it goes: a window, an image file,
/// or nowhere.
/// </summary>
internal static class RuntimeLoop
{
    private static readonly Vector4 Background = new(0.07f, 0.08f, 0.1f, 1f);

    /// <summary>The switches the overlay offers for the session: what shows how the game is built, drawn over the world.</summary>
    private static OverlayToggle[] Toggles(ScriptSession session) =>
        [new OverlayToggle("Physics shapes", () => session.Physics.DebugDraw, value => session.Physics.DebugDraw = value)];

    private static string ShaderCache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apogee", "ShaderCache");

    /// <summary>No window and no GPU: the frames run, nothing is shown. What runs everywhere, CI machines without Vulkan included.</summary>
    public static int Headless(RuntimeOptions options, ImGuiHost host, ScriptSession session, out int frames)
    {
        frames = options.Frames ?? 1;
        for (int frame = 0; frame < frames && !GameLibrary.QuitRequested; frame++)
        {
            session.Reload();
            session.Progress(1f / 60);
            host.BeginFrame(options.Width, options.Height, 1f, 1f / 60);
            ScriptGui.RunGui();
            host.EndFrame();
            session.EndFrame();
        }

        return 0;
    }

    /// <summary>Renders into an image without a window: a fixed time step and size, so that frame n always looks the same. Writes the last frame as a PNG.</summary>
    public static int Offscreen(RuntimeOptions options, ImGuiHost host, ScriptSession session, out int frames)
    {
        using var device = GraphicsDevice.Create(new GraphicsDeviceOptions { ApplicationName = "Apogee runtime", PreferredDeviceName = options.Gpu });
        using var loop = device.CreateFrameLoop();
        using var pool = new TransientPool(device);
        ShaderLibrary shaders = session.CreateShaderLibrary(ShaderCache);
        using var renderer = new ImGuiRenderer(device, shaders);
        using var overlays = new FrameOverlay(device, shaders, session, renderer);
        using GpuImage target = device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, (uint)options.Width, (uint)options.Height, ImageUsage.ColorAttachment), "Runtime target");
        using var view = new WorldView(device, shaders, session, overlays);
        session.Ui.SetSize(options.Width, options.Height);
        var overlay = new DevOverlay();
        OverlayToggle[] toggles = Toggles(session);

        frames = options.Frames ?? 1;
        Log.Info("Runtime", $"{frames} frame(s) at {options.Width}x{options.Height} on {device.Name}");
        for (int i = 0; i < frames && !GameLibrary.QuitRequested; i++)
        {
            session.Reload();
            session.Progress(1f / 60);
            host.BeginFrame(options.Width, options.Height, 1f, 1f / 60);
            ScriptGui.RunGui();
            if (options.Overlay)
            {
                overlay.Draw(new DevOverlayData { GpuName = device.Name, CpuFrameMilliseconds = 16, GpuTimings = loop.Timings, ShaderErrors = shaders.Errors, Toggles = toggles });
            }

            host.EndFrame();

            Frame frame = loop.BeginFrame()!;
            if (!view.TryDraw(frame, RenderTarget.Offscreen(target)))
            {
                Draw(device, pool, overlays, frame, graph => graph.ImportImage("Output", target, ImageState.Undefined, ImageState.TransferSource, output: true));
            }

            loop.EndFrame(frame);
            session.EndFrame();
        }

        loop.WaitIdle();
        byte[] pixels = device.ReadImage(target, ImageState.TransferSource);
        File.WriteAllBytes(options.Screenshot!, Png.Encode(options.Width, options.Height, pixels));
        Console.WriteLine($"Wrote {options.Screenshot}");

        return ValidationResult(device);
    }

    /// <summary>A window with a swapchain: the scripts' panels over the background, with the dev overlay (F1 toggles it). Esc or closing the window ends it.</summary>
    public static int Windowed(RuntimeOptions options, ImGuiHost host, ScriptSession session, string title, out int frames)
    {
        PlatformHost.Initialize();
        try
        {
            using var window = new Window(title, options.Width, options.Height, WindowOptions.Resizable | WindowOptions.HighPixelDensity | GraphicsDevice.RequiredWindowOptions());
            using var device = GraphicsDevice.Create(new GraphicsDeviceOptions { ApplicationName = "Apogee runtime", Window = window, PreferredDeviceName = options.Gpu });
            using var swapchain = device.CreateSwapchain();
            using var loop = device.CreateFrameLoop(swapchain);
            using var pool = new TransientPool(device);
            ShaderLibrary shaders = session.CreateShaderLibrary(ShaderCache);
            using var renderer = new ImGuiRenderer(device, shaders);
            using var overlays = new FrameOverlay(device, shaders, session, renderer);
            using var view = new WorldView(device, shaders, session, overlays);
            host.Attach(window);
            using var keys = new Keys(window.Id);
            using var uiInput = new UiInput(session.Ui, window.Id);
            using var devices = new Apogee.Input.InputDevices(session.Input)
            {
                KeyboardCaptured = () => host.WantsKeyboard || session.Ui.WantsKeyboard,
                MouseCaptured = () => host.WantsMouse || session.Ui.WantsMouse,
            };
            var overlay = new DevOverlay();
            OverlayToggle[] toggles = Toggles(session);

            var clock = Stopwatch.StartNew();
            double previous = 0;
            double cpuMilliseconds = 0;
            frames = 0;
            while (!PlatformHost.QuitRequested && !GameLibrary.QuitRequested && !window.CloseRequested && (options.Frames is null || frames < options.Frames))
            {
                double now = clock.Elapsed.TotalSeconds;
                float delta = Math.Min((float)(now - previous), 0.25f);
                previous = now;
                long cpuStart = Stopwatch.GetTimestamp();

                PlatformHost.PumpEvents();
                PixelSize pixels = window.PixelSize;
                if (!pixels.IsEmpty)
                {
                    session.Ui.SetSize(pixels.Width, pixels.Height, window.PixelDensity);
                }

                uiInput.Update(window, applyCursor: !host.WantsMouse);
                if (keys.Pressed(Sdl.Scancode.Escape) && GameLibrary.EscapeQuits && !host.WantsKeyboard && !session.Ui.WantsKeyboard)
                {
                    break;
                }

                if (keys.Pressed(Sdl.Scancode.F1))
                {
                    overlay.Visible = !overlay.Visible;
                }

                session.Reload();
                session.Progress(delta);
                host.BeginFrame(Math.Max(delta, 1e-4f));
                ScriptGui.RunGui();
                overlay.Draw(new DevOverlayData { GpuName = device.Name, CpuFrameMilliseconds = cpuMilliseconds, GpuTimings = loop.Timings, ShaderErrors = shaders.Errors, Toggles = toggles });
                host.EndFrame();

                Frame? frame = loop.BeginFrame();
                if (frame is not null)
                {
                    if (!view.TryDraw(frame, RenderTarget.Window(frame.Backbuffer!.Value)))
                    {
                        Draw(device, pool, overlays, frame, graph => graph.ImportBackbuffer(frame.Backbuffer!.Value));
                    }

                    loop.EndFrame(frame);
                    frames++;
                }

                session.EndFrame();
                cpuMilliseconds = (Stopwatch.GetTimestamp() - cpuStart) * 1000.0 / Stopwatch.Frequency;
            }

            loop.WaitIdle();
            return ValidationResult(device);
        }
        finally
        {
            PlatformHost.Shutdown();
        }
    }

    /// <summary>The background cleared, then the ImGui frame drawn over it, into the image <paramref name="output"/> imports.</summary>
    private static void Draw(GraphicsDevice device, TransientPool pool, FrameOverlay renderer, Frame frame, Func<RenderGraph, ImageHandle> output)
    {
        var graph = new RenderGraph(device, pool);
        ImageHandle image = output(graph);
        graph.AddPass("Clear", PassType.Graphics, pass => pass.WriteColor(image, Background));
        renderer.AddPasses(graph, image, frame);
        graph.Execute(frame);
    }

    /// <summary>Exit code 3 when the validation layer reported errors (they are printed), 0 otherwise.</summary>
    private static int ValidationResult(GraphicsDevice device)
    {
        if (device.ValidationErrorCount == 0)
        {
            return 0;
        }

        string[] errors = device.TakeValidationErrors();
        Console.Error.WriteLine($"{errors.Length} validation error(s); the first:\n{errors[0]}");
        return 3;
    }

    /// <summary>The keys pressed in one window since they were last asked about.</summary>
    private sealed unsafe class Keys : IDisposable
    {
        private readonly uint _windowId;
        private readonly System.Collections.Generic.HashSet<Sdl.Scancode> _pressed = [];

        public Keys(uint windowId)
        {
            _windowId = windowId;
            PlatformHost.RawEvent += OnEvent;
        }

        /// <summary>True once per key press.</summary>
        public bool Pressed(Sdl.Scancode key) => _pressed.Remove(key);

        private void OnEvent(in Sdl.Event e)
        {
            if ((Sdl.EventType)e.Type == Sdl.EventType.KeyDown && e.Key.WindowID == _windowId && !e.Key.Repeat)
            {
                _pressed.Add(e.Key.Scancode);
            }
        }

        public void Dispose() => PlatformHost.RawEvent -= OnEvent;
    }
}
