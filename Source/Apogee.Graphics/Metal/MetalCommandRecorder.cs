using System;
using Apogee.Core.Graphics;
using Apogee.Native.Metal;
using MtlBuffer = Apogee.Native.Metal.Buffer;
using MtlCommandBuffer = Apogee.Native.Metal.CommandBuffer;
using MtlFence = Apogee.Native.Metal.Fence;

namespace Apogee.Graphics.Metal;

/// <summary>
/// A Metal command buffer being recorded. Metal has encoders where Vulkan has one command buffer: this opens the one the next command needs (render, compute or blit) and ends the one that is open.
/// Metal does not order what a shader reaches through an address or an argument buffer, so the encoders form a chain, each waiting for a fence that the one before it updates: that is what the barriers of the
/// render graph mean here (there are no image layouts to change, so <see cref="Barriers"/> does nothing by itself).
/// </summary>
public sealed unsafe class MetalCommandRecorder : CommandRecorder
{
    private enum Open
    {
        None,
        Compute,
        Blit,
        Render,
    }

    private readonly MetalDevice _device;
    private MtlCommandBuffer* _commandBuffer;
    private MtlFence* _fence;
    private Open _open;
    private bool _chained;
    private ComputeEncoder* _compute;
    private BlitEncoder* _blit;
    private RenderEncoder* _render;
    private MetalPipeline? _computePipeline;
    private int _primitive;
    private MetalBuffer? _indexBuffer;
    private IndexFormat _indexFormat;
    private ulong _indexOffset;

    internal MetalCommandRecorder(MetalDevice device)
        : base(device)
    {
        _device = device;
        _commandBuffer = Mtl.CommandBufferCreate(device.Queue);
        _fence = Mtl.FenceCreate(device.Handle);
        if (_commandBuffer is null || _fence is null)
        {
            throw new MetalException("The Metal queue could not make a command buffer.");
        }
    }

    internal MtlCommandBuffer* CommandBuffer => _commandBuffer;

    // ---- Encoders ----------------------------------------------------------------------------------------------------------------

    private void End()
    {
        switch (_open)
        {
            case Open.Compute:
                Mtl.ComputeEncoderUpdateFence(_compute, _fence);
                Mtl.ComputeEncoderEnd(_compute);
                _compute = null;
                break;
            case Open.Blit:
                Mtl.BlitEncoderUpdateFence(_blit, _fence);
                Mtl.BlitEncoderEnd(_blit);
                _blit = null;
                break;
            case Open.Render:
                Mtl.RenderEncoderUpdateFence(_render, _fence);
                Mtl.RenderEncoderEnd(_render);
                _render = null;
                break;
            default:
                return;
        }

        _open = Open.None;
        _chained = true;
    }

    private ComputeEncoder* Compute()
    {
        if (_open != Open.Compute)
        {
            End();
            _compute = Mtl.ComputeEncoderBegin(_commandBuffer);
            if (_chained)
            {
                Mtl.ComputeEncoderWaitFence(_compute, _fence);
            }

            _open = Open.Compute;
        }

        return _compute;
    }

    private BlitEncoder* Blit()
    {
        if (_open != Open.Blit)
        {
            End();
            _blit = Mtl.BlitEncoderBegin(_commandBuffer);
            if (_chained)
            {
                Mtl.BlitEncoderWaitFence(_blit, _fence);
            }

            _open = Open.Blit;
        }

        return _blit;
    }

    private RenderEncoder* Render() => _open == Open.Render ? _render : throw new InvalidOperationException("This needs a render pass: call BeginRendering first.");

    /// <summary>Ends the open encoder, commits the command buffer and gives the recorder's references back. The buffer keeps running; wait for it with <paramref name="wait"/>.</summary>
    internal void Commit(bool wait)
    {
        End();
        _device.CommitResidency();
        MtlCommandBuffer* commandBuffer = _commandBuffer;
        Mtl.CommandBufferCommit(commandBuffer);
        if (wait)
        {
            Mtl.CommandBufferWait(commandBuffer);
            byte* message = stackalloc byte[512];
            int code = Mtl.CommandBufferError(commandBuffer, message, 512);
            if (code != 0)
            {
                Release();
                throw new MetalException($"A Metal command buffer failed ({code}): {Apogee.Interop.Utf8.FromPointer(message)}");
            }
        }

        Release();
    }

    /// <summary>Signals the event, presents the drawable and commits (a frame's end).</summary>
    internal void CommitFrame(Event* timeline, ulong value, Drawable* drawable)
    {
        End();
        if (drawable is not null)
        {
            Mtl.CommandBufferPresent(_commandBuffer, drawable);
        }

        Mtl.CommandBufferEncodeSignal(_commandBuffer, timeline, value);
        Commit(wait: false);
    }

    private void Release()
    {
        if (_commandBuffer is not null)
        {
            Mtl.Release(_commandBuffer);
            _commandBuffer = null;
        }

        if (_fence is not null)
        {
            Mtl.Release(_fence);
            _fence = null;
        }
    }

    // ---- Barriers and copies -----------------------------------------------------------------------------------------------------

    public override void Barriers(ReadOnlySpan<ImageBarrier> images, ReadOnlySpan<BufferBarrier> buffers = default)
    {
        // Nothing to transition, and the encoders are already chained (see the class comment): a barrier only has to be a boundary, which every change of encoder is.
    }

    public override void CopyBuffer(GpuBuffer source, GpuBuffer destination, ulong size, ulong sourceOffset = 0, ulong destinationOffset = 0) =>
        Mtl.BlitEncoderCopyBuffer(Blit(), ((MetalBuffer)source).Handle, sourceOffset, ((MetalBuffer)destination).Handle, destinationOffset, size);

    public override void CopyBufferToImage(GpuBuffer source, GpuImage destination, ReadOnlySpan<ImageRegion> regions)
    {
        var image = (MetalImage)destination;
        foreach (ImageRegion r in regions)
        {
            (uint bytesPerRow, uint bytesPerImage) = Layout(image.Format, r.Width, r.Height);
            Mtl.BlitEncoderCopyBufferToTexture(Blit(), ((MetalBuffer)source).Handle, r.BufferOffset, bytesPerRow, bytesPerImage, image.Handle, r.MipLevel, r.ArrayLayer, r.Width, r.Height, r.Depth);
        }
    }

    public override void CopyImageToBuffer(GpuImage source, GpuBuffer destination, ReadOnlySpan<ImageRegion> regions)
    {
        var image = (MetalImage)source;
        foreach (ImageRegion r in regions)
        {
            (uint bytesPerRow, uint bytesPerImage) = Layout(image.Format, r.Width, r.Height);
            Mtl.BlitEncoderCopyTextureToBuffer(Blit(), image.Handle, r.MipLevel, r.ArrayLayer, r.Width, r.Height, r.Depth, ((MetalBuffer)destination).Handle, r.BufferOffset, bytesPerRow, bytesPerImage);
        }
    }

    /// <summary>The bytes of a row of blocks and of the whole image, for tightly packed texels.</summary>
    private static (uint BytesPerRow, uint BytesPerImage) Layout(PixelFormat format, uint width, uint height)
    {
        (int bytes, int blockWidth, _) = FormatInfo.Block(format);
        uint bytesPerRow = (((width + (uint)blockWidth) - 1) / (uint)blockWidth) * (uint)bytes;
        return (bytesPerRow, (uint)FormatInfo.LevelSize(format, width, height));
    }

    // ---- Pipelines and state -----------------------------------------------------------------------------------------------------

    public override void BindPipeline(Pipeline pipeline)
    {
        var metal = (MetalPipeline)pipeline;
        MtlBuffer* arguments = _device.MetalBindless.Arguments.Handle;
        if (metal.IsCompute)
        {
            ComputeEncoder* encoder = Compute();
            Mtl.ComputeEncoderSetPipeline(encoder, metal.Compute);
            Mtl.ComputeEncoderSetBuffer(encoder, arguments, 0, 1);
            _computePipeline = metal;
            return;
        }

        RenderEncoder* render = Render();
        Mtl.RenderEncoderSetPipeline(render, metal.Render);
        Mtl.RenderEncoderSetDepthStencil(render, metal.DepthStencil);
        Mtl.RenderEncoderSetRasterizer(render, metal.Cull, metal.FrontCounterClockwise ? 1 : 0);
        Mtl.RenderEncoderSetFragmentBuffer(render, arguments, 0, 1);
        _primitive = metal.Primitive;
    }

    protected override void PushBytes(ReadOnlySpan<byte> data)
    {
        fixed (byte* bytes = data)
        {
            if (_open == Open.Compute)
            {
                Mtl.ComputeEncoderSetBytes(_compute, bytes, (uint)data.Length, 0);
            }
            else
            {
                Mtl.RenderEncoderSetBytes(Render(), bytes, (uint)data.Length, 0);
            }
        }
    }

    public override void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1)
    {
        ThreadGroupSize group = _computePipeline?.Group ?? throw new InvalidOperationException("Bind a compute pipeline before dispatching.");
        Mtl.ComputeEncoderDispatch(Compute(), groupsX, groupsY, groupsZ, group.X, group.Y, group.Z);
    }

    // ---- Rendering ---------------------------------------------------------------------------------------------------------------

    public override void BeginRendering(uint width, uint height, ReadOnlySpan<ColorTarget> colors, DepthTarget? depth = null)
    {
        End();
        ColorAttachment* attachments = stackalloc ColorAttachment[Math.Max(1, colors.Length)];
        for (int i = 0; i < colors.Length; i++)
        {
            ColorTarget c = colors[i];
            attachments[i] = new ColorAttachment { Texture = ((MetalImageView)c.View).Handle, Clear = c.Clear ? 1 : 0, Store = c.Store ? 1 : 0 };
            attachments[i].ClearColor[0] = c.ClearColor.X;
            attachments[i].ClearColor[1] = c.ClearColor.Y;
            attachments[i].ClearColor[2] = c.ClearColor.Z;
            attachments[i].ClearColor[3] = c.ClearColor.W;
        }

        DepthAttachment depthAttachment = default;
        if (depth is { } d)
        {
            depthAttachment = new DepthAttachment { Texture = ((MetalImageView)d.View).Handle, Clear = d.Clear ? 1 : 0, Store = d.Store ? 1 : 0, ClearDepth = d.ClearDepth };
        }

        _render = Mtl.RenderEncoderBegin(_commandBuffer, attachments, colors.Length, depth is null ? null : &depthAttachment);
        if (_chained)
        {
            Mtl.RenderEncoderWaitFence(_render, _fence);
        }

        _open = Open.Render;
        SetViewport(width, height);
    }

    public override void EndRendering() => End();

    public override void SetViewport(uint width, uint height)
    {
        RenderEncoder* render = Render();
        Mtl.RenderEncoderSetViewport(render, 0, 0, width, height, 0, 1);
        Mtl.RenderEncoderSetScissor(render, 0, 0, width, height);
    }

    public override void SetScissor(int x, int y, uint width, uint height) => Mtl.RenderEncoderSetScissor(Render(), (uint)Math.Max(x, 0), (uint)Math.Max(y, 0), width, height);

    public override void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0) =>
        Mtl.RenderEncoderDraw(Render(), _primitive, firstVertex, vertexCount, instanceCount, firstInstance);

    public override void BindIndexBuffer(GpuBuffer buffer, IndexFormat format = IndexFormat.Index32, ulong offset = 0)
    {
        _indexBuffer = (MetalBuffer)buffer;
        _indexFormat = format;
        _indexOffset = offset;
    }

    public override void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        MetalBuffer indices = _indexBuffer ?? throw new InvalidOperationException("Bind an index buffer before DrawIndexed.");
        ulong offset = _indexOffset + (firstIndex * (_indexFormat == IndexFormat.Index16 ? 2UL : 4UL));
        Mtl.RenderEncoderDrawIndexed(Render(), _primitive, indexCount, _indexFormat == IndexFormat.Index16 ? 0 : 1, indices.Handle, offset, instanceCount, vertexOffset, firstInstance);
    }

    // ---- Debug labels ------------------------------------------------------------------------------------------------------------

    protected override void BeginLabel(string name, float r, float g, float b)
    {
        fixed (byte* text = Apogee.Interop.Utf8.ToZeroTerminated(name))
        {
            Mtl.CommandBufferPushDebugGroup(_commandBuffer, text);
        }
    }

    internal override void EndLabel() => Mtl.CommandBufferPopDebugGroup(_commandBuffer);
}
