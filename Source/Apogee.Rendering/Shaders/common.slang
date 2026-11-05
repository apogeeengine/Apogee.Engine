// Shared by every renderer shader: the bindless arrays, and the structs the CPU writes (Apogee.Rendering/GpuStructs.cs mirrors them).
// Matrices are row-major and used with row vectors, mul(v, M), exactly like System.Numerics. Structs reached through pointers contain only
// 16-byte aligned members (float4, uint4, matrices, pointers in pairs) so that every layout rule agrees on their size.
#pragma once

// Metal has no descriptor sets or push constants: the push constants are a buffer at index 0 and the bindless block an argument buffer at index 1, in every entry point (the
// Metal backend relies on it). APOGEE_METAL is defined by the compiler for the Metal build only; the SPIR-V build does not see these annotations.
#ifdef APOGEE_METAL
#define PUSH_SLOT : register(b0)
#define BINDLESS_SLOT : register(b1)
#else
#define PUSH_SLOT
#define BINDLESS_SLOT
#endif

// Vulkan's clip space has +y pointing down (the engine's projection is built for it) and Metal's has +y up, so the Metal build flips y in the vertex shaders to draw the same picture.
// Front faces are counter-clockwise on screen in both, so the winding needs no change.
#ifdef APOGEE_METAL
float4 clipPosition(float4 p) { return float4(p.x, -p.y, p.z, p.w); }
#else
float4 clipPosition(float4 p) { return p; }
#endif

// The bindless arrays, in a parameter block: on Vulkan that is descriptor set 0 (binding 0 the sampled images, 1 the samplers: the engine's one bindless set, see
// BindlessHeap), on Metal an argument buffer at buffer 1 (push constants are buffer 0). The sizes are BindlessHeap.MaxSampledImages and MaxSamplers.
struct BindlessResources
{
    Texture2D<float4> textures[16384];
    SamplerState samplers[256];
};
ParameterBlock<BindlessResources> gBindless BINDLESS_SLOT;
#define gTextures gBindless.textures
#define gSamplers gBindless.samplers

static const float PI = 3.14159265358979;
static const uint CLUSTERS_X = 16;
static const uint CLUSTERS_Y = 9;
static const uint CLUSTERS_Z = 24;
static const uint MAX_LIGHTS_PER_CLUSTER = 64;
static const uint CLUSTER_STRIDE = MAX_LIGHTS_PER_CLUSTER + 1;    // a cluster's light count, then its light indices

static const uint FLAG_ALPHA_MASK = 1;
static const uint FLAG_ALPHA_BLEND = 2;
static const uint FLAG_DOUBLE_SIDED = 4;
static const uint FLAG_UNLIT = 8;
static const uint FLAG_NORMAL_MAP = 16;

// 12 floats, no float3: this is the 48-byte Apogee.Content.Vertex.
struct Vertex
{
    float px; float py; float pz;
    float nx; float ny; float nz;
    float tx; float ty; float tz; float tw;
    float u; float v;
};

struct Instance
{
    float4x4 world;
    float4x4 normalMatrix;    // transpose(inverse(world)), for row vectors
    uint vertexOffset;
    uint material;
    uint pad0;
    uint pad1;
};

struct Material
{
    float4 baseColor;
    float4 emissiveMetallic;  // rgb: emissive, a: metallic
    float4 shading;           // x: roughness, y: normal scale, z: occlusion strength, w: alpha cutoff
    uint4 textures0;          // bindless indices: base color, metallic-roughness, normal, occlusion
    uint4 textures1;          // emissive, flags
    uint4 samplers0;
    uint4 samplers1;
    float4 transforms[10];    // per texture (base, mr, normal, occlusion, emissive), two rows (m00 m01 m02 _) and (m10 m11 m12 _)
};

struct Light
{
    float4 positionRadius;    // world position, culling radius
    float4 colorType;         // rgb: color * intensity, a: 0 point, 1 spot
    float4 directionCone;     // spot: direction the light shines, w: cosine of the outer cone angle
    float4 spot;              // x: 1 / (cos inner - cos outer), y: -cos outer * x
};

struct FrameData
{
    float4x4 view;
    float4x4 proj;
    float4x4 viewProj;
    float4 cameraPosition;    // w: time in seconds
    float4 screen;            // width, height, 1 / width, 1 / height
    float4 clusterParams;     // near, far, 1 / log(far / near), unused
    uint4 counts;             // light count, debug mode, unused
    float4 sunDirection;      // xyz: toward the sun
    float4 sunColor;          // rgb: color * illuminance
    float4 ambientSky;
    float4 ambientGround;
    float4 projScale;         // proj[0][0], proj[1][1]
    Instance* instances;
    uint2 reserved2;          // the vertices and materials are per model, in DrawPush
    uint2 reserved3;
    Light* lights;
    float4* clusterBounds;    // per cluster: view-space AABB min, max
    uint* clusterLights;      // per cluster: CLUSTER_STRIDE uints
    uint2 reserved0;
    uint2 reserved1;
};

struct DrawPush
{
    FrameData* frame;
    Vertex* vertices;         // of the model this draw belongs to
    Material* materials;
    uint instance;            // index in the frame's instances
    uint unused;
};

float2 transformUv(float4 row0, float4 row1, float2 uv)
{
    return float2(row0.x * uv.x + row0.y * uv.y + row0.z, row1.x * uv.x + row1.y * uv.y + row1.z);
}

// Which of the 24 depth slices a view-space distance (positive, in front of the camera) falls in.
uint clusterSlice(FrameData* frame, float viewDepth)
{
    float slice = log(max(viewDepth, frame->clusterParams.x) / frame->clusterParams.x) * frame->clusterParams.z * float(CLUSTERS_Z);
    return min(uint(slice), CLUSTERS_Z - 1);
}

// The depth where slice k starts.
float sliceDepth(FrameData* frame, uint k)
{
    return frame->clusterParams.x * pow(frame->clusterParams.y / frame->clusterParams.x, float(k) / float(CLUSTERS_Z));
}
