using System;

namespace Apogee.Graphics;

/// <summary>
/// The one set of resources every pipeline sees: arrays of sampled images, samplers and storage images that shaders index with integers, plus the push
/// constants. Slot 0 of the images and samplers is a white texel and a linear repeat sampler, so an unset index still reads something sane.
/// </summary>
public abstract class BindlessHeap : IDisposable
{
    public const uint MaxSampledImages = 16384;
    public const uint MaxSamplers = 256;
    public const uint MaxStorageImages = 256;

    /// <summary>The size of the push constants of every pipeline.</summary>
    public const uint PushConstantBytes = 128;

    /// <summary>The index to read <paramref name="image"/> by in a shader.</summary>
    public uint AddSampledImage(GpuImage image) => AddSampledImage(image.View);

    public abstract uint AddSampledImage(ImageView view);

    public abstract void RemoveSampledImage(uint index);

    public abstract uint AddSampler(GpuSampler sampler);

    public abstract void RemoveSampler(uint index);

    public abstract uint AddStorageImage(ImageView view);

    public abstract void RemoveStorageImage(uint index);

    public abstract int SampledImageCount { get; }

    public abstract void Dispose();
}
