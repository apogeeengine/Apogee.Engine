using System;
using Apogee.Core.Graphics;

namespace Apogee.Graphics.Metal;

/// <summary>
/// The bindless textures and samplers as one Metal argument buffer, buffer 1 of every shader that reads them (see <c>ParameterBlock&lt;BindlessResources&gt;</c> in common.slang). In an argument buffer of tier 2 each
/// resource of an array takes one 8-byte slot, in order: the 16 384 textures, then the 256 samplers; a slot holds the resource's GPU resource id. The textures and samplers are made resident through the
/// device's residency set. There are no storage images: no engine shader uses them.
/// </summary>
public sealed unsafe class MetalBindlessHeap : BindlessHeap
{
    private const int SlotBytes = 8;

    private readonly MetalDevice _device;
    private readonly object _lock = new();
    private readonly SlotAllocator _sampledImages = new(MaxSampledImages);
    private readonly SlotAllocator _samplers = new(MaxSamplers);
    private readonly MetalBuffer _arguments;
    private readonly byte* _memory;
    private MetalImage? _whiteTexel;
    private MetalSampler? _defaultSampler;

    internal MetalBindlessHeap(MetalDevice device)
    {
        _device = device;
        _arguments = new MetalBuffer(device, (MaxSampledImages + MaxSamplers) * SlotBytes, BufferUsage.Storage, MemoryKind.Upload, "Bindless argument buffer");
        _memory = _arguments.Mapped;
    }

    /// <summary>The argument buffer to bind at index 1.</summary>
    public MetalBuffer Arguments => _arguments;

    /// <summary>Fills slot 0 of the images and samplers. Called by the device once uploads work.</summary>
    internal void CreateDefaults()
    {
        _whiteTexel = (MetalImage)_device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 1, 1, ImageUsage.Sampled), "White texel");
        _device.UploadImage(_whiteTexel, [0xFF, 0xFF, 0xFF, 0xFF], [new ImageRegion(0, 0, 0, 1, 1)], ImageState.ShaderRead);
        _defaultSampler = (MetalSampler)_device.CreateSampler(SamplerDescription.LinearRepeat, "Default sampler");
        WriteImage(_sampledImages.Reserve(0), _whiteTexel);
        WriteSampler(_samplers.Reserve(0), _defaultSampler);
    }

    public override uint AddSampledImage(ImageView view)
    {
        lock (_lock)
        {
            uint index = _sampledImages.Allocate();
            WriteImage(index, (MetalImageView)view);
            return index;
        }
    }

    public override void RemoveSampledImage(uint index)
    {
        lock (_lock)
        {
            _sampledImages.Free(index);
        }
    }

    public override uint AddSampler(GpuSampler sampler)
    {
        lock (_lock)
        {
            uint index = _samplers.Allocate();
            WriteSampler(index, (MetalSampler)sampler);
            return index;
        }
    }

    public override void RemoveSampler(uint index)
    {
        lock (_lock)
        {
            _samplers.Free(index);
        }
    }

    public override uint AddStorageImage(ImageView view) => throw new NotSupportedException("The Metal backend has no storage images in its bindless heap yet: no engine shader uses them.");

    public override void RemoveStorageImage(uint index) => throw new NotSupportedException("The Metal backend has no storage images in its bindless heap yet.");

    public override int SampledImageCount { get { lock (_lock) { return _sampledImages.Used; } } }

    private void WriteImage(uint index, MetalImage image) => *(ulong*)(_memory + (index * SlotBytes)) = image.ResourceId;

    private void WriteImage(uint index, MetalImageView view) => *(ulong*)(_memory + (index * SlotBytes)) = Apogee.Native.Metal.Mtl.TextureResourceId(view.Handle);

    private void WriteSampler(uint index, MetalSampler sampler) => *(ulong*)(_memory + ((MaxSampledImages + index) * SlotBytes)) = sampler.ResourceId;

    public override void Dispose()
    {
        _whiteTexel?.Dispose();
        _defaultSampler?.Dispose();
        _arguments.Dispose();
    }
}
