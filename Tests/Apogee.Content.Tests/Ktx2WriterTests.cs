using Apogee.Core.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Content.Import;
using Xunit;

namespace Apogee.Content.Tests;

public class Ktx2WriterTests
{
    private static TextureData Texture(int width, int height, bool srgb, int seed = 1, bool mips = true)
    {
        var random = new Random(seed);
        byte[] level0 = new byte[width * height * 4];
        random.NextBytes(level0);
        IReadOnlyList<byte[]> chain = mips ? ImageDecoder.BuildMipChain(level0, width, height, srgb) : [level0];
        return new TextureData("test", srgb ? PixelFormat.R8G8B8A8Srgb : PixelFormat.R8G8B8A8Unorm, width, height, chain, srgb, SamplerData.Default);
    }

    [Theory]
    [InlineData(64, 32, true)]
    [InlineData(64, 32, false)]
    [InlineData(5, 3, true)]
    [InlineData(1, 1, false)]
    [InlineData(37, 91, true)]
    public void ATextureWrittenAsKtx2IsReadBackExactly(int width, int height, bool srgb)
    {
        TextureData texture = Texture(width, height, srgb);

        byte[] file = Ktx2Writer.Write(texture);
        TextureData back = Ktx2Loader.Load("back", file, srgb, TextureCaps.None, SamplerData.Default);

        Assert.True(Ktx2Loader.IsKtx2(file));
        Assert.Equal((texture.Width, texture.Height, texture.Format), (back.Width, back.Height, back.Format));
        Assert.Equal(texture.Mips.Count, back.Mips.Count);
        for (int level = 0; level < texture.Mips.Count; level++)
        {
            Assert.Equal(texture.Mips[level], back.Mips[level]);
        }
    }

    [Fact]
    public void ZstdSupercompressionMakesASmallerFileOfTheSameTexture()
    {
        // A picture that compresses (flat areas and a gradient), as most textures do.
        byte[] level0 = new byte[128 * 128 * 4];
        for (int i = 0; i < level0.Length; i += 4)
        {
            level0[i] = (byte)((i / 4 % 128) / 8 * 8);
            level0[i + 1] = (byte)((i / 4 / 128) / 16 * 16);
            level0[i + 3] = 255;
        }

        var texture = new TextureData("flat", PixelFormat.R8G8B8A8Srgb, 128, 128, ImageDecoder.BuildMipChain(level0, 128, 128, true), true, SamplerData.Default);

        byte[] plain = Ktx2Writer.Write(texture);
        byte[] compressed = Ktx2Writer.Write(texture, Ktx2Writer.ImportLevel);
        TextureData back = Ktx2Loader.Load("flat", compressed, true, TextureCaps.None, SamplerData.Default);

        Assert.Equal(2u, BitConverter.ToUInt32(compressed, 44));
        Assert.True(compressed.Length < plain.Length / 4, $"{compressed.Length} bytes for {plain.Length}");
        Assert.Equal(texture.Mips.Count, back.Mips.Count);
        for (int level = 0; level < texture.Mips.Count; level++)
        {
            Assert.Equal(texture.Mips[level], back.Mips[level]);
        }
    }

    [Fact]
    public void ATextureWithoutMipsIsOneLevel()
    {
        TextureData texture = Texture(16, 16, true, mips: false);

        TextureData back = Ktx2Loader.Load("t", Ktx2Writer.Write(texture), true, TextureCaps.None, SamplerData.Default);

        Assert.Single(back.Mips);
        Assert.Equal(texture.Mips[0], back.Mips[0]);
    }

    [Fact]
    public void TheDescriptorSaysWhatTheBytesAre()
    {
        byte[] file = Ktx2Writer.Write(Texture(8, 8, srgb: true));
        byte[] linear = Ktx2Writer.Write(Texture(8, 8, srgb: false));

        Assert.Equal(43u, BitConverter.ToUInt32(file, 12));   // R8G8B8A8_SRGB
        Assert.Equal(37u, BitConverter.ToUInt32(linear, 12)); // R8G8B8A8_UNORM
        Assert.Equal(0u, BitConverter.ToUInt32(file, 44));    // no supercompression
        uint descriptor = BitConverter.ToUInt32(file, 48);
        Assert.Equal(2, file[descriptor + 14]);               // transfer function: sRGB
        Assert.Equal(1, linear[descriptor + 14]);             // linear
        Assert.Equal(0x1F, file[descriptor + 28 + (3 * 16) + 3]); // alpha: channel 15, linear
        // The smallest level is stored first.
        ulong level0 = BitConverter.ToUInt64(file, 80);
        ulong lastLevel = BitConverter.ToUInt64(file, 80 + (3 * 24));
        Assert.True(lastLevel < level0);
    }

    [Fact]
    public void WhatIsNotRgba8OrNotAMipChainIsRefused()
    {
        var bc7 = new TextureData("bc7", PixelFormat.Bc7UnormBlock, 4, 4, [new byte[16]], false, SamplerData.Default);
        var wrong = new TextureData("wrong", PixelFormat.R8G8B8A8Unorm, 4, 4, [new byte[64], new byte[5]], false, SamplerData.Default);

        Assert.Contains("only RGBA8 is written", Assert.Throws<ContentException>(() => Ktx2Writer.Write(bc7)).Message);
        Assert.Contains("level 1 has 5 bytes, not 16", Assert.Throws<ContentException>(() => Ktx2Writer.Write(wrong)).Message);
    }
}
