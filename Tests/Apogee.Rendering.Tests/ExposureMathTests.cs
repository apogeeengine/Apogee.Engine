using System;
using Xunit;

namespace Apogee.Rendering.Tests;

/// <summary>The photographic maths of the exposure on the CPU (no GPU): EV100, the exposure factor, the luminance histogram's bins and average, and the adaptation.</summary>
public class ExposureMathTests
{
    [Fact]
    public void Ev100FollowsTheCameraEquationAndTheSunny16Rule()
    {
        // f/16 at 1/100 s and ISO 100 is the "sunny 16" day: EV100 = log2(256 * 100) = 14.64. Each stop of aperture, shutter or sensitivity is one EV.
        Assert.Equal(14.64f, ExposureMath.Ev100(16f, 0.01f, 100f), 0.01f);
        Assert.Equal(ExposureMath.Ev100(16f, 0.01f, 100f) - 1f, ExposureMath.Ev100(11.3137f, 0.01f, 100f), 0.001f);   // one stop wider
        Assert.Equal(ExposureMath.Ev100(16f, 0.01f, 100f) - 1f, ExposureMath.Ev100(16f, 0.02f, 100f), 1e-4f);        // twice as long
        Assert.Equal(ExposureMath.Ev100(16f, 0.01f, 100f) - 1f, ExposureMath.Ev100(16f, 0.01f, 200f), 1e-4f);        // twice as sensitive
        Assert.Equal(0f, ExposureMath.Ev100(1f, 1f, 100f), 1e-6f);                                                  // f/1 for a second is EV 0
    }

    [Fact]
    public void TheScaleIsOneOverTheSaturationLuminanceAndHalvesPerStop()
    {
        // At EV100 0 a luminance of 1.2 cd/m2 saturates the sensor, so it scales to exactly 1.
        Assert.Equal(1f / 1.2f, ExposureMath.Scale(0f), 1e-6f);
        Assert.Equal(1f, ExposureMath.Scale(0f) * 1.2f, 1e-6f);
        Assert.Equal(ExposureMath.Scale(10f) / 2f, ExposureMath.Scale(11f), 1e-9f);

        // A sunlit white surface: 100 000 lux * 0.8 / pi = 25 000 cd/m2, exposed at the sunny 16 EV100 of 14.64 (saturation at 1.2 * 2^14.64 = 30 000), is just under 1.
        float exposed = 100_000f * 0.8f / MathF.PI * ExposureMath.Scale(ExposureMath.Ev100(16f, 0.01f, 100f));
        Assert.InRange(exposed, 0.8f, 1f);
    }

    [Fact]
    public void AMidGrayLuminanceAndItsEv100AreEachOthersInverse()
    {
        // The meter's calibration: EV100 = log2(L * 100 / 12.5), so 0.125 cd/m2 is EV 0 and each doubling of the luminance is one more stop.
        Assert.Equal(0f, ExposureMath.Ev100FromLuminance(0.125f), 1e-6f);
        Assert.Equal(3f, ExposureMath.Ev100FromLuminance(1f), 1e-6f);
        foreach (float ev in new[] { -4f, 0f, 7f, 15f })
        {
            Assert.Equal(ev, ExposureMath.Ev100FromLuminance(ExposureMath.LuminanceFromEv100(ev)), 1e-4f);
        }
    }

    [Fact]
    public void ALuminanceFallsInTheBinOfItsLog2AndABinsCenterFallsInTheBin()
    {
        const float min = ExposureMath.DefaultMinLog2Luminance, max = ExposureMath.DefaultMaxLog2Luminance;

        Assert.Equal(0, ExposureMath.HistogramBin(0f, min, max));
        Assert.Equal(0, ExposureMath.HistogramBin(float.NaN, min, max));
        Assert.Equal(0, ExposureMath.HistogramBin(-5f, min, max));
        Assert.Equal(0, ExposureMath.HistogramBin(MathF.Pow(2f, min - 1f), min, max));       // below the range
        Assert.Equal(255, ExposureMath.HistogramBin(MathF.Pow(2f, max + 3f), min, max));     // above it
        Assert.Equal(255, ExposureMath.HistogramBin(float.PositiveInfinity, min, max));
        Assert.Equal(1, ExposureMath.HistogramBin(MathF.Pow(2f, min + 0.01f), min, max));

        for (int bin = 1; bin < ExposureMath.HistogramBins; bin++)
        {
            float center = ExposureMath.BinLog2Luminance(bin, min, max);
            Assert.Equal(bin, ExposureMath.HistogramBin(MathF.Pow(2f, center), min, max));
        }

        // The bins are even along the log axis: 28 stops over 254 bins.
        Assert.Equal(28f / 254f, ExposureMath.BinLog2Luminance(101, min, max) - ExposureMath.BinLog2Luminance(100, min, max), 1e-5f);
    }

    [Fact]
    public void TheAverageIsTheCenterOfTheBinForOneBinAndIgnoresBlackPixels()
    {
        const float min = ExposureMath.DefaultMinLog2Luminance, max = ExposureMath.DefaultMaxLog2Luminance;
        uint[] bins = new uint[ExposureMath.HistogramBins];
        bins[0] = 5000;      // black: not measured
        bins[120] = 77;

        Assert.Equal(ExposureMath.BinLog2Luminance(120, min, max), ExposureMath.AverageLog2Luminance(bins, min, max), 1e-5f);

        // An empty histogram falls back to the bottom of the range.
        Assert.Equal(min, ExposureMath.AverageLog2Luminance(new uint[ExposureMath.HistogramBins], min, max));
    }

    [Fact]
    public void ThePercentilesLeaveOutTheDarkestAndBrightestPixels()
    {
        const float min = ExposureMath.DefaultMinLog2Luminance, max = ExposureMath.DefaultMaxLog2Luminance;
        uint[] bins = new uint[ExposureMath.HistogramBins];
        bins[50] = 10;       // a dark corner: 10 percent
        bins[130] = 80;      // the scene
        bins[240] = 10;      // a lamp: 10 percent

        // All of it: the average is dragged to the middle of the range by the two ends (10 * 50 + 80 * 130 + 10 * 240 over 100, in bins).
        float all = ExposureMath.AverageLog2Luminance(bins, min, max, 0f, 1f);
        float expectedAll = ((10 * ExposureMath.BinLog2Luminance(50, min, max)) + (80 * ExposureMath.BinLog2Luminance(130, min, max)) + (10 * ExposureMath.BinLog2Luminance(240, min, max))) / 100f;
        Assert.Equal(expectedAll, all, 1e-4f);

        // Without the ends it is the scene's bin.
        Assert.Equal(ExposureMath.BinLog2Luminance(130, min, max), ExposureMath.AverageLog2Luminance(bins, min, max, 0.1f, 0.9f), 1e-4f);

        // A window that cuts into a bin takes only its part: 15 percent to 95 percent keeps 5 of the dark bin's 10, all 80 of the scene's and 5 of the lamp's 10.
        float partial = ExposureMath.AverageLog2Luminance(bins, min, max, 0.05f, 0.95f);
        float expectedPartial = ((5 * ExposureMath.BinLog2Luminance(50, min, max)) + (80 * ExposureMath.BinLog2Luminance(130, min, max)) + (5 * ExposureMath.BinLog2Luminance(240, min, max))) / 90f;
        Assert.Equal(expectedPartial, partial, 1e-4f);
    }

    [Fact]
    public void TheTargetIsClampedToTheAllowedRange()
    {
        Assert.Equal(ExposureMath.Ev100FromLuminance(100f), ExposureMath.TargetEv100(MathF.Log2(100f), -6f, 20f), 1e-4f);
        Assert.Equal(-6f, ExposureMath.TargetEv100(-10f, -6f, 20f));
        Assert.Equal(20f, ExposureMath.TargetEv100(18f, -6f, 20f));
        Assert.Equal(12f, ExposureMath.TargetEv100(18f, -6f, 12f));
    }

    [Fact]
    public void AdaptationIsExponentialIndependentOfTheFrameRateAndFasterToLightThanToDark()
    {
        // One second at a speed of 3 closes 1 - e^-3 = 95 percent of the gap upwards (a brighter scene), 63 percent downwards at a speed of 1.
        Assert.Equal(10f * (1f - MathF.Exp(-3f)), ExposureMath.Adapt(0f, 10f, 1f, 3f, 1f), 1e-4f);
        Assert.Equal(10f - (10f * (1f - MathF.Exp(-1f))), ExposureMath.Adapt(10f, 0f, 1f, 3f, 1f), 1e-4f);

        // Sixty steps of a sixtieth of a second reach what one second does.
        float stepped = 0f;
        for (int i = 0; i < 60; i++)
        {
            stepped = ExposureMath.Adapt(stepped, 10f, 1f / 60f, 3f, 1f);
        }

        Assert.Equal(ExposureMath.Adapt(0f, 10f, 1f, 3f, 1f), stepped, 1e-3f);
        Assert.Equal(7f, ExposureMath.Adapt(7f, 7f, 0.5f, 3f, 1f));
    }
}
