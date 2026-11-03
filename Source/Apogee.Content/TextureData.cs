using Apogee.Core.Graphics;
using System.Collections.Generic;

namespace Apogee.Content;

public enum TextureFilter
{
    Nearest,
    Linear,
}

public enum TextureWrap
{
    Repeat,
    MirroredRepeat,
    ClampToEdge,
}

/// <summary>How a texture is sampled: glTF's sampler, reduced to what the renderer has.</summary>
public readonly record struct SamplerData(TextureFilter Filter, bool Mipmaps, TextureWrap WrapU, TextureWrap WrapV)
{
    public static SamplerData Default { get; } = new(TextureFilter.Linear, true, TextureWrap.Repeat, TextureWrap.Repeat);
}

/// <summary>
/// A decoded texture ready to upload: the GPU format, the size, and the bytes of every mip level (largest first, each tightly packed).
/// </summary>
public sealed class TextureData(string name, PixelFormat format, int width, int height, IReadOnlyList<byte[]> mips, bool srgb, SamplerData sampler)
{
    public string Name { get; } = name;

    public PixelFormat Format { get; } = format;

    public int Width { get; } = width;

    public int Height { get; } = height;

    /// <summary>The bytes of each mip level; level 0 is Width x Height.</summary>
    public IReadOnlyList<byte[]> Mips { get; } = mips;

    /// <summary>The colors are sRGB encoded (the format is an sRGB one and the GPU decodes on sampling).</summary>
    public bool Srgb { get; } = srgb;

    public SamplerData Sampler { get; } = sampler;
}

/// <summary>Which compressed formats the GPU can sample, so Basis textures are transcoded to one it has.</summary>
public readonly record struct TextureCaps(bool Bc, bool Astc)
{
    /// <summary>Nothing compressed: Basis textures become RGBA8.</summary>
    public static TextureCaps None => default;
}
