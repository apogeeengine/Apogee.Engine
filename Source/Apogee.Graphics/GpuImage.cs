using System;
using Apogee.Core.Graphics;

namespace Apogee.Graphics;

/// <summary>What an image is: format, size, mip chain, layers and what it will be used for.</summary>
public readonly record struct ImageDescription(
    PixelFormat Format,
    uint Width,
    uint Height,
    ImageUsage Usage,
    uint MipLevels = 1,
    uint ArrayLayers = 1,
    uint Depth = 1,
    bool Cube = false,
    SampleCount Samples = SampleCount.Count1)
{
    /// <summary>The number of mip levels down to 1x1 for this size.</summary>
    public static uint FullMipCount(uint width, uint height) => (uint)System.Numerics.BitOperations.Log2(Math.Max(width, height)) + 1;
}

/// <summary>A view of (part of) an image, as the API of a backend sees it. Opaque: the backend that made it knows what is in it.</summary>
public abstract class ImageView
{
}

/// <summary>An image with a view of the whole of it (the aspect a shader samples).</summary>
public abstract class GpuImage : IDisposable
{
    protected GpuImage(in ImageDescription description) => Description = description;

    public ImageDescription Description { get; }

    public PixelFormat Format => Description.Format;

    public uint Width => Description.Width;

    public uint Height => Description.Height;

    public uint MipLevels => Description.MipLevels;

    /// <summary>The number of array layers (six per cube).</summary>
    public abstract uint Layers { get; }

    /// <summary>A view of every mip and layer, with the aspect a shader samples (depth only for a depth-stencil format).</summary>
    public abstract ImageView View { get; }

    /// <summary>Creates an extra view (one mip for a compute pass, one layer for a render target). The caller destroys it with <see cref="DestroyView"/>.</summary>
    public abstract ImageView CreateView(uint baseMip, uint mipCount, uint baseLayer, uint layerCount);

    public abstract void DestroyView(ImageView view);

    public abstract void Dispose();
}
