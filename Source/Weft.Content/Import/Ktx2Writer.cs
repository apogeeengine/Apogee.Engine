using Weft.Core.Graphics;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Weft.Content.Textures;

namespace Weft.Content.Import;

/// <summary>
/// Writes a texture as a KTX 2 file: the header, the level index, a data format descriptor that says what the bytes are, and the levels, smallest first as the format requires. <see cref="Ktx2Loader"/> reads it
/// back; so does any KTX 2 tool. <see cref="Write(TextureData, int)"/> takes a 2D texture of 8-bit RGBA with its mip chain; with <c>zstdLevel</c> each level is compressed with zstd
/// (<c>supercompressionScheme</c> 2, which libktx inflates when it loads): the file is smaller, the texture the same. This is not GPU texture compression: the texture is RGBA8 in memory and on the GPU. That is
/// <see cref="BasisEncoder"/>; this writer is for <c>compression="none"</c> and for textures too small to compress. <see cref="Write(WtexInfo, IReadOnlyList{ReadOnlyMemory{byte}})"/> takes the levels of a
/// <c>.wtex</c> as they are, whatever the payload (UASTC, RGBA8, BC4, BC5, BC6H, BC7, half-float RGBA; 2D, 3D, cube maps and arrays), without supercompression.
/// </summary>
public static class Ktx2Writer
{
    /// <summary>The zstd level the importer compresses textures with: good ratio, and fast enough for big textures.</summary>
    public const int ImportLevel = 9;

    private const int HeaderSize = 80;

    /// <summary>What a header says besides the sizes of the levels.</summary>
    private readonly record struct Fields(uint VkFormat, uint TypeSize, uint Width, uint Height, uint Depth, uint Layers, uint Faces, uint Scheme);

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

        var raw = new ReadOnlyMemory<byte>[levels];
        var stored = new ReadOnlyMemory<byte>[levels];
        for (int level = 0; level < levels; level++)
        {
            raw[level] = texture.Mips[level];
            stored[level] = zstdLevel > 0 ? Packs.PackFormat.Compress(texture.Mips[level], zstdLevel) : texture.Mips[level];
        }

        var fields = new Fields(srgb ? 43u : 37u, 1, (uint)texture.Width, (uint)texture.Height, 0, 0, 1, zstdLevel > 0 ? 2u : 0u);
        return Assemble(fields, raw, stored, 4, WtexPayload.Rgba8, srgb, premultiplied: false);
    }

    /// <summary>
    /// A <c>.wtex</c> as a KTX 2 file: the same level bytes, no supercompression. A UASTC texture has the format undefined and a descriptor of the UASTC model, as the Basis Universal tools write it; the others have
    /// the Vulkan format of their payload and its descriptor.
    /// </summary>
    /// <param name="info">The texture.</param>
    /// <param name="levels">The bytes of each level, the largest first, each of the size <see cref="WtexInfo.LevelSize"/> says.</param>
    /// <exception cref="ArgumentException">The texture cannot be a file, or a level has the wrong size.</exception>
    /// <exception cref="ContentException">The file would be larger than 2 GiB.</exception>
    public static byte[] Write(WtexInfo info, IReadOnlyList<ReadOnlyMemory<byte>> levels)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(levels);
        WtexFile.CheckLevels(info, levels);
        var raw = new ReadOnlyMemory<byte>[levels.Count];
        for (int level = 0; level < raw.Length; level++)
        {
            raw[level] = levels[level];
        }

        bool srgb = (info.Flags & WtexTraits.Srgb) != 0;
        var fields = new Fields(
            info.VkFormat,
            info.Payload == WtexPayload.Rgba16f ? 2u : 1u,
            (uint)info.Width,
            (uint)info.Height,
            info.Depth > 1 ? (uint)info.Depth : 0,
            (info.Flags & WtexTraits.Array) != 0 ? (uint)info.Layers : 0,
            (uint)info.Faces,
            0);
        return Assemble(fields, raw, raw, WtexInfo.BlockOf(info.Payload).Bytes, info.Payload, srgb, (info.Flags & WtexTraits.Premultiplied) != 0);
    }

    /// <param name="alignment">What each level's offset is a multiple of: the least common multiple of the bytes of a block and 4, which for these formats is the bytes of a block.</param>
    private static byte[] Assemble(Fields fields, ReadOnlyMemory<byte>[] raw, ReadOnlyMemory<byte>[] stored, int alignment, WtexPayload payload, bool srgb, bool premultiplied)
    {
        int levels = raw.Length;
        int indexSize = levels * 24;
        int descriptorOffset = HeaderSize + indexSize;
        int descriptorSize = DescriptorSize(payload);
        long dataStart = Align(descriptorOffset + descriptorSize, alignment);
        long[] offsets = new long[levels];
        long position = dataStart;
        for (int level = levels - 1; level >= 0; level--) // the smallest level comes first in the file
        {
            offsets[level] = position;
            position = Align(position + stored[level].Length, alignment);
        }

        if (position > int.MaxValue)
        {
            throw new ContentException($"The KTX 2 file would be {position} bytes; at most {int.MaxValue} are allowed.");
        }

        byte[] file = new byte[position];
        Span<byte> bytes = file;
        ReadOnlySpan<byte> magic = Ktx2Loader.Magic;
        magic.CopyTo(bytes);
        Put(bytes, 12, fields.VkFormat);
        Put(bytes, 16, fields.TypeSize);
        Put(bytes, 20, fields.Width);
        Put(bytes, 24, fields.Height);
        Put(bytes, 28, fields.Depth);            // pixelDepth: 0 for a 2D texture
        Put(bytes, 32, fields.Layers);           // layerCount: 0 when it is not an array
        Put(bytes, 36, fields.Faces);            // faceCount
        Put(bytes, 40, (uint)levels);
        Put(bytes, 44, fields.Scheme);           // supercompressionScheme: none, or zstd
        Put(bytes, 48, (uint)descriptorOffset);
        Put(bytes, 52, (uint)descriptorSize);
        // no key/value data, no supercompression global data: offsets and lengths stay 0

        for (int level = 0; level < levels; level++)
        {
            int entry = HeaderSize + (level * 24);
            Put(bytes, entry, (ulong)offsets[level]);
            Put(bytes, entry + 8, (ulong)stored[level].Length);
            Put(bytes, entry + 16, (ulong)raw[level].Length);
            stored[level].Span.CopyTo(bytes[(int)offsets[level]..]);
        }

        WriteDescriptor(bytes[descriptorOffset..], payload, srgb, premultiplied);
        return file;
    }

    private static int SampleCount(WtexPayload payload) => payload switch
    {
        WtexPayload.Rgba8 or WtexPayload.Rgba16f => 4,
        WtexPayload.Bc5 => 2,
        _ => 1,
    };

    private static int DescriptorSize(WtexPayload payload) => 4 + 24 + (SampleCount(payload) * 16);

    /// <summary>The basic data format descriptor, as the KTX 2 and Khronos Data Format specifications lay it out.</summary>
    private static void WriteDescriptor(Span<byte> bytes, WtexPayload payload, bool srgb, bool premultiplied)
    {
        int samples = SampleCount(payload);
        (int blockBytes, int blockWidth, int blockHeight) = WtexInfo.BlockOf(payload);
        Put(bytes, 0, (uint)DescriptorSize(payload));        // dfdTotalSize
        Put(bytes, 4, 0u);                                   // vendorId 0 (Khronos), descriptorType 0 (basic)
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], 2);                              // versionNumber
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], (ushort)(24 + (samples * 16))); // descriptorBlockSize
        bytes[12] = payload switch                           // colorModel
        {
            WtexPayload.Uastc => 166,
            WtexPayload.Bc7 => 134,
            WtexPayload.Bc6hU => 133,
            WtexPayload.Bc5 => 132,
            WtexPayload.Bc4 => 131,
            _ => 1, // RGBSDA
        };
        bytes[13] = 1;                                       // colorPrimaries: BT.709
        bytes[14] = (byte)(srgb ? 2 : 1);                    // transferFunction: sRGB or linear
        bytes[15] = (byte)(premultiplied ? 1 : 0);           // flags: alpha premultiplied or not
        bytes[16] = (byte)(blockWidth - 1);                  // texelBlockDimension0 and 1 are the block less one; 2 and 3 are 0
        bytes[17] = (byte)(blockHeight - 1);
        bytes[20] = (byte)blockBytes;                        // bytesPlane0

        for (int channel = 0; channel < samples; channel++)
        {
            Span<byte> sample = bytes[(28 + (channel * 16))..];
            switch (payload)
            {
                case WtexPayload.Rgba8:
                    BinaryPrimitives.WriteUInt16LittleEndian(sample, (ushort)(channel * 8)); // bitOffset
                    sample[2] = 7;                                                           // bitLength - 1
                    sample[3] = (byte)(channel < 3 ? channel : srgb ? 15 | 0x10 : 15);       // R, G, B; alpha is channel 15, and linear when the colour is sRGB (KHR_DF_SAMPLE_DATATYPE_LINEAR)
                    Put(sample, 8, 0u);                                                      // sampleLower
                    Put(sample, 12, 255u);                                                   // sampleUpper
                    break;
                case WtexPayload.Rgba16f:
                    BinaryPrimitives.WriteUInt16LittleEndian(sample, (ushort)(channel * 16));
                    sample[2] = 15;
                    sample[3] = (byte)((channel < 3 ? channel : 15) | 0xC0);                 // float and signed (KHR_DF_SAMPLE_DATATYPE_FLOAT, _SIGNED)
                    Put(sample, 8, 0xBF800000u);                                             // -1.0f
                    Put(sample, 12, 0x3F800000u);                                            // 1.0f
                    break;
                case WtexPayload.Bc5:
                    BinaryPrimitives.WriteUInt16LittleEndian(sample, (ushort)(channel * 64));
                    sample[2] = 63;
                    sample[3] = (byte)channel;                                               // KHR_DF_CHANNEL_BC5_RED, _GREEN
                    Put(sample, 8, 0u);
                    Put(sample, 12, uint.MaxValue);
                    break;
                case WtexPayload.Bc4:
                    sample[2] = 63;                                                          // one channel of 64 bits
                    Put(sample, 8, 0u);
                    Put(sample, 12, uint.MaxValue);
                    break;
                case WtexPayload.Uastc:
                    sample[2] = 127;                                                         // one channel of 128 bits
                    sample[3] = 3;                                                           // KHR_DF_CHANNEL_UASTC_RGBA
                    Put(sample, 8, 0u);
                    Put(sample, 12, uint.MaxValue);
                    break;
                default: // BC7 and BC6H: one channel of 128 bits
                    sample[2] = 127;
                    sample[3] = payload == WtexPayload.Bc6hU ? (byte)0x80 : (byte)0;        // BC6H is float
                    Put(sample, 8, 0u);
                    Put(sample, 12, payload == WtexPayload.Bc6hU ? 0x3F800000u : uint.MaxValue); // BC6H: up to 1.0f, as libktx writes it
                    break;
            }
        }
    }

    private static long Align(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static void Put(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);

    private static void Put(Span<byte> bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[offset..], value);
}
