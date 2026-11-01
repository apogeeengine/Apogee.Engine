using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Apogee.Core.Numerics;

namespace Apogee.Core.Graphics;

/// <summary>A line to draw, in world positions. The color is bytes r, g, b, a (see <see cref="DebugColor"/>). The layout is that of Jolt's <c>AjDebugLine</c> (56 bytes), so physics copies its lines in as they are.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct DebugLine(Double3 From, Double3 To, uint Color)
{
    private readonly uint _padding = 0;
}

/// <summary>A triangle to draw, in world positions, wound counter-clockwise seen from outside. The layout is that of Jolt's <c>AjDebugTriangle</c> (80 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct DebugTriangle(Double3 A, Double3 B, Double3 C, uint Color)
{
    private readonly uint _padding = 0;
}

/// <summary>Colors for <see cref="DebugDraw"/>: bytes r, g, b, a packed as a little-endian <see cref="uint"/> (0xAABBGGRR).</summary>
public static class DebugColor
{
    public static uint Rgba(byte r, byte g, byte b, byte a = 255) => r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

    public static uint FromVector(Vector4 color) => Rgba(Channel(color.X), Channel(color.Y), Channel(color.Z), Channel(color.W));

    private static byte Channel(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    public static uint White { get; } = Rgba(255, 255, 255);

    public static uint Gray { get; } = Rgba(128, 128, 128);

    public static uint Red { get; } = Rgba(230, 60, 60);

    public static uint Green { get; } = Rgba(70, 200, 90);

    public static uint Blue { get; } = Rgba(70, 120, 240);

    public static uint Yellow { get; } = Rgba(240, 210, 60);

    public static uint Cyan { get; } = Rgba(60, 210, 220);

    public static uint Magenta { get; } = Rgba(220, 80, 200);

    public static uint Orange { get; } = Rgba(240, 150, 50);
}

/// <summary>
/// Immediate-mode debug drawing: lines and triangles in world positions that live for one frame. Anything may add to it (gameplay, physics, tools) with no knowledge of
/// rendering; the renderer draws what is in it when a frame is rendered and the owner then <see cref="Clear"/>s it for the next. Positions are double precision, like the world's,
/// and are turned into offsets from the camera only when drawn. The number of lines and triangles is capped (<see cref="Capacity"/>); what does not fit is counted in
/// <see cref="Dropped"/> and left out, so a loop that draws too much costs a frame's detail and not the program's memory.
/// </summary>
public sealed class DebugDraw
{
    private readonly List<DebugLine> _lines = [];
    private readonly List<DebugTriangle> _triangles = [];

    /// <summary>The most lines, and the most triangles, that are kept.</summary>
    public int Capacity { get; set; } = 1_000_000;

    /// <summary>How many lines and triangles did not fit since the last <see cref="Clear"/>.</summary>
    public int Dropped { get; private set; }

    public int LineCount => _lines.Count;

    public int TriangleCount => _triangles.Count;

    public ReadOnlySpan<DebugLine> Lines => CollectionsMarshal.AsSpan(_lines);

    public ReadOnlySpan<DebugTriangle> Triangles => CollectionsMarshal.AsSpan(_triangles);

    public bool IsEmpty => _lines.Count == 0 && _triangles.Count == 0;

    public void Clear()
    {
        _lines.Clear();
        _triangles.Clear();
        Dropped = 0;
    }

    public void Line(Double3 from, Double3 to, uint color)
    {
        if (_lines.Count >= Capacity)
        {
            Dropped++;
            return;
        }

        _lines.Add(new DebugLine(from, to, color));
    }

    public void Triangle(Double3 a, Double3 b, Double3 c, uint color)
    {
        if (_triangles.Count >= Capacity)
        {
            Dropped++;
            return;
        }

        _triangles.Add(new DebugTriangle(a, b, c, color));
    }

    /// <summary>Adds lines made elsewhere (physics hands over what Jolt collected in one go).</summary>
    public void AddLines(ReadOnlySpan<DebugLine> lines)
    {
        int room = Math.Max(0, Capacity - _lines.Count);
        if (lines.Length > room)
        {
            Dropped += lines.Length - room;
            lines = lines[..room];
        }

        _lines.AddRange(lines);
    }

    public void AddTriangles(ReadOnlySpan<DebugTriangle> triangles)
    {
        int room = Math.Max(0, Capacity - _triangles.Count);
        if (triangles.Length > room)
        {
            Dropped += triangles.Length - room;
            triangles = triangles[..room];
        }

        _triangles.AddRange(triangles);
    }

    /// <summary>The twelve edges of a box centered at <paramref name="center"/>, <paramref name="halfExtents"/> from the center to each face before it is turned by <paramref name="rotation"/>.</summary>
    public void Box(Double3 center, Vector3 halfExtents, Quaternion rotation, uint color)
    {
        Span<Vector3> corner = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3((i & 1) == 0 ? -halfExtents.X : halfExtents.X, (i & 2) == 0 ? -halfExtents.Y : halfExtents.Y, (i & 4) == 0 ? -halfExtents.Z : halfExtents.Z);
            corner[i] = Vector3.Transform(local, rotation);
        }

        // An edge joins two corners that differ in one bit.
        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit < 8; bit <<= 1)
            {
                if ((i & bit) == 0)
                {
                    Line(center + corner[i], center + corner[i | bit], color);
                }
            }
        }
    }

    /// <summary>An axis-aligned box from its two opposite corners.</summary>
    public void Box(Double3 min, Double3 max, uint color) =>
        Box(new Double3((min.X + max.X) * 0.5, (min.Y + max.Y) * 0.5, (min.Z + max.Z) * 0.5), new Vector3((float)(max.X - min.X) * 0.5f, (float)(max.Y - min.Y) * 0.5f, (float)(max.Z - min.Z) * 0.5f), Quaternion.Identity, color);

    /// <summary>A circle of <paramref name="radius"/> around <paramref name="center"/> in the plane that <paramref name="axisA"/> and <paramref name="axisB"/> (unit, at right angles) span.</summary>
    public void Circle(Double3 center, Vector3 axisA, Vector3 axisB, float radius, uint color, int segments = 24)
    {
        segments = Math.Max(segments, 3);
        Double3 previous = center + (axisA * radius);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * MathF.Tau / segments;
            Double3 next = center + (axisA * (MathF.Cos(angle) * radius)) + (axisB * (MathF.Sin(angle) * radius));
            Line(previous, next, color);
            previous = next;
        }
    }

    /// <summary>A sphere as its three great circles.</summary>
    public void Sphere(Double3 center, float radius, uint color, int segments = 24)
    {
        Circle(center, Vector3.UnitX, Vector3.UnitY, radius, color, segments);
        Circle(center, Vector3.UnitY, Vector3.UnitZ, radius, color, segments);
        Circle(center, Vector3.UnitZ, Vector3.UnitX, radius, color, segments);
    }

    /// <summary>A line with a head at <paramref name="to"/>; the head is <paramref name="headLength"/> long (a fifth of the arrow when it is zero).</summary>
    public void Arrow(Double3 from, Double3 to, uint color, float headLength = 0f)
    {
        Line(from, to, color);
        Vector3 along = (Vector3)(to - from);
        float length = along.Length();
        if (length < 1e-6f)
        {
            return;
        }

        Vector3 direction = along / length;
        float head = headLength > 0f ? headLength : length * 0.2f;
        Vector3 helper = MathF.Abs(direction.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 side = Vector3.Normalize(Vector3.Cross(direction, helper)) * (head * 0.4f);
        Vector3 up = Vector3.Normalize(Vector3.Cross(direction, side)) * (head * 0.4f);
        Double3 baseCenter = to - (direction * head);
        Line(to, baseCenter + side, color);
        Line(to, baseCenter - side, color);
        Line(to, baseCenter + up, color);
        Line(to, baseCenter - up, color);
    }

    /// <summary>The three axes of a placement: X red, Y green, Z blue, each <paramref name="length"/> long.</summary>
    public void Axes(Double3 origin, Quaternion rotation, float length)
    {
        Line(origin, origin + Vector3.Transform(Vector3.UnitX * length, rotation), DebugColor.Red);
        Line(origin, origin + Vector3.Transform(Vector3.UnitY * length, rotation), DebugColor.Green);
        Line(origin, origin + Vector3.Transform(Vector3.UnitZ * length, rotation), DebugColor.Blue);
    }
}
