using Apogee.Core.Graphics;
using System;
using System.IO;
using Apogee.Native.Ktx;
using Apogee.Native.Tests;
using Xunit;
using KtxApi = Apogee.Native.Ktx.Ktx;

namespace Apogee.Content.Tests;

public class Ktx2LoaderTests
{
    public Ktx2LoaderTests()
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ktx2", name));

    [Fact]
    public void TheMagicIdentifiesKtx2()
    {
        Assert.True(Ktx2Loader.IsKtx2(Fixture("cyan_rgb_reference_basis.ktx2")));
        Assert.False(Ktx2Loader.IsKtx2("PNG...."u8));
    }

    [Theory]
    [InlineData(false, false, true, PixelFormat.R8G8B8A8Srgb)]
    [InlineData(false, false, false, PixelFormat.R8G8B8A8Unorm)]
    [InlineData(true, false, true, PixelFormat.Bc7SrgbBlock)]
    [InlineData(true, true, false, PixelFormat.Bc7UnormBlock)]            // BC wins over ASTC when both exist
    [InlineData(false, true, true, PixelFormat.Astc4X4SrgbBlock)]
    public void BasisIsTranscodedToTheBestFormatTheGpuHas(bool bc, bool astc, bool srgb, PixelFormat expected)
    {
        TextureData texture = Ktx2Loader.Load("cyan", Fixture("cyan_rgb_reference_basis.ktx2"), srgb, new TextureCaps(bc, astc), SamplerData.Default);

        Assert.Equal(expected, texture.Format);
        Assert.Equal(srgb, texture.Srgb);
        Assert.NotEmpty(texture.Mips);
        int blocks = ((texture.Width + 3) / 4) * ((texture.Height + 3) / 4);
        Assert.Equal(expected is PixelFormat.R8G8B8A8Srgb or PixelFormat.R8G8B8A8Unorm ? texture.Width * texture.Height * 4 : blocks * 16, texture.Mips[0].Length);
    }

    [Fact]
    public void UastcWorksToo()
    {
        TextureData texture = Ktx2Loader.Load("cyan uastc", Fixture("cyan_rgb_reference_uastc.ktx2"), srgb: true, TextureCaps.None, SamplerData.Default);

        Assert.Equal(PixelFormat.R8G8B8A8Srgb, texture.Format);
        Assert.InRange(texture.Mips[0][0], 0, 48);            // cyan: little red, a lot of green and blue
        Assert.InRange(texture.Mips[0][1], 207, 255);
    }

    [Fact]
    public void EveryMipLevelOfAChainIsReturned()
    {
        TextureData texture = Ktx2Loader.Load("mips", Fixture("rgba-mipmap-reference-basis.ktx2"), srgb: false, TextureCaps.None, SamplerData.Default);

        Assert.True(texture.Mips.Count > 1);
        for (int level = 0; level < texture.Mips.Count; level++)
        {
            int size = Math.Max(1, texture.Width >> level) * Math.Max(1, texture.Height >> level) * 4;
            Assert.Equal(size, texture.Mips[level].Length);
        }
    }

    [Fact]
    public void NotKtxIsAContentException()
    {
        var error = Assert.Throws<ContentException>(() => Ktx2Loader.Load("bad.ktx2", new byte[64], srgb: true, TextureCaps.None, SamplerData.Default));

        Assert.Contains("bad.ktx2", error.Message);
    }
}
