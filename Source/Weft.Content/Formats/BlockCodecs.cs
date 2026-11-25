using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Weft.Content.Packs;
using MeshoptApi = Weft.Native.Meshopt.Meshopt;
using ZstdApi = Weft.Native.Zstd.Zstd;

namespace Weft.Content.Formats;

/// <summary>How the bytes of a block are stored in a <see cref="BinaryAsset"/>.</summary>
public enum BlockCodec : byte
{
    /// <summary>As they are: the stored bytes are the raw bytes, and a streamer can read or map them directly.</summary>
    None = 0,

    /// <summary>One zstd frame that states its size, and nothing after it.</summary>
    Zstd = 1,

    /// <summary>meshoptimizer's vertex codec: the raw bytes are <c>count</c> vertices of <c>stride</c> bytes (a multiple of 4, at most 256).</summary>
    MeshoptVertex = 2,

    /// <summary>meshoptimizer's index codec: the raw bytes are <c>count</c> indices of <c>stride</c> bytes (2 or 4), three to a triangle. A triangle may come back starting at another of its corners (the same winding).</summary>
    MeshoptIndex = 3,
}

/// <summary>The codecs of the blocks of a <see cref="BinaryAsset"/>: what a block may claim, and the encoding and decoding.</summary>
internal static unsafe class BlockCodecs
{
    /// <summary>The most a zstd frame can grow: its smallest block (an RLE block of 128 KiB) takes four bytes.</summary>
    private const long ZstdMaxExpansion = 32768;

    /// <summary>The encoder this engine writes with: meshoptimizer's vertex codec version 1 at its default level, so that the bytes do not depend on what else in the process set.</summary>
    private const int VertexVersion = 1;

    private const int VertexLevel = 2;

    private static readonly object s_indexEncoder = new();

    /// <summary>
    /// The fewest bytes meshoptimizer's vertex codec can store <paramref name="raw"/> bytes of vertices of <paramref name="stride"/> bytes in (version 1, the smallest of the versions it decodes): a header byte,
    /// a control byte for every four byte-channels of each whole block (a block is as many vertices as 8 KiB holds, in groups of 16, at most 256), and the tail that holds the first vertex.
    /// </summary>
    private static long MinVertexBytes(long raw, int stride)
    {
        long count = raw / stride;
        long block = Math.Min((8192 / stride) & ~15, 256);
        long tail = Math.Min(Math.Max(stride, 32), Math.Max(stride + (stride / 4), 24));
        return 1 + (count / block * (stride / 4)) + tail;
    }

    /// <summary>Why a block row is wrong for its codec, or null. Looks at the numbers only, so nothing is allocated for a row that lies.</summary>
    public static string? Check(BlockCodec codec, long stored, long raw, int stride)
    {
        switch (codec)
        {
            case BlockCodec.None:
                return stride != 0 ? "has an element size but no codec" : stored != raw ? "is not compressed but its stored size is not its raw size" : null;
            case BlockCodec.Zstd:
                return stride != 0 ? "has an element size but no mesh codec" : raw / ZstdMaxExpansion > stored ? "is stored in fewer bytes than zstd can make" : null;
            case BlockCodec.MeshoptVertex:
                if (stride is < 4 or > 256 || stride % 4 != 0)
                {
                    return $"has the vertex size {stride}, which is not a multiple of 4 from 4 to 256";
                }

                if (raw % stride != 0)
                {
                    return $"is not a whole number of vertices of {stride} bytes";
                }

                return stored < MinVertexBytes(raw, stride) ? "is stored in fewer bytes than the vertex codec can make" : null;
            case BlockCodec.MeshoptIndex:
                if (stride is not (2 or 4))
                {
                    return $"has the index size {stride}, which is not 2 or 4";
                }

                if (raw % (stride * 3L) != 0)
                {
                    return "does not hold whole triangles";
                }

                // The decoder refuses less than a header byte, a byte for each triangle and a 16-byte table.
                return stored < 17 + (raw / stride / 3) ? "is stored in fewer bytes than the index codec can make" : null;
            default:
                return $"has the codec {(byte)codec}, which this engine does not know";
        }
    }

    /// <summary>The raw bytes of a block stored with <paramref name="codec"/> (not <see cref="BlockCodec.None"/>), after <see cref="Check"/>.</summary>
    /// <exception cref="InvalidOperationException">The codec cannot decode the bytes.</exception>
    public static byte[] Decode(BlockCodec codec, ReadOnlySpan<byte> stored, long raw, int stride)
    {
        if (codec == BlockCodec.Zstd)
        {
            fixed (byte* source = stored)
            {
                // One frame exactly: anything after it (a skippable frame, say) would be bytes that nothing checks.
                nuint frame = ZstdApi.FindFrameCompressedSize(source, (nuint)stored.Length);
                if (ZstdApi.IsError(frame) != 0 || frame != (nuint)stored.Length)
                {
                    throw new InvalidOperationException("the data is not exactly one zstd frame");
                }
            }

            try
            {
                return PackFormat.Decompress(stored, raw);
            }
            catch (CryptographicException e)
            {
                throw new InvalidOperationException(e.Message, e);
            }
        }

        byte[] output = new byte[raw];
        fixed (byte* source = stored)
        fixed (byte* target = output)
        {
            int result = codec == BlockCodec.MeshoptVertex
                ? MeshoptApi.DecodeVertexBuffer(target, (nuint)(raw / stride), (nuint)stride, source, (nuint)stored.Length)
                : MeshoptApi.DecodeIndexBuffer(target, (nuint)(raw / stride), (nuint)stride, source, (nuint)stored.Length);
            return result == 0 ? output : throw new InvalidOperationException("the data does not decode");
        }
    }

    /// <summary>The stored bytes of <paramref name="raw"/> with <paramref name="codec"/> (not <see cref="BlockCodec.None"/>).</summary>
    public static byte[] Encode(BlockCodec codec, ReadOnlySpan<byte> raw, int stride, int zstdLevel)
    {
        if (codec == BlockCodec.Zstd)
        {
            return PackFormat.Compress(raw, zstdLevel);
        }

        nuint count = (nuint)(raw.Length / stride);
        if (codec == BlockCodec.MeshoptVertex)
        {
            byte[] buffer = new byte[(int)MeshoptApi.EncodeVertexBufferBound(count, (nuint)stride)];
            fixed (byte* source = raw)
            fixed (byte* target = buffer)
            {
                nuint size = MeshoptApi.EncodeVertexBufferLevel(target, (nuint)buffer.Length, source, count, (nuint)stride, VertexLevel, VertexVersion);
                return buffer.AsSpan(0, (int)size).ToArray();
            }
        }

        // The index encoder takes 32-bit indices.
        uint[] indices = new uint[(int)count];
        if (stride == 4)
        {
            MemoryMarshal.Cast<byte, uint>(raw).CopyTo(indices);
        }
        else
        {
            ReadOnlySpan<ushort> narrow = MemoryMarshal.Cast<byte, ushort>(raw);
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = narrow[i];
            }
        }

        uint highest = 0;
        foreach (uint index in indices)
        {
            highest = Math.Max(highest, index);
        }

        byte[] encoded = new byte[(int)MeshoptApi.EncodeIndexBufferBound(count, (nuint)highest + 1)];
        fixed (uint* source = indices)
        fixed (byte* target = encoded)
        {
            // The index encoder has no version argument, only a setting for the whole process: set it here, and make sure two writers do not set it at once.
            lock (s_indexEncoder)
            {
                MeshoptApi.EncodeIndexVersion(1);
                nuint size = MeshoptApi.EncodeIndexBuffer(target, (nuint)encoded.Length, source, count);
                return encoded.AsSpan(0, (int)size).ToArray();
            }
        }
    }
}
