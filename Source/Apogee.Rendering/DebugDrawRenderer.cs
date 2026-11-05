using System;
using System.Numerics;
using Apogee.Core.Graphics;
using Apogee.Core.Numerics;
using Apogee.Graphics;

namespace Apogee.Rendering;

/// <summary>
/// Turns a <see cref="DebugDraw"/> into what <c>debug_draw.slang</c> reads: for each frame in flight, one buffer of lines and one of triangles, with every position already an offset from the camera
/// (the difference of the double positions is taken first, so a line a thousand kilometres out is as steady as one at the origin). The buffers grow when a frame needs more; the old one is freed
/// when the GPU is done with the frame that last used it.
/// </summary>
internal sealed unsafe class DebugDrawRenderer : IDisposable
{
    /// <summary>Two float4 per line: from.xyz and the color's bits, to.xyz.</summary>
    internal const int LineStride = 32;

    /// <summary>Three float4 per triangle: a.xyz and the color's bits, b.xyz, c.xyz.</summary>
    internal const int TriangleStride = 48;

    private readonly GraphicsDevice _device;
    private readonly GpuBuffer?[] _lines;
    private readonly GpuBuffer?[] _triangles;

    public DebugDrawRenderer(GraphicsDevice device, int framesInFlight)
    {
        _device = device;
        _lines = new GpuBuffer?[framesInFlight];
        _triangles = new GpuBuffer?[framesInFlight];
    }

    /// <summary>Writes the lines and triangles of <paramref name="draw"/> for <paramref name="frame"/>'s slot and returns where they are.</summary>
    public (ulong LineAddress, int Lines, ulong TriangleAddress, int Triangles) Upload(Frame frame, DebugDraw draw, Double3 camera)
    {
        int slot = frame.SlotIndex;
        ReadOnlySpan<DebugLine> lines = draw.Lines;
        ReadOnlySpan<DebugTriangle> triangles = draw.Triangles;

        ulong lineAddress = 0;
        if (lines.Length > 0)
        {
            GpuBuffer buffer = Ensure(frame, _lines, slot, (ulong)lines.Length * LineStride, "Debug lines");
            var destination = (Vector4*)buffer.Mapped;
            for (int i = 0; i < lines.Length; i++)
            {
                DebugLine line = lines[i];
                Vector3 from = (Vector3)(line.From - camera);
                Vector3 to = (Vector3)(line.To - camera);
                destination[i * 2] = new Vector4(from, BitConverter.UInt32BitsToSingle(line.Color));
                destination[(i * 2) + 1] = new Vector4(to, 0f);
            }

            lineAddress = buffer.Address;
        }

        ulong triangleAddress = 0;
        if (triangles.Length > 0)
        {
            GpuBuffer buffer = Ensure(frame, _triangles, slot, (ulong)triangles.Length * TriangleStride, "Debug triangles");
            var destination = (Vector4*)buffer.Mapped;
            for (int i = 0; i < triangles.Length; i++)
            {
                DebugTriangle triangle = triangles[i];
                destination[i * 3] = new Vector4((Vector3)(triangle.A - camera), BitConverter.UInt32BitsToSingle(triangle.Color));
                destination[(i * 3) + 1] = new Vector4((Vector3)(triangle.B - camera), 0f);
                destination[(i * 3) + 2] = new Vector4((Vector3)(triangle.C - camera), 0f);
            }

            triangleAddress = buffer.Address;
        }

        return (lineAddress, lines.Length, triangleAddress, triangles.Length);
    }

    private GpuBuffer Ensure(Frame frame, GpuBuffer?[] buffers, int slot, ulong needed, string name)
    {
        GpuBuffer? buffer = buffers[slot];
        if (buffer is null || buffer.Size < needed)
        {
            if (buffer is not null)
            {
                frame.Defer(buffer);
            }

            ulong grown = buffer is null ? 0 : buffer.Size + (buffer.Size / 2);
            buffer = buffers[slot] = _device.CreateBuffer(Math.Max(needed, Math.Max(grown, 4096)), BufferUsage.Storage, MemoryKind.Upload, $"{name} {slot}");
        }

        return buffer;
    }

    public void Dispose()
    {
        foreach (GpuBuffer? buffer in _lines)
        {
            buffer?.Dispose();
        }

        foreach (GpuBuffer? buffer in _triangles)
        {
            buffer?.Dispose();
        }
    }
}
