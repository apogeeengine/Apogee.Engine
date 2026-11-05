using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apogee.Content;
using Xunit;

namespace Apogee.Rendering.Tests;

/// <summary>The CPU side of the renderer: the structs the shaders read, the camera and the cluster grid. No GPU.</summary>
public class LayoutAndMathTests
{
    [Fact]
    public void TheStructsHaveTheSizesTheShadersExpect()
    {
        Assert.Equal(48, Unsafe.SizeOf<Vertex>());
        Assert.Equal(144, Unsafe.SizeOf<GpuInstance>());      // two matrices and four uints
        Assert.Equal(272, Unsafe.SizeOf<GpuMaterial>());
        Assert.Equal(64, Unsafe.SizeOf<GpuLight>());
        Assert.Equal(400, Unsafe.SizeOf<GpuFrameData>());
        Assert.Equal(32, Unsafe.SizeOf<GpuDrawPush>());
        Assert.Equal(16, Unsafe.SizeOf<GpuTonemapPush>());
    }

    [Fact]
    public void FrameDataPutsEachFieldWhereCommonSlangDoes()
    {
        // common.slang: three matrices, nine 16-byte vectors, then eight 8-byte pointers.
        Assert.Equal(192, (int)Marshal.OffsetOf<GpuFrameData>(nameof(GpuFrameData.CameraPosition)));
        Assert.Equal(240, (int)Marshal.OffsetOf<GpuFrameData>(nameof(GpuFrameData.Counts)));
        Assert.Equal(320, (int)Marshal.OffsetOf<GpuFrameData>(nameof(GpuFrameData.ProjScale)));
        Assert.Equal(336, (int)Marshal.OffsetOf<GpuFrameData>(nameof(GpuFrameData.Instances)));
        Assert.Equal(376, (int)Marshal.OffsetOf<GpuFrameData>(nameof(GpuFrameData.ClusterLights)));
        Assert.Equal(128, (int)Marshal.OffsetOf<GpuInstance>(nameof(GpuInstance.VertexOffset)));
        Assert.Equal(112, (int)Marshal.OffsetOf<GpuMaterial>(nameof(GpuMaterial.Transforms)));
    }

    [Fact]
    public void ReverseZMapsTheNearPlaneToOneAndInfinityToZero()
    {
        Matrix4x4 projection = Camera.ReverseZ(MathF.PI / 2f, 1f, 0.5f);

        static float Depth(Vector3 viewPoint, Matrix4x4 m)
        {
            Vector4 clip = Vector4.Transform(new Vector4(viewPoint, 1f), m);
            return clip.Z / clip.W;
        }

        Assert.Equal(1f, Depth(new Vector3(0, 0, -0.5f), projection), 1e-6f);
        Assert.Equal(0.5f, Depth(new Vector3(0, 0, -1f), projection), 1e-6f);
        Assert.Equal(0.5f / 10000f, Depth(new Vector3(0, 0, -10000f), projection), 1e-7f);
    }

    [Fact]
    public void TheProjectionFlipsYSoThatNdcYPointsDown()
    {
        Matrix4x4 projection = Camera.ReverseZ(MathF.PI / 2f, 1f, 0.1f);

        Vector4 above = Vector4.Transform(new Vector4(0, 1, -1, 1), projection);

        Assert.True(above.Y / above.W < 0);         // a point above the axis lands in the top half of the image, which is negative y
    }

    [Fact]
    public void ACameraLooksDownMinusZAtYawZeroAndTurnsLeft()
    {
        var camera = new Camera();
        Assert.Equal(-Vector3.UnitZ, camera.Forward);

        camera.Yaw = MathF.PI / 2f;
        Assert.Equal(-1f, camera.Forward.X, 1e-6f);        // positive yaw turns left (towards -X)
        camera.Yaw = 0;
        camera.Pitch = MathF.PI / 2f;
        Assert.Equal(1f, camera.Forward.Y, 1e-6f);
    }

    [Fact]
    public void LookingStraightDownGivesAValidViewWithNorthUp()
    {
        var camera = new Camera { Position = new Vector3(0, 10, 0), Pitch = -MathF.PI / 2f };

        Matrix4x4 view = camera.View;

        Assert.False(float.IsNaN(view.M11) || float.IsNaN(view.M42));
        Assert.Equal(1f, Vector3.Transform(new Vector3(1, 0, 0), view).X, 1e-5f);     // +X is to the right
        Assert.True(Vector3.Transform(new Vector3(0, 0, -1), view).Y > 0.99f);         // -Z (north) is up on the screen
        Assert.Equal(-10f, camera.WorldToView(Apogee.Core.Numerics.Double3.Zero).Z, 1e-4f);   // the origin is 10 m in front
        Assert.Equal(0f, Vector3.Transform(Vector3.Zero, view).Z, 1e-6f);              // the view itself has the camera at the origin
    }

    [Fact]
    public void LookAtAimsTheCamera()
    {
        var camera = new Camera { Position = new Vector3(1, 2, 3) };
        camera.LookAt(new Vector3(1, 2, -7));
        Assert.Equal(-Vector3.UnitZ, camera.Forward);

        camera.LookAt(new Vector3(11, 12, 3));
        Vector3 expected = Vector3.Normalize(new Vector3(10, 10, 0));
        Assert.Equal(expected.X, camera.Forward.X, 1e-5f);
        Assert.Equal(expected.Y, camera.Forward.Y, 1e-5f);
        Assert.Equal(expected.Z, camera.Forward.Z, 1e-5f);
    }

    [Fact]
    public void SlicesStartAtNearEndAtFarAndGrowGeometrically()
    {
        Assert.Equal(0.1f, ClusterGrid.SliceDepth(0.1f, 100f, 0), 1e-6f);
        Assert.Equal(100f, ClusterGrid.SliceDepth(0.1f, 100f, 24), 1e-3f);

        float previousRatio = 0f;
        for (int k = 0; k < ClusterGrid.CountZ; k++)
        {
            float ratio = ClusterGrid.SliceDepth(0.1f, 100f, k + 1) / ClusterGrid.SliceDepth(0.1f, 100f, k);
            if (k > 0)
            {
                Assert.Equal(previousRatio, ratio, 1e-4f);
            }

            previousRatio = ratio;
        }
    }

    [Fact]
    public void SliceOfIsTheInverseOfSliceDepth()
    {
        for (int k = 0; k < ClusterGrid.CountZ; k++)
        {
            float start = ClusterGrid.SliceDepth(0.1f, 100f, k);
            float end = ClusterGrid.SliceDepth(0.1f, 100f, k + 1);
            Assert.Equal(k, ClusterGrid.SliceOf(0.1f, 100f, start * 1.001f));
            Assert.Equal(k, ClusterGrid.SliceOf(0.1f, 100f, end * 0.999f));
        }

        Assert.Equal(0, ClusterGrid.SliceOf(0.1f, 100f, 0.01f));                       // closer than near
        Assert.Equal(ClusterGrid.CountZ - 1, ClusterGrid.SliceOf(0.1f, 100f, 5000f));   // beyond far
    }

    [Fact]
    public void AClusterBoxContainsEveryPointThatProjectsIntoItsCell()
    {
        const float near = 0.1f;
        const float far = 100f;
        Matrix4x4 projection = Camera.ReverseZ(60f * MathF.PI / 180f, 16f / 9f, near);
        var random = new Random(7);

        for (int i = 0; i < 2000; i++)
        {
            // A random point in front of the camera, and the cell the projection says it is in.
            var view = new Vector3((float)(random.NextDouble() * 20 - 10), (float)(random.NextDouble() * 12 - 6), -(float)(near + random.NextDouble() * (far - near)));
            Vector4 clip = Vector4.Transform(new Vector4(view, 1f), projection);
            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            if (MathF.Abs(ndcX) >= 1f || MathF.Abs(ndcY) >= 1f)
            {
                continue;
            }

            int x = (int)((ndcX * 0.5f + 0.5f) * ClusterGrid.CountX);
            int y = (int)((ndcY * 0.5f + 0.5f) * ClusterGrid.CountY);
            int z = ClusterGrid.SliceOf(near, far, -view.Z);
            (Vector3 min, Vector3 max) = ClusterGrid.Bounds(x, y, z, near, far, projection.M11, projection.M22);

            const float slack = 1e-3f;
            Assert.True(view.X >= min.X - slack && view.X <= max.X + slack && view.Y >= min.Y - slack && view.Y <= max.Y + slack && view.Z >= min.Z - slack && view.Z <= max.Z + slack,
                $"{view} is outside cluster ({x},{y},{z}) {min}..{max}");
        }
    }

    [Fact]
    public void ClusterIndexOrdersXThenYThenZ()
    {
        Assert.Equal(0, ClusterGrid.Index(0, 0, 0));
        Assert.Equal(1, ClusterGrid.Index(1, 0, 0));
        Assert.Equal(ClusterGrid.CountX, ClusterGrid.Index(0, 1, 0));
        Assert.Equal(ClusterGrid.CountX * ClusterGrid.CountY, ClusterGrid.Index(0, 0, 1));
        Assert.Equal(ClusterGrid.Total - 1, ClusterGrid.Index(ClusterGrid.CountX - 1, ClusterGrid.CountY - 1, ClusterGrid.CountZ - 1));
    }

    [Fact]
    public void TextureTransformRowsFollowTheKhrSpec()
    {
        (Vector4 identity0, Vector4 identity1) = RenderScene.TransformRows(null);
        Assert.Equal(new Vector4(1, 0, 0, 0), identity0);
        Assert.Equal(new Vector4(0, 1, 0, 0), identity1);

        // Offset only.
        (Vector4 row0, Vector4 row1) = RenderScene.TransformRows(new TextureRef(0, 0, new Vector2(0.25f, 0.5f), Vector2.One, 0f));
        Assert.Equal(new Vector4(1, 0, 0.25f, 0), row0);
        Assert.Equal(new Vector4(0, 1, 0.5f, 0), row1);

        // A quarter turn with scale: uv' = T * R * S * uv.
        (row0, row1) = RenderScene.TransformRows(new TextureRef(0, 0, Vector2.Zero, new Vector2(2, 3), MathF.PI / 2f));
        Assert.Equal(0f, row0.X, 1e-6f);
        Assert.Equal(3f, row0.Y, 1e-6f);
        Assert.Equal(-2f, row1.X, 1e-6f);
        Assert.Equal(0f, row1.Y, 1e-6f);
    }
}
