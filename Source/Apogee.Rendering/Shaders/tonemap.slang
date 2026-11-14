// Turns the HDR image into what the screen shows: a tone mapper (Gran Turismo 7's, or the Khronos PBR Neutral one) and the sRGB transfer function (the swapchain is UNORM, so the encoding
// is done here). The HDR image holds exposed values (the forward pass applied the camera's exposure): 1.0 is the luminance that saturates the sensor, and the GT7 tone mapper
// shows it at the SDR display's paper white.
//
// The GT7 part is a port of Polyphony Digital's gt7_tone_mapping.cpp (version 1.0, MIT licence, Copyright (c) 2025 Polyphony Digital Inc.): the notice is in Legal/THIRD_PARTY_NOTICES.md.
// Apogee.Rendering/Gt7ToneMapping.cs is its twin on the CPU and the tests compare the two. The functions keep the original's names and order.
#include "common.slang"

struct TonemapPush
{
    uint texture;
    uint sampler;
    uint tonemapper;    // 0: GT7, 1: Khronos PBR Neutral
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

// ---- Khronos PBR Neutral ----------------------------------------------------------------------------------------------------------

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

// ---- Gran Turismo 7 tone mapping ---------------------------------------------------------------------------------------------------

// In the frame-buffer space of the operator 1.0 is REFERENCE_LUMINANCE cd/m2, and the SDR display's white (GRAN_TURISMO_SDR_PAPER_WHITE cd/m2) is 2.5.
static const float REFERENCE_LUMINANCE = 100.0;
static const float GRAN_TURISMO_SDR_PAPER_WHITE = 250.0;

// The curve's parameters for the SDR display, and the blend between the per-channel and the chroma-preserving result.
static const float GT7_CURVE_ALPHA = 0.25;
static const float GT7_CURVE_GRAY_POINT = 0.538;
static const float GT7_CURVE_LINEAR_SECTION = 0.444;
static const float GT7_CURVE_TOE_STRENGTH = 1.280;
static const float GT7_BLEND_RATIO = 0.6;
static const float GT7_FADE_START = 0.98;
static const float GT7_FADE_END = 1.16;

// 0 below edge0, 1 above edge1, a cubic smoothstep between. The value comes first, as in the original.
float gtSmoothStep(float x, float edge0, float edge1)
{
    if (x < edge0)
        return 0.0;
    if (x > edge1)
        return 1.0;

    float t = (x - edge0) / (edge1 - edge0);
    return t * t * (3.0 - 2.0 * t);
}

float gtChromaCurve(float x, float a, float b)
{
    return 1.0 - gtSmoothStep(x, a, b);
}

// SMPTE ST 2084 (PQ): a code value in 0..1 to a frame-buffer luminance value.
float eotfSt2084(float n)
{
    const float m1 = 0.1593017578125;       // (2610 / 4096) / 4
    const float m2 = 78.84375;              // (2523 / 4096) * 128
    const float c1 = 0.8359375;             // 3424 / 4096
    const float c2 = 18.8515625;            // (2413 / 4096) * 32
    const float c3 = 18.6875;               // (2392 / 4096) * 32
    const float pqC = 10000.0;              // the most luminance PQ can encode, in cd/m2

    n = clamp(n, 0.0, 1.0);
    float np = pow(n, 1.0 / m2);
    float l = max(np - c1, 0.0);
    l = l / (c2 - c3 * np);
    l = pow(l, 1.0 / m1);
    return l * pqC / REFERENCE_LUMINANCE;
}

// A frame-buffer luminance value to its PQ code value.
float inverseEotfSt2084(float v)
{
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;
    const float pqC = 10000.0;

    float y = v * REFERENCE_LUMINANCE / pqC;
    float ym = pow(y, m1);
    return exp2(m2 * (log2(c1 + c2 * ym) - log2(1.0 + c3 * ym)));
}

// Linear Rec.2020 to ICtCp (ITU-T T.302): x is the lightness I, y and z the chroma Ct and Cp.
float3 rgbToICtCp(float3 rgb)
{
    float l = (rgb.r * 1688.0 + rgb.g * 2146.0 + rgb.b * 262.0) / 4096.0;
    float m = (rgb.r * 683.0 + rgb.g * 2951.0 + rgb.b * 462.0) / 4096.0;
    float s = (rgb.r * 99.0 + rgb.g * 309.0 + rgb.b * 3688.0) / 4096.0;

    float lPQ = inverseEotfSt2084(l);
    float mPQ = inverseEotfSt2084(m);
    float sPQ = inverseEotfSt2084(s);

    return float3(
        (2048.0 * lPQ + 2048.0 * mPQ) / 4096.0,
        (6610.0 * lPQ - 13613.0 * mPQ + 7003.0 * sPQ) / 4096.0,
        (17933.0 * lPQ - 17390.0 * mPQ - 543.0 * sPQ) / 4096.0);
}

// ICtCp to linear Rec.2020, with negative channels clipped to 0.
float3 iCtCpToRgb(float3 ictCp)
{
    float l = ictCp.x + 0.00860904 * ictCp.y + 0.11103 * ictCp.z;
    float m = ictCp.x - 0.00860904 * ictCp.y - 0.11103 * ictCp.z;
    float s = ictCp.x + 0.560031 * ictCp.y - 0.320627 * ictCp.z;

    float lLin = eotfSt2084(l);
    float mLin = eotfSt2084(m);
    float sLin = eotfSt2084(s);

    return float3(
        max(3.43661 * lLin - 2.50645 * mLin + 0.0698454 * sLin, 0.0),
        max(-0.79133 * lLin + 1.9836 * mLin - 0.192271 * sLin, 0.0),
        max(-0.0259499 * lLin - 0.0989137 * mLin + 1.12486 * sLin, 0.0));
}

// The operator's curve for one channel, with the display's peak (a frame-buffer value): a power-law toe into a linear section, then an exponential shoulder that levels off above the peak.
float gtCurve(float x, float peak)
{
    if (x < 0.0)
        return 0.0;

    // The shoulder's constants: kA + kB * exp(x * kC) meets the linear section at LINEAR_SECTION * peak.
    float k = (GT7_CURVE_LINEAR_SECTION - 1.0) / (GT7_CURVE_ALPHA - 1.0);
    float kA = peak * GT7_CURVE_LINEAR_SECTION + peak * k;
    float kB = -peak * k * exp(GT7_CURVE_LINEAR_SECTION / k);
    float kC = -1.0 / (k * peak);

    if (x >= GT7_CURVE_LINEAR_SECTION * peak)
        return kA + kB * exp(x * kC);

    float weightLinear = gtSmoothStep(x, 0.0, GT7_CURVE_GRAY_POINT);
    float weightToe = 1.0 - weightLinear;
    float toeMapped = GT7_CURVE_GRAY_POINT * pow(x / GT7_CURVE_GRAY_POINT, GT7_CURVE_TOE_STRENGTH);
    return weightToe * toeMapped + weightLinear * x;
}

// Input: linear Rec.2020 in frame-buffer units. Output: tone-mapped, in frame-buffer units up to the peak, divided by the paper white so that the display's white is 1.0 (the SDR mode of the original).
float3 gt7ToneMapSdr(float3 rgb)
{
    const float peak = GRAN_TURISMO_SDR_PAPER_WHITE / REFERENCE_LUMINANCE;
    const float sdrCorrectionFactor = 1.0 / peak;

    // The lightness of a gray at the peak, to know how close to the peak a pixel is.
    float peakIctcp = rgbToICtCp(float3(peak, peak, peak)).x;

    float3 ucs = rgbToICtCp(rgb);

    // Each channel through the curve: this "skews" the hue of saturated colors.
    float3 skewedRgb = float3(gtCurve(rgb.r, peak), gtCurve(rgb.g, peak), gtCurve(rgb.b, peak));
    float3 skewedUcs = rgbToICtCp(skewedRgb);

    // The input's chroma, faded out as its lightness nears the peak, on the curved lightness.
    float chromaScale = gtChromaCurve(ucs.x / peakIctcp, GT7_FADE_START, GT7_FADE_END);
    float3 scaledUcs = float3(skewedUcs.x, ucs.y * chromaScale, ucs.z * chromaScale);
    float3 scaledRgb = iCtCpToRgb(scaledUcs);

    float3 blended = (1.0 - GT7_BLEND_RATIO) * skewedRgb + GT7_BLEND_RATIO * scaledRgb;
    return sdrCorrectionFactor * min(blended, float3(peak, peak, peak));
}

// Linear Rec.709 (the renderer's working space) to linear Rec.2020, where the operator works. The rows are the matrix's; a color is a column.
float3 rec709ToRec2020(float3 c)
{
    return mul(float3x3(
        0.6274039, 0.3292830, 0.0433131,
        0.0690973, 0.9195404, 0.0113623,
        0.0163914, 0.0880133, 0.8955953), c);
}

float3 rec2020ToRec709(float3 c)
{
    return mul(float3x3(
         1.6604910, -0.5876411, -0.0728499,
        -0.1245505,  1.1328999, -0.0083494,
        -0.0181508, -0.1005789,  1.1187297), c);
}

// An exposed color of the HDR image to the linear Rec.709 color the sRGB transfer function encodes. Negative and NaN channels are taken as 0 (the original assumes valid input).
float3 gt7Display(float3 exposed)
{
    exposed = float3(isnan(exposed.r) ? 0.0 : max(exposed.r, 0.0), isnan(exposed.g) ? 0.0 : max(exposed.g, 0.0), isnan(exposed.b) ? 0.0 : max(exposed.b, 0.0));
    float3 mapped = gt7ToneMapSdr(rec709ToRec2020(exposed * (GRAN_TURISMO_SDR_PAPER_WHITE / REFERENCE_LUMINANCE)));
    return saturate(rec2020ToRec709(mapped));
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
    float3 hdr = gTextures[push.texture].SampleLevel(gSamplers[push.sampler], input.uv, 0).rgb;
    float3 display = push.tonemapper == 0 ? gt7Display(hdr) : saturate(neutralTonemap(hdr));
    return float4(linearToSrgb(display), 1.0);
}
