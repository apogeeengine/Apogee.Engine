using System;

namespace Apogee.Scripting;

/// <summary>
/// The easing curves of <c>tween</c>: how the progress of a tween (0 to 1 in time) maps to the progress of its value. Scripts name them in snake_case (<c>quad_in_out</c>). The
/// <c>In</c> curves start slowly, the <c>Out</c> ones end slowly and the <c>InOut</c> ones do both. <c>Back</c>, <c>Elastic</c> and <c>Bounce</c> go past the ends of the range on the way.
/// </summary>
public enum Ease
{
    /// <summary>No easing: the value moves at a constant speed.</summary>
    Linear,

    /// <summary>Starts slowly: t squared.</summary>
    QuadIn,

    /// <summary>Ends slowly.</summary>
    QuadOut,

    /// <summary>Starts and ends slowly.</summary>
    QuadInOut,

    /// <summary>Starts slowly: t cubed.</summary>
    CubicIn,

    /// <summary>Ends slowly.</summary>
    CubicOut,

    /// <summary>Starts and ends slowly.</summary>
    CubicInOut,

    /// <summary>Starts slowly: t to the fourth.</summary>
    QuartIn,

    /// <summary>Ends slowly.</summary>
    QuartOut,

    /// <summary>Starts and ends slowly.</summary>
    QuartInOut,

    /// <summary>Starts slowly: t to the fifth.</summary>
    QuintIn,

    /// <summary>Ends slowly.</summary>
    QuintOut,

    /// <summary>Starts and ends slowly.</summary>
    QuintInOut,

    /// <summary>Starts slowly: a quarter of a sine wave.</summary>
    SineIn,

    /// <summary>Ends slowly.</summary>
    SineOut,

    /// <summary>Starts and ends slowly.</summary>
    SineInOut,

    /// <summary>Starts very slowly and ends fast: an exponential.</summary>
    ExpoIn,

    /// <summary>Starts fast and ends very slowly.</summary>
    ExpoOut,

    /// <summary>Very slow at both ends.</summary>
    ExpoInOut,

    /// <summary>Starts slowly: a quarter of a circle.</summary>
    CircIn,

    /// <summary>Ends slowly.</summary>
    CircOut,

    /// <summary>Starts and ends slowly.</summary>
    CircInOut,

    /// <summary>Pulls back below the start before it goes.</summary>
    BackIn,

    /// <summary>Overshoots the end and settles back.</summary>
    BackOut,

    /// <summary>Pulls back at the start and overshoots at the end.</summary>
    BackInOut,

    /// <summary>Swings like a spring before it leaves the start.</summary>
    ElasticIn,

    /// <summary>Swings like a spring around the end.</summary>
    ElasticOut,

    /// <summary>Swings at both ends.</summary>
    ElasticInOut,

    /// <summary>Bounces off the start.</summary>
    BounceIn,

    /// <summary>Bounces on the end like a dropped ball.</summary>
    BounceOut,

    /// <summary>Bounces at both ends.</summary>
    BounceInOut,
}

/// <summary>The easing functions (Robert Penner's set, written from the formulas).</summary>
public static class Easing
{
    private const double BackOvershoot = 1.70158;
    private const double ElasticPeriod = 2 * Math.PI / 3;
    private const double ElasticPeriodInOut = 2 * Math.PI / 4.5;

    /// <summary>
    /// The eased progress for a time <paramref name="t"/> from 0 to 1 (clamped to it). <c>Apply(e, 0)</c> is 0 and <c>Apply(e, 1)</c> is 1 exactly, for every curve. A curve with overshoot gives values below 0 or above 1 in
    /// between. NaN is taken as 0.
    /// </summary>
    public static double Apply(Ease ease, double t)
    {
        if (!(t > 0))
        {
            return 0;
        }

        if (t >= 1)
        {
            return 1;
        }

        return ease switch
        {
            Ease.Linear => t,
            Ease.QuadIn => t * t,
            Ease.QuadOut => 1 - Square(1 - t),
            Ease.QuadInOut => t < 0.5 ? 2 * t * t : 1 - (Square((-2 * t) + 2) / 2),
            Ease.CubicIn => t * t * t,
            Ease.CubicOut => 1 - Math.Pow(1 - t, 3),
            Ease.CubicInOut => t < 0.5 ? 4 * t * t * t : 1 - (Math.Pow((-2 * t) + 2, 3) / 2),
            Ease.QuartIn => Math.Pow(t, 4),
            Ease.QuartOut => 1 - Math.Pow(1 - t, 4),
            Ease.QuartInOut => t < 0.5 ? 8 * Math.Pow(t, 4) : 1 - (Math.Pow((-2 * t) + 2, 4) / 2),
            Ease.QuintIn => Math.Pow(t, 5),
            Ease.QuintOut => 1 - Math.Pow(1 - t, 5),
            Ease.QuintInOut => t < 0.5 ? 16 * Math.Pow(t, 5) : 1 - (Math.Pow((-2 * t) + 2, 5) / 2),
            Ease.SineIn => 1 - Math.Cos(t * Math.PI / 2),
            Ease.SineOut => Math.Sin(t * Math.PI / 2),
            Ease.SineInOut => -(Math.Cos(Math.PI * t) - 1) / 2,
            Ease.ExpoIn => Math.Pow(2, (10 * t) - 10),
            Ease.ExpoOut => 1 - Math.Pow(2, -10 * t),
            Ease.ExpoInOut => t < 0.5 ? Math.Pow(2, (20 * t) - 10) / 2 : (2 - Math.Pow(2, (-20 * t) + 10)) / 2,
            Ease.CircIn => 1 - Math.Sqrt(1 - Square(t)),
            Ease.CircOut => Math.Sqrt(1 - Square(t - 1)),
            Ease.CircInOut => t < 0.5 ? (1 - Math.Sqrt(1 - Square(2 * t))) / 2 : (Math.Sqrt(1 - Square((-2 * t) + 2)) + 1) / 2,
            Ease.BackIn => ((BackOvershoot + 1) * t * t * t) - (BackOvershoot * t * t),
            Ease.BackOut => 1 + ((BackOvershoot + 1) * Math.Pow(t - 1, 3)) + (BackOvershoot * Square(t - 1)),
            Ease.BackInOut => BackInOutOf(t),
            Ease.ElasticIn => -Math.Pow(2, (10 * t) - 10) * Math.Sin(((10 * t) - 10.75) * ElasticPeriod),
            Ease.ElasticOut => (Math.Pow(2, -10 * t) * Math.Sin(((10 * t) - 0.75) * ElasticPeriod)) + 1,
            Ease.ElasticInOut => t < 0.5
                ? -(Math.Pow(2, (20 * t) - 10) * Math.Sin(((20 * t) - 11.125) * ElasticPeriodInOut)) / 2
                : (Math.Pow(2, (-20 * t) + 10) * Math.Sin(((20 * t) - 11.125) * ElasticPeriodInOut) / 2) + 1,
            Ease.BounceIn => 1 - BounceOut(1 - t),
            Ease.BounceOut => BounceOut(t),
            Ease.BounceInOut => t < 0.5 ? (1 - BounceOut(1 - (2 * t))) / 2 : (1 + BounceOut((2 * t) - 1)) / 2,
            _ => t,
        };
    }

    private static double Square(double x) => x * x;

    private static double BackInOutOf(double t)
    {
        double c2 = BackOvershoot * 1.525;
        return t < 0.5
            ? Square(2 * t) * (((c2 + 1) * 2 * t) - c2) / 2
            : ((Square((2 * t) - 2) * (((c2 + 1) * ((t * 2) - 2)) + c2)) + 2) / 2;
    }

    private static double BounceOut(double t)
    {
        const double n1 = 7.5625;
        const double d1 = 2.75;
        if (t < 1 / d1)
        {
            return n1 * t * t;
        }

        if (t < 2 / d1)
        {
            t -= 1.5 / d1;
            return (n1 * t * t) + 0.75;
        }

        if (t < 2.5 / d1)
        {
            t -= 2.25 / d1;
            return (n1 * t * t) + 0.9375;
        }

        t -= 2.625 / d1;
        return (n1 * t * t) + 0.984375;
    }
}
