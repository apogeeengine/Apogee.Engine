using System;
using System.Numerics;
using Apogee.Interop;
using Apogee.Native.ImPlot;

namespace Apogee.DevTools;

/// <summary>
/// Plots with ImPlot, for the overlay's graphs and the editor's profiler: a small surface over the binding that takes strings and spans. Use it between <see cref="ImGuiHost.BeginFrame(float)"/> and
/// <see cref="ImGuiHost.EndFrame"/>: <c>if (Plots.Begin("id", size)) { Plots.Axes(...); Plots.Line(...); Plots.End(); }</c>.
/// </summary>
public static unsafe class Plots
{
    /// <summary>Begins a plot of <paramref name="size"/> pixels (a zero or negative side takes what is left). Call <see cref="End"/> only when this returns true.</summary>
    public static bool Begin(string id, Vector2 size, AplotFlags flags = 0)
    {
        fixed (byte* title = Utf8.ToZeroTerminated(id))
        {
            return Plot.BeginPlot(title, size.X, size.Y, (int)flags);
        }
    }

    public static void End() => Plot.EndPlot();

    /// <summary>The labels (null: none) and options of the x and y axes.</summary>
    public static void Axes(string? xLabel, string? yLabel, AplotAxisFlags xFlags = 0, AplotAxisFlags yFlags = 0)
    {
        fixed (byte* x = xLabel is null ? null : Utf8.ToZeroTerminated(xLabel))
        fixed (byte* y = yLabel is null ? null : Utf8.ToZeroTerminated(yLabel))
        {
            Plot.SetupAxes(x, y, (int)xFlags, (int)yFlags);
        }
    }

    /// <summary>Fixes the visible range of both axes (the player cannot pan or zoom them) or, with <paramref name="always"/> false, only sets it at the start.</summary>
    public static void Limits(double xMin, double xMax, double yMin, double yMax, bool always = true) =>
        Plot.SetupAxesLimits(xMin, xMax, yMin, yMax, (int)(always ? AplotCond.Always : AplotCond.Once));

    public static void Legend(AplotLocation location, AplotLegendFlags flags = 0) => Plot.SetupLegend((int)location, (int)flags);

    /// <summary>A printf format for the tick labels of an axis (<c>"%.1f ms"</c>).</summary>
    public static void AxisFormat(AplotAxis axis, string format)
    {
        fixed (byte* f = Utf8.ToZeroTerminated(format))
        {
            Plot.SetupAxisFormat((int)axis, f);
        }
    }

    /// <summary>A spec with ImPlot's defaults (automatic colors); change what you want.</summary>
    public static AplotSpec Spec()
    {
        AplotSpec spec = default;
        Plot.DefaultSpec(&spec);
        return spec;
    }

    /// <summary>A spec with this line color (RGBA, 0 to 1) and weight; the fill is the same color, half transparent.</summary>
    public static AplotSpec Colored(Vector4 color, float lineWeight = 1.5f)
    {
        AplotSpec spec = Spec();
        spec.LineColor[0] = color.X; spec.LineColor[1] = color.Y; spec.LineColor[2] = color.Z; spec.LineColor[3] = color.W;
        spec.FillColor[0] = color.X; spec.FillColor[1] = color.Y; spec.FillColor[2] = color.Z; spec.FillColor[3] = color.W;
        spec.LineWeight = lineWeight;
        spec.FillAlpha = 0.35f;
        return spec;
    }

    /// <summary>A line through <paramref name="values"/> at x = <paramref name="xStart"/> + i * <paramref name="xScale"/>.</summary>
    public static void Line(string label, ReadOnlySpan<float> values, in AplotSpec spec, double xScale = 1, double xStart = 0)
    {
        fixed (byte* l = Utf8.ToZeroTerminated(label))
        fixed (float* v = values)
        fixed (AplotSpec* s = &spec)
        {
            Plot.PlotLineF(l, v, values.Length, xScale, xStart, s);
        }
    }

    /// <summary>The area between the line through <paramref name="values"/> and <paramref name="yRef"/>.</summary>
    public static void Shaded(string label, ReadOnlySpan<float> values, in AplotSpec spec, double yRef = 0, double xScale = 1, double xStart = 0)
    {
        fixed (byte* l = Utf8.ToZeroTerminated(label))
        fixed (float* v = values)
        fixed (AplotSpec* s = &spec)
        {
            Plot.PlotShadedF(l, v, values.Length, yRef, xScale, xStart, s);
        }
    }

    /// <summary>The area between two lines (<paramref name="ys1"/> and <paramref name="ys2"/> at the x values <paramref name="xs"/>): a band in a stacked plot.</summary>
    public static void Band(string label, ReadOnlySpan<float> xs, ReadOnlySpan<float> ys1, ReadOnlySpan<float> ys2, in AplotSpec spec)
    {
        int count = Math.Min(xs.Length, Math.Min(ys1.Length, ys2.Length));
        fixed (byte* l = Utf8.ToZeroTerminated(label))
        fixed (float* x = xs)
        fixed (float* y1 = ys1)
        fixed (float* y2 = ys2)
        fixed (AplotSpec* s = &spec)
        {
            Plot.PlotShadedBetweenF(l, x, y1, y2, count, s);
        }
    }

    public static void Bars(string label, ReadOnlySpan<float> values, in AplotSpec spec, double barSize = 0.67, double shift = 0)
    {
        fixed (byte* l = Utf8.ToZeroTerminated(label))
        fixed (float* v = values)
        fixed (AplotSpec* s = &spec)
        {
            Plot.PlotBarsF(l, v, values.Length, barSize, shift, s);
        }
    }
}
