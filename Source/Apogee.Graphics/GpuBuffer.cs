using System;

namespace Apogee.Graphics;

/// <summary>Where a buffer's memory lives.</summary>
public enum MemoryKind
{
    /// <summary>On the GPU, fastest to use; filled through <see cref="GraphicsDevice.UploadBuffer"/> or a copy.</summary>
    DeviceLocal,
    /// <summary>Host memory the CPU writes sequentially and the GPU reads (per-frame data, staging); mapped for its whole life.</summary>
    Upload,
    /// <summary>Host memory the GPU writes and the CPU reads back; mapped for its whole life.</summary>
    Readback,
}

/// <summary>A buffer in GPU memory. Every buffer has a device address, so shaders reach it through a pointer in push constants.</summary>
public abstract unsafe class GpuBuffer : IDisposable
{
    protected GpuBuffer(ulong size, BufferUsage usage, MemoryKind kind)
    {
        Size = size;
        Kind = kind;
        Usage = usage | BufferUsage.TransferSource | BufferUsage.TransferDestination;
    }

    public ulong Size { get; }

    public MemoryKind Kind { get; }

    public BufferUsage Usage { get; }

    /// <summary>The GPU address of the first byte: what a shader's pointer holds.</summary>
    public abstract ulong Address { get; }

    /// <summary>The CPU's view of the memory for <see cref="MemoryKind.Upload"/> and <see cref="MemoryKind.Readback"/> buffers, null for device-local ones.</summary>
    public byte* Mapped { get; protected init; }

    public Span<byte> AsSpan() => Mapped is null
        ? throw new InvalidOperationException("Only upload and readback buffers are mapped.")
        : new Span<byte>(Mapped, checked((int)Size));

    /// <summary>Copies <paramref name="data"/> into a mapped buffer at <paramref name="offset"/> and makes the write visible to the GPU.</summary>
    public void Write(ReadOnlySpan<byte> data, ulong offset = 0)
    {
        if (Mapped is null)
        {
            throw new InvalidOperationException("Only upload and readback buffers are mapped; use GraphicsDevice.UploadBuffer for device-local ones.");
        }

        if (offset + (ulong)data.Length > Size)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "The data does not fit in the buffer.");
        }

        data.CopyTo(new Span<byte>(Mapped + offset, data.Length));
        Flush(offset, (ulong)data.Length);
    }

    /// <summary>Makes the GPU's writes visible to the CPU and returns a copy of a range of a mapped buffer.</summary>
    public byte[] Read(ulong offset, int length)
    {
        if (Mapped is null)
        {
            throw new InvalidOperationException("Only upload and readback buffers are mapped; use GraphicsDevice.ReadBuffer for device-local ones.");
        }

        Invalidate(offset, (ulong)length);
        return new ReadOnlySpan<byte>(Mapped + offset, length).ToArray();
    }

    /// <summary>Makes CPU writes to a range of the mapped memory visible to the GPU (a no-op for coherent memory).</summary>
    protected abstract void Flush(ulong offset, ulong length);

    /// <summary>Makes GPU writes to a range of the mapped memory visible to the CPU (a no-op for coherent memory).</summary>
    protected abstract void Invalidate(ulong offset, ulong length);

    public abstract void Dispose();
}
