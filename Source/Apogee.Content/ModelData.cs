using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Apogee.Content;

/// <summary>The vertex the renderer pulls from a buffer: 48 bytes, the same layout in Slang.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;

    /// <summary>xyz is the tangent, w is the handedness (±1) of the bitangent.</summary>
    public Vector4 Tangent;
    public Vector2 Uv0;
}

public enum AlphaMode
{
    Opaque,
    Mask,
    Blend,
}

/// <summary>A reference from a material to a texture of the model, with glTF's <c>KHR_texture_transform</c>.</summary>
public readonly record struct TextureRef(int Texture, int TexCoord, Vector2 Offset, Vector2 Scale, float Rotation)
{
    public bool HasTransform => Offset != Vector2.Zero || Scale != Vector2.One || Rotation != 0f;
}

/// <summary>glTF's metallic-roughness material.</summary>
public sealed class MaterialData
{
    public string Name { get; init; } = "";

    public Vector4 BaseColorFactor { get; init; } = Vector4.One;

    public TextureRef? BaseColorTexture { get; init; }

    public float MetallicFactor { get; init; } = 1f;

    public float RoughnessFactor { get; init; } = 1f;

    /// <summary>Roughness in green, metalness in blue.</summary>
    public TextureRef? MetallicRoughnessTexture { get; init; }

    public TextureRef? NormalTexture { get; init; }

    public float NormalScale { get; init; } = 1f;

    /// <summary>Ambient occlusion in red.</summary>
    public TextureRef? OcclusionTexture { get; init; }

    public float OcclusionStrength { get; init; } = 1f;

    /// <summary>Emitted color, already multiplied by <c>KHR_materials_emissive_strength</c>.</summary>
    public Vector3 EmissiveFactor { get; init; }

    public TextureRef? EmissiveTexture { get; init; }

    public AlphaMode AlphaMode { get; init; }

    public float AlphaCutoff { get; init; } = 0.5f;

    public bool DoubleSided { get; init; }

    /// <summary><c>KHR_materials_unlit</c>: shown as the base color, without lighting.</summary>
    public bool Unlit { get; init; }
}

/// <summary>One glTF primitive: triangles with one material.</summary>
public sealed class MeshData(string name, Vertex[] vertices, uint[] indices, int materialIndex, Vector3 boundsMin, Vector3 boundsMax)
{
    public string Name { get; } = name;

    public Vertex[] Vertices { get; } = vertices;

    public uint[] Indices { get; } = indices;

    public int MaterialIndex { get; } = materialIndex;

    public Vector3 BoundsMin { get; } = boundsMin;

    public Vector3 BoundsMax { get; } = boundsMax;
}

/// <summary>A mesh placed in the world by the scene's node hierarchy.</summary>
public readonly record struct MeshInstance(int Mesh, Matrix4x4 World);

public enum LightKind
{
    Directional,
    Point,
    Spot,
}

/// <summary>A <c>KHR_lights_punctual</c> light in world space. Intensity is in lux (directional) or candela (point and spot), as glTF defines it.</summary>
public sealed record LightData(string Name, LightKind Kind, Vector3 Color, float Intensity, float Range, float InnerConeAngle, float OuterConeAngle, Vector3 Position, Vector3 Direction);

/// <summary>A glTF file, flattened: the meshes the default scene uses, where they are, their materials and textures, and its lights.</summary>
public sealed class ModelData
{
    public required string Name { get; init; }

    public required IReadOnlyList<MeshData> Meshes { get; init; }

    public required IReadOnlyList<MeshInstance> Instances { get; init; }

    public required IReadOnlyList<MaterialData> Materials { get; init; }

    public required IReadOnlyList<TextureData> Textures { get; init; }

    public required IReadOnlyList<LightData> Lights { get; init; }

    /// <summary>The box around every instance, in world space.</summary>
    public required Vector3 BoundsMin { get; init; }

    public required Vector3 BoundsMax { get; init; }
}

public sealed class ContentOptions
{
    /// <summary>The compressed texture formats the GPU samples; Basis textures are transcoded to one of them.</summary>
    public TextureCaps TextureCaps { get; set; }

    /// <summary>Builds a mip chain for PNG and JPEG textures.</summary>
    public bool GenerateMips { get; set; } = true;

    /// <summary>Removes duplicate vertices and reorders for the GPU's vertex cache (meshoptimizer).</summary>
    public bool OptimizeMeshes { get; set; } = true;

    /// <summary>Reads a file the model refers to (a texture or a .bin next to a .gltf), by the path of the model plus the reference: <c>vfs.ReadAllBytes</c> for a model in the virtual file system. Defaults to the real file system.</summary>
    public System.Func<string, byte[]>? ReadFile { get; set; }
}
