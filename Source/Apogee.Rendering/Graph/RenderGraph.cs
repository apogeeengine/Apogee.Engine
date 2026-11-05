using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Apogee.Graphics;

namespace Apogee.Rendering.Graph;

/// <summary>
/// One frame's rendering as a graph. Passes declare how they use images and buffers; <see cref="Compile"/> culls the passes nothing depends on,
/// shares memory between transients whose lifetimes do not overlap, and works out the barriers; <see cref="Execute"/> runs the rest in
/// the order they were added, each in a labelled, timed section. Build a new graph every frame: it is cheap, and the transients come from a
/// <see cref="TransientPool"/> that lives across frames.
/// </summary>
/// <remarks>
/// Images are tracked as a whole: every mip and layer is in one state. Transients have one mip and are not multisampled.
/// </remarks>
public sealed class RenderGraph
{
    private static int s_nextId;

    private readonly int _id = Interlocked.Increment(ref s_nextId);
    private readonly GraphicsDevice? _device;
    private readonly TransientPool? _pool;
    private readonly List<ResourceNode> _resources = [];
    private readonly List<PassNode> _passes = [];
    private GraphPlan? _plan;
    private ResolvedImage[] _images = [];
    private GpuBuffer?[] _buffers = [];

    /// <summary>A graph that can be compiled and executed.</summary>
    public RenderGraph(GraphicsDevice device, TransientPool pool)
    {
        _device = device;
        _pool = pool;
    }

    /// <summary>A graph that can only be compiled: for tools, and for testing the planner without a GPU.</summary>
    public RenderGraph()
    {
    }

    /// <summary>The compiled plan, once <see cref="Compile"/> or <see cref="Execute"/> ran.</summary>
    public GraphPlan? Plan => _plan;

    /// <summary>An image the graph allocates and frees: its contents exist only inside the frame.</summary>
    public ImageHandle CreateImage(string name, in ImageDescription description)
    {
        if (description.MipLevels != 1 || description.Samples != SampleCount.Count1)
        {
            throw new RenderGraphException($"Transient image '{name}' must have one mip level and no multisampling.");
        }

        _plan = null;
        _resources.Add(new ResourceNode { Index = _resources.Count, Name = name, Kind = ResourceKind.Image, ImageDescription = description });
        return new ImageHandle(_resources.Count - 1, _id);
    }

    /// <summary>
    /// An image that exists outside the graph. It is in <paramref name="initial"/> when the graph starts and is left in <paramref name="final"/>.
    /// With <paramref name="output"/> the passes that write it always run.
    /// </summary>
    /// <param name="sampledIndex">The image's bindless index, if the passes read it by index.</param>
    public ImageHandle ImportImage(string name, GpuImage image, ImageState initial, ImageState final, bool output = false, uint sampledIndex = 0)
    {
        _plan = null;
        _resources.Add(new ResourceNode
        {
            Index = _resources.Count,
            Name = name,
            Kind = ResourceKind.Image,
            Imported = true,
            IsOutput = output,
            ImportedImage = image,
            ImportedSampledIndex = sampledIndex,
            ImageInitial = initial,
            ImageFinal = final,
        });
        return new ImageHandle(_resources.Count - 1, _id);
    }

    /// <summary>The window's image: it starts undefined, ends ready to present, and is an output, so what draws into it always runs.</summary>
    public ImageHandle ImportBackbuffer(in Backbuffer backbuffer)
    {
        _plan = null;
        _resources.Add(new ResourceNode
        {
            Index = _resources.Count,
            Name = "Backbuffer",
            Kind = ResourceKind.Image,
            Imported = true,
            IsOutput = true,
            ImportedBackbuffer = backbuffer,
            ImageInitial = ImageState.Undefined,
            ImageFinal = ImageState.Present,
        });
        return new ImageHandle(_resources.Count - 1, _id);
    }

    public BufferHandle CreateBuffer(string name, ulong size, BufferUsage usage)
    {
        _plan = null;
        _resources.Add(new ResourceNode { Index = _resources.Count, Name = name, Kind = ResourceKind.Buffer, BufferSize = size, BufferUsage = usage });
        return new BufferHandle(_resources.Count - 1, _id);
    }

    public BufferHandle ImportBuffer(string name, GpuBuffer buffer, BufferState initial, BufferState final, bool output = false)
    {
        _plan = null;
        _resources.Add(new ResourceNode
        {
            Index = _resources.Count,
            Name = name,
            Kind = ResourceKind.Buffer,
            Imported = true,
            IsOutput = output,
            ImportedBuffer = buffer,
            BufferSize = buffer.Size,
            BufferInitial = initial,
            BufferFinal = final,
        });
        return new BufferHandle(_resources.Count - 1, _id);
    }

    /// <summary>
    /// Adds a pass. <paramref name="setup"/> runs now and declares the pass's resources and its execute callback; the callback runs during
    /// <see cref="Execute"/>, in the order passes were added.
    /// </summary>
    public void AddPass(string name, PassType type, Action<PassBuilder> setup)
    {
        _plan = null;
        var pass = new PassNode { Index = _passes.Count, Name = name, Type = type };
        _passes.Add(pass);
        setup(new PassBuilder(this, pass));
    }

    /// <summary>Works out which passes run, what is allocated and where the barriers go. Throws <see cref="RenderGraphException"/> for an inconsistent graph.</summary>
    public GraphPlan Compile() => _plan ??= Planner.Plan(_resources, _passes);

    /// <summary>Records the graph into the frame's command buffer.</summary>
    public void Execute(Frame frame)
    {
        if (_device is null || _pool is null)
        {
            throw new InvalidOperationException("This graph was created without a device and can only be compiled.");
        }

        GraphPlan plan = Compile();
        ResolveResources(plan, frame.SlotIndex);
        CommandRecorder recorder = frame.Recorder;
        var context = new PassContext(this, frame);

        foreach (PassNode pass in plan.LivePasses)
        {
            using (frame.Scope(pass.Name))
            {
                (List<PlannedImageBarrier> images, List<PlannedBufferBarrier> buffers) = plan.Barriers[pass.Index];
                EmitBarriers(recorder, images, buffers);

                bool rendering = pass.Colors.Count > 0 || pass.Depth is not null;
                if (rendering)
                {
                    BeginRendering(recorder, pass);
                }

                pass.Execute?.Invoke(context);
                if (rendering)
                {
                    recorder.EndRendering();
                }
            }
        }

        EmitBarriers(recorder, plan.Epilogue.Images, plan.Epilogue.Buffers);
        _pool.EndExecution(frame.SlotIndex);
    }

    internal ResolvedImage Resolve(ImageHandle handle)
    {
        CheckImage(handle);
        return _images.Length > handle.Index && _images[handle.Index].Image is not null
            ? _images[handle.Index]
            : throw new RenderGraphException($"Image '{_resources[handle.Index].Name}' is not available: its passes were culled or the graph is not executing.");
    }

    internal GpuBuffer Resolve(BufferHandle handle)
    {
        CheckBuffer(handle);
        return _buffers.Length > handle.Index && _buffers[handle.Index] is { } buffer
            ? buffer
            : throw new RenderGraphException($"Buffer '{_resources[handle.Index].Name}' is not available: its passes were culled or the graph is not executing.");
    }

    internal ResourceNode CheckImage(ImageHandle handle) =>
        handle.Graph == _id && _resources[handle.Index].Kind == ResourceKind.Image
            ? _resources[handle.Index]
            : throw new RenderGraphException("This image handle belongs to another graph.");

    internal ResourceNode CheckBuffer(BufferHandle handle) =>
        handle.Graph == _id && _resources[handle.Index].Kind == ResourceKind.Buffer
            ? _resources[handle.Index]
            : throw new RenderGraphException("This buffer handle belongs to another graph.");

    private void ResolveResources(GraphPlan plan, int slot)
    {
        _images = new ResolvedImage[_resources.Count];
        _buffers = new GpuBuffer?[_resources.Count];

        // Transients sharing a physical slot get the same pooled object.
        var pooledImages = new TransientPool.PooledImage?[plan.PhysicalImages.Count];
        var pooledBuffers = new GpuBuffer?[plan.PhysicalBuffers.Count];
        foreach (ResourceNode resource in _resources.Where(r => r.LastUse >= 0))
        {
            if (resource.Kind == ResourceKind.Image)
            {
                if (resource.ImportedBackbuffer is { } back)
                {
                    _images[resource.Index] = new ResolvedImage(back.Image, back.View, back.Format, back.Width, back.Height, 0, 0);
                }
                else if (resource.ImportedImage is { } image)
                {
                    _images[resource.Index] = new ResolvedImage(image, image.View, image.Format, image.Width, image.Height, resource.ImportedSampledIndex, 0);
                }
                else
                {
                    TransientPool.PooledImage pooled = pooledImages[resource.Physical] ??= _pool!.AcquireImage(slot, plan.PhysicalImages[resource.Physical]);
                    GpuImage image2 = pooled.Image;
                    _images[resource.Index] = new ResolvedImage(image2, image2.View, image2.Format, image2.Width, image2.Height, pooled.SampledIndex, pooled.StorageIndex);
                }
            }
            else
            {
                _buffers[resource.Index] = resource.ImportedBuffer
                    ?? (pooledBuffers[resource.Physical] ??= _pool!.AcquireBuffer(slot, plan.PhysicalBuffers[resource.Physical].Size, plan.PhysicalBuffers[resource.Physical].Usage));
            }
        }
    }

    private void EmitBarriers(CommandRecorder recorder, List<PlannedImageBarrier> images, List<PlannedBufferBarrier> buffers)
    {
        if (images.Count == 0 && buffers.Count == 0)
        {
            return;
        }

        ImageBarrier[] imageBarriers = [.. images.Select(b =>
        {
            ResolvedImage image = _images[b.Resource];
            return new ImageBarrier(image.Image, b.Before, b.After);
        })];
        BufferBarrier[] bufferBarriers = [.. buffers.Select(b => new BufferBarrier(_buffers[b.Resource]!, b.Before, b.After))];
        recorder.Barriers(imageBarriers, bufferBarriers);
    }

    private void BeginRendering(CommandRecorder recorder, PassNode pass)
    {
        ColorTarget[] colors = [.. pass.Colors.Select(c => new ColorTarget(_images[c.Resource].View, Clear: c.Clear is not null, ClearColor: c.Clear ?? default))];
        DepthTarget? depth = pass.Depth is { } d
            ? new DepthTarget(_images[d.Resource].View, Clear: d.Clear is not null, ClearDepth: d.Clear ?? 0f, ReadOnly: d.ReadOnly)
            : null;
        ResolvedImage first = pass.Colors.Count > 0 ? _images[pass.Colors[0].Resource] : _images[pass.Depth!.Resource];
        recorder.BeginRendering(first.Width, first.Height, colors, depth);
    }
}
