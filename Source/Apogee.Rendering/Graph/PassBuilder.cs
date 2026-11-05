using System;
using System.Numerics;
using Apogee.Graphics;

namespace Apogee.Rendering.Graph;

/// <summary>What a pass declares while the graph is built: how it uses each image and buffer. The graph derives culling, lifetimes and barriers from this.</summary>
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
    public ImageHandle ReadSampled(ImageHandle image) => UseImage(image, ImageState.ShaderRead, reads: true, writes: false);

    /// <summary>Writes the whole image as a storage image (compute shaders, or fragment shaders): what was in it before is not needed.</summary>
    public ImageHandle WriteStorage(ImageHandle image) => UseImage(image, ImageState.StorageReadWrite, reads: false, writes: true);

    /// <summary>Reads and writes the image as a storage image: the pass needs what was in it before.</summary>
    public ImageHandle ReadWriteStorage(ImageHandle image) => UseImage(image, ImageState.StorageReadWrite, reads: true, writes: true);

    public ImageHandle CopyFrom(ImageHandle image) => UseImage(image, ImageState.TransferSource, reads: true, writes: false);

    public ImageHandle CopyTo(ImageHandle image) => UseImage(image, ImageState.TransferDestination, reads: false, writes: true);

    /// <summary>Renders into the image as a color attachment; the graph begins and ends the rendering. With <paramref name="clear"/> the old contents are discarded, otherwise they are loaded.</summary>
    public ImageHandle WriteColor(ImageHandle image, Vector4? clear = null)
    {
        UseImage(image, ImageState.ColorAttachment, reads: clear is null, writes: true);
        _pass.Colors.Add(new ColorAttachmentUse(_graph.CheckImage(image).Index, clear));
        return image;
    }

    /// <summary>Renders into the image as the depth attachment. The clear value is reverse-Z's far plane, 0, unless given; <c>null</c> loads the old contents.</summary>
    public ImageHandle WriteDepth(ImageHandle image, float? clear = 0f)
    {
        UseImage(image, ImageState.DepthAttachment, reads: clear is null, writes: true);
        _pass.Depth = new DepthAttachmentUse(_graph.CheckImage(image).Index, clear, ReadOnly: false);
        return image;
    }

    /// <summary>Depth-tests against the image without writing it, and lets shaders sample it.</summary>
    public ImageHandle ReadDepth(ImageHandle image)
    {
        UseImage(image, ImageState.DepthRead, reads: true, writes: false);
        _pass.Depth = new DepthAttachmentUse(_graph.CheckImage(image).Index, null, ReadOnly: true);
        return image;
    }

    /// <summary>Reads the buffer in shaders (through its address).</summary>
    public BufferHandle Read(BufferHandle buffer) => UseBuffer(buffer, BufferState.ShaderRead, reads: true, writes: false);

    /// <summary>Writes the whole buffer in shaders: what was in it before is not needed.</summary>
    public BufferHandle Write(BufferHandle buffer) => UseBuffer(buffer, BufferState.ShaderWrite, reads: false, writes: true);

    /// <summary>Reads and writes the buffer in shaders: the pass needs what was in it before (a counter it adds to, a list it appends to).</summary>
    public BufferHandle ReadWrite(BufferHandle buffer) => UseBuffer(buffer, BufferState.ShaderWrite, reads: true, writes: true);

    public BufferHandle CopyFrom(BufferHandle buffer) => UseBuffer(buffer, BufferState.TransferSource, reads: true, writes: false);

    public BufferHandle CopyTo(BufferHandle buffer) => UseBuffer(buffer, BufferState.TransferDestination, reads: false, writes: true);

    /// <summary>The pass does something the graph cannot see (writes to memory it does not track, to the screen), so it is never culled.</summary>
    public void NoCull() => _pass.SideEffects = true;

    /// <summary>The commands of the pass. For a graphics pass with attachments, rendering is already begun and ends after the callback.</summary>
    public void Execute(Action<PassContext> execute) => _pass.Execute = execute;

    private ImageHandle UseImage(ImageHandle handle, ImageState state, bool reads, bool writes)
    {
        ResourceNode node = _graph.CheckImage(handle);
        Merge(_pass.ImageUses, node, (int)state, reads, writes);
        return handle;
    }

    private BufferHandle UseBuffer(BufferHandle handle, BufferState state, bool reads, bool writes)
    {
        ResourceNode node = _graph.CheckBuffer(handle);
        Merge(_pass.BufferUses, node, (int)state, reads, writes);
        return handle;
    }

    private void Merge(System.Collections.Generic.List<ResourceUse> uses, ResourceNode node, int state, bool reads, bool writes)
    {
        int existing = uses.FindIndex(u => u.Resource == node.Index);
        if (existing < 0)
        {
            uses.Add(new ResourceUse(node.Index, state, reads, writes));
            return;
        }

        if (uses[existing].State != state)
        {
            throw new RenderGraphException($"Pass '{_pass.Name}' uses '{node.Name}' in two different ways at once ({StateName(node, uses[existing].State)} and {StateName(node, state)}).");
        }

        uses[existing] = uses[existing] with { Reads = uses[existing].Reads || reads, Writes = uses[existing].Writes || writes };
    }

    internal static string StateName(ResourceNode node, int state) => node.Kind == ResourceKind.Image ? ((ImageState)state).ToString() : ((BufferState)state).ToString();
}
