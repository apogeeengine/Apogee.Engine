using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using Xunit;

namespace Weft.Rendering.Tests;

/// <summary>The CPU twin of the GT7 tone mapper, on its own (no GPU): the properties of the curve, the color transforms, and the gamut matrices against the primaries they come from.</summary>
public class Gt7ToneMappingTests
{
    private static Vector3 Gray(float v) => new(v);

    [Fact]
    public void TheCurveIsZeroAtBlackAndForNegativesAndFollowsTheDiagonalInItsLinearSection()
    {
        Gt7ToneMapping sdr = Gt7ToneMapping.Sdr();

        Assert.Equal(0f, sdr.Curve(0f));
        Assert.Equal(0f, sdr.Curve(-3f));

        // From the gray point (0.538) up to 0.444 of the peak (2.5, so 1.11) the curve is the identity.
        foreach (float x in new[] { 0.54f, 0.8f, 1.0f, 1.1f })
        {
            Assert.Equal(x, sdr.Curve(x), 1e-6f);
        }
    }

    [Fact]
    public void TheToeIsAPowerLawBlendedIntoTheLinearSectionByASmoothstep()
    {
        Gt7ToneMapping sdr = Gt7ToneMapping.Sdr();
        const double grayPoint = 0.538, toeStrength = 1.280;

        foreach (double x in new[] { 0.01, 0.1, 0.3, 0.5 })
        {
            double t = x / grayPoint;
            double linearWeight = t * t * (3 - (2 * t));
            double expected = ((1 - linearWeight) * grayPoint * Math.Pow(t, toeStrength)) + (linearWeight * x);
            Assert.Equal(expected, sdr.Curve((float)x), 1e-5);
        }
    }

    [Fact]
    public void TheShoulderIsContinuousIncreasingAndLevelsOffAboveThePeak()
    {
        Gt7ToneMapping sdr = Gt7ToneMapping.Sdr();
        const float peak = 2.5f;
        float knee = 0.444f * peak;

        // Both sides of the knee agree.
        Assert.Equal(sdr.Curve(knee - 1e-4f), sdr.Curve(knee + 1e-4f), 1e-3f);

        float previous = -1f;
        for (float x = 0f; x < 60f; x += 0.01f)
        {
            float y = sdr.Curve(x);
            Assert.True(y >= previous, $"curve({x}) = {y} fell below {previous}");
            previous = y;
        }

        // The shoulder converges on kA = peak * (linear section + k), with k = (0.444 - 1) / (0.25 - 1): above the peak, which the final clamp then cuts.
        float asymptote = peak * (0.444f + ((0.444f - 1f) / (0.25f - 1f)));
        Assert.Equal(asymptote, sdr.Curve(1000f), 1e-4f);
        Assert.True(asymptote > peak);
    }

    [Fact]
    public void AGrayInTheLinearSectionComesOutAsItselfScaledToTheDisplay()
    {
        Gt7ToneMapping sdr = Gt7ToneMapping.Sdr();

        // No chroma to keep and nothing to curve: the output is the input over the paper white (2.5), so a frame-buffer 1.0 (100 cd/m2) is 0.4 of the display's white.
        Assert.Equal(0.4f, sdr.Apply(Gray(1f)).X, 2e-3f);
        Assert.Equal(0.32f, sdr.Apply(Gray(0.8f)).Y, 2e-3f);
        Assert.Equal(Vector3.Zero, sdr.Apply(Vector3.Zero));
    }

    [Fact]
    public void SdrOutputNeverExceedsTheDisplaysWhiteAndRisesWithTheInput()
    {
        Gt7ToneMapping sdr = Gt7ToneMapping.Sdr();

        float previous = 0f;
        for (float x = 0f; x <= 200f; x += x < 10f ? 0.05f : 5f)
        {
            Vector3 mapped = sdr.Apply(Gray(x));
            Assert.InRange(mapped.X, 0f, 1.0001f);
            Assert.True(mapped.X >= previous - 1e-4f, $"gray {x}: {mapped.X} after {previous}");
            previous = mapped.X;
        }

        // Far above the peak the display is at its white.
        Assert.Equal(1f, sdr.Apply(Gray(500f)).X, 1e-3f);

        // A very bright saturated color is cut to the peak and desaturated: no channel is above white, and the red channel leads.
        Vector3 hot = sdr.Apply(new Vector3(1504.7f, 64.51f, 0.5f));
        Assert.InRange(hot.X, 0.9f, 1.0001f);
        Assert.True(hot.X >= hot.Y && hot.Y >= hot.Z);
    }

    [Fact]
    public void HdrOutputRisesToTheDisplaysPeakInFrameBufferUnits()
    {
        Gt7ToneMapping hdr = Gt7ToneMapping.Hdr(1000f);

        Assert.Equal(10f, hdr.Apply(Gray(10_000f)).X, 1e-2f);
        Assert.Equal(1f, hdr.Apply(Gray(1f)).X, 2e-3f);   // below the knee (4.44) nothing changes
    }

    [Fact]
    public void ThePqTransferFunctionMatchesItsKnownValuesAndRoundTrips()
    {
        // 100 cd/m2 (a frame-buffer 1.0) is 50.8 percent, the HDR reference white of 203 cd/m2 is 58.1, and 10 000 cd/m2 is 1.
        Assert.Equal(0.5081f, Gt7ToneMapping.LinearToPq(1f), 1e-3f);
        Assert.Equal(0.5807f, Gt7ToneMapping.LinearToPq(2.03f), 1e-3f);
        Assert.Equal(1f, Gt7ToneMapping.LinearToPq(100f), 1e-4f);
        Assert.Equal(0f, Gt7ToneMapping.PqToLinear(0f));

        foreach (float v in new[] { 0.001f, 0.05f, 0.5f, 1f, 2.5f, 10f, 50f })
        {
            Assert.Equal(v, Gt7ToneMapping.PqToLinear(Gt7ToneMapping.LinearToPq(v)), v * 2e-3f);
        }
    }

    [Fact]
    public void ICtCpRoundTripsAndPutsTheLightnessOfAGrayInIAlone()
    {
        foreach (Vector3 rgb in new[] { new Vector3(0.5f, 1.23f, 0.75f), new Vector3(12.3f, 34.3f, 56.9f), new Vector3(2f, 0.05f, 0.1f), new Vector3(0.01f, 0.02f, 0.9f) })
        {
            Vector3 ictcp = Gt7ToneMapping.RgbToICtCp(rgb);
            Vector3 back = Gt7ToneMapping.ICtCpToRgb(ictcp.X, ictcp.Y, ictcp.Z);
            Assert.Equal(rgb.X, back.X, rgb.X * 5e-3f + 1e-4f);
            Assert.Equal(rgb.Y, back.Y, rgb.Y * 5e-3f + 1e-4f);
            Assert.Equal(rgb.Z, back.Z, rgb.Z * 5e-3f + 1e-4f);
        }

        // A gray has no chroma.
        Vector3 gray = Gt7ToneMapping.RgbToICtCp(Gray(3f));
        Assert.Equal(0f, gray.Y, 2e-3f);
        Assert.Equal(0f, gray.Z, 2e-3f);
        Assert.True(gray.X > 0.5f);
    }

    [Fact]
    public void TheGamutMatricesAreTheOnesTheRec709AndRec2020PrimariesGive()
    {
        // The RGB-to-XYZ matrix of a color space from its primaries' chromaticities and white point (the standard derivation), in double precision.
        static double[,] ToXyz((double X, double Y) r, (double X, double Y) g, (double X, double Y) b, (double X, double Y) white)
        {
            double[,] primaries = { { r.X / r.Y, g.X / g.Y, b.X / b.Y }, { 1, 1, 1 }, { (1 - r.X - r.Y) / r.Y, (1 - g.X - g.Y) / g.Y, (1 - b.X - b.Y) / b.Y } };
            double[] w = [white.X / white.Y, 1, (1 - white.X - white.Y) / white.Y];
            double[] scale = Solve(primaries, w);
            double[,] m = new double[3, 3];
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    m[row, column] = primaries[row, column] * scale[column];
                }
            }

            return m;
        }

        (double, double) d65 = (0.3127, 0.3290);
        double[,] xyz709 = ToXyz((0.640, 0.330), (0.300, 0.600), (0.150, 0.060), d65);
        double[,] xyz2020 = ToXyz((0.708, 0.292), (0.170, 0.797), (0.131, 0.046), d65);

        // 709 to 2020 is inverse(xyz2020) * xyz709: each column is the 2020 color of a unit 709 channel.
        Vector3[] columns = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
        for (int column = 0; column < 3; column++)
        {
            double[] xyz = [xyz709[0, column], xyz709[1, column], xyz709[2, column]];
            double[] in2020 = Solve(xyz2020, xyz);
            Vector3 mapped = Gt7ToneMapping.Rec709ToRec2020(columns[column]);
            Assert.Equal(in2020[0], mapped.X, 1e-3);
            Assert.Equal(in2020[1], mapped.Y, 1e-3);
            Assert.Equal(in2020[2], mapped.Z, 1e-3);
        }

        // White stays white and the two directions undo each other.
        Vector3 white = Gt7ToneMapping.Rec709ToRec2020(Vector3.One);
        Assert.Equal(1f, white.X, 1e-4f);
        Assert.Equal(1f, white.Y, 1e-4f);
        Assert.Equal(1f, white.Z, 1e-4f);
        var color = new Vector3(0.2f, 0.7f, 0.4f);
        Vector3 roundTrip = Gt7ToneMapping.Rec2020ToRec709(Gt7ToneMapping.Rec709ToRec2020(color));
        Assert.Equal(color.X, roundTrip.X, 1e-4f);
        Assert.Equal(color.Y, roundTrip.Y, 1e-4f);
        Assert.Equal(color.Z, roundTrip.Z, 1e-4f);
    }

    [Fact]
    public void TheDisplayPathIsZeroForBlackNegativeAndNaNAndWithinRangeOtherwise()
    {
        Assert.Equal(Vector3.Zero, Gt7ToneMapping.ToDisplay(Vector3.Zero));
        Assert.Equal(Vector3.Zero, Gt7ToneMapping.ToDisplay(new Vector3(-1f, float.NaN, -0.2f)));

        // An infinity (a highlight that overflowed the half-float image) is the largest half float, and comes out finite, at the display's white: not NaN, which min and max do not define.
        Vector3 overflowed = Gt7ToneMapping.ToDisplay(new Vector3(float.PositiveInfinity, 50f, float.PositiveInfinity));
        Assert.True(float.IsFinite(overflowed.X) && float.IsFinite(overflowed.Y) && float.IsFinite(overflowed.Z), overflowed.ToString());
        Assert.Equal(Gt7ToneMapping.ToDisplay(new Vector3(65504f, 50f, 65504f)), overflowed);
        Assert.Equal(1f, Gt7ToneMapping.ToDisplay(new Vector3(float.PositiveInfinity)).X, 1e-3f);

        float previous = 0f;
        for (float v = 0f; v <= 30f; v += 0.05f)
        {
            Vector3 shown = Gt7ToneMapping.ToDisplay(Gray(v));
            Assert.InRange(shown.X, 0f, 1f);
            Assert.True(shown.X >= previous - 1e-4f);
            previous = shown.X;
        }

        // An exposed 1.0 (the sensor's saturation) is shown a little below white, 0.18 as a mid-tone.
        Assert.InRange(Gt7ToneMapping.ToDisplay(Gray(1f)).X, 0.8f, 0.9f);
        Assert.InRange(Gt7ToneMapping.ToDisplay(Gray(0.18f)).X, 0.15f, 0.25f);
    }

    [Fact]
    public void TheDerivedConstantsInTheShaderAreTheTwinsValues()
    {
        // tonemap.slang has the SDR shoulder's constants and the lightness of the peak as literals (the original computes them once; a shader would do it for every pixel).
        string source = File.ReadAllText(Path.Combine(ShaderFiles.Directory(), "tonemap.slang"));
        float Literal(string name) => float.Parse(Regex.Match(source, @"static const float " + name + @" = (-?[0-9.]+);").Groups[1].Value, CultureInfo.InvariantCulture);

        (float a, float b, float c, float peak) = Gt7ToneMapping.Sdr().Derived;
        Assert.Equal(a, Literal("GT7_SDR_SHOULDER_A"), MathF.Abs(a) * 1e-6f);
        Assert.Equal(b, Literal("GT7_SDR_SHOULDER_B"), MathF.Abs(b) * 1e-6f);
        Assert.Equal(c, Literal("GT7_SDR_SHOULDER_C"), MathF.Abs(c) * 1e-6f);
        Assert.Equal(peak, Literal("GT7_SDR_PEAK_ICTCP"), MathF.Abs(peak) * 1e-6f);

        // And they are the original's: kA 2.9633333, kB -3.3733512, kC -0.5395683, I of the peak 0.60255915 (computed from the original's initializeCurve in double precision).
        Assert.Equal(2.9633333f, a, 1e-5f);
        Assert.Equal(-3.3733512f, b, 1e-5f);
        Assert.Equal(-0.5395683f, c, 1e-5f);
        Assert.Equal(0.60255915f, peak, 1e-5f);
    }

    [Fact]
    public void TheOperatorGivesTheOriginalsOutputsForItsOwnTestInputs()
    {
        // The inputs of the original's main() (linear Rec.2020, frame-buffer values) and its outputs, computed in double precision from the original's source (a transliteration; the
        // original itself was not run). The values are frame-buffer values: the SDR ones are 0 to 1, the HDR ones go up to the peak over 100.
        Vector3[] inputs = [new(0.5f, 1.23f, 0.75f), new(12.3f, 34.3f, 56.9f), new(1504.7f, 64.51f, 0.5f)];
        (Gt7ToneMapping Mapper, Vector3[] Expected)[] cases =
        [
            (Gt7ToneMapping.Sdr(), [new(0.199664f, 0.490678f, 0.299549f), new(1f, 1f, 1f), new(1f, 1f, 0.738770f)]),
            (Gt7ToneMapping.Hdr(1000f), [new(0.499930f, 1.229967f, 0.749978f), new(10f, 10f, 10f), new(10f, 10f, 6.706918f)]),
            (Gt7ToneMapping.Hdr(4000f), [new(0.499930f, 1.229967f, 0.749978f), new(11.355797f, 30.070416f, 40f), new(40f, 40f, 23.848369f)]),
            (Gt7ToneMapping.Hdr(10000f), [new(0.499930f, 1.229967f, 0.749978f), new(12.279202f, 34.240514f, 56.400996f), new(91.724877f, 68.019940f, 42.573367f)]),
        ];

        foreach ((Gt7ToneMapping mapper, Vector3[] expected) in cases)
        {
            for (int i = 0; i < inputs.Length; i++)
            {
                Vector3 actual = mapper.Apply(inputs[i]);
                Assert.Equal(expected[i].X, actual.X, (expected[i].X * 5e-4f) + 1e-4f);
                Assert.Equal(expected[i].Y, actual.Y, (expected[i].Y * 5e-4f) + 1e-4f);
                Assert.Equal(expected[i].Z, actual.Z, (expected[i].Z * 5e-4f) + 1e-4f);
            }
        }
    }

    [Fact]
    public void TheHdrPeakIsKeptInTheRangeTheOriginalSupports()
    {
        // 250 to 10 000 cd/m2: below that the curve's parameters (tuned for a paper white of 250) do not hold.
        Assert.Equal(Gt7ToneMapping.Hdr(250f).Apply(Gray(100f)), Gt7ToneMapping.Hdr(50f).Apply(Gray(100f)));
        Assert.Equal(Gt7ToneMapping.Hdr(10_000f).Apply(Gray(500f)), Gt7ToneMapping.Hdr(50_000f).Apply(Gray(500f)));
    }

    /// <summary>Solves a 3 x 3 linear system by Cramer's rule.</summary>
    private static double[] Solve(double[,] a, double[] b)
    {
        double Det(double[,] m) =>
            (m[0, 0] * ((m[1, 1] * m[2, 2]) - (m[1, 2] * m[2, 1]))) - (m[0, 1] * ((m[1, 0] * m[2, 2]) - (m[1, 2] * m[2, 0]))) + (m[0, 2] * ((m[1, 0] * m[2, 1]) - (m[1, 1] * m[2, 0])));

        double determinant = Det(a);
        double[] x = new double[3];
        for (int column = 0; column < 3; column++)
        {
            double[,] replaced = (double[,])a.Clone();
            for (int row = 0; row < 3; row++)
            {
                replaced[row, column] = b[row];
            }

            x[column] = Det(replaced) / determinant;
        }

        return x;
    }
}
