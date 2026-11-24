using System;

namespace Weft.Rendering;

/// <summary>
/// The photographic exposure maths of the renderer, on the CPU: the twin of <c>exposure.slang</c> and of the exposure functions in <c>common.slang</c>, which the tests compare it with.
/// <para>
/// Scene light is photometric: the forward pass writes luminance in cd/m2 (a sun of <c>E</c> lux on a white diffuse surface gives <c>E / pi</c> cd/m2). The camera's exposure is an
/// EV100, the exposure value at ISO 100: <c>EV100 = log2(N^2 / t) - log2(S / 100)</c> for an aperture f/<c>N</c>, a shutter time <c>t</c> in seconds and a sensitivity <c>S</c>. The
/// forward pass multiplies its result by <see cref="Scale"/>, the saturation-based exposure of a camera at that EV100, so the HDR image holds <em>exposed</em> values: 1.0 is the luminance
/// that just saturates the sensor, <c>1.2 * 2^EV100</c> cd/m2, and a fp16 image cannot overflow with a bright sun.
/// </para>
/// </summary>
public static class ExposureMath
{
    /// <summary>
    /// The number of bins of the luminance histogram. Bin 0 counts the pixels that are not measured (black, below the range, NaN or infinite); bins 1 to 254 divide the log2-luminance range evenly, and
    /// bin 255 holds what is at or above the top of the range.
    /// </summary>
    public const int HistogramBins = 256;

    /// <summary>The reflected-light meter's calibration constant K: a gray of average luminance <c>L</c> is exposed correctly at <c>EV100 = log2(L * 100 / K)</c>.</summary>
    public const float MeterCalibration = 12.5f;

    /// <summary>The factor of the saturation-based exposure, <c>78 / (S * q)</c> with <c>q = 0.65</c> and <c>S = 100</c>: the luminance that saturates the sensor is this times <c>2^EV100</c> cd/m2.</summary>
    public const float SaturationFactor = 1.2f;

    /// <summary>The lowest luminance the histogram measures, as log2 cd/m2 (about 0.001 cd/m2, a moonless night).</summary>
    public const float DefaultMinLog2Luminance = -10f;

    /// <summary>The highest luminance the histogram measures, as log2 cd/m2 (about 260 000 cd/m2, the sun's glare off a mirror).</summary>
    public const float DefaultMaxLog2Luminance = 18f;

    /// <summary>The EV100 of a camera with the f-number <paramref name="aperture"/>, a shutter open for <paramref name="shutterSeconds"/> and the sensitivity <paramref name="iso"/>.</summary>
    public static float Ev100(float aperture, float shutterSeconds, float iso) =>
        MathF.Log2(aperture * aperture / shutterSeconds) - MathF.Log2(iso / 100f);

    /// <summary>The factor the scene's luminance is multiplied by at <paramref name="ev100"/>: 1 / (1.2 * 2^EV100). The result is 1.0 for a luminance that saturates the sensor.</summary>
    public static float Scale(float ev100) => 1f / (SaturationFactor * MathF.Pow(2f, ev100));

    /// <summary>The EV100 that exposes a scene of average luminance <paramref name="luminance"/> (cd/m2) as a mid-gray.</summary>
    public static float Ev100FromLuminance(float luminance) => MathF.Log2(luminance * 100f / MeterCalibration);

    /// <summary>The average luminance (cd/m2) that is exposed as a mid-gray at <paramref name="ev100"/>; the inverse of <see cref="Ev100FromLuminance"/>.</summary>
    public static float LuminanceFromEv100(float ev100) => MeterCalibration * MathF.Pow(2f, ev100) / 100f;

    /// <summary>The luminance of a linear Rec.709 color, in the units of the color.</summary>
    public static float Luminance(float r, float g, float b) => (0.2126f * r) + (0.7152f * g) + (0.0722f * b);

    /// <summary>
    /// The histogram bin of a luminance (cd/m2): 0 for black, below the range, NaN or infinite; otherwise 1 to 254 along the log2 range from <paramref name="minLog2"/> to <paramref name="maxLog2"/>, and
    /// 255 at or above <paramref name="maxLog2"/>.
    /// </summary>
    public static int HistogramBin(float luminance, float minLog2, float maxLog2)
    {
        if (!float.IsFinite(luminance) || luminance <= 0f)
        {
            return 0;
        }

        float log2 = MathF.Log2(luminance);
        if (log2 < minLog2)
        {
            return 0;
        }

        float t = (log2 - minLog2) / (maxLog2 - minLog2);
        return (int)Math.Clamp((t * (HistogramBins - 2)) + 1f, 1f, HistogramBins - 1);
    }

    /// <summary>The log2 luminance at the center of a histogram bin (1 to 254; bin 255 holds everything from the top of the range up, and its "center" is just past it).</summary>
    public static float BinLog2Luminance(int bin, float minLog2, float maxLog2) =>
        minLog2 + ((bin - 1 + 0.5f) / (HistogramBins - 2) * (maxLog2 - minLog2));

    /// <summary>
    /// The average log2 luminance of the pixels in a histogram, leaving out the darkest <paramref name="lowPercentile"/> and the brightest <c>1 - <paramref name="highPercentile"/></c> of the
    /// measured ones (a few very bright lamps or a dark corner should not set the exposure), and the unmeasurable ones in bin 0. An empty histogram gives <paramref name="minLog2"/>.
    /// </summary>
    public static float AverageLog2Luminance(ReadOnlySpan<uint> bins, float minLog2, float maxLog2, float lowPercentile = 0.1f, float highPercentile = 0.9f)
    {
        float total = 0f;
        for (int i = 1; i < HistogramBins; i++)
        {
            total += bins[i];
        }

        if (total <= 0f)
        {
            return minLog2;
        }

        // The window [from, to) of the sorted pixels (the bins are in order) that counts; each bin contributes the part of its pixels inside it.
        float from = total * lowPercentile;
        float to = total * highPercentile;
        float sum = 0f;
        float weight = 0f;
        float seen = 0f;
        for (int i = 1; i < HistogramBins; i++)
        {
            float start = seen;
            seen += bins[i];
            float inside = MathF.Max(0f, MathF.Min(seen, to) - MathF.Max(start, from));
            sum += inside * BinLog2Luminance(i, minLog2, maxLog2);
            weight += inside;
        }

        // A window that holds nothing (the percentiles meet) falls back to the plain average.
        return weight > 0f ? sum / weight : AverageLog2Luminance(bins, minLog2, maxLog2, 0f, 1f);
    }

    /// <summary>The EV100 the auto-exposure aims for in a scene of average log2 luminance <paramref name="averageLog2"/>, kept between <paramref name="minEv100"/> and <paramref name="maxEv100"/>.</summary>
    public static float TargetEv100(float averageLog2, float minEv100, float maxEv100) =>
        Math.Clamp(Ev100FromLuminance(MathF.Pow(2f, averageLog2)), minEv100, maxEv100);

    /// <summary>
    /// Moves <paramref name="current"/> towards <paramref name="target"/> over <paramref name="deltaSeconds"/>: exponentially, so a frame rate does not change how fast the eye adapts.
    /// <paramref name="speedBright"/> (per second) applies when the scene got brighter (the EV rises), <paramref name="speedDark"/> when it got darker; the eye adapts to light faster than to dark.
    /// </summary>
    public static float Adapt(float current, float target, float deltaSeconds, float speedBright, float speedDark)
    {
        float speed = target > current ? speedBright : speedDark;
        return current + ((target - current) * (1f - MathF.Exp(-deltaSeconds * speed)));
    }
}
