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

    public override void FillBuffer(GpuBuffer buffer, uint value = 0, ulong offset = 0, ulong size = WholeSize)
    {
        if ((offset & 3) != 0 || (size != WholeSize && (size & 3) != 0))
        {
            throw new ArgumentException("The offset and size of a buffer fill are multiples of four.");
        }

        // Metal's blit fill writes one byte over and over, so only a pattern of four equal bytes can be made.
        uint low = value & 0xFF;
        if (value != low * 0x01010101u)
        {
            throw new NotSupportedException($"Metal fills buffers with one byte: 0x{value:X8} has different bytes. Use 0, 0xFFFFFFFF or another pattern of four equal bytes.");
        }

        ulong length = size == WholeSize ? buffer.Size - offset : size;
        Mtl.BlitEncoderFillBuffer(Blit(), ((MetalBuffer)buffer).Handle, offset, length, (int)low);
    }

    public override void ClearImage(GpuImage image, System.Numerics.Vector4 color, uint baseMip = 0, uint mipCount = uint.MaxValue, uint baseLayer = 0, uint layerCount = uint.MaxValue)
    {
        var metal = (MetalImage)image;
        if ((metal.Description.Usage & ImageUsage.ColorAttachment) == 0)
        {
            throw new InvalidOperationException("Metal clears an image with a render pass: create it with ImageUsage.ColorAttachment to clear it.");
        }

        ClearSubresources(metal, baseMip, mipCount, baseLayer, layerCount, color, 0f, depth: false);
    }

    public override void ClearDepthImage(GpuImage image, float depth = 0f, uint baseMip = 0, uint mipCount = uint.MaxValue, uint baseLayer = 0, uint layerCount = uint.MaxValue)
    {
        var metal = (MetalImage)image;
        if ((metal.Description.Usage & ImageUsage.DepthStencilAttachment) == 0)
        {
            throw new InvalidOperationException("Metal clears an image with a render pass: create it with ImageUsage.DepthStencilAttachment to clear it.");
        }

        ClearSubresources(metal, baseMip, mipCount, baseLayer, layerCount, default, depth, depth: true);
    }

    /// <summary>Clears a one-level, one-slice view of each subresource asked for with a render pass of its own, and gives the view back.</summary>
    private void ClearSubresources(MetalImage image, uint baseMip, uint mipCount, uint baseLayer, uint layerCount, System.Numerics.Vector4 color, float clearDepth, bool depth)
    {
        if (image.Description.Depth > 1)
        {
            throw new NotSupportedException("The Metal backend does not clear 3D textures.");
        }

        uint mips = mipCount == uint.MaxValue ? image.MipLevels - baseMip : mipCount;
        uint layers = layerCount == uint.MaxValue ? image.Layers - baseLayer : layerCount;
        for (uint mip = baseMip; mip < baseMip + mips; mip++)
        {
            for (uint layer = baseLayer; layer < baseLayer + layers; layer++)
            {
                Apogee.Native.Metal.Texture* view = Mtl.TextureView(image.Handle, 0, Mtl.Texture2d, mip, 1, layer, 1);
                if (view is null)
                {
                    throw new MetalException("The Metal texture could not make a view to clear.");
                }

                try
                {
                    if (depth)
                    {
                        DepthAttachment attachment = new() { Texture = view, Clear = 1, Store = 1, ClearDepth = clearDepth };
                        BeginClearPass(null, 0, &attachment);
                    }
                    else
                    {
                        ColorAttachment attachment = new() { Texture = view, Clear = 1, Store = 1 };
                        attachment.ClearColor[0] = color.X;
                        attachment.ClearColor[1] = color.Y;
                        attachment.ClearColor[2] = color.Z;
                        attachment.ClearColor[3] = color.W;
                        BeginClearPass(&attachment, 1, null);
                    }
                }
                finally
                {
                    Mtl.Release(view);
                }
            }
        }
    }

    /// <summary>A render pass that only clears its attachment, and ends at once.</summary>
    private void BeginClearPass(ColorAttachment* color, int colorCount, DepthAttachment* depth)
    {
        End();
        _render = Mtl.RenderEncoderBegin(_commandBuffer, color, colorCount, depth);
        if (_chained)
        {
            Mtl.RenderEncoderWaitFence(_render, _fence);
        }

        _open = Open.Render;
        End();
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

    public override void DispatchIndirect(GpuBuffer arguments, ulong offset = 0)
    {
        ThreadGroupSize group = _computePipeline?.Group ?? throw new InvalidOperationException("Bind a compute pipeline before dispatching.");
        Mtl.ComputeEncoderDispatchIndirect(Compute(), ((MetalBuffer)arguments).Handle, offset, group.X, group.Y, group.Z);
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

    public override void DrawIndexedIndirect(GpuBuffer commands, ulong offset, uint drawCount, uint stride = DrawIndexedIndirectCommand.Size)
    {
        MetalBuffer indices = _indexBuffer ?? throw new InvalidOperationException("Bind an index buffer before DrawIndexedIndirect.");
        RenderEncoder* render = Render();
        MtlBuffer* arguments = ((MetalBuffer)commands).Handle;

        // Metal's indirect draw makes one draw per call (the commands have the layout Vulkan's do); a multi-draw is a loop.
        for (uint i = 0; i < drawCount; i++)
        {
            Mtl.RenderEncoderDrawIndexedIndirect(render, _primitive, _indexFormat == IndexFormat.Index16 ? 0 : 1, indices.Handle, _indexOffset, arguments, offset + (i * stride));
        }
    }

    public override void DrawIndexedIndirectCount(GpuBuffer commands, ulong offset, GpuBuffer count, ulong countOffset, uint maxDrawCount, uint stride = DrawIndexedIndirectCommand.Size) =>
        throw new NotSupportedException("Metal cannot take the number of draws from a buffer (GraphicsDevice.SupportsDrawIndirectCount is false): call DrawIndexedIndirect with the maximum count, with the instance count of unused commands set to 0.");

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
