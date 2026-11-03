using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Apogee.Content.FileSystem;
using Microsoft.Win32.SafeHandles;

namespace Apogee.Content.Packs;

/// <summary>
/// A pack (see <see cref="PackWriter"/>) as a source of the virtual file system. Opening reads the headers and indexes of the first part and of the parts that follow it
/// (<c>name_001.pack</c>…, which must all be there); files are read, decrypted, decompressed and checked against their hashes when asked for. A pack that is encrypted needs the key
/// (without one, or with a wrong one, <see cref="Open"/> says so); damage anywhere is an <see cref="IOException"/> naming the file. Reads are safe from several threads.
/// A patch pack can <see cref="FileKind.Removed"/> paths of the packs mounted under it.
/// </summary>
public sealed class PackSource : IFileSource, IDisposable
{
    private const int MaxParts = 1000;

    private readonly List<SafeFileHandle> _handles = [];
    private readonly List<byte[]?> _keys = [];
    private readonly Dictionary<string, PackEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Name, FileKind Kind)>> _directories = new(StringComparer.Ordinal) { [""] = [] };
    private readonly string _description;

    private PackSource(string description) => _description = description;

    public string Description => _description;

    /// <summary>The number of files (not counting removals).</summary>
    public int FileCount => _entries.Values.Count(e => (e.Flags & EntryFlags.Removed) == 0);

    /// <summary>The number of parts the pack is in.</summary>
    public int PartCount => _handles.Count;

    /// <summary>True when the pack's index is encrypted.</summary>
    public bool IsEncrypted { get; private set; }

    /// <summary>Opens the pack whose first part is <paramref name="path"/>, and the parts after it.</summary>
    /// <param name="key">The key of an encrypted pack; ignored for one that is not.</param>
    /// <exception cref="IOException">It is not a pack, a part is missing, it is encrypted and the key is missing or wrong, or it is damaged.</exception>
    public static PackSource Open(string path, PackKey? key = null)
    {
        string full = Path.GetFullPath(path);
        var source = new PackSource(Path.GetFileName(full));
        try
        {
            for (int part = 0; part < MaxParts; part++)
            {
                string partPath = part == 0 ? full : Path.Combine(Path.GetDirectoryName(full)!, $"{Path.GetFileNameWithoutExtension(full)}_{part:D3}{Path.GetExtension(full)}");
                if (!File.Exists(partPath))
                {
                    throw new IOException($"{source._description}: part {part} of the pack is missing ({Path.GetFileName(partPath)}).");
                }

                if (!source.ReadPart(partPath, part, key))
                {
                    return source;
                }
            }

            throw new IOException($"{source._description}: the pack has more than {MaxParts} parts.");
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Reads a part's header and index. Returns true when another part follows.</summary>
    private bool ReadPart(string partPath, int part, PackKey? key)
    {
        SafeFileHandle handle = File.OpenHandle(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        _handles.Add(handle);
        string name = Path.GetFileName(partPath);
        long length = RandomAccess.GetLength(handle);
        if (length < PackFormat.HeaderSize)
        {
            throw new IOException($"{name} is not a pack.");
        }

        byte[] header = new byte[PackFormat.HeaderSize];
        ReadExactly(handle, header, 0, name);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != PackFormat.PlainMagic && magic != PackFormat.MagicFor(key))
        {
            throw new IOException(key is null
                ? $"{name} is not a pack, or it is encrypted and no key was given (set {PackKey.EnvironmentVariable})."
                : $"{name} is not a pack of this game, or the key is wrong.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (version != PackFormat.Version)
        {
            throw new IOException($"{name} is version {version} of the pack format; this engine reads version {PackFormat.Version}.");
        }

        var flags = (PackFlags)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        long indexOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
        long indexLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24));
        long indexRaw = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
        if (indexOffset < PackFormat.HeaderSize || indexLength < 0 || indexOffset + indexLength > length || indexRaw < 0 || indexRaw > int.MaxValue)
        {
            throw new IOException($"{name} is damaged: its index is not inside the file.");
        }

        byte[]? aesKey = null;
        if ((flags & PackFlags.IndexEncrypted) != 0)
        {
            if (key is null)
            {
                throw new IOException($"{name} is encrypted and no key was given (set {PackKey.EnvironmentVariable}).");
            }

            IsEncrypted = true;
            aesKey = PackFormat.DeriveKey(key, header.AsSpan(48, 32));
        }

        _keys.Add(aesKey);
        byte[] stored = new byte[indexLength];
        ReadExactly(handle, stored, indexOffset, name);
        try
        {
            byte[] compressed = aesKey is null ? stored : PackFormat.Open(aesKey, stored, PackFormat.IndexContext());
            foreach (PackEntry entry in PackIndex.Read(PackFormat.Decompress(compressed, indexRaw), part))
            {
                Register(entry, name);
            }
        }
        catch (CryptographicException) when (aesKey is not null)
        {
            throw new IOException($"{name}: the index cannot be read: the key is wrong, or the pack is damaged.");
        }
        catch (Exception e) when (e is CryptographicException or InvalidDataException or EndOfStreamException)
        {
            throw new IOException($"{name}: the index is damaged ({e.Message}).");
        }

        return (flags & PackFlags.PartsFollow) != 0;
    }

    private void Register(PackEntry entry, string part)
    {
        if (!VfsPath.TryParse("pack://" + entry.Path, out string canonical, out _) || canonical["pack://".Length..] != entry.Path || entry.Path.Length == 0)
        {
            throw new IOException($"{part}: the pack has an entry with an invalid path.");
        }

        _entries[entry.Path] = entry; // a later part overrides an earlier one
        string path = entry.Path;
        FileKind kind = (entry.Flags & EntryFlags.Removed) != 0 ? FileKind.Removed : FileKind.File;
        while (path.Length > 0)
        {
            int slash = path.LastIndexOf('/');
            string parent = slash < 0 ? "" : path[..slash];
            string name = path[(slash + 1)..];
            if (!_directories.TryGetValue(parent, out List<(string Name, FileKind Kind)>? children))
            {
                _directories[parent] = children = [];
            }

            int existing = children.FindIndex(c => c.Name == name);
            if (existing >= 0)
            {
                if (kind != FileKind.Directory)
                {
                    children[existing] = (name, kind);
                }

                return;
            }

            children.Add((name, kind));
            if (kind == FileKind.Directory && !_directories.ContainsKey(path))
            {
                _directories[path] = [];
            }

            path = parent;
            kind = FileKind.Directory;
        }
    }

    public FileKind Kind(string path)
    {
        if (_entries.TryGetValue(path, out PackEntry? entry))
        {
            return (entry.Flags & EntryFlags.Removed) != 0 ? FileKind.Removed : FileKind.File;
        }

        return _directories.ContainsKey(path) ? FileKind.Directory : FileKind.None;
    }

    public byte[] ReadAllBytes(string path)
    {
        if (!_entries.TryGetValue(path, out PackEntry? entry) || (entry.Flags & EntryFlags.Removed) != 0)
        {
            throw new FileNotFoundException($"There is no file '{path}' in {_description}.");
        }

        byte[] data = new byte[entry.StoredLength];
        ReadExactly(_handles[entry.Part], data, entry.Offset, _description);
        try
        {
            if ((entry.Flags & EntryFlags.Encrypted) != 0)
            {
                data = PackFormat.Open(_keys[entry.Part] ?? throw new IOException($"{_description}: '{path}' is encrypted and the pack has no key."), data, PackFormat.PathContext(path));
            }

            if ((entry.Flags & EntryFlags.Compressed) != 0)
            {
                data = PackFormat.Decompress(data, entry.RawLength);
            }
        }
        catch (CryptographicException e)
        {
            throw new IOException($"{_description}: '{path}' is damaged or the key is wrong ({e.Message}).");
        }

        if (data.Length != entry.RawLength || !PackFormat.Hash(data).AsSpan().SequenceEqual(entry.Hash))
        {
            throw new IOException($"{_description}: '{path}' is damaged (its hash is not the one in the index).");
        }

        return data;
    }

    /// <summary>A file that is neither compressed nor encrypted is streamed from the pack without being read whole (and without its hash checked); any other is read whole.</summary>
    public Stream OpenRead(string path)
    {
        if (_entries.TryGetValue(path, out PackEntry? entry) && (entry.Flags & (EntryFlags.Removed | EntryFlags.Compressed | EntryFlags.Encrypted)) == 0)
        {
            return new EntryStream(_handles[entry.Part], entry.Offset, entry.RawLength);
        }

        return new MemoryStream(ReadAllBytes(path), writable: false);
    }

    public void List(string directory, List<(string Name, FileKind Kind)> into)
    {
        if (_directories.TryGetValue(directory, out List<(string Name, FileKind Kind)>? children))
        {
            into.AddRange(children);
        }
    }

    private static void ReadExactly(SafeFileHandle handle, byte[] buffer, long offset, string name)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = RandomAccess.Read(handle, buffer.AsSpan(read), offset + read);
            if (count == 0)
            {
                throw new IOException($"{name} ends where it should not: the pack is cut short.");
            }

            read += count;
        }
    }

    public void Dispose()
    {
        foreach (SafeFileHandle handle in _handles)
        {
            handle.Dispose();
        }

        _handles.Clear();
    }

    /// <summary>A read-only view of a stretch of a pack's file.</summary>
    private sealed class EntryStream(SafeFileHandle handle, long start, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int count = RandomAccess.Read(handle, buffer[..(int)Math.Min(buffer.Length, remaining)], start + _position);
            _position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
