using System;
using System.Collections.Generic;
using Weft.Core.Graphics;

namespace Weft.Graphics.Metal;

/// <summary>
/// The bindless textures, samplers and storage images as one Metal argument buffer, buffer 1 of every shader that reads them (see <c>ParameterBlock&lt;BindlessResources&gt;</c> in common.slang). In an argument buffer of
/// tier 2 each resource of an array takes one 8-byte slot and a struct's members follow one another, so the buffer is the runs of <see cref="Members"/> in the order the shader block declares them: the 16 384 2D textures,
/// the 256 samplers, then the cube, 2D-array, 3D and unsigned-integer texture arrays (16 384 each) and the 256 storage images. A slot holds the resource's GPU resource id. Metal has no aliasing: on Vulkan all the
/// sampled arrays are one descriptor binding, here each is a run of its own, so a sampled texture's id is written into the same index of every sampled run and the shader reads it through the array of its type.
/// The textures and samplers are made resident through the device's residency set.
/// </summary>
public sealed unsafe class MetalBindlessHeap : BindlessHeap
{
    private const int SlotBytes = 8;

    private readonly MetalDevice _device;
    private readonly object _lock = new();
    private readonly SlotAllocator _sampledImages = new(MaxSampledImages);
    private readonly SlotAllocator _samplers = new(MaxSamplers);
    private readonly SlotAllocator _storageImages = new(MaxStorageImages);
    private readonly MetalBuffer _arguments;
    private readonly byte* _memory;
    private static readonly string[] s_members = ["textures", "samplers", "cubes", "arrays", "volumes", "uintTextures", "storage"];
    private static readonly uint[] s_counts = [MaxSampledImages, MaxSamplers, MaxSampledImages, MaxSampledImages, MaxSampledImages, MaxSampledImages, MaxStorageImages];
    private static readonly ulong[] s_offsets = Offsets();
    private static readonly int[] s_sampledRuns = [0, 2, 3, 4, 5];     // the runs a sampled texture's id goes into
    private const int SamplersRun = 1;
    private const int StorageRun = 6;

    private MetalImage? _whiteTexel;
    private MetalSampler? _defaultSampler;

    internal MetalBindlessHeap(MetalDevice device)
    {
        _device = device;
        _arguments = new MetalBuffer(device, ArgumentBufferBytes, BufferUsage.Storage, MemoryKind.Upload, "Bindless argument buffer");
        _memory = _arguments.Mapped;
    }

    /// <summary>The members of <c>BindlessResources</c> in common.slang, in declaration order: the runs of the argument buffer (a test compares them with what Slang writes for Metal).</summary>
    public static IReadOnlyList<string> Members => s_members;

    /// <summary>The number of 8-byte slots in a member's run.</summary>
    public static uint CountOf(string member) => s_counts[Array.IndexOf(s_members, member)];

    /// <summary>Where a member's run starts in the argument buffer, in bytes.</summary>
    public static ulong OffsetOf(string member) => s_offsets[Array.IndexOf(s_members, member)];

    /// <summary>The size of the argument buffer in bytes.</summary>
    public static ulong ArgumentBufferBytes { get; } = s_offsets[^1] + (s_counts[^1] * SlotBytes);

    private static ulong[] Offsets()
    {
        ulong[] offsets = new ulong[s_counts.Length];
        for (int i = 1; i < offsets.Length; i++)
        {
            offsets[i] = offsets[i - 1] + (s_counts[i - 1] * SlotBytes);
        }

        return offsets;
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
        _storageImages.Reserve(0);     // like the other arrays, index 0 means "none" (a storage image has no default to put there)
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

    public override uint AddStorageImage(ImageView view)
    {
        lock (_lock)
        {
            uint index = _storageImages.Allocate();
            WriteSlot(StorageRun, index, Weft.Native.Metal.Mtl.TextureResourceId(((MetalImageView)view).Handle));
            return index;
        }
    }

    public override void RemoveStorageImage(uint index)
    {
        lock (_lock)
        {
            _storageImages.Free(index);
        }
    }

    public override int SampledImageCount { get { lock (_lock) { return _sampledImages.Used; } } }

    private void WriteImage(uint index, MetalImage image) => WriteSampled(index, image.ResourceId);

    private void WriteImage(uint index, MetalImageView view) => WriteSampled(index, Weft.Native.Metal.Mtl.TextureResourceId(view.Handle));

    private void WriteSampled(uint index, ulong id)
    {
        foreach (int run in s_sampledRuns)
        {
            WriteSlot(run, index, id);
        }
    }

    private void WriteSampler(uint index, MetalSampler sampler) => WriteSlot(SamplersRun, index, sampler.ResourceId);

    private void WriteSlot(int run, uint index, ulong id) => *(ulong*)(_memory + s_offsets[run] + (index * SlotBytes)) = id;

    public override void Dispose()
    {
        _whiteTexel?.Dispose();
        _defaultSampler?.Dispose();
        _arguments.Dispose();
    }
}
