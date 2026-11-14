using System;
using System.Numerics;

namespace Apogee.Rendering;

/// <summary>
/// The Gran Turismo 7 tone mapping operator, on the CPU: the twin of the one in <c>tonemap.slang</c>, which the tests compare it with. It is a port of Polyphony Digital's sample
/// implementation <c>gt7_tone_mapping.cpp</c> (version 1.0, MIT licence, Copyright (c) 2025 Polyphony Digital Inc.; the notice is in <c>Legal/THIRD_PARTY_NOTICES.md</c>).
/// <para>
/// The operator works on linear Rec.2020 colors in "frame-buffer" units, where 1.0 is <see cref="ReferenceLuminance"/> (100) cd/m2. It tone-maps each channel with a curve (a toe, a linear section and an
/// exponential shoulder that converges on the display's peak), then blends that with a version whose lightness comes from the curved color but whose chroma (in ICtCp) is kept and faded out
/// near the peak, so bright colors desaturate towards white instead of changing hue. In SDR mode the peak is <see cref="SdrPaperWhite"/> (250 cd/m2) and the result is scaled so that 1.0
/// is the display's white, ready for the sRGB transfer function.
/// </para>
/// </summary>
public readonly struct Gt7ToneMapping
{
    /// <summary>The luminance in cd/m2 that a frame-buffer value of 1.0 stands for.</summary>
    public const float ReferenceLuminance = 100f;

    /// <summary>The luminance of the SDR display's white, in cd/m2, that the curve's parameters were tuned for.</summary>
    public const float SdrPaperWhite = 250f;

    /// <summary>The SDR paper white as a frame-buffer value (2.5): the scene value the renderer's exposure maps to the display's white.</summary>
    public const float SdrPaperWhiteFrameBuffer = SdrPaperWhite / ReferenceLuminance;

    // The curve's parameters (slightly different from GT Sport's), and the blend between the per-channel and the chroma-preserving result.
    private const float CurveAlpha = 0.25f;
    private const float CurveGrayPoint = 0.538f;
    private const float CurveLinearSection = 0.444f;
    private const float CurveToeStrength = 1.280f;
    private const float BlendRatio = 0.6f;
    private const float FadeStart = 0.98f;
    private const float FadeEnd = 1.16f;

    private readonly float _sdrCorrection;
    private readonly float _peak;          // the display's peak, as a frame-buffer value
    private readonly float _peakIctcp;     // the I of ICtCp of a gray at the peak
    private readonly float _kA, _kB, _kC;  // the shoulder's constants

    private Gt7ToneMapping(float peakNits, float sdrCorrection)
    {
        _sdrCorrection = sdrCorrection;
        _peak = peakNits / ReferenceLuminance;

        // The shoulder: kA + kB * exp(x * kC), which meets the linear section at LinearSection * peak and levels off at kA.
        float k = (CurveLinearSection - 1f) / (CurveAlpha - 1f);
        _kA = _peak * CurveLinearSection + _peak * k;
        _kB = -_peak * k * MathF.Exp(CurveLinearSection / k);
        _kC = -1f / (k * _peak);

        _peakIctcp = RgbToICtCp(new Vector3(_peak)).X;
    }

    /// <summary>The operator for an SDR display: the curve peaks at 250 cd/m2 and the result is scaled to 0..1 for the sRGB transfer function.</summary>
    public static Gt7ToneMapping Sdr() => new(SdrPaperWhite, 1f / SdrPaperWhiteFrameBuffer);

    /// <summary>
    /// The operator for an HDR display of <paramref name="peakNits"/> cd/m2; the result is in frame-buffer units, up to the peak. The original supports 250 to 10 000 (its curve's parameters were tuned
    /// for an SDR paper white of 250), so the peak is clamped to that range.
    /// </summary>
    public static Gt7ToneMapping Hdr(float peakNits) => new(Math.Clamp(peakNits, SdrPaperWhite, 10_000f), 1f);

    /// <summary>The shoulder's constants and the ICtCp lightness of a gray at the peak, which <c>tonemap.slang</c> has as literals for the SDR peak; the tests compare them.</summary>
    internal (float ShoulderA, float ShoulderB, float ShoulderC, float PeakIctcp) Derived => (_kA, _kB, _kC, _peakIctcp);

    /// <summary>The tone-mapped color (frame-buffer units, linear Rec.2020) of a scene color in linear Rec.2020 frame-buffer units.</summary>
    public Vector3 Apply(Vector3 rgb)
    {
        // Lightness and chroma of the input.
        Vector3 ucs = RgbToICtCp(rgb);

        // Each channel through the curve: this "skews" the hue of saturated colors.
        var skewed = new Vector3(Curve(rgb.X), Curve(rgb.Y), Curve(rgb.Z));
        Vector3 skewedUcs = RgbToICtCp(skewed);

        // Keep the input's chroma, fading it out as its lightness nears the peak, on the curved lightness.
        float chromaScale = ChromaCurve(ucs.X / _peakIctcp, FadeStart, FadeEnd);
        Vector3 scaled = ICtCpToRgb(skewedUcs.X, ucs.Y * chromaScale, ucs.Z * chromaScale);

        // Blend the two, clip to the peak, and (in SDR) bring the peak to 1.
        Vector3 blended = ((1f - BlendRatio) * skewed) + (BlendRatio * scaled);
        return new Vector3(
            _sdrCorrection * MathF.Min(blended.X, _peak),
            _sdrCorrection * MathF.Min(blended.Y, _peak),
            _sdrCorrection * MathF.Min(blended.Z, _peak));
    }

    /// <summary>The per-channel curve: a power-law toe into a linear section (blended by a smoothstep), then the shoulder.</summary>
    public float Curve(float x)
    {
        if (x < 0f)
        {
            return 0f;
        }

        if (x >= CurveLinearSection * _peak)
        {
            return _kA + (_kB * MathF.Exp(x * _kC));
        }

        float weightLinear = SmoothStep(x, 0f, CurveGrayPoint);
        float weightToe = 1f - weightLinear;
        float toe = CurveGrayPoint * MathF.Pow(x / CurveGrayPoint, CurveToeStrength);
        return (weightToe * toe) + (weightLinear * x);
    }

    /// <summary>0 below <paramref name="edge0"/>, 1 above <paramref name="edge1"/>, a cubic smoothstep between (note the argument order of the original: the value comes first).</summary>
    public static float SmoothStep(float x, float edge0, float edge1)
    {
        if (x < edge0)
        {
            return 0f;
        }

        if (x > edge1)
        {
            return 1f;
        }

        float t = (x - edge0) / (edge1 - edge0);
        return t * t * (3f - (2f * t));
    }

    /// <summary>1 below <paramref name="a"/>, fading to 0 at <paramref name="b"/>: how much of the input's chroma is kept.</summary>
    public static float ChromaCurve(float x, float a, float b) => 1f - SmoothStep(x, a, b);

    // ---- SMPTE ST 2084 (PQ) ---------------------------------------------------------------------------------------------------

    private const float M1 = 0.1593017578125f;   // (2610 / 4096) / 4
    private const float M2 = 78.84375f;          // (2523 / 4096) * 128
    private const float C1 = 0.8359375f;         // 3424 / 4096
    private const float C2 = 18.8515625f;        // (2413 / 4096) * 32
    private const float C3 = 18.6875f;           // (2392 / 4096) * 32
    private const float PqPeak = 10000f;         // the most luminance PQ can encode, in cd/m2

    /// <summary>PQ code value (0..1) to a frame-buffer luminance value.</summary>
    public static float PqToLinear(float n)
    {
        n = Math.Clamp(n, 0f, 1f);
        float np = MathF.Pow(n, 1f / M2);
        float l = MathF.Max(np - C1, 0f);
        l /= C2 - (C3 * np);
        l = MathF.Pow(l, 1f / M1);
        return l * PqPeak / ReferenceLuminance;
    }

    /// <summary>A frame-buffer luminance value to its PQ code value (0..1).</summary>
    public static float LinearToPq(float v)
    {
        float y = v * ReferenceLuminance / PqPeak;
        float ym = MathF.Pow(y, M1);
        return MathF.Pow(2f, M2 * (MathF.Log2(C1 + (C2 * ym)) - MathF.Log2(1f + (C3 * ym))));
    }

    // ---- ICtCp (ITU-R BT.2100, as ITU-T T.302 gives it) -----------------------------------------------------------------------

    /// <summary>Linear Rec.2020 (frame-buffer units) to ICtCp, as (I, Ct, Cp): I is the lightness, Ct and Cp the chroma.</summary>
    public static Vector3 RgbToICtCp(Vector3 rgb)
    {
        // To the cone responses (LMS), through PQ, then to the opponent channels.
        float l = ((rgb.X * 1688f) + (rgb.Y * 2146f) + (rgb.Z * 262f)) / 4096f;
        float m = ((rgb.X * 683f) + (rgb.Y * 2951f) + (rgb.Z * 462f)) / 4096f;
        float s = ((rgb.X * 99f) + (rgb.Y * 309f) + (rgb.Z * 3688f)) / 4096f;

        float lPq = LinearToPq(l);
        float mPq = LinearToPq(m);
        float sPq = LinearToPq(s);

        return new Vector3(
            ((2048f * lPq) + (2048f * mPq)) / 4096f,
            ((6610f * lPq) - (13613f * mPq) + (7003f * sPq)) / 4096f,
            ((17933f * lPq) - (17390f * mPq) - (543f * sPq)) / 4096f);
    }

    /// <summary>ICtCp to linear Rec.2020 (frame-buffer units), with negative channels clipped to 0.</summary>
    public static Vector3 ICtCpToRgb(float i, float ct, float cp)
    {
        float l = i + (0.00860904f * ct) + (0.11103f * cp);
        float m = i - (0.00860904f * ct) - (0.11103f * cp);
        float s = i + (0.560031f * ct) - (0.320627f * cp);

        float lLin = PqToLinear(l);
        float mLin = PqToLinear(m);
        float sLin = PqToLinear(s);

        return new Vector3(
            MathF.Max((3.43661f * lLin) - (2.50645f * mLin) + (0.0698454f * sLin), 0f),
            MathF.Max((-0.79133f * lLin) + (1.9836f * mLin) - (0.192271f * sLin), 0f),
            MathF.Max((-0.0259499f * lLin) - (0.0989137f * mLin) + (1.12486f * sLin), 0f));
    }

    // ---- Gamut ---------------------------------------------------------------------------------------------------------------

    /// <summary>Linear Rec.709 (the renderer's working space, the sRGB primaries) to linear Rec.2020, where the operator works. White stays white.</summary>
    public static Vector3 Rec709ToRec2020(Vector3 c) => new(
        (0.6274039f * c.X) + (0.3292830f * c.Y) + (0.0433131f * c.Z),
        (0.0690973f * c.X) + (0.9195404f * c.Y) + (0.0113623f * c.Z),
        (0.0163914f * c.X) + (0.0880133f * c.Y) + (0.8955953f * c.Z));

    /// <summary>Linear Rec.2020 back to linear Rec.709. Colors outside the sRGB gamut come out with a negative channel; the caller clips.</summary>
    public static Vector3 Rec2020ToRec709(Vector3 c) => new(
        (1.6604910f * c.X) - (0.5876411f * c.Y) - (0.0728499f * c.Z),
        (-0.1245505f * c.X) + (1.1328999f * c.Y) - (0.0083494f * c.Z),
        (-0.0181508f * c.X) - (0.1005789f * c.Y) + (1.1187297f * c.Z));

    /// <summary>
    /// The whole SDR path of the renderer's tone-mapping pass for one pixel, as bytes' source values: a color in the HDR image (linear Rec.709, exposed so that 1.0 is the exposure's saturation point)
    /// to the linear Rec.709 color that is encoded with the sRGB transfer function. Negative and NaN channels are taken as 0, and an infinity as 65 504 (the largest half float): an infinity would make NaN in the ICtCp conversion.
    /// </summary>
    public static Vector3 ToDisplay(Vector3 exposed)
    {
        exposed = new Vector3(Sanitize(exposed.X), Sanitize(exposed.Y), Sanitize(exposed.Z));
        Vector3 mapped = Sdr().Apply(Rec709ToRec2020(exposed * SdrPaperWhiteFrameBuffer));
        return Vector3.Clamp(Rec2020ToRec709(mapped), Vector3.Zero, Vector3.One);
    }

    /// <summary>The largest finite half float: what an infinity in the HDR image (a highlight that overflowed it) is taken as.</summary>
    private const float MaxHalf = 65504f;

    private static float Sanitize(float v) => float.IsNaN(v) ? 0f : Math.Clamp(v, 0f, MaxHalf);
}
