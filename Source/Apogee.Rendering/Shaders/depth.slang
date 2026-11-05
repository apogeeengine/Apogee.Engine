// The depth prepass. Opaque geometry needs only the vertex shader; alpha-masked geometry also runs the fragment shader to discard holes.
#include "common.slang"

[[vk::push_constant]] ConstantBuffer<DrawPush> push PUSH_SLOT;

struct DepthOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    nointerpolation uint material : TEXCOORD1;
};

[shader("vertex")]
DepthOutput depthVs(uint vertexId : SV_VertexID)
{
    FrameData* frame = push.frame;
    Instance instance = frame->instances[push.instance];
    Vertex v = push.vertices[instance.vertexOffset + vertexId];

    DepthOutput output;
    output.position = clipPosition(mul(mul(float4(v.px, v.py, v.pz, 1.0), instance.world), frame->viewProj));
    output.uv = float2(v.u, v.v);
    output.material = instance.material;
    return output;
}

[shader("fragment")]
void depthMaskFs(DepthOutput input)
{
    Material material = push.materials[input.material];
    float2 uv = transformUv(material.transforms[0], material.transforms[1], input.uv);
    float alpha = material.baseColor.a * gTextures[material.textures0.x].Sample(gSamplers[material.samplers0.x], uv).a;
    if (alpha < material.shading.w)
        discard;
}
