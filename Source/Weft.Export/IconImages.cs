using System;
using System.Collections.Generic;
using Weft.Content;

namespace Weft.Export;

/// <summary>
/// A game's icon (<c>game.wgam</c>'s <c>Icon</c>: a square PNG) at the sizes the platforms want: Windows icon resources, the macOS <c>icns</c>, the Linux icon. Sizes below the image are made by averaging
/// the pixels each new pixel covers (a box filter, with transparent pixels counting for nothing in the colors); a size above it is made the same way, which only repeats pixels, and is warned about.
/// </summary>
public sealed class IconImages
{
    /// <summary>The width and height an icon should have at least to look right at the largest size the common platforms use for a window or a file list.</summary>
    public const int AdvisedSize = 256;

    private readonly byte[] _rgba;
    private readonly List<string> _warnings = [];
    private readonly HashSet<int> _upscaled = [];

    private IconImages(int size, byte[] rgba)
    {
        Size = size;
        _rgba = rgba;
    }

    /// <summary>The width and height of the image as it was given.</summary>
    public int Size { get; }

    /// <summary>What is not right about the icon: it is smaller than <see cref="AdvisedSize"/>, or a size had to be made bigger than the image. Grows as sizes are asked for.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Decodes a PNG (or any image the importer reads).</summary>
    /// <exception cref="ExportException">The file is not an image, or it is not square.</exception>
    public static IconImages Load(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        TextureData decoded;
        try
        {
            decoded = ImageDecoder.Decode("icon", png, srgb: false, SamplerData.Default, generateMips: false);
        }
        catch (ContentException e)
        {
            throw new ExportException($"The icon is not an image: {e.Message}", e);
        }

        if (decoded.Width != decoded.Height)
        {
            throw new ExportException($"The icon must be square, not {decoded.Width}x{decoded.Height}.");
        }

        var icon = new IconImages(decoded.Width, decoded.Mips[0]);
        if (icon.Size < AdvisedSize)
        {
            icon._warnings.Add($"The icon is {icon.Size}x{icon.Size}; {AdvisedSize}x{AdvisedSize} or more is advised (1024x1024 for macOS).");
        }

        return icon;
    }

    /// <summary>The image at <paramref name="size"/> x <paramref name="size"/> as RGBA8, top row first, not premultiplied.</summary>
    public byte[] Rgba(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        if (size == Size)
        {
            return (byte[])_rgba.Clone();
        }

        if (size > Size && _upscaled.Add(size))
        {
            _warnings.Add($"The icon is {Size}x{Size}, so its {size}x{size} image is scaled up and looks soft.");
        }

        byte[] output = new byte[size * size * 4];
        double scale = (double)Size / size;
        for (int y = 0; y < size; y++)
        {
            double y0 = y * scale;
            double y1 = (y + 1) * scale;
            for (int x = 0; x < size; x++)
            {
                double x0 = x * scale;
                double x1 = (x + 1) * scale;
                double red = 0;
                double green = 0;
                double blue = 0;
                double alpha = 0;
                double total = 0;
                for (int sy = (int)y0; sy < Math.Min(Size, (int)Math.Ceiling(y1)); sy++)
                {
                    double wy = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
                    for (int sx = (int)x0; sx < Math.Min(Size, (int)Math.Ceiling(x1)); sx++)
                    {
                        double weight = wy * (Math.Min(sx + 1, x1) - Math.Max(sx, x0));
                        int at = (sy * Size + sx) * 4;
                        double a = _rgba[at + 3] * weight;
                        red += _rgba[at] * a;
                        green += _rgba[at + 1] * a;
                        blue += _rgba[at + 2] * a;
                        alpha += a;
                        total += weight;
                    }
                }

                int to = (y * size + x) * 4;
                if (alpha > 0)
                {
                    output[to] = (byte)Math.Clamp(Math.Round(red / alpha), 0, 255);
                    output[to + 1] = (byte)Math.Clamp(Math.Round(green / alpha), 0, 255);
                    output[to + 2] = (byte)Math.Clamp(Math.Round(blue / alpha), 0, 255);
                    output[to + 3] = (byte)Math.Clamp(Math.Round(alpha / total), 0, 255);
                }
            }
        }

        return output;
    }

    /// <summary>The image at <paramref name="size"/> x <paramref name="size"/> as a PNG file.</summary>
    public byte[] Png(int size) => Weft.Content.Png.Encode(size, size, Rgba(size));
}
