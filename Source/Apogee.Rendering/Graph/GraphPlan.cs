using System.Collections.Generic;
using System.Linq;
using System.Text;
using Apogee.Graphics;

namespace Apogee.Rendering.Graph;

/// <summary>What the planner decided about one pass.</summary>
/// <param name="Culled">Nothing that reaches an output depends on it, so it does not run.</param>
public sealed record PassPlan(string Name, PassType Type, bool Culled, IReadOnlyList<string> Reads, IReadOnlyList<string> Writes, int Barriers);

internal readonly record struct PlannedImageBarrier(int Resource, ImageState Before, ImageState After);

internal readonly record struct PlannedBufferBarrier(int Resource, BufferState Before, BufferState After);

/// <summary>The graph, compiled: which passes run, in what order, what is allocated, and the barriers in front of each pass.</summary>
public sealed class GraphPlan
{
    internal List<PassNode> LivePasses { get; } = [];

    internal Dictionary<int, (List<PlannedImageBarrier> Images, List<PlannedBufferBarrier> Buffers)> Barriers { get; } = [];

    internal (List<PlannedImageBarrier> Images, List<PlannedBufferBarrier> Buffers) Epilogue { get; } = ([], []);

    /// <summary>The distinct images the graph allocates (transients that share one because their lifetimes do not overlap count once).</summary>
    internal List<ImageDescription> PhysicalImages { get; } = [];

    internal List<(ulong Size, BufferUsage Usage)> PhysicalBuffers { get; } = [];

    public IReadOnlyList<PassPlan> Passes { get; internal set; } = [];

    public int LivePassCount => Passes.Count(p => !p.Culled);

    public int CulledPassCount => Passes.Count(p => p.Culled);

    /// <summary>Transient images the graph declared and used, before sharing.</summary>
    public int TransientImageCount { get; internal set; }

    /// <summary>Images actually allocated after transients with disjoint lifetimes share one.</summary>
    public int PhysicalImageCount => PhysicalImages.Count;

    public int PhysicalBufferCount => PhysicalBuffers.Count;

    public int BarrierCount => Passes.Sum(p => p.Barriers) + Epilogue.Images.Count + Epilogue.Buffers.Count;

    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (PassPlan pass in Passes)
        {
            text.Append(pass.Culled ? "  (culled) " : "  ").Append(pass.Name).Append(" [").Append(pass.Type).Append(']');
            if (!pass.Culled)
            {
                text.Append("  reads: ").Append(string.Join(", ", pass.Reads)).Append("  writes: ").Append(string.Join(", ", pass.Writes)).Append("  barriers: ").Append(pass.Barriers);
            }

            text.AppendLine();
        }

        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"  {PhysicalImageCount} image(s) for {TransientImageCount} transient(s), {PhysicalBufferCount} buffer(s), {BarrierCount} barrier(s)");
        return text.ToString();
    }
}
