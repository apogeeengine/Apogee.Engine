using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Graphics;

namespace Apogee.Rendering.Graph;

/// <summary>
/// What a pass declares while the graph is built: how it uses each image and buffer. The graph derives culling, lifetimes and barriers from this. Every image method takes an optional
/// <see cref="SubresourceRange"/>: without one the pass uses the whole image (every mip and layer), with one only that part, which the graph tracks and transitions on its own.
/// </summary>
public sealed class PassBuilder
{
    private readonly RenderGraph _graph;
    private readonly PassNode _pass;

    internal PassBuilder(RenderGraph graph, PassNode pass)
    {
        _graph = graph;
        _pass = pass;
    }

    /// <summary>Samples the image in shaders (it ends in <see cref="ImageState.ShaderRead"/>).</summary>
    public ImageHandle ReadSampled(ImageHandle image, SubresourceRange? range = null) => UseImage(image, ImageState.ShaderRead, reads: true, writes: false, range);

    /// <summary>Writes the whole image as a storage image (compute shaders, or fragment shaders): what was in it before is not needed.</summary>
    public ImageHandle WriteStorage(ImageHandle image, SubresourceRange? range = null) => UseImage(image, ImageState.StorageReadWrite, reads: false, writes: true, range);

    /// <summary>Reads and writes the image as a storage image: the pass needs what was in it before.</summary>
    public ImageHandle ReadWriteStorage(ImageHandle image, SubresourceRange? range = null) => UseImage(image, ImageState.StorageReadWrite, reads: true, writes: true, range);

    public ImageHandle CopyFrom(ImageHandle image, SubresourceRange? range = null) => UseImage(image, ImageState.TransferSource, reads: true, writes: false, range);

    public ImageHandle CopyTo(ImageHandle image, SubresourceRange? range = null) => UseImage(image, ImageState.TransferDestination, reads: false, writes: true, range);

    /// <summary>
    /// Renders into the image as a color attachment; the graph begins and ends the rendering. With <paramref name="clear"/> the old contents are discarded, otherwise they are loaded. The attachment is one mip level
    /// (a range must say which when the image has several), and its size is that of the level.
    /// </summary>
    public ImageHandle WriteColor(ImageHandle image, Vector4? clear = null, SubresourceRange? range = null)
    {
        SubresourceRange resolved = UseAttachment(image, ImageState.ColorAttachment, reads: clear is null, writes: true, range);
        _pass.Colors.Add(new ColorAttachmentUse(_graph.CheckImage(image).Index, clear, resolved));
        return image;
    }

    /// <summary>Renders into the image as the depth attachment. The clear value is reverse-Z's far plane, 0, unless given; <c>null</c> loads the old contents.</summary>
    public ImageHandle WriteDepth(ImageHandle image, float? clear = 0f, SubresourceRange? range = null)
    {
        SubresourceRange resolved = UseAttachment(image, ImageState.DepthAttachment, reads: clear is null, writes: true, range);
        _pass.Depth = new DepthAttachmentUse(_graph.CheckImage(image).Index, clear, ReadOnly: false, resolved);
        return image;
    }

    /// <summary>Depth-tests against the image without writing it, and lets shaders sample it.</summary>
    public ImageHandle ReadDepth(ImageHandle image, SubresourceRange? range = null)
    {
        SubresourceRange resolved = UseAttachment(image, ImageState.DepthRead, reads: true, writes: false, range);
        _pass.Depth = new DepthAttachmentUse(_graph.CheckImage(image).Index, null, ReadOnly: true, resolved);
        return image;
    }

    /// <summary>Reads the buffer in shaders (through its address).</summary>
    public BufferHandle Read(BufferHandle buffer) => UseBuffer(buffer, BufferState.ShaderRead, reads: true, writes: false);

    /// <summary>Writes the whole buffer in shaders: what was in it before is not needed.</summary>
    public BufferHandle Write(BufferHandle buffer) => UseBuffer(buffer, BufferState.ShaderWrite, reads: false, writes: true);

    /// <summary>Reads and writes the buffer in shaders: the pass needs what was in it before (a counter it adds to, a list it appends to).</summary>
    public BufferHandle ReadWrite(BufferHandle buffer) => UseBuffer(buffer, BufferState.ShaderWrite, reads: true, writes: true);

    /// <summary>Reads the buffer as the arguments of an indirect dispatch or draw (<see cref="CommandRecorder.DispatchIndirect"/>, <see cref="CommandRecorder.DrawIndexedIndirect"/>); the buffer needs <see cref="BufferUsage.Indirect"/>.</summary>
    public BufferHandle ReadIndirect(BufferHandle buffer) => UseBuffer(buffer, BufferState.IndirectRead, reads: true, writes: false);

    public BufferHandle CopyFrom(BufferHandle buffer) => UseBuffer(buffer, BufferState.TransferSource, reads: true, writes: false);

    public BufferHandle CopyTo(BufferHandle buffer) => UseBuffer(buffer, BufferState.TransferDestination, reads: false, writes: true);

    /// <summary>The pass does something the graph cannot see (writes to memory it does not track, to the screen), so it is never culled.</summary>
    public void NoCull() => _pass.SideEffects = true;

    /// <summary>The commands of the pass. For a graphics pass with attachments, rendering is already begun and ends after the callback.</summary>
    public void Execute(Action<PassContext> execute) => _pass.Execute = execute;

    private SubresourceRange UseAttachment(ImageHandle handle, ImageState state, bool reads, bool writes, SubresourceRange? range)
    {
        ResourceNode node = _graph.CheckImage(handle);
        if (range is null && node.MipCount > 1)
        {
            throw new RenderGraphException($"Pass '{_pass.Name}' renders to '{node.Name}', which has {node.MipCount} mip levels: say which one with a SubresourceRange.");
        }

        SubresourceRange resolved = Resolve(node, range);
        if (resolved.MipCount != 1)
        {
            throw new RenderGraphException($"Pass '{_pass.Name}' renders to {resolved.MipCount} mip levels of '{node.Name}' at once; an attachment is one level.");
        }

        Merge(_pass.ImageUses, node, (int)state, reads, writes, resolved);
        return resolved;
    }

    private ImageHandle UseImage(ImageHandle handle, ImageState state, bool reads, bool writes, SubresourceRange? range)
    {
        ResourceNode node = _graph.CheckImage(handle);
        Merge(_pass.ImageUses, node, (int)state, reads, writes, Resolve(node, range));
        return handle;
    }

    private BufferHandle UseBuffer(BufferHandle handle, BufferState state, bool reads, bool writes)
    {
        ResourceNode node = _graph.CheckBuffer(handle);
        Merge(_pass.BufferUses, node, (int)state, reads, writes, default);
        return handle;
    }

    /// <summary>The range with every default filled in and checked against the image.</summary>
    private SubresourceRange Resolve(ResourceNode node, SubresourceRange? range)
    {
        if (range is not { } r)
        {
            return new SubresourceRange(0, node.MipCount, 0, node.LayerCount);
        }

        uint mips = r.MipCount == uint.MaxValue && r.BaseMip <= node.MipCount ? node.MipCount - r.BaseMip : r.MipCount;
        uint layers = r.LayerCount == uint.MaxValue && r.BaseLayer <= node.LayerCount ? node.LayerCount - r.BaseLayer : r.LayerCount;
        if (mips == 0 || layers == 0 || (ulong)r.BaseMip + mips > node.MipCount || (ulong)r.BaseLayer + layers > node.LayerCount)
        {
            throw new RenderGraphException($"Pass '{_pass.Name}' uses {new SubresourceRange(r.BaseMip, mips, r.BaseLayer, layers)} of '{node.Name}', which has {node.MipCount} mip level(s) and {node.LayerCount} layer(s).");
        }

        return new SubresourceRange(r.BaseMip, mips, r.BaseLayer, layers);
    }

    private void Merge(List<ResourceUse> uses, ResourceNode node, int state, bool reads, bool writes, SubresourceRange range)
    {
        bool image = node.Kind == ResourceKind.Image;
        for (int i = 0; i < uses.Count; i++)
        {
            ResourceUse existing = uses[i];
            if (existing.Resource != node.Index || (image && !existing.Range.Overlaps(range)))
            {
                continue;
            }

            if (existing.State != state)
            {
                throw new RenderGraphException($"Pass '{_pass.Name}' uses '{node.Name}' in two different ways at once ({StateName(node, existing.State)} and {StateName(node, state)}).");
            }

            if (existing.Range == range)
            {
                uses[i] = existing with { Reads = existing.Reads || reads, Writes = existing.Writes || writes };
                return;
            }
        }

        uses.Add(new ResourceUse(node.Index, state, reads, writes, range));
    }

    internal static string StateName(ResourceNode node, int state) => node.Kind == ResourceKind.Image ? ((ImageState)state).ToString() : ((BufferState)state).ToString();
}
