using System;
using System.Numerics;

namespace Apogee.Core.Numerics;

/// <summary>
/// The view volume of a perspective camera with an infinite far plane, as five planes (left, right, bottom, top, near) in the camera-relative
/// space the renderer works in. A plane is <c>(nx, ny, nz, d)</c> and a point is inside when <c>n·p + d &gt;= 0</c> for all five. The four side planes
/// pass through the camera, which is the origin, so their <c>d</c> is zero; <see cref="Offset"/> moves the origin for a scene whose coordinates are relative to
/// something else. The normals are not unit length (the side ones are <c>forward * tan + right</c>); the culling kernel takes that into account.
/// </summary>
public readonly struct Frustum
{
    public const int PlaneCount = 5;

    public Frustum(Vector4 left, Vector4 right, Vector4 bottom, Vector4 top, Vector4 near)
    {
        Left = left;
        Right = right;
        Bottom = bottom;
        Top = top;
        Near = near;
    }

    public Vector4 Left { get; }

    public Vector4 Right { get; }

    public Vector4 Bottom { get; }

    public Vector4 Top { get; }

    public Vector4 Near { get; }

    /// <summary>
    /// The frustum of a camera at the origin looking along <paramref name="forward"/>, from its basis (as in the rows of a view matrix).
    /// <paramref name="tanHalfFovY"/> is the tangent of half the vertical field of view and <paramref name="aspect"/> is width over height.
    /// </summary>
    public static Frustum FromCamera(Vector3 right, Vector3 up, Vector3 forward, float tanHalfFovY, float aspect, float near)
    {
        float tanX = tanHalfFovY * aspect;
        Vector3 left = (forward * tanX) + right;
        Vector3 rightPlane = (forward * tanX) - right;
        Vector3 bottom = (forward * tanHalfFovY) + up;
        Vector3 top = (forward * tanHalfFovY) - up;
        return new Frustum(
            new Vector4(left, 0f),
            new Vector4(rightPlane, 0f),
            new Vector4(bottom, 0f),
            new Vector4(top, 0f),
            new Vector4(forward, -near));
    }

    /// <summary>The same volume for boxes given relative to a point <paramref name="origin"/> away from the camera: <c>d</c> absorbs <c>n·origin</c>.</summary>
    public Frustum Offset(Vector3 origin) => new(Shift(Left, origin), Shift(Right, origin), Shift(Bottom, origin), Shift(Top, origin), Shift(Near, origin));

    public Vector4 this[int plane] => plane switch
    {
        0 => Left,
        1 => Right,
        2 => Bottom,
        3 => Top,
        4 => Near,
        _ => throw new ArgumentOutOfRangeException(nameof(plane)),
    };

    /// <summary>
    /// Whether a box (center and half extents along the axes) is at least partly inside. Exact for the planes, so a box near a corner can still be
    /// reported inside without being visible. The arithmetic is in the same order as <see cref="FrustumCulling"/>'s vector kernels, which give the same answer.
    /// </summary>
    public bool Intersects(Vector3 center, Vector3 extents)
    {
        for (int i = 0; i < PlaneCount; i++)
        {
            if (Distance(this[i], center, extents) < 0f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The signed distance (times the normal's length) of the box's farthest corner along the plane's normal: negative means the whole box is outside.</summary>
    internal static float Distance(Vector4 plane, Vector3 center, Vector3 extents)
    {
        float centerSide = (((plane.X * center.X) + (plane.Y * center.Y)) + (plane.Z * center.Z));
        float reach = (((MathF.Abs(plane.X) * extents.X) + (MathF.Abs(plane.Y) * extents.Y)) + (MathF.Abs(plane.Z) * extents.Z));
        return (centerSide + reach) + plane.W;
    }

    private static Vector4 Shift(Vector4 plane, Vector3 origin) => new(plane.X, plane.Y, plane.Z, plane.W + (plane.X * origin.X) + (plane.Y * origin.Y) + (plane.Z * origin.Z));
}
