using Apogee.Core.Graphics;
using System;
using System.Buffers.Binary;

namespace Apogee.Content.Import;

/// <summary>
/// Writes a 2D texture of 8-bit RGBA with its mip chain as a KTX 2 file, uncompressed (<c>supercompressionScheme</c> 0): the header, the level index, a data format descriptor that
/// says what the bytes are (RGBA, sRGB or linear), and the levels, smallest first as the format requires. <see cref="Ktx2Loader"/> reads it back; so does any KTX 2 tool. With
/// <c>zstdLevel</c> each level is compressed with zstd (<c>supercompressionScheme</c> 2, which libktx inflates when it loads): the file is smaller, the texture the same. This is not GPU
/// texture compression: the texture is RGBA8 in memory and on the GPU. That is <see cref="BasisEncoder"/>; this writer is for <c>compression="none"</c> and for textures too small to compress.
/// </summary>
public static class Ktx2Writer
{
    /// <summary>The zstd level the importer compresses textures with: good ratio, and fast enough for big textures.</summary>
    public const int ImportLevel = 9;

    private const int HeaderSize = 80;
    private const int DescriptorSize = 4 + 24 + 4 * 16;

    /// <exception cref="ContentException">The texture is not RGBA8, or its levels are not the sizes of a mip chain.</exception>
    public static byte[] Write(TextureData texture, int zstdLevel = 0)
    {
        bool srgb = texture.Format == PixelFormat.R8G8B8A8Srgb;
        if (texture.Format != PixelFormat.R8G8B8A8Srgb && texture.Format != PixelFormat.R8G8B8A8Unorm)
        {
            throw new ContentException($"Texture '{texture.Name}' is {texture.Format}; only RGBA8 is written.");
        }

        int levels = texture.Mips.Count;
        for (int level = 0; level < levels; level++)
        {
            int expected = Math.Max(1, texture.Width >> level) * Math.Max(1, texture.Height >> level) * 4;
            if (texture.Mips[level].Length != expected)
            {
                throw new ContentException($"Texture '{texture.Name}': level {level} has {texture.Mips[level].Length} bytes, not {expected}.");
            }
        }

        byte[][] stored = [.. texture.Mips];
        if (zstdLevel > 0)
        {
            for (int level = 0; level < levels; level++)
            {
                stored[level] = Packs.PackFormat.Compress(texture.Mips[level], zstdLevel);
            }
        }

        int indexSize = levels * 24;
        int descriptorOffset = HeaderSize + indexSize;
        int dataStart = Align(descriptorOffset + DescriptorSize, 4);
        long[] offsets = new long[levels];
        long position = dataStart;
        for (int level = levels - 1; level >= 0; level--) // the smallest level comes first in the file
        {
            offsets[level] = position;
            position = Align(position + stored[level].Length, 4);
        }

        byte[] file = new byte[position];
        Span<byte> bytes = file;
        ReadOnlySpan<byte> magic = Ktx2Loader.Magic;
        magic.CopyTo(bytes);
        Put(bytes, 12, srgb ? 43u : 37u);        // vkFormat: R8G8B8A8_SRGB or _UNORM
        Put(bytes, 16, 1u);                      // typeSize
        Put(bytes, 20, (uint)texture.Width);
        Put(bytes, 24, (uint)texture.Height);
        Put(bytes, 28, 0u);                      // pixelDepth: a 2D texture
        Put(bytes, 32, 0u);                      // layerCount: not an array
        Put(bytes, 36, 1u);                      // faceCount
        Put(bytes, 40, (uint)levels);
        Put(bytes, 44, zstdLevel > 0 ? 2u : 0u); // supercompressionScheme: none, or zstd
        Put(bytes, 48, (uint)descriptorOffset);
        Put(bytes, 52, (uint)DescriptorSize);
        // no key/value data, no supercompression global data: offsets and lengths stay 0

        for (int level = 0; level < levels; level++)
        {
            int entry = HeaderSize + (level * 24);
            Put(bytes, entry, (ulong)offsets[level]);
            Put(bytes, entry + 8, (ulong)stored[level].Length);
            Put(bytes, entry + 16, (ulong)texture.Mips[level].Length);
            stored[level].CopyTo(bytes[(int)offsets[level]..]);
        }

        WriteDescriptor(bytes[descriptorOffset..], srgb);
        return file;
    }

    /// <summary>The basic data format descriptor of four 8-bit channels, as the KTX 2 and Khronos Data Format specifications lay it out.</summary>
    private static void WriteDescriptor(Span<byte> bytes, bool srgb)
    {
        Put(bytes, 0, (uint)DescriptorSize);                 // dfdTotalSize
        Put(bytes, 4, 0u);                                   // vendorId 0 (Khronos), descriptorType 0 (basic)
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], 2);               // versionNumber
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], 24 + 4 * 16);    // descriptorBlockSize
        bytes[12] = 1;                                       // colorModel: RGBSDA
        bytes[13] = 1;                                       // colorPrimaries: BT.709
        bytes[14] = (byte)(srgb ? 2 : 1);                    // transferFunction: sRGB or linear
        bytes[15] = 0;                                       // flags: alpha is not premultiplied
        // texelBlockDimension0..3 are 0 (a block is one texel); bytesPlane0 is 4
        bytes[20] = 4;
        for (int channel = 0; channel < 4; channel++)
        {
            Span<byte> sample = bytes[(28 + (channel * 16))..];
            BinaryPrimitives.WriteUInt16LittleEndian(sample, (ushort)(channel * 8)); // bitOffset
            sample[2] = 7;                                                           // bitLength - 1
            sample[3] = (byte)(channel < 3 ? channel : 15 | 0x10);                   // R, G, B; alpha is channel 15 and linear (KHR_DF_SAMPLE_DATATYPE_LINEAR)
            Put(sample, 8, 0u);                                                      // sampleLower
            Put(sample, 12, 255u);                                                   // sampleUpper
        }
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static long Align(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static void Put(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);

    private static void Put(Span<byte> bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[offset..], value);
}
