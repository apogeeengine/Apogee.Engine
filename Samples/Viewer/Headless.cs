using Apogee.Core.Graphics;
using System;
using System.Diagnostics;
using System.IO;
using Apogee.Content;
using Apogee.Core.Logging;
using Apogee.DevTools;
using Apogee.Graphics;
using Apogee.Rendering;

namespace Apogee.Samples.Viewer;

/// <summary>Renders without a window into an image: screenshots, the CI comparison, and servers. Frames advance by a fixed 1/60 s, so a frame number always looks the same.</summary>
internal static class Headless
{
    public static int Run(Options options, string shaderCache)
    {
        using var device = GraphicsDevice.Create(new GraphicsDeviceOptions { ApplicationName = "Apogee viewer", PreferredDeviceName = options.Gpu });
        using var loop = device.CreateFrameLoop();
        using var renderer = new SceneRenderer(device, cacheDirectory: shaderCache);
        using var scene = new ViewerScene(renderer, options, new TextureCaps(device.SupportsBc, device.SupportsAstc));
        using GpuImage target = device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, (uint)options.Width, (uint)options.Height, ImageUsage.ColorAttachment), "Headless target");
        using ImGuiHost? imgui = options.Overlay ? new ImGuiHost() : null;
        using ImGuiRenderer? overlayRenderer = options.Overlay ? new ImGuiRenderer(device, cacheDirectory: shaderCache) : null;
        var overlay = new DevOverlay();
        var settings = new RenderSettings();

        int frames = options.Frames ?? 1;
        Log.Info("Viewer", $"{scene.Model.Name}: {scene.Model.Instances.Count} instances, {scene.Model.Textures.Count} textures, {scene.LightCount} lights on {device.Name}; {frames} frame(s) at {options.Width}x{options.Height}");

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            scene.Animate(i / 60f);
            if (imgui is not null)
            {
                imgui.BeginFrame(options.Width, options.Height, 1f, 1f / 60f);
                overlay.Draw(new DevOverlayData
                {
                    GpuName = device.Name,
                    CpuFrameMilliseconds = 16,
                    GpuTimings = loop.Timings,
                    Plan = renderer.LastPlan,
                    Stats = renderer.LastStats,
                    Settings = settings,
                    Camera = scene.Camera,
                    Scene = scene.Scene,
                });
                imgui.EndFrame();
            }

            Frame frame = loop.BeginFrame()!;
            renderer.Render(frame, scene.Scene, scene.Camera, RenderTarget.Offscreen(target), settings, overlayRenderer);
            loop.EndFrame(frame);
        }

        loop.WaitIdle();
        double milliseconds = clock.Elapsed.TotalMilliseconds / frames;
        byte[] pixels = device.ReadImage(target, ImageState.TransferSource);
        Console.WriteLine($"Rendered {frames} frame(s), {milliseconds:F1} ms per frame, {renderer.LastStats!.Draws} draws, {renderer.LastStats.Lights} lights");

        if (options.Screenshot is not null)
        {
            File.WriteAllBytes(options.Screenshot, Png.Encode(options.Width, options.Height, pixels));
            Console.WriteLine($"Wrote {options.Screenshot}");
        }

        if (device.ValidationErrorCount > 0)
        {
            string[] errors = device.TakeValidationErrors();
            Console.Error.WriteLine($"{errors.Length} validation error(s); the first:\n{errors[0]}");
            return 3;
        }

        return 0;
    }
}
