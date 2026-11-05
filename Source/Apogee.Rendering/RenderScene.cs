using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Content;
using Apogee.Core.Numerics;

namespace Apogee.Rendering;

/// <summary>
/// One <see cref="GpuModel"/> placed in the world, with the lights and sky it is lit with. The lights can be changed every frame.
/// The model's own coordinates (instance transforms, light positions, bounds) are relative to the scene's <see cref="Origin"/>, a position in the
/// double-precision world; every frame the renderer turns them into offsets from the camera, so a scene can be anywhere in the world.
/// For many models, or for models placed by entities, see <see cref="RenderWorld"/>.
/// </summary>
public sealed class RenderScene : IRenderSource, IDisposable
{
    internal RenderScene(GpuModel model, Double3 origin)
    {
        Model = model;
        Origin = origin;
        Lights = [.. model.Lights];
        if (model.SunDirection is { } sun)
        {
            SunDirection = sun;
            SunColor = model.SunColor;
        }
    }

    /// <summary>The model this scene shows, which it owns.</summary>
    public GpuModel Model { get; }

    public string Name => Model.Name;

    /// <summary>Where the model's origin is in the world. Set it to move the whole scene; the model's coordinates stay relative to it.</summary>
    public Double3 Origin { get; set; }

    public Vector3 BoundsMin => Model.BoundsMin;

    public Vector3 BoundsMax => Model.BoundsMax;

    /// <summary>Point and spot lights, relative to <see cref="Origin"/>. Edit the list between frames to move or add lights.</summary>
    public List<LightData> Lights { get; }

    /// <summary>The direction toward the sun (unit length), and its color times illuminance. Zero color turns the sun off.</summary>
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.4f, 1f, 0.3f));

    public Vector3 SunColor { get; set; }

    public Vector3 AmbientSky { get; set; } = new(0.12f, 0.15f, 0.2f);

    public Vector3 AmbientGround { get; set; } = new(0.05f, 0.045f, 0.04f);

    public int MeshCount => Model.MeshCount;

    void IRenderSource.Gather(SceneGather into, Double3 camera)
    {
        into.Placements.Add(new Placement(Model, Origin, Quaternion.Identity, Vector3.One));
        Vector3 sceneToCamera = (Vector3)(Origin - camera);
        foreach (LightData light in Lights)
        {
            into.Lights.Add(SceneRenderer.ToGpu(light, sceneToCamera));
        }

        into.SunDirection = SunDirection;
        into.SunColor = SunColor;
        into.AmbientSky = AmbientSky;
        into.AmbientGround = AmbientGround;
    }

    /// <summary>KHR_texture_transform: uv' = T * R * S * uv, as two rows of a 2x3 matrix.</summary>
    internal static (Vector4 Row0, Vector4 Row1) TransformRows(TextureRef? reference)
    {
        if (reference is not { } r || !r.HasTransform)
        {
            return (new Vector4(1, 0, 0, 0), new Vector4(0, 1, 0, 0));
        }

        float cos = MathF.Cos(r.Rotation);
        float sin = MathF.Sin(r.Rotation);
        return (new Vector4(cos * r.Scale.X, sin * r.Scale.Y, r.Offset.X, 0), new Vector4(-sin * r.Scale.X, cos * r.Scale.Y, r.Offset.Y, 0));
    }

    /// <summary>The half extents of the axis-aligned box that holds a box of half extents <paramref name="half"/> carried by <paramref name="world"/> (row vectors: <c>x' = x * M</c>).</summary>
    internal static Vector3 WorldExtents(Vector3 half, Matrix4x4 world) => new(
        (MathF.Abs(world.M11) * half.X) + (MathF.Abs(world.M21) * half.Y) + (MathF.Abs(world.M31) * half.Z),
        (MathF.Abs(world.M12) * half.X) + (MathF.Abs(world.M22) * half.Y) + (MathF.Abs(world.M32) * half.Z),
        (MathF.Abs(world.M13) * half.X) + (MathF.Abs(world.M23) * half.Y) + (MathF.Abs(world.M33) * half.Z));

    /// <summary>Frees the model. Draw frames that use the scene first (the GPU is waited for).</summary>
    public void Dispose() => Model.Dispose();
}
