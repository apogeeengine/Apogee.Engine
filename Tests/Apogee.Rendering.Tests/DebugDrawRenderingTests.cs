using Apogee.Core.Graphics;
using System;
using System.IO;
using System.Numerics;
using Apogee.Content;
using Apogee.Core.Numerics;
using Apogee.Graphics;
using Apogee.Graphics.Tests;
using Xunit;

namespace Apogee.Rendering.Tests;

/// <summary>Debug lines and triangles drawn over the lit scene, read back from the image.</summary>
public class DebugDrawRenderingTests(GpuFixture gpu) : GpuTest(gpu)
{
    private const uint Width = 192;
    private const uint Height = 108;

    private static readonly string s_shaderCache = Path.Combine(Path.GetTempPath(), "apogee-test-shader-cache");

    private sealed class Rig : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly GpuImage _target;
        private readonly FrameLoop _loop;

        public Rig(GraphicsDevice device, ModelData model)
        {
            _device = device;
            Renderer = new SceneRenderer(device, cacheDirectory: s_shaderCache);
            Scene = Renderer.CreateScene(model);
            _target = device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, Width, Height, ImageUsage.ColorAttachment), "Debug draw target");
            _loop = device.CreateFrameLoop();
        }

        public SceneRenderer Renderer { get; }

        public RenderScene Scene { get; }

        public byte[] Render(Camera camera, DebugDraw? draw, float hiddenAlpha = 0.25f, float width = 1.5f)
        {
            Frame frame = _loop.BeginFrame()!;
            Renderer.Render(frame, Scene, camera, RenderTarget.Offscreen(_target), new RenderSettings { DebugDraw = draw, DebugHiddenAlpha = hiddenAlpha, DebugLineWidth = width });
            _loop.EndFrame(frame);
            _loop.WaitIdle();
            return _device.ReadImage(_target, ImageState.TransferSource);
        }

        public void Dispose()
        {
            _loop.WaitIdle();
            _loop.Dispose();
            _target.Dispose();
            Scene.Dispose();
            Renderer.Dispose();
        }
    }

    private static ModelData Empty() => new TestScene().ToModel();

    private static ModelData WallInFront()
    {
        var scene = new TestScene();
        int gray = scene.AddMaterial(new MaterialData { BaseColorFactor = new Vector4(0.5f, 0.5f, 0.5f, 1f), MetallicFactor = 0f, RoughnessFactor = 1f });
        scene.AddWall(new Vector3(0, 1, 1), 4f, 3f, gray);
        return scene.ToModel();
    }

    private static Camera Looking(Double3 from, Double3 at)
    {
        var camera = new Camera { Position = from };
        camera.LookAt(at);
        return camera;
    }

    /// <summary>The pixels that are clearly red: the debug color of the tests, which neither the sky nor the gray wall comes close to.</summary>
    private static int RedPixels(byte[] image)
    {
        int count = 0;
        for (int i = 0; i < image.Length; i += 4)
        {
            if (image[i] > 150 && image[i] > image[i + 1] + 70 && image[i] > image[i + 2] + 70)
            {
                count++;
            }
        }

        return count;
    }

    private static readonly uint s_red = DebugColor.Rgba(255, 0, 0);

    [Fact]
    public void ALineIsDrawnAsAWideStripeOfItsColor()
    {
        using var rig = new Rig(Device, Empty());
        Camera camera = Looking(new Double3(0, 1, 6), new Double3(0, 1, 0));
        var draw = new DebugDraw();
        draw.Line(new Double3(-2, 1, 0), new Double3(2, 1, 0), s_red);

        byte[] without = rig.Render(camera, null);
        byte[] with = rig.Render(camera, draw, width: 3f);

        Assert.Equal(0, RedPixels(without));
        // About four meters at six meters distance with a 60 degree lens across 192 pixels: the stripe is some 70 pixels long and three wide.
        int red = RedPixels(with);
        Assert.InRange(red, 120, 330);

        // A wider line covers more.
        Assert.True(RedPixels(rig.Render(camera, draw, width: 8f)) > red * 2, "a wider line covers more pixels");
    }

    [Fact]
    public void WhatIsBehindASurfaceIsHiddenOrFaintAsAsked()
    {
        using var rig = new Rig(Device, WallInFront());
        Camera camera = Looking(new Double3(0, 1, 6), new Double3(0, 1, 0));
        var draw = new DebugDraw();
        draw.Line(new Double3(-2, 1, -1), new Double3(2, 1, -1), s_red); // a meter behind the wall

        Assert.Equal(0, RedPixels(rig.Render(camera, draw, hiddenAlpha: 0f)));       // hidden: the wall is all there is
        Assert.True(RedPixels(rig.Render(camera, draw, hiddenAlpha: 1f)) > 50);      // seen through the wall, as strongly as a visible one

        // In front of the wall, depth testing does not hide it.
        var near = new DebugDraw();
        near.Line(new Double3(-2, 1, 2), new Double3(2, 1, 2), s_red);
        Assert.True(RedPixels(rig.Render(camera, near, hiddenAlpha: 0f)) > 50);
    }

    [Fact]
    public void ALineBehindTheCameraOrCrossingItsNearPlaneDoesNotBreakThePicture()
    {
        using var rig = new Rig(Device, Empty());
        Camera camera = Looking(new Double3(0, 1, 6), new Double3(0, 1, 0));
        byte[] without = rig.Render(camera, null);

        var behind = new DebugDraw();
        behind.Line(new Double3(-2, 1, 8), new Double3(2, 1, 9), s_red);
        Assert.Equal(without, rig.Render(camera, behind));

        // One end behind the camera and one in front: it is cut at the near plane, and the part in front is drawn.
        var crossing = new DebugDraw();
        crossing.Line(new Double3(4, 1, 9), new Double3(-1, 1, 0), s_red);
        byte[] image = rig.Render(camera, crossing, width: 3f);
        Assert.True(RedPixels(image) > 10, "the part in front of the camera is drawn");
        Assert.True(RedPixels(image) < 600, "and nothing swings across the screen");
    }

    [Fact]
    public void LinesFarFromTheOriginLandOnTheSamePixels()
    {
        using var rig = new Rig(Device, Empty());
        var far = new Double3(3_000_000, 50, -2_000_000);

        var near = new DebugDraw();
        near.Line(new Double3(-2, 1, 0), new Double3(2, 1, 0), s_red);
        byte[] atOrigin = rig.Render(Looking(new Double3(0, 1, 6), new Double3(0, 1, 0)), near, width: 3f);

        var shifted = new DebugDraw();
        shifted.Line(far + new Double3(-2, 1, 0), far + new Double3(2, 1, 0), s_red);
        byte[] atFar = rig.Render(Looking(far + new Double3(0, 1, 6), far + new Double3(0, 1, 0)), shifted, width: 3f);

        Assert.True(RedPixels(atOrigin) > 100);
        int different = 0;
        for (int i = 0; i < atOrigin.Length; i += 4)
        {
            different += atOrigin[i] != atFar[i] || atOrigin[i + 1] != atFar[i + 1] ? 1 : 0;
        }

        Assert.True(different < 20, $"{different} pixels differ between the origin and 3 000 km out");
    }

    [Fact]
    public void ATriangleIsDrawnSolidAndShaded()
    {
        using var rig = new Rig(Device, Empty());
        Camera camera = Looking(new Double3(0, 1, 6), new Double3(0, 1, 0));
        var draw = new DebugDraw();
        draw.Triangle(new Double3(-1.5, 0, 0), new Double3(1.5, 0, 0), new Double3(0, 2.5, 0), s_red);

        byte[] image = rig.Render(camera, draw);

        // Facing the camera: bright red in the middle of the picture, sky where the triangle is not.
        int center = ((int)(Height / 2) * (int)Width + (int)(Width / 2)) * 4;
        Assert.True(image[center] > 150 && image[center + 1] < 60, $"the center is {image[center]},{image[center + 1]},{image[center + 2]}");
        Assert.True(RedPixels(image) > 800, $"{RedPixels(image)} red pixels");
    }

    [Fact]
    public void TheRendererLeavesTheDebugDrawAloneAndAnEmptyOneAddsNoPass()
    {
        using var rig = new Rig(Device, Empty());
        Camera camera = Looking(new Double3(0, 1, 6), new Double3(0, 1, 0));
        var draw = new DebugDraw();
        draw.Line(new Double3(-2, 1, 0), new Double3(2, 1, 0), s_red);

        rig.Render(camera, draw);
        Assert.Equal(1, draw.LineCount); // whoever filled it clears it
        Assert.Contains(rig.Renderer.LastPlan!.Passes, p => p.Name == "Debug draw");

        draw.Clear();
        rig.Render(camera, draw);
        Assert.DoesNotContain(rig.Renderer.LastPlan!.Passes, p => p.Name == "Debug draw");
    }
}
