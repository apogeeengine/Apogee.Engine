using Apogee.Core.Graphics;
using System;
using System.Linq;
using System.Numerics;
using Apogee.Content;
using Apogee.Graphics;
using Apogee.Graphics.Tests;
using Apogee.Native.CImGui;
using Apogee.Native.Vulkan;
using Apogee.Rendering;
using Apogee.Rendering.Graph;
using Xunit;

namespace Apogee.DevTools.Tests;

[Collection("imgui")]
public sealed class ImGuiRenderingTests : IDisposable
{
    private const uint Width = 320;
    private const uint Height = 180;

    private readonly GpuFixture _gpu;
    private readonly GpuTest _guard;

    public ImGuiRenderingTests(GpuFixture gpu)
    {
        _gpu = gpu;
        _guard = new Guard(gpu);
    }

    private sealed class Guard(GpuFixture gpu) : GpuTest(gpu);

    public void Dispose()
    {
        _guard.Dispose();
        GC.SuppressFinalize(this);
    }

    private GraphicsDevice Device => _gpu.Device!;

    [Fact]
    public unsafe void TheGlyphsAreRasterizedByFreeType()
    {
        using var rig = new Rig(Device);

        byte[] pixels = rig.Frame(() =>
        {
            Ui.SetNextWindow(new Vector2(10, 10), new Vector2(200, 60), ImGuiCond.Always);
            Ui.Begin("text");
            Ui.Text("Hamburgefonstiv 0123");
            Ui.End();
        });

        // ImGui picks its default loader when the first glyph is baked; cimgui is built with FreeType enabled and our freetype library next to it.
        ImFontAtlas* atlas = rig.Host.IO->Fonts;
        Assert.StartsWith("FreeType (2.15.0", Apogee.Interop.Utf8.FromPointer(atlas->FontLoader->Name)); // "FreeType (compiled against; linked)"
        Assert.True(pixels.Distinct().Count() > 8, "the window and its text were drawn");
    }

    /// <summary>An ImGui host and renderer drawing onto an offscreen RGBA8 image, one frame at a time.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly GpuImage _target;
        private readonly FrameLoop _loop;
        private readonly TransientPool _pool;

        public Rig(GraphicsDevice device, Apogee.Rendering.Shaders.ShaderLibrary? shaders = null)
        {
            _device = device;
            Host = new ImGuiHost();
            Renderer = new ImGuiRenderer(device, shaders, cacheDirectory: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apogee-test-shader-cache"));
            _target = device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, Width, Height, ImageUsage.ColorAttachment), "overlay target");
            _loop = device.CreateFrameLoop();
            _pool = new TransientPool(device);
        }

        public ImGuiHost Host { get; }

        public ImGuiRenderer Renderer { get; }

        /// <summary>Runs one ImGui frame with <paramref name="widgets"/>, draws it over a mid-gray clear, and returns the pixels.</summary>
        public byte[] Frame(Action widgets)
        {
            Host.BeginFrame(Width, Height, 1f, 1f / 60f);
            widgets();
            Host.EndFrame();

            Frame frame = _loop.BeginFrame()!;
            var graph = new RenderGraph(_device, _pool);
            ImageHandle output = graph.ImportImage("Output", _target, ImageState.Undefined, ImageState.TransferSource, output: true);
            graph.AddPass("Clear", PassType.Graphics, pass => pass.WriteColor(output, new Vector4(0.5f, 0.5f, 0.5f, 1f)));
            Renderer.AddPasses(graph, output, frame);
            graph.Execute(frame);
            _loop.EndFrame(frame);
            _loop.WaitIdle();
            return _device.ReadImage(_target, ImageState.TransferSource);
        }

        public void Dispose()
        {
            _loop.Dispose();
            _pool.Dispose();
            _target.Dispose();
            Renderer.Dispose();
            Host.Dispose();
        }
    }

    private static int NonBackground(byte[] rgba, int x0, int y0, int x1, int y1)
    {
        int count = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * (int)Width + x) * 4;
                // The 0.5 clear is 127 or 128 depending on how the GPU rounds.
                if (rgba[i] is not (127 or 128) || rgba[i + 1] is not (127 or 128) || rgba[i + 2] is not (127 or 128))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int Count(byte[] rgba, Func<byte, byte, byte, bool> predicate)
    {
        int count = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (predicate(rgba[i], rgba[i + 1], rgba[i + 2]))
            {
                count++;
            }
        }

        return count;
    }

    private static void Window()
    {
        Ui.SetNextWindow(new Vector2(20, 20), new Vector2(140, 80), ImGuiCond.Always);
        if (Ui.Begin("Test window"))
        {
            Ui.Text("Hello, Apogee");
        }

        Ui.End();
    }

    /// <summary>A copy of the overlay's shader in a temporary folder, mounted as <c>shaders://</c>.</summary>
    private sealed class ShaderCopy : IDisposable
    {
        private readonly string _folder = System.IO.Directory.CreateTempSubdirectory("apogee-imgui-shader").FullName;

        public ShaderCopy()
        {
            Native.Tests.NativeRequirements.Require(typeof(Apogee.Native.Slang.Slang).Assembly, "slang_c");
            System.IO.File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "Shaders", "imgui.slang"), System.IO.Path.Combine(_folder, "imgui.slang"));
            var files = new Apogee.Content.FileSystem.VirtualFileSystem();
            files.Mount("shaders://", new Apogee.Content.FileSystem.DirectorySource(_folder));
            Library = new Apogee.Rendering.Shaders.ShaderLibrary(files);
        }

        public Apogee.Rendering.Shaders.ShaderLibrary Library { get; }

        public void Edit(Func<string, string> change)
        {
            string path = System.IO.Path.Combine(_folder, "imgui.slang");
            System.IO.File.WriteAllText(path, change(System.IO.File.ReadAllText(path)));
            Library.NotifyChanged(["shaders://imgui.slang"]);
        }

        public void Dispose() => System.IO.Directory.Delete(_folder, recursive: true);
    }

    private const string ImGuiReturn = "return input.color * gTextures[push.texture].Sample(gSamplers[push.sampler], input.uv);";

    [Fact]
    public void AnEditToTheOverlayShaderRebuildsItsPipelinesBeforeTheNextFrame()
    {
        using var shader = new ShaderCopy();
        using var rig = new Rig(Device, shader.Library);
        Assert.Equal(0, Count(rig.Frame(Window), (r, g, b) => r > 200 && g < 60 && b > 200));

        shader.Edit(text => text.Replace(ImGuiReturn, "float4 sampled = input.color * gTextures[push.texture].Sample(gSamplers[push.sampler], input.uv); return float4(1.0, 0.0, 1.0, sampled.a);", StringComparison.Ordinal));
        rig.Renderer.WaitForShaderRebuilds();
        byte[] pixels = rig.Frame(Window);

        Assert.Equal(1, rig.Renderer.ShaderRebuilds);
        Assert.True(Count(pixels, (r, g, b) => r > 200 && g < 60 && b > 200) > 3000, "the window is magenta now");
    }

    [Fact]
    public void ABrokenOverlayShaderKeepsTheOldOneAndTheDevOverlayShowsTheError()
    {
        using var shader = new ShaderCopy();
        using var rig = new Rig(Device, shader.Library);
        rig.Frame(Window);
        rig.Frame(Window); // the first frames of ImGui windows fade in and settle
        byte[] before = rig.Frame(Window);

        shader.Edit(text => text.Replace(ImGuiReturn, "return undefinedThing;", StringComparison.Ordinal));
        rig.Renderer.WaitForShaderRebuilds();
        byte[] kept = rig.Frame(Window);

        Assert.Equal(0, rig.Renderer.ShaderRebuilds);
        Assert.Equal(before, kept);
        Assert.Contains("undefinedThing", Assert.Single(shader.Library.Errors));

        var overlay = new DevOverlay();
        byte[] withErrors = rig.Frame(() => overlay.Draw(new DevOverlayData { ShaderErrors = shader.Library.Errors }));
        Assert.True(Count(withErrors, (r, g, b) => r > 200 && g < 150 && b < 140) > 20, "the error is drawn in red");
        byte[] withoutErrors = rig.Frame(() => overlay.Draw(new DevOverlayData()));
        Assert.Equal(0, Count(withoutErrors, (r, g, b) => r > 200 && g < 150 && b < 140));
    }

    [Fact]
    public void AWindowIsDrawnWhereItWasPlacedAndNowhereElse()
    {
        using var rig = new Rig(Device);

        byte[] pixels = rig.Frame(() =>
        {
            Ui.SetNextWindow(new Vector2(20, 20), new Vector2(140, 80), ImGuiCond.Always);
            if (Ui.Begin("Test window"))
            {
                Ui.Text("Hello, Apogee");
                float value = 0.5f;
                Ui.SliderFloat("slider", ref value, 0f, 1f);
            }

            Ui.End();
        });

        Assert.True(NonBackground(pixels, 20, 20, 160, 100) > 8000, "the window covers its area");
        Assert.Equal(0, NonBackground(pixels, 200, 0, (int)Width, (int)Height));
        Assert.Equal(0, NonBackground(pixels, 0, 120, (int)Width, (int)Height));
    }

    [Fact]
    public void TextIsDrawnFromTheFontAtlasTexture()
    {
        using var rig = new Rig(Device);
        int baseline = Device.Bindless.SampledImageCount;

        // A window without a title bar or background: only the text's glyphs remain.
        byte[] pixels = rig.Frame(() =>
        {
            Ui.SetNextWindow(new Vector2(10, 10), new Vector2(200, 40), ImGuiCond.Always);
            Ui.Begin("text only", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground);
            Ui.Text("MMMMMMMMMMMMMMMM");
            Ui.End();
        });

        int glyphPixels = NonBackground(pixels, 10, 10, 210, 50);
        Assert.InRange(glyphPixels, 200, 4000);          // some pixels, but far from the whole box
        Assert.Equal(1, rig.Renderer.TextureCount);
        Assert.Equal(baseline + 1, Device.Bindless.SampledImageCount);
    }

    [Fact]
    public void TheAtlasIsReleasedWithTheRenderer()
    {
        int baseline = Device.Bindless.SampledImageCount;
        using (var rig = new Rig(Device))
        {
            rig.Frame(() =>
            {
                Ui.Begin("w");
                Ui.Text("x");
                Ui.End();
            });
            Assert.Equal(baseline + 1, Device.Bindless.SampledImageCount);
        }

        Assert.Equal(baseline, Device.Bindless.SampledImageCount);
    }

    [Fact]
    public void ManyWidgetsGrowTheBuffersAndStillRender()
    {
        using var rig = new Rig(Device);

        for (int frame = 0; frame < 3; frame++)
        {
            int count = 200 * (frame + 1) * (frame + 1);      // each frame draws more than the last, so the buffers have to grow
            byte[] pixels = rig.Frame(() =>
            {
                Ui.SetNextWindow(new Vector2(0, 0), new Vector2(Width, Height), ImGuiCond.Always);
                Ui.Begin("many", ImGuiWindowFlags.NoDecoration);
                for (int i = 0; i < count; i++)
                {
                    Ui.Text($"line {i} with some text to draw");
                }

                Ui.End();
            });

            Assert.True(NonBackground(pixels, 0, 0, (int)Width, (int)Height) > 20000);
        }
    }

    [Fact]
    public void AWindowPartlyOffTheTargetIsClipped()
    {
        using var rig = new Rig(Device);

        byte[] pixels = rig.Frame(() =>
        {
            Ui.SetNextWindow(new Vector2(-60, -30), new Vector2(150, 90), ImGuiCond.Always);
            Ui.Begin("corner");
            Ui.Text("clipped at the corner");
            Ui.End();
            Ui.SetNextWindow(new Vector2(Width - 50, Height - 40), new Vector2(150, 90), ImGuiCond.Always);
            Ui.Begin("other corner");
            Ui.Text("clipped too");
            Ui.End();
        });

        Assert.True(NonBackground(pixels, 0, 0, 80, 50) > 500);
        Assert.True(NonBackground(pixels, (int)Width - 40, (int)Height - 30, (int)Width, (int)Height) > 200);
    }

    [Fact]
    public void AFrameWithNothingDrawnLeavesTheTargetAlone()
    {
        using var rig = new Rig(Device);

        byte[] pixels = rig.Frame(() => { });

        Assert.Equal(0, NonBackground(pixels, 0, 0, (int)Width, (int)Height));
    }

    [Fact]
    public void ASceneRendersWithTheOverlayOnTop()
    {
        using var host = new ImGuiHost();
        using var overlay = new ImGuiRenderer(Device, cacheDirectory: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apogee-test-shader-cache"));
        using var renderer = new SceneRenderer(Device, cacheDirectory: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apogee-test-shader-cache"));
        using var loop = Device.CreateFrameLoop();
        using GpuImage target = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, Width, Height, ImageUsage.ColorAttachment), "target");
        var model = new ModelData
        {
            Name = "empty",
            Meshes = [],
            Instances = [],
            Materials = [new MaterialData()],
            Textures = [],
            Lights = [],
            BoundsMin = Vector3.Zero,
            BoundsMax = Vector3.Zero,
        };
        using RenderScene scene = renderer.CreateScene(model);
        var camera = new Camera();
        var settings = new RenderSettings { SkyColor = new Vector3(0.2f, 0.3f, 0.5f) };

        byte[] Render(bool withOverlay)
        {
            host.BeginFrame(Width, Height, 1f, 1f / 60f);
            if (withOverlay)
            {
                Ui.SetNextWindow(new Vector2(10, 10), new Vector2(120, 60), ImGuiCond.Always);
                Ui.Begin("over the scene");
                Ui.Text("overlay");
                Ui.End();
            }

            host.EndFrame();
            Frame frame = loop.BeginFrame()!;
            renderer.Render(frame, scene, camera, RenderTarget.Offscreen(target), settings, overlay);
            loop.EndFrame(frame);
            loop.WaitIdle();
            return Device.ReadImage(target, ImageState.TransferSource);
        }

        byte[] without = Render(false);
        byte[] with = Render(true);

        int differing = 0;
        for (int i = 0; i < with.Length; i += 4)
        {
            if (with[i] != without[i] || with[i + 1] != without[i + 1] || with[i + 2] != without[i + 2])
            {
                differing++;
            }
        }

        Assert.InRange(differing, 3000, (int)(Width * Height) / 2);
        Assert.Contains("Dev overlay", renderer.LastPlan!.Passes.Where(p => !p.Culled).Select(p => p.Name));
    }

    [Fact]
    public void TheDevOverlayDrawsItsPanelsFromTheDataItIsGiven()
    {
        using var rig = new Rig(Device);
        var overlay = new DevOverlay();
        var settings = new RenderSettings();
        var camera = new Camera();
        var data = new DevOverlayData
        {
            GpuName = "Test GPU",
            CpuFrameMilliseconds = 6.5,
            GpuTimings = [new GpuTiming("Depth prepass", 0.4), new GpuTiming("Forward", 2.1)],
            Settings = settings,
            Camera = camera,
            Stats = new RenderStats(120, 64, 0, 4),
        };

        byte[] pixels = [];
        for (int i = 0; i < 5; i++)
        {
            pixels = rig.Frame(() => overlay.Draw(data));
        }

        Assert.True(NonBackground(pixels, 0, 0, (int)Width, (int)Height) > 3000);
        Assert.Equal(5, overlay.FrameTimeHistory.Count);
        Assert.All(overlay.FrameTimeHistory, t => Assert.Equal(6.5f, t));
        Assert.Equal(1f, settings.Exposure);      // untouched without input
    }

    [Fact]
    public void AHiddenOverlayDrawsNothingButKeepsRecordingFrameTimes()
    {
        using var rig = new Rig(Device);
        var overlay = new DevOverlay { Visible = false };
        var data = new DevOverlayData { CpuFrameMilliseconds = 4 };

        byte[] pixels = rig.Frame(() => overlay.Draw(data));

        Assert.Equal(0, NonBackground(pixels, 0, 0, (int)Width, (int)Height));
        Assert.Single(overlay.FrameTimeHistory);
    }

    [Fact]
    public void TheFrameTimeHistoryIsARingOfTheLastTwoHundredFortyFrames()
    {
        var overlay = new DevOverlay { Visible = false };
        for (int i = 0; i < 300; i++)
        {
            overlay.Draw(new DevOverlayData { CpuFrameMilliseconds = i });
        }

        Assert.Equal(240, overlay.FrameTimeHistory.Count);
        Assert.Equal(60f, overlay.FrameTimeHistory[0]);        // the oldest of the last 240
        Assert.Equal(299f, overlay.FrameTimeHistory[^1]);
    }

    [Fact]
    public void TheGpuTimeOfEveryTopLevelPassIsKeptForTheLastFramesAndZeroWhereItDidNotRun()
    {
        var overlay = new DevOverlay { Visible = false };
        for (int i = 0; i < 300; i++)
        {
            GpuTiming[] timings = i % 2 == 0
                ? [new GpuTiming("Depth prepass", 0.5), new GpuTiming("Forward", i), new GpuTiming("Forward/lights", 0.25)]
                : [new GpuTiming("Forward", i)];
            overlay.Draw(new DevOverlayData { GpuTimings = timings });
        }

        var history = overlay.GpuHistory;
        Assert.Equal(["Depth prepass", "Forward"], history.Keys.Order());    // a section inside a pass ("Forward/lights") is part of it, not another line
        Assert.Equal(240, history["Forward"].Length);
        Assert.Equal(60f, history["Forward"][0]);                              // the oldest of the last 240
        Assert.Equal(299f, history["Forward"][^1]);
        Assert.Equal(0.5f, history["Depth prepass"][^2]);                      // frame 298 ran it
        Assert.Equal(0f, history["Depth prepass"][^1]);                        // frame 299 did not
    }
}
