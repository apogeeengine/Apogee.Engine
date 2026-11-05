using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Apogee.Rendering;

// The structs the shaders read through pointers (Shaders/common.slang). Their sizes and field offsets are checked by tests.

[StructLayout(LayoutKind.Sequential)]
public struct UInt4(uint x, uint y, uint z, uint w)
{
    public uint X = x;
    public uint Y = y;
    public uint Z = z;
    public uint W = w;
}

[InlineArray(10)]
public struct TextureTransforms
{
    private Vector4 _element;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuInstance
{
    public Matrix4x4 World;

    /// <summary>transpose(inverse(World)): the matrix that carries normals along, for row vectors.</summary>
    public Matrix4x4 NormalMatrix;
    public uint VertexOffset;
    public uint Material;
    public uint Pad0;
    public uint Pad1;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuMaterial
{
    public Vector4 BaseColor;

    /// <summary>xyz: emissive; w: metallic.</summary>
    public Vector4 EmissiveMetallic;

    /// <summary>x: roughness; y: normal scale; z: occlusion strength; w: alpha cutoff.</summary>
    public Vector4 Shading;
    public UInt4 Textures0;
    public UInt4 Textures1;
    public UInt4 Samplers0;
    public UInt4 Samplers1;
    public TextureTransforms Transforms;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuLight
{
    /// <summary>xyz: world position; w: the radius beyond which the light is zero.</summary>
    public Vector4 PositionRadius;

    /// <summary>xyz: color times intensity; w: 0 for a point light, 1 for a spot light.</summary>
    public Vector4 ColorType;

    /// <summary>xyz: the direction a spot light shines; w: cosine of its outer cone angle.</summary>
    public Vector4 DirectionCone;

    /// <summary>x: 1 / (cos inner - cos outer); y: -cos outer * x.</summary>
    public Vector4 Spot;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuFrameData
{
    public Matrix4x4 View;
    public Matrix4x4 Proj;
    public Matrix4x4 ViewProj;
    public Vector4 CameraPosition;
    public Vector4 Screen;
    public Vector4 ClusterParams;
    public UInt4 Counts;
    public Vector4 SunDirection;
    public Vector4 SunColor;
    public Vector4 AmbientSky;
    public Vector4 AmbientGround;
    public Vector4 ProjScale;
    public ulong Instances;
    public ulong Reserved2;   // the vertices and materials are per model, in GpuDrawPush
    public ulong Reserved3;
    public ulong Lights;
    public ulong ClusterBounds;
    public ulong ClusterLights;
    public ulong Reserved0;
    public ulong Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuDrawPush
{
    public ulong Frame;

    /// <summary>The vertices and materials of the model the draw belongs to: a draw's instance names its mesh's first vertex and its material in these.</summary>
    public ulong Vertices;
    public ulong Materials;

    /// <summary>The index of the instance in the frame's instance buffer.</summary>
    public uint Instance;
    public uint Unused;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuDebugPush
{
    public ulong Frame;

    /// <summary>The lines or triangles (see <c>debug_draw.slang</c>).</summary>
    public ulong Data;

    /// <summary>A line's width in pixels.</summary>
    public float Thickness;

    /// <summary>Multiplies the colors' alpha: less than one for the part of a line that is hidden.</summary>
    public float Alpha;
    public uint Unused0;
    public uint Unused1;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuTonemapPush
{
    public uint Texture;
    public uint Sampler;
    public float Exposure;
    public uint Unused;
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuFramePush
{
    public ulong Frame;
}
