using System;
using System.Numerics;
using Apogee.Core.Numerics;

namespace Apogee.Rendering;

/// <summary>
/// A perspective camera in a right-handed world (+Y up, looking down -Z at yaw 0). The projection is reverse-Z with an infinite far plane: the
/// near plane maps to depth 1 and infinity to 0, which spreads float precision evenly over the whole depth range. Normalized device y points
/// down, as in Vulkan, so no viewport flip is needed; the image, and so the winding of triangles (counter-clockwise for front faces, as in glTF), is unchanged.
/// The position is a <see cref="Double3"/> (the world is double precision) and the renderer works relative to it: <see cref="View"/> has the camera at the
/// origin, and everything else is turned into an offset from the camera before it reaches the GPU.
/// </summary>
public sealed class Camera
{
    public Double3 Position { get; set; }

    /// <summary>Rotation about +Y in radians; positive turns to the left.</summary>
    public float Yaw { get; set; }

    /// <summary>Rotation up (positive) or down, in radians.</summary>
    public float Pitch { get; set; }

    public float VerticalFov { get; set; } = 60f * MathF.PI / 180f;

    public float Near { get; set; } = 0.1f;

    /// <summary>How far the light-culling clusters reach; nothing is clipped at this distance.</summary>
    public float ClusterFar { get; set; } = 200f;

    public Vector3 Forward => new(-MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), -MathF.Cos(Yaw) * MathF.Cos(Pitch));

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    /// <summary>The view matrix for camera-relative positions: the camera is at the origin, so it holds only the rotation. Use <see cref="WorldToView"/> for a world point.</summary>
    public Matrix4x4 View
    {
        get
        {
            Vector3 forward = Forward;

            // Looking straight up or down, +Y cannot be "up on the screen": use -Z (north) when looking down and +Z when looking up.
            Vector3 up = MathF.Abs(forward.Y) > 0.9999f ? new Vector3(0, 0, MathF.Sign(forward.Y)) : Vector3.UnitY;
            return Matrix4x4.CreateLookAt(Vector3.Zero, forward, up);
        }
    }

    /// <summary>A world position in view space (x right, y up, the camera looking down -Z): the offset from the camera is taken in double precision first.</summary>
    public Vector3 WorldToView(Double3 point) => Vector3.Transform(point.RelativeTo(Position), View);

    public Matrix4x4 Projection(float aspect) => ReverseZ(VerticalFov, aspect, Near);

    /// <summary>Points the camera at a target.</summary>
    public void LookAt(Double3 target)
    {
        Vector3 direction = Vector3.Normalize(target.RelativeTo(Position));
        Pitch = MathF.Asin(direction.Y);
        Yaw = MathF.Atan2(-direction.X, -direction.Z);
    }

    public static Matrix4x4 ReverseZ(float verticalFov, float aspect, float near)
    {
        float f = 1f / MathF.Tan(verticalFov * 0.5f);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, -f, 0, 0,
            0, 0, 0, -1,
            0, 0, near, 0);
    }
}
