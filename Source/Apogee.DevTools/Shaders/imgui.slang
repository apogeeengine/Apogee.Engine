// Draws Dear ImGui's triangles. Vertices are pulled from a buffer (ImDrawVert: position, uv, packed RGBA) and the texture is a bindless index.
// Metal has no descriptor sets or push constants: the push constants are a buffer at index 0 and the bindless block an argument buffer at index 1, in every entry point (the Metal backend relies on it). APOGEE_METAL is defined by the compiler for the Metal build only; the SPIR-V build does not see these annotations.
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

// The bindless arrays, as in Apogee.Rendering/Shaders/common.slang (this shader does not include it): descriptor set 0 on Vulkan, an argument buffer at buffer 1 on Metal.
struct BindlessResources
{
    Texture2D<float4> textures[16384];
    SamplerState samplers[256];
};
ParameterBlock<BindlessResources> gBindless BINDLESS_SLOT;
#define gTextures gBindless.textures
#define gSamplers gBindless.samplers

struct ImVertex
{
    float x; float y;
    float u; float v;
    uint color;
};

struct ImPush
{
    ImVertex* vertices;
    float2 scale;
    float2 translate;
    uint texture;
    uint sampler;
    uint vertexOffset;
    uint unused;
};
[[vk::push_constant]] ConstantBuffer<ImPush> push PUSH_SLOT;

struct ImOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
};

[shader("vertex")]
ImOutput imguiVs(uint id : SV_VertexID)
{
    ImVertex v = push.vertices[push.vertexOffset + id];
    ImOutput output;
    output.position = clipPosition(float4(float2(v.x, v.y) * push.scale + push.translate, 0.0, 1.0));
    output.uv = float2(v.u, v.v);
    // ImGui packs colors as 0xAABBGGRR.
    output.color = float4(float(v.color & 255), float((v.color >> 8) & 255), float((v.color >> 16) & 255), float(v.color >> 24)) / 255.0;
    return output;
}

[shader("fragment")]
float4 imguiFs(ImOutput input) : SV_Target
{
    return input.color * gTextures[push.texture].Sample(gSamplers[push.sampler], input.uv);
}
