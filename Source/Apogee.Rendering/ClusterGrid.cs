using System;
using System.Numerics;

namespace Apogee.Rendering;

/// <summary>
/// The grid lights are culled against: 16 x 9 cells over the screen and 24 slices in depth, spaced exponentially from the camera's near plane
/// to <see cref="Camera.ClusterFar"/> so that near slices are thin. This is the CPU twin of the shaders (cluster_build and common.slang), for
/// tests and tools.
/// </summary>
public static class ClusterGrid
{
    public const int CountX = 16;
    public const int CountY = 9;
    public const int CountZ = 24;
    public const int Total = CountX * CountY * CountZ;
    public const int MaxLightsPerCluster = 64;

    /// <summary>Uints per cluster in the light list: the count, then the indices.</summary>
    public const int Stride = MaxLightsPerCluster + 1;

    public static int Index(int x, int y, int z) => (z * CountY + y) * CountX + x;

    /// <summary>The distance in front of the camera where slice <paramref name="slice"/> starts (slice 24 is the far end).</summary>
    public static float SliceDepth(float near, float far, int slice) => near * MathF.Pow(far / near, slice / (float)CountZ);

    /// <summary>The slice a distance in front of the camera falls in; closer than near is slice 0, farther than far the last.</summary>
    public static int SliceOf(float near, float far, float viewDepth)
    {
        float slice = MathF.Log(MathF.Max(viewDepth, near) / near) / MathF.Log(far / near) * CountZ;
        return Math.Min((int)slice, CountZ - 1);
    }

    /// <summary>The view-space box around a cluster. <paramref name="projX"/> and <paramref name="projY"/> are the projection's [0,0] and [1,1] (the latter negative).</summary>
    public static (Vector3 Min, Vector3 Max) Bounds(int x, int y, int z, float near, float far, float projX, float projY)
    {
        Vector2 ndcMin = new Vector2((float)x / CountX, (float)y / CountY) * 2f - Vector2.One;
        Vector2 ndcMax = new Vector2((float)(x + 1) / CountX, (float)(y + 1) / CountY) * 2f - Vector2.One;
        float zNear = SliceDepth(near, far, z);
        float zFar = SliceDepth(near, far, z + 1);

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector2 ndc = new((corner & 1) != 0 ? ndcMax.X : ndcMin.X, (corner & 2) != 0 ? ndcMax.Y : ndcMin.Y);
            float depth = (corner & 4) != 0 ? zFar : zNear;
            var p = new Vector3(ndc.X * depth / projX, ndc.Y * depth / projY, -depth);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return (min, max);
    }
}
