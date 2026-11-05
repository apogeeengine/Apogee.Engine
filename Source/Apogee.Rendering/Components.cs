using System.Numerics;
using Apogee.Reflection;

namespace Apogee.Rendering;

#pragma warning disable CA1051 // The fields of a component are its memory layout.

/// <summary>
/// Draws a model at the entity's <see cref="Ecs.WorldTransform"/>: its position, rotation and scale place every mesh of the model. <see cref="Model"/> is an id from the
/// <see cref="ModelLibrary"/> (<c>models.load</c> in scripts); 0, or an id the library no longer knows, draws nothing. Many entities may share a model.
/// </summary>
[Component]
public struct ModelRenderer
{
    /// <summary>The model's id in the <see cref="ModelLibrary"/>; an inspector shows its path instead.</summary>
    [Property(Category = "Model")]
    public uint Model;
}

/// <summary>
/// A light at the entity's <see cref="Ecs.WorldTransform"/>, shining along its -Z (as glTF lights do). A directional light is the world's sun: its color and direction set the sun (the
/// first one wins), and its position does not matter.
/// </summary>
[Component]
public struct Light
{
    /// <summary>The color, in linear 0 to 1.</summary>
    [Property(Category = "Light")]
    public Vector3 Color;

    /// <summary>Candela for point and spot lights, lux for the sun.</summary>
    [Property(Min = 0, Step = 10, Category = "Light")]
    public float Intensity;

    /// <summary>How far a point or spot light reaches; 0 lets the renderer find where it falls below a faint threshold.</summary>
    [Property(Min = 0, Step = 0.5, Category = "Light")]
    public float Range;

    /// <summary>A spot light's cone, in radians: fully lit inside <see cref="InnerCone"/>, fading to nothing at <see cref="OuterCone"/>.</summary>
    [Property(Min = 0, Max = 1.5707964, Step = 0.01, Category = "Spot cone")]
    public float InnerCone;

    [Property(Min = 0, Max = 1.5707964, Step = 0.01, Category = "Spot cone")]
    public float OuterCone;

    /// <summary>A <see cref="Apogee.Content.LightKind"/>: 0 directional, 1 point, 2 spot.</summary>
    [Property(Min = 0, Max = 2, Category = "Light")]
    public byte Kind;

    public static Light Point(Vector3 color, float intensity, float range = 0f) => new() { Color = color, Intensity = intensity, Range = range, Kind = (byte)Apogee.Content.LightKind.Point };

    public static Light Sun(Vector3 color, float illuminance) => new() { Color = color, Intensity = illuminance, Kind = (byte)Apogee.Content.LightKind.Directional };
}

/// <summary>
/// A perspective camera at the entity's <see cref="Ecs.WorldTransform"/>, looking along its -Z. The entity that also has <see cref="MainCamera"/> is the one the world is drawn through.
/// Roll is ignored: the camera turns about +Y (yaw) and then up or down (pitch).
/// </summary>
[Component]
public struct CameraComponent
{
    [Property(Min = 10, Max = 140, Step = 0.5, Category = "Lens")]
    public float FovDegrees;

    [Property(Min = 0.001, Max = 10, Step = 0.01, Category = "Lens")]
    public float Near;

    /// <summary>How far the light-culling clusters reach; nothing is clipped at this distance.</summary>
    [Property(Min = 1, Max = 5000, Step = 1, Category = "Lens")]
    public float ClusterFar;

    public static CameraComponent Default => new() { FovDegrees = 60f, Near = 0.1f, ClusterFar = 200f };
}

/// <summary>Marks the <see cref="CameraComponent"/> the world is drawn through (the first one found, when several have it).</summary>
[Component]
public struct MainCamera;

#pragma warning restore CA1051
