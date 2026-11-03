using Apogee.Core.Graphics;
using System;
using System.Linq;
using Apogee.Native.Stb;
using Apogee.Native.Tests;
using Xunit;

namespace Apogee.Content.Tests;

public class ImageTests
{
    public ImageTests()
    {
        NativeRequirements.Require(typeof(Stbi).Assembly, "stb_image");
    }

    [Fact]
    public void ThePngWriterProducesFilesStbReadsBack()
    {
        byte[] rgba = [.. Enumerable.Range(0, 5 * 3 * 4).Select(i => (byte)(i * 7 + 3))];

        TextureData decoded = ImageDecoder.Decode("round trip", Png.Encode(5, 3, rgba), srgb: false, SamplerData.Default, generateMips: false);

        Assert.Equal((5, 3), (decoded.Width, decoded.Height));
        Assert.Equal(rgba, decoded.Mips[0]);
        Assert.Equal(PixelFormat.R8G8B8A8Unorm, decoded.Format);
        Assert.Throws<ArgumentException>(() => Png.Encode(2, 2, new byte[15]));
    }

    [Fact]
    public void AMipChainGoesDownToOneTexel()
    {
        TextureData texture = ImageDecoder.Decode("mips", Png.Encode(8, 4, new byte[8 * 4 * 4]), srgb: false, SamplerData.Default);

        Assert.Equal(4, texture.Mips.Count);                        // 8x4, 4x2, 2x1, 1x1
        Assert.Equal([8 * 4 * 4, 4 * 2 * 4, 2 * 1 * 4, 1 * 1 * 4], texture.Mips.Select(m => m.Length));
    }

    [Fact]
    public void LinearImagesAverageTheirValuesAndSrgbImagesAverageLight()
    {
        // A black and a white texel side by side, twice.
        byte[] pixels = [0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255];
        byte[] png = Png.Encode(2, 2, pixels);

        TextureData linear = ImageDecoder.Decode("linear", png, srgb: false, SamplerData.Default);
        TextureData srgb = ImageDecoder.Decode("srgb", png, srgb: true, SamplerData.Default);

        Assert.Equal(128, linear.Mips[1][0]);                       // (0 + 255) / 2
        Assert.Equal(188, srgb.Mips[1][0]);                         // half the light, encoded: 0.5 linear is 188 in sRGB
        Assert.Equal(255, srgb.Mips[1][3]);                         // alpha is not gamma encoded
        Assert.Equal(PixelFormat.R8G8B8A8Srgb, srgb.Format);
        Assert.True(srgb.Srgb);
    }

    [Fact]
    public void GarbageIsAContentException()
    {
        var error = Assert.Throws<ContentException>(() => ImageDecoder.Decode("junk.png", "not an image"u8, srgb: true, SamplerData.Default));

        Assert.Contains("junk.png", error.Message);
    }

    [Fact]
    public void VertexIsFortyEightBytesLikeTheShaderStruct()
    {
        Assert.Equal(48, System.Runtime.CompilerServices.Unsafe.SizeOf<Vertex>());
    }
}
