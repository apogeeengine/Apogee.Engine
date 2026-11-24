using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Weft.Export.Windows;

/// <summary>
/// The name of a resource type, of a resource or of its language: a number (<c>MAKEINTRESOURCE</c>, such as 3 for an icon) or a string. Strings compare without regard to case, as the Windows loader's
/// search does, and sort before numbers, as the format wants its directory entries in.
/// </summary>
internal readonly struct ResourceId : IEquatable<ResourceId>, IComparable<ResourceId>
{
    private ResourceId(string? name, int id)
    {
        Name = name;
        Id = id;
    }

    /// <summary>The string name, or null for a number.</summary>
    public string? Name { get; }

    /// <summary>The number; 0 for a string.</summary>
    public int Id { get; }

    /// <summary>Whether this is a string.</summary>
    public bool IsNamed => Name is not null;

    /// <summary>A number.</summary>
    public static ResourceId FromId(int id) => new(null, id);

    /// <summary>A string.</summary>
    public static ResourceId FromName(string name) => new(name ?? throw new ArgumentNullException(nameof(name)), 0);

    /// <summary>A number.</summary>
    public static implicit operator ResourceId(int id) => FromId(id);

    public static bool operator ==(ResourceId left, ResourceId right) => left.Equals(right);

    public static bool operator !=(ResourceId left, ResourceId right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(ResourceId other) => Name is null ? other.Name is null && Id == other.Id : string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ResourceId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Name is null ? Id : StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

    /// <summary>Strings first (ordered without regard to case), then numbers in ascending order.</summary>
    public int CompareTo(ResourceId other)
    {
        if (Name is not null)
        {
            return other.Name is null ? -1 : string.Compare(Name, other.Name, StringComparison.OrdinalIgnoreCase);
        }

        return other.Name is not null ? 1 : Id.CompareTo(other.Id);
    }

    /// <inheritdoc/>
    public override string ToString() => Name ?? Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The numbers of the resource types we write or look for (<c>RT_*</c> in <c>winuser.h</c>).</summary>
internal static class ResourceType
{
    /// <summary><c>RT_ICON</c>: one image of an icon.</summary>
    public const int Icon = 3;

    /// <summary><c>RT_GROUP_ICON</c>: the list of the images that make an icon.</summary>
    public const int GroupIcon = 14;

    /// <summary><c>RT_VERSION</c>: the version information the Details tab shows.</summary>
    public const int Version = 16;

    /// <summary><c>RT_MANIFEST</c>: the application manifest.</summary>
    public const int Manifest = 24;
}

/// <summary>Where a resource is: its type, its name and its language (<c>LANGID</c>; 0 is neutral, 0x0409 English US).</summary>
internal readonly record struct ResourceKey(ResourceId Type, ResourceId Name, int Language);

/// <summary>The bytes of a resource, and the code page its text is in (<c>IMAGE_RESOURCE_DATA_ENTRY.CodePage</c>; 0 for the ones we write).</summary>
internal readonly record struct ResourceData(byte[] Bytes, uint CodePage);

/// <summary>
/// The resources of a PE file (the tree the <c>.rsrc</c> section holds: type, then name, then language, down to the data), read into memory, changed, and written out again in the layout linkers use.
/// <para>
/// The format: a directory is <c>IMAGE_RESOURCE_DIRECTORY</c> (16 bytes: <c>Characteristics</c>, <c>TimeDateStamp</c>, two version words, <c>NumberOfNamedEntries</c>, <c>NumberOfIdEntries</c>) followed by
/// that many 8 byte entries (<c>Name</c> or <c>Id</c>, then <c>OffsetToData</c>), the named ones first. In an entry's first word, bit 31 set means the low 31 bits are the offset of a length-prefixed
/// UTF-16 string, otherwise the word is the number; in its second word, bit 31 set means the low 31 bits are the offset of a subdirectory, otherwise of an <c>IMAGE_RESOURCE_DATA_ENTRY</c>
/// (16 bytes: <c>OffsetToData</c> as an <b>RVA</b>, <c>Size</c>, <c>CodePage</c>, reserved). All other offsets are relative to the start of the resource directory.
/// </para>
/// </summary>
internal sealed class ResourceTree
{
    private const uint HighBit = 0x8000_0000;
    private const int DirectorySize = 16;
    private const int EntrySize = 8;
    private const int DataEntrySize = 16;
    private const long MaxSerializedSize = 1L << 30;

    /// <summary>The most directory entries read in all: a real executable has a few dozen, and a directory entry may point to a directory another entry points to, so the count bounds the work of a hostile file.</summary>
    private const int MaxEntriesRead = 100_000;

    private readonly Dictionary<ResourceKey, ResourceData> _entries = [];

    /// <summary>Every resource in the tree.</summary>
    public IReadOnlyDictionary<ResourceKey, ResourceData> Entries => _entries;

    /// <summary>
    /// Reads the resources of <paramref name="image"/> (data directory 2) into a tree; the bytes are copied. No resource directory is an empty tree. The tree must have the usual three levels
    /// (type, name, language); the sizes and offsets it declares are checked against the section that holds it before anything is read or allocated.
    /// </summary>
    /// <exception cref="ExportException">The directory is outside the sections, damaged, or has a shape other than type/name/language.</exception>
    public static ResourceTree Read(PeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var tree = new ResourceTree();
        (uint rva, uint size) = image.GetDirectory(PeImage.ResourceDirectory);
        if (rva == 0 || size == 0)
        {
            return tree;
        }

        // The directory is read from the start of its RVA to the end of the section's mapped data: the declared size is only a hint, linkers that round it differ.
        int sectionIndex = image.SectionIndexOf(rva);
        if (sectionIndex < 0)
        {
            throw new ExportException("The executable's resource directory is not inside any of its sections.");
        }

        PeSection section = image.Sections[sectionIndex];
        uint into = rva - section.VirtualAddress;
        ReadOnlySpan<byte> region = image.Bytes.AsSpan((int)(section.PointerToRawData + into), (int)(section.Extent - into));
        long copied = 0;
        int visited = 0;
        ReadDirectory(image, region, 0, 0, default, default, tree._entries, ref copied, ref visited);
        return tree;
    }

    /// <summary>Sets a resource, replacing the one with the same type, name and language.</summary>
    public void Set(ResourceKey key, byte[] data) => _entries[key] = new ResourceData(data, 0);

    /// <summary>Removes every resource of <paramref name="type"/>, whatever its name and language.</summary>
    /// <returns>How many were removed.</returns>
    public int RemoveType(ResourceId type)
    {
        List<ResourceKey> doomed = [];
        foreach (ResourceKey key in _entries.Keys)
        {
            if (key.Type == type)
            {
                doomed.Add(key);
            }
        }

        foreach (ResourceKey key in doomed)
        {
            _entries.Remove(key);
        }

        return doomed.Count;
    }

    /// <summary>
    /// Writes the tree as the contents of a resource section that will be at <paramref name="baseRva"/> in memory (the data entries hold RVAs). The layout is the one linkers write: the directories
    /// (the root, then the name directory of each type, then the language directory of each name), the strings, the data entries, and the data, each blob starting on 8 bytes. Entries are sorted as
    /// the loader's binary search needs: strings first without regard to case, then numbers ascending. Time stamps and versions are zero, so the same tree is always the same bytes.
    /// </summary>
    /// <exception cref="ExportException">The tree is too big to be a resource section.</exception>
    public byte[] Serialize(uint baseRva)
    {
        Node root = BuildNodes();

        // Pass 1: where everything goes. The directories are laid out level by level: the root, the type directories (whose entries are names), the name directories (whose entries are the
        // languages, the leaves), then the strings, the data entries and the data.
        List<Node> directories = [root];
        List<Node> level = [root];
        for (int depth = 0; depth < 2; depth++)
        {
            List<Node> next = [];
            foreach (Node directory in level)
            {
                next.AddRange(directory.Children!);
            }

            directories.AddRange(next);
            level = next;
        }

        List<Node> leaves = [];
        foreach (Node directory in level)
        {
            leaves.AddRange(directory.Children!);
        }

        long position = 0;
        foreach (Node directory in directories)
        {
            directory.Offset = position;
            position += DirectorySize + (EntrySize * directory.Children!.Count);

            // The counts of named and numbered entries are 16 bit fields.
            int named = directory.Children.Count(static c => c.Id.IsNamed);
            if (named > ushort.MaxValue || directory.Children.Count - named > ushort.MaxValue)
            {
                throw new ExportException("A resource directory would have more than 65535 entries.");
            }
        }

        var strings = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (Node directory in directories)
        {
            foreach (Node child in directory.Children!)
            {
                if (child.Id.Name is { } name && !strings.ContainsKey(name))
                {
                    strings.Add(name, position);
                    position += 2 + (2L * name.Length);
                }
            }
        }

        position = PeImage.AlignUp(position, 4);
        foreach (Node leaf in leaves)
        {
            leaf.Offset = position;
            position += DataEntrySize;
        }

        position = PeImage.AlignUp(position, 8);
        foreach (Node leaf in leaves)
        {
            leaf.DataOffset = position;
            position = PeImage.AlignUp(position + leaf.Data.Bytes.Length, 8);
            if (position > MaxSerializedSize)
            {
                throw new ExportException("The resources are too big to be written into a resource section.");
            }
        }

        // The data entries hold RVAs (32 bit): the section must end inside the 4 GiB the format can address.
        if ((long)baseRva + position > uint.MaxValue)
        {
            throw new ExportException("The resource section would end past 4 GiB of address space.");
        }

        // Pass 2: write it down.
        byte[] output = new byte[position];
        foreach (Node directory in directories)
        {
            WriteDirectory(output, directory, strings);
        }

        foreach (Node leaf in leaves)
        {
            Span<byte> entry = output.AsSpan((int)leaf.Offset, DataEntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, baseRva + (uint)leaf.DataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)leaf.Data.Bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], leaf.Data.CodePage);
            leaf.Data.Bytes.CopyTo(output.AsSpan((int)leaf.DataOffset));
        }

        foreach ((string name, long at) in strings)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan((int)at), (ushort)name.Length);
            Encoding.Unicode.GetBytes(name, output.AsSpan((int)at + 2, name.Length * 2));
        }

        return output;
    }

    /// <summary>Reads one directory and everything below it. <paramref name="level"/> is 0 for the root (the entries are types), 1 (names), 2 (languages: the entries are data).</summary>
    private static void ReadDirectory(PeImage image, ReadOnlySpan<byte> region, long offset, int level, ResourceId type, ResourceId name, Dictionary<ResourceKey, ResourceData> entries, ref long copied, ref int visited)
    {
        Need(region, offset, DirectorySize);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(region[(int)(offset + 12)..]) + BinaryPrimitives.ReadUInt16LittleEndian(region[(int)(offset + 14)..]);
        Need(region, offset + DirectorySize, (long)EntrySize * count);
        visited += count;
        if (visited > MaxEntriesRead)
        {
            throw new ExportException("The executable's resource directory has more than 100000 entries, or entries that point back into it (it is damaged).");
        }

        for (int i = 0; i < count; i++)
        {
            int at = (int)(offset + DirectorySize + (EntrySize * i));
            uint nameField = BinaryPrimitives.ReadUInt32LittleEndian(region[at..]);
            uint target = BinaryPrimitives.ReadUInt32LittleEndian(region[(at + 4)..]);
            bool named = (nameField & HighBit) != 0;
            bool subdirectory = (target & HighBit) != 0;
            long targetOffset = target & ~HighBit;
            ResourceId id = named ? ReadName(image, region, nameField & ~HighBit, ref copied) : ResourceId.FromId((int)nameField);

            if (level < 2)
            {
                if (!subdirectory)
                {
                    throw new ExportException("The executable's resources are not laid out as type, name, language: a data entry is too high in the tree.");
                }

                ReadDirectory(image, region, targetOffset, level + 1, level == 0 ? id : type, level == 1 ? id : name, entries, ref copied, ref visited);
            }
            else
            {
                if (subdirectory || named)
                {
                    throw new ExportException("The executable's resources are not laid out as type, name, language: a language is a name or a directory.");
                }

                ReadData(image, region, targetOffset, new ResourceKey(type, name, (int)nameField), entries, ref copied);
            }
        }
    }

    private static void ReadData(PeImage image, ReadOnlySpan<byte> region, long offset, ResourceKey key, Dictionary<ResourceKey, ResourceData> entries, ref long copied)
    {
        Need(region, offset, DataEntrySize);
        uint rva = BinaryPrimitives.ReadUInt32LittleEndian(region[(int)offset..]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(region[(int)(offset + 4)..]);
        uint codePage = BinaryPrimitives.ReadUInt32LittleEndian(region[(int)(offset + 8)..]);

        // The size is checked against the section before the copy is allocated; and the copies together cannot be bigger than the file, which entries sharing one blob could otherwise make.
        copied += size;
        byte[] data = [];
        if (size != 0)
        {
            if (copied > image.Bytes.Length || !image.TryRvaToOffset(rva, size, out int dataOffset))
            {
                throw new ExportException($"A resource of the executable ({key.Type}/{key.Name}/{key.Language}) is bigger than the file or outside its sections.");
            }

            data = image.Bytes.AsSpan(dataOffset, (int)size).ToArray();
        }

        if (!entries.TryAdd(key, new ResourceData(data, codePage)))
        {
            throw new ExportException($"The executable has the resource {key.Type}/{key.Name}/{key.Language} twice.");
        }
    }

    /// <summary>A resource name: at <paramref name="offset"/>, a 16 bit length in characters and then that many UTF-16 characters.</summary>
    private static ResourceId ReadName(PeImage image, ReadOnlySpan<byte> region, long offset, ref long copied)
    {
        Need(region, offset, 2);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(region[(int)offset..]);
        Need(region, offset + 2, 2L * length);

        // Entries may name the same string; the copies together stay below the file's size, like those of the data.
        copied += 2L * length;
        if (copied > image.Bytes.Length)
        {
            throw new ExportException("The executable's resource names are bigger than the file (it is damaged).");
        }

        return ResourceId.FromName(Encoding.Unicode.GetString(region.Slice((int)offset + 2, length * 2)));
    }

    private static void Need(ReadOnlySpan<byte> region, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset + length > region.Length)
        {
            throw new ExportException("The executable's resource directory points outside its resource section (it is truncated or damaged).");
        }
    }

    private static void WriteDirectory(byte[] output, Node directory, Dictionary<string, long> strings)
    {
        int named = 0;
        foreach (Node child in directory.Children!)
        {
            if (child.Id.IsNamed)
            {
                named++;
            }
        }

        // Characteristics(4) TimeDateStamp(4) MajorVersion(2) MinorVersion(2) NumberOfNamedEntries(2) NumberOfIdEntries(2), all zero but the counts.
        Span<byte> header = output.AsSpan((int)directory.Offset, DirectorySize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)named);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)(directory.Children.Count - named));
        for (int i = 0; i < directory.Children.Count; i++)
        {
            Node child = directory.Children[i];
            Span<byte> entry = output.AsSpan((int)directory.Offset + DirectorySize + (EntrySize * i), EntrySize);
            uint first = child.Id.Name is { } name ? HighBit | (uint)strings[name] : (uint)child.Id.Id;
            uint second = child.Children is not null ? HighBit | (uint)child.Offset : (uint)child.Offset;
            BinaryPrimitives.WriteUInt32LittleEndian(entry, first);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], second);
        }
    }

    /// <summary>The entries as a sorted three-level tree of nodes: the root's children are the types, theirs the names, theirs the languages (the leaves).</summary>
    private Node BuildNodes()
    {
        var root = new Node(default);
        var types = new SortedDictionary<ResourceId, SortedDictionary<ResourceId, SortedDictionary<int, ResourceData>>>();
        foreach ((ResourceKey key, ResourceData data) in _entries)
        {
            if (!types.TryGetValue(key.Type, out var names))
            {
                types[key.Type] = names = [];
            }

            if (!names.TryGetValue(key.Name, out var languages))
            {
                names[key.Name] = languages = [];
            }

            languages[key.Language] = data;
        }

        root.Children = [];
        foreach ((ResourceId type, var names) in types)
        {
            var typeNode = new Node(type) { Children = [] };
            root.Children.Add(typeNode);
            foreach ((ResourceId name, var languages) in names)
            {
                var nameNode = new Node(name) { Children = [] };
                typeNode.Children.Add(nameNode);
                foreach ((int language, ResourceData data) in languages)
                {
                    nameNode.Children.Add(new Node(ResourceId.FromId(language)) { Data = data });
                }
            }
        }

        return root;
    }

    private sealed class Node(ResourceId id)
    {
        public ResourceId Id { get; } = id;

        /// <summary>The entries of a directory; null for a leaf.</summary>
        public List<Node>? Children { get; set; }

        public ResourceData Data { get; init; }

        /// <summary>Where the directory or data entry is, in the section.</summary>
        public long Offset { get; set; }

        /// <summary>Where a leaf's data is, in the section.</summary>
        public long DataOffset { get; set; }
    }
}
