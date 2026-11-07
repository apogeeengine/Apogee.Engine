using Apogee.DevTools;

namespace Apogee.Editor.Panels;

/// <summary>Frame times and the GPU time of every pass, as ImPlot plots (the same drawing as the dev overlay's).</summary>
public static class ProfilerPanel
{
    public static void Draw(EditorContext ctx)
    {
        if (Ui.Begin("Profiler"))
        {
            if (ctx.ProfilerData.GpuName.Length > 0)
            {
                Ui.Text(ctx.ProfilerData.GpuName);
            }

            ctx.Profiler.DrawProfiler(ctx.ProfilerData);
        }

        Ui.End();
    }
}
