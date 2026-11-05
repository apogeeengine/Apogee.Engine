using Apogee.Core.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Content;
using Apogee.Core.Numerics;
using Apogee.Graphics;
using Apogee.Graphics.Tests;
using Apogee.Native.Vulkan;
using Xunit;

namespace Apogee.Rendering.Tests;

public class ForwardPlusTests(GpuFixture gpu) : GpuTest(gpu)
{
    private const uint Width = 192;
    private const uint Height = 108;

    private static readonly string s_shaderCache = Path.Combine(Path.GetTempPath(), "apogee-test-shader-cache");

    private static Camera DefaultCamera()
    {
        var camera = new Camera { Position = new Vector3(0, 4, 8) };
        camera.LookAt(Vector3.Zero);
        return camera;
    }

    /// <summary>Renders frames of a scene to an offscreen RGBA8 image and returns the pixels of the last one.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly GpuImage _target;
        private readonly FrameLoop _loop;

        public Rig(GraphicsDevice device)
        {
            _device = device;
            Renderer = new SceneRenderer(device, cacheDirectory: s_shaderCache);
            _target = device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, Width, Height, ImageUsage.ColorAttachment), "Rig target");
            _loop = device.CreateFrameLoop();
        }

        public SceneRenderer Renderer { get; }

        public byte[] Render(RenderScene scene, Camera camera, RenderSettings? settings = null, int frames = 1)
        {
            for (int i = 0; i < frames; i++)
            {
                Frame frame = _loop.BeginFrame()!;
                Renderer.Render(frame, scene, camera, RenderTarget.Offscreen(_target), settings);
                _loop.EndFrame(frame);
                _loop.WaitIdle();
            }

            return _device.ReadImage(_target, ImageState.TransferSource);
        }

        public void Dispose()
        {
            _loop.Dispose();
            _target.Dispose();
            Renderer.Dispose();
        }
    }

    private static Vector3 Pixel(byte[] rgba, int x, int y) => new(rgba[(y * (int)Width + x) * 4], rgba[(y * (int)Width + x) * 4 + 1], rgba[(y * (int)Width + x) * 4 + 2]);

    private static float Luma(Vector3 p) => (p.X + p.Y + p.Z) / 3f;

    private static float MeanLuma(byte[] rgba, int x0, int y0, int x1, int y1)
    {
        float sum = 0;
        int count = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                sum += Luma(Pixel(rgba, x, y));
                count++;
            }
        }

        return sum / count;
    }

    private static int GrayMaterial(TestScene scene, float albedo = 0.8f, float roughness = 0.8f) =>
        scene.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(albedo, albedo, albedo, 1), MetallicFactor = 0, RoughnessFactor = roughness });

    /// <summary>The CPU twin of tonemap.slang: exposure, the Khronos PBR Neutral tone mapper, then the sRGB transfer function, as bytes.</summary>
    private static Vector3 Display(Vector3 hdr, float exposure)
    {
        Vector3 c = hdr * exposure;
        float x = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float offset = x < 0.08f ? x - 6.25f * x * x : 0.04f;
        c -= new Vector3(offset);
        float peak = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        const float start = 0.8f - 0.04f;
        if (peak >= start)
        {
            const float d = 1f - start;
            float newPeak = 1f - d * d / (peak + d - start);
            c *= newPeak / peak;
            float g = 1f - 1f / (0.15f * (peak - newPeak) + 1f);
            c = Vector3.Lerp(c, new Vector3(newPeak), g);
        }

        c = Vector3.Clamp(c, Vector3.Zero, Vector3.One);
        static float Srgb(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        return new Vector3(Srgb(c.X), Srgb(c.Y), Srgb(c.Z)) * 255f;
    }

    private static void AssertPixel(Vector3 expected, Vector3 actual, float tolerance = 2.5f)
    {
        Assert.True(MathF.Abs(expected.X - actual.X) <= tolerance && MathF.Abs(expected.Y - actual.Y) <= tolerance && MathF.Abs(expected.Z - actual.Z) <= tolerance, $"expected about {expected}, got {actual}");
    }

    [Fact]
    public void TheFirstFrameRunsEveryPassAndLaterFramesSkipTheClusterBuild()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        model.AddGround(10, GrayMaterial(model));
        using RenderScene scene = rig.Renderer.CreateScene(model.ToModel());

        rig.Render(scene, DefaultCamera());
        Assert.Equal(["Depth prepass", "Build clusters", "Cull lights", "Forward", "Tonemap"], rig.Renderer.LastPlan!.Passes.Where(p => !p.Culled).Select(p => p.Name));
        Assert.Equal(ClusterGrid.Total, rig.Renderer.LastStats!.ClustersBuilt);
        Assert.Equal(5, rig.Renderer.LastStats.PassesRun);

        var built = new List<int>();
        var passes = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            rig.Render(scene, DefaultCamera());
            built.Add(rig.Renderer.LastStats!.ClustersBuilt);
            passes.Add(rig.Renderer.LastStats.PassesRun);
        }

        // Two frame slots each build once; after that the bounds are reused until the projection changes.
        Assert.Equal([ClusterGrid.Total, 0, 0, 0], built);
        Assert.Equal([5, 4, 4, 4], passes);

        Camera wider = DefaultCamera();
        wider.VerticalFov = 90f * MathF.PI / 180f;
        rig.Render(scene, wider);
        Assert.Equal(ClusterGrid.Total, rig.Renderer.LastStats!.ClustersBuilt);
        Assert.Equal(5, rig.Renderer.LastStats.PassesRun);
    }

    [Fact]
    public void WhereNothingIsDrawnTheSkyIsToneMappedExactlyLikeTheCpuTwin()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        model.AddGround(1, GrayMaterial(model));      // a small patch; the top of the image is empty
        using RenderScene scene = rig.Renderer.CreateScene(model.ToModel());
        var settings = new RenderSettings { SkyColor = new Vector3(0.35f, 0.5f, 0.75f), Exposure = 1f };

        byte[] pixels = rig.Render(scene, DefaultCamera(), settings);

        AssertPixel(Display(settings.SkyColor, 1f), Pixel(pixels, 2, 2));
        AssertPixel(Display(settings.SkyColor, 1f), Pixel(pixels, (int)Width - 3, 2));

        settings.Exposure = 0.5f;
        AssertPixel(Display(settings.SkyColor, 0.5f), Pixel(rig.Render(scene, DefaultCamera(), settings), 2, 2));
    }

    [Fact]
    public void APointLightBrightensTheSurfaceNearItAndLeavesTheFarOneAlone()
    {
        using var rig = new Rig(Device);
        var dark = new TestScene();
        dark.AddGround(10, GrayMaterial(dark));
        var lit = new TestScene();
        lit.AddGround(10, GrayMaterial(lit));
        lit.AddLight(TestScene.Point(new Vector3(-3, 1.5f, 0), Vector3.One, 20f, range: 5f));
        using RenderScene darkScene = rig.Renderer.CreateScene(dark.ToModel());
        using RenderScene litScene = rig.Renderer.CreateScene(lit.ToModel());
        Camera topDown = new() { Position = new Vector3(0, 12, 0) };
        topDown.LookAt(Vector3.Zero);

        byte[] before = rig.Render(darkScene, topDown);
        byte[] after = rig.Render(litScene, topDown);

        // The light at x = -3 is on the left half of the image; its range (5) does not reach x > 2.
        float nearBefore = MeanLuma(before, 20, 30, 80, 80);
        float nearAfter = MeanLuma(after, 20, 30, 80, 80);
        Assert.True(nearAfter > nearBefore + 20f, $"near the light: {nearBefore} -> {nearAfter}");
        Assert.Equal(MeanLuma(before, 140, 30, 190, 80), MeanLuma(after, 140, 30, 190, 80), 0.01f);
    }

    [Fact]
    public void ALightIsZeroAtItsRadiusSoNothingChangesBeyondIt()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        model.AddGround(10, GrayMaterial(model));
        using RenderScene scene = rig.Renderer.CreateScene(model.ToModel());
        Camera topDown = new() { Position = new Vector3(0, 12, 0) };
        topDown.LookAt(Vector3.Zero);
        byte[] baseline = rig.Render(scene, topDown);

        scene.Lights.Add(TestScene.Point(new Vector3(0, 2, 0), Vector3.One, 50f, range: 3f));
        byte[] withLight = rig.Render(scene, topDown);

        // The light reaches 3 m from (0, 2, 0): on the ground that is a disc of radius sqrt(3^2 - 2^2) = 2.2 m. Compare a ring well outside it.
        int changedInside = 0;
        int changedOutside = 0;
        for (int y = 0; y < (int)Height; y++)
        {
            for (int x = 0; x < (int)Width; x++)
            {
                if (Pixel(baseline, x, y) != Pixel(withLight, x, y))
                {
                    // Distance from the image centre in metres: the camera sees 12 * tan(30 deg) * 2 = 13.9 m across the image height.
                    float metersPerPixel = 13.86f / Height;
                    float distance = MathF.Sqrt(MathF.Pow((x - Width / 2f) * metersPerPixel, 2) + MathF.Pow((y - Height / 2f) * metersPerPixel, 2));
                    if (distance < 3f)
                    {
                        changedInside++;
                    }
                    else
                    {
                        changedOutside++;
                    }
                }
            }
        }

        Assert.True(changedInside > 100, $"only {changedInside} pixels changed near the light");
        Assert.Equal(0, changedOutside);
    }

    [Fact]
    public void TheSunLightsAFlatSurfaceByTheAngleBetweenThemWithoutAnyClusterLights()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        model.AddGround(10, GrayMaterial(model));
        using RenderScene overhead = rig.Renderer.CreateScene(model.ToModel());
        overhead.SunDirection = Vector3.UnitY;
        overhead.SunColor = new Vector3(3f);
        using RenderScene grazing = rig.Renderer.CreateScene(model.ToModel());
        grazing.SunDirection = Vector3.Normalize(new Vector3(1f, 0.15f, 0f));
        grazing.SunColor = new Vector3(3f);
        using RenderScene none = rig.Renderer.CreateScene(model.ToModel());

        float high = MeanLuma(rig.Render(overhead, DefaultCamera()), 60, 70, 130, 100);
        float low = MeanLuma(rig.Render(grazing, DefaultCamera()), 60, 70, 130, 100);
        float off = MeanLuma(rig.Render(none, DefaultCamera()), 60, 70, 130, 100);

        Assert.True(high > low + 30f, $"overhead {high}, grazing {low}");
        Assert.True(low > off, $"grazing {low}, no sun {off}");
    }

    [Fact]
    public void AlphaMaskDiscardsAndAlphaBlendBlends()
    {
        using var rig = new Rig(Device);
        Vector3 Center(byte[] p) => Pixel(p, (int)Width / 2, (int)Height / 2);
        Camera camera = new() { Position = new Vector3(0, 0, 5) };
        camera.LookAt(new Vector3(0, 0, 0));

        RenderScene Build(MaterialData? front)
        {
            var model = new TestScene();
            int back = model.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(1, 0, 0, 1), Unlit = true });
            model.AddWall(new Vector3(0, 0, -3), 4, 4, back);
            if (front is not null)
            {
                model.AddWall(new Vector3(0, 0, -1), 4, 4, model.AddMaterial(front));
            }

            return rig.Renderer.CreateScene(model.ToModel());
        }

        using RenderScene redOnly = Build(null);
        using RenderScene maskedAway = Build(new MaterialData { BaseColorFactor = new Vector4(0, 1, 0, 0.2f), AlphaMode = AlphaMode.Mask, AlphaCutoff = 0.5f, Unlit = true });
        using RenderScene maskedIn = Build(new MaterialData { BaseColorFactor = new Vector4(0, 1, 0, 0.8f), AlphaMode = AlphaMode.Mask, AlphaCutoff = 0.5f, Unlit = true });
        using RenderScene blended = Build(new MaterialData { BaseColorFactor = new Vector4(0, 1, 0, 0.5f), AlphaMode = AlphaMode.Blend, Unlit = true });

        Vector3 red = Center(rig.Render(redOnly, camera));
        Vector3 away = Center(rig.Render(maskedAway, camera));
        Vector3 solid = Center(rig.Render(maskedIn, camera));
        Vector3 mixed = Center(rig.Render(blended, camera));

        Assert.True(red.X > 200 && red.Y < 60 && red.Z < 60, $"red wall {red}");
        Assert.Equal(red, away);                                   // alpha 0.2 is below the cutoff: the front wall leaves no trace
        Assert.True(solid.Y > 200 && solid.X < 60, $"masked in {solid}");   // alpha 0.8 is above it: the front wall is opaque
        Assert.True(mixed.X > 60 && mixed.Y > 60, $"blended {mixed}");      // half red, half green
    }

    [Fact]
    public void DoubleSidedMaterialsAreVisibleFromBehindAndSingleSidedOnesAreNot()
    {
        using var rig = new Rig(Device);
        Camera behind = new() { Position = new Vector3(0, 0, -5) };
        behind.LookAt(Vector3.Zero);       // looks at the wall's back (the wall faces +Z)
        Vector3 Center(byte[] p) => Pixel(p, (int)Width / 2, (int)Height / 2);

        RenderScene Build(bool doubleSided)
        {
            var model = new TestScene();
            model.AddWall(Vector3.Zero, 4, 4, model.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(0, 0, 1, 1), Unlit = true, DoubleSided = doubleSided }));
            return rig.Renderer.CreateScene(model.ToModel());
        }

        var settings = new RenderSettings { SkyColor = Vector3.Zero };
        using RenderScene single = Build(false);
        using RenderScene @double = Build(true);

        Assert.True(Luma(Center(rig.Render(single, behind, settings))) < 3f);
        Assert.True(Center(rig.Render(@double, behind, settings)).Z > 150f);
    }

    [Fact]
    public void TheClusterHeatmapShowsWhereLightsAre()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        model.AddGround(10, GrayMaterial(model));
        model.AddLight(TestScene.Point(new Vector3(-3, 1.5f, 0), Vector3.One, 20f, range: 4f));
        using RenderScene scene = rig.Renderer.CreateScene(model.ToModel());
        Camera topDown = new() { Position = new Vector3(0, 12, 0) };
        topDown.LookAt(Vector3.Zero);

        byte[] heat = rig.Render(scene, topDown, new RenderSettings { Debug = DebugView.ClusterLights });

        Vector3 near = Pixel(heat, 72, 54);       // under the light
        Vector3 far = Pixel(heat, 170, 54);       // no light reaches here
        Assert.True(Luma(far) < 40f, $"far {far}: no lights is dark");
        Assert.True(MathF.Max(near.X, MathF.Max(near.Y, near.Z)) - MathF.Min(near.X, MathF.Min(near.Y, near.Z)) > 100f, $"near {near}: a lit cluster is saturated colour");
    }

    [Fact]
    public void ManyLightsStillRender()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        model.AddGround(20, GrayMaterial(model));
        var random = new Random(5);
        for (int i = 0; i < 600; i++)
        {
            model.AddLight(TestScene.Point(new Vector3((float)(random.NextDouble() * 36 - 18), 1f, (float)(random.NextDouble() * 36 - 18)), new Vector3(1, 0.7f, 0.4f), 8f, range: 3f));
        }

        using RenderScene scene = rig.Renderer.CreateScene(model.ToModel());
        byte[] pixels = rig.Render(scene, DefaultCamera());

        Assert.Equal(600, rig.Renderer.LastStats!.Lights);
        Assert.True(MeanLuma(pixels, 0, 60, (int)Width, (int)Height) > 20f);
    }

    [Fact]
    public void LightCullingOnTheGpuMatchesACpuReference()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        model.AddGround(20, GrayMaterial(model));
        var random = new Random(11);
        var lights = new List<LightData>();
        for (int i = 0; i < 150; i++)
        {
            LightData light = TestScene.Point(
                new Vector3((float)(random.NextDouble() * 30 - 15), (float)(random.NextDouble() * 4), (float)(random.NextDouble() * 30 - 15)),
                Vector3.One,
                5f,
                range: (float)(0.8 + random.NextDouble() * 2.5));
            lights.Add(light);
            model.AddLight(light);
        }

        using RenderScene scene = rig.Renderer.CreateScene(model.ToModel());
        Camera camera = DefaultCamera();
        rig.Render(scene, camera);
        uint[] gpu = rig.Renderer.ReadClusterLights(0);

        Matrix4x4 view = camera.View;
        Matrix4x4 projection = camera.Projection(Width / (float)Height);
        Vector3[] centers = [.. lights.Select(l => camera.WorldToView(l.Position))];
        int strictTotal = 0;
        int gpuTotal = 0;
        for (int z = 0; z < ClusterGrid.CountZ; z++)
        {
            for (int y = 0; y < ClusterGrid.CountY; y++)
            {
                for (int x = 0; x < ClusterGrid.CountX; x++)
                {
                    (Vector3 min, Vector3 max) = ClusterGrid.Bounds(x, y, z, camera.Near, camera.ClusterFar, projection.M11, projection.M22);
                    var strict = new HashSet<uint>();
                    var loose = new HashSet<uint>();
                    for (int i = 0; i < lights.Count; i++)
                    {
                        float d2 = Vector3.DistanceSquared(centers[i], Vector3.Clamp(centers[i], min, max));
                        float r = lights[i].Range;
                        if (d2 <= r * r * 0.998f)
                        {
                            strict.Add((uint)i);
                        }

                        if (d2 <= r * r * 1.002f)
                        {
                            loose.Add((uint)i);
                        }
                    }

                    int offset = ClusterGrid.Index(x, y, z) * ClusterGrid.Stride;
                    uint count = gpu[offset];
                    Assert.True(count < ClusterGrid.MaxLightsPerCluster, $"cluster ({x},{y},{z}) overflowed in this test");
                    var actual = new HashSet<uint>(gpu.Skip(offset + 1).Take((int)count));

                    Assert.True(strict.IsSubsetOf(actual), $"cluster ({x},{y},{z}) misses lights {string.Join(",", strict.Except(actual))}");
                    Assert.True(actual.IsSubsetOf(loose), $"cluster ({x},{y},{z}) has extra lights {string.Join(",", actual.Except(loose))}");
                    strictTotal += strict.Count;
                    gpuTotal += actual.Count;
                }
            }
        }

        Assert.True(gpuTotal > 500, $"only {gpuTotal} light-cluster pairs; the test would prove little");
        Assert.True(strictTotal <= gpuTotal);
    }

    [Fact]
    public void AClusterKeepsAtMostSixtyFourLightsAndStillCounts()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        model.AddGround(5, GrayMaterial(model));
        for (int i = 0; i < 200; i++)
        {
            model.AddLight(TestScene.Point(new Vector3(0, 0.5f, 0), Vector3.One, 1f, range: 4f));      // all in the same place
        }

        using RenderScene scene = rig.Renderer.CreateScene(model.ToModel());
        rig.Render(scene, DefaultCamera());
        uint[] gpu = rig.Renderer.ReadClusterLights(0);

        uint max = 0;
        for (int cluster = 0; cluster < ClusterGrid.Total; cluster++)
        {
            max = Math.Max(max, gpu[cluster * ClusterGrid.Stride]);
        }

        Assert.Equal((uint)ClusterGrid.MaxLightsPerCluster, max);
    }

    [Fact]
    public void ASpotLightLightsOnlyInsideItsCone()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        model.AddGround(10, GrayMaterial(model));
        model.AddLight(TestScene.Spot(new Vector3(0, 4, 0), -Vector3.UnitY, Vector3.One, 60f, 0.15f, 0.3f));
        using RenderScene lit = rig.Renderer.CreateScene(model.ToModel());
        var none = new TestScene();
        none.AddGround(10, GrayMaterial(none));
        using RenderScene unlit = rig.Renderer.CreateScene(none.ToModel());
        Camera topDown = new() { Position = new Vector3(0, 12, 0) };
        topDown.LookAt(Vector3.Zero);

        byte[] with = rig.Render(lit, topDown);
        byte[] without = rig.Render(unlit, topDown);

        // Directly under the spot is lit; a spot 5 m to the side (far outside a 0.3 rad cone at 4 m) is untouched.
        Assert.True(Luma(Pixel(with, (int)Width / 2, (int)Height / 2)) > Luma(Pixel(without, (int)Width / 2, (int)Height / 2)) + 20f);
        Assert.Equal(Pixel(without, 150, 54), Pixel(with, 150, 54));
    }
}

/// <summary>The scene of the golden image: a ground, four boxes, a glass pane, an emissive block, a sun, three point lights and a spot.</summary>
internal static class GoldenScene
{
    public static ModelData Build()
    {
        var scene = new TestScene();
        int ground = scene.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(0.45f, 0.5f, 0.42f, 1), RoughnessFactor = 0.9f, MetallicFactor = 0 });
        int red = scene.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(0.8f, 0.12f, 0.1f, 1), RoughnessFactor = 0.45f, MetallicFactor = 0 });
        int metal = scene.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(0.95f, 0.8f, 0.45f, 1), RoughnessFactor = 0.25f, MetallicFactor = 1 });
        int blue = scene.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(0.1f, 0.25f, 0.8f, 1), RoughnessFactor = 0.15f, MetallicFactor = 0 });
        int glass = scene.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(0.4f, 0.9f, 0.6f, 0.35f), RoughnessFactor = 0.2f, AlphaMode = AlphaMode.Blend, DoubleSided = true });
        int emissive = scene.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(0.1f, 0.1f, 0.1f, 1), EmissiveFactor = new Vector3(4f, 1.6f, 0.4f) });

        scene.AddGround(14, ground)
            .AddBox(new Vector3(-3.2f, 0.9f, -1f), new Vector3(1.8f, 1.8f, 1.8f), red)
            .AddBox(new Vector3(0f, 0.6f, 0.5f), new Vector3(1.2f, 1.2f, 1.2f), metal)
            .AddBox(new Vector3(3.0f, 1.2f, -2f), new Vector3(1.4f, 2.4f, 1.4f), blue)
            .AddBox(new Vector3(1.5f, 0.15f, 2.2f), new Vector3(0.8f, 0.3f, 0.8f), emissive)
            .AddWall(new Vector3(-0.5f, 1.1f, 2.8f), 1.6f, 1.1f, glass)
            .AddLight(TestScene.Sun(new Vector3(-0.4f, -1f, -0.3f), new Vector3(1f, 0.95f, 0.85f), 1.2f))
            .AddLight(TestScene.Point(new Vector3(-1.6f, 1.6f, 1.6f), new Vector3(1f, 0.4f, 0.25f), 30f, range: 7f))
            .AddLight(TestScene.Point(new Vector3(2.4f, 2.6f, 0.2f), new Vector3(0.3f, 0.5f, 1f), 40f, range: 8f))
            .AddLight(TestScene.Point(new Vector3(1.5f, 0.7f, 2.2f), new Vector3(1f, 0.5f, 0.1f), 12f, range: 4f))
            .AddLight(TestScene.Spot(new Vector3(-4f, 4f, 3f), new Vector3(0.9f, -1f, -0.9f), new Vector3(1f, 1f, 0.9f), 180f, 0.12f, 0.35f));
        return scene.ToModel();
    }
}

/// <summary>A composed scene rendered offscreen and compared with a stored image.</summary>
public class GoldenImageTests(GpuFixture gpu) : GpuTest(gpu)
{
    private const uint Width = 192;
    private const uint Height = 108;

    private static string GoldenPath
    {
        get
        {
            string? directory = AppContext.BaseDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "Apogee.slnx")))
            {
                directory = Path.GetDirectoryName(directory);
            }

            return Path.Combine(directory ?? ".", "Tests", "Apogee.Rendering.Tests", "Golden", "forward_plus.png");
        }
    }

    /// <summary>The golden scene seen by the golden camera, with both moved by <paramref name="shift"/> in the world.</summary>
    private byte[] RenderGoldenScene(Double3 shift)
    {
        using var renderer = new SceneRenderer(Device, cacheDirectory: Path.Combine(Path.GetTempPath(), "apogee-test-shader-cache"));
        using var loop = Device.CreateFrameLoop();
        using GpuImage target = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, Width, Height, ImageUsage.ColorAttachment), "golden target");
        using RenderScene scene = renderer.CreateScene(GoldenScene.Build(), shift);
        scene.AmbientSky = new Vector3(0.1f, 0.13f, 0.18f);
        scene.AmbientGround = new Vector3(0.05f, 0.045f, 0.04f);
        var camera = new Camera { Position = shift + new Vector3(-0.5f, 4.2f, 9f) };
        camera.LookAt(shift + new Vector3(0f, 0.8f, 0f));

        Frame frame = loop.BeginFrame()!;
        renderer.Render(frame, scene, camera, RenderTarget.Offscreen(target), new RenderSettings { SkyColor = new Vector3(0.3f, 0.42f, 0.65f) });
        loop.EndFrame(frame);
        loop.WaitIdle();
        return Device.ReadImage(target, ImageState.TransferSource);
    }

    [Fact]
    public void TheSceneLooksTheSameFarFromTheWorldsOrigin()
    {
        // 100 km out: a float position there moves in 8 mm steps, which would show as a wobbly picture. Camera-relative rendering does not care.
        byte[] near = RenderGoldenScene(Double3.Zero);
        byte[] far = RenderGoldenScene(new Double3(100_000, 0, -100_000));
        byte[] farther = RenderGoldenScene(new Double3(10_000_000, 250, 10_000_000));

        Assert.True(MeanDifference(near, far) < 0.05, $"100 km: {MeanDifference(near, far)}");
        Assert.True(MeanDifference(near, farther) < 0.05, $"10 000 km: {MeanDifference(near, farther)}");
        Assert.True(near.Distinct().Count() > 20, "the picture is not blank");
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += i % 4 == 3 ? 0 : Math.Abs(a[i] - b[i]);
        }

        return sum / (a.Length / 4 * 3);
    }

    [Fact]
    public void TheComposedSceneLooksLikeTheStoredImage()
    {
        byte[] actual = RenderGoldenScene(Double3.Zero);

        string path = GoldenPath;
        if (Environment.GetEnvironmentVariable("APOGEE_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllBytes(path, Png.Encode((int)Width, (int)Height, actual));
        }

        if (!File.Exists(path))
        {
            Assert.Skip($"There is no golden image at {path}; run once with APOGEE_UPDATE_GOLDEN=1 to create it.");
        }

        TextureData golden = ImageDecoder.Decode("golden", File.ReadAllBytes(path), srgb: false, SamplerData.Default, generateMips: false);
        Assert.Equal(((int)Width, (int)Height), (golden.Width, golden.Height));

        double sum = 0;
        int worst = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            if (i % 4 == 3)
            {
                continue;
            }

            int difference = Math.Abs(actual[i] - golden.Mips[0][i]);
            sum += difference;
            worst = Math.Max(worst, difference);
        }

        double mean = sum / (actual.Length / 4 * 3);
        string actualPath = Path.Combine(AppContext.BaseDirectory, "golden-actual.png");
        File.WriteAllBytes(actualPath, Png.Encode((int)Width, (int)Height, actual));

        // The stored image comes from Mesa's software rasterizer; the discrete GPU matches it to a mean of 0.02 and no channel off by more than 1,
        // so this leaves room for other vendors and Mesa versions but not for a real change in the picture (Metal's bound is looser: see ReferenceTolerance).
        (double meanLimit, int worstLimit) = ReferenceTolerance;
        Assert.True(mean <= meanLimit && worst <= worstLimit, $"mean difference {mean:F3} (worst channel {worst}) exceeds {meanLimit} ({worstLimit}); the actual image is {actualPath}");
    }
}
