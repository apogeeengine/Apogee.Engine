using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using Xunit;

namespace Weft.Export.Tests;

/// <summary>One resource of a PE file as the test-side reader found it, in file order.</summary>
/// <param name="Type">The type: <c>#3</c> for a number, the string itself for a name.</param>
/// <param name="Name">The name, in the same notation.</param>
/// <param name="Language">The language id.</param>
/// <param name="Data">The bytes.</param>
internal sealed record TestResource(string Type, string Name, int Language, byte[] Data)
{
    /// <summary>The resource's place as one string: <c>#3/#1/1033</c>.</summary>
    public string Key => $"{Type}/{Name}/{Language}";
}

/// <summary>
/// A resource reader and a resource writer for the tests, written apart from the code they check: the reader goes through <see cref="PEReader"/> (which finds the section), the writer lays the
/// directories out depth-first, where the product writes them level by level.
/// </summary>
internal static class TestResources
{
    /// <summary>How a number is written in a <see cref="TestResource.Type"/> or <see cref="TestResource.Name"/>.</summary>
    public static string Id(int number) => "#" + number;

    /// <summary>The resource section for <paramref name="items"/> (a type and a name are an <c>int</c> or a <c>string</c>), for a section at <paramref name="rva"/>.</summary>
    public static byte[] Build(IReadOnlyList<(object Type, object Name, int Language, byte[] Data)> items, uint rva)
    {
        var output = new List<byte>();

        void Align(int boundary)
        {
            while (output.Count % boundary != 0)
            {
                output.Add(0);
            }
        }

        void Put32(int at, uint value)
        {
            for (int i = 0; i < 4; i++)
            {
                output[at + i] = (byte)(value >> (8 * i));
            }
        }

        int NewDirectory(int named, int ids)
        {
            Align(4);
            int at = output.Count;
            output.AddRange(new byte[16 + (8 * (named + ids))]);
            output[at + 12] = (byte)named;
            output[at + 14] = (byte)ids;
            return at;
        }

        // The name field of entry k of the directory at 'directory': a number, or the offset of a string (with the high bit).
        void PutKey(int directory, int k, object key)
        {
            if (key is string text)
            {
                Align(2);
                int at = output.Count;
                output.AddRange(BitConverter.GetBytes((ushort)text.Length));
                output.AddRange(Encoding.Unicode.GetBytes(text));
                Put32(directory + 16 + (8 * k), 0x8000_0000 | (uint)at);
            }
            else
            {
                Put32(directory + 16 + (8 * k), (uint)(int)key);
            }
        }

        static IOrderedEnumerable<T> Sorted<T>(IEnumerable<T> keys, Func<T, object> key) => keys
            .OrderBy(k => key(k) is string ? 0 : 1)
            .ThenBy(k => key(k) is string s ? s.ToUpperInvariant() : "", StringComparer.Ordinal)
            .ThenBy(k => key(k) is int i ? i : 0);

        int Count(IEnumerable<object> keys, bool named) => keys.Count(k => (k is string) == named);

        List<object> types = [.. Sorted(items.Select(i => i.Type).Distinct(), k => k)];
        int root = NewDirectory(Count(types, true), Count(types, false));
        for (int t = 0; t < types.Count; t++)
        {
            PutKey(root, t, types[t]);
            var ofType = items.Where(i => i.Type.Equals(types[t])).ToList();
            List<object> names = [.. Sorted(ofType.Select(i => i.Name).Distinct(), k => k)];
            int typeDirectory = NewDirectory(Count(names, true), Count(names, false));
            Put32(root + 16 + (8 * t) + 4, 0x8000_0000 | (uint)typeDirectory);
            for (int n = 0; n < names.Count; n++)
            {
                PutKey(typeDirectory, n, names[n]);
                var ofName = ofType.Where(i => i.Name.Equals(names[n])).OrderBy(i => i.Language).ToList();
                int nameDirectory = NewDirectory(0, ofName.Count);
                Put32(typeDirectory + 16 + (8 * n) + 4, 0x8000_0000 | (uint)nameDirectory);
                for (int l = 0; l < ofName.Count; l++)
                {
                    Put32(nameDirectory + 16 + (8 * l), (uint)ofName[l].Language);
                    Align(4);
                    int entry = output.Count;
                    Put32(nameDirectory + 16 + (8 * l) + 4, (uint)entry);
                    output.AddRange(new byte[16]);
                    Align(8);
                    int data = output.Count;
                    output.AddRange(ofName[l].Data);
                    Put32(entry, rva + (uint)data);
                    Put32(entry + 4, (uint)ofName[l].Data.Length);
                }
            }
        }

        Align(8);
        return [.. output];
    }

    /// <summary>The resources of a PE file, in the order of its directories, read through <see cref="PEReader"/>; none when it has no resource directory.</summary>
    public static List<TestResource> Read(byte[] file)
    {
        using var reader = new PEReader(new MemoryStream(file));
        DirectoryEntry directory = reader.PEHeaders.PEHeader!.ResourceTableDirectory;
        List<TestResource> found = [];
        if (directory.Size == 0)
        {
            return found;
        }

        byte[] section = reader.GetSectionData(directory.RelativeVirtualAddress).GetContent().ToArray();
        string Key(uint field) => (field & 0x8000_0000) != 0
            ? Encoding.Unicode.GetString(section, (int)(field & 0x7FFF_FFFF) + 2, BinaryPrimitives.ReadUInt16LittleEndian(section.AsSpan((int)(field & 0x7FFF_FFFF))) * 2)
            : Id((int)field);

        List<(string Key, uint Target)> Entries(int at)
        {
            int count = BinaryPrimitives.ReadUInt16LittleEndian(section.AsSpan(at + 12)) + BinaryPrimitives.ReadUInt16LittleEndian(section.AsSpan(at + 14));
            return [.. Enumerable.Range(0, count).Select(i => (Key(BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(at + 16 + (8 * i)))), BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(at + 20 + (8 * i)))))];
        }

        foreach ((string type, uint typeTarget) in Entries(0))
        {
            foreach ((string name, uint nameTarget) in Entries((int)(typeTarget & 0x7FFF_FFFF)))
            {
                int languages = (int)(nameTarget & 0x7FFF_FFFF);
                int count = BinaryPrimitives.ReadUInt16LittleEndian(section.AsSpan(languages + 12)) + BinaryPrimitives.ReadUInt16LittleEndian(section.AsSpan(languages + 14));
                for (int i = 0; i < count; i++)
                {
                    int language = (int)BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(languages + 16 + (8 * i)));
                    int entry = (int)BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(languages + 20 + (8 * i)));
                    uint dataRva = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(entry));
                    int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(entry + 4));
                    found.Add(new TestResource(type, name, language, reader.GetSectionData((int)dataRva).GetContent(0, size).ToArray()));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Checks every directory of the file's resource tree the way the loader's binary search needs it: exactly as many entries with a name (the high bit of the first word) as <c>NumberOfNamedEntries</c>
    /// says, and they come first, ordered by name without regard to case; the numbered ones follow, ascending; and the tree is three levels deep.
    /// </summary>
    public static void AssertOrdered(byte[] file)
    {
        using var reader = new PEReader(new MemoryStream(file));
        DirectoryEntry directory = reader.PEHeaders.PEHeader!.ResourceTableDirectory;
        byte[] section = reader.GetSectionData(directory.RelativeVirtualAddress).GetContent().ToArray();

        void Walk(int at, int depth)
        {
            Assert.True(depth < 3, "the tree is deeper than type/name/language");
            int named = BinaryPrimitives.ReadUInt16LittleEndian(section.AsSpan(at + 12));
            int ids = BinaryPrimitives.ReadUInt16LittleEndian(section.AsSpan(at + 14));
            string? previousName = null;
            long previousId = -1;
            for (int i = 0; i < named + ids; i++)
            {
                uint field = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(at + 16 + (8 * i)));
                uint target = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(at + 20 + (8 * i)));
                Assert.Equal(i < named, (field & 0x8000_0000) != 0); // the named entries are exactly the first 'named'
                if (i < named)
                {
                    int offset = (int)(field & 0x7FFF_FFFF);
                    string name = Encoding.Unicode.GetString(section, offset + 2, BinaryPrimitives.ReadUInt16LittleEndian(section.AsSpan(offset)) * 2).ToUpperInvariant();
                    Assert.True(previousName is null || string.CompareOrdinal(previousName, name) < 0, "names are not in order");
                    previousName = name;
                }
                else
                {
                    Assert.True(field > previousId, "numbers are not in ascending order");
                    previousId = field;
                }

                Assert.Equal(depth < 2, (target & 0x8000_0000) != 0); // directories above the languages, data at the languages
                if (depth < 2)
                {
                    Walk((int)(target & 0x7FFF_FFFF), depth + 1);
                }
            }
        }

        Walk(0, 0);
    }

    /// <summary>The one resource at (<paramref name="type"/>, <paramref name="name"/>, any language) of a file; fails the test if there is not exactly one.</summary>
    public static byte[] Single(byte[] file, int type, int name)
    {
        TestResource[] matches = [.. Read(file).Where(r => r.Type == Id(type) && r.Name == Id(name))];
        return matches.Length == 1 ? matches[0].Data : throw new InvalidOperationException($"{matches.Length} resources of type {type} named {name}.");
    }
}
