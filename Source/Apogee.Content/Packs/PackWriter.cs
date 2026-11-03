using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Apogee.Content.FileSystem;

namespace Apogee.Content.Packs;

public sealed record PackWriterOptions
{
    /// <summary>Encrypts the pack (null: it is not encrypted).</summary>
    public PackKey? Key { get; init; }

    /// <summary>A part grows to about this many bytes before the next file starts a new one. A file is never split, so one bigger than this gets a part to itself.</summary>
    public long SplitBytes { get; init; } = 1536L * 1024 * 1024;

    /// <summary>The zstd level, 1 (fast) to 22 (small).</summary>
    public int CompressionLevel { get; init; } = 12;
}

/// <summary>
/// Writes a pack: files added with <see cref="Add"/> (and paths taken away from the packs below it with <see cref="Remove"/>, for a patch pack), then <see cref="Finish"/>. Each file is
/// compressed with zstd when that saves at least 10%, and sealed with AES-256-GCM when there is a key (see <see cref="PackFormat"/>). The pack is split into parts
/// (<c>game.pack</c>, <c>game_001.pack</c>…) at <see cref="PackWriterOptions.SplitBytes"/>; mount the first with <see cref="PackSource.Open"/> and it finds the rest.
/// </summary>
public sealed class PackWriter : IDisposable
{
    private readonly string _path;
    private readonly PackWriterOptions _options;
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private readonly List<string> _parts = [];
    private readonly List<PackEntry> _entries = [];
    private FileStream? _stream;
    private byte[] _salt = [];
    private byte[]? _aesKey;
    private bool _finished;

    public PackWriter(string path, PackWriterOptions? options = null)
    {
        _path = Path.GetFullPath(path);
        _options = options ?? new PackWriterOptions();
        if (_options.CompressionLevel is < 1 or > 22)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The compression level is 1 to 22.");
        }
    }

    /// <summary>Adds a file at <paramref name="path"/> (relative, as in <c>textures/ground.png</c>).</summary>
    /// <exception cref="ArgumentException">The path is not valid or is in the pack already.</exception>
    public void Add(string path, ReadOnlySpan<byte> data)
    {
        string canonical = Canonical(path);
        byte[] hash = PackFormat.Hash(data);
        EntryFlags flags = EntryFlags.None;
        byte[] stored = data.ToArray();
        if (data.Length >= PackFormat.MinimumCompressedSize)
        {
            byte[] compressed = PackFormat.Compress(data, _options.CompressionLevel);
            if (compressed.Length <= data.Length * 0.9)
            {
                stored = compressed;
                flags |= EntryFlags.Compressed;
            }
        }

        BeginPart(stored.Length + (_options.Key is null ? 0 : PackFormat.SealOverhead));
        if (_aesKey is not null)
        {
            stored = PackFormat.Seal(_aesKey, stored, PackFormat.PathContext(canonical));
            flags |= EntryFlags.Encrypted;
        }

        long offset = AlignedPosition();
        WriteZeros(offset - _stream!.Position);
        _stream.Write(stored);
        _entries.Add(new PackEntry(canonical, flags, offset, stored.Length, data.Length, hash));
    }

    public void AddFile(string path, string physicalPath) => Add(path, File.ReadAllBytes(physicalPath));

    /// <summary>Takes <paramref name="path"/> away from the packs this one is mounted over.</summary>
    public void Remove(string path)
    {
        string canonical = Canonical(path);
        BeginPart(0);
        _entries.Add(new PackEntry(canonical, EntryFlags.Removed, 0, 0, 0, new byte[16]));
    }

    /// <summary>Writes the last part. Returns the files written, the first one first.</summary>
    public IReadOnlyList<string> Finish()
    {
        if (_finished)
        {
            return _parts;
        }

        if (_stream is null)
        {
            BeginPart(0); // an empty pack is still a pack
        }

        EndPart(partsFollow: false);
        _finished = true;
        return _parts;
    }

    private string Canonical(string path)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The pack is finished.");
        }

        string canonical = VfsPath.Parse("pack://" + path)["pack://".Length..];
        if (canonical.Length == 0)
        {
            throw new ArgumentException("A file needs a path.", nameof(path));
        }

        return _paths.Add(canonical) ? canonical : throw new ArgumentException($"'{canonical}' is in the pack already.", nameof(path));
    }

    private string PartPath(int index)
    {
        if (index == 0)
        {
            return _path;
        }

        string extension = Path.GetExtension(_path);
        return Path.Combine(Path.GetDirectoryName(_path)!, $"{Path.GetFileNameWithoutExtension(_path)}_{index:D3}{extension}");
    }

    /// <summary>Makes sure there is a part open with room for <paramref name="size"/> more bytes, starting the next one when this one is full.</summary>
    private void BeginPart(long size)
    {
        if (_stream is not null && _entries.Count > 0 && _stream.Position + size + 4096 > _options.SplitBytes)
        {
            EndPart(partsFollow: true);
        }

        if (_stream is not null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string path = PartPath(_parts.Count);
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        _parts.Add(path);
        _salt = RandomNumberGenerator.GetBytes(32);
        _aesKey = _options.Key is { } key ? PackFormat.DeriveKey(key, _salt) : null;
        _stream.Write(new byte[PackFormat.HeaderSize]); // the header, written for real when the part is finished
        _entries.Clear();
    }

    private long AlignedPosition() => (_stream!.Position + PackFormat.Alignment - 1) / PackFormat.Alignment * PackFormat.Alignment;

    private void WriteZeros(long count)
    {
        if (count > 0)
        {
            _stream!.Write(new byte[count]);
        }
    }

    private void EndPart(bool partsFollow)
    {
        FileStream stream = _stream!;
        byte[] raw = PackIndex.Write(_entries);
        byte[] stored = PackFormat.Compress(raw, 19);
        PackFlags flags = partsFollow ? PackFlags.PartsFollow : PackFlags.None;
        if (_aesKey is not null)
        {
            stored = PackFormat.Seal(_aesKey, stored, PackFormat.IndexContext());
            flags |= PackFlags.IndexEncrypted;
        }

        long indexOffset = AlignedPosition();
        WriteZeros(indexOffset - stream.Position);
        stream.Write(stored);

        byte[] header = new byte[PackFormat.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), PackFormat.MagicFor(_options.Key));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), PackFormat.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)flags);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)indexOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), (ulong)stored.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), (ulong)raw.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)_entries.Count);
        _salt.CopyTo(header.AsSpan(48));
        stream.Position = 0;
        stream.Write(header);
        stream.Dispose();
        _stream = null;
        _aesKey = null;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}

/// <summary>An entry of a pack's index.</summary>
internal sealed record PackEntry(string Path, EntryFlags Flags, long Offset, long StoredLength, long RawLength, byte[] Hash)
{
    /// <summary>Which part of the pack holds it (set when the pack is read).</summary>
    public int Part { get; init; }
}

internal static class PackIndex
{
    public static byte[] Write(List<PackEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((uint)entries.Count);
            foreach (PackEntry entry in entries)
            {
                writer.Write(entry.Path);
                writer.Write((byte)entry.Flags);
                writer.Write(entry.Offset);
                writer.Write(entry.StoredLength);
                writer.Write(entry.RawLength);
                writer.Write(entry.Hash);
            }
        }

        return stream.ToArray();
    }

    public static List<PackEntry> Read(byte[] data, int part)
    {
        using var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        uint count = reader.ReadUInt32();
        if (count > data.Length)
        {
            throw new InvalidDataException("The index of the pack claims more entries than it has bytes.");
        }

        var entries = new List<PackEntry>((int)count);
        for (uint i = 0; i < count; i++)
        {
            string path = reader.ReadString();
            var flags = (EntryFlags)reader.ReadByte();
            long offset = reader.ReadInt64();
            long stored = reader.ReadInt64();
            long raw = reader.ReadInt64();
            byte[] hash = reader.ReadBytes(16);
            if (hash.Length != 16 || offset < 0 || stored < 0 || raw < 0)
            {
                throw new InvalidDataException("The index of the pack is damaged.");
            }

            entries.Add(new PackEntry(path, flags, offset, stored, raw, hash) { Part = part });
        }

        return entries;
    }
}
