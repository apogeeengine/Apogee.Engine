using System;
using System.Runtime.InteropServices;
using Apogee.Native.Stb;
using Xunit;

namespace Apogee.Native.Tests;

public unsafe class StbSmokeTests
{
    // A 2x2, 24 bit BMP: red, green on the top row; blue, white below (BMP rows run bottom up, pixels are BGR, rows pad to 4 bytes).
    private static readonly byte[] s_bmp =
    [
        (byte)'B', (byte)'M', 70, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0,
        40, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 1, 0, 24, 0, 0, 0, 0, 0, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        255, 0, 0, /* blue */ 255, 255, 255, /* white */ 0, 0,
        0, 0, 255, /* red */ 0, 255, 0, /* green */ 0, 0,
    ];

    [Fact]
    public void StbDecodesAnImageFromMemory()
    {
        NativeRequirements.Require(typeof(Stbi).Assembly, "stb_image");

        int width;
        int height;
        int channelsInFile;
        byte* pixels;
        fixed (byte* data = s_bmp)
        {
            Assert.Equal(1, Stbi.InfoFromMemory(data, s_bmp.Length, &width, &height, &channelsInFile));
            pixels = Stbi.LoadFromMemory(data, s_bmp.Length, &width, &height, &channelsInFile, Stbi.RgbAlpha);
        }

        Assert.True(pixels != null, Marshal.PtrToStringUTF8((IntPtr)Stbi.FailureReason()));
        try
        {
            Assert.Equal((2, 2), (width, height));
            Assert.Equal(
                [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255],
                new ReadOnlySpan<byte>(pixels, 16).ToArray());
        }
        finally
        {
            Stbi.ImageFree(pixels);
        }
    }

    [Fact]
    public void StbReportsWhyADecodeFailed()
    {
        NativeRequirements.Require(typeof(Stbi).Assembly, "stb_image");

        byte[] garbage = "not an image"u8.ToArray();
        int width;
        int height;
        int channels;
        fixed (byte* data = garbage)
        {
            Assert.True(Stbi.LoadFromMemory(data, garbage.Length, &width, &height, &channels, 0) == null);
        }

        Assert.False(string.IsNullOrEmpty(Marshal.PtrToStringUTF8((IntPtr)Stbi.FailureReason())));
    }
}
