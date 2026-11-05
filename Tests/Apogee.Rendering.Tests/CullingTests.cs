using Apogee.Core.Graphics;
using System;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Graphics;
using Apogee.Graphics.Tests;
using Apogee.Native.Vulkan;
using Xunit;

namespace Apogee.Rendering.Tests;

public class FrustumFromCameraTests
{
    private static Frustum FrustumOf(Camera camera, float aspect)
    {
        Matrix4x4 view = camera.View;
        return Frustum.FromCamera(
            new Vector3(view.M11, view.M21, view.M31),
            new Vector3(view.M12, view.M22, view.M32),
            new Vector3(-view.M13, -view.M23, -view.M33),
            MathF.Tan(camera.VerticalFov * 0.5f),
            aspect,
            camera.Near);
    }

    /// <summary>Whether a point is inside the volume, by the projection matrix: after the view and the projection, |x| &lt;= w, |y| &lt;= w and (reverse-Z, infinite far) w &gt;= near.</summary>
    private static bool InsideByMatrix(Camera camera, float aspect, Vector3 point)
    {
        Matrix4x4 viewProjection = camera.View * camera.Projection(aspect);
        Vector4 clip = Vector4.Transform(new Vector4(point, 1f), viewProjection);
        return MathF.Abs(clip.X) <= clip.W && MathF.Abs(clip.Y) <= clip.W && clip.W >= camera.Near;
    }

    [Fact]
    public void ThePlanesAgreeWithTheProjectionMatrixForAnyCamera()
    {
        var random = new Random(11);
        int inside = 0;
        int disagreements = 0;
        for (int round = 0; round < 40; round++)
        {
            float aspect = 0.8f + (float)random.NextDouble();
            var camera = new Camera
            {
                Position = new Double3(random.NextDouble() * 1e6, random.NextDouble() * 100, random.NextDouble() * 1e6),
                Yaw = (float)(random.NextDouble() * 6.28),
                Pitch = (float)((random.NextDouble() - 0.5) * 3.0),
                VerticalFov = (float)(0.5 + random.NextDouble()),
                Near = (float)(0.05 + random.NextDouble()),
            };
            Frustum frustum = FrustumOf(camera, aspect);

            for (int i = 0; i < 400; i++)
            {
                var point = new Vector3((float)((random.NextDouble() - 0.5) * 80), (float)((random.NextDouble() - 0.5) * 80), (float)((random.NextDouble() - 0.5) * 80));
                bool byPlanes = frustum.Intersects(point, Vector3.Zero);
                bool byMatrix = InsideByMatrix(camera, aspect, point);
                inside += byMatrix ? 1 : 0;

                // A point within a hair of a plane may fall either way once rounded; anything else must agree.
                if (byPlanes != byMatrix && !NearAPlane(frustum, point))
                {
                    disagreements++;
                }
            }
        }

        Assert.True(inside > 500, $"only {inside} points were inside; the test needs both kinds");
        Assert.Equal(0, disagreements);
    }

    private static bool NearAPlane(Frustum frustum, Vector3 point)
    {
        for (int p = 0; p < Frustum.PlaneCount; p++)
        {
            Vector4 plane = frustum[p];
            float length = new Vector3(plane.X, plane.Y, plane.Z).Length();
            if (MathF.Abs((Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), point) + plane.W) / length) < 1e-3f)
            {
                return true;
            }
        }

        return false;
    }
}

public class CullingTests(GpuFixture gpu) : GpuTest(gpu)
{
    private const uint Width = 192;
    private const uint Height = 108;

    private byte[] Render(RenderScene scene, SceneRenderer renderer, Camera camera, bool cull, out RenderStats stats)
    {
        using var loop = Device.CreateFrameLoop();
        using GpuImage target = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, Width, Height, ImageUsage.ColorAttachment), "culling target");
        Frame frame = loop.BeginFrame()!;
        renderer.Render(frame, scene, camera, RenderTarget.Offscreen(target), new RenderSettings { CullDraws = cull });
        loop.EndFrame(frame);
        loop.WaitIdle();
        stats = renderer.LastStats!;
        return Device.ReadImage(target, ImageState.TransferSource);
    }

    [Fact]
    public void CullingLeavesTheImageAloneAndDropsWhatIsBehindTheCamera()
    {
        using var renderer = new SceneRenderer(Device, cacheDirectory: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apogee-test-shader-cache"));
        using RenderScene scene = renderer.CreateScene(GoldenScene.Build());
        int total = scene.MeshCount;
        Assert.True(total > 3);

        // Looking at the scene: the picture is the same with and without culling.
        var toward = new Camera { Position = new Double3(-0.5, 4.2, 9) };
        toward.LookAt(new Double3(0, 0.8, 0));
        byte[] culledImage = Render(scene, renderer, toward, cull: true, out RenderStats culledStats);
        byte[] fullImage = Render(scene, renderer, toward, cull: false, out RenderStats fullStats);
        Assert.Equal(fullImage, culledImage);
        Assert.Equal(0, fullStats.Culled);
        Assert.True(culledStats.Draws <= fullStats.Draws);
        Assert.True(culledStats.Culled <= total);

        // Turned away: everything is behind the camera, nothing is drawn, and the picture is still the sky.
        var away = new Camera { Position = new Double3(-0.5, 4.2, 9), Yaw = MathF.PI };
        byte[] awayCulled = Render(scene, renderer, away, cull: true, out RenderStats awayStats);
        byte[] awayFull = Render(scene, renderer, away, cull: false, out _);
        Assert.Equal(total, awayStats.Culled);
        Assert.Equal(0, awayStats.Draws);
        Assert.Equal(awayFull, awayCulled);
    }
}
