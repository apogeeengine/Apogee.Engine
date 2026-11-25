using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Weft.Core.Assets;

namespace Weft.Content.Formats;

/// <summary>One row of the block table of a <see cref="BinaryAsset"/>.</summary>
/// <param name="Kind">The block's name, four ASCII characters as a little-endian integer (<see cref="BinaryAsset.Fourcc"/>).</param>
/// <param name="Codec">How the bytes are stored.</param>
/// <param name="Flags">Bits a format gives a meaning to; the container does not read them.</param>
/// <param name="AlignmentLog2">The block starts at a multiple of two to this power (from the start of the file).</param>
/// <param name="Offset">Where the stored bytes start, from the start of the file.</param>
/// <param name="StoredSize">How many bytes the block takes in the file.</param>
/// <param name="RawSize">How many bytes it is after the codec has been undone (the same as <paramref name="StoredSize"/> for <see cref="BlockCodec.None"/>).</param>
/// <param name="Crc">The CRC-32C of the raw bytes.</param>
/// <param name="Stride">For the mesh codecs, the size of one element in bytes; 0 for the others.</param>
public readonly record struct BinaryBlock(uint Kind, BlockCodec Codec, byte Flags, int AlignmentLog2, long Offset, long StoredSize, long RawSize, uint Crc, int Stride)
{
    /// <summary>The kind as text (<c>META</c>, <c>MIP3</c>).</summary>
    public string KindName => BinaryAsset.FourccText(Kind);
}

/// <summary>
/// The file layout that every binary artifact of the engine shares (<c>.wmdl</c>, <c>.wtex</c>, <c>.wsnd</c>, <c>.wshd</c> and the planned others): a 64-byte header with the magic of the format, its version, the asset's UID
/// and the import hash; a table of blocks; and the blocks, each a four-character kind, aligned (4096 bytes for the big ones) so that a streamer can read or map them directly. Block 0 is always <c>META</c>, a small
/// structure the format defines. Written by <see cref="BinaryAssetWriter"/>, which makes the same bytes for the same input. The layout is in <c>Docs/manual/content/formats.md</c>.
/// </summary>
/// <remarks>
/// The bytes of a file are untrusted (a mod, a pack): <see cref="Read(ReadOnlyMemory{byte}, string, uint)"/> checks every number against the length of the file before anything is allocated, refuses blocks
/// that overlap each other, the header or the table or lie outside the file, and verifies the CRC of <c>META</c>; a block's CRC is checked when it is read. Everything wrong is a <see cref="ContentException"/>.
/// The reader works on the memory it is given, without copying it: <see cref="Meta"/> and the blocks of <see cref="BlockCodec.None"/> are slices of it.
/// </remarks>
public sealed class BinaryAsset
{
    /// <summary>The size of the header.</summary>
    public const int HeaderSize = 64;

    /// <summary>The size of one row of the block table.</summary>
    public const int BlockRowSize = 40;

    /// <summary>The version of the container (this layout) that is written and read.</summary>
    public const int ContainerVersion = 1;

    /// <summary>The most blocks a file may have.</summary>
    public const int MaxBlocks = 4096;

    /// <summary>The most bytes <c>META</c> may have.</summary>
    public const int MaxMetaBytes = 16 * 1024 * 1024;

    /// <summary>The most raw bytes a block may have (1 GiB).</summary>
    public const long MaxBlockBytes = 1L << 30;

    /// <summary>The most raw bytes all the blocks of a file may have together (4 GiB): what a consumer that reads every block can be made to decode.</summary>
    public const long MaxTotalRawBytes = 4L << 30;

    /// <summary>The largest alignment a block may ask for: two to the 16th, 64 KiB.</summary>
    public const int MaxAlignmentLog2 = 16;

    /// <summary>The alignment of the blocks a writer makes unless it is told otherwise: 4096 bytes.</summary>
    public const int PageAlignmentLog2 = 12;

    /// <summary>The alignment of <c>META</c>: 8 bytes.</summary>
    public const int MetaAlignmentLog2 = 3;

    /// <summary>The number of bytes of the import hash.</summary>
    public const int ImportHashSize = 16;

    /// <summary>The kind of block 0.</summary>
    public static readonly uint MetaKind = Fourcc("META");

    private readonly ReadOnlyMemory<byte> _file;
    private readonly BinaryBlock[] _blocks;

    private BinaryAsset(string name, ReadOnlyMemory<byte> file, uint magic, int formatVersion, uint flags, AssetUid uid, ReadOnlyMemory<byte> importHash, BinaryBlock[] blocks)
    {
        Name = name;
        _file = file;
        Magic = magic;
        FormatVersion = formatVersion;
        Flags = flags;
        Uid = uid;
        ImportHash = importHash;
        _blocks = blocks;
    }

    /// <summary>What the file is called in messages.</summary>
    public string Name { get; }

    /// <summary>The magic of the format as a little-endian integer (<c>WMDL</c>: <c>0x4C444D57</c>).</summary>
    public uint Magic { get; }

    /// <summary>The magic as text.</summary>
    public string MagicName => FourccText(Magic);

    /// <summary>The version of the format inside the container (of <c>.wmdl</c>, of <c>.wtex</c>), at least 1. The reader of a format decides which it takes.</summary>
    public int FormatVersion { get; }

    /// <summary>Bits the format gives a meaning to.</summary>
    public uint Flags { get; }

    /// <summary>The UID of the asset, copied from its sidecar when the file was made; <see cref="AssetUid.None"/> when the file belongs to a sub-asset that has none, or to a file with no sidecar.</summary>
    public AssetUid Uid { get; }

    /// <summary>The first 16 bytes of the import key (the settings, the source and the importer's fingerprint) the file was made with; all zero when there is none.</summary>
    public ReadOnlyMemory<byte> ImportHash { get; }

    /// <summary>The size of the file.</summary>
    public int FileLength => _file.Length;

    /// <summary>The blocks, in the order of the table (the order of the file). Block 0 is <c>META</c>.</summary>
    public IReadOnlyList<BinaryBlock> Blocks => _blocks;

    /// <summary>The bytes of <c>META</c>: a slice of the file. Its CRC was verified when the file was read.</summary>
    public ReadOnlyMemory<byte> Meta => _file.Slice((int)_blocks[0].Offset, (int)_blocks[0].StoredSize);

    /// <summary>A four-character kind or magic as an integer: the characters in file order (<c>WMDL</c> is the bytes <c>57 4D 44 4C</c>).</summary>
    /// <exception cref="ArgumentException">The text is not four printable ASCII characters.</exception>
    public static uint Fourcc(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length != 4)
        {
            throw new ArgumentException($"'{text}' is not four characters.", nameof(text));
        }

        uint value = 0;
        for (int i = 0; i < 4; i++)
        {
            if (text[i] is < ' ' or > '~')
            {
                throw new ArgumentException($"'{text}' has a character that is not printable ASCII.", nameof(text));
            }

            value |= (uint)text[i] << (8 * i);
        }

        return value;
    }

    /// <summary>A kind or magic as text: its four characters, with <c>?</c> for any that is not printable ASCII.</summary>
    public static string FourccText(uint value)
    {
        Span<char> text = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(value >> (8 * i));
            text[i] = b is >= (byte)' ' and <= (byte)'~' ? (char)b : '?';
        }

        return new string(text);
    }

    /// <summary>The block of <paramref name="kind"/> (<see cref="Fourcc"/>), or false.</summary>
    public bool TryGetBlock(uint kind, out BinaryBlock block)
    {
        foreach (BinaryBlock candidate in _blocks)
        {
            if (candidate.Kind == kind)
            {
                block = candidate;
                return true;
            }
        }

        block = default;
        return false;
    }

    /// <summary>Whether there is a block of <paramref name="kind"/>.</summary>
    public bool Has(uint kind) => TryGetBlock(kind, out _);

    /// <summary>The stored bytes of the block of <paramref name="kind"/>, as they are in the file: no copy, no decoding and no CRC check. For a streamer that reads a <see cref="BlockCodec.None"/> block straight into its buffer.</summary>
    /// <exception cref="ContentException">There is no such block.</exception>
    public ReadOnlySpan<byte> StoredBytes(uint kind) => StoredMemory(Find(kind));

    /// <summary>
    /// The raw bytes of the block of <paramref name="kind"/>, with its CRC verified (on every call): for <see cref="BlockCodec.None"/> a slice of the file (no copy), for the other codecs a new array.
    /// A file that is memory-mapped must be mapped read-only and private (or copied): the memory is read again after the check, and a mapping that another process can write to can change under it.
    /// </summary>
    /// <exception cref="ContentException">There is no such block, it does not decode, or its CRC is not the one in the table.</exception>
    public ReadOnlyMemory<byte> ReadBlock(uint kind) => ReadBlock(IndexOf(kind));

    /// <summary>The raw bytes of block number <paramref name="index"/> of <see cref="Blocks"/> (see <see cref="ReadBlock(uint)"/>).</summary>
    /// <exception cref="ContentException">It does not decode, or its CRC is not the one in the table.</exception>
    public ReadOnlyMemory<byte> ReadBlock(int index)
    {
        BinaryBlock block = _blocks[index];
        ReadOnlyMemory<byte> stored = _file.Slice((int)block.Offset, (int)block.StoredSize);
        ReadOnlyMemory<byte> raw;
        if (block.Codec == BlockCodec.None)
        {
            raw = stored;
        }
        else
        {
            try
            {
                raw = BlockCodecs.Decode(block.Codec, stored.Span, block.RawSize, block.Stride);
            }
            catch (InvalidOperationException e)
            {
                throw new ContentException($"'{Name}': block '{block.KindName}' cannot be decoded ({e.Message}).", e);
            }
        }

        if (Crc32C.Compute(raw.Span) != block.Crc)
        {
            throw new ContentException($"'{Name}' is damaged: the checksum of block '{block.KindName}' is not the one in the file.");
        }

        return raw;
    }

    private BinaryBlock Find(uint kind) => _blocks[IndexOf(kind)];

    private int IndexOf(uint kind)
    {
        for (int i = 0; i < _blocks.Length; i++)
        {
            if (_blocks[i].Kind == kind)
            {
                return i;
            }
        }

        throw new ContentException($"'{Name}' has no block '{FourccText(kind)}'.");
    }

    private ReadOnlySpan<byte> StoredMemory(BinaryBlock block) => _file.Span.Slice((int)block.Offset, (int)block.StoredSize);

    // ---- Reading --------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the header and the block table of <paramref name="file"/> and checks them all; the memory is kept, not copied. Blocks are read, and their CRCs checked, with <see cref="ReadBlock(uint)"/>;
    /// <c>META</c> is checked here.
    /// </summary>
    /// <param name="file">The whole file. It must not change while the result is used.</param>
    /// <param name="name">What the file is called in messages (its path).</param>
    /// <param name="expectedMagic">The magic the caller wants (<see cref="Fourcc"/>), or 0 for any: another is refused with a message that names both.</param>
    /// <exception cref="ContentException">The file is not a container, is of a newer version, is cut short, or has a table that is wrong in any way.</exception>
    public static BinaryAsset Read(ReadOnlyMemory<byte> file, string name = "file", uint expectedMagic = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        ReadOnlySpan<byte> bytes = file.Span;
        long length = bytes.Length;
        if (length < HeaderSize)
        {
            throw Wrong(name, length < 4 ? "is not a binary asset: it is too short" : $"is cut short: it is {length} bytes and a header is {HeaderSize}");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (!IsFourcc(magic))
        {
            throw Wrong(name, "is not a binary asset: it does not start with a four-letter magic");
        }

        if (expectedMagic != 0 && magic != expectedMagic)
        {
            throw Wrong(name, $"is a '{FourccText(magic)}' file, not a '{FourccText(expectedMagic)}' one");
        }

        int containerVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (containerVersion != ContainerVersion)
        {
            throw Wrong(name, containerVersion > ContainerVersion
                ? $"is container version {containerVersion}; this engine reads version {ContainerVersion}. Update the engine, or import it again with this one"
                : $"is container version {containerVersion}, which is not a version of the container. Import it again");
        }

        int formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        if (formatVersion == 0)
        {
            throw Wrong(name, "has the format version 0, which is none: it is not on the container. Import it again");
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != HeaderSize)
        {
            throw Wrong(name, "has a header of a size this engine does not know");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        if (count is 0 or > MaxBlocks)
        {
            throw Wrong(name, count == 0 ? "has no blocks" : $"claims {count} blocks; at most {MaxBlocks} are allowed");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) != BlockRowSize)
        {
            throw Wrong(name, "has a block table row of a size this engine does not know");
        }

        ulong tableOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
        long tableSize = (long)count * BlockRowSize;
        if (tableOffset != HeaderSize)
        {
            throw Wrong(name, "has its block table somewhere other than after the header");
        }

        if (tableSize > length - HeaderSize)
        {
            throw Wrong(name, "is cut short: its block table is not in the file");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..]) != 0)
        {
            throw Wrong(name, "has something in the reserved part of its header");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[56..]) != HeaderChecksum(bytes[..HeaderSize], bytes.Slice(HeaderSize, (int)tableSize)))
        {
            throw Wrong(name, "is damaged: the checksum of its header and block table is not the one in the file");
        }

        var uid = new AssetUid(BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]));
        ReadOnlyMemory<byte> importHash = file.Slice(40, ImportHashSize);
        var blocks = new BinaryBlock[count];
        var seen = new HashSet<uint>();
        long cursor = HeaderSize + tableSize; // blocks follow the table in the order of the table, and do not overlap
        long totalRaw = 0;

        for (int i = 0; i < blocks.Length; i++)
        {
            ReadOnlySpan<byte> row = bytes.Slice(HeaderSize + (i * BlockRowSize), BlockRowSize);
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(row);
            if (!IsFourcc(kind))
            {
                throw Wrong(name, $"has a block ({i}) with a name that is not four printable letters");
            }

            string kindName = FourccText(kind);
            var codec = (BlockCodec)row[4];
            byte blockFlags = row[5];
            int alignment = row[6];
            if (row[7] != 0)
            {
                throw Wrong(name, $"has something in the reserved part of the row of block '{kindName}'");
            }

            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(row[8..]);
            ulong stored = BinaryPrimitives.ReadUInt64LittleEndian(row[16..]);
            ulong raw = BinaryPrimitives.ReadUInt64LittleEndian(row[24..]);
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(row[32..]);
            uint stride = BinaryPrimitives.ReadUInt32LittleEndian(row[36..]);

            if (!seen.Add(kind))
            {
                throw Wrong(name, $"has two blocks named '{kindName}'");
            }

            if (alignment > MaxAlignmentLog2)
            {
                throw Wrong(name, $"has block '{kindName}' aligned to 2^{alignment} bytes; at most 2^{MaxAlignmentLog2} is allowed");
            }

            if (offset > (ulong)length || stored > (ulong)length - offset)
            {
                throw Wrong(name, $"is cut short: block '{kindName}' (offset {offset}, {stored} bytes) is not in the file");
            }

            if ((offset & ((1UL << alignment) - 1)) != 0)
            {
                throw Wrong(name, $"has block '{kindName}' at offset {offset}, which is not aligned to {1 << alignment} bytes");
            }

            if (raw > MaxBlockBytes)
            {
                throw Wrong(name, $"has block '{kindName}' of {raw} bytes (at most {MaxBlockBytes} are allowed)");
            }

            if (stride > 256)
            {
                throw Wrong(name, $"has block '{kindName}' with the element size {stride}, which is more than 256");
            }

            totalRaw += (long)raw;
            if (totalRaw > MaxTotalRawBytes)
            {
                throw Wrong(name, $"has blocks that are more than {MaxTotalRawBytes} bytes together (at block '{kindName}')");
            }

            if ((long)offset < cursor)
            {
                throw Wrong(name, $"has block '{kindName}' that overlaps the header, the table or the block before it (blocks follow each other in the order of the table)");
            }

            if (BlockCodecs.Check(codec, (long)stored, (long)raw, (int)stride) is { } problem)
            {
                throw Wrong(name, $"has block '{kindName}' that {problem}");
            }

            if (i == 0 && (kind != MetaKind || codec != BlockCodec.None || raw > MaxMetaBytes))
            {
                throw Wrong(name, kind != MetaKind ? $"does not start with its META block (it starts with '{kindName}')" : codec != BlockCodec.None ? "has a META block that is compressed" : $"has a META block of {raw} bytes; at most {MaxMetaBytes} are allowed");
            }

            blocks[i] = new BinaryBlock(kind, codec, blockFlags, alignment, (long)offset, (long)stored, (long)raw, crc, (int)stride);
            cursor = (long)(offset + stored);
        }

        var asset = new BinaryAsset(name, file, magic, formatVersion, flags, uid, importHash, blocks);
        _ = asset.ReadBlock(0);
        return asset;
    }

    /// <summary>
    /// Whether the first bytes of a file (at least <see cref="HeaderSize"/> of them) are the header of a container this engine reads: the magic, the versions, the sizes, the block count and the reserved bytes
    /// are as <see cref="Read(ReadOnlyMemory{byte}, string, uint)"/> wants them. It does not check the checksum, which needs the table; <see cref="ReadUid(ReadOnlySpan{byte})"/> does.
    /// </summary>
    public static bool IsContainer(ReadOnlySpan<byte> head) =>
        head.Length >= HeaderSize
        && IsFourcc(BinaryPrimitives.ReadUInt32LittleEndian(head))
        && BinaryPrimitives.ReadUInt16LittleEndian(head[4..]) == ContainerVersion
        && BinaryPrimitives.ReadUInt16LittleEndian(head[6..]) != 0
        && BinaryPrimitives.ReadUInt32LittleEndian(head[12..]) == HeaderSize
        && BinaryPrimitives.ReadUInt32LittleEndian(head[16..]) is >= 1 and <= MaxBlocks
        && BinaryPrimitives.ReadUInt32LittleEndian(head[20..]) == BlockRowSize
        && BinaryPrimitives.ReadUInt64LittleEndian(head[24..]) == HeaderSize
        && BinaryPrimitives.ReadUInt32LittleEndian(head[60..]) == 0;

    /// <summary>The checksum of a file's header (without the checksum itself) and block table, which is what the header holds at offset 56.</summary>
    internal static uint HeaderChecksum(ReadOnlySpan<byte> header, ReadOnlySpan<byte> table) => Crc32C.Compute(header[..56], table);

    /// <summary>
    /// The UID in the header of a container (<see cref="AssetUid.None"/> when there is none, or when the bytes are not a container header and table with their checksum). <paramref name="file"/> is the file
    /// or at least its first <c>64 + 40 * block count</c> bytes; the blocks are not looked at. It never throws for bad content.
    /// </summary>
    public static AssetUid ReadUid(ReadOnlySpan<byte> file)
    {
        if (!IsContainer(file))
        {
            return AssetUid.None;
        }

        int tableSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[16..]) * BlockRowSize;
        return file.Length >= HeaderSize + tableSize && BinaryPrimitives.ReadUInt32LittleEndian(file[56..]) == HeaderChecksum(file[..HeaderSize], file.Slice(HeaderSize, tableSize))
            ? new AssetUid(BinaryPrimitives.ReadUInt64LittleEndian(file[32..]))
            : AssetUid.None;
    }

    /// <summary>
    /// The UID in the header of the container that <paramref name="file"/> holds (see <see cref="ReadUid(ReadOnlySpan{byte})"/>). Only the header and the table are read, at most 160 KiB; the stream is left
    /// where it was. A stream that cannot seek gives <see cref="AssetUid.None"/>.
    /// </summary>
    public static AssetUid ReadUid(Stream file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.CanSeek)
        {
            return AssetUid.None;
        }

        long start = file.Position;
        try
        {
            byte[] head = new byte[HeaderSize];
            if (file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < HeaderSize || !IsContainer(head))
            {
                return AssetUid.None;
            }

            byte[] all = new byte[HeaderSize + ((int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(16)) * BlockRowSize)];
            head.CopyTo(all, 0);
            int read = file.ReadAtLeast(all.AsSpan(HeaderSize), all.Length - HeaderSize, throwOnEndOfStream: false);
            return read == all.Length - HeaderSize ? ReadUid(all) : AssetUid.None;
        }
        finally
        {
            file.Position = start;
        }
    }

    internal static bool IsFourcc(uint value)
    {
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(value >> (8 * i));
            if (b is < (byte)' ' or > (byte)'~')
            {
                return false;
            }
        }

        return true;
    }

    private static ContentException Wrong(string name, string what) => new($"'{name}' {what}.");

    /// <summary>The import hash as 32 lower-case hex digits.</summary>
    public string ImportHashHex()
    {
        var text = new StringBuilder(ImportHashSize * 2);
        foreach (byte b in ImportHash.Span)
        {
            text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
