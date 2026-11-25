using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Weft.Core.Assets;

namespace Weft.Content.Formats;

/// <summary>
/// Makes the bytes of a <see cref="BinaryAsset"/>. The same input makes the same bytes: the blocks are laid out in the order they were added (<c>META</c> first), each at the next offset its alignment
/// allows, the gaps are zeros, the table is in the order of the file, and nothing in it depends on the time or the machine.
/// </summary>
public sealed class BinaryAssetWriter
{
    private sealed record Pending(BinaryBlock Row, ReadOnlyMemory<byte> Stored);

    private readonly List<Pending> _blocks = [];
    private readonly byte[] _importHash = new byte[BinaryAsset.ImportHashSize];
    private int _zstdLevel = 12;
    private long _totalRaw;

    /// <summary>Starts a file.</summary>
    /// <param name="magic">The magic of the format (<see cref="BinaryAsset.Fourcc"/>).</param>
    /// <param name="formatVersion">The version of the format inside the container, from 1 to 65535.</param>
    /// <exception cref="ArgumentOutOfRangeException">The version is out of range.</exception>
    public BinaryAssetWriter(uint magic, int formatVersion)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(formatVersion, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(formatVersion, ushort.MaxValue);
        if (!BinaryAsset.IsFourcc(magic))
        {
            throw new ArgumentException("A magic is four printable ASCII characters.", nameof(magic));
        }

        Magic = magic;
        FormatVersion = formatVersion;
    }

    /// <summary>The magic of the format.</summary>
    public uint Magic { get; }

    /// <summary>The version of the format.</summary>
    public int FormatVersion { get; }

    /// <summary>Bits the format gives a meaning to (the header's flags); 0 by default.</summary>
    public uint Flags { get; set; }

    /// <summary>The UID of the asset; <see cref="AssetUid.None"/> (the default) when there is none.</summary>
    public AssetUid Uid { get; set; }

    /// <summary>The zstd level of <see cref="BlockCodec.Zstd"/> blocks, 1 to 22 (default 12).</summary>
    public int ZstdLevel
    {
        get => _zstdLevel;
        set => _zstdLevel = value is >= 1 and <= 22 ? value : throw new ArgumentOutOfRangeException(nameof(value), "The zstd level is 1 to 22.");
    }

    /// <summary>Sets the import hash: the first 16 bytes of <paramref name="hash"/> (the import key's SHA-256 is 32).</summary>
    /// <exception cref="ArgumentException">There are fewer than 16 bytes.</exception>
    public void SetImportHash(ReadOnlySpan<byte> hash)
    {
        if (hash.Length < BinaryAsset.ImportHashSize)
        {
            throw new ArgumentException($"An import hash has at least {BinaryAsset.ImportHashSize} bytes.", nameof(hash));
        }

        hash[..BinaryAsset.ImportHashSize].CopyTo(_importHash);
    }

    /// <summary>Adds <c>META</c>, which must come first: stored as it is, aligned to 8 bytes, at most <see cref="BinaryAsset.MaxMetaBytes"/>.</summary>
    /// <exception cref="InvalidOperationException">Something was added already.</exception>
    /// <exception cref="ArgumentException">It is too big.</exception>
    public void AddMeta(ReadOnlyMemory<byte> meta)
    {
        if (_blocks.Count != 0)
        {
            throw new InvalidOperationException("META is the first block.");
        }

        if (meta.Length > BinaryAsset.MaxMetaBytes)
        {
            throw new ArgumentException($"META has at most {BinaryAsset.MaxMetaBytes} bytes.", nameof(meta));
        }

        _totalRaw += meta.Length;
        _blocks.Add(new Pending(new BinaryBlock(BinaryAsset.MetaKind, BlockCodec.None, 0, BinaryAsset.MetaAlignmentLog2, 0, meta.Length, meta.Length, Crc32C.Compute(meta.Span), 0), meta));
    }

    /// <summary>
    /// Adds a block, after <c>META</c>. The bytes are encoded now (so that a bad input is found here) and are not copied for <see cref="BlockCodec.None"/>: the memory must not change until the file is written.
    /// </summary>
    /// <param name="kind">Its name (<see cref="BinaryAsset.Fourcc"/>); not used by another block.</param>
    /// <param name="raw">The bytes the reader gets back. (With <see cref="BlockCodec.MeshoptIndex"/> every triangle may come back starting at another of its corners: the same triangles, the same winding.)</param>
    /// <param name="codec">How they are stored.</param>
    /// <param name="alignmentLog2">The block starts at a multiple of two to this power: 12 (4096 bytes) unless the block is small and not for a streamer to map, at most 16.</param>
    /// <param name="stride">For the mesh codecs, the size of one element: a multiple of 4 up to 256 for vertices, 2 or 4 for indices. 0 otherwise.</param>
    /// <param name="flags">Bits the format gives a meaning to.</param>
    /// <exception cref="InvalidOperationException"><c>META</c> was not added first.</exception>
    /// <exception cref="ArgumentException">The kind is used or is not four printable characters, the block or the file would be too big, the alignment is out of range, or the size does not fit the codec.</exception>
    public void AddBlock(uint kind, ReadOnlyMemory<byte> raw, BlockCodec codec = BlockCodec.None, int alignmentLog2 = BinaryAsset.PageAlignmentLog2, int stride = 0, byte flags = 0)
    {
        if (_blocks.Count == 0)
        {
            throw new InvalidOperationException("META is the first block: add it before any other.");
        }

        if (!BinaryAsset.IsFourcc(kind))
        {
            throw new ArgumentException("A block's kind is four printable ASCII characters.", nameof(kind));
        }

        string name = BinaryAsset.FourccText(kind);
        foreach (Pending other in _blocks)
        {
            if (other.Row.Kind == kind)
            {
                throw new ArgumentException($"There is a block '{name}' already.", nameof(kind));
            }
        }

        if (_blocks.Count >= BinaryAsset.MaxBlocks)
        {
            throw new ArgumentException($"A file has at most {BinaryAsset.MaxBlocks} blocks.", nameof(kind));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(alignmentLog2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(alignmentLog2, BinaryAsset.MaxAlignmentLog2);
        if (raw.Length > BinaryAsset.MaxBlockBytes)
        {
            throw new ArgumentException($"Block '{name}' has {raw.Length} bytes; at most {BinaryAsset.MaxBlockBytes} are allowed.", nameof(raw));
        }

        if (!Enum.IsDefined(codec))
        {
            throw new ArgumentException($"Block '{name}' has a codec that is not one.", nameof(codec));
        }

        if (codec is BlockCodec.None or BlockCodec.Zstd)
        {
            if (stride != 0)
            {
                throw new ArgumentException($"Block '{name}' has an element size but a codec that has none.", nameof(stride));
            }
        }
        else if (BlockCodecs.Check(codec, long.MaxValue, raw.Length, stride) is { } problem)
        {
            throw new ArgumentException($"Block '{name}' {problem}.", nameof(stride));
        }

        ReadOnlyMemory<byte> stored = codec == BlockCodec.None ? raw : BlockCodecs.Encode(codec, raw.Span, stride, _zstdLevel);
        if (codec == BlockCodec.MeshoptIndex)
        {
            // The index codec may start a triangle at another of its corners; the block is what the decoder gives back, so that its CRC holds when it is read.
            raw = BlockCodecs.Decode(codec, stored.Span, raw.Length, stride);
        }

        if (BlockCodecs.Check(codec, stored.Length, raw.Length, stride) is { } refused)
        {
            throw new ArgumentException($"Block '{name}' came out {refused}: a reader would refuse it.", nameof(raw));
        }

        if (_totalRaw + raw.Length > BinaryAsset.MaxTotalRawBytes)
        {
            throw new ArgumentException($"The blocks of a file are at most {BinaryAsset.MaxTotalRawBytes} bytes together.", nameof(raw));
        }

        _totalRaw += raw.Length;

        _blocks.Add(new Pending(new BinaryBlock(kind, codec, flags, alignmentLog2, 0, stored.Length, raw.Length, Crc32C.Compute(raw.Span), stride), stored));
    }

    /// <summary>The file.</summary>
    /// <exception cref="InvalidOperationException">There is no <c>META</c>.</exception>
    /// <exception cref="ContentException">The file would be larger than 2 GiB.</exception>
    public byte[] ToArray()
    {
        if (_blocks.Count == 0)
        {
            throw new InvalidOperationException("A file needs a META block.");
        }

        long tableSize = (long)_blocks.Count * BinaryAsset.BlockRowSize;
        long position = BinaryAsset.HeaderSize + tableSize;
        var offsets = new long[_blocks.Count];
        for (int i = 0; i < offsets.Length; i++)
        {
            long alignment = 1L << _blocks[i].Row.AlignmentLog2;
            position = (position + alignment - 1) / alignment * alignment;
            offsets[i] = position;
            position += _blocks[i].Stored.Length;
        }

        if (position > int.MaxValue)
        {
            throw new ContentException($"The file would be {position} bytes; at most {int.MaxValue} are allowed.");
        }

        byte[] file = new byte[position]; // zeros: the padding is nothing else
        Span<byte> bytes = file;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], BinaryAsset.ContainerVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], (ushort)FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], BinaryAsset.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], (uint)_blocks.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[20..], BinaryAsset.BlockRowSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], BinaryAsset.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], Uid.Value);
        _importHash.CopyTo(bytes[40..]);

        for (int i = 0; i < _blocks.Count; i++)
        {
            BinaryBlock row = _blocks[i].Row;
            Span<byte> entry = bytes.Slice(BinaryAsset.HeaderSize + (i * BinaryAsset.BlockRowSize), BinaryAsset.BlockRowSize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, row.Kind);
            entry[4] = (byte)row.Codec;
            entry[5] = row.Flags;
            entry[6] = (byte)row.AlignmentLog2;
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)offsets[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], (ulong)row.StoredSize);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[24..], (ulong)row.RawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[32..], row.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], (uint)row.Stride);
            _blocks[i].Stored.Span.CopyTo(bytes[(int)offsets[i]..]);
        }

        // The checksum of the header and the table goes in last, where the header has a field for it.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[56..], BinaryAsset.HeaderChecksum(bytes[..BinaryAsset.HeaderSize], bytes.Slice(BinaryAsset.HeaderSize, (int)tableSize)));
        return file;
    }

    /// <summary>Writes the file to <paramref name="output"/> (see <see cref="ToArray"/>).</summary>
    public void WriteTo(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Write(ToArray());
    }
}
