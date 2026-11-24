using Weft.Core.Graphics;
using System;
using System.Collections.Generic;
using System.Numerics;
using Weft.Graphics;

namespace Weft.Rendering.Graph;

/// <summary>A graph image: a transient one the graph allocates, or an imported one (the window's image, a persistent texture). Valid for one graph.</summary>
public readonly record struct ImageHandle(int Index, int Graph)
{
    public bool IsValid => Graph != 0;
}

public readonly record struct BufferHandle(int Index, int Graph)
{
    public bool IsValid => Graph != 0;
}

/// <summary>
/// A rectangle of an image's subresources: some mip levels of some array layers (six per cube). A pass that declares one of these uses only that part of the image, and the graph tracks and transitions the parts
/// separately. A count of <see cref="uint.MaxValue"/> means every level or layer that remains.
/// </summary>
public readonly record struct SubresourceRange(uint BaseMip, uint MipCount, uint BaseLayer, uint LayerCount)
{
    /// <summary>One mip level, every layer.</summary>
    public static SubresourceRange Mip(uint level) => new(level, 1, 0, uint.MaxValue);

    /// <summary>Several mip levels, every layer.</summary>
    public static SubresourceRange Mips(uint baseMip, uint count = uint.MaxValue) => new(baseMip, count, 0, uint.MaxValue);

    /// <summary>One array layer (a cube face is a layer), every mip level.</summary>
    public static SubresourceRange Layer(uint layer) => new(0, uint.MaxValue, layer, 1);

    /// <summary>Several array layers, every mip level.</summary>
    public static SubresourceRange Layers(uint baseLayer, uint count = uint.MaxValue) => new(0, uint.MaxValue, baseLayer, count);

    /// <summary>One mip level of one layer.</summary>
    public static SubresourceRange Of(uint mip, uint layer) => new(mip, 1, layer, 1);

    internal bool Overlaps(SubresourceRange other) =>
        BaseMip < other.BaseMip + other.MipCount && other.BaseMip < BaseMip + MipCount
        && BaseLayer < other.BaseLayer + other.LayerCount && other.BaseLayer < BaseLayer + LayerCount;

    public override string ToString() => $"mips {BaseMip}+{MipCount}, layers {BaseLayer}+{LayerCount}";
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

    /// <summary>
    /// The image as a view of some of its mips and layers: <see cref="ResolvedImage.View"/> is a view of just that part, the bindless indices read or write just that part (a mip to build the next one from, a face to
    /// store into), and the size is that of its first mip. The view lives as long as the image's frame slot needs it: do not keep it past the frame.
    /// </summary>
    public ResolvedImage Image(ImageHandle handle, SubresourceRange range) => _graph.Resolve(handle, range);

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

    /// <summary>The mip levels the image has (images only).</summary>
    public uint MipCount { get; init; } = 1;

    /// <summary>The array layers the image has, six per cube (images only).</summary>
    public uint LayerCount { get; init; } = 1;

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
internal readonly record struct ResourceUse(int Resource, int State, bool Reads, bool Writes, SubresourceRange Range = default);

internal sealed record ColorAttachmentUse(int Resource, Vector4? Clear, SubresourceRange Range);

internal sealed record DepthAttachmentUse(int Resource, float? Clear, bool ReadOnly, SubresourceRange Range);

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
