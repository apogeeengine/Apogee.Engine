using System;
using System.Numerics;
using Xunit;

namespace Apogee.Rendering.Tests;

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
