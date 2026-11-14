using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Apogee.Content;
using Apogee.Core.Graphics;
using Apogee.Graphics;
using Apogee.Graphics.Shaders;
using Apogee.Graphics.Tests;
using Xunit;

namespace Apogee.Rendering.Tests;

/// <summary>
/// Exposure on the GPU: the histogram and the exposure passes of <c>exposure.slang</c> on their own against the CPU twin (<see cref="ExposureMath"/>), and the renderer as a whole: light in
/// lux and candela, the camera's exposure value, and an auto-exposure that follows the scene.
/// </summary>
public unsafe class ExposureTests(GpuFixture gpu) : GpuTest(gpu)
{
    // ---- The two compute passes, driven directly --------------------------------------------------------------------------------

    private const float MinLog2 = ExposureMath.DefaultMinLog2Luminance;
    private const float MaxLog2 = ExposureMath.DefaultMaxLog2Luminance;

    /// <summary>A frame-data block whose exposure the passes read: the CPU's exposure of 1, so the image's values are the luminance.</summary>
    private GpuBuffer FrameDataWithUnitExposure(GpuBuffer state)
    {
        GpuBuffer buffer = Device.CreateBuffer((ulong)sizeof(GpuFrameData), BufferUsage.Storage, MemoryKind.Upload, "frame data");
        *(GpuFrameData*)buffer.Mapped = new GpuFrameData { Exposure = new Vector4(1f, 1f, 0f, 0f), ExposureState = state.Address };
        return buffer;
    }

    private static uint[] Counts(byte[] bytes)
    {
        uint[] counts = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, counts, 0, bytes.Length);
        return counts;
    }

    [Fact]
    public void TheHistogramPassCountsEveryPixelInTheBinTheCpuTwinPicks()
    {
        const int width = 40, height = 20;   // not multiples of the 16 x 16 group: the edge groups must not count pixels that are not there
        byte[] texels = new byte[width * height * 8];
        float[] depths = new float[width * height];
        uint[] expected = new uint[ExposureMath.HistogramBins];
        int sky = 0;
        for (int i = 0; i < width * height; i++)
        {
            depths[i] = i % 11 == 5 ? 0f : 0.5f;     // depth 0 (reverse-Z) is where nothing was drawn: the sky is not measured

            // A luminance at the center of a bin, so that the half precision of the image and the GPU's log2 cannot tip it into the next bin.
            float luminance = MathF.Pow(2f, ExposureMath.BinLog2Luminance(1 + (i * 37 % 254), MinLog2, MaxLog2));
            if (i % 50 == 0)
            {
                luminance = 0f;                          // black
            }
            else if (i % 73 == 0)
            {
                luminance = float.PositiveInfinity;      // a pixel that overflowed the half-float image: not measured (bin 0)
            }
            else if (i % 97 == 0)
            {
                luminance = MathF.Pow(2f, MinLog2 - 3f); // below it
            }

            Half half = (Half)luminance;   // the bins at the top of the range overflow to infinity, which is not measured (bin 0)
            // Every 13th pixel is unlit: the forward pass writes alpha 0 for it, and it is not measured (it lands in bin 0, which the average leaves out).
            bool unlit = i % 13 == 7;
            if (depths[i] == 0f)
            {
                sky++;
            }
            else if (unlit)
            {
                expected[0]++;
            }
            else
            {
                expected[ExposureMath.HistogramBin((float)half, MinLog2, MaxLog2)]++;
            }

            for (int c = 0; c < 3; c++)
            {
                BitConverter.TryWriteBytes(texels.AsSpan((i * 8) + (c * 2)), half);
            }

            BitConverter.TryWriteBytes(texels.AsSpan((i * 8) + 6), unlit ? (Half)0f : (Half)1f);
        }

        using GpuImage image = Device.CreateImage(new ImageDescription(PixelFormat.R16G16B16A16Sfloat, width, height, ImageUsage.Sampled), "histogram input");
        Device.UploadImage(image, texels, [new ImageRegion(0, 0, 0, width, height)], ImageState.ShaderRead);
        uint index = Device.Bindless.AddSampledImage(image);
        using GpuImage depthImage = Device.CreateImage(new ImageDescription(PixelFormat.D32Sfloat, width, height, ImageUsage.Sampled | ImageUsage.DepthStencilAttachment), "histogram depth");
        Device.UploadImage(depthImage, MemoryMarshal.AsBytes(depths.AsSpan()), [new ImageRegion(0, 0, 0, width, height)], ImageState.ShaderRead);
        uint depthIndex = Device.Bindless.AddSampledImage(depthImage);
        using GpuBuffer state = Device.CreateBuffer(new byte[16], BufferUsage.Storage, "state");
        using GpuBuffer histogram = Device.CreateBuffer(new byte[ExposureMath.HistogramBins * 4], BufferUsage.Storage, "histogram");
        using GpuBuffer frameData = FrameDataWithUnitExposure(state);
        CompiledShader shader = ShaderFiles.Compile(Device, "exposure.slang");
        using Pipeline pipeline = Device.CreateComputePipeline(shader.Code("buildHistogram"), "histogram");

        var push = new GpuExposurePush
        {
            Frame = frameData.Address,
            Histogram = histogram.Address,
            Texture = index,
            Depth = depthIndex,
            Width = width,
            Height = height,
            MinLog2Luminance = MinLog2,
            Log2Range = MaxLog2 - MinLog2,
        };
        Device.Immediate.Submit(recorder =>
        {
            recorder.Barriers([], [new BufferBarrier(histogram, BufferState.ShaderRead, BufferState.ShaderWrite)]);
            recorder.BindPipeline(pipeline);
            recorder.PushConstants(push);
            recorder.Dispatch((width + 15) / 16, (height + 15) / 16);
        });
        uint[] actual = Counts(Device.ReadBuffer(histogram, 0, ExposureMath.HistogramBins * 4));
        Device.Bindless.RemoveSampledImage(index);
        Device.Bindless.RemoveSampledImage(depthIndex);

        Assert.True(sky > 50);
        Assert.Equal((width * height) - sky, (int)actual.Sum(c => (long)c));
        Assert.Equal(expected, actual);
        Assert.True(actual[0] > 20, "black and too-dark pixels are in bin 0");
    }

    [Theory]
    [InlineData(true, 0.1f, 0.9f, 3f, 1f, 0.5f, 6f, 20f)]       // the first frame: the exposure starts at the target
    [InlineData(false, 0.1f, 0.9f, 3f, 1f, 0.5f, 6f, 20f)]      // the scene needs a higher EV100 than the state has (it got brighter): fast
    [InlineData(false, 0.1f, 0.9f, 3f, 1f, 0.5f, 16f, 20f)]     // a lower one (it got darker): slow
    [InlineData(false, 0f, 1f, 2f, 0.5f, 0.1f, 6f, 20f)]        // all pixels, other speeds
    [InlineData(false, 0.1f, 0.9f, 3f, 1f, 50f, 6f, 8f)]        // the target is clamped to the allowed range
    public void TheExposurePassAveragesAdaptsClampsAndClearsLikeTheCpuTwin(bool reset, float low, float high, float speedBright, float speedDark, float deltaTime, float previousEv, float maxEv)
    {
        // A scene: a dark corner, most of it around 400 cd/m2, a few lamps.
        uint[] bins = new uint[ExposureMath.HistogramBins];
        bins[0] = 900;
        bins[60] = 300;
        bins[156] = 5000;
        bins[157] = 2500;
        bins[200] = 200;
        bins[250] = 40;

        using GpuBuffer histogram = Device.CreateBuffer(MemoryMarshal.AsBytes(bins.AsSpan()), BufferUsage.Storage, "histogram");
        using GpuBuffer state = Device.CreateBuffer(MemoryMarshal.AsBytes(new[] { ExposureMath.Scale(previousEv), previousEv, 1f, 0f }.AsSpan()), BufferUsage.Storage, "state");
        using GpuBuffer frameData = FrameDataWithUnitExposure(state);
        CompiledShader shader = ShaderFiles.Compile(Device, "exposure.slang");
        using Pipeline pipeline = Device.CreateComputePipeline(shader.Code("resolveExposure"), "resolve");

        const float minEv = -2f;
        var push = new GpuExposurePush
        {
            Frame = frameData.Address,
            Histogram = histogram.Address,
            Reset = reset ? 1u : 0u,
            MinLog2Luminance = MinLog2,
            Log2Range = MaxLog2 - MinLog2,
            LowPercentile = low,
            HighPercentile = high,
            MinEv100 = minEv,
            MaxEv100 = maxEv,
            SpeedBright = speedBright,
            SpeedDark = speedDark,
            DeltaTime = deltaTime,
        };
        Device.Immediate.Submit(recorder =>
        {
            recorder.Barriers([], [new BufferBarrier(histogram, BufferState.ShaderRead, BufferState.ShaderWrite), new BufferBarrier(state, BufferState.ShaderRead, BufferState.ShaderWrite)]);
            recorder.BindPipeline(pipeline);
            recorder.PushConstants(push);
            recorder.Dispatch(1);
        });

        float averageLog2 = ExposureMath.AverageLog2Luminance(bins, MinLog2, MaxLog2, low, high);
        float target = ExposureMath.TargetEv100(averageLog2, minEv, maxEv);
        float ev = reset ? target : ExposureMath.Adapt(previousEv, target, deltaTime, speedBright, speedDark);

        float[] actual = new float[4];
        Buffer.BlockCopy(Device.ReadBuffer(state, 0, 16), 0, actual, 0, 16);
        Assert.Equal(ev, actual[1], 1e-3f);
        Assert.Equal(ExposureMath.Scale(ev), actual[0], ExposureMath.Scale(ev) * 1e-3f);
        Assert.Equal(MathF.Pow(2f, averageLog2), actual[2], MathF.Pow(2f, averageLog2) * 1e-3f);

        // The pass leaves the histogram empty for the next frame.
        Assert.All(Counts(Device.ReadBuffer(histogram, 0, ExposureMath.HistogramBins * 4)), count => Assert.Equal(0u, count));
    }

    // ---- The renderer ------------------------------------------------------------------------------------------------------------

    private const uint Width = 64;
    private const uint Height = 36;

    private static readonly string s_shaderCache = Path.Combine(Path.GetTempPath(), "apogee-test-shader-cache");

    private sealed class Rig : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly GpuImage _target;
        private readonly FrameLoop _loop;

        public Rig(GraphicsDevice device)
        {
            _device = device;
            Renderer = new SceneRenderer(device, cacheDirectory: s_shaderCache);
            _target = device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, Width, Height, ImageUsage.ColorAttachment), "Exposure target");
            _loop = device.CreateFrameLoop();
        }

        public SceneRenderer Renderer { get; }

        public byte[] Render(RenderScene scene, RenderSettings settings)
        {
            Frame frame = _loop.BeginFrame()!;
            Renderer.Render(frame, scene, TopDown(), RenderTarget.Offscreen(_target), settings);
            _loop.EndFrame(frame);
            _loop.WaitIdle();
            return _device.ReadImage(_target, ImageState.TransferSource);
        }

        public void Dispose()
        {
            _loop.Dispose();
            _target.Dispose();
            Renderer.Dispose();
        }
    }

    private static Camera TopDown()
    {
        var camera = new Camera { Position = new Vector3(0, 12, 0) };
        camera.LookAt(Vector3.Zero);
        return camera;
    }

    /// <summary>The middle of the image, as the average of its four middle pixels (0 to 255 per channel).</summary>
    private static Vector3 Center(byte[] rgba)
    {
        Vector3 sum = Vector3.Zero;
        foreach ((uint x, uint y) in new[] { (31u, 17u), (32u, 17u), (31u, 18u), (32u, 18u) })
        {
            int at = (int)((y * Width) + x) * 4;
            sum += new Vector3(rgba[at], rgba[at + 1], rgba[at + 2]);
        }

        return sum / 4f;
    }

    /// <summary>What the GT7 tone mapper makes of a gray of this exposed value, as an 8-bit sRGB channel.</summary>
    private static float Gt7Byte(float exposed) => Srgb(Gt7ToneMapping.ToDisplay(new Vector3(exposed)).X);

    private static float Srgb(float v) => (v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1f / 2.4f)) - 0.055f) * 255f;

    /// <summary>A model of a ground that glows with <paramref name="nits"/> cd/m2 and reflects nothing (a black base color), so its luminance is the only light in the picture.</summary>
    private static ModelData Emitter(float nits)
    {
        var scene = new TestScene();
        scene.AddGround(10, scene.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(0, 0, 0, 1), EmissiveFactor = new Vector3(nits), RoughnessFactor = 1f }));
        return scene.ToModel();
    }

    /// <summary>A white ground (albedo 0.8, fully rough, not metallic) lit by nothing in the model; the sun or lights are added by the test.</summary>
    private static ModelData WhiteGround(params LightData[] lights)
    {
        var scene = new TestScene();
        scene.AddGround(10, scene.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(0.8f, 0.8f, 0.8f, 1), MetallicFactor = 0f, RoughnessFactor = 1f }));
        foreach (LightData light in lights)
        {
            scene.AddLight(light);
        }

        return scene.ToModel();
    }

    private static RenderSettings Fixed(float ev100) => new() { Tonemapper = Tonemapper.Gt7, ExposureMode = ExposureMode.Fixed, Ev100 = ev100 };

    [Theory]
    [InlineData(8f)]
    [InlineData(10f)]
    [InlineData(11f)]
    [InlineData(13f)]
    public void AnEmitterOfKnownLuminanceShowsAsThePhotographicExposureAndGt7Say(float ev100)
    {
        using var rig = new Rig(Device);
        using RenderScene scene = rig.Renderer.CreateScene(Emitter(1000f));
        scene.AmbientSky = scene.AmbientGround = Vector3.Zero;

        Vector3 pixel = Center(rig.Render(scene, Fixed(ev100)));

        // 1000 cd/m2 over the luminance that saturates the sensor at this EV100, tone mapped.
        float exposed = 1000f * ExposureMath.Scale(ev100);
        float expected = Gt7Byte(exposed);
        Assert.True(MathF.Abs(expected - pixel.X) <= 2.5f && MathF.Abs(expected - pixel.Y) <= 2.5f && MathF.Abs(expected - pixel.Z) <= 2.5f, $"EV100 {ev100}: expected {expected}, got {pixel}");
    }

    [Fact]
    public void ThePhysicalCameraAndTheCompensationAreJustEvOffsets()
    {
        using var rig = new Rig(Device);
        using RenderScene scene = rig.Renderer.CreateScene(Emitter(1000f));
        scene.AmbientSky = scene.AmbientGround = Vector3.Zero;

        // f/4, 1/60 s and ISO 400 is EV100 log2(16 * 60) - 2 = 7.9: the very same image as that EV100 set by hand.
        float ev = ExposureMath.Ev100(4f, 1f / 60f, 400f);
        Assert.Equal(7.9f, ev, 0.01f);
        byte[] camera = rig.Render(scene, new RenderSettings { Tonemapper = Tonemapper.Gt7, ExposureMode = ExposureMode.Camera, Aperture = 4f, ShutterSeconds = 1f / 60f, Iso = 400f });
        byte[] manual = rig.Render(scene, Fixed(ev));
        Assert.Equal(manual, camera);

        // A compensation of 2 is one stop: the same as the EV100 one lower.
        RenderSettings brighter = Fixed(ev);
        brighter.Exposure = 2f;
        Assert.Equal(rig.Render(scene, Fixed(ev - 1f)), rig.Render(scene, brighter));
    }

    [Fact]
    public void ALuxAndACandelaAreTheSamePhotometryAndTheBrdfGivesTheLuminanceTheDocumentationSays()
    {
        // 2 lux straight down on the white ground, from the sun or from a point light of 200 cd, 10 m above it (200 / 10^2 = 2 lux there).
        using var rig = new Rig(Device);
        using RenderScene sun = rig.Renderer.CreateScene(WhiteGround());
        sun.SunDirection = Vector3.UnitY;
        sun.SunColor = new Vector3(2f);
        sun.AmbientSky = sun.AmbientGround = Vector3.Zero;
        using RenderScene lamp = rig.Renderer.CreateScene(WhiteGround(TestScene.Point(new Vector3(0, 10, 0), Vector3.One, 200f, range: 1000f)));
        lamp.AmbientSky = lamp.AmbientGround = Vector3.Zero;

        RenderSettings settings = Legacy.Settings();
        Vector3 fromSun = Center(rig.Render(sun, settings));
        Vector3 fromLamp = Center(rig.Render(lamp, settings));
        Assert.True(Vector3.Abs(fromSun - fromLamp).Length() < 2f, $"sun {fromSun}, lamp {fromLamp}");

        // The luminance of a rough dielectric under a light straight above, looked at straight down: (1 - F) * albedo / pi + F * D * V, times the illuminance (here: F = 0.04, D = 1 / pi and V = 1 / 4 at
        // roughness 1). At the unit exposure of the Legacy settings the exposed value is that luminance.
        const float f = 0.04f;
        float brdf = ((1f - f) * 0.8f / MathF.PI) + (f * (1f / MathF.PI) * 0.25f);
        float luminance = 2f * brdf;
        Assert.Equal(0.4953f, luminance, 1e-3f);
        float expected = Srgb(ToneTwins.Neutral(new Vector3(luminance)).X);
        Assert.True(MathF.Abs(expected - fromSun.X) <= 2.5f, $"expected {expected}, got {fromSun}");

        // Twice the lux is twice the exposed value: a stop of exposure undoes it.
        sun.SunColor = new Vector3(4f);
        RenderSettings darker = Legacy.Settings();
        darker.Ev100 += 1f;
        Assert.True(Vector3.Abs(Center(rig.Render(sun, darker)) - fromSun).Length() < 2f);
    }

    [Fact]
    public void AutoExposureMeasuresTheSceneAdaptsToChangesAndRestartsAfterAnotherMode()
    {
        using var rig = new Rig(Device);
        using RenderScene bright = rig.Renderer.CreateScene(Emitter(2000f));
        using RenderScene dim = rig.Renderer.CreateScene(Emitter(125f));
        bright.AmbientSky = bright.AmbientGround = dim.AmbientSky = dim.AmbientGround = Vector3.Zero;
        var settings = new RenderSettings { Tonemapper = Tonemapper.Gt7, ExposureMode = ExposureMode.Auto, DeltaTime = 10f };

        // The first frame draws with the manual EV100 (12), measures, and starts at what the histogram calls for.
        rig.Render(bright, settings);
        Assert.Contains("Luminance histogram", rig.Renderer.LastPlan!.Passes.Where(p => !p.Culled).Select(p => p.Name));
        Assert.Contains("Resolve exposure", rig.Renderer.LastPlan.Passes.Where(p => !p.Culled).Select(p => p.Name));
        (float scale1, float ev1, float average1) = rig.Renderer.ReadExposureState();
        Assert.Equal(2000f, average1, 2000f * 0.05f);                                  // within the width of a bin
        Assert.Equal(ExposureMath.Ev100FromLuminance(2000f), ev1, 0.07f);
        Assert.Equal(ExposureMath.Scale(ev1), scale1, scale1 * 1e-3f);

        // The second frame is drawn with that exposure (and a compensation of 2 on top, which the measurement must divide out again).
        settings.Exposure = 2f;
        byte[] second = rig.Render(bright, settings);
        float expected = Gt7Byte(2000f * scale1 * 2f);
        Assert.True(MathF.Abs(expected - Center(second).X) <= 2.5f, $"expected {expected}, got {Center(second)}");
        (_, float ev2, float average2) = rig.Renderer.ReadExposureState();
        Assert.Equal(2000f, average2, 2000f * 0.05f);
        Assert.Equal(ev1, ev2, 0.01f);

        // The scene gets 4 stops darker: it is measured with the exposure it was drawn with, and the exposure follows by the adaptation's rate, slowly (to dark) at 0.5 s a frame.
        settings.Exposure = 1f;
        settings.DeltaTime = 0.5f;
        rig.Render(dim, settings);
        (_, float ev3, float average3) = rig.Renderer.ReadExposureState();
        Assert.Equal(125f, average3, 125f * 0.05f);
        float target = ExposureMath.Ev100FromLuminance(average3);
        Assert.True(target < ev2 - 3f);
        Assert.Equal(ExposureMath.Adapt(ev2, target, 0.5f, settings.AdaptationSpeedBright, settings.AdaptationSpeedDark), ev3, 5e-3f);

        // Another mode, then auto again: the exposure restarts at the target instead of crawling from a stale value.
        rig.Render(dim, Fixed(5f));
        Assert.DoesNotContain("Luminance histogram", rig.Renderer.LastPlan!.Passes.Where(p => !p.Culled).Select(p => p.Name));
        settings.DeltaTime = 0.001f;
        rig.Render(bright, settings);
        (_, float ev4, float average4) = rig.Renderer.ReadExposureState();
        Assert.Equal(2000f, average4, 2000f * 0.05f);
        Assert.Equal(ExposureMath.Ev100FromLuminance(average4), ev4, 1e-3f);
    }

    [Fact]
    public void AnUnlitColorIsDisplayReferredAndDoesNotFeedBackIntoTheAutoExposure()
    {
        // Most of the view is an unlit quad of 0.8: not light, so it is neither exposed nor measured. Measured as luminance (0.8 divided by the exposure) it would pull the exposure towards
        // itself a little further every frame; only the small lit patch in the middle, 2000 cd/m2, may set it.
        var scene = new TestScene();
        scene.AddGround(2, scene.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(0, 0, 0, 1), EmissiveFactor = new Vector3(2000f), RoughnessFactor = 1f }));
        scene.AddBox(new Vector3(0, -0.6f, 0), new Vector3(24f, 1f, 24f), scene.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(0.8f, 0.8f, 0.8f, 1), Unlit = true }));

        using var rig = new Rig(Device);
        using RenderScene mostlyUnlit = rig.Renderer.CreateScene(scene.ToModel());
        mostlyUnlit.AmbientSky = mostlyUnlit.AmbientGround = Vector3.Zero;
        var settings = new RenderSettings { Tonemapper = Tonemapper.Gt7, ExposureMode = ExposureMode.Auto, DeltaTime = 0.5f };

        float expected = ExposureMath.Ev100FromLuminance(2000f);
        byte[] last = [];
        for (int frame = 0; frame < 6; frame++)
        {
            last = rig.Render(mostlyUnlit, settings);
            (_, float ev, float average) = rig.Renderer.ReadExposureState();
            Assert.Equal(expected, ev, 0.07f);
            Assert.Equal(2000f, average, 2000f * 0.05f);
        }

        // The unlit color is shown as it is authored (0.8 through the tone mapper, whatever the exposure), the lit patch as exposed.
        int at = ((18 * (int)Width) + 8) * 4;
        Assert.Equal(Gt7Byte(0.8f), last[at], 2.5f);
    }
}
