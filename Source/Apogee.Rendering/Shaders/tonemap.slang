// Turns the HDR image into what the screen shows: exposure, the Khronos PBR Neutral tone mapper, and the sRGB transfer function
// (the swapchain is UNORM, so the encoding is done here).
#include "common.slang"

struct TonemapPush
{
    uint texture;
    uint sampler;
    float exposure;
    uint unused;
};
[[vk::push_constant]] ConstantBuffer<TonemapPush> push PUSH_SLOT;

struct FullscreenOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
FullscreenOutput fullscreenVs(uint id : SV_VertexID)
{
    float2 uv = float2((id << 1) & 2, id & 2);
    FullscreenOutput output;
    output.position = clipPosition(float4(uv * 2.0 - 1.0, 0.0, 1.0));
    output.uv = uv;
    return output;
}

float3 neutralTonemap(float3 color)
{
    const float startCompression = 0.8 - 0.04;
    const float desaturation = 0.15;

    float x = min(color.r, min(color.g, color.b));
    float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
    color -= offset;

    float peak = max(color.r, max(color.g, color.b));
    if (peak < startCompression)
        return color;

    const float d = 1.0 - startCompression;
    float newPeak = 1.0 - d * d / (peak + d - startCompression);
    color *= newPeak / peak;

    float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
    return lerp(color, newPeak * float3(1.0, 1.0, 1.0), g);
}

float3 linearToSrgb(float3 c)
{
    float3 low = c * 12.92;
    float3 high = 1.055 * pow(c, float3(1.0 / 2.4, 1.0 / 2.4, 1.0 / 2.4)) - 0.055;
    return float3(c.r <= 0.0031308 ? low.r : high.r, c.g <= 0.0031308 ? low.g : high.g, c.b <= 0.0031308 ? low.b : high.b);
}

[shader("fragment")]
float4 tonemapFs(FullscreenOutput input) : SV_Target
{
    float3 hdr = gTextures[push.texture].SampleLevel(gSamplers[push.sampler], input.uv, 0).rgb * push.exposure;
    return float4(linearToSrgb(saturate(neutralTonemap(hdr))), 1.0);
}
