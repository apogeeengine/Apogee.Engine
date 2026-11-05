using Apogee.Core.Graphics;
using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Graphics;

namespace Apogee.Rendering.Graph;

/// <summary>A graph image: a transient one the graph allocates, or an imported one (the window's image, a persistent texture). Valid for one graph.</summary>
public readonly record struct ImageHandle(int Index, int Graph)
{
    public bool IsValid => Graph != 0;
}

public readonly record struct BufferHandle(int Index, int Graph)
{
    public bool IsValid => Graph != 0;
}

public enum PassType
{
    Graphics,
    Compute,
    Transfer,
}

/// <summary>An image as a pass sees it while it runs.</summary>
/// <param name="SampledIndex">The bindless index to read it with (a transient image with <see cref="ImageUsage.Sampled"/>), or 0.</param>
/// <param name="StorageIndex">The bindless storage image index (a transient image with <see cref="ImageUsage.Storage"/>), or 0.</param>
public readonly record struct ResolvedImage(
    GpuImage Image,
    ImageView View,
    PixelFormat Format,
    uint Width,
    uint Height,
    uint SampledIndex,
    uint StorageIndex);

/// <summary>What a pass's execute callback gets: the command recorder and its resources.</summary>
public readonly struct PassContext
{
    private readonly RenderGraph _graph;

    internal PassContext(RenderGraph graph, Frame frame)
    {
        _graph = graph;
        Frame = frame;
    }

    public Frame Frame { get; }

    public CommandRecorder Recorder => Frame.Recorder;

    public ResolvedImage Image(ImageHandle handle) => _graph.Resolve(handle);

    public GpuBuffer Buffer(BufferHandle handle) => _graph.Resolve(handle);
}

internal enum ResourceKind
{
    Image,
    Buffer,
}

/// <summary>One image or buffer of a graph, with everything the planner and the executor need to know about it.</summary>
internal sealed class ResourceNode
{
    public required int Index { get; init; }

    public required string Name { get; init; }

    public required ResourceKind Kind { get; init; }

    public bool Imported { get; init; }

    /// <summary>Passes that write an output are never culled.</summary>
    public bool IsOutput { get; init; }

    public ImageDescription ImageDescription { get; init; }

    public GpuImage? ImportedImage { get; init; }

    public Backbuffer? ImportedBackbuffer { get; init; }

    public uint ImportedSampledIndex { get; init; }

    public ImageState ImageInitial { get; init; }

    public ImageState ImageFinal { get; init; }

    public ulong BufferSize { get; init; }

    public BufferUsage BufferUsage { get; init; }

    public GpuBuffer? ImportedBuffer { get; init; }

    public BufferState BufferInitial { get; init; }

    public BufferState BufferFinal { get; init; }

    // Filled by the planner.
    public int FirstUse { get; set; } = int.MaxValue;

    public int LastUse { get; set; } = -1;

    public int Physical { get; set; } = -1;
}

/// <summary>How one pass uses one resource.</summary>
internal readonly record struct ResourceUse(int Resource, int State, bool Reads, bool Writes);

internal sealed record ColorAttachmentUse(int Resource, Vector4? Clear);

internal sealed record DepthAttachmentUse(int Resource, float? Clear, bool ReadOnly);

internal sealed class PassNode
{
    public required int Index { get; init; }

    public required string Name { get; init; }

    public required PassType Type { get; init; }

    public List<ResourceUse> ImageUses { get; } = [];

    public List<ResourceUse> BufferUses { get; } = [];

    public List<ColorAttachmentUse> Colors { get; } = [];

    public DepthAttachmentUse? Depth { get; set; }

    public bool SideEffects { get; set; }

    public Action<PassContext>? Execute { get; set; }
}
