using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Content.Formats;
using Weft.Content.Xml;
using Weft.Core.Assets;
using Xunit;

namespace Weft.Content.Tests;

/// <summary>The common binary container (<see cref="BinaryAsset"/>, <see cref="BinaryAssetWriter"/>): the layout, determinism, hostile input, the dump and the UID in the header.</summary>
public partial class BinaryAssetTests
{
    private static readonly uint s_magic = BinaryAsset.Fourcc("TSTA");
    private static readonly AssetUid s_uid = new(0x0123456789ABCDEF);

    static BinaryAssetTests()
    {
        // A test-only format, registered the way a real one registers its dumper: META is four integers.
        AssetDumpers.Register(s_magic, static (asset, output) =>
        {
            ReadOnlySpan<byte> meta = asset.Meta.Span;
            if (meta.Length < 8)
            {
                output.WriteLine("(short)");
                return;
            }

            output.WriteLine($"width = {BinaryPrimitives.ReadUInt32LittleEndian(meta)}");
            output.WriteLine($"height = {BinaryPrimitives.ReadUInt32LittleEndian(meta[4..])}");
        });
    }

    private static byte[] Pattern(int length, int seed)
    {
        var random = new Random(seed);
        byte[] data = new byte[length];
        random.NextBytes(data);
        return data;
    }

    private static byte[] Meta()
    {
        byte[] meta = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(meta, 640);
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(4), 480);
        return meta;
    }

    /// <summary>Vertices of 16 bytes and the indices of a strip of quads, so that the mesh codecs have something to do.</summary>
    private static (byte[] Vertices, byte[] Indices) Mesh()
    {
        const int Quads = 40;
        float[] vertices = new float[(Quads + 1) * 2 * 4];
        for (int i = 0; i <= Quads; i++)
        {
            for (int row = 0; row < 2; row++)
            {
                int v = ((i * 2) + row) * 4;
                vertices[v] = i;
                vertices[v + 1] = row;
                vertices[v + 2] = 0.5f * i;
                vertices[v + 3] = 1;
            }
        }

        uint[] indices = new uint[Quads * 6];
        for (int i = 0; i < Quads; i++)
        {
            uint a = (uint)(i * 2);
            uint[] quad = [a, a + 1, a + 2, a + 1, a + 3, a + 2];
            quad.CopyTo(indices, i * 6);
        }

        return (MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray(), MemoryMarshal.AsBytes(indices.AsSpan()).ToArray());
    }

    /// <summary>The triangles with each rotated to start at its smallest index: what is the same after the index codec, which may start a triangle at another corner.</summary>
    private static uint[][] Triangles(ReadOnlySpan<byte> raw, int stride)
    {
        uint[] indices = new uint[raw.Length / stride];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = stride == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(raw[(i * 4)..]) : BinaryPrimitives.ReadUInt16LittleEndian(raw[(i * 2)..]);
        }

        var triangles = new uint[indices.Length / 3][];
        for (int t = 0; t < triangles.Length; t++)
        {
            uint[] corners = indices[(t * 3)..((t * 3) + 3)];
            int first = Array.IndexOf(corners, corners.Min());
            triangles[t] = [corners[first], corners[(first + 1) % 3], corners[(first + 2) % 3]];
        }

        return triangles;
    }

    private static readonly byte[] s_plain = Pattern(5000, 1);
    private static readonly byte[] s_squeezable = Enumerable.Range(0, 20000).Select(i => (byte)(i % 7)).ToArray();

    private static BinaryAssetWriter Writer()
    {
        (byte[] vertices, byte[] indices) = Mesh();
        var writer = new BinaryAssetWriter(s_magic, 3) { Uid = s_uid, Flags = 0x5, ZstdLevel = 3 };
        writer.SetImportHash(SHA256.HashData(Encoding.UTF8.GetBytes("settings")));
        writer.AddMeta(Meta());
        writer.AddBlock(BinaryAsset.Fourcc("PLN0"), s_plain);
        writer.AddBlock(BinaryAsset.Fourcc("ZST0"), s_squeezable, BlockCodec.Zstd);
        writer.AddBlock(BinaryAsset.Fourcc("VTX0"), vertices, BlockCodec.MeshoptVertex, stride: 16);
        writer.AddBlock(BinaryAsset.Fourcc("IDX0"), indices, BlockCodec.MeshoptIndex, stride: 4);
        writer.AddBlock(BinaryAsset.Fourcc("SML0"), new byte[] { 1, 2, 3 }, alignmentLog2: 2);
        return writer;
    }

    private static byte[] ValidFile() => Writer().ToArray();

    /// <summary>Where the row of block <paramref name="index"/> starts.</summary>
    private static int Row(int index) => BinaryAsset.HeaderSize + (index * BinaryAsset.BlockRowSize);

    /// <summary>Makes the checksum of the header and the table right again after a test changed them, so that the change is judged on its own and not as damage.</summary>
    private static void Seal(byte[] file)
    {
        if (file.Length < 64)
        {
            return;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16));
        if (count is >= 1 and <= 4096 && BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(24)) == 64 && 64 + (count * 40L) <= file.Length)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(56), BinaryAsset.HeaderChecksum(file.AsSpan(0, 64), file.AsSpan(64, (int)count * 40)));
        }
    }

    private static byte[] With(Action<byte[]> change)
    {
        byte[] file = ValidFile();
        change(file);
        Seal(file);
        return file;
    }

    /// <summary>A block as a test wants it in the table, whatever it holds.</summary>
    private sealed record Crafted(string Kind, BlockCodec Codec, byte[] Stored, long Raw, int Stride = 0, uint? Crc = null, int AlignmentLog2 = 12);

    /// <summary>A file with a valid <c>META</c> and the given blocks after it, laid out in order at aligned offsets, with the table sealed: for a block that the writer would never make.</summary>
    private static byte[] Craft(params Crafted[] blocks)
    {
        byte[] meta = Meta();
        int rows = 1 + blocks.Length;
        long position = 64 + (rows * 40L);
        var offsets = new long[rows];
        var sizes = new long[rows];
        offsets[0] = (position + 7) / 8 * 8;
        sizes[0] = meta.Length;
        position = offsets[0] + meta.Length;
        for (int i = 0; i < blocks.Length; i++)
        {
            long alignment = 1L << blocks[i].AlignmentLog2;
            offsets[i + 1] = (position + alignment - 1) / alignment * alignment;
            sizes[i + 1] = blocks[i].Stored.Length;
            position = offsets[i + 1] + sizes[i + 1];
        }

        byte[] file = new byte[position];
        BinaryPrimitives.WriteUInt32LittleEndian(file, s_magic);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), (uint)rows);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 40);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(24), 64);
        for (int i = 0; i < rows; i++)
        {
            Span<byte> row = file.AsSpan(64 + (i * 40), 40);
            byte[] stored = i == 0 ? meta : blocks[i - 1].Stored;
            BinaryPrimitives.WriteUInt32LittleEndian(row, BinaryAsset.Fourcc(i == 0 ? "META" : blocks[i - 1].Kind));
            row[4] = i == 0 ? (byte)0 : (byte)blocks[i - 1].Codec;
            row[6] = i == 0 ? (byte)3 : (byte)blocks[i - 1].AlignmentLog2;
            BinaryPrimitives.WriteUInt64LittleEndian(row[8..], (ulong)offsets[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(row[16..], (ulong)stored.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(row[24..], (ulong)(i == 0 ? meta.Length : blocks[i - 1].Raw));
            BinaryPrimitives.WriteUInt32LittleEndian(row[32..], i == 0 ? Crc32C.Compute(meta) : blocks[i - 1].Crc ?? 0);
            BinaryPrimitives.WriteUInt32LittleEndian(row[36..], i == 0 ? 0 : (uint)blocks[i - 1].Stride);
            stored.CopyTo(file, offsets[i]);
        }

        Seal(file);
        return file;
    }

    // ---- CRC ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Crc32CIsTheCastagnoliCheckValue()
    {
        byte[] check = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xE3069283u, Crc32C.Compute(check));
        Assert.Equal(0xE3069283u, Crc32C.ComputeSoftware(check));
        Assert.Equal(0u, Crc32C.Compute([]));
    }

    [Fact]
    public void Crc32CByInstructionAndByTableAgree()
    {
        for (int length = 0; length < 70; length++)
        {
            byte[] data = Pattern(length, length);
            Assert.Equal(Crc32C.ComputeSoftware(data), Crc32C.Compute(data));
        }

        byte[] big = Pattern(100_003, 9);
        Assert.Equal(Crc32C.ComputeSoftware(big), Crc32C.Compute(big));
    }

    // ---- Round trip -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void RoundTripGivesBackEveryBlockAndTheHeader()
    {
        (byte[] vertices, byte[] indices) = Mesh();
        byte[] file = ValidFile();

        BinaryAsset asset = BinaryAsset.Read(file, "test.tsta", s_magic);

        Assert.Equal(s_magic, asset.Magic);
        Assert.Equal("TSTA", asset.MagicName);
        Assert.Equal(3, asset.FormatVersion);
        Assert.Equal(5u, asset.Flags);
        Assert.Equal(s_uid, asset.Uid);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes("settings"))[..16], asset.ImportHash.ToArray());
        Assert.Equal(file.Length, asset.FileLength);
        Assert.Equal(["META", "PLN0", "ZST0", "VTX0", "IDX0", "SML0"], asset.Blocks.Select(b => b.KindName));
        Assert.Equal(Meta(), asset.Meta.ToArray());
        Assert.Equal(s_plain, asset.ReadBlock(BinaryAsset.Fourcc("PLN0")).ToArray());
        Assert.Equal(s_squeezable, asset.ReadBlock(BinaryAsset.Fourcc("ZST0")).ToArray());
        Assert.Equal(vertices, asset.ReadBlock(BinaryAsset.Fourcc("VTX0")).ToArray());
        Assert.Equal(Triangles(indices, 4), Triangles(asset.ReadBlock(BinaryAsset.Fourcc("IDX0")).Span, 4));
        Assert.Equal(new byte[] { 1, 2, 3 }, asset.ReadBlock(BinaryAsset.Fourcc("SML0")).ToArray());

        Assert.True(asset.Blocks[2].StoredSize < asset.Blocks[2].RawSize, "zstd made it smaller");
        Assert.True(asset.Blocks[3].StoredSize < asset.Blocks[3].RawSize, "meshopt made the vertices smaller");
        Assert.True(asset.Blocks[4].StoredSize < asset.Blocks[4].RawSize, "meshopt made the indices smaller");
        Assert.True(asset.Has(BinaryAsset.Fourcc("IDX0")));
        Assert.False(asset.Has(BinaryAsset.Fourcc("NOPE")));
        Assert.Throws<ContentException>(() => asset.ReadBlock(BinaryAsset.Fourcc("NOPE")));
    }

    [Fact]
    public void TheHeaderIsWhatTheManualSays()
    {
        byte[] file = ValidFile();

        Assert.Equal("TSTA"u8.ToArray(), file[..4]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)));
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16)));
        Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20)));
        Assert.Equal(64ul, BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(24)));
        Assert.Equal(s_uid.Value, BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(32)));
        Assert.Equal(BinaryAsset.HeaderChecksum(file.AsSpan(0, 64), file.AsSpan(64, 6 * 40)), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(56)));
        Assert.All(file[60..64], b => Assert.Equal(0, b));
        Assert.Equal("META"u8.ToArray(), file[64..68]);
    }

    [Fact]
    public void ABlockWithNoCodecIsASliceOfTheFileNotACopy()
    {
        byte[] file = ValidFile();
        BinaryAsset asset = BinaryAsset.Read(file);
        BinaryBlock plain = asset.Blocks[1];

        ReadOnlyMemory<byte> block = asset.ReadBlock(plain.Kind);

        Assert.True(Unsafe.AreSame(in MemoryMarshal.GetReference(block.Span), in file[plain.Offset]));
        Assert.True(Unsafe.AreSame(in MemoryMarshal.GetReference(asset.StoredBytes(plain.Kind)), in file[plain.Offset]));
        Assert.True(Unsafe.AreSame(in MemoryMarshal.GetReference(asset.Meta.Span), in file[asset.Blocks[0].Offset]));
    }

    [Fact]
    public void NarrowIndicesRoundTripThroughTheMeshCodec()
    {
        ushort[] indices = [0, 1, 2, 2, 1, 3, 2, 3, 4];
        byte[] raw = MemoryMarshal.AsBytes(indices.AsSpan()).ToArray();
        var writer = new BinaryAssetWriter(s_magic, 1);
        writer.AddMeta(Meta());
        writer.AddBlock(BinaryAsset.Fourcc("IDX0"), raw, BlockCodec.MeshoptIndex, stride: 2);

        BinaryAsset asset = BinaryAsset.Read(writer.ToArray());

        Assert.Equal(Triangles(raw, 2), Triangles(asset.ReadBlock(BinaryAsset.Fourcc("IDX0")).Span, 2));
    }

    [Fact]
    public void AFileWithOnlyMetaAndAnEmptyBlockIsFine()
    {
        var writer = new BinaryAssetWriter(s_magic, 1);
        writer.AddMeta(Meta());
        writer.AddBlock(BinaryAsset.Fourcc("EMPT"), ReadOnlyMemory<byte>.Empty);

        BinaryAsset asset = BinaryAsset.Read(writer.ToArray());

        Assert.Equal(2, asset.Blocks.Count);
        Assert.Equal(0, asset.ReadBlock(BinaryAsset.Fourcc("EMPT")).Length);
        Assert.True(asset.Uid.IsNone);
        Assert.All(asset.ImportHash.ToArray(), b => Assert.Equal(0, b));
    }

    // ---- The writer -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void SameInputSameBytes()
    {
        byte[] first = Writer().ToArray();
        byte[] second = Writer().ToArray();

        Assert.Equal(first, second);
        Assert.Equal(first, Writer().ToArray());

        // Writing twice from one writer is the same file too.
        BinaryAssetWriter writer = Writer();
        Assert.Equal(writer.ToArray(), writer.ToArray());

        using var stream = new MemoryStream();
        writer.WriteTo(stream);
        Assert.Equal(first, stream.ToArray());
    }

    [Fact]
    public void BlocksAreAlignedTo4096AndThePaddingIsZero()
    {
        byte[] file = ValidFile();
        BinaryAsset asset = BinaryAsset.Read(file);

        foreach (BinaryBlock block in asset.Blocks.Skip(1).Take(4))
        {
            Assert.Equal(12, block.AlignmentLog2);
            Assert.Equal(0, block.Offset % 4096);
        }

        Assert.Equal(0, asset.Blocks[0].Offset % 8);
        Assert.Equal(2, asset.Blocks[5].AlignmentLog2);
        Assert.Equal(0, asset.Blocks[5].Offset % 4);

        // The bytes that are in no block, the header or the table are zero.
        var covered = new bool[file.Length];
        Array.Fill(covered, true, 0, Row(asset.Blocks.Count));
        foreach (BinaryBlock block in asset.Blocks)
        {
            Array.Fill(covered, true, (int)block.Offset, (int)block.StoredSize);
        }

        for (int i = 0; i < file.Length; i++)
        {
            if (!covered[i])
            {
                Assert.Equal(0, file[i]);
            }
        }

        // The table is in the order of the file.
        Assert.True(asset.Blocks.Zip(asset.Blocks.Skip(1), (a, b) => a.Offset + a.StoredSize <= b.Offset).All(x => x));
        Assert.Equal(file.Length, asset.Blocks[^1].Offset + asset.Blocks[^1].StoredSize);
    }

    [Fact]
    public void TheWriterRefusesWhatCannotBeRead()
    {
        var writer = new BinaryAssetWriter(s_magic, 1);
        Assert.Throws<InvalidOperationException>(() => writer.AddBlock(BinaryAsset.Fourcc("ABCD"), s_plain));
        Assert.Throws<InvalidOperationException>(writer.ToArray);
        writer.AddMeta(Meta());
        Assert.Throws<InvalidOperationException>(() => writer.AddMeta(Meta()));
        writer.AddBlock(BinaryAsset.Fourcc("ABCD"), s_plain);
        Assert.Throws<ArgumentException>(() => writer.AddBlock(BinaryAsset.Fourcc("ABCD"), s_plain));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddBlock(BinaryAsset.Fourcc("EFGH"), s_plain, alignmentLog2: 17));
        Assert.Throws<ArgumentException>(() => writer.AddBlock(BinaryAsset.Fourcc("EFGH"), s_plain, BlockCodec.MeshoptVertex, stride: 6));
        Assert.Throws<ArgumentException>(() => writer.AddBlock(BinaryAsset.Fourcc("EFGH"), new byte[8], BlockCodec.MeshoptIndex, stride: 4));
        Assert.Throws<ArgumentException>(() => writer.AddBlock(BinaryAsset.Fourcc("EFGH"), s_plain, stride: 4));
        Assert.Throws<ArgumentException>(() => new BinaryAssetWriter(s_magic, 1).AddMeta(new byte[BinaryAsset.MaxMetaBytes + 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BinaryAssetWriter(s_magic, 0));
        Assert.Throws<ArgumentException>(() => BinaryAsset.Fourcc("ABC"));
        Assert.Throws<ArgumentException>(() => BinaryAsset.Fourcc("AB\0D"));
    }

    // ---- Hostile input --------------------------------------------------------------------------------------------------------------

    private static void AssertRefused(byte[] file, string? message = null)
    {
        ContentException e = Assert.Throws<ContentException>(() =>
        {
            BinaryAsset asset = BinaryAsset.Read(file, "hostile.bin");
            for (int i = 0; i < asset.Blocks.Count; i++)
            {
                asset.ReadBlock(i);
            }
        });
        Assert.Contains("hostile.bin", e.Message, StringComparison.Ordinal);
        if (message is not null)
        {
            Assert.Contains(message, e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ACrcThatDoesNotMatchIsRefused()
    {
        BinaryAsset valid = BinaryAsset.Read(ValidFile());

        // A flipped byte in a block is found when the block is read, and only that block.
        byte[] damaged = With(f => f[(int)valid.Blocks[1].Offset + 10] ^= 0xFF);
        BinaryAsset asset = BinaryAsset.Read(damaged, "hostile.bin");
        ContentException e = Assert.Throws<ContentException>(() => asset.ReadBlock(BinaryAsset.Fourcc("PLN0")));
        Assert.Contains("checksum", e.Message, StringComparison.Ordinal);
        Assert.Equal(s_squeezable, asset.ReadBlock(BinaryAsset.Fourcc("ZST0")).ToArray());

        // A wrong CRC in the table is the same thing.
        AssertRefused(With(f => f[Row(3) + 32] ^= 1), "checksum");

        // META is read at once.
        Assert.Throws<ContentException>(() => BinaryAsset.Read(With(f => f[(int)valid.Blocks[0].Offset] ^= 1), "hostile.bin"));

        // So is a flipped byte in a compressed block.
        AssertRefused(With(f => f[(int)valid.Blocks[2].Offset + 4] ^= 0x55));
    }

    [Fact]
    public void ATruncatedFileIsRefusedAtEveryLength()
    {
        byte[] file = ValidFile();
        for (int length = 0; length < file.Length; length++)
        {
            AssertRefused(file[..length]);
        }
    }

    [Fact]
    public void AFileThatIsNotAContainerIsRefusedWithAMessage()
    {
        AssertRefused(Encoding.UTF8.GetBytes("<Scene version=\"1\"/>" + new string(' ', 100)));
        AssertRefused(new byte[200], "binary asset");
        AssertRefused([], "too short");
        Assert.Contains("'TSTA'", Assert.Throws<ContentException>(() => BinaryAsset.Read(ValidFile(), "x", BinaryAsset.Fourcc("WMDL"))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderFieldsThatAreWrongAreRefused()
    {
        AssertRefused(With(f => f[4] = 2), "container version 2");
        AssertRefused(With(f => f[4] = 0), "not a version");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(6), 0)), "format version 0");
        AssertRefused(With(f => f[12] = 80), "header");
        AssertRefused(With(f => f[20] = 48), "row");
        AssertRefused(With(f => f[60] = 1), "reserved");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), 0)), "no blocks");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), 4097)), "4096");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), uint.MaxValue)), "4096");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), 4096)), "cut short");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(24), ulong.MaxValue)), "block table");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(24), 8)), "block table");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(24), 66)), "block table");
    }

    [Fact]
    public void BlockRowsThatAreWrongAreRefused()
    {
        // META is first, uncompressed and small.
        AssertRefused(With(f => "ZZZZ"u8.CopyTo(f.AsSpan(Row(0)))), "META");
        AssertRefused(With(f => f[Row(0) + 4] = 1), "compressed");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(Row(0) + 24), (ulong)BinaryAsset.MaxMetaBytes + 1)), "META");
        AssertRefused(With(f => "PLN0"u8.CopyTo(f.AsSpan(Row(2)))), "two blocks");
        AssertRefused(With(f => f[Row(1)] = 7), "printable");
        AssertRefused(With(f => f[Row(1) + 7] = 1), "reserved");
        AssertRefused(With(f => f[Row(1) + 4] = 9), "codec");
        AssertRefused(With(f => f[Row(1) + 6] = 17), "aligned to 2^17");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(Row(1) + 8), BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(Row(1) + 8)) + 8)), "aligned");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(Row(1) + 36), 4)), "element size");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(Row(3) + 36), 6)), "vertex size");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(Row(4) + 36), 3)), "index size");
        AssertRefused(With(f => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(Row(1) + 24), 5001)), "stored size");
    }

    [Fact]
    public void OversizedAndOutOfRangeBlocksAreRefusedWithoutAllocatingThem()
    {
        byte[] file = ValidFile();
        ulong[] huge = [ulong.MaxValue, ulong.MaxValue - 1, 1UL << 62, 1UL << 40, (ulong)BinaryAsset.MaxBlockBytes + 1, (ulong)file.Length + 1, int.MaxValue];
        foreach (int field in new[] { 8, 16, 24 }) // offset, stored size, raw size
        {
            foreach (ulong value in huge)
            {
                for (int block = 1; block < 5; block++)
                {
                    byte[] mutated = (byte[])file.Clone();
                    BinaryPrimitives.WriteUInt64LittleEndian(mutated.AsSpan(Row(block) + field), value);
                    Seal(mutated);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    AssertRefused(mutated);
                    Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4 * 1024 * 1024, $"field {field}, value {value}, block {block}");
                }
            }
        }
    }

    [Fact]
    public void ARawSizeThatTheFrameDoesNotHaveIsRefusedBeforeAnythingIsAllocated()
    {
        BinaryAsset valid = BinaryAsset.Read(ValidFile());
        long stored = valid.Blocks[2].StoredSize;
        ulong claim = (ulong)stored * 30000; // within what zstd could expand, but not what the frame says
        byte[] file = With(f => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(Row(2) + 24), claim));
        BinaryAsset asset = BinaryAsset.Read(file, "hostile.bin");

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ContentException>(() => asset.ReadBlock(BinaryAsset.Fourcc("ZST0")));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    [Fact]
    public void BlocksThatOverlapAreRefused()
    {
        BinaryAsset valid = BinaryAsset.Read(ValidFile());

        // Block 2 starts inside block 1 (at an aligned offset, so nothing else is wrong).
        AssertRefused(With(f => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(Row(2) + 8), (ulong)valid.Blocks[1].Offset + 4096)), "overlaps");

        // Block 1 starts on the header, or in the table, or on META (with an alignment that lets it).
        AssertRefused(With(f => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(Row(1) + 8), 0)), "overlaps");
        AssertRefused(With(f =>
        {
            f[Row(1) + 6] = 3;
            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(Row(1) + 8), 72);
        }), "overlaps");
        AssertRefused(With(f =>
        {
            f[Row(1) + 6] = 3;
            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(Row(1) + 8), (ulong)valid.Blocks[0].Offset);
        }), "overlaps");

        // Two blocks that start at the same place.
        AssertRefused(With(f => f.AsSpan(Row(1) + 8, 8).CopyTo(f.AsSpan(Row(2) + 8, 8))), "overlaps");

        // One that runs into the next.
        ulong longer = (ulong)(valid.Blocks[2].Offset - valid.Blocks[1].Offset + 8);
        AssertRefused(With(f =>
        {
            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(Row(1) + 16), longer);
            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(Row(1) + 24), longer);
        }), "overlaps");
    }

    [Fact]
    public void FuzzingAValidFileOnlyEverGivesAContentException()
    {
        byte[] valid = ValidFile();
        int tableEnd = Row(6);
        int tolerated = 0;
        for (int seed = 0; seed < 500; seed++)
        {
            var random = new Random(seed);
            byte[] file = (byte[])valid.Clone();
            int edits = 1 + random.Next(4);
            for (int edit = 0; edit < edits; edit++)
            {
                // Half of the edits go to the header and the table, where the numbers are.
                int at = Math.Min(random.Next(2) == 0 ? random.Next(tableEnd) : random.Next(file.Length), file.Length - 1);
                switch (random.Next(5))
                {
                    case 0:
                        file[at] ^= (byte)(1 << random.Next(8));
                        break;
                    case 1:
                        file[at] = (byte)random.Next(256);
                        break;
                    case 2 when at + 8 <= file.Length:
                        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(at), new[] { 0UL, ulong.MaxValue, 1UL << 31, 1UL << 32, (ulong)file.Length, 4096UL }[random.Next(6)]);
                        break;
                    case 3 when at + 4 <= file.Length:
                        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), new[] { 0u, uint.MaxValue, 1u << 31, 4096u, 4097u, 40u }[random.Next(6)]);
                        break;
                    default:
                        file = file[..Math.Max(1, at)];
                        break;
                }
            }

            if (seed % 2 == 0)
            {
                Seal(file); // so that the numbers of the table are judged, not the checksum
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                BinaryAsset asset = BinaryAsset.Read(file, "fuzz");
                for (int i = 0; i < asset.Blocks.Count; i++)
                {
                    try
                    {
                        asset.ReadBlock(i);
                    }
                    catch (ContentException)
                    {
                        // refused, which is the point
                    }
                }

                _ = asset.Uid;
                var text = new StringWriter();
                AssetDumpers.Dump(file, "fuzz", text);
                tolerated++;
            }
            catch (ContentException)
            {
                // refused
            }

            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 32 * 1024 * 1024, $"seed {seed} allocated a lot");
        }

        // Some mutations land in the padding or in an unused bit and change nothing that matters.
        Assert.InRange(tolerated, 0, 499);
    }

    // ---- The dump -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void DumpListsBlocks()
    {
        byte[] file = ValidFile();
        var text = new StringWriter();

        AssetDumpers.Dump(file, "crate.tsta", text);

        string dump = text.ToString();
        Assert.Contains("crate.tsta", dump, StringComparison.Ordinal);
        Assert.Contains("TSTA", dump, StringComparison.Ordinal);
        Assert.Contains("version 3", dump, StringComparison.Ordinal);
        Assert.Contains(s_uid.ToString(), dump, StringComparison.Ordinal);
        Assert.Contains(BinaryAsset.Read(file).ImportHashHex(), dump, StringComparison.Ordinal);
        Assert.Contains("0x00000005", dump, StringComparison.Ordinal);
        BinaryAsset asset = BinaryAsset.Read(file);
        foreach (BinaryBlock block in asset.Blocks)
        {
            string line = dump.Split('\n').Single(l => l.Contains($" {block.KindName} ", StringComparison.Ordinal));
            Assert.Contains(block.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture), line, StringComparison.Ordinal);
            Assert.Contains(block.Crc.ToString("x8", System.Globalization.CultureInfo.InvariantCulture), line, StringComparison.Ordinal);
            Assert.Contains(block.Codec.ToString(), line, StringComparison.Ordinal);
            Assert.EndsWith("ok", line.TrimEnd(), StringComparison.Ordinal);
        }

        // The format's own dumper writes META.
        Assert.Contains("width = 640", dump, StringComparison.Ordinal);
        Assert.Contains("height = 480", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void DumpSaysWhichBlockIsDamagedAndGoesOn()
    {
        BinaryAsset valid = BinaryAsset.Read(ValidFile());
        byte[] file = With(f => f[(int)valid.Blocks[1].Offset] ^= 1);
        var text = new StringWriter();

        AssetDumpers.Dump(file, "x", text);

        string[] lines = text.ToString().Split('\n');
        Assert.Contains("BAD", lines.Single(l => l.Contains(" PLN0 ", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.EndsWith("ok", lines.Single(l => l.Contains(" ZST0 ", StringComparison.Ordinal)).TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void DumpOfAFormatWithNoDumperStillListsTheBlocks()
    {
        var writer = new BinaryAssetWriter(BinaryAsset.Fourcc("NODP"), 1);
        writer.AddMeta(Meta());
        var text = new StringWriter();

        AssetDumpers.Dump(writer.ToArray(), "x", text);

        Assert.Contains("no dumper for 'NODP'", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("META", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DumpWritesCookedXmlAsXmlAndRefusesWhatItDoesNotKnow()
    {
        var node = XmlNode.Parse("<Scene version=\"1\"><Entity id=\"1\"/></Scene>", "s");
        var text = new StringWriter();

        AssetDumpers.Dump(XmlBinary.Write(node), "s.wscn", text);

        Assert.Contains("<Entity id=\"1\"/>", text.ToString(), StringComparison.Ordinal);
        Assert.Throws<ContentException>(() => AssetDumpers.Dump(Encoding.UTF8.GetBytes("hello, this is only text and it is long enough to have a header"), "t.txt", new StringWriter()));
    }

    [Fact]
    public void ADumperIsRegisteredOncePerMagic()
    {
        Assert.True(AssetDumpers.IsRegistered(s_magic));
        Assert.Throws<InvalidOperationException>(() => AssetDumpers.Register(s_magic, static (_, _) => { }));
    }

    // ---- The UID in the header ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheHeaderUidIsReadFromTheHeaderAndTheTableAlone()
    {
        byte[] file = ValidFile();

        Assert.Equal(s_uid, BinaryAsset.ReadUid(file));
        Assert.Equal(s_uid, BinaryAsset.ReadUid(file.AsSpan(0, 64 + (6 * 40))));
        Assert.Equal(s_uid, BinaryAsset.ReadUid(new MemoryStream(file)));
        Assert.True(BinaryAsset.IsContainer(file));

        // Without its table, or with a checksum that does not hold, a header gives nothing.
        Assert.Equal(AssetUid.None, BinaryAsset.ReadUid(file.AsSpan(0, 64)));
        Assert.Equal(AssetUid.None, BinaryAsset.ReadUid(file.AsSpan(0, 63)));
        Assert.Equal(AssetUid.None, BinaryAsset.ReadUid(new byte[100]));
        Assert.Equal(AssetUid.None, BinaryAsset.ReadUid(Encoding.UTF8.GetBytes("<Scene uid=\"uid://0123456789abc\"/>" + new string(' ', 80))));
        byte[] flipped = ValidFile();
        flipped[33] ^= 1;
        Assert.Equal(AssetUid.None, BinaryAsset.ReadUid(flipped));
        Assert.Equal(AssetUid.None, BinaryAsset.ReadUid(new MemoryStream(flipped)));
        Assert.Equal(AssetUid.None, BinaryAsset.ReadUid(new MemoryStream(file[..200])));

        var stream = new MemoryStream(file);
        Assert.Equal(s_uid, BinaryAsset.ReadUid(stream));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void IsContainerIsAsStrictAsTheHeaderOfRead()
    {
        Assert.True(BinaryAsset.IsContainer(ValidFile()));
        Assert.False(BinaryAsset.IsContainer(With(f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), 0))));
        Assert.False(BinaryAsset.IsContainer(With(f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), 4097))));
        Assert.False(BinaryAsset.IsContainer(With(f => f[60] = 1)));
        Assert.False(BinaryAsset.IsContainer(With(f => f[24] = 72)));
        Assert.False(BinaryAsset.IsContainer(With(f => f[6] = 0)));
    }

    [Fact]
    public void AuthoredAssetsReadsTheUidOfAContainerToo()
    {
        byte[] file = ValidFile();

        Assert.Equal(s_uid, AuthoredAssets.ReadUid(file));
        Assert.Equal(s_uid, AuthoredAssets.ReadUid(new MemoryStream(file)));
        Assert.Equal(AssetUid.None, AuthoredAssets.ReadUid(file.AsSpan(0, 10)));
        Assert.Equal(new AssetUid(0x0123), AuthoredAssets.ReadUid(Encoding.UTF8.GetBytes($"<Scene version=\"1\" uid=\"{new AssetUid(0x0123)}\"/>")));
    }

    [Fact]
    public void TheDatabaseReadsTheUidOfASubAssetThatHasNoSidecar()
    {
        using var folder = new TempFolder();
        var withUid = new BinaryAssetWriter(BinaryAsset.Fourcc("WMDL"), 2) { Uid = s_uid };
        withUid.AddMeta(Meta());
        var withoutUid = new BinaryAssetWriter(BinaryAsset.Fourcc("WMDL"), 2);
        withoutUid.AddMeta(Meta());
        Directory.CreateDirectory(Path.Combine(folder.Root, "imported", "models"));
        File.WriteAllBytes(Path.Combine(folder.Root, "imported", "models", "crate.wmdl"), withUid.ToArray());
        File.WriteAllBytes(Path.Combine(folder.Root, "imported", "models", "none.wmdl"), withoutUid.ToArray());
        byte[] old = [.. "WMDL"u8, 1, 0, 0, 0, .. new byte[100]];
        File.WriteAllBytes(Path.Combine(folder.Root, "imported", "models", "old.wmdl"), old);
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(folder.Root, writable: true));
        var database = new AssetDatabase(files);

        Assert.Equal(s_uid, database.UidOf("res://imported/models/crate.wmdl"));
        Assert.Equal(AssetUid.None, database.UidOf("res://imported/models/none.wmdl"));
        Assert.Equal(AssetUid.None, database.UidOf("res://imported/models/old.wmdl"));
        Assert.Equal(AssetUid.None, database.UidOf("res://imported/models/missing.wmdl"));
    }
}
