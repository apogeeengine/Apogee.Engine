using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Weft.Content.Formats;
using Weft.Content.Xml;
using Xunit;

namespace Weft.Content.Tests;

// The second half of BinaryAssetTests: what the codecs make and take, hostile rows, and --dump on hostile files.
public partial class BinaryAssetTests
{
    // ---- Codecs: what the writer makes the reader takes -----------------------------------------------------------------------------

    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(256)]
    public void ConstantAndMorphDeltaVertexStreamsRoundTrip(int stride)
    {
        // 64 K vertices that are all the same (a zeroed particle pool, a constant colour), and a morph-target delta: zero but for a few vertices.
        const int Count = 65536;
        byte[] constant = new byte[Count * stride];
        byte[] morph = new byte[Count * stride];
        var random = new Random(stride);
        for (int i = 0; i < 40; i++)
        {
            random.NextBytes(morph.AsSpan(random.Next(Count) * stride, stride));
        }

        foreach (byte[] raw in new[] { constant, morph })
        {
            var writer = new BinaryAssetWriter(s_magic, 1);
            writer.AddMeta(Meta());
            writer.AddBlock(BinaryAsset.Fourcc("VTX0"), raw, BlockCodec.MeshoptVertex, stride: stride);

            BinaryAsset asset = BinaryAsset.Read(writer.ToArray());

            Assert.Equal(raw, asset.ReadBlock(BinaryAsset.Fourcc("VTX0")).ToArray());
        }

        // The constant stream is the codec's best case: about a thousand to one for small strides.
        var best = new BinaryAssetWriter(s_magic, 1);
        best.AddMeta(Meta());
        best.AddBlock(BinaryAsset.Fourcc("VTX0"), constant, BlockCodec.MeshoptVertex, stride: stride);
        Assert.True(BinaryAsset.Read(best.ToArray()).Blocks[1].StoredSize < constant.Length / 100);
    }

    [Fact]
    public void ExtremeBlocksTheEncoderAcceptsAreOnesTheReaderTakes()
    {
        // Degenerate triangles (all the same index) are the index codec's best case: a byte for each triangle.
        uint[] zero = new uint[3 * 50_000];
        var indices = new BinaryAssetWriter(s_magic, 1);
        indices.AddMeta(Meta());
        indices.AddBlock(BinaryAsset.Fourcc("IDX0"), MemoryMarshal.AsBytes(zero.AsSpan()).ToArray(), BlockCodec.MeshoptIndex, stride: 4);
        Assert.Equal(zero.Length * 4, BinaryAsset.Read(indices.ToArray()).ReadBlock(BinaryAsset.Fourcc("IDX0")).Length);

        // No elements at all.
        var empty = new BinaryAssetWriter(s_magic, 1);
        empty.AddMeta(Meta());
        empty.AddBlock(BinaryAsset.Fourcc("VTX0"), ReadOnlyMemory<byte>.Empty, BlockCodec.MeshoptVertex, stride: 16);
        empty.AddBlock(BinaryAsset.Fourcc("IDX0"), ReadOnlyMemory<byte>.Empty, BlockCodec.MeshoptIndex, stride: 4);
        empty.AddBlock(BinaryAsset.Fourcc("ZST0"), ReadOnlyMemory<byte>.Empty, BlockCodec.Zstd);
        BinaryAsset asset = BinaryAsset.Read(empty.ToArray());
        Assert.All(asset.Blocks.Skip(1), b => Assert.Equal(0, asset.ReadBlock(b.Kind).Length));

        // Random vertices of every stride, from one vertex up.
        var random = new Random(7);
        foreach (int stride in new[] { 4, 8, 20, 36, 100, 256 })
        {
            foreach (int count in new[] { 1, 15, 16, 17, 255, 256, 257, 1000 })
            {
                byte[] raw = new byte[count * stride];
                random.NextBytes(raw);
                var writer = new BinaryAssetWriter(s_magic, 1);
                writer.AddMeta(Meta());
                writer.AddBlock(BinaryAsset.Fourcc("VTX0"), raw, BlockCodec.MeshoptVertex, stride: stride);
                Assert.Equal(raw, BinaryAsset.Read(writer.ToArray()).ReadBlock(BinaryAsset.Fourcc("VTX0")).ToArray());
            }
        }
    }

    [Fact]
    public void AZstdBlockOverFourMebibytesRoundTripsAndIsTheSameBytesEveryTime()
    {
        byte[] raw = new byte[5 * 1024 * 1024];
        var random = new Random(3);
        for (int i = 0; i < raw.Length; i += 16)
        {
            raw[i] = (byte)random.Next(4);
            raw[i + 5] = (byte)(i >> 12);
        }

        byte[] Make()
        {
            var writer = new BinaryAssetWriter(s_magic, 1) { ZstdLevel = 3 };
            writer.AddMeta(Meta());
            writer.AddBlock(BinaryAsset.Fourcc("BIG0"), raw, BlockCodec.Zstd);
            return writer.ToArray();
        }

        byte[] first = Make();

        Assert.Equal(first, Make());
        BinaryAsset asset = BinaryAsset.Read(first);
        Assert.Equal(raw, asset.ReadBlock(BinaryAsset.Fourcc("BIG0")).ToArray());
        Assert.True(asset.Blocks[1].StoredSize < raw.Length);
    }

    [Fact]
    public void AMeshBlockIsTheSameBytesWhateverElseTheProcessSet()
    {
        (byte[] vertices, byte[] indices) = Mesh();
        byte[] Make()
        {
            var writer = new BinaryAssetWriter(s_magic, 1);
            writer.AddMeta(Meta());
            writer.AddBlock(BinaryAsset.Fourcc("VTX0"), vertices, BlockCodec.MeshoptVertex, stride: 16);
            writer.AddBlock(BinaryAsset.Fourcc("IDX0"), indices, BlockCodec.MeshoptIndex, stride: 4);
            return writer.ToArray();
        }

        byte[] first = Make();
        Weft.Native.Meshopt.Meshopt.EncodeVertexVersion(0);
        try
        {
            Assert.Equal(first, Make());
        }
        finally
        {
            Weft.Native.Meshopt.Meshopt.EncodeVertexVersion(1);
        }
    }

    [Fact]
    public void TheWriterRefusesMagicsAndKindsThatAreNotFourPrintableCharacters()
    {
        Assert.Throws<ArgumentException>(() => new BinaryAssetWriter(1, 1));
        Assert.Throws<ArgumentException>(() => new BinaryAssetWriter(0, 1));
        Assert.Throws<ArgumentException>(() => new BinaryAssetWriter(0x7F414141, 1));
        var writer = new BinaryAssetWriter(s_magic, 1);
        writer.AddMeta(Meta());
        Assert.Throws<ArgumentException>(() => writer.AddBlock(7, s_plain));
        Assert.Throws<ArgumentException>(() => writer.AddBlock(0, s_plain));
        Assert.Throws<ArgumentException>(() => writer.AddBlock(BinaryAsset.MetaKind, s_plain));
        Assert.Throws<ArgumentException>(() => BinaryAsset.Fourcc("AéBC"));
    }

    // ---- Hostile input: what the header and the table promise -----------------------------------------------------------------------

    [Fact]
    public void AnyBitFlippedInTheHeaderOrTheTableIsRefused()
    {
        // The UID, the flags, the import hash, a row's flags and alignment, a size: nothing in the first 304 bytes can change without the file being refused.
        byte[] valid = ValidFile();
        for (int at = 0; at < 64 + (6 * 40); at++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] file = (byte[])valid.Clone();
                file[at] ^= (byte)(1 << bit);

                Assert.Throws<ContentException>(() => BinaryAsset.Read(file, "flipped.bin"));
            }
        }
    }

    [Fact]
    public void ABlockIsCheckedEveryTimeItIsReadNotOnlyTheFirst()
    {
        byte[] file = ValidFile();
        BinaryAsset asset = BinaryAsset.Read(file);
        foreach (string name in new[] { "PLN0", "ZST0", "VTX0", "IDX0" })
        {
            uint kind = BinaryAsset.Fourcc(name);
            _ = asset.ReadBlock(kind);
            Assert.True(asset.TryGetBlock(kind, out BinaryBlock block));
            int at = (int)(block.Offset + (block.StoredSize / 2));
            file[at] ^= 0x40; // the memory changes under a reader that has read the block once

            Assert.Throws<ContentException>(() => asset.ReadBlock(kind));
            file[at] ^= 0x40;
            _ = asset.ReadBlock(kind);
        }
    }

    [Fact]
    public void BlocksAreInTheOrderOfTheFileAndRowsThatAreNotAreRefused()
    {
        BinaryAsset valid = BinaryAsset.Read(ValidFile());
        Assert.True(valid.Blocks.Zip(valid.Blocks.Skip(1), (a, b) => a.Offset + a.StoredSize <= b.Offset).All(x => x));
        AssertRefused(With(f =>
        {
            byte[] swap = f.AsSpan(Row(1), 40).ToArray();
            f.AsSpan(Row(2), 40).CopyTo(f.AsSpan(Row(1), 40));
            swap.CopyTo(f.AsSpan(Row(2), 40));
        }), "overlaps");
    }

    private static byte[] WithEmptyBlockAt(long offset)
    {
        // META, a block of 9 bytes (at 4096, to 4105) and an empty block that the table puts at the given place.
        byte[] data = new byte[9];
        byte[] file = Craft(new Crafted("DATA", BlockCodec.None, data, 9, Crc: Crc32C.Compute(data)), new Crafted("EMPT", BlockCodec.None, [], 0, Crc: 0, AlignmentLog2: 2));
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(Row(2) + 8), (ulong)offset);
        Seal(file);
        return file;
    }

    [Fact]
    public void AnEmptyBlockSitsInTheOrderOfTheFileAndInsideNoOtherBlock()
    {
        BinaryAsset ok = BinaryAsset.Read(WithEmptyBlockAt(4108));
        Assert.Equal(0, ok.ReadBlock(BinaryAsset.Fourcc("EMPT")).Length);

        AssertRefused(WithEmptyBlockAt(4100), "overlaps"); // inside DATA
        AssertRefused(WithEmptyBlockAt(80), "overlaps"); // inside the table
        AssertRefused(WithEmptyBlockAt(1 << 20), "cut short"); // past the end of the file
        AssertRefused(WithEmptyBlockAt(4105), "aligned"); // not at its alignment
    }

    [Fact]
    public void BlocksThatTogetherDecodeToTooMuchAreRefused()
    {
        // Gibibyte blocks from 40 KB each: every row is fine alone (zstd can do 32768 to 1).
        static Crafted Big(int i) => new($"BG{i}0", BlockCodec.Zstd, new byte[40000], BinaryAsset.MaxBlockBytes);
        Assert.Equal(3, BinaryAsset.Read(Craft(Big(1), Big(2), Big(3)), "hostile.bin").Blocks.Count - 1);
        AssertRefused(Craft(Big(1), Big(2), Big(3), Big(4)), "together");
    }

    [Fact]
    public void AZstdBlockWithMoreThanOneFrameOrAFrameThatLiesIsRefused()
    {
        byte[] raw = Enumerable.Range(0, 3000).Select(i => (byte)(i % 5)).ToArray();
        var writer = new BinaryAssetWriter(s_magic, 1) { ZstdLevel = 3 };
        writer.AddMeta(Meta());
        writer.AddBlock(BinaryAsset.Fourcc("ZST0"), raw, BlockCodec.Zstd);
        byte[] frame = BinaryAsset.Read(writer.ToArray()).StoredBytes(BinaryAsset.Fourcc("ZST0")).ToArray();
        uint crc = Crc32C.Compute(raw);

        Assert.Equal(raw, BinaryAsset.Read(Craft(new Crafted("ZST0", BlockCodec.Zstd, frame, raw.Length, Crc: crc))).ReadBlock(BinaryAsset.Fourcc("ZST0")).ToArray());

        // A skippable frame after the frame (magic 0x184D2A50, size 0) decodes as before, and nothing would check it.
        byte[] trailing = [.. frame, 0x50, 0x2A, 0x4D, 0x18, 0, 0, 0, 0];
        BinaryAsset withTrailing = BinaryAsset.Read(Craft(new Crafted("ZST0", BlockCodec.Zstd, trailing, raw.Length, Crc: crc)));
        Assert.Contains("exactly one", Assert.Throws<ContentException>(() => withTrailing.ReadBlock(BinaryAsset.Fourcc("ZST0"))).Message, StringComparison.Ordinal);

        // A frame that is cut, or that says more than it holds, is a ContentException and no big allocation.
        long before = GC.GetAllocatedBytesForCurrentThread();
        BinaryAsset cut = BinaryAsset.Read(Craft(new Crafted("ZST0", BlockCodec.Zstd, frame[..(frame.Length / 2)], raw.Length, Crc: crc)));
        Assert.Throws<ContentException>(() => cut.ReadBlock(BinaryAsset.Fourcc("ZST0")));
        BinaryAsset lying = BinaryAsset.Read(Craft(new Crafted("ZST0", BlockCodec.Zstd, frame, raw.Length * 5, Crc: crc)));
        Assert.Throws<ContentException>(() => lying.ReadBlock(BinaryAsset.Fourcc("ZST0")));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4 * 1024 * 1024);
    }

    /// <summary>The largest raw size, a whole number of elements, that a row may claim for <paramref name="stored"/> bytes of <paramref name="codec"/> by the rule the reader uses (at most 16 MiB).</summary>
    private static long GreediestRaw(BlockCodec codec, int stride, int stored)
    {
        long unit = codec switch
        {
            BlockCodec.MeshoptVertex => stride,
            BlockCodec.MeshoptIndex => stride * 3L,
            _ => 1,
        };
        long best = 0;
        foreach (long ratio in new long[] { 1, 2, 8, 12, 64, 128, 512, 1000, 32768 })
        {
            long candidate = Math.Min(stored * ratio, 16L * 1024 * 1024) / unit * unit;
            if (candidate > best && BlockCodecs.Check(codec, stored, candidate, stride) is null)
            {
                best = candidate;
            }
        }

        return best;
    }

    [Fact]
    public void RandomStoredBytesBehindARowAtTheLimitOfEveryCodecOnlyEverGiveAContentException()
    {
        // The row is valid and as greedy as the codec allows; what it holds is random, or real encoder output with a few bytes changed.
        (byte[] vertices, byte[] indices) = Mesh();
        var writer = new BinaryAssetWriter(s_magic, 1);
        writer.AddMeta(Meta());
        writer.AddBlock(BinaryAsset.Fourcc("VTX0"), vertices, BlockCodec.MeshoptVertex, stride: 16);
        writer.AddBlock(BinaryAsset.Fourcc("IDX0"), indices, BlockCodec.MeshoptIndex, stride: 4);
        writer.AddBlock(BinaryAsset.Fourcc("ZST0"), s_squeezable, BlockCodec.Zstd);
        BinaryAsset real = BinaryAsset.Read(writer.ToArray());
        var cases = new (BlockCodec Codec, int Stride, byte[] Good)[]
        {
            (BlockCodec.MeshoptVertex, 16, real.StoredBytes(BinaryAsset.Fourcc("VTX0")).ToArray()),
            (BlockCodec.MeshoptIndex, 4, real.StoredBytes(BinaryAsset.Fourcc("IDX0")).ToArray()),
            (BlockCodec.Zstd, 0, real.StoredBytes(BinaryAsset.Fourcc("ZST0")).ToArray()),
        };

        int decoded = 0;
        foreach ((BlockCodec codec, int stride, byte[] good) in cases)
        {
            var random = new Random((int)codec);
            for (int round = 0; round < 2000; round++)
            {
                byte[] stored = (byte[])good.Clone();
                if (round % 2 == 0)
                {
                    random.NextBytes(stored); // nothing like encoder output
                }
                else
                {
                    int edits = 1 + random.Next(4);
                    for (int edit = 0; edit < edits; edit++)
                    {
                        stored[random.Next(stored.Length)] = (byte)random.Next(256);
                    }
                }

                long raw = GreediestRaw(codec, stride, stored.Length);
                byte[] file = Craft(new Crafted("DATA", codec, stored, raw, stride, Crc: (uint)random.Next()));
                long before = GC.GetAllocatedBytesForCurrentThread();
                BinaryAsset asset = BinaryAsset.Read(file, "fuzz");
                try
                {
                    asset.ReadBlock(1);
                    decoded++;
                }
                catch (ContentException)
                {
                    // refused: the data does not decode, or its checksum is not the one in the row
                }

                Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < (raw * 3) + (4 * 1024 * 1024), $"{codec} round {round}: allocated a lot for {raw} bytes");
            }
        }

        Assert.Equal(0, decoded); // a random checksum never holds
    }

    // ---- --dump on hostile input ----------------------------------------------------------------------------------------------------

    [Fact]
    public void DumpDoesNotDecodeABlockThatWouldNeedAHugeAllocation()
    {
        // 100 MiB claimed from 4 KB of nothing: the row is allowed (zstd can do 32768 to 1), but a dump does not go and find out.
        byte[] file = Craft(new Crafted("BIG0", BlockCodec.Zstd, new byte[4000], 100L * 1024 * 1024));
        var text = new StringWriter();

        long before = GC.GetAllocatedBytesForCurrentThread();
        AssetDumpers.Dump(file, "big.bin", text);

        Assert.Contains("not checked", text.ToString(), StringComparison.Ordinal);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8 * 1024 * 1024);
    }

    [Fact]
    public void ADumperThatThrowsIsReportedAndTheDumpEnds()
    {
        uint magic = BinaryAsset.Fourcc("THRW");
        AssetDumpers.Register(magic, static (_, _) => throw new InvalidOperationException("hostile META"));
        var writer = new BinaryAssetWriter(magic, 1);
        writer.AddMeta(Meta());
        var text = new StringWriter();

        AssetDumpers.Dump(writer.ToArray(), "x", text);

        Assert.Contains("dumper of 'THRW' failed", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("hostile META", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CookedXmlIsDumpedWithItsControlCharactersEscapedAndWithinALimit()
    {
        var root = new XmlNode("Scene");
        root.SetAttribute("note", "before\u001B[31mred\u0007after");
        root.Children.Add(new XmlNode("Entity") { Text = "tab\there\u001B" });
        var text = new StringWriter();

        AssetDumpers.Dump(XmlBinary.Write(root), "s.wscn", text);

        string dump = text.ToString();
        Assert.DoesNotContain('\u001B', dump);
        Assert.DoesNotContain('\u0007', dump);
        Assert.Contains("&#x1B;", dump, StringComparison.Ordinal);
        Assert.Contains("&#x9;", dump, StringComparison.Ordinal);

        // One text of a mebibyte named by forty elements: a small file that would be forty mebibytes of XML.
        var big = new XmlNode("Scene");
        string shared = new('x', 1024 * 1024);
        for (int i = 0; i < 40; i++)
        {
            big.Children.Add(new XmlNode("Entity") { Text = shared });
        }

        byte[] cooked = XmlBinary.Write(big);
        Assert.True(cooked.Length < 2 * 1024 * 1024, $"the cooked file is {cooked.Length} bytes");
        Assert.Contains("more than", Assert.Throws<ContentException>(() => AssetDumpers.Dump(cooked, "big.wscn", new StringWriter())).Message, StringComparison.Ordinal);
    }

    // ---- A hostile pack -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void APackWhoseIndexSumWrapsAroundIsRefusedAndNotAnOutOfMemory()
    {
        using var folder = new TempFolder();
        byte[] header = new byte[Packs.PackFormat.HeaderSize + 64];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Packs.PackFormat.PlainMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), Packs.PackFormat.Version);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), 1UL << 62); // index offset
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), 1UL << 62); // index length: the sum is 2^63, which wraps to a negative number
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), 100);
        string path = Path.Combine(folder.Root, "hostile.wpak");
        File.WriteAllBytes(path, header);

        IOException e = Assert.Throws<IOException>(() => Packs.PackSource.Open(path).Dispose());

        Assert.Contains("index is not inside the file", e.Message, StringComparison.Ordinal);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)Packs.PackFormat.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), long.MaxValue);
        File.WriteAllBytes(path, header);
        Assert.Throws<IOException>(() => Packs.PackSource.Open(path).Dispose());
    }
}
