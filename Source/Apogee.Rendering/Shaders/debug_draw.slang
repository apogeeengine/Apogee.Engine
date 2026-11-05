// Debug lines and triangles (Apogee.Core's DebugDraw): drawn over the lit scene, depth-tested against its depth. Everything is an offset from the camera already (the CPU took the
// difference of the double positions), so the view matrix, which has no translation, is all it takes to get to view space.
//
// Lines are not hardware lines (their width is not portable, and Metal has none): each is a quad of two triangles, widened to a few pixels on the screen. The part of a line behind the
// near plane is cut off here, because a quad cannot be left to the GPU's clipper (its corners would swing across the screen).
#include "common.slang"

struct DebugPush
{
    FrameData* frame;
    float4* data;       // lines: two float4 each (from.xyz and the color as bits, to.xyz); triangles: three float4 each (a.xyz and the color, b.xyz, c.xyz)
    float thickness;    // in pixels
    float alpha;        // multiplies the color's own alpha
    uint2 unused;
};
[[vk::push_constant]] ConstantBuffer<DebugPush> push PUSH_SLOT;

struct DebugOutput
{
    float4 position : SV_Position;
    nointerpolation float4 color : TEXCOORD0;
};

float3 srgbToLinear(float3 c)
{
    return select(c <= 0.04045, c / 12.92, pow((c + 0.055) / 1.055, 2.4));
}

// The color as the CPU packed it: bytes r, g, b, a of a little-endian uint. It is authored as it should look on the screen, and the tone mapper encodes sRGB, so it goes in linear.
float4 unpackColor(uint bits)
{
    float4 c = float4(bits & 0xFF, (bits >> 8) & 0xFF, (bits >> 16) & 0xFF, (bits >> 24) & 0xFF) / 255.0;
    return float4(srgbToLinear(c.rgb), c.a * push.alpha);
}

[shader("vertex")]
DebugOutput lineVs(uint vertexId : SV_VertexID)
{
    FrameData* frame = push.frame;
    uint line = vertexId / 6;
    uint corner = vertexId % 6;

    float4 first = push.data[line * 2];
    float4 second = push.data[line * 2 + 1];
    uint colorBits = asuint(first.w);

    // The quad's corners: 0 (a, left), 1 (a, right), 2 (b, left), 3 (b, left), 4 (a, right), 5 (b, right).
    bool atEnd = corner == 2 || corner == 3 || corner == 5;
    float side = (corner == 1 || corner == 4 || corner == 5) ? 1.0 : -1.0;

    float4 viewA = mul(float4(first.xyz, 1.0), frame->view);
    float4 viewB = mul(float4(second.xyz, 1.0), frame->view);
    float nearPlane = frame->clusterParams.x;

    DebugOutput output;
    output.color = unpackColor(colorBits);

    // View space looks down -z: a point is behind the near plane when its z is above -nearPlane. Both behind: nothing to draw.
    if (viewA.z > -nearPlane && viewB.z > -nearPlane)
    {
        output.position = float4(2.0, 2.0, 2.0, 1.0);
        return output;
    }

    if (viewA.z > -nearPlane)
        viewA = lerp(viewA, viewB, (-nearPlane - viewA.z) / (viewB.z - viewA.z));
    if (viewB.z > -nearPlane)
        viewB = lerp(viewB, viewA, (-nearPlane - viewB.z) / (viewA.z - viewB.z));

    float4 clipA = mul(float4(viewA.xyz, 1.0), frame->proj);
    float4 clipB = mul(float4(viewB.xyz, 1.0), frame->proj);
    float2 halfScreen = frame->screen.xy * 0.5;
    float2 pixelA = clipA.xy / clipA.w * halfScreen;
    float2 pixelB = clipB.xy / clipB.w * halfScreen;
    float2 direction = pixelB - pixelA;
    direction = dot(direction, direction) > 1e-8 ? normalize(direction) : float2(1.0, 0.0);
    float2 normal = float2(-direction.y, direction.x);

    float4 clip = atEnd ? clipB : clipA;
    // The widening is in pixels; in clip space a pixel is 1 / halfScreen of the clip w.
    clip.xy += normal * (side * push.thickness * 0.5) / halfScreen * clip.w;
    output.position = clipPosition(clip);
    return output;
}

[shader("vertex")]
DebugOutput triangleVs(uint vertexId : SV_VertexID)
{
    FrameData* frame = push.frame;
    uint triangle = vertexId / 3;
    uint corner = vertexId % 3;

    float4 first = push.data[triangle * 3];
    float3 a = mul(float4(first.xyz, 1.0), frame->view).xyz;
    float3 b = mul(float4(push.data[triangle * 3 + 1].xyz, 1.0), frame->view).xyz;
    float3 c = mul(float4(push.data[triangle * 3 + 2].xyz, 1.0), frame->view).xyz;

    // Flat shading with a light at the eye: a face looking at the camera is bright, one seen edge-on is dim.
    float3 normal = normalize(cross(b - a, c - a));
    float facing = abs(dot(normal, normalize((a + b + c) / 3.0)));
    float shade = 0.3 + 0.7 * facing;

    float3 position = corner == 0 ? a : (corner == 1 ? b : c);
    DebugOutput output;
    output.color = unpackColor(asuint(first.w));
    output.color.rgb *= shade;
    output.position = clipPosition(mul(float4(position, 1.0), frame->proj));
    return output;
}

[shader("fragment")]
float4 debugFs(DebugOutput input) : SV_Target
{
    return input.color;
}
