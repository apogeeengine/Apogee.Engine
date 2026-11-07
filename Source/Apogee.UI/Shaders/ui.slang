// Draws RmlUi's geometry: indexed triangles whose vertices are pulled from a buffer (20 bytes: position in pixels, a color with its alpha multiplied in, a texture coordinate).
// The color is premultiplied, so is the texture (the host makes it so), and the pipeline blends with source + destination * (1 - source alpha).
// Effects: a clip mask is a texture (its red channel is the coverage) that geometry is multiplied by; a gradient is a shader that replaces the texture; layers, blurs, drop shadows and color
// transforms are passes that draw a full-screen triangle (fxVs, fxFs) sampling a layer. RmlUi's stencil is replaced by the clip mask texture, which needs no stencil in the pipelines.
// Metal has no descriptor sets or push constants: the push constants are a buffer at index 0 and the bindless block an argument buffer at index 1, in every entry point (the Metal backend relies on it).
#ifdef APOGEE_METAL
#define PUSH_SLOT : register(b0)
#define BINDLESS_SLOT : register(b1)
#else
#define PUSH_SLOT
#define BINDLESS_SLOT
#endif

// Vulkan's clip space has +y pointing down (the pixel origin is the top left, as RmlUi's is) and Metal's has +y up, so the Metal build flips y.
#ifdef APOGEE_METAL
float4 clipPosition(float4 p) { return float4(p.x, -p.y, p.z, p.w); }
#else
float4 clipPosition(float4 p) { return p; }
#endif

// The bindless arrays, as in Apogee.Rendering/Shaders/common.slang (this shader does not include it).
struct BindlessResources
{
    Texture2D<float4> textures[16384];
    SamplerState samplers[256];
};
ParameterBlock<BindlessResources> gBindless BINDLESS_SLOT;
#define gTextures gBindless.textures
#define gSamplers gBindless.samplers

struct UiVertex
{
    float x; float y;
    uint color;          // bytes r, g, b, a
    float u; float v;
};

struct GradientData
{
    float4 colors[16];     // premultiplied, at most 16 stops
    float4 positions[4];   // the stops' positions, four to a float4
    float4 a;              // linear: p0, p1; radial: center, radius; conic: center, angle
    uint kind;             // 1 linear, 2 radial, 3 conic
    uint repeating;
    uint count;
    uint unused;
};

struct UiPush
{
    UiVertex* vertices;
    uint texture;        // bindless index; 0 is a white texel, which is what untextured geometry samples
    uint sampler;
    float4 transform0;   // the element's CSS transform, one row each (a vertex is a row vector: v * M); identity when hasTransform is 0
    float4 transform1;
    float4 transform2;
    float4 transform3;
    float2 translation;  // added to the vertices before the transform
    float2 viewport;     // the context's size in pixels
    uint vertexOffset;
    uint hasTransform;
    uint clipTexture;    // bindless index of the clip mask (its red channel is the coverage), 0 for none
    uint shaderKind;     // geometry: 0 plain, otherwise a gradient (uiFs); for maskFs the operation: 0 writes 1, 1 writes 0, 2 writes the mask that clipTexture holds
    GradientData* gradient;
};
[[vk::push_constant]] ConstantBuffer<UiPush> push PUSH_SLOT;

float clipCoverage(float2 pixel)
{
    if (push.clipTexture == 0)
        return 1.0;
    return gTextures[push.clipTexture].Sample(gSamplers[push.sampler], pixel / push.viewport).r;
}

float stopPosition(GradientData* g, uint i)
{
    return g->positions[i >> 2][i & 3];
}

float4 gradientColor(GradientData* g, float2 p)
{
    float t;
    if (g->kind == 1)
    {
        float2 d = g->a.zw - g->a.xy;
        t = dot(p - g->a.xy, d) / max(dot(d, d), 1e-8);
    }
    else if (g->kind == 2)
    {
        t = length((p - g->a.xy) / max(g->a.zw, float2(1e-4, 1e-4)));
    }
    else
    {
        float2 q = p - g->a.xy;
        t = frac((atan2(q.x, -q.y) - g->a.z) / 6.2831853 + 1.0);
    }

    uint count = min(g->count, 16u);
    float first = stopPosition(g, 0);
    float last = stopPosition(g, count - 1);
    if (g->repeating != 0 && last > first)
        t = first + frac((t - first) / (last - first)) * (last - first);

    if (t <= first)
        return g->colors[0];
    for (uint i = 1; i < count; i++)
    {
        float next = stopPosition(g, i);
        if (t <= next)
        {
            float previous = stopPosition(g, i - 1);
            return lerp(g->colors[i - 1], g->colors[i], (t - previous) / max(next - previous, 1e-6));
        }
    }
    return g->colors[count - 1];
}

struct UiOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
};

[shader("vertex")]
UiOutput uiVs(uint id : SV_VertexID)
{
    UiVertex v = push.vertices[push.vertexOffset + id];
    float4 p = float4(v.x + push.translation.x, v.y + push.translation.y, 0.0, 1.0);
    if (push.hasTransform != 0)
        p = push.transform0 * p.x + push.transform1 * p.y + push.transform2 * p.z + push.transform3 * p.w;

    // Pixels to clip space: x from 0..width to -1..1, y from 0..height (down) to -1..1 (Vulkan's y points down too). Written for w != 1 so a perspective transform divides correctly.
    UiOutput output;
    output.position = clipPosition(float4(p.x * 2.0 / push.viewport.x - p.w, p.y * 2.0 / push.viewport.y - p.w, 0.0, p.w));
    output.uv = float2(v.u, v.v);
    output.color = float4(float(v.color & 255), float((v.color >> 8) & 255), float((v.color >> 16) & 255), float(v.color >> 24)) / 255.0;
    return output;
}

[shader("fragment")]
float4 uiFs(UiOutput input) : SV_Target
{
    float4 color = push.shaderKind != 0 ? gradientColor(push.gradient, input.uv) * input.color
                                        : input.color * gTextures[push.texture].Sample(gSamplers[push.sampler], input.uv);
    return color * clipCoverage(input.position.xy);
}

// Draws the shape of a clip mask operation into the mask (a texture cleared to 0, or to 1 for the inverse): what is inside the geometry gets the value.
[shader("fragment")]
float4 maskFs(UiOutput input) : SV_Target
{
    float v = push.shaderKind == 0 ? 1.0 : (push.shaderKind == 1 ? 0.0 : clipCoverage(input.position.xy));
    return float4(v, v, v, v);
}

// ---- Full-screen passes over a layer ----

struct FxPush
{
    float4 m0;           // mode 0: the matrix's rows (r, g, b, a: columns r, g, b, a), m4 the offsets; mode 1: m0.xy the step in pixels, m0.z sigma; mode 2: m0 the shadow's color (straight alpha), m1.xy its offset
    float4 m1;
    float4 m2;
    float4 m3;
    float4 m4;
    uint source;         // bindless index of the layer
    uint source2;        // the original (mode 2) or the mask (mode 3)
    uint sampler;
    uint clip;           // the clip mask, 0 for none
    uint mode;           // 0 color matrix, 1 blur along a direction, 2 drop shadow over the original, 3 multiply by a mask's alpha, 4 plain copy
    uint unused;
    float2 viewport;     // the size of the layer that is sampled
    float2 shift;        // added to the pixel position before sampling: where a saved region starts
};
// The two push blocks are used by different entry points, so on Metal sharing buffer index 0 is what is wanted (Slang's "explicit binding overlap" warning for the Metal build is expected).
[[vk::push_constant]] ConstantBuffer<FxPush> fx PUSH_SLOT;

struct FxOutput
{
    float4 position : SV_Position;
};

[shader("vertex")]
FxOutput fxVs(uint id : SV_VertexID)
{
    float2 p = float2(float((id << 1) & 2), float(id & 2));
    FxOutput output;
    output.position = clipPosition(float4(p * 2.0 - 1.0, 0.0, 1.0));
    return output;
}

float4 fxSample(uint texture, float2 pixel)
{
    return gTextures[texture].Sample(gSamplers[fx.sampler], (pixel + fx.shift) / fx.viewport);
}

[shader("fragment")]
float4 fxFs(FxOutput input) : SV_Target
{
    float2 pixel = input.position.xy;
    float coverage = 1.0;
    if (fx.clip != 0)
        coverage = gTextures[fx.clip].Sample(gSamplers[fx.sampler], pixel / fx.viewport).r;

    float4 result;
    if (fx.mode == 0)
    {
        float4 s = fxSample(fx.source, pixel);
        float3 rgb = s.a > 0.0 ? s.rgb / s.a : float3(0.0, 0.0, 0.0);
        float4 v = float4(rgb, s.a);
        float4 c = saturate(float4(dot(fx.m0, v), dot(fx.m1, v), dot(fx.m2, v), dot(fx.m3, v)) + fx.m4);
        result = float4(c.rgb * c.a, c.a);
    }
    else if (fx.mode == 1)
    {
        float sigma = max(fx.m0.z, 0.01);
        int radius = int(min(ceil(sigma * 3.0), 48.0));
        float4 sum = float4(0.0, 0.0, 0.0, 0.0);
        float total = 0.0;
        for (int i = -radius; i <= radius; i++)
        {
            float w = exp(-0.5 * float(i * i) / (sigma * sigma));
            sum += fxSample(fx.source, pixel + fx.m0.xy * float(i)) * w;
            total += w;
        }
        result = sum / total;
    }
    else if (fx.mode == 2)
    {
        float4 original = fxSample(fx.source2, pixel);
        float shadowAlpha = fxSample(fx.source, pixel - fx.m1.xy).a;
        float4 shadow = float4(fx.m0.rgb * fx.m0.a, fx.m0.a) * shadowAlpha;
        result = original + shadow * (1.0 - original.a);
    }
    else if (fx.mode == 3)
    {
        result = fxSample(fx.source, pixel) * fxSample(fx.source2, pixel).a;
    }
    else
    {
        result = fxSample(fx.source, pixel);
    }

    return result * coverage;
}
