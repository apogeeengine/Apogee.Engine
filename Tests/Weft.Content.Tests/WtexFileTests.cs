using Weft.Core.Graphics;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Weft.Content.Assets;
using Weft.Content.Formats;
using Weft.Content.Import;
using Weft.Content.Textures;
using Weft.Core.Assets;
using Weft.Native.Ktx;
using Weft.Native.Tests;
using Xunit;
using KtxApi = Weft.Native.Ktx.Ktx;

namespace Weft.Content.Tests;

/// <summary>The texture file (<see cref="WtexFile"/>): its layout on the container, hostile input, the conversion to and from KTX 2, and the dump.</summary>
public unsafe class WtexFileTests
{
    private static readonly AssetUid s_uid = new(0x0123456789ABCDEF);

    private static byte[] Random(int length, int seed)
    {
        var random = new System.Random(seed);
        byte[] data = new byte[length];
        random.NextBytes(data);
        return data;
    }

    private static WtexInfo Info(int width, int height, int mips, WtexPayload payload = WtexPayload.Rgba8, WtexTraits flags = WtexTraits.None, int depth = 1, int layers = 1, int faces = 1)
    {
        if (payload == WtexPayload.Uastc)
        {
            flags |= WtexTraits.Portable;
        }

        if (payload is WtexPayload.Bc6hU or WtexPayload.Rgba16f)
        {
            flags |= WtexTraits.Hdr;
        }

        if (faces == 6)
        {
            flags |= WtexTraits.Cube;
        }

        if (layers > 1)
        {
            flags |= WtexTraits.Array;
        }

        return new WtexInfo { Width = width, Height = height, Depth = depth, Layers = layers, Faces = faces, MipCount = mips, Payload = payload, Flags = flags };
    }

    private static ReadOnlyMemory<byte>[] Levels(WtexInfo info, int seed = 1)
    {
        var levels = new ReadOnlyMemory<byte>[info.MipCount];
        for (int level = 0; level < levels.Length; level++)
        {
            levels[level] = Random((int)info.LevelSize(level), seed + level);
        }

        return levels;
    }

    private static WtexWriteOptions Options(int zstd = 12) => new() { Uid = s_uid, ImportHash = Random(32, 99), ZstdLevel = zstd };

    /// <summary>The shapes of the tests: a chain, a cube map, an array, a cube array, a 3D texture.</summary>
    public static TheoryData<WtexPayload, int, int, int, int, int, int> Shapes()
    {
        var data = new TheoryData<WtexPayload, int, int, int, int, int, int>();
        foreach (WtexPayload payload in Enum.GetValues<WtexPayload>())
        {
            data.Add(payload, 64, 32, 7, 1, 1, 1);      // a full chain
            data.Add(payload, 37, 91, 5, 1, 1, 1);      // sizes that are not a multiple of a block
            data.Add(payload, 1, 1, 1, 1, 1, 1);        // one texel
            data.Add(payload, 16, 16, 5, 1, 1, 6);      // a cube map
            data.Add(payload, 16, 8, 4, 1, 3, 1);       // an array
            data.Add(payload, 8, 8, 4, 1, 2, 6);        // a cube map array
        }

        data.Add(WtexPayload.Rgba8, 8, 8, 4, 8, 1, 1);  // 3D
        data.Add(WtexPayload.Bc7, 8, 4, 4, 6, 1, 1);
        return data;
    }

    // ---- Round trip and layout ------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ATextureIsReadBackExactly(WtexPayload payload, int width, int height, int mips, int depth, int layers, int faces)
    {
        WtexInfo info = Info(width, height, mips, payload, payload is WtexPayload.Uastc or WtexPayload.Rgba8 or WtexPayload.Bc7 ? WtexTraits.Srgb : WtexTraits.None, depth, layers, faces) with
        {
            Group = WtexGroup.Character,
            LodBias = -2,
            AlphaCutoff = 0.25f,
            Sampler = new SamplerData(TextureFilter.Nearest, false, TextureWrap.ClampToEdge, TextureWrap.MirroredRepeat),
        };
        ReadOnlyMemory<byte>[] levels = Levels(info);

        byte[] file = WtexFile.Write(info, levels, Options());
        WtexFile back = WtexFile.Read(file, "t.wtex");

        Assert.True(WtexFile.IsWtex(file));
        Assert.Equal(info, back.Info);
        Assert.Equal(s_uid, back.Uid);
        Assert.Equal(Options().ImportHash.Span[..16].ToArray(), back.ImportHash.ToArray());
        Assert.Equal(s_uid, BinaryAsset.ReadUid(file)); // the UID is where AssetDatabase reads it
        Assert.Equal((uint)info.Flags, back.Container.Flags);
        ReadOnlyMemory<byte>[] all = back.ReadLevels();
        for (int level = 0; level < levels.Length; level++)
        {
            Assert.Equal(levels[level].ToArray(), back.ReadLevel(level).ToArray());
            Assert.Equal(levels[level].ToArray(), all[level].ToArray());
        }
    }

    [Fact]
    public void TheFormatHasItsRowInTheTable()
    {
        Assert.Equal(".wtex", AssetFormats.Texture.Extension);
        Assert.Equal(WtexFile.Magic, AssetFormats.Texture.Magic);
        Assert.Equal(WtexFile.FormatVersion, AssetFormats.Texture.Version);
        Assert.Same(AssetFormats.Texture, AssetFormats.Find("res://imported/a.png.wtex"));
        Assert.True(AssetDumpers.IsRegistered(WtexFile.Magic));
        Assert.False(WtexFile.IsWtex(new byte[100]));
    }

    [Fact]
    public void TheSmallLevelsAreTheTailAndTheLargeOnesHaveABlockEach()
    {
        // 1024x1024 RGBA8: 4 MiB, 1 MiB, 256 KiB, 64 KiB, ... so the tail starts at level 3, the first of 64 KiB or less.
        WtexInfo info = Info(1024, 1024, 11);
        ReadOnlyMemory<byte>[] levels = Levels(info);

        byte[] file = WtexFile.Write(info, levels, Options());
        WtexFile back = WtexFile.Read(file);
        BinaryAsset container = back.Container;

        Assert.Equal(3, back.TailMip);
        Assert.Equal(3, info.DefaultTailMip);
        Assert.Equal(["META", "TAIL", "MIP2", "MIP1", "MIP0"], container.Blocks.Select(b => b.KindName));

        // The tail is the levels from the tail index on, the smallest first, with nothing between them.
        ReadOnlyMemory<byte> tail = back.ReadTail();
        long expected = Enumerable.Range(3, 8).Sum(info.LevelSize);
        Assert.Equal(expected, tail.Length);
        long cursor = 0;
        for (int level = 10; level >= 3; level--)
        {
            Assert.Equal(cursor, back.TailOffset(level));
            Assert.Equal(levels[level].ToArray(), tail.Slice((int)cursor, levels[level].Length).ToArray());
            cursor += levels[level].Length;
        }

        // The first level of the tail is at most 64 KiB and the level below it is more.
        Assert.True(info.LevelSize(3) <= WtexInfo.TailLevelBytes);
        Assert.True(info.LevelSize(2) > WtexInfo.TailLevelBytes);
    }

    [Fact]
    public void TheBlocksAreAlignedAndStoredAsTheDesignSays()
    {
        WtexInfo info = Info(1024, 1024, 11);
        byte[] file = WtexFile.Write(info, Levels(info), Options());
        BinaryAsset container = BinaryAsset.Read(file);

        Assert.Equal(BlockCodec.None, container.Blocks[0].Codec);
        Assert.Equal(BinaryAsset.MetaAlignmentLog2, container.Blocks[0].AlignmentLog2);
        Assert.Equal(WtexFile.MetaSize, container.Blocks[0].StoredSize);
        Assert.Equal(BlockCodec.None, container.Blocks[1].Codec);       // the tail is read in one go and mapped: not compressed
        for (int i = 1; i < container.Blocks.Count; i++)
        {
            BinaryBlock block = container.Blocks[i];
            Assert.Equal(0, block.Offset % 4096);                       // a streamer reads or maps them
            Assert.Equal(12, block.AlignmentLog2);
            Assert.Equal(0, block.Flags);
        }

        for (int i = 2; i < container.Blocks.Count; i++)
        {
            Assert.Equal(BlockCodec.Zstd, container.Blocks[i].Codec);
        }

        // Smallest first in the file: the tail, then the larger levels in order of size.
        Assert.True(container.Blocks.Zip(container.Blocks.Skip(1)).All(pair => pair.First.Offset < pair.Second.Offset));

        byte[] plain = WtexFile.Write(info, Levels(info), Options(zstd: 0));
        Assert.All(BinaryAsset.Read(plain).Blocks, block => Assert.Equal(BlockCodec.None, block.Codec));
    }

    [Theory]
    [InlineData(WtexPayload.Rgba8)]
    [InlineData(WtexPayload.Bc7)]
    [InlineData(WtexPayload.Bc4)]
    [InlineData(WtexPayload.Rgba16f)]
    [InlineData(WtexPayload.Uastc)]
    public void EveryLevelOfTheTailStartsWhereAGpuCanReadItFrom(WtexPayload payload)
    {
        // A buffer offset for an upload is a multiple of the size of a texel block (and of four); with no padding between the levels of the tail it is one because every level is whole blocks.
        WtexInfo info = Info(512, 256, 10, payload);
        WtexFile back = WtexFile.Read(WtexFile.Write(info, Levels(info), Options()));
        int block = WtexInfo.BlockOf(payload).Bytes;

        for (int level = back.TailMip; level < info.MipCount; level++)
        {
            Assert.Equal(0, back.TailOffset(level) % block);
            Assert.Equal(0, back.TailOffset(level) % 4);
        }
    }

    [Fact]
    public void ASingleLargeLevelStillHasATail()
    {
        WtexInfo info = Info(512, 512, 1); // 1 MiB and no smaller level
        WtexFile back = WtexFile.Read(WtexFile.Write(info, Levels(info), Options()));

        Assert.Equal(0, back.TailMip);
        Assert.Equal(["META", "TAIL"], back.Container.Blocks.Select(b => b.KindName));
        Assert.Equal(Levels(info)[0].ToArray(), back.ReadLevel(0).ToArray());
    }

    [Fact]
    public void ASmallTextureIsAllTail()
    {
        WtexInfo info = Info(64, 64, 7);
        WtexFile back = WtexFile.Read(WtexFile.Write(info, Levels(info), Options()));

        Assert.Equal(0, back.TailMip);
        Assert.Equal(["META", "TAIL"], back.Container.Blocks.Select(b => b.KindName));
    }

    [Fact]
    public void MipBlocksUseOneCharacterForTheirNumber()
    {
        Assert.Equal("MIP0", BinaryAsset.FourccText(WtexFile.MipKind(0)));
        Assert.Equal("MIP9", BinaryAsset.FourccText(WtexFile.MipKind(9)));
        Assert.Equal("MIPA", BinaryAsset.FourccText(WtexFile.MipKind(10)));
        Assert.Equal("MIPE", BinaryAsset.FourccText(WtexFile.MipKind(14)));
        Assert.Throws<ArgumentOutOfRangeException>(() => WtexFile.MipKind(15));
        Assert.Throws<ArgumentOutOfRangeException>(() => WtexFile.MipKind(-1));

        // 16384x16384 has fifteen levels; level 14 is the last one, and it is in the tail. A level with a letter for a number is a block too.
        WtexInfo info = Info(16384, 1, 15, WtexPayload.Bc4);
        WtexFile back = WtexFile.Read(WtexFile.Write(info, Levels(info), Options(zstd: 1)));
        Assert.Equal(info, back.Info);
    }

    [Fact]
    public void TheSameTextureMakesTheSameBytes()
    {
        WtexInfo info = Info(300, 200, 9, WtexPayload.Bc7, WtexTraits.Srgb);
        ReadOnlyMemory<byte>[] levels = Levels(info);

        byte[] first = WtexFile.Write(info, levels, Options());
        byte[] second = WtexFile.Write(info with { }, levels, Options());

        Assert.Equal(first, second);
        // Writing what was read makes the same file again.
        WtexFile back = WtexFile.Read(first);
        Assert.Equal(first, WtexFile.Write(back.Info, back.ReadLevels(), Options()));
        // The UID and the import hash are in it and nothing else changes with them.
        Assert.NotEqual(first, WtexFile.Write(info, levels, new WtexWriteOptions()));
    }

    [Fact]
    public void TheWriterRefusesWhatAReaderWouldRefuse()
    {
        WtexInfo ok = Info(8, 8, 4);
        ReadOnlyMemory<byte>[] levels = Levels(ok);

        Assert.Throws<ArgumentException>(() => WtexFile.Write(ok, levels[..3]));                                       // a level missing
        Assert.Throws<ArgumentException>(() => WtexFile.Write(ok, [levels[0], levels[1], levels[2], new byte[3]]));    // a level of the wrong size
        Assert.Throws<ArgumentException>(() => WtexFile.Write(ok with { Width = 0 }, levels));
        Assert.Throws<ArgumentException>(() => WtexFile.Write(ok with { MipCount = 5 }, levels));                       // more than a chain
        Assert.Throws<ArgumentException>(() => WtexFile.Write(ok with { Flags = WtexTraits.Cube }, levels));            // a cube map with one face
        Assert.Throws<ArgumentException>(() => WtexFile.Write(ok with { Payload = WtexPayload.Uastc }, levels));        // UASTC without the portable flag
        Assert.Throws<ArgumentException>(() => WtexFile.Write(ok with { AlphaCutoff = float.NaN }, levels));
        Assert.Throws<ArgumentException>(() => WtexFile.Write(ok, levels, new WtexWriteOptions { ZstdLevel = 23 }));
        Assert.Throws<ArgumentException>(() => WtexFile.Write(ok, levels, new WtexWriteOptions { ImportHash = new byte[4] }));
    }

    [Fact]
    public void EveryFlagMeansSomething()
    {
        // The combinations the check refuses, one by one.
        WtexInfo ok = Info(8, 8, 1);
        Assert.Null(ok.Check());
        Assert.Null((ok with { Flags = WtexTraits.Srgb | WtexTraits.Premultiplied | WtexTraits.AlphaCoverage }).Check());
        Assert.Null((ok with { Flags = WtexTraits.NormalMapRG | WtexTraits.Portable }).Check());
        Assert.NotNull((ok with { Flags = WtexTraits.Srgb | WtexTraits.NormalMapRG }).Check());       // a normal map is not colour
        Assert.NotNull((ok with { Flags = WtexTraits.Hdr }).Check());                                  // not floats
        Assert.NotNull((ok with { Payload = WtexPayload.Rgba16f }).Check());                           // floats and not flagged
        Assert.NotNull((ok with { Payload = WtexPayload.Bc5, Flags = WtexTraits.Srgb }).Check());      // no sRGB BC5
        Assert.Null((ok with { Flags = WtexTraits.Array }).Check());                                   // an array of one layer is an array
        Assert.NotNull((ok with { Layers = 2 }).Check());                                              // two layers and not an array
    }

    // ---- Hostile input --------------------------------------------------------------------------------------------------------------

    /// <summary>The <c>META</c> of the specification, written here on its own so that a test can change a field.</summary>
    private static byte[] Meta(WtexInfo info, int tail, Action<byte[]>? edit = null)
    {
        byte[] meta = new byte[WtexFile.MetaSize];
        BinaryPrimitives.WriteUInt32LittleEndian(meta, (uint)info.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(4), (uint)info.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(8), (uint)info.Depth);
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(12), (uint)info.Layers);
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(16), (uint)info.Faces);
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(20), (uint)info.MipCount);
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(24), (uint)info.Flags);
        meta[28] = (byte)info.Payload;
        meta[29] = (byte)info.Group;
        meta[30] = (byte)info.LodBias;
        meta[31] = (byte)tail;
        BinaryPrimitives.WriteSingleLittleEndian(meta.AsSpan(32), info.AlphaCutoff);
        meta[36] = (byte)info.Sampler.Filter;
        meta[37] = (byte)(info.Sampler.Mipmaps ? 1 : 0);
        meta[38] = (byte)info.Sampler.WrapU;
        meta[39] = (byte)info.Sampler.WrapV;
        edit?.Invoke(meta);
        return meta;
    }

    /// <summary>A file made by hand on the container: the given META, a TAIL of the right size for <paramref name="info"/> and a block for each level below the tail, unless a test asks otherwise.</summary>
    private static byte[] Hand(WtexInfo info, int tail, Action<byte[]>? editMeta = null, Action<BinaryAssetWriter>? editWriter = null, Func<IEnumerable<(uint Kind, int Size)>, IEnumerable<(uint Kind, int Size)>>? editBlocks = null, int formatVersion = 1)
    {
        var writer = new BinaryAssetWriter(WtexFile.Magic, formatVersion) { Flags = (uint)info.Flags };
        editWriter?.Invoke(writer);
        writer.AddMeta(Meta(info, tail, editMeta));
        var blocks = new List<(uint Kind, int Size)> { (WtexFile.TailKind, (int)Enumerable.Range(tail, info.MipCount - tail).Sum(info.LevelSize)) };
        for (int level = tail - 1; level >= 0; level--)
        {
            blocks.Add((WtexFile.MipKind(level), (int)info.LevelSize(level)));
        }

        foreach ((uint kind, int size) in editBlocks is null ? blocks : editBlocks(blocks))
        {
            writer.AddBlock(kind, new byte[size]);
        }

        return writer.ToArray();
    }

    private static void Refused(byte[] file, string contains)
    {
        ContentException e = Assert.Throws<ContentException>(() => WtexFile.Read(file, "hostile.wtex"));
        Assert.Contains(contains, e.Message, StringComparison.Ordinal);
        Assert.Contains("hostile.wtex", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileMadeByHandIsReadWhenItIsRight()
    {
        WtexInfo info = Info(64, 64, 7);

        WtexFile back = WtexFile.Read(Hand(info, 0));

        Assert.Equal(info, back.Info);
        Assert.Equal(new byte[info.LevelSize(0)], back.ReadLevel(0).ToArray());
    }

    [Theory]
    [InlineData(0, 8, "texels")]
    [InlineData(8, 0, "texels")]
    [InlineData(16385, 8, "texels")]
    [InlineData(int.MaxValue, 8, "texels")]
    public void SizesOutsideTheLimitsAreRefused(int width, int height, string message)
    {
        WtexInfo info = Info(8, 8, 1);
        Refused(Hand(info, 0, meta =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(meta, (uint)width);
            BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(4), (uint)height);
        }, editBlocks: blocks => blocks), message);
    }

    [Fact]
    public void EveryFieldOfMetaIsJudged()
    {
        WtexInfo info = Info(8, 8, 4);

        void Case(Action<byte[]> edit, string message) => Refused(Hand(info, 0, edit), message);

        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(8), 0), "depth");
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(8), 2049), "depth");
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(8), uint.MaxValue), "depth");
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(12), 0), "layers");
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(12), 2049), "layers");
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(12), 3), "not an array");
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(16), 0), "faces");
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(16), 2), "faces");
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(16), 6), "six faces");
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(20), 0), "mip");
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(20), 5), "mip");                // 8x8 has four
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(20), uint.MaxValue), "mip");
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(24), 0x100), "flags");
        Case(meta => BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(24), (uint)WtexTraits.Hdr), "high dynamic range");
        Case(meta => meta[28] = 7, "payload");
        Case(meta => meta[28] = (byte)WtexPayload.Uastc, "UASTC");
        Case(meta => meta[29] = 5, "group");
        Case(meta => meta[31] = 4, "tail");
        Case(meta => meta[31] = 255, "tail");
        Case(meta => BinaryPrimitives.WriteSingleLittleEndian(meta.AsSpan(32), float.NaN), "alpha cutoff");
        Case(meta => BinaryPrimitives.WriteSingleLittleEndian(meta.AsSpan(32), 1.5f), "alpha cutoff");
        Case(meta => BinaryPrimitives.WriteSingleLittleEndian(meta.AsSpan(32), -0.1f), "alpha cutoff");
        Case(meta => meta[36] = 2, "sampler");
        Case(meta => meta[37] = 2, "sampler");
        Case(meta => meta[38] = 3, "sampler");
        Case(meta => meta[39] = 200, "sampler");
        Case(meta => meta[40] = 1, "reserved");
        Case(meta => meta[47] = 1, "reserved");
    }

    [Fact]
    public void ACubeMapIsSquareAndATextureIsNotBothThreeDimensionalAndAnArray()
    {
        WtexInfo cube = Info(16, 8, 4, faces: 6);
        Refused(Hand(cube, 0), "square");
        WtexInfo flat = Info(8, 8, 4);
        Refused(Hand(flat, 0, meta =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(8), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(12), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(24), (uint)WtexTraits.Array);
        }), "3D");
    }

    [Fact]
    public void ATextureLargerThanTheLimitsIsRefusedBeforeAnythingIsAllocated()
    {
        // A cube map of 16384 texels with half floats would be 12 GiB in its first level; the file is a few hundred bytes.
        WtexInfo big = Info(16384, 16384, 15, WtexPayload.Rgba16f, faces: 6);
        byte[] file = Hand(Info(8, 8, 1), 0, meta =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(meta, 16384);
            BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(4), 16384);
            BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(16), 6);
            BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(20), 15);
            BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(24), (uint)big.Flags);
            meta[28] = (byte)WtexPayload.Rgba16f;
        }, writer => writer.Flags = (uint)big.Flags);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Refused(file, "first level");

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
        Assert.NotNull(big.Check());
    }

    [Fact]
    public void TheHeaderAndMetaAgreeAboutTheFlags()
    {
        WtexInfo info = Info(8, 8, 4);
        Refused(Hand(info, 0, editWriter: writer => writer.Flags = (uint)WtexTraits.Srgb), "flags in its header");
    }

    [Fact]
    public void AnotherVersionOrMagicIsRefusedWithWhatToDo()
    {
        WtexInfo info = Info(8, 8, 4);

        Refused(Hand(info, 0, formatVersion: 2), "Update the engine");
        Refused(Hand(info, 0, formatVersion: 3), "version 3");
        Assert.Throws<ContentException>(() => WtexFile.Read(new byte[200]));

        var other = new BinaryAssetWriter(BinaryAsset.Fourcc("WMDL"), 1);
        other.AddMeta(new byte[WtexFile.MetaSize]);
        Refused(other.ToArray(), "not a 'WTEX'");
        Assert.Throws<ContentException>(() => WtexFile.FromContainer(BinaryAsset.Read(other.ToArray())));
    }

    [Fact]
    public void MetaOfAnotherSizeIsRefused()
    {
        WtexInfo info = Info(8, 8, 4);
        foreach (int size in new[] { 0, 1, 47, 49, 64, 4096 })
        {
            var writer = new BinaryAssetWriter(WtexFile.Magic, 1) { Flags = (uint)info.Flags };
            writer.AddMeta(new byte[size]);
            Refused(writer.ToArray(), "META");
        }
    }

    [Fact]
    public void TheBlocksMustBeExactlyTheOnesMetaImplies()
    {
        WtexInfo info = Info(64, 64, 7);
        int tail0Size = (int)Enumerable.Range(0, 7).Sum(info.LevelSize);

        Refused(Hand(info, 0, editBlocks: blocks => blocks.Skip(1)), "blocks");                                         // no TAIL
        Refused(Hand(info, 0, editBlocks: blocks => [.. blocks, (WtexFile.MipKind(0), 16)]), "blocks");                  // one more
        Refused(Hand(info, 2, editBlocks: blocks => blocks.Take(1)), "blocks");                                         // the tail at 2 asks for two more
        Refused(Hand(info, 0, editBlocks: blocks => [(WtexFile.TailKind, tail0Size - 4)]), "needs");                     // a short TAIL
        Refused(Hand(info, 0, editBlocks: blocks => [(WtexFile.TailKind, tail0Size + 4)]), "needs");                     // a long one
        Refused(Hand(info, 0, editBlocks: blocks => [(BinaryAsset.Fourcc("DATA"), tail0Size)]), "'TAIL'");               // another name
        Refused(Hand(info, 2, editBlocks: blocks => blocks.Reverse()), "belongs");                                       // another order
        Refused(Hand(info, 1, editBlocks: blocks => blocks.Select(b => b.Kind == WtexFile.MipKind(0) ? (b.Kind, b.Size + 1) : b)), "needs"); // a level of the wrong size
        Refused(Hand(info, 1, editBlocks: blocks => blocks.Select(b => b.Kind == WtexFile.MipKind(0) ? (WtexFile.MipKind(1), b.Size) : b)), "belongs"); // the wrong number
    }

    [Fact]
    public void ABlockMustBeStoredAsAPixelBlockIs()
    {
        WtexInfo info = Info(64, 64, 7);
        int size = (int)Enumerable.Range(0, 7).Sum(info.LevelSize);

        // The mesh codecs are not for pixels.
        var meshopt = new BinaryAssetWriter(WtexFile.Magic, 1) { Flags = (uint)info.Flags };
        meshopt.AddMeta(Meta(info, 0));
        meshopt.AddBlock(WtexFile.TailKind, new byte[size - (size % 16)], BlockCodec.MeshoptVertex, stride: 16);
        Refused(meshopt.ToArray(), "codec");

        // Flags and a small alignment are not written.
        var flagged = new BinaryAssetWriter(WtexFile.Magic, 1) { Flags = (uint)info.Flags };
        flagged.AddMeta(Meta(info, 0));
        flagged.AddBlock(WtexFile.TailKind, new byte[size], flags: 1);
        Refused(flagged.ToArray(), "flags");

        var packed = new BinaryAssetWriter(WtexFile.Magic, 1) { Flags = (uint)info.Flags };
        packed.AddMeta(Meta(info, 0));
        packed.AddBlock(WtexFile.TailKind, new byte[size], alignmentLog2: 4);
        Refused(packed.ToArray(), "aligned");
    }

    [Fact]
    public void ADamagedBlockIsFoundWhenItIsRead()
    {
        WtexInfo info = Info(1024, 1024, 11);
        byte[] file = WtexFile.Write(info, Levels(info), Options(zstd: 0));
        WtexFile back = WtexFile.Read(file);
        BinaryBlock mip0 = back.Container.Blocks.Single(b => b.KindName == "MIP0");
        file[mip0.Offset + 100] ^= 1;

        WtexFile damaged = WtexFile.Read(file);

        Assert.Equal(Levels(info)[1].ToArray(), damaged.ReadLevel(1).ToArray()); // the other levels are fine
        Assert.Throws<ContentException>(() => damaged.ReadLevel(0));
        Assert.Throws<ContentException>(() => damaged.ReadLevels());
    }

    [Fact]
    public void ALevelOutsideTheTextureIsAnArgumentError()
    {
        WtexInfo info = Info(8, 8, 4);
        WtexFile back = WtexFile.Read(WtexFile.Write(info, Levels(info), Options()));

        Assert.Throws<ArgumentOutOfRangeException>(() => back.ReadLevel(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => back.ReadLevel(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => back.TailOffset(back.TailMip - 1));
    }

    [Fact]
    public void FuzzingAValidFileOnlyEverGivesAContentException()
    {
        // A texture with mip blocks, a tail, zstd and a cube map, so that every kind of block and number is there to be damaged.
        WtexInfo info = Info(256, 256, 9, WtexPayload.Bc7, WtexTraits.Srgb, faces: 6);
        byte[] valid = WtexFile.Write(info, Levels(info), Options(zstd: 3));
        int tableEnd = BinaryAsset.HeaderSize + (BinaryAsset.Read(valid).Blocks.Count * BinaryAsset.BlockRowSize);
        int metaStart = (int)BinaryAsset.Read(valid).Blocks[0].Offset;
        int tolerated = 0;
        for (int seed = 0; seed < 500; seed++)
        {
            var random = new System.Random(seed);
            byte[] file = (byte[])valid.Clone();
            int edits = 1 + random.Next(4);
            for (int edit = 0; edit < edits; edit++)
            {
                // Most edits land where the numbers are: the header, the table and META.
                int at = random.Next(3) switch
                {
                    0 => random.Next(tableEnd),
                    1 => metaStart + random.Next(WtexFile.MetaSize),
                    _ => random.Next(file.Length),
                };
                at = Math.Min(at, file.Length - 1);
                switch (random.Next(5))
                {
                    case 0:
                        file[at] ^= (byte)(1 << random.Next(8));
                        break;
                    case 1:
                        file[at] = (byte)random.Next(256);
                        break;
                    case 2 when at + 4 <= file.Length:
                        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), new[] { 0u, uint.MaxValue, 1u << 31, 4096u, 16384u, 16385u, 6u }[random.Next(7)]);
                        break;
                    case 3 when at + 8 <= file.Length:
                        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(at), new[] { 0UL, ulong.MaxValue, 1UL << 32, (ulong)file.Length }[random.Next(4)]);
                        break;
                    default:
                        file = file[..Math.Max(1, at)];
                        break;
                }
            }

            if (seed % 2 == 0)
            {
                SealContainer(file); // so that the numbers are judged and not the checksum of the table
            }

            if (seed % 4 == 0 && file.Length >= metaStart + WtexFile.MetaSize)
            {
                SealMeta(file, metaStart); // and so that META is judged and not its checksum
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                WtexFile texture = WtexFile.Read(file, "fuzz");
                try
                {
                    _ = texture.ReadLevels();
                }
                catch (ContentException)
                {
                    // refused, which is the point
                }

                Assert.Null(texture.Info.Check());
                var text = new StringWriter();
                AssetDumpers.Dump(file, "fuzz", text);
                tolerated++;
            }
            catch (ContentException)
            {
                // refused
            }

            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024 * 1024, $"seed {seed} allocated a lot");
        }

        Assert.InRange(tolerated, 0, 499);
    }

    private static void SealContainer(byte[] file)
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

    /// <summary>Makes the CRC of META in the first row right again (META is block 0).</summary>
    private static void SealMeta(byte[] file, int metaStart)
    {
        if (BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(64 + 8)) == (ulong)metaStart && BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(64 + 16)) == WtexFile.MetaSize)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(64 + 32), Crc32C.Compute(file.AsSpan(metaStart, WtexFile.MetaSize)));
            SealContainer(file);
        }
    }

    // ---- The dump -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDumpListsTheFieldsAndWhereEachLevelIs()
    {
        WtexInfo info = Info(1024, 512, 11, WtexPayload.Bc7, WtexTraits.Srgb | WtexTraits.AlphaCoverage) with { Group = WtexGroup.Ui, LodBias = -1, AlphaCutoff = 0.75f };
        byte[] file = WtexFile.Write(info, Levels(info), Options());
        var text = new StringWriter();

        AssetDumpers.Dump(file, "icon.png.wtex", text);

        string dump = text.ToString();
        Assert.Contains("WTEX (Imported texture), version 1", dump, StringComparison.Ordinal);
        Assert.Contains(s_uid.ToString(), dump, StringComparison.Ordinal);
        Assert.Contains("TAIL", dump, StringComparison.Ordinal);
        foreach (string line in new[]
        {
            "width = 1024", "height = 512", "depth = 1", "layers = 1", "faces = 1", "mips = 11", "payload = Bc7", "vk_format = 146", "form = gpu",
            "flags = Srgb | AlphaCoverage", "group = Ui", "lod_bias = -1", "alpha_cutoff = 0.75", "sampler = Linear, mipmaps on, wrap Repeat Repeat", "tail_mip = 2",
            "level 0 = 1024x512, 524288 bytes, in MIP0", "level 2 = 256x128, 32768 bytes, in TAIL+",
        })
        {
            Assert.Contains(line, dump, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("failed", dump, StringComparison.Ordinal);
        Assert.Equal(dump, Dump(file));                                // the same text again
        Assert.Contains("form = portable", Dump(WtexFile.Write(Info(8, 8, 4, WtexPayload.Uastc), Levels(Info(8, 8, 4, WtexPayload.Uastc)), Options())), StringComparison.Ordinal);
    }

    private static string Dump(byte[] file)
    {
        var text = new StringWriter();
        AssetDumpers.Dump(file, "icon.png.wtex", text);
        return text.ToString();
    }

    [Fact]
    public void TheDumpOfATextureThatIsNotOneSaysSoAndKeepsGoing()
    {
        WtexInfo info = Info(8, 8, 4);
        string dump = Dump(Hand(info, 0, meta => BinaryPrimitives.WriteUInt32LittleEndian(meta, 0)));

        Assert.Contains("invalid = ", dump, StringComparison.Ordinal);
        Assert.Contains("TAIL", dump, StringComparison.Ordinal);   // the container's part is there
        Assert.DoesNotContain("failed", dump, StringComparison.Ordinal);
    }

    // ---- KTX 2 ----------------------------------------------------------------------------------------------------------------------

    private static Texture2* Load(byte[] ktx2)
    {
        Texture2* texture;
        Result result;
        fixed (byte* data = ktx2)
        {
            result = KtxApi.Texture2CreateFromMemory(data, (nuint)ktx2.Length, TextureCreateFlagBits.LoadImageData, &texture);
        }

        Assert.Equal(Result.Success, result);
        return texture;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ToKtx2LoadsInLibktx(WtexPayload payload, int width, int height, int mips, int depth, int layers, int faces)
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
        bool colour = payload is WtexPayload.Uastc or WtexPayload.Rgba8 or WtexPayload.Bc7;
        WtexInfo info = Info(width, height, mips, payload, (colour ? WtexTraits.Srgb : WtexTraits.None) | (payload == WtexPayload.Rgba8 ? WtexTraits.Premultiplied : WtexTraits.None), depth, layers, faces);
        WtexFile wtex = WtexFile.Read(WtexFile.Write(info, Levels(info), Options()));

        byte[] ktx2 = wtex.ToKtx2();
        Texture2* texture = Load(ktx2);
        try
        {
            Assert.Equal((uint)width, texture->BaseWidth);
            Assert.Equal((uint)height, texture->BaseHeight);
            Assert.Equal((uint)depth, texture->BaseDepth);
            Assert.Equal((uint)mips, texture->NumLevels);
            Assert.Equal((uint)layers, texture->NumLayers);
            Assert.Equal((uint)faces, texture->NumFaces);
            Assert.Equal(faces == 6, (bool)texture->IsCubemap);
            Assert.Equal(layers > 1, (bool)texture->IsArray);
            Assert.Equal(info.VkFormat, texture->VkFormat);
            Assert.Equal(payload == WtexPayload.Uastc, (bool)KtxApi.Texture2NeedsTranscoding(texture));
            Assert.Equal(SupercmpScheme.None, texture->SupercompressionScheme);
            Assert.Equal(colour, KtxApi.Texture2GetTransferFunction(texture) == KhrDfTransfer.Srgb);
            Assert.Equal(payload == WtexPayload.Rgba8, (bool)KtxApi.Texture2GetPremultipliedAlpha(texture));
            if (payload == WtexPayload.Uastc)
            {
                Assert.Equal(KhrDfModel.Uastc, KtxApi.Texture2GetColorModel(texture));
            }

            for (int level = 0; level < mips; level++)
            {
                nuint offset;
                Assert.Equal(Result.Success, KtxApi.Texture2GetImageOffset(texture, (uint)level, 0, 0, &offset));
                Assert.Equal((nuint)info.LevelSize(level), texture->Vtbl->GetLevelSize((Texture*)texture, (uint)level));
                Assert.Equal(wtex.ReadLevel(level).ToArray(), new ReadOnlySpan<byte>(texture->PData + offset, (int)info.LevelSize(level)).ToArray());
            }
        }
        finally
        {
            KtxApi.Texture2Destroy(texture);
        }
    }

    [Theory]
    [InlineData(WtexPayload.Rgba8, false)]
    [InlineData(WtexPayload.Rgba8, true)]
    [InlineData(WtexPayload.Rgba16f, false)]
    [InlineData(WtexPayload.Bc7, false)]
    [InlineData(WtexPayload.Bc7, true)]
    [InlineData(WtexPayload.Bc5, false)]
    [InlineData(WtexPayload.Bc4, false)]
    [InlineData(WtexPayload.Bc6hU, false)]
    public void TheDescriptorIsWhatLibktxMakesForTheFormat(WtexPayload payload, bool srgb)
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
        WtexInfo info = Info(16, 16, 1, payload, srgb ? WtexTraits.Srgb : WtexTraits.None);
        byte[] ktx2 = Ktx2Writer.Write(info, Levels(info));
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(ktx2.AsSpan(48));
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(ktx2.AsSpan(52));

        var create = new TextureCreateInfo { VkFormat = info.VkFormat, BaseWidth = 16, BaseHeight = 16, BaseDepth = 1, NumDimensions = 2, NumLevels = 1, NumLayers = 1, NumFaces = 1 };
        Texture2* made;
        Assert.Equal(Result.Success, KtxApi.Texture2Create(&create, TextureCreateStorageEnum.AllocStorage, &made));
        try
        {
            byte[] expected = new ReadOnlySpan<byte>(made->PDfd, (int)made->PDfd[0]).ToArray();
            Assert.Equal(expected.Length, length);
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(ktx2.AsSpan(offset, length)));
        }
        finally
        {
            KtxApi.Texture2Destroy(made);
        }
    }

    /// <summary>A UASTC KTX 2 file with zstd supercompression, as the importer makes them.</summary>
    private static byte[] ImportedUastc(int width, int height, bool srgb)
    {
        var random = new System.Random(5);
        byte[] level0 = new byte[width * height * 4];
        for (int i = 0; i < level0.Length; i += 4)
        {
            level0[i] = (byte)(i / 4 % width * 255 / Math.Max(1, width - 1));
            level0[i + 1] = (byte)(i / 4 / width * 255 / Math.Max(1, height - 1));
            level0[i + 2] = (byte)random.Next(64);
            level0[i + 3] = 255;
        }

        var texture = new TextureData("g", srgb ? PixelFormat.R8G8B8A8Srgb : PixelFormat.R8G8B8A8Unorm, width, height, ImageDecoder.BuildMipChain(level0, width, height, srgb), srgb, SamplerData.Default);
        return BasisEncoder.Encode(texture, normalMap: false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FromKtx2KeepsPayloadBytes(bool srgb)
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
        byte[] ktx2 = ImportedUastc(64, 48, srgb);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(ktx2.AsSpan(44))); // zstd inside the KTX 2 file; the wtex holds the bytes after it
        WtexTexture read = WtexFile.ReadKtx2("g.ktx2", ktx2, new WtexSettings { Group = WtexGroup.Effects, LodBias = 1, Flags = WtexTraits.AlphaCoverage });

        byte[] file = WtexFile.FromKtx2("g.ktx2", ktx2, new WtexSettings { Group = WtexGroup.Effects, LodBias = 1, Flags = WtexTraits.AlphaCoverage }, Options());
        WtexFile wtex = WtexFile.Read(file);

        Assert.Equal(WtexPayload.Uastc, wtex.Info.Payload);
        Assert.Equal(WtexTraits.Portable | WtexTraits.AlphaCoverage | (srgb ? WtexTraits.Srgb : WtexTraits.None), wtex.Info.Flags);
        Assert.Equal((64, 48, 7), (wtex.Info.Width, wtex.Info.Height, wtex.Info.MipCount));
        Assert.Equal((WtexGroup.Effects, (sbyte)1), (wtex.Info.Group, wtex.Info.LodBias));
        Assert.Equal(read.Info, wtex.Info);

        // The bytes of libktx's own view of the file, after it inflated them, are the bytes of every level.
        Texture2* texture = Load(ktx2);
        try
        {
            for (int level = 0; level < wtex.Info.MipCount; level++)
            {
                nuint offset;
                Assert.Equal(Result.Success, KtxApi.Texture2GetImageOffset(texture, (uint)level, 0, 0, &offset));
                byte[] expected = new ReadOnlySpan<byte>(texture->PData + offset, (int)wtex.Info.LevelSize(level)).ToArray();
                Assert.Equal(expected, wtex.ReadLevel(level).ToArray());
                Assert.Equal(expected, read.Levels[level]);
            }
        }
        finally
        {
            KtxApi.Texture2Destroy(texture);
        }
    }

    [Fact]
    public void TheUastcDescriptorIsTheOneTheEncoderWrites()
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
        // Noise with alpha: the encoder then says RGBA, which is what a wtex always says (an opaque texture is RGB in the encoder's descriptor, and transcodes the same).
        byte[] noise = Random(16 * 16 * 4, 1);
        var texture = new TextureData("g", PixelFormat.R8G8B8A8Srgb, 16, 16, ImageDecoder.BuildMipChain(noise, 16, 16, true), true, SamplerData.Default);
        byte[] encoded = BasisEncoder.Encode(texture, normalMap: false);

        byte[] rewrapped = WtexFile.Read(WtexFile.FromKtx2("g", encoded)).ToKtx2();

        static byte[] Descriptor(byte[] file) => file.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(48)), (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(52))).ToArray();
        Assert.Equal(Descriptor(encoded), Descriptor(rewrapped));
    }

    [Fact]
    public void AnImportedUastcTextureTranscodesTheSameThroughAWtex()
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
        byte[] original = ImportedUastc(64, 64, srgb: true);
        byte[] viaWtex = WtexFile.Read(WtexFile.FromKtx2("g", original, options: Options())).ToKtx2();

        foreach (TextureCaps caps in new[] { new TextureCaps(true, false), new TextureCaps(false, true), TextureCaps.None })
        {
            TextureData a = Ktx2Loader.Load("a", original, srgb: true, caps, SamplerData.Default);
            TextureData b = Ktx2Loader.Load("b", viaWtex, srgb: true, caps, SamplerData.Default);

            Assert.Equal(a.Format, b.Format);
            Assert.Equal(a.Mips.Count, b.Mips.Count);
            for (int level = 0; level < a.Mips.Count; level++)
            {
                Assert.Equal(a.Mips[level], b.Mips[level]);
            }
        }

        Assert.True(Ktx2Loader.IsSrgb("b", viaWtex));
    }

    [Fact]
    public void AnRgba8KtxFileBecomesAPortableWtexAndComesBack()
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
        byte[] level0 = Random(37 * 91 * 4, 3);
        var texture = new TextureData("r", PixelFormat.R8G8B8A8Srgb, 37, 91, ImageDecoder.BuildMipChain(level0, 37, 91, true), true, SamplerData.Default);
        byte[] ktx2 = Ktx2Writer.Write(texture, Ktx2Writer.ImportLevel);

        WtexFile wtex = WtexFile.Read(WtexFile.FromKtx2("r", ktx2, options: Options()));

        Assert.Equal(WtexPayload.Rgba8, wtex.Info.Payload);
        Assert.Equal(WtexTraits.Portable | WtexTraits.Srgb, wtex.Info.Flags);
        for (int level = 0; level < texture.Mips.Count; level++)
        {
            Assert.Equal(texture.Mips[level], wtex.ReadLevel(level).ToArray());
        }

        // And back: the same levels, uncompressed.
        TextureData back = Ktx2Loader.Load("r", wtex.ToKtx2(), true, TextureCaps.None, SamplerData.Default);
        Assert.Equal(texture.Mips.Count, back.Mips.Count);
        for (int level = 0; level < texture.Mips.Count; level++)
        {
            Assert.Equal(texture.Mips[level], back.Mips[level]);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void AWtexMadeIntoKtx2AndBackIsTheSameTexture(WtexPayload payload, int width, int height, int mips, int depth, int layers, int faces)
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
        bool colour = payload is WtexPayload.Uastc or WtexPayload.Rgba8 or WtexPayload.Bc7;
        WtexInfo info = Info(width, height, mips, payload, colour ? WtexTraits.Srgb : WtexTraits.None, depth, layers, faces);
        WtexFile first = WtexFile.Read(WtexFile.Write(info, Levels(info), Options()));

        WtexFile again = WtexFile.Read(WtexFile.FromKtx2("t", first.ToKtx2(), options: Options()));

        // Rgba8 and UASTC come back portable; the others come back as a GPU form, which is what they were.
        Assert.Equal(info.Flags | (payload == WtexPayload.Rgba8 ? WtexTraits.Portable : WtexTraits.None), again.Info.Flags);
        Assert.Equal(info with { Flags = again.Info.Flags }, again.Info);
        for (int level = 0; level < mips; level++)
        {
            Assert.Equal(first.ReadLevel(level).ToArray(), again.ReadLevel(level).ToArray());
        }
    }

    [Fact]
    public void ToKtx2IsDeterministic()
    {
        WtexInfo info = Info(33, 17, 6, WtexPayload.Bc7, WtexTraits.Srgb);
        WtexFile wtex = WtexFile.Read(WtexFile.Write(info, Levels(info), Options()));

        Assert.Equal(wtex.ToKtx2(), wtex.ToKtx2());
        Assert.True(Ktx2Loader.IsKtx2(wtex.ToKtx2()));
    }

    [Fact]
    public void TheKtx2WriterStillMakesTheFilesItMade()
    {
        // The RGBA8 writer was generalised: what it writes for a texture is what it wrote before (a header of 80 bytes, a descriptor of 92, levels at multiples of 4).
        byte[] level0 = Random(5 * 3 * 4, 1);
        var texture = new TextureData("t", PixelFormat.R8G8B8A8Srgb, 5, 3, ImageDecoder.BuildMipChain(level0, 5, 3, true), true, SamplerData.Default);

        byte[] file = Ktx2Writer.Write(texture);

        Assert.Equal(80u + (3u * 24u), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(48)));
        Assert.Equal(92u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(52)));
        Assert.Equal(43u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12)));
        for (int level = 0; level < 3; level++)
        {
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(80 + (level * 24))) % 4);
        }
    }

    // ---- Hostile KTX 2 --------------------------------------------------------------------------------------------------------------

    private static void Ktx2Refused(byte[] ktx2, string contains)
    {
        ContentException e = Assert.Throws<ContentException>(() => WtexFile.FromKtx2("hostile.ktx2", ktx2));
        Assert.Contains(contains, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKtx2FileThatIsNotWhatTheEngineConvertsIsRefused()
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
        WtexInfo info = Info(16, 16, 5);
        byte[] valid = Ktx2Writer.Write(info, Levels(info));
        _ = WtexFile.FromKtx2("valid.ktx2", valid); // the original is fine

        byte[] With(Action<byte[]> edit)
        {
            byte[] copy = (byte[])valid.Clone();
            edit(copy);
            return copy;
        }

        void Put(byte[] file, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), value);

        Ktx2Refused("not ktx"u8.ToArray(), "not a KTX 2");
        Ktx2Refused(valid[..60], "not a KTX 2");
        Ktx2Refused(With(f => Put(f, 44, 1)), "ETC1S");                  // BasisLZ
        Ktx2Refused(With(f => Put(f, 44, 3)), "supercompression");       // zlib
        Ktx2Refused(With(f => Put(f, 24, 0)), "1D texture");
        Ktx2Refused(With(f => Put(f, 40, 0)), "generated");
        Ktx2Refused(With(f => Put(f, 40, 16)), "mip levels");
        Ktx2Refused(With(f => Put(f, 40, 6)), "mip levels");             // more than a 16x16 chain
        Ktx2Refused(With(f => Put(f, 20, 16385)), "this engine reads");
        Ktx2Refused(With(f => Put(f, 20, uint.MaxValue)), "this engine reads");
        Ktx2Refused(With(f => Put(f, 28, 5000)), "this engine reads");
        Ktx2Refused(With(f => Put(f, 32, 5000)), "this engine reads");
        Ktx2Refused(With(f => Put(f, 36, 2)), "this engine reads");
        Ktx2Refused(With(f => Put(f, 36, 6)), "level 0");                // six faces: the levels are not the sizes of a cube map
        Ktx2Refused(With(f => Put(f, 12, 44)), "Vulkan format");         // B8G8R8A8_SRGB
        Ktx2Refused(With(f => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(80 + 16), 1UL << 40)), "level 0");   // a level that claims to be a terabyte
        Ktx2Refused(valid[..(valid.Length - 100)], "KTX 2");             // a level cut short
    }

    [Fact]
    public void FuzzingAKtx2FileOnlyEverGivesAContentException()
    {
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
        WtexInfo info = Info(32, 16, 6, WtexPayload.Bc7, WtexTraits.Srgb);
        byte[] valid = Ktx2Writer.Write(info, Levels(info));
        int converted = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            var random = new System.Random(seed);
            byte[] file = (byte[])valid.Clone();
            for (int edit = 1 + random.Next(3); edit > 0; edit--)
            {
                int at = random.Next(2) == 0 ? random.Next(Math.Min(file.Length, 80 + (6 * 24) + 100)) : random.Next(file.Length);
                switch (random.Next(4))
                {
                    case 0:
                        file[at] ^= (byte)(1 << random.Next(8));
                        break;
                    case 1:
                        file[at] = (byte)random.Next(256);
                        break;
                    case 2 when at + 4 <= file.Length:
                        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), new[] { 0u, uint.MaxValue, 1u << 31, 16384u, 16385u, 6u }[random.Next(6)]);
                        break;
                    default:
                        file = file[..Math.Max(1, at)];
                        break;
                }
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                _ = WtexFile.FromKtx2("fuzz", file);
                converted++;
            }
            catch (ContentException)
            {
                // refused
            }

            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024 * 1024, $"seed {seed} allocated a lot");
        }

        Assert.InRange(converted, 0, 299);
    }
}
