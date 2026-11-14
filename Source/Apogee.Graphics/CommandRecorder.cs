using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Apogee.Graphics;

/// <summary>One image transition: from the state it was last used in to the state the next use needs.</summary>
public readonly record struct ImageBarrier(
    GpuImage Image,
    ImageState Before,
    ImageState After,
    uint BaseMip = 0,
    uint MipCount = uint.MaxValue,
    uint BaseLayer = 0,
    uint LayerCount = uint.MaxValue)
{
    public static ImageBarrier For(GpuImage image, ImageState before, ImageState after, uint baseMip = 0, uint mipCount = uint.MaxValue, uint baseLayer = 0, uint layerCount = uint.MaxValue) =>
        new(image, before, after, baseMip, mipCount, baseLayer, layerCount);
}

public readonly record struct BufferBarrier(GpuBuffer Buffer, BufferState Before, BufferState After);

/// <summary>Where in a staging buffer the bytes of one image subresource are.</summary>
public readonly record struct ImageRegion(ulong BufferOffset, uint MipLevel, uint ArrayLayer, uint Width, uint Height, uint Depth = 1);

/// <summary>The arguments of one indirect dispatch, as <see cref="CommandRecorder.DispatchIndirect"/> reads them from a buffer (a compute shader can write them). Twelve bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct DispatchIndirectCommand(uint GroupsX, uint GroupsY, uint GroupsZ)
{
    /// <summary>The size of one command in a buffer.</summary>
    public const uint Size = 12;
}

/// <summary>The arguments of one indexed indirect draw, as <see cref="CommandRecorder.DrawIndexedIndirect"/> reads them from a buffer (a compute shader can write them). Twenty bytes, the same layout in Vulkan and Metal.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct DrawIndexedIndirectCommand(uint IndexCount, uint InstanceCount, uint FirstIndex, int VertexOffset, uint FirstInstance)
{
    /// <summary>The size of one command in a buffer, and the default stride between commands.</summary>
    public const uint Size = 20;
}

/// <summary>A color attachment to render into: its view (the image is in <see cref="ImageState.ColorAttachment"/>), cleared or loaded, and kept or discarded.</summary>
public readonly record struct ColorTarget(ImageView View, bool Clear = false, Vector4 ClearColor = default, bool Store = true);

/// <summary>The depth attachment. The engine uses reverse-Z, so the usual clear value is 0 (the far plane).</summary>
public readonly record struct DepthTarget(ImageView View, bool Clear = false, float ClearDepth = 0f, bool Store = true, bool ReadOnly = false);

/// <summary>A command buffer being recorded, with the operations the engine needs on it. Everything here is on the one queue family the device uses.</summary>
public abstract unsafe class CommandRecorder
{
    protected CommandRecorder(GraphicsDevice device) => Device = device;

    public GraphicsDevice Device { get; }

    /// <summary>Records the barriers in one call; a state that only reads after a read needs none and is skipped by the caller (the render graph).</summary>
    public abstract void Barriers(ReadOnlySpan<ImageBarrier> images, ReadOnlySpan<BufferBarrier> buffers = default);

    public void Barrier(in ImageBarrier barrier) => Barriers([barrier]);

    public abstract void CopyBuffer(GpuBuffer source, GpuBuffer destination, ulong size, ulong sourceOffset = 0, ulong destinationOffset = 0);

    /// <summary>Copies tightly packed texels from a buffer into subresources of an image that is in <see cref="ImageState.TransferDestination"/>.</summary>
    public abstract void CopyBufferToImage(GpuBuffer source, GpuImage destination, ReadOnlySpan<ImageRegion> regions);

    /// <summary>Copies subresources of an image in <see cref="ImageState.TransferSource"/> into a buffer, tightly packed.</summary>
    public abstract void CopyImageToBuffer(GpuImage source, GpuBuffer destination, ReadOnlySpan<ImageRegion> regions);

    /// <summary>For <see cref="FillBuffer"/>: from the offset to the end of the buffer.</summary>
    public const ulong WholeSize = ulong.MaxValue;

    /// <summary>
    /// Fills a range of a buffer that is in <see cref="BufferState.TransferDestination"/> with a 32-bit pattern (zero by default: a counter or a histogram that is about to be written). The offset and the size are multiples of
    /// four; <see cref="WholeSize"/> means the rest of the buffer. Metal fills bytes, so there the four bytes of <paramref name="value"/> must be equal (0, 0xFFFFFFFF, 0x01010101, ...).
    /// </summary>
    public abstract void FillBuffer(GpuBuffer buffer, uint value = 0, ulong offset = 0, ulong size = WholeSize);

    /// <summary>
    /// Sets mips and layers of a color image to one color (for float, normalized and sRGB formats); the image must be in <see cref="ImageState.TransferDestination"/> and not inside a rendering pass. A count of
    /// <see cref="uint.MaxValue"/> means every remaining mip or layer. Metal has no such command and clears with a render pass, so there the image needs <see cref="ImageUsage.ColorAttachment"/>.
    /// </summary>
    public abstract void ClearImage(GpuImage image, Vector4 color, uint baseMip = 0, uint mipCount = uint.MaxValue, uint baseLayer = 0, uint layerCount = uint.MaxValue);

    /// <summary>
    /// Sets mips and layers of a depth image to one value (the stencil, if there is one, to zero); the image must be in <see cref="ImageState.TransferDestination"/> and not inside a rendering pass. Counts as in
    /// <see cref="ClearImage"/>. Metal needs <see cref="ImageUsage.DepthStencilAttachment"/> on the image.
    /// </summary>
    public abstract void ClearDepthImage(GpuImage image, float depth = 0f, uint baseMip = 0, uint mipCount = uint.MaxValue, uint baseLayer = 0, uint layerCount = uint.MaxValue);

    /// <summary>Binds the pipeline, and the bindless heap with it.</summary>
    public abstract void BindPipeline(Pipeline pipeline);

    /// <summary>Sets the push constants (at most <see cref="BindlessHeap.PushConstantBytes"/> bytes) every stage can read: texture indices, buffer addresses, small parameters.</summary>
    public void PushConstants<T>(in T data)
        where T : unmanaged
    {
        if (sizeof(T) > BindlessHeap.PushConstantBytes)
        {
            throw new ArgumentException($"{typeof(T).Name} is {sizeof(T)} bytes; push constants hold {BindlessHeap.PushConstantBytes}.");
        }

        fixed (T* pointer = &data)
        {
            PushBytes(new ReadOnlySpan<byte>(pointer, sizeof(T)));
        }
    }

    protected abstract void PushBytes(ReadOnlySpan<byte> data);

    public abstract void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1);

    /// <summary>
    /// Dispatches with the group counts in a buffer (a <see cref="DispatchIndirectCommand"/> at <paramref name="offset"/>, four-byte aligned) that is in <see cref="BufferState.IndirectRead"/>: the output of a culling or
    /// compaction pass that decided how much work there is.
    /// </summary>
    public abstract void DispatchIndirect(GpuBuffer arguments, ulong offset = 0);

    /// <summary>Starts a dynamic-rendering pass over the whole of the given size. The viewport and scissor are set to it too.</summary>
    public abstract void BeginRendering(uint width, uint height, ReadOnlySpan<ColorTarget> colors, DepthTarget? depth = null);

    public abstract void EndRendering();

    /// <summary>Sets viewport and scissor to the whole target. The viewport is not flipped: clip-space +Y is down in the image.</summary>
    public abstract void SetViewport(uint width, uint height);

    /// <summary>Restricts drawing to a rectangle of the target, in pixels (the viewport is left as it is).</summary>
    public abstract void SetScissor(int x, int y, uint width, uint height);

    public abstract void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0);

    public abstract void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0);

    /// <summary>
    /// Draws with the arguments in a buffer in <see cref="BufferState.IndirectRead"/>: <paramref name="drawCount"/> <see cref="DrawIndexedIndirectCommand"/>s, <paramref name="stride"/> bytes apart, from
    /// <paramref name="offset"/>. Commands with an instance count of 0 draw nothing, which is how a culling pass removes a draw. The first instance of a command is honoured.
    /// </summary>
    public abstract void DrawIndexedIndirect(GpuBuffer commands, ulong offset, uint drawCount, uint stride = DrawIndexedIndirectCommand.Size);

    /// <summary>
    /// Like <see cref="DrawIndexedIndirect"/>, with the number of draws in a buffer too (a <c>uint</c> at <paramref name="countOffset"/>, written by the pass that filled the commands), capped at
    /// <paramref name="maxDrawCount"/>. Needs <see cref="GraphicsDevice.SupportsDrawIndirectCount"/>; without it, call <see cref="DrawIndexedIndirect"/> with the maximum and zero the instance count of the unused commands.
    /// </summary>
    public abstract void DrawIndexedIndirectCount(GpuBuffer commands, ulong offset, GpuBuffer count, ulong countOffset, uint maxDrawCount, uint stride = DrawIndexedIndirectCommand.Size);

    public abstract void BindIndexBuffer(GpuBuffer buffer, IndexFormat format = IndexFormat.Index32, ulong offset = 0);

    /// <summary>Opens a named, coloured group in RenderDoc and the validation layer's messages. Dispose the result to close it.</summary>
    public DebugLabelScope Label(string name, float r = 0.5f, float g = 0.5f, float b = 0.5f)
    {
        BeginLabel(name, r, g, b);
        return new DebugLabelScope(this);
    }

    protected abstract void BeginLabel(string name, float r, float g, float b);

    internal abstract void EndLabel();
}

/// <summary>Closes a debug label group when disposed.</summary>
public readonly struct DebugLabelScope(CommandRecorder recorder) : IDisposable
{
    public void Dispose() => recorder.EndLabel();
}
