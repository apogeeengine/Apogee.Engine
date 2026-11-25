using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Weft.Content.Assets;
using Weft.Content.Formats;
using Weft.Core.Assets;

namespace Weft.Content.Textures;

/// <summary>What the bytes of a level of a <c>.wtex</c> are.</summary>
public enum WtexPayload : byte
{
    /// <summary>Basis Universal UASTC blocks (4x4 texels in 16 bytes): the portable form, transcoded to what the GPU samples when it is loaded.</summary>
    Uastc = 0,

    /// <summary>8-bit RGBA, four bytes a texel, <c>R8G8B8A8_UNORM</c> or <c>_SRGB</c>.</summary>
    Rgba8 = 1,

    /// <summary>BC7 blocks (16 bytes a 4x4 block), <c>BC7_UNORM_BLOCK</c> or <c>_SRGB_BLOCK</c>.</summary>
    Bc7 = 2,

    /// <summary>BC5 blocks (16 bytes a 4x4 block, two channels), <c>BC5_UNORM_BLOCK</c>.</summary>
    Bc5 = 3,

    /// <summary>BC4 blocks (8 bytes a 4x4 block, one channel), <c>BC4_UNORM_BLOCK</c>.</summary>
    Bc4 = 4,

    /// <summary>BC6H blocks of unsigned floats (16 bytes a 4x4 block), <c>BC6H_UFLOAT_BLOCK</c>.</summary>
    Bc6hU = 5,

    /// <summary>Half-float RGBA, eight bytes a texel, <c>R16G16B16A16_SFLOAT</c>.</summary>
    Rgba16f = 6,
}

/// <summary>The bits of the flags of a <c>.wtex</c> (in its header and in its <c>META</c>): what is true of the texture (named traits because the analyzers do not allow a type to end in Flags).</summary>
[Flags]
public enum WtexTraits : uint
{
    /// <summary>No flag.</summary>
    None = 0,

    /// <summary>The file is an import form: a game that is cooked rewrites it to a GPU form. A payload of <see cref="WtexPayload.Uastc"/> is always portable.</summary>
    Portable = 1,

    /// <summary>The colours are sRGB encoded (the format the GPU samples is an sRGB one).</summary>
    Srgb = 2,

    /// <summary>The texture is a normal map whose first two channels hold the normal (the third is rebuilt in the shader).</summary>
    NormalMapRG = 4,

    /// <summary>The alpha channel is a coverage that mips should keep (for alpha-tested materials); <see cref="WtexInfo.AlphaCutoff"/> is the reference.</summary>
    AlphaCoverage = 8,

    /// <summary>The colour channels are multiplied by alpha.</summary>
    Premultiplied = 16,

    /// <summary>The texture holds high dynamic range values (<see cref="WtexPayload.Bc6hU"/> or <see cref="WtexPayload.Rgba16f"/>).</summary>
    Hdr = 32,

    /// <summary>The texture is a cube map (six faces, square).</summary>
    Cube = 64,

    /// <summary>The texture is an array (its layers are separate images).</summary>
    Array = 128,
}

/// <summary>The group a texture belongs to: the engine's quality setting applies per group.</summary>
public enum WtexGroup : byte
{
    /// <summary>Level geometry and props.</summary>
    World = 0,

    /// <summary>Characters.</summary>
    Character = 1,

    /// <summary>User interface and icons.</summary>
    Ui = 2,

    /// <summary>Particles and effects.</summary>
    Effects = 3,

    /// <summary>Baked lighting.</summary>
    Lightmap = 4,
}

/// <summary>
/// What a <c>.wtex</c> says about its texture: the shape, what the bytes are and the engine's own fields (it is the <c>META</c> without the tail index). The same record describes the texture to write and the
/// texture that was read. <see cref="Check"/> says whether it can be a file; the reader and the writer both go by it.
/// </summary>
public sealed record WtexInfo
{
    /// <summary>The widest or tallest a texture may be.</summary>
    public const int MaxSize = 16384;

    /// <summary>The deepest a 3D texture may be.</summary>
    public const int MaxDepth = 2048;

    /// <summary>The most layers an array may have.</summary>
    public const int MaxLayers = 2048;

    /// <summary>The most mip levels: a 16384 texel texture down to one texel.</summary>
    public const int MaxMips = 15;

    /// <summary>The raw size up to which a mip level is in the tail (64 KiB).</summary>
    public const int TailLevelBytes = 64 * 1024;

    /// <summary>Width of the first mip level in texels.</summary>
    public required int Width { get; init; }

    /// <summary>Height of the first mip level in texels.</summary>
    public required int Height { get; init; }

    /// <summary>Depth of the first mip level: 1 unless the texture is 3D.</summary>
    public int Depth { get; init; } = 1;

    /// <summary>The number of layers: 1 unless the texture is an array (<see cref="WtexTraits.Array"/>).</summary>
    public int Layers { get; init; } = 1;

    /// <summary>The number of faces: 1, or 6 for a cube map (<see cref="WtexTraits.Cube"/>).</summary>
    public int Faces { get; init; } = 1;

    /// <summary>The number of mip levels, 1 to <see cref="MaxMips"/> and at most the length of the full chain.</summary>
    public required int MipCount { get; init; }

    /// <summary>What the bytes of the levels are.</summary>
    public required WtexPayload Payload { get; init; }

    /// <summary>The flags; <see cref="WtexTraits.Cube"/>, <see cref="WtexTraits.Array"/>, <see cref="WtexTraits.Hdr"/> and <see cref="WtexTraits.Portable"/> must agree with the rest (see <see cref="Check"/>).</summary>
    public WtexTraits Flags { get; init; }

    /// <summary>The group of the texture.</summary>
    public WtexGroup Group { get; init; }

    /// <summary>How many mip levels the quality setting drops more (positive) or fewer (negative) for this texture.</summary>
    public sbyte LodBias { get; init; }

    /// <summary>The coverage reference for <see cref="WtexTraits.AlphaCoverage"/>, 0 to 1.</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>How the texture is sampled unless a material says otherwise.</summary>
    public SamplerData Sampler { get; init; } = SamplerData.Default;

    /// <summary>Whether <see cref="WtexTraits.Portable"/> is set.</summary>
    public bool IsPortable => (Flags & WtexTraits.Portable) != 0;

    /// <summary>The Vulkan format of the payload: <c>VK_FORMAT_UNDEFINED</c> (0) for UASTC, which a KTX 2 file describes in its data format descriptor.</summary>
    public uint VkFormat => VkFormatOf(Payload, (Flags & WtexTraits.Srgb) != 0);

    /// <summary>The Vulkan format of a payload (0 for <see cref="WtexPayload.Uastc"/>).</summary>
    public static uint VkFormatOf(WtexPayload payload, bool srgb) => payload switch
    {
        WtexPayload.Rgba8 => srgb ? 43u : 37u,
        WtexPayload.Bc7 => srgb ? 146u : 145u,
        WtexPayload.Bc5 => 141,
        WtexPayload.Bc4 => 139,
        WtexPayload.Bc6hU => 143,
        WtexPayload.Rgba16f => 97,
        _ => 0,
    };

    /// <summary>The bytes of one block of the payload, and the block's width and height in texels.</summary>
    public static (int Bytes, int Width, int Height) BlockOf(WtexPayload payload) => payload switch
    {
        WtexPayload.Rgba8 => (4, 1, 1),
        WtexPayload.Rgba16f => (8, 1, 1),
        WtexPayload.Bc4 => (8, 4, 4),
        _ => (16, 4, 4),
    };

    /// <summary>How many levels the full chain of a texture of <paramref name="size"/> texels at most has (down to one texel).</summary>
    public static int FullChain(int size) => 32 - int.LeadingZeroCount(Math.Max(1, size));

    /// <summary>The size in bytes of mip level <paramref name="level"/>: every layer and face of it. Only meaningful when <see cref="Check"/> found nothing.</summary>
    public long LevelSize(int level)
    {
        (int bytes, int blockWidth, int blockHeight) = BlockOf(Payload);
        long width = Math.Max(1, Width >> level);
        long height = Math.Max(1, Height >> level);
        long depth = Math.Max(1, Depth >> level);
        return (width + blockWidth - 1) / blockWidth * ((height + blockHeight - 1) / blockHeight) * bytes * depth * Layers * Faces;
    }

    /// <summary>
    /// The first mip level of the tail when a writer lays the texture out: the smallest level such that it and every level after it are at most <see cref="TailLevelBytes"/>; the last level is always in the tail.
    /// </summary>
    public int DefaultTailMip
    {
        get
        {
            int tail = MipCount - 1;
            while (tail > 0 && LevelSize(tail - 1) <= TailLevelBytes)
            {
                tail--;
            }

            return tail;
        }
    }

    /// <summary>The flags a reader of this version knows.</summary>
    internal const WtexTraits KnownFlags = WtexTraits.Portable | WtexTraits.Srgb | WtexTraits.NormalMapRG | WtexTraits.AlphaCoverage | WtexTraits.Premultiplied | WtexTraits.Hdr | WtexTraits.Cube | WtexTraits.Array;

    /// <summary>Why this cannot be the description of a file, or null. Looks at the numbers only, so that nothing is allocated for a texture that cannot be.</summary>
    public string? Check()
    {
        if (Width is < 1 or > MaxSize || Height is < 1 or > MaxSize)
        {
            return $"is {Width}x{Height} texels; a side is 1 to {MaxSize}";
        }

        if (Depth is < 1 or > MaxDepth)
        {
            return $"has the depth {Depth}; it is 1 to {MaxDepth}";
        }

        if (Layers is < 1 or > MaxLayers)
        {
            return $"has {Layers} layers; it has 1 to {MaxLayers}";
        }

        if (Faces is not (1 or 6))
        {
            return $"has {Faces} faces; it has 1 or 6";
        }

        if ((Flags & ~KnownFlags) != 0)
        {
            return $"has flags this engine does not know (0x{(uint)Flags:x8})";
        }

        bool cube = (Flags & WtexTraits.Cube) != 0;
        bool array = (Flags & WtexTraits.Array) != 0;
        if (cube != (Faces == 6))
        {
            return cube ? "is a cube map without six faces" : "has six faces and is not a cube map";
        }

        if (cube && Width != Height)
        {
            return "is a cube map that is not square";
        }

        if (!array && Layers != 1)
        {
            return $"has {Layers} layers and is not an array";
        }

        if (Depth > 1 && (array || cube))
        {
            return "is a 3D texture and an array or a cube map";
        }

        int longest = Math.Max(Math.Max(Width, Height), Depth);
        if (MipCount < 1 || MipCount > MaxMips || MipCount > FullChain(longest))
        {
            return $"has {MipCount} mip levels; a {Width}x{Height}x{Depth} texture has 1 to {Math.Min(MaxMips, FullChain(longest))}";
        }

        if (!Enum.IsDefined(Payload))
        {
            return $"has the payload {(byte)Payload}, which this engine does not know";
        }

        if (!Enum.IsDefined(Group))
        {
            return $"has the texture group {(byte)Group}, which this engine does not know";
        }

        bool hdrPayload = Payload is WtexPayload.Bc6hU or WtexPayload.Rgba16f;
        if (((Flags & WtexTraits.Hdr) != 0) != hdrPayload)
        {
            return hdrPayload ? "holds floats and is not flagged as high dynamic range" : "is flagged as high dynamic range but its payload is not floats";
        }

        if (Payload == WtexPayload.Uastc && !IsPortable)
        {
            return "has UASTC blocks, which only the portable form holds";
        }

        if ((Flags & WtexTraits.Srgb) != 0 && (hdrPayload || Payload is WtexPayload.Bc5 or WtexPayload.Bc4 || (Flags & WtexTraits.NormalMapRG) != 0))
        {
            return "is flagged as sRGB but its payload is not colour";
        }

        if (!(AlphaCutoff >= 0f && AlphaCutoff <= 1f))
        {
            return "has an alpha cutoff that is not from 0 to 1";
        }

        if (!Enum.IsDefined(Sampler.Filter) || !Enum.IsDefined(Sampler.WrapU) || !Enum.IsDefined(Sampler.WrapV))
        {
            return "has a sampler that is not one";
        }

        if (LevelSize(0) > BinaryAsset.MaxBlockBytes)
        {
            return $"has a first level of {LevelSize(0)} bytes; at most {BinaryAsset.MaxBlockBytes} are allowed";
        }

        long total = 0;
        for (int level = 0; level < MipCount; level++)
        {
            total += LevelSize(level);
        }

        return total > BinaryAsset.MaxTotalRawBytes - WtexFile.MetaSize ? $"has levels of {total} bytes together; at most {BinaryAsset.MaxTotalRawBytes} are allowed" : null;
    }
}

/// <summary>How a <c>.wtex</c> is written; what is not the texture itself.</summary>
public sealed record WtexWriteOptions
{
    /// <summary>The UID of the asset (the sidecar's); none by default.</summary>
    public AssetUid Uid { get; init; }

    /// <summary>The import key's hash (16 bytes or the 32 of the SHA-256, of which the first 16 are kept); empty for none.</summary>
    public ReadOnlyMemory<byte> ImportHash { get; init; }

    /// <summary>The zstd level of the mip blocks, 1 to 22; 0 stores them as they are. The tail is never compressed.</summary>
    public int ZstdLevel { get; init; } = 12;
}

/// <summary>
/// A texture file on the common container (<c>.wtex</c>, <see cref="BinaryAsset"/> with the magic <c>WTEX</c>): a <c>META</c> block (<see cref="WtexInfo"/> and the index of the tail), the <c>TAIL</c> block with the small mip
/// levels, smallest first, and the larger levels each in its own block <c>MIP&lt;n&gt;</c>, the smallest of them first in the file. The layout and its limits are in <c>Docs/manual/content/formats.md</c>.
/// </summary>
/// <remarks>
/// The file is untrusted. <see cref="Read(ReadOnlyMemory{byte}, string)"/> checks the container, then every number of <c>META</c> against the limits of <see cref="WtexInfo"/>, then that the blocks are exactly the ones
/// <c>META</c> asks for with exactly the sizes it implies; nothing is allocated for a size before it has passed. The levels are read (and their checksums checked) when they are asked for.
/// Everything wrong is a <see cref="ContentException"/>.
/// </remarks>
public sealed partial class WtexFile
{
    /// <summary>The magic of the format, <c>WTEX</c>.</summary>
    public static readonly uint Magic = BinaryAsset.Fourcc("WTEX");

    /// <summary>The version of the format that is written and read.</summary>
    public const int FormatVersion = 1;

    /// <summary>The size of <c>META</c> in version 1.</summary>
    public const int MetaSize = 48;

    /// <summary>The kind of the block that holds the small levels.</summary>
    public static readonly uint TailKind = BinaryAsset.Fourcc("TAIL");

    private readonly BinaryAsset _asset;
    private readonly long[] _tailOffsets;

    private WtexFile(BinaryAsset asset, WtexInfo info, int tailMip)
    {
        _asset = asset;
        Info = info;
        TailMip = tailMip;
        _tailOffsets = new long[info.MipCount - tailMip];
        long offset = 0;
        for (int level = info.MipCount - 1; level >= tailMip; level--) // the smallest level first
        {
            _tailOffsets[level - tailMip] = offset;
            offset += info.LevelSize(level);
        }
    }

    /// <summary>What the file says about the texture.</summary>
    public WtexInfo Info { get; }

    /// <summary>The first mip level that is in the tail (<c>TAIL</c> holds this level and all the smaller ones); the larger ones have a block each.</summary>
    public int TailMip { get; }

    /// <summary>What the file is called in messages.</summary>
    public string Name => _asset.Name;

    /// <summary>The UID in the header.</summary>
    public AssetUid Uid => _asset.Uid;

    /// <summary>The import hash in the header (16 bytes).</summary>
    public ReadOnlyMemory<byte> ImportHash => _asset.ImportHash;

    /// <summary>The container the file is on (for the block table).</summary>
    public BinaryAsset Container => _asset;

    /// <summary>The kind of the block of mip level <paramref name="level"/>, <c>MIP</c> and a hexadecimal digit (<c>MIP0</c> to <c>MIPE</c>: the digit is one character, which is all a kind has room for).</summary>
    public static uint MipKind(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, WtexInfo.MaxMips - 1);
        return BinaryAsset.Fourcc("MIP" + "0123456789ABCDEF"[level]);
    }

    /// <summary>Whether the first bytes of a file are the header of a texture (the magic and the sizes of a container; nothing else is looked at).</summary>
    public static bool IsWtex(ReadOnlySpan<byte> head) => head.Length >= BinaryAsset.HeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(head) == Magic && BinaryAsset.IsContainer(head);

    // ---- Reading --------------------------------------------------------------------------------------------------------------------

    /// <summary>Reads the header, the table and <c>META</c> of a texture file and checks that its blocks are the ones its <c>META</c> asks for. The memory is kept, not copied.</summary>
    /// <param name="file">The whole file; it must not change while the result is used.</param>
    /// <param name="name">What the file is called in messages.</param>
    /// <exception cref="ContentException">The file is not a texture this engine reads, or is damaged in any way.</exception>
    public static WtexFile Read(ReadOnlyMemory<byte> file, string name = "file") => FromContainer(BinaryAsset.Read(file, name, Magic));

    /// <summary>The texture in a container that was read already (the magic must be <c>WTEX</c>).</summary>
    /// <exception cref="ContentException">It is not a texture this engine reads.</exception>
    public static WtexFile FromContainer(BinaryAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        string name = asset.Name;
        if (asset.Magic != Magic)
        {
            throw Wrong(name, $"is a '{asset.MagicName}' file, not a 'WTEX' one");
        }

        if (asset.FormatVersion != FormatVersion)
        {
            throw Wrong(name, asset.FormatVersion > FormatVersion
                ? $"is texture version {asset.FormatVersion}; this engine reads version {FormatVersion}. Update the engine, or import it again with this one"
                : $"is texture version {asset.FormatVersion}, which is not one this engine reads. Import it again");
        }

        ReadOnlySpan<byte> meta = asset.Meta.Span;
        if (meta.Length != MetaSize)
        {
            throw Wrong(name, $"has a META of {meta.Length} bytes; version {FormatVersion} has {MetaSize}");
        }

        if (BinaryPrimitives.ReadUInt64LittleEndian(meta[40..]) != 0)
        {
            throw Wrong(name, "has something in the reserved part of its META");
        }

        byte mipmaps = meta[37];
        if (mipmaps > 1)
        {
            throw Wrong(name, "has a sampler that is not one");
        }

        var info = new WtexInfo
        {
            Width = ToInt(BinaryPrimitives.ReadUInt32LittleEndian(meta)),
            Height = ToInt(BinaryPrimitives.ReadUInt32LittleEndian(meta[4..])),
            Depth = ToInt(BinaryPrimitives.ReadUInt32LittleEndian(meta[8..])),
            Layers = ToInt(BinaryPrimitives.ReadUInt32LittleEndian(meta[12..])),
            Faces = ToInt(BinaryPrimitives.ReadUInt32LittleEndian(meta[16..])),
            MipCount = ToInt(BinaryPrimitives.ReadUInt32LittleEndian(meta[20..])),
            Flags = (WtexTraits)BinaryPrimitives.ReadUInt32LittleEndian(meta[24..]),
            Payload = (WtexPayload)meta[28],
            Group = (WtexGroup)meta[29],
            LodBias = (sbyte)meta[30],
            AlphaCutoff = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(meta[32..])),
            Sampler = new SamplerData((TextureFilter)meta[36], mipmaps == 1, (TextureWrap)meta[38], (TextureWrap)meta[39]),
        };
        if (info.Check() is { } problem)
        {
            throw Wrong(name, "is not a texture this engine reads: it " + problem);
        }

        if ((uint)info.Flags != asset.Flags)
        {
            throw Wrong(name, "has flags in its header that are not the flags of its META");
        }

        int tail = meta[31];
        if (tail >= info.MipCount)
        {
            throw Wrong(name, $"has its tail at mip level {tail} of {info.MipCount}");
        }

        // The blocks are META, TAIL and then the levels below the tail from the largest tail neighbour down to level 0: no more and no fewer, with the sizes the shape implies.
        if (asset.Blocks.Count != 2 + tail)
        {
            throw Wrong(name, $"has {asset.Blocks.Count} blocks; a texture with its tail at level {tail} has {2 + tail}");
        }

        long tailBytes = 0;
        for (int level = tail; level < info.MipCount; level++)
        {
            tailBytes += info.LevelSize(level);
        }

        for (int i = 1; i < asset.Blocks.Count; i++)
        {
            BinaryBlock block = asset.Blocks[i];
            uint kind = i == 1 ? TailKind : MipKind(tail - i + 1);
            long raw = i == 1 ? tailBytes : info.LevelSize(tail - i + 1);
            if (block.Kind != kind)
            {
                throw Wrong(name, $"has block '{block.KindName}' where '{BinaryAsset.FourccText(kind)}' belongs");
            }

            if (block.Codec is not (BlockCodec.None or BlockCodec.Zstd))
            {
                throw Wrong(name, $"has block '{block.KindName}' with a codec that is not for pixels");
            }

            if (block.RawSize != raw)
            {
                throw Wrong(name, $"has block '{block.KindName}' of {block.RawSize} bytes; the texture needs {raw}");
            }

            if (block.Flags != 0 || block.AlignmentLog2 < BinaryAsset.PageAlignmentLog2)
            {
                throw Wrong(name, $"has block '{block.KindName}' with flags, or aligned to less than {1 << BinaryAsset.PageAlignmentLog2} bytes");
            }
        }

        return new WtexFile(asset, info, tail);
    }

    private static int ToInt(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static ContentException Wrong(string name, string what) => new($"'{name}' {what}.");

    /// <summary>The bytes of mip level <paramref name="level"/> (0 is the largest): every layer, face and slice of it, as a KTX 2 file has a level. The checksum of its block is verified.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is not one of the texture.</exception>
    /// <exception cref="ContentException">The block does not decode or its checksum fails.</exception>
    public ReadOnlyMemory<byte> ReadLevel(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, Info.MipCount);
        if (level < TailMip)
        {
            return _asset.ReadBlock(MipKind(level));
        }

        return _asset.ReadBlock(TailKind).Slice((int)_tailOffsets[level - TailMip], (int)Info.LevelSize(level));
    }

    /// <summary>The whole tail: the levels from <see cref="TailMip"/> up, the smallest first, with no gaps (what a streamer reads first). The checksum is verified.</summary>
    /// <exception cref="ContentException">The block does not decode or its checksum fails.</exception>
    public ReadOnlyMemory<byte> ReadTail() => _asset.ReadBlock(TailKind);

    /// <summary>Where in <see cref="ReadTail"/> mip level <paramref name="level"/> starts, for a level of the tail.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is not in the tail.</exception>
    public long TailOffset(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, TailMip);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, Info.MipCount);
        return _tailOffsets[level - TailMip];
    }

    /// <summary>Every level, the largest first; the tail is read once. Each is a slice of the file or of a block that was decoded.</summary>
    /// <exception cref="ContentException">A block does not decode or its checksum fails.</exception>
    public ReadOnlyMemory<byte>[] ReadLevels()
    {
        var levels = new ReadOnlyMemory<byte>[Info.MipCount];
        for (int level = 0; level < TailMip; level++)
        {
            levels[level] = _asset.ReadBlock(MipKind(level));
        }

        ReadOnlyMemory<byte> tail = _asset.ReadBlock(TailKind);
        for (int level = TailMip; level < levels.Length; level++)
        {
            levels[level] = tail.Slice((int)_tailOffsets[level - TailMip], (int)Info.LevelSize(level));
        }

        return levels;
    }

    // ---- Writing --------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The bytes of a texture file. The tail starts where <see cref="WtexInfo.DefaultTailMip"/> says; the mip blocks are compressed with zstd unless <see cref="WtexWriteOptions.ZstdLevel"/> is 0. The same texture
    /// makes the same bytes.
    /// </summary>
    /// <param name="info">The texture.</param>
    /// <param name="levels">The bytes of each level, the largest first, each of the size <see cref="WtexInfo.LevelSize"/> says.</param>
    /// <param name="options">The UID, the import hash and the compression; null for the defaults.</param>
    /// <exception cref="ArgumentException">The texture cannot be a file, or a level has the wrong size.</exception>
    public static byte[] Write(WtexInfo info, IReadOnlyList<ReadOnlyMemory<byte>> levels, WtexWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(levels);
        options ??= new WtexWriteOptions();
        CheckLevels(info, levels);
        if (options.ZstdLevel is < 0 or > 22)
        {
            throw new ArgumentException("The zstd level is 0 to 22.", nameof(options));
        }

        int tail = info.DefaultTailMip;
        var writer = new BinaryAssetWriter(Magic, FormatVersion) { Flags = (uint)info.Flags, Uid = options.Uid };
        if (options.ZstdLevel > 0)
        {
            writer.ZstdLevel = options.ZstdLevel;
        }

        if (!options.ImportHash.IsEmpty)
        {
            writer.SetImportHash(options.ImportHash.Span);
        }

        writer.AddMeta(MetaBytes(info, tail));

        long tailBytes = 0;
        for (int level = tail; level < info.MipCount; level++)
        {
            tailBytes += levels[level].Length;
        }

        byte[] tailBlock = new byte[tailBytes];
        int cursor = 0;
        for (int level = info.MipCount - 1; level >= tail; level--) // the smallest first
        {
            levels[level].Span.CopyTo(tailBlock.AsSpan(cursor));
            cursor += levels[level].Length;
        }

        writer.AddBlock(TailKind, tailBlock);
        BlockCodec codec = options.ZstdLevel > 0 ? BlockCodec.Zstd : BlockCodec.None;
        for (int level = tail - 1; level >= 0; level--)
        {
            writer.AddBlock(MipKind(level), levels[level], codec);
        }

        return writer.ToArray();
    }

    /// <summary>Checks the description and that there is a level of the right size for every mip level.</summary>
    /// <exception cref="ArgumentException">Something is wrong.</exception>
    internal static void CheckLevels(WtexInfo info, IReadOnlyList<ReadOnlyMemory<byte>> levels)
    {
        if (info.Check() is { } problem)
        {
            throw new ArgumentException("The texture " + problem + ".", nameof(info));
        }

        if (levels.Count != info.MipCount)
        {
            throw new ArgumentException($"The texture has {info.MipCount} mip levels and {levels.Count} were given.", nameof(levels));
        }

        for (int level = 0; level < levels.Count; level++)
        {
            if (levels[level].Length != info.LevelSize(level))
            {
                throw new ArgumentException($"Mip level {level} has {levels[level].Length} bytes; the texture needs {info.LevelSize(level)}.", nameof(levels));
            }
        }
    }

    private static byte[] MetaBytes(WtexInfo info, int tail)
    {
        byte[] meta = new byte[MetaSize];
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
        BinaryPrimitives.WriteInt32LittleEndian(meta.AsSpan(32), BitConverter.SingleToInt32Bits(info.AlphaCutoff));
        meta[36] = (byte)info.Sampler.Filter;
        meta[37] = (byte)(info.Sampler.Mipmaps ? 1 : 0);
        meta[38] = (byte)info.Sampler.WrapU;
        meta[39] = (byte)info.Sampler.WrapV;
        return meta; // 40 to 47 are reserved: zero
    }

    // ---- Dump -----------------------------------------------------------------------------------------------------------------------

    /// <summary>Writes the <c>META</c> of a texture as <c>name = value</c> lines, and where each mip level is (the dumper that <c>--dump</c> uses for the magic <c>WTEX</c>).</summary>
    public static void Dump(BinaryAsset asset, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(output);
        WtexFile file;
        try
        {
            file = FromContainer(asset);
        }
        catch (ContentException e)
        {
            output.WriteLine("invalid = " + Clean(e.Message));
            return;
        }

        WtexInfo info = file.Info;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"width = {info.Width}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"height = {info.Height}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"depth = {info.Depth}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"layers = {info.Layers}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"faces = {info.Faces}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"mips = {info.MipCount}"));
        output.WriteLine($"payload = {info.Payload}");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"vk_format = {info.VkFormat}"));
        output.WriteLine($"form = {(info.IsPortable ? "portable" : "gpu")}");
        output.WriteLine($"flags = {(info.Flags == WtexTraits.None ? "none" : info.Flags.ToString().Replace(", ", " | ", StringComparison.Ordinal))}");
        output.WriteLine($"group = {info.Group}");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"lod_bias = {info.LodBias}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"alpha_cutoff = {info.AlphaCutoff:R}"));
        output.WriteLine($"sampler = {info.Sampler.Filter}, mipmaps {(info.Sampler.Mipmaps ? "on" : "off")}, wrap {info.Sampler.WrapU} {info.Sampler.WrapV}");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"tail_mip = {file.TailMip}"));
        for (int level = 0; level < info.MipCount; level++)
        {
            int width = Math.Max(1, info.Width >> level);
            int height = Math.Max(1, info.Height >> level);
            string where = level < file.TailMip ? BinaryAsset.FourccText(MipKind(level)) : string.Create(CultureInfo.InvariantCulture, $"TAIL+{file.TailOffset(level)}");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"level {level} = {width}x{height}, {info.LevelSize(level)} bytes, in {where}"));
        }
    }

    private static string Clean(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            text.Append(char.IsControl(c) ? '?' : c);
        }

        return text.ToString();
    }
}
