using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Apogee.Core.Numerics;

/// <summary>
/// Axis-aligned boxes as separate arrays of centers and half extents (structure of arrays), so that a vector instruction tests eight (or four) of them against a
/// plane at once. The arrays are padded to a multiple of <see cref="FrustumCulling.Block"/> with boxes of no size at the origin, which a kernel tests like any other;
/// their results are meaningless and ignored (only the first <see cref="Count"/> matter).
/// </summary>
public sealed class BoxSet
{
    public BoxSet(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Count = count;
        PaddedCount = (count + FrustumCulling.Block - 1) / FrustumCulling.Block * FrustumCulling.Block;
        CenterX = new float[PaddedCount];
        CenterY = new float[PaddedCount];
        CenterZ = new float[PaddedCount];
        ExtentX = new float[PaddedCount];
        ExtentY = new float[PaddedCount];
        ExtentZ = new float[PaddedCount];
    }

    public int Count { get; }

    public int PaddedCount { get; }

    internal float[] CenterX { get; }

    internal float[] CenterY { get; }

    internal float[] CenterZ { get; }

    internal float[] ExtentX { get; }

    internal float[] ExtentY { get; }

    internal float[] ExtentZ { get; }

    /// <summary>The number of bytes a result needs: one bit per box.</summary>
    public int ResultLength => PaddedCount / FrustumCulling.Block;

    public void Set(int index, Vector3 center, Vector3 extents)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count);
        CenterX[index] = center.X;
        CenterY[index] = center.Y;
        CenterZ[index] = center.Z;
        ExtentX[index] = extents.X;
        ExtentY[index] = extents.Y;
        ExtentZ[index] = extents.Z;
    }

    public (Vector3 Center, Vector3 Extents) Get(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count);
        return (new Vector3(CenterX[index], CenterY[index], CenterZ[index]), new Vector3(ExtentX[index], ExtentY[index], ExtentZ[index]));
    }
}

/// <summary>
/// Tests boxes against a <see cref="Frustum"/>, writing a bit per box (set: at least partly inside): bit <c>k</c> of byte <c>b</c> is box <c>8b + k</c>.
/// There are three kernels with the same arithmetic in the same order, so they give the same bits: <see cref="CullScalar"/>, <see cref="CullVector128"/> (four boxes
/// per instruction) and <see cref="CullVector256"/> (eight). <see cref="Cull"/> picks the widest one the process runs fast: NativeAOT builds for x86-64-v2 have 128-bit
/// vectors only (see the profiling chapter of the manual), CoreCLR on a modern CPU has 256.
/// </summary>
public static class FrustumCulling
{
    /// <summary>The boxes are processed (and padded) in blocks of this many.</summary>
    public const int Block = 8;

    private const int Floats = 7; // per plane: nx, ny, nz, |nx|, |ny|, |nz|, d

    public static void Cull(in Frustum frustum, BoxSet boxes, Span<byte> visible)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            CullVector256(frustum, boxes, visible);
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            CullVector128(frustum, boxes, visible);
        }
        else
        {
            CullScalar(frustum, boxes, visible);
        }
    }

    /// <summary>The number of boxes among the first <see cref="BoxSet.Count"/> whose bit is set.</summary>
    public static int CountVisible(ReadOnlySpan<byte> visible, int count)
    {
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            total += (visible[i >> 3] >> (i & 7)) & 1;
        }

        return total;
    }

    public static bool IsVisible(ReadOnlySpan<byte> visible, int index) => ((visible[index >> 3] >> (index & 7)) & 1) != 0;

    public static void CullScalar(in Frustum frustum, BoxSet boxes, Span<byte> visible)
    {
        Check(boxes, visible);
        for (int i = 0; i < boxes.PaddedCount; i++)
        {
            var center = new Vector3(boxes.CenterX[i], boxes.CenterY[i], boxes.CenterZ[i]);
            var extents = new Vector3(boxes.ExtentX[i], boxes.ExtentY[i], boxes.ExtentZ[i]);
            int mask = 1 << (i & 7);
            if (frustum.Intersects(center, extents))
            {
                visible[i >> 3] |= (byte)mask;
            }
            else
            {
                visible[i >> 3] &= (byte)~mask;
            }
        }
    }

    public static void CullVector128(in Frustum frustum, BoxSet boxes, Span<byte> visible)
    {
        Check(boxes, visible);
        Span<float> planes = stackalloc float[Frustum.PlaneCount * Floats];
        Unpack(frustum, planes);
        ref float cx = ref MemoryMarshal.GetArrayDataReference(boxes.CenterX);
        ref float cy = ref MemoryMarshal.GetArrayDataReference(boxes.CenterY);
        ref float cz = ref MemoryMarshal.GetArrayDataReference(boxes.CenterZ);
        ref float ex = ref MemoryMarshal.GetArrayDataReference(boxes.ExtentX);
        ref float ey = ref MemoryMarshal.GetArrayDataReference(boxes.ExtentY);
        ref float ez = ref MemoryMarshal.GetArrayDataReference(boxes.ExtentZ);

        for (int block = 0; block < boxes.ResultLength; block++)
        {
            uint outside = Outside4(planes, ref cx, ref cy, ref cz, ref ex, ref ey, ref ez, (nuint)(block * Block))
                | (Outside4(planes, ref cx, ref cy, ref cz, ref ex, ref ey, ref ez, (nuint)((block * Block) + 4)) << 4);
            visible[block] = (byte)~outside;
        }
    }

    private static uint Outside4(ReadOnlySpan<float> planes, ref float cx, ref float cy, ref float cz, ref float ex, ref float ey, ref float ez, nuint i)
    {
        Vector128<float> x = Vector128.LoadUnsafe(ref cx, i);
        Vector128<float> y = Vector128.LoadUnsafe(ref cy, i);
        Vector128<float> z = Vector128.LoadUnsafe(ref cz, i);
        Vector128<float> hx = Vector128.LoadUnsafe(ref ex, i);
        Vector128<float> hy = Vector128.LoadUnsafe(ref ey, i);
        Vector128<float> hz = Vector128.LoadUnsafe(ref ez, i);
        Vector128<float> outside = Vector128<float>.Zero;
        for (int p = 0; p < Frustum.PlaneCount; p++)
        {
            ReadOnlySpan<float> plane = planes.Slice(p * Floats, Floats);
            Vector128<float> side = ((Vector128.Create(plane[0]) * x) + (Vector128.Create(plane[1]) * y)) + (Vector128.Create(plane[2]) * z);
            Vector128<float> reach = ((Vector128.Create(plane[3]) * hx) + (Vector128.Create(plane[4]) * hy)) + (Vector128.Create(plane[5]) * hz);
            Vector128<float> distance = (side + reach) + Vector128.Create(plane[6]);
            outside |= Vector128.LessThan(distance, Vector128<float>.Zero);
        }

        return outside.ExtractMostSignificantBits();
    }

    public static void CullVector256(in Frustum frustum, BoxSet boxes, Span<byte> visible)
    {
        Check(boxes, visible);
        Span<float> planes = stackalloc float[Frustum.PlaneCount * Floats];
        Unpack(frustum, planes);
        ref float cx = ref MemoryMarshal.GetArrayDataReference(boxes.CenterX);
        ref float cy = ref MemoryMarshal.GetArrayDataReference(boxes.CenterY);
        ref float cz = ref MemoryMarshal.GetArrayDataReference(boxes.CenterZ);
        ref float ex = ref MemoryMarshal.GetArrayDataReference(boxes.ExtentX);
        ref float ey = ref MemoryMarshal.GetArrayDataReference(boxes.ExtentY);
        ref float ez = ref MemoryMarshal.GetArrayDataReference(boxes.ExtentZ);

        for (int block = 0; block < boxes.ResultLength; block++)
        {
            nuint i = (nuint)(block * Block);
            Vector256<float> x = Vector256.LoadUnsafe(ref cx, i);
            Vector256<float> y = Vector256.LoadUnsafe(ref cy, i);
            Vector256<float> z = Vector256.LoadUnsafe(ref cz, i);
            Vector256<float> hx = Vector256.LoadUnsafe(ref ex, i);
            Vector256<float> hy = Vector256.LoadUnsafe(ref ey, i);
            Vector256<float> hz = Vector256.LoadUnsafe(ref ez, i);
            Vector256<float> outside = Vector256<float>.Zero;
            for (int p = 0; p < Frustum.PlaneCount; p++)
            {
                ReadOnlySpan<float> plane = planes.Slice(p * Floats, Floats);
                Vector256<float> side = ((Vector256.Create(plane[0]) * x) + (Vector256.Create(plane[1]) * y)) + (Vector256.Create(plane[2]) * z);
                Vector256<float> reach = ((Vector256.Create(plane[3]) * hx) + (Vector256.Create(plane[4]) * hy)) + (Vector256.Create(plane[5]) * hz);
                Vector256<float> distance = (side + reach) + Vector256.Create(plane[6]);
                outside |= Vector256.LessThan(distance, Vector256<float>.Zero);
            }

            visible[block] = (byte)~outside.ExtractMostSignificantBits();
        }
    }

    private static void Unpack(in Frustum frustum, Span<float> planes)
    {
        for (int p = 0; p < Frustum.PlaneCount; p++)
        {
            Vector4 plane = frustum[p];
            planes[(p * Floats) + 0] = plane.X;
            planes[(p * Floats) + 1] = plane.Y;
            planes[(p * Floats) + 2] = plane.Z;
            planes[(p * Floats) + 3] = MathF.Abs(plane.X);
            planes[(p * Floats) + 4] = MathF.Abs(plane.Y);
            planes[(p * Floats) + 5] = MathF.Abs(plane.Z);
            planes[(p * Floats) + 6] = plane.W;
        }
    }

    private static void Check(BoxSet boxes, Span<byte> visible)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        if (visible.Length < boxes.ResultLength)
        {
            throw new ArgumentException($"The result needs {boxes.ResultLength} bytes for {boxes.Count} boxes.", nameof(visible));
        }
    }
}
