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

// The bindless arrays: the engine's one descriptor set (see BindlessHeap). A texture's index comes from the heap's AddSampledImage. The sampled textures of every dimension share one index
// space: a texture added to the heap is read through the array of its own type (a 2D view as gTextures, a cube view as gCubes, a 2D-array view as gArrays, a 3D view as gVolumes, a view of an
// unsigned integer format as gUintTextures) and is not read through the others. gStorage holds the storage images, a separate index space (views added with AddStorageImage).
// The sizes are BindlessHeap.MaxSampledImages, MaxSamplers and MaxStorageImages.
//
// On Vulkan these are globals with explicit bindings: binding 0 is the sampled images (every sampled array is aliased on it: the descriptor type is the same whatever the dimension), 1 the
// samplers, 2 the storage images. On Metal they are the members of a parameter block, an argument buffer at buffer 1 (push constants are buffer 0), and there is no aliasing: each member
// is its own run of 8-byte resource ids, in declaration order. Keep the order below in step with MetalBindlessHeap: textures, samplers, then the rest.
#ifdef APOGEE_METAL
struct BindlessResources
{
    Texture2D<float4> textures[16384];
    SamplerState samplers[256];
    TextureCube<float4> cubes[16384];
    Texture2DArray<float4> arrays[16384];
    Texture3D<float4> volumes[16384];
    Texture2D<uint> uintTextures[16384];
    RWTexture2D<float4> storage[256];
};
ParameterBlock<BindlessResources> gBindless BINDLESS_SLOT;
#define gTextures gBindless.textures
#define gSamplers gBindless.samplers
#define gCubes gBindless.cubes
#define gArrays gBindless.arrays
#define gVolumes gBindless.volumes
#define gUintTextures gBindless.uintTextures
#define gStorage gBindless.storage
#else
[[vk::binding(0, 0)]] Texture2D<float4> gTextures[16384];
[[vk::binding(1, 0)]] SamplerState gSamplers[256];
// The aliasing below is on purpose.
#pragma warning(push)
#pragma warning(disable: 39001)
[[vk::binding(0, 0)]] TextureCube<float4> gCubes[16384];
[[vk::binding(0, 0)]] Texture2DArray<float4> gArrays[16384];
[[vk::binding(0, 0)]] Texture3D<float4> gVolumes[16384];
[[vk::binding(0, 0)]] Texture2D<uint> gUintTextures[16384];
[[vk::binding(2, 0)]] RWTexture2D<float4> gStorage[256];
#pragma warning(pop)
#endif

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
    float4 exposure;          // x: the exposure the CPU chose, y: the compensation, z: 1 to take the auto-exposure's (exposureState) instead
    float4* exposureState;    // written by exposure.slang: x: exposure (without compensation), y: the adapted EV100, z: the scene's average luminance
    uint2 reserved4;
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

// The exposure the forward pass multiplies the scene's luminance (cd/m2) by, so that the HDR image holds exposed values: 1.0 is the luminance that saturates the sensor
// (ExposureMath.Scale on the CPU). With auto-exposure it is the one the previous frame's histogram led to, so no frame waits for the GPU.
float sceneExposure(FrameData* frame)
{
    return frame->exposure.z > 0.5 ? frame->exposureState[0].x * frame->exposure.y : frame->exposure.x;
}
