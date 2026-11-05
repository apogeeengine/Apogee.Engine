using System.Numerics;
using Apogee.Core.Graphics;
using Apogee.Core.Numerics;
using Apogee.Reflection;

namespace Apogee.Rendering;

/// <summary>
/// Debug drawing for Luau scripts, as the global table <c>debug_draw</c>: lines and shapes that last one frame, drawn over the world. Call them every frame (from a system, or from the
/// <c>devtools.on_gui</c> function) for as long as they should show. Colors are vectors of red, green and blue from 0 to 1; without one a line is white. Positions are the world's.
/// With nowhere to draw (no renderer) the calls do nothing.
/// </summary>
[ScriptLibrary("debug_draw")]
public static partial class DebugDrawLibrary
{
    /// <summary>What the calls draw into. The host sets it, draws it each frame with the world and clears it after.</summary>
    public static DebugDraw? Target { get; set; }

    private static uint ColorOf(Value color) => color.Kind == ValueKind.Vector3
        ? DebugColor.FromVector(new Vector4(color.ToVector3(), 1f))
        : DebugColor.White;

    /// <summary>A line from one point to another.</summary>
    [Method]
    public static void Line(Vector3 from, Vector3 to, Value color = default) => Target?.Line(from, to, ColorOf(color));

    /// <summary>The edges of the box that has <c>center</c> and reaches <c>half_extents</c> from it along the axes.</summary>
    [Method]
    public static void Box(Vector3 center, Vector3 halfExtents, Value color = default) => Target?.Box(center, halfExtents, Quaternion.Identity, ColorOf(color));

    /// <summary>A sphere as three circles.</summary>
    [Method]
    public static void Sphere(Vector3 center, float radius, Value color = default) => Target?.Sphere(center, radius, ColorOf(color));

    /// <summary>A line with an arrow head at <c>to</c>.</summary>
    [Method]
    public static void Arrow(Vector3 from, Vector3 to, Value color = default) => Target?.Arrow(from, to, ColorOf(color));

    /// <summary>The three axes at a point, each <c>length</c> long: X red, Y green, Z blue.</summary>
    [Method]
    public static void Axes(Vector3 origin, float length = 1f) => Target?.Axes(origin, Quaternion.Identity, length);
}
