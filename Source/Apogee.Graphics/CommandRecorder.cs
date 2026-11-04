using System;
using System.Numerics;

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

    /// <summary>Starts a dynamic-rendering pass over the whole of the given size. The viewport and scissor are set to it too.</summary>
    public abstract void BeginRendering(uint width, uint height, ReadOnlySpan<ColorTarget> colors, DepthTarget? depth = null);

    public abstract void EndRendering();

    /// <summary>Sets viewport and scissor to the whole target. The viewport is not flipped: clip-space +Y is down in the image.</summary>
    public abstract void SetViewport(uint width, uint height);

    /// <summary>Restricts drawing to a rectangle of the target, in pixels (the viewport is left as it is).</summary>
    public abstract void SetScissor(int x, int y, uint width, uint height);

    public abstract void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0);

    public abstract void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0);

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
