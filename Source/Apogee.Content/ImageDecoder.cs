using Apogee.Core.Graphics;
using System;
using System.Collections.Generic;
using Apogee.Interop;
using Apogee.Native.Stb;

namespace Apogee.Content;

/// <summary>Decodes PNG, JPEG, BMP, TGA and the other formats stb_image reads into RGBA8 textures with a full mip chain.</summary>
public static unsafe class ImageDecoder
{
    private static readonly float[] s_srgbToLinear = BuildSrgbToLinear();

    /// <summary>Decodes an image file to RGBA8. <paramref name="srgb"/> says what the colors mean: sRGB for base color and emissive textures, linear for the rest.</summary>
    public static TextureData Decode(string name, ReadOnlySpan<byte> file, bool srgb, SamplerData sampler, bool generateMips = true)
    {
        int width;
        int height;
        int channels;
        byte* pixels;
        fixed (byte* data = file)
        {
            pixels = Stbi.LoadFromMemory(data, file.Length, &width, &height, &channels, Stbi.RgbAlpha);
        }

        if (pixels is null)
        {
            throw new ContentException($"Texture '{name}' could not be decoded: {Utf8.FromPointer(Stbi.FailureReason())}");
        }

        try
        {
            byte[] level0 = new ReadOnlySpan<byte>(pixels, checked(width * height * 4)).ToArray();
            IReadOnlyList<byte[]> mips = generateMips ? BuildMipChain(level0, width, height, srgb) : [level0];
            return new TextureData(name, srgb ? PixelFormat.R8G8B8A8Srgb : PixelFormat.R8G8B8A8Unorm, width, height, mips, srgb, sampler);
        }
        finally
        {
            Stbi.ImageFree(pixels);
        }
    }

    /// <summary>Halves the image down to 1x1 with a box filter. sRGB images are averaged in linear light, which is what the sampler does when it filters.</summary>
    public static IReadOnlyList<byte[]> BuildMipChain(byte[] level0, int width, int height, bool srgb)
    {
        var mips = new List<byte[]> { level0 };
        byte[] source = level0;
        int sourceWidth = width;
        int sourceHeight = height;
        while (sourceWidth > 1 || sourceHeight > 1)
        {
            int destinationWidth = Math.Max(1, sourceWidth / 2);
            int destinationHeight = Math.Max(1, sourceHeight / 2);
            byte[] destination = new byte[destinationWidth * destinationHeight * 4];
            for (int y = 0; y < destinationHeight; y++)
            {
                for (int x = 0; x < destinationWidth; x++)
                {
                    int x0 = Math.Min(x * 2, sourceWidth - 1);
                    int x1 = Math.Min(x * 2 + 1, sourceWidth - 1);
                    int y0 = Math.Min(y * 2, sourceHeight - 1);
                    int y1 = Math.Min(y * 2 + 1, sourceHeight - 1);
                    for (int c = 0; c < 4; c++)
                    {
                        // Alpha is never gamma encoded.
                        bool encoded = srgb && c < 3;
                        float sum = Sample(source, sourceWidth, x0, y0, c, encoded) + Sample(source, sourceWidth, x1, y0, c, encoded)
                            + Sample(source, sourceWidth, x0, y1, c, encoded) + Sample(source, sourceWidth, x1, y1, c, encoded);
                        destination[(y * destinationWidth + x) * 4 + c] = Store(sum * 0.25f, encoded);
                    }
                }
            }

            mips.Add(destination);
            source = destination;
            sourceWidth = destinationWidth;
            sourceHeight = destinationHeight;
        }

        return mips;
    }

    private static float Sample(byte[] pixels, int width, int x, int y, int channel, bool encoded)
    {
        byte value = pixels[(y * width + x) * 4 + channel];
        return encoded ? s_srgbToLinear[value] : value / 255f;
    }

    private static byte Store(float value, bool encoded)
    {
        if (encoded)
        {
            value = value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
        }

        return (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    }

    private static float[] BuildSrgbToLinear()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            table[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        return table;
    }
}
