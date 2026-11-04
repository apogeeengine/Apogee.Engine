using System;
using Apogee.Native.Metal;
using MtlBuffer = Apogee.Native.Metal.Buffer;

namespace Apogee.Graphics.Metal;

/// <summary>A Metal buffer. Device-local buffers are private (the GPU's), upload and readback buffers are shared and mapped for their whole life.</summary>
public sealed unsafe class MetalBuffer : GpuBuffer
{
    private readonly MetalDevice _device;
    private MtlBuffer* _handle;

    internal MetalBuffer(MetalDevice device, ulong size, BufferUsage usage, MemoryKind kind, string? name)
        : base(size, usage, kind)
    {
        _device = device;
        _handle = Mtl.BufferCreate(device.Handle, Math.Max(size, 1), kind == MemoryKind.DeviceLocal ? Mtl.StoragePrivate : Mtl.StorageShared, kind == MemoryKind.Upload ? 1 : 0);
        if (_handle is null)
        {
            throw new MetalException($"The Metal device could not make a buffer of {size} bytes.");
        }

        Mapped = (byte*)Mtl.BufferContents(_handle);
        Address = Mtl.BufferGpuAddress(_handle);
        device.Track(_handle);
        if (name is not null)
        {
            fixed (byte* label = Apogee.Interop.Utf8.ToZeroTerminated(name))
            {
                Mtl.BufferSetLabel(_handle, label);
            }
        }
    }

    public MtlBuffer* Handle => _handle;

    /// <inheritdoc/>
    public override ulong Address { get; }

    // Shared memory is coherent: the GPU sees the CPU's writes when the command buffer is committed, and the CPU sees the GPU's once it has finished.
    protected override void Flush(ulong offset, ulong length)
    {
    }

    protected override void Invalidate(ulong offset, ulong length)
    {
    }

    public override void Dispose()
    {
        if (_handle is null)
        {
            return;
        }

        _device.Untrack(_handle);
        Mtl.Release(_handle);
        _handle = null;
    }
}
