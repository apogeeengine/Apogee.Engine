using System;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Rendering;

namespace Apogee.Editor.Editing;

/// <summary>
/// The camera the editor looks through, which is not an entity of the scene. It orbits a target point (right mouse drag), pans (middle drag), zooms toward the target (the wheel) and flies (the right
/// button with WASD): all of these move the same three things, the target, the distance to it and the two angles, so switching between them is seamless.
/// </summary>
public sealed class EditorCamera
{
    /// <summary>The point the camera looks at and turns around.</summary>
    public Double3 Target { get; set; } = new(0, 1, 0);

    /// <summary>How far the camera is from the target, in meters.</summary>
    public float Distance { get; set; } = 8f;

    /// <summary>The same convention as <see cref="Camera"/>: yaw turns left about +Y, pitch looks up when positive.</summary>
    public float Yaw { get; set; } = 0.6f;

    public float Pitch { get; set; } = -0.4f;

    public float FovDegrees { get; set; } = 60f;

    public float Near { get; set; } = 0.05f;

    public float ClusterFar { get; set; } = 1000f;

    public Vector3 Forward => new(-MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), -MathF.Cos(Yaw) * MathF.Cos(Pitch));

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    public Vector3 Up => Vector3.Cross(Right, Forward);

    public Double3 Position => Target - (Double3)(Forward * Distance);

    /// <summary>Turns around the target: radians of yaw and pitch, from a mouse drag.</summary>
    public void Orbit(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -1.55f, 1.55f);
    }

    /// <summary>Slides the camera and its target along the view plane: <paramref name="right"/> and <paramref name="up"/> are in screens' worth of the distance (a drag of a fraction of the panel).</summary>
    public void Pan(float right, float up)
    {
        Vector3 move = (Right * -right + Up * up) * Distance;
        Target += (Double3)move;
    }

    /// <summary>Moves toward (positive) or away from the target by a fraction of the distance, never closer than a centimeter.</summary>
    public void Zoom(float amount) => Distance = MathF.Max(Distance * MathF.Pow(0.85f, amount), 0.01f);

    /// <summary>Flies: <paramref name="move"/> is x right, y up, z forward, in meters per second; the camera moves and the target goes with it.</summary>
    public void Fly(Vector3 move, float seconds)
    {
        Vector3 world = Right * move.X + Vector3.UnitY * move.Y + Forward * move.Z;
        Target += (Double3)(world * seconds);
    }

    /// <summary>Looks at a sphere from where it fits in the view.</summary>
    public void Frame(Double3 center, float radius)
    {
        Target = center;
        float fit = radius / MathF.Sin(MathF.Max(FovDegrees * MathF.PI / 360f, 0.1f));
        Distance = Math.Clamp(fit * 1.1f, 0.5f, 100000f);
    }

    /// <summary>Fills a renderer camera from this one.</summary>
    public void ApplyTo(Camera camera)
    {
        camera.Position = Position;
        camera.Yaw = Yaw;
        camera.Pitch = Pitch;
        camera.VerticalFov = FovDegrees * MathF.PI / 180f;
        camera.Near = Near;
        camera.ClusterFar = ClusterFar;
    }
}
