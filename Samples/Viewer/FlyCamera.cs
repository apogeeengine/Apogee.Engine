using System;
using System.Numerics;
using Apogee.Rendering;

namespace Apogee.Samples.Viewer;

/// <summary>Moves a <see cref="Camera"/> from keyboard and mouse input.</summary>
internal sealed class FlyCamera(Camera camera)
{
    private float _speed = 6f;

    public void Update(float seconds, Vector3 move, bool fast, float mouseDx, float mouseDy, float wheel)
    {
        camera.Yaw -= mouseDx * 0.0025f;
        camera.Pitch = Math.Clamp(camera.Pitch - mouseDy * 0.0025f, -1.55f, 1.55f);
        _speed = Math.Clamp(_speed * MathF.Pow(1.2f, wheel), 0.5f, 200f);

        // move.X right, move.Y up, move.Z forward.
        Vector3 forward = camera.Forward;
        Vector3 step = camera.Right * move.X + Vector3.UnitY * move.Y + forward * move.Z;
        if (step.LengthSquared() > 0f)
        {
            camera.Position += Vector3.Normalize(step) * _speed * (fast ? 4f : 1f) * seconds;
        }
    }
}
