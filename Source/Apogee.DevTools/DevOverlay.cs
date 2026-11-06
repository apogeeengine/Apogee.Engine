using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Apogee.Graphics;
using Apogee.Native.CImGui;
using Apogee.Native.ImPlot;
using Apogee.Rendering;
using Apogee.Rendering.Graph;

namespace Apogee.DevTools;

/// <summary>A checkbox the host adds to the overlay's Debug section: it reads and writes a switch of the host's (physics debug drawing, say).</summary>
public sealed record OverlayToggle(string Label, Func<bool> Get, Action<bool> Set);

/// <summary>What the overlay shows. Fill it each frame from whatever the game has; leave out what it does not have.</summary>
public sealed class DevOverlayData
{
    public string GpuName { get; set; } = "";

    /// <summary>The CPU time of the last frame in milliseconds.</summary>
    public double CpuFrameMilliseconds { get; set; }

    /// <summary>The GPU sections of the last finished frame (<see cref="FrameLoop.Timings"/>).</summary>
    public IReadOnlyList<GpuTiming> GpuTimings { get; set; } = [];

    /// <summary>The render graph of the last frame (<see cref="SceneRenderer.LastPlan"/>).</summary>
    public GraphPlan? Plan { get; set; }

    public RenderStats? Stats { get; set; }

    /// <summary>The renderer's settings: the overlay edits exposure and the debug view in place.</summary>
    public RenderSettings? Settings { get; set; }

    /// <summary>The camera: the overlay edits its field of view, near plane and cluster range.</summary>
    public Camera? Camera { get; set; }

    public RenderScene? Scene { get; set; }

    /// <summary>Switches the host offers in a Debug section: whatever it can turn on to see what is going on.</summary>
    public IReadOnlyList<OverlayToggle> Toggles { get; set; } = [];

    /// <summary>Shaders that were edited and did not compile (<see cref="Apogee.Rendering.Shaders.ShaderLibrary.Errors"/>): the pipelines made from them are as they were before the edit. The overlay shows them in red.</summary>
    public IReadOnlyList<string> ShaderErrors { get; set; } = [];
}

/// <summary>
/// The engine's debug window: frame time graph, GPU pass timings, the render graph, and lighting statistics and controls. Call
/// <see cref="Draw"/> every frame between <see cref="ImGuiHost.BeginFrame(float)"/> and <see cref="ImGuiHost.EndFrame"/>.
/// </summary>
public sealed class DevOverlay
{
    private const int HistoryLength = 240;

    private readonly float[] _frameTimes = new float[HistoryLength];
    private int _frameTimeCursor;
    private int _samples;
    private readonly Dictionary<string, float[]> _gpuHistory = [];
    private int _gpuCursor;
    private int _gpuSamples;
    private bool _open = true;

    /// <summary>Shows or hides the window (the game binds a key to it).</summary>
    public bool Visible { get; set; } = true;

    /// <summary>The frame times recorded so far, oldest first.</summary>
    public IReadOnlyList<float> FrameTimeHistory =>
        [.. Enumerable.Range(0, Math.Min(_samples, HistoryLength)).Select(i => _frameTimes[(_frameTimeCursor - Math.Min(_samples, HistoryLength) + i + HistoryLength * 2) % HistoryLength])];

    /// <summary>The GPU milliseconds of each top-level pass over the last frames, oldest first (a pass that did not run in a frame has 0 there).</summary>
    public IReadOnlyDictionary<string, float[]> GpuHistory
    {
        get
        {
            int count = Math.Min(_gpuSamples, HistoryLength);
            var result = new Dictionary<string, float[]>();
            foreach ((string name, float[] ring) in _gpuHistory)
            {
                result[name] = Ordered(ring, _gpuCursor, count);
            }

            return result;
        }
    }

    private static float[] Ordered(float[] ring, int cursor, int count)
    {
        float[] ordered = new float[count];
        for (int i = 0; i < count; i++)
        {
            ordered[i] = ring[(cursor - count + i + (HistoryLength * 2)) % HistoryLength];
        }

        return ordered;
    }

    public void Draw(DevOverlayData data)
    {
        // The history keeps filling while the window is hidden, so it is complete when it opens.
        _frameTimes[_frameTimeCursor] = (float)data.CpuFrameMilliseconds;
        _frameTimeCursor = (_frameTimeCursor + 1) % HistoryLength;
        _samples++;
        RecordGpu(data.GpuTimings);

        if (!Visible)
        {
            return;
        }

        Ui.SetNextWindow(new Vector2(12, 12), new Vector2(420, 620));
        if (!Ui.Begin("Apogee", ref _open))
        {
            Ui.End();
            return;
        }

        if (!_open)
        {
            Visible = false;
            _open = true;
        }

        if (data.GpuName.Length > 0)
        {
            Ui.Text(data.GpuName);
        }

        if (data.ShaderErrors.Count > 0)
        {
            Ui.TextColored(new Vector4(1f, 0.35f, 0.3f, 1f), "Shader errors (the old shaders are still in use):");
            foreach (string error in data.ShaderErrors)
            {
                Ui.TextColored(new Vector4(1f, 0.55f, 0.5f, 1f), error);
            }

            Ui.Separator();
        }

        if (Ui.CollapsingHeader("Frame"))
        {
            DrawFrame(data);
        }

        if (Ui.CollapsingHeader("GPU passes") && data.GpuTimings.Count > 0)
        {
            DrawGpuHistory();
            DrawTimings(data.GpuTimings);
        }

        if (Ui.CollapsingHeader("Render graph") && data.Plan is not null)
        {
            DrawGraph(data.Plan);
        }

        if (Ui.CollapsingHeader("Lighting") && (data.Settings is not null || data.Stats is not null))
        {
            DrawLighting(data);
        }

        if (data.Toggles.Count > 0 && Ui.CollapsingHeader("Debug"))
        {
            foreach (OverlayToggle toggle in data.Toggles)
            {
                bool value = toggle.Get();
                if (Ui.Checkbox(toggle.Label, ref value))
                {
                    toggle.Set(value);
                }
            }
        }

        if (ScriptGui.Watches.Count > 0 && Ui.CollapsingHeader("Script watches"))
        {
            DrawWatches();
        }

        Ui.End();
    }

    private static void DrawWatches()
    {
        if (Ui.BeginTable("watches", 2))
        {
            foreach ((string name, string value) in ScriptGui.Watches)
            {
                Ui.Row();
                Ui.Cell(name);
                Ui.Cell(value);
            }

            Ui.EndTable();
        }
    }

    /// <summary>
    /// The frame-time plot, the GPU plot and the pass table with no window around them, for a panel of an editor. Call <see cref="Draw"/> every frame as well (with <see cref="Visible"/> false it only
    /// records the history), so that there is something to show.
    /// </summary>
    public void DrawProfiler(DevOverlayData data)
    {
        DrawFrame(data);
        if (data.GpuTimings.Count > 0)
        {
            DrawGpuHistory();
            DrawTimings(data.GpuTimings);
        }
    }

    private void RecordGpu(IReadOnlyList<GpuTiming> timings)
    {
        if (timings.Count == 0 && _gpuHistory.Count == 0)
        {
            return;
        }

        foreach (GpuTiming timing in timings)
        {
            if (!timing.Name.Contains('/', StringComparison.Ordinal) && !_gpuHistory.ContainsKey(timing.Name))
            {
                _gpuHistory[timing.Name] = new float[HistoryLength];
            }
        }

        // Every pass the overlay has seen gets a value for this frame: 0 when it did not run, so the lines stay in step.
        foreach ((string name, float[] ring) in _gpuHistory)
        {
            ring[_gpuCursor] = 0f;
        }

        foreach (GpuTiming timing in timings)
        {
            if (_gpuHistory.TryGetValue(timing.Name, out float[]? ring))
            {
                ring[_gpuCursor] = (float)timing.Milliseconds;
            }
        }

        _gpuCursor = (_gpuCursor + 1) % HistoryLength;
        _gpuSamples++;
    }

    private void DrawFrame(DevOverlayData data)
    {
        int count = Math.Min(_samples, HistoryLength);
        float[] ordered = [.. FrameTimeHistory];
        float average = count > 0 ? ordered.Average() : 0f;
        float worst = count > 0 ? ordered.Max() : 0f;
        float best = count > 0 ? ordered.Min() : 0f;

        Ui.Text(string.Create(CultureInfo.InvariantCulture, $"{(average > 0 ? 1000f / average : 0f):F0} fps   {average:F2} ms   (min {best:F2}, max {worst:F2})"));

        // The last 240 frames, newest on the right: a line over a shaded area, the y axis fixed from 0 to a little over the worst frame.
        if (Plots.Begin("##frametimes", new Vector2(-1, 90), AplotFlags.CanvasOnly | AplotFlags.NoInputs))
        {
            Plots.Axes(null, null, AplotAxisFlags.NoDecorations, AplotAxisFlags.NoLabel | AplotAxisFlags.NoGridLines);
            Plots.Limits(0, HistoryLength - 1, 0, MathF.Max(worst * 1.1f, 1f));
            Plots.AxisFormat(AplotAxis.Y1, "%.1f ms");
            AplotSpec spec = Plots.Colored(new Vector4(0.35f, 0.75f, 1f, 1f));
            // The history is oldest first and shorter than the window until it fills: it is drawn ending at the right edge.
            double start = HistoryLength - ordered.Length;
            Plots.Shaded("frame time", ordered, spec, 0, 1, start);
            Plots.Line("frame time", ordered, spec, 1, start);
            Plots.End();
        }
    }

    /// <summary>The GPU time of each top-level pass over the last frames, stacked: the top edge is the whole GPU frame.</summary>
    private void DrawGpuHistory()
    {
        int count = Math.Min(_gpuSamples, HistoryLength);
        if (count < 2 || _gpuHistory.Count == 0)
        {
            return;
        }

        var history = GpuHistory;
        float[] xs = new float[count];
        for (int i = 0; i < count; i++)
        {
            xs[i] = HistoryLength - count + i;
        }

        float[] totals = new float[count];
        foreach (float[] values in history.Values)
        {
            for (int i = 0; i < count; i++)
            {
                totals[i] += values[i];
            }
        }

        float top = MathF.Max(totals.Max() * 1.1f, 0.5f);
        if (Plots.Begin("##gpupasses", new Vector2(-1, 170), AplotFlags.NoInputs | AplotFlags.NoMenus | AplotFlags.NoBoxSelect | AplotFlags.NoMouseText))
        {
            Plots.Axes(null, null, AplotAxisFlags.NoDecorations, AplotAxisFlags.NoLabel);
            Plots.Limits(0, HistoryLength - 1, 0, top);
            Plots.AxisFormat(AplotAxis.Y1, "%.2f ms");
            Plots.Legend(AplotLocation.South, AplotLegendFlags.Outside | AplotLegendFlags.Horizontal | AplotLegendFlags.NoButtons);
            float[] lower = new float[count];
            foreach ((string name, float[] values) in history)
            {
                float[] upper = new float[count];
                for (int i = 0; i < count; i++)
                {
                    upper[i] = lower[i] + values[i];
                }

                Plots.Band(name, xs, lower, upper, Plots.Spec());
                lower = upper;
            }

            Plots.End();
        }
    }

    private static void DrawTimings(IReadOnlyList<GpuTiming> timings)
    {
        double total = timings.Where(t => !t.Name.Contains('/', StringComparison.Ordinal)).Sum(t => t.Milliseconds);
        if (Ui.BeginTable("gpu", 3))
        {
            Ui.TableHeader("Pass", "ms", "");
            foreach (GpuTiming timing in timings)
            {
                Ui.Row();
                Ui.Cell(timing.Name);
                Ui.Cell(timing.Milliseconds.ToString("F3", CultureInfo.InvariantCulture));
                Ui.Cell(total > 0 ? new string('#', Math.Clamp((int)(timing.Milliseconds / total * 20), 0, 20)) : "");
            }

            Ui.EndTable();
        }
    }

    private static void DrawGraph(GraphPlan plan)
    {
        Ui.Text(string.Create(CultureInfo.InvariantCulture, $"{plan.LivePassCount} passes ({plan.CulledPassCount} culled), {plan.BarrierCount} barriers"));
        Ui.Text(string.Create(CultureInfo.InvariantCulture, $"{plan.PhysicalImageCount} images for {plan.TransientImageCount} transients, {plan.PhysicalBufferCount} buffers"));
        if (Ui.BeginTable("graph", 4))
        {
            Ui.TableHeader("Pass", "Type", "Barriers", "Writes");
            foreach (PassPlan pass in plan.Passes)
            {
                Ui.Row();
                Ui.Cell(pass.Culled ? pass.Name + " (culled)" : pass.Name);
                Ui.Cell(pass.Type.ToString());
                Ui.Cell(pass.Culled ? "-" : pass.Barriers.ToString(CultureInfo.InvariantCulture));
                Ui.Cell(string.Join(", ", pass.Writes));
            }

            Ui.EndTable();
        }
    }

    private static void DrawLighting(DevOverlayData data)
    {
        if (data.Stats is { } stats)
        {
            Ui.Text(string.Create(CultureInfo.InvariantCulture, $"{stats.Draws} draws ({stats.Culled} culled), {stats.Lights} lights"));
            Ui.Text(stats.ClustersBuilt > 0
                ? string.Create(CultureInfo.InvariantCulture, $"clusters rebuilt this frame ({stats.ClustersBuilt})")
                : $"clusters: {ClusterGrid.CountX} x {ClusterGrid.CountY} x {ClusterGrid.CountZ}, up to {ClusterGrid.MaxLightsPerCluster} lights each");
        }

        if (data.Settings is { } settings)
        {
            float exposure = settings.Exposure;
            if (Ui.SliderFloat("Exposure", ref exposure, 0.05f, 8f))
            {
                settings.Exposure = exposure;
            }

            int view = (int)settings.Debug;
            if (Ui.Combo("View", ref view, "Final image", "Cluster lights (heatmap)"))
            {
                settings.Debug = (DebugView)view;
            }
        }

        if (data.Camera is { } camera)
        {
            float fov = camera.VerticalFov * 180f / MathF.PI;
            if (Ui.SliderFloat("Field of view", ref fov, 20f, 120f, "%.0f deg"))
            {
                camera.VerticalFov = fov * MathF.PI / 180f;
            }

            float far = camera.ClusterFar;
            if (Ui.SliderFloat("Cluster range", ref far, 10f, 1000f, "%.0f m"))
            {
                camera.ClusterFar = far;
            }
        }
    }
}
