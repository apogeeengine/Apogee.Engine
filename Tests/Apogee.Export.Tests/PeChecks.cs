using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Apogee.Export.Tests;

/// <summary>What a Windows loader (or a careful tool) would insist on in a PE file, checked with <see cref="PEReader"/> and plain arithmetic, apart from the code that writes the files.</summary>
internal static class PeChecks
{
    /// <summary>The checksum of a file as imagehlp defines it, summed with the carries added at the end instead of after each word. <paramref name="field"/> is the offset of the CheckSum field, which counts as zero.</summary>
    public static uint CheckSumOf(byte[] file, int field)
    {
        ulong sum = 0;
        for (int i = 0; i < file.Length; i += 2)
        {
            if (i == field || i == field + 2)
            {
                continue;
            }

            sum += i + 1 < file.Length ? BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(i)) : file[i];
        }

        while (sum > 0xFFFF)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (uint)sum + (uint)file.Length;
    }

    /// <summary>
    /// The layout rules of the loader: the sections follow one another in memory with no gap (each starts at the aligned end of the one before, the first after the headers), are aligned in the
    /// file and do not overlap, <c>SizeOfImage</c> is the aligned end of the last, the resource directory lies in the <c>.rsrc</c> section, every resource directory is ordered (named entries first, as
    /// many as it says, by name without regard to case; then numbers, ascending), and the checksum is right.
    /// </summary>
    public static PEHeaders AssertConsistent(byte[] file)
    {
        using var reader = new PEReader(new MemoryStream(file));
        PEHeaders headers = reader.PEHeaders;
        PEHeader pe = headers.PEHeader!;
        long nextVirtual = TinyPe.AlignUp(pe.SizeOfHeaders, pe.SectionAlignment);
        long previousRawEnd = pe.SizeOfHeaders;
        foreach (SectionHeader section in headers.SectionHeaders)
        {
            Assert.Equal(nextVirtual, section.VirtualAddress); // no gap, no overlap
            nextVirtual = TinyPe.AlignUp((long)section.VirtualAddress + (section.VirtualSize != 0 ? section.VirtualSize : section.SizeOfRawData), pe.SectionAlignment);
            if (section.SizeOfRawData != 0)
            {
                Assert.Equal(0, section.PointerToRawData % pe.FileAlignment);
                Assert.Equal(0, section.SizeOfRawData % pe.FileAlignment);
                Assert.True(section.PointerToRawData >= previousRawEnd, $"section {section.Name} overlaps the one before it in the file");
                previousRawEnd = (long)section.PointerToRawData + section.SizeOfRawData;
                Assert.True(previousRawEnd <= file.Length);
            }
        }

        Assert.Equal(previousRawEnd, file.Length);
        Assert.Equal(nextVirtual, pe.SizeOfImage);

        DirectoryEntry resources = pe.ResourceTableDirectory;
        if (resources.Size != 0)
        {
            SectionHeader holder = headers.SectionHeaders.Single(s => resources.RelativeVirtualAddress >= s.VirtualAddress && resources.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
            Assert.True((long)resources.RelativeVirtualAddress + resources.Size <= (long)holder.VirtualAddress + holder.VirtualSize);
            Assert.Equal(".rsrc", holder.Name);
            TestResources.AssertOrdered(file);
        }

        int field = headers.PEHeaderStartOffset + 64;
        Assert.Equal(CheckSumOf(file, field), pe.CheckSum);
        return headers;
    }
}
