using System;
using System.IO;
using Apogee.Native.Ktx;
using Xunit;
using KtxApi = Apogee.Native.Ktx.Ktx;

namespace Apogee.Native.Tests;

public unsafe class KtxSmokeTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ktx2", name));

    /// <summary>Loads a KTX 2 file and transcodes it to RGBA8; returns the level 0 pixels and the texture's size and level count.</summary>
    private static (byte[] Pixels, uint Width, uint Height, uint Levels) LoadRgba(string name) => LoadRgba(Fixture(name));

    private static (byte[] Pixels, uint Width, uint Height, uint Levels) LoadRgba(byte[] file)
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");

        Texture2* texture;
        fixed (byte* data = file)
        {
            Assert.Equal(Result.Success, KtxApi.Texture2CreateFromMemory(data, (nuint)file.Length, TextureCreateFlagBits.LoadImageData, &texture));
        }

        try
        {
            Assert.True(KtxApi.Texture2NeedsTranscoding(texture));
            Assert.Equal(Result.Success, KtxApi.Texture2TranscodeBasis(texture, TranscodeFmt.TtfRgba32, 0));
            Assert.False(KtxApi.Texture2NeedsTranscoding(texture));

            nuint offset;
            Assert.Equal(Result.Success, KtxApi.Texture2GetImageOffset(texture, 0, 0, 0, &offset));
            int size = checked((int)(texture->BaseWidth * texture->BaseHeight * 4));
            return (new ReadOnlySpan<byte>(texture->PData + offset, size).ToArray(), texture->BaseWidth, texture->BaseHeight, texture->NumLevels);
        }
        finally
        {
            KtxApi.Texture2Destroy(texture);
        }
    }

    [Theory]
    [InlineData("cyan_rgb_reference_basis.ktx2")]
    [InlineData("cyan_rgb_reference_uastc.ktx2")]
    public void KtxTranscodesBasisToRgba8(string file)
    {
        (byte[] pixels, uint width, uint height, _) = LoadRgba(file);

        Assert.True(width > 0 && height > 0);
        Assert.Equal((int)(width * height * 4), pixels.Length);

        // The reference image is cyan (0, 255, 255); Basis is lossy, so allow for it.
        Assert.InRange(pixels[0], 0, 48);
        Assert.InRange(pixels[1], 207, 255);
        Assert.InRange(pixels[2], 207, 255);
        Assert.Equal(255, pixels[3]);
    }

    /// <summary>
    /// The same file with every level compressed by zstd (supercompression scheme 2), made here with our zstd binding: the levels are appended, the level index points at
    /// them, and the header says zstd. libktx inflates them with the zstd library it links.
    /// </summary>
    private static byte[] WithZstdLevels(byte[] file)
    {
        NativeRequirements.Require(typeof(Apogee.Native.Zstd.Zstd).Assembly, "zstd");
        const int HeaderSize = 80;

        Assert.Equal(0u, BitConverter.ToUInt32(file, 44)); // no supercompression yet
        uint levels = BitConverter.ToUInt32(file, 40);
        var result = new System.Collections.Generic.List<byte>(file);
        for (int level = 0; level < levels; level++)
        {
            int entry = HeaderSize + (level * 24);
            ulong offset = BitConverter.ToUInt64(file, entry);
            ulong length = BitConverter.ToUInt64(file, entry + 8);

            byte[] compressed = new byte[(int)Apogee.Native.Zstd.Zstd.CompressBound((nuint)length)];
            nuint size;
            fixed (byte* source = &file[(int)offset])
            fixed (byte* target = compressed)
            {
                size = Apogee.Native.Zstd.Zstd.Compress(target, (nuint)compressed.Length, source, (nuint)length, 9);
            }

            Assert.Equal(0u, Apogee.Native.Zstd.Zstd.IsError(size));
            while (result.Count % 8 != 0)
            {
                result.Add(0);
            }

            ulong newOffset = (ulong)result.Count;
            result.AddRange(compressed.AsSpan(0, (int)size).ToArray());
            WriteIndex(result, entry, newOffset, size);
        }

        byte[] bytes = result.ToArray();
        BitConverter.GetBytes(2u).CopyTo(bytes, 44);
        return bytes;
    }

    private static void WriteIndex(System.Collections.Generic.List<byte> file, int entry, ulong offset, nuint compressedLength)
    {
        byte[] offsetBytes = BitConverter.GetBytes(offset);
        byte[] lengthBytes = BitConverter.GetBytes((ulong)compressedLength);
        for (int i = 0; i < 8; i++)
        {
            file[entry + i] = offsetBytes[i];
            file[entry + 8 + i] = lengthBytes[i];
        }
    }

    [Fact]
    public void KtxInflatesZstdSupercompressedLevelsThroughOurZstd()
    {
        byte[] plain = Fixture("cyan_rgb_reference_uastc.ktx2");
        byte[] compressed = WithZstdLevels(plain);
        Assert.Equal(2u, BitConverter.ToUInt32(compressed, 44));

        (byte[] expected, uint width, uint height, _) = LoadRgba(plain);
        (byte[] pixels, uint compressedWidth, uint compressedHeight, _) = LoadRgba(compressed);

        Assert.Equal((width, height), (compressedWidth, compressedHeight));
        Assert.Equal(expected, pixels);
    }

    [Fact]
    public void KtxReadsAMipChain()
    {
        (byte[] pixels, uint width, uint height, uint levels) = LoadRgba("rgba-mipmap-reference-basis.ktx2");

        Assert.True(levels > 1);
        Assert.Equal((int)(width * height * 4), pixels.Length);
        Assert.Equal(1u, width >> (int)(levels - 1));
    }

    [Fact]
    public void KtxEncodesUastcAndTheTranscoderReadsItBack()
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
        NativeRequirements.Require(typeof(Apogee.Native.Zstd.Zstd).Assembly, "zstd");

        // A 16x8 image whose left half is red and whose right half is blue.
        byte[] pixels = new byte[16 * 8 * 4];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int i = ((y * 16) + x) * 4;
                pixels[i] = (byte)(x < 8 ? 255 : 0);
                pixels[i + 2] = (byte)(x < 8 ? 0 : 255);
                pixels[i + 3] = 255;
            }
        }

        var info = new TextureCreateInfo { VkFormat = 37, BaseWidth = 16, BaseHeight = 8, BaseDepth = 1, NumDimensions = 2, NumLevels = 1, NumLayers = 1, NumFaces = 1 };
        Texture2* encoder;
        Assert.Equal(Result.Success, KtxApi.Texture2Create(&info, TextureCreateStorageEnum.AllocStorage, &encoder));
        byte* file;
        nuint size;
        try
        {
            fixed (byte* source = pixels)
            {
                Assert.Equal(Result.Success, encoder->Vtbl->SetImageFromMemory((Texture*)encoder, 0, 0, 0, source, (nuint)pixels.Length));
            }

            var parameters = new BasisParams { StructSize = (uint)sizeof(BasisParams), Uastc = true, ThreadCount = 2, UastcFlags = 2 };
            Assert.Equal(Result.Success, KtxApi.Texture2CompressBasisEx(encoder, &parameters));
            Assert.Equal(Result.Success, KtxApi.Texture2DeflateZstd(encoder, 9));
            Assert.Equal(Result.Success, KtxApi.Texture2WriteToMemory(encoder, &file, &size));
        }
        finally
        {
            KtxApi.Texture2Destroy(encoder);
        }

        byte[] ktx2 = new ReadOnlySpan<byte>(file, (int)size).ToArray();
        System.Runtime.InteropServices.NativeMemory.Free(file);
        Assert.Equal(2u, BitConverter.ToUInt32(ktx2, 44)); // zstd supercompression

        (byte[] back, uint width, uint height, _) = LoadRgba(ktx2);

        Assert.Equal((16u, 8u), (width, height));
        Assert.InRange(back[0], 232, 255);                       // left: red
        Assert.InRange(back[2], 0, 24);
        Assert.InRange(back[(15 * 4) + 0], 0, 24);               // right: blue
        Assert.InRange(back[(15 * 4) + 2], 232, 255);
    }

    [Fact]
    public void KtxRejectsNotKtx()
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");

        byte[] garbage = new byte[128];
        Texture2* texture;
        fixed (byte* data = garbage)
        {
            Assert.Equal(Result.UnknownFileFormat, KtxApi.Texture2CreateFromMemory(data, (nuint)garbage.Length, TextureCreateFlagBits.NoFlags, &texture));
        }
    }
}
