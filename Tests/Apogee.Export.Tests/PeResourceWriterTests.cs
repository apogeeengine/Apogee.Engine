using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Apogee.Export.Windows;
using Xunit;

namespace Apogee.Export.Tests;

public class PeResourceWriterTests
{
    private static readonly byte[] s_slot = [.. PackKeySlot.Template.ToArray()];

    private static readonly IReadOnlyCollection<int> s_none = [];

    private static Dictionary<ResourceKey, byte[]> Replace(params (int Type, int Name, int Language, byte[] Data)[] items) =>
        items.ToDictionary(i => new ResourceKey(i.Type, i.Name, i.Language), i => i.Data);

    private static readonly Dictionary<ResourceKey, byte[]> s_version = Replace((16, 1, 0x409, [1, 2, 3, 4, 5]));

    /// <summary>A runtime-like file: the pack key slot in the code, a manifest, and <c>.reloc</c> last.</summary>
    private static byte[] Typical(ushort subsystem = 3, byte[]? certificate = null) => TinyPe.Typical([.. new byte[50], .. s_slot, .. new byte[50]], subsystem, certificate).Build();

    private static byte[] Rewrite(byte[] file, IReadOnlyDictionary<ResourceKey, byte[]>? replace = null, IReadOnlyCollection<int>? remove = null, bool gui = true, List<string>? warnings = null) =>
        PeResourceWriter.Rewrite(file, replace ?? s_version, remove ?? s_none, gui, warnings ?? []);

    /// <summary>The checksum of a file as imagehlp defines it, written again here with the carries added at the end instead of after each word.</summary>
    private static uint CheckSumOf(byte[] file, int field)
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

    /// <summary>Everything a loader checks about the layout: sections in order, aligned, not overlapping, inside the file and the image; the resource directory inside its section; the checksum.</summary>
    private static PEHeaders AssertConsistent(byte[] file)
    {
        using var reader = new PEReader(new MemoryStream(file));
        PEHeaders headers = reader.PEHeaders;
        PEHeader pe = headers.PEHeader!;
        long previousVirtualEnd = 0;
        long previousRawEnd = pe.SizeOfHeaders;
        foreach (SectionHeader section in headers.SectionHeaders)
        {
            Assert.Equal(0, section.VirtualAddress % pe.SectionAlignment);
            Assert.True(section.VirtualAddress >= previousVirtualEnd, $"section {section.Name} overlaps the one before it in memory");
            previousVirtualEnd = (long)section.VirtualAddress + section.VirtualSize;
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
        Assert.Equal(TinyPe.AlignUp(previousVirtualEnd, pe.SectionAlignment), pe.SizeOfImage);

        DirectoryEntry resources = pe.ResourceTableDirectory;
        if (resources.Size != 0)
        {
            SectionHeader holder = headers.SectionHeaders.Single(s => resources.RelativeVirtualAddress >= s.VirtualAddress && resources.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
            Assert.True((long)resources.RelativeVirtualAddress + resources.Size <= (long)holder.VirtualAddress + holder.VirtualSize);
            Assert.Equal(".rsrc", holder.Name);
        }

        int field = headers.PEHeaderStartOffset + 64;
        Assert.Equal(CheckSumOf(file, field), pe.CheckSum);
        return headers;
    }

    private static Dictionary<string, byte[]> Resources(byte[] file) => TestResources.Read(file).ToDictionary(r => r.Key, r => r.Data);

    [Fact]
    public void TheNewResourceIsThereAndTheManifestIsKept()
    {
        byte[] input = Typical();

        byte[] output = Rewrite(input);

        Dictionary<string, byte[]> resources = Resources(output);
        Assert.Equal(["#16/#1/1033", "#24/#1/1033"], resources.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([1, 2, 3, 4, 5], resources["#16/#1/1033"]);
        Assert.Equal("<assembly>a manifest</assembly>"u8.ToArray(), resources["#24/#1/1033"]); // byte-identical, although it moved
        Assert.Equal(Resources(input)["#24/#1/1033"], resources["#24/#1/1033"]);
    }

    [Fact]
    public void TheImageStaysConsistent()
    {
        AssertConsistent(Rewrite(Typical())); // .rsrc is not last: a section is added
        AssertConsistent(Rewrite(Typical(), Replace((3, 1, 0x409, new byte[5000]), (14, 1, 0x409, new byte[40])))); // a bigger section
        AssertConsistent(Rewrite(new TinyPe { MinHeaderSize = 0x400 }.Section(".text", new byte[10], TinyPe.TextFlags).Build())); // no resources at all
        AssertConsistent(Rewrite(new TinyPe { MinHeaderSize = 0x400, Pe32Plus = false }.Section(".text", new byte[10], TinyPe.TextFlags).Build())); // PE32
        AssertConsistent(Rewrite(new TinyPe { MinHeaderSize = 0x400, FileAlignment = 0x1000, SectionAlignment = 0x2000 }.Section(".text", new byte[10], TinyPe.TextFlags).Build())); // another alignment

        // The input of a linker that is already consistent keeps being so when rewritten again and again.
        byte[] twice = Rewrite(Rewrite(Typical()), Replace((16, 1, 0x409, new byte[999])));
        AssertConsistent(twice);
    }

    [Fact]
    public void AnAppendedSectionKeepsEverythingElseWhereItIs()
    {
        byte[] input = Typical();
        PEHeaders before = new PEReader(new MemoryStream(input)).PEHeaders;

        byte[] output = Rewrite(input);

        PEHeaders after = new PEReader(new MemoryStream(output)).PEHeaders;
        Assert.Equal(before.SectionHeaders.Length + 1, after.SectionHeaders.Length);
        Assert.Equal([".text", ".rdata", ".rsrc0", ".reloc", ".rsrc"], after.SectionHeaders.Select(s => s.Name));
        for (int i = 0; i < before.SectionHeaders.Length; i++)
        {
            SectionHeader a = before.SectionHeaders[i];
            SectionHeader b = after.SectionHeaders[i];
            Assert.Equal((a.VirtualAddress, a.VirtualSize, a.PointerToRawData, a.SizeOfRawData, a.SectionCharacteristics), (b.VirtualAddress, b.VirtualSize, b.PointerToRawData, b.SizeOfRawData, b.SectionCharacteristics));
            Assert.Equal(input.AsSpan((int)a.PointerToRawData, a.SizeOfRawData).ToArray(), output.AsSpan((int)b.PointerToRawData, b.SizeOfRawData).ToArray());
        }

        // The pack key slot is where it was, and still found exactly once.
        Assert.Equal(FakeRuntime.SlotOffset(input), FakeRuntime.SlotOffset(output));
        Assert.Equal(output.AsSpan().IndexOf(PackKeySlot.Template[..16]), output.AsSpan().LastIndexOf(PackKeySlot.Template[..16]));
    }

    [Fact]
    public void AResourceSectionThatIsLastIsRewrittenInPlace()
    {
        byte[] input = new TinyPe { MinHeaderSize = 0x400 }
            .Section(".text", new byte[100], TinyPe.TextFlags)
            .Resources([(24, 1, 0x409, new byte[40]), (3, 5, 0x409, new byte[2000])])
            .Build();
        PEHeaders before = new PEReader(new MemoryStream(input)).PEHeaders;

        byte[] output = Rewrite(input, Replace((3, 5, 0x409, new byte[10]))); // smaller

        PEHeaders after = new PEReader(new MemoryStream(output)).PEHeaders;
        Assert.Equal([".text", ".rsrc"], after.SectionHeaders.Select(s => s.Name));
        Assert.Equal(before.SectionHeaders[1].VirtualAddress, after.SectionHeaders[1].VirtualAddress);
        Assert.Equal(before.SectionHeaders[1].PointerToRawData, after.SectionHeaders[1].PointerToRawData);
        Assert.True(output.Length < input.Length);
        AssertConsistent(output);

        byte[] bigger = Rewrite(input, Replace((3, 5, 0x409, new byte[20000])));
        Assert.True(bigger.Length > input.Length);
        AssertConsistent(bigger);
        Assert.Equal(20000, Resources(bigger)["#3/#5/1033"].Length);
    }

    [Fact]
    public void ResourcesOfTheDroppedTypesAreGone()
    {
        byte[] input = new TinyPe { MinHeaderSize = 0x400 }
            .Section(".text", new byte[100], TinyPe.TextFlags)
            .Resources([(3, 1, 0, [1]), (3, 2, 0, [2]), (14, 1, 0, [3]), (16, 1, 0, [4]), (24, 1, 0x409, [5])])
            .Build();

        Dictionary<string, byte[]> resources = Resources(Rewrite(input, Replace((16, 1, 0x409, [9])), [3, 14, 16]));

        Assert.Equal(["#16/#1/1033", "#24/#1/1033"], resources.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NamesAreWrittenAndSortedWithoutRegardToCase()
    {
        byte[] input = new TinyPe { MinHeaderSize = 0x400 }
            .Section(".text", new byte[100], TinyPe.TextFlags)
            .Resources([("zeta", "b", 0, [1]), ("Alpha", 7, 0, [2]), (24, "Name", 0x409, [3]), ("alpha2", 1, 0, [4])])
            .Build();

        List<TestResource> output = TestResources.Read(Rewrite(input, Replace((16, 1, 0x409, [9]))));

        // Names first, ordered as the loader's search wants (case does not count), then numbers.
        Assert.Equal(["Alpha", "alpha2", "zeta", "#16", "#24"], output.Select(r => r.Type));
        Assert.Equal("Name", output.Single(r => r.Type == "#24").Name);
        Assert.Equal([2], output[0].Data);
        Assert.Equal([3], output.Single(r => r.Type == "#24").Data);
    }

    [Fact]
    public void WritingTwiceGivesTheSameFileAndTheSameFileAgain()
    {
        byte[] input = Typical();

        byte[] first = Rewrite(input);
        byte[] second = Rewrite(input);

        Assert.Equal(first, second);

        // And a rewrite of the output with the same resources changes nothing (the resource section it wrote is last and is rewritten where it is).
        Assert.Equal(first, Rewrite(first));
    }

    [Fact]
    public void TheInputIsNotChanged()
    {
        byte[] input = Typical();
        byte[] copy = [.. input];

        Rewrite(input);

        Assert.Equal(copy, input);
    }

    [Fact]
    public void TheSubsystemIsGuiUnlessConsoleIsAsked()
    {
        byte[] console = Typical(subsystem: 3);

        Assert.Equal(2, Subsystem(Rewrite(console, gui: true)));
        Assert.Equal(3, Subsystem(Rewrite(console, gui: false)));
        Assert.Equal(2, Subsystem(Rewrite(Typical(subsystem: 2), gui: false))); // a program that is windowed is not made a console program
        Assert.Equal(2, Subsystem(Rewrite(Typical(subsystem: 2), gui: true)));
    }

    private static int Subsystem(byte[] file) => (int)new PEReader(new MemoryStream(file)).PEHeaders.PEHeader!.Subsystem;

    [Fact]
    public void TheCheckSumIsRecomputedWhateverItWas()
    {
        byte[] input = new TinyPe { MinHeaderSize = 0x400, CheckSum = 0xDEADBEEF }.Section(".text", new byte[100], TinyPe.TextFlags).Build();

        byte[] output = Rewrite(input);

        AssertConsistent(output); // compares with the independent sum
        Assert.NotEqual(0xDEADBEEFu, new PEReader(new MemoryStream(output)).PEHeaders.PEHeader!.CheckSum);

    }

    [Fact]
    public void TheCheckSumOfAFileOfOddLengthTakesTheLastByteAsAWord()
    {
        // Alignments of 1 let the file end on any byte.
        byte[] file = new TinyPe { FileAlignment = 1, SectionAlignment = 1 }.Section(".text", new byte[101], TinyPe.TextFlags).Build();
        Assert.Equal(1, file.Length % 2);
        file[^1] = 0x9C; // a last byte that is not zero
        PeImage image = PeImage.Parse(file);

        image.UpdateCheckSum();

        Assert.Equal(CheckSumOf(image.Bytes, 0x40 + 24 + 64), BinaryPrimitives.ReadUInt32LittleEndian(image.Bytes.AsSpan(0x40 + 24 + 64)));
    }

    [Fact]
    public void ASignatureIsStrippedWithAWarning()
    {
        byte[] input = Typical(certificate: [.. Enumerable.Repeat((byte)0x5A, 600)]);
        List<string> warnings = [];

        byte[] output = Rewrite(input, warnings: warnings);

        Assert.Contains(warnings, w => w.Contains("signature", StringComparison.Ordinal));
        Assert.True(output.Length < input.Length);
        PEHeaders headers = AssertConsistent(output);
        Assert.Equal(0, headers.PEHeader!.CertificateTableDirectory.Size);
        Assert.Equal(0, headers.PEHeader.CertificateTableDirectory.RelativeVirtualAddress);
        Assert.Equal(-1, output.AsSpan().IndexOf(Enumerable.Repeat((byte)0x5A, 600).ToArray()));
    }

    [Fact]
    public void AFileWithNoSignatureGivesNoWarning()
    {
        List<string> warnings = [];

        Rewrite(Typical(), warnings: warnings);

        Assert.Empty(warnings);
    }

    [Fact]
    public void OverlayThatIsNotASignatureIsRefused()
    {
        byte[] withOverlay = new TinyPe { MinHeaderSize = 0x400, Overlay = [1, 2, 3, 4] }.Section(".text", new byte[100], TinyPe.TextFlags).Build();
        Assert.Contains("overlay", Assert.Throws<ExportException>(() => Rewrite(withOverlay)).Message, StringComparison.Ordinal);

        // Data after the signature too, and a signature that is not at the end.
        byte[] afterSignature = new TinyPe { MinHeaderSize = 0x400, Certificate = new byte[64], Overlay = [9, 9, 9] }.Section(".text", new byte[100], TinyPe.TextFlags).Build();
        Assert.Throws<ExportException>(() => Rewrite(afterSignature));
    }

    [Fact]
    public void NoHeaderRoomIsAClearError()
    {
        // With the default header size the section table of four sections ends 24 bytes before the first section's data: no room for the fifth header.
        byte[] tight = new TinyPe()
            .Section(".text", new byte[100], TinyPe.TextFlags)
            .Resources([(24, 1, 0x409, new byte[10])])
            .Section(".reloc", new byte[10], TinyPe.RelocFlags)
            .Section(".data", new byte[10], TinyPe.DataFlags)
            .Build();

        Assert.Contains("room", Assert.Throws<ExportException>(() => Rewrite(tight)).Message, StringComparison.Ordinal);

        // Three sections leave room for one more.
        byte[] roomy = new TinyPe()
            .Section(".text", new byte[100], TinyPe.TextFlags)
            .Resources([(24, 1, 0x409, new byte[10])])
            .Section(".reloc", new byte[10], TinyPe.RelocFlags)
            .Build();
        AssertConsistent(Rewrite(roomy));

        // And a resource section that is last needs no room at all.
        byte[] inPlace = new TinyPe()
            .Section(".text", new byte[100], TinyPe.TextFlags)
            .Section(".data", new byte[100], TinyPe.DataFlags)
            .Section(".pdata", new byte[100], TinyPe.DataFlags)
            .Resources([(24, 1, 0x409, new byte[10])])
            .Build();
        AssertConsistent(Rewrite(inPlace));
    }

    [Fact]
    public void ARealAssemblyIsReadLikeThePEReaderReadsIt()
    {
        // The test assembly is a PE32 file made by the compiler: .text, .rsrc and .reloc, the resource section not last, and headers with no spare room (SizeOfHeaders 0x200).
        byte[] input = File.ReadAllBytes(typeof(PeResourceWriterTests).Assembly.Location);
        List<TestResource> expected = TestResources.Read(input);
        Assert.NotEmpty(expected);

        ResourceTree tree = ResourceTree.Read(PeImage.Parse(input));

        Assert.Equal(expected.Count, tree.Entries.Count);
        foreach (TestResource resource in expected)
        {
            Assert.Equal(resource.Data, tree.Entries.Single(e => e.Key.Type.ToString() == resource.Type.TrimStart('#') && e.Key.Name.ToString() == resource.Name.TrimStart('#') && e.Key.Language == resource.Language).Value.Bytes);
        }

        // Written out and read back, with the independent reader, the same resources come out; the file is not changed (it is read from a copy).
        byte[] copy = [.. input];
        try
        {
            byte[] output = Rewrite(input, Replace((16, 1, 0x409, [8])), [16]);
            AssertConsistent(output);
            Assert.Equal(expected.Count(r => r.Type != "#16") + 1, TestResources.Read(output).Count);
            using var reader = new PEReader(new MemoryStream(output));
            MetadataReader metadata = reader.GetMetadataReader(); // the managed code is where its metadata says
            Assert.Equal("Apogee.Export.Tests", metadata.GetString(metadata.GetAssemblyDefinition().Name));
        }
        catch (ExportException e)
        {
            Assert.Contains("room", e.Message, StringComparison.Ordinal); // the compiler leaves no room for a header: said so, not guessed
        }

        Assert.Equal(copy, input);
    }

    // Damaged and hostile files: every one is an ExportException, quickly, with no huge allocation.

    [Fact]
    public void EveryTruncatedFileIsRefused()
    {
        byte[] file = Typical();

        for (int length = 0; length < file.Length; length++)
        {
            byte[] cut = file[..length];
            Assert.Throws<ExportException>(() => Rewrite(cut));
        }
    }

    [Theory]
    [InlineData("NotMz")]
    [InlineData("PeOffsetHuge")]
    [InlineData("PeOffsetMisaligned")]
    [InlineData("NoPeSignature")]
    [InlineData("TooManySections")]
    [InlineData("SectionTableOutsideHeaders")]
    [InlineData("OptionalHeaderHuge")]
    [InlineData("OptionalHeaderTiny")]
    [InlineData("BadMagic")]
    [InlineData("ZeroFileAlignment")]
    [InlineData("OddSectionAlignment")]
    [InlineData("FileAlignmentAboveSectionAlignment")]
    [InlineData("HeadersPastTheEnd")]
    [InlineData("SectionRawSizeHuge")]
    [InlineData("SectionRawPointerHuge")]
    [InlineData("SectionsOutOfOrder")]
    [InlineData("ResourceDirectoryNowhere")]
    [InlineData("RootEntryCountHuge")]
    [InlineData("EntryPointsToItself")]
    [InlineData("DataSizeHuge")]
    [InlineData("DataOutsideSections")]
    [InlineData("NameOutsideDirectory")]
    [InlineData("NameLengthHuge")]
    [InlineData("AliasedDirectories")]
    public void AHostileFileIsAnExportException(string damage)
    {
        byte[] file = Typical();
        PEHeaders headers = new PEReader(new MemoryStream(file)).PEHeaders;
        int pe = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x3C));
        int optional = pe + 24;
        int table = optional + 240;
        SectionHeader rsrc = headers.SectionHeaders.Single(s => s.Name == ".rsrc");
        int rsrcAt = (int)rsrc.PointerToRawData;
        Span<byte> bytes = file;

        switch (damage)
        {
            case "NotMz": bytes[0] = (byte)'X'; break;
            case "PeOffsetHuge": BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x3C..], 0xFFFFFFF0); break;
            case "PeOffsetMisaligned": BinaryPrimitives.WriteInt32LittleEndian(bytes[0x3C..], pe + 2); break;
            case "NoPeSignature": bytes[pe] = 0; break;
            case "TooManySections": BinaryPrimitives.WriteUInt16LittleEndian(bytes[(pe + 6)..], 0xFFFF); break;
            case "SectionTableOutsideHeaders": BinaryPrimitives.WriteUInt16LittleEndian(bytes[(pe + 6)..], 80); break;
            case "OptionalHeaderHuge": BinaryPrimitives.WriteUInt16LittleEndian(bytes[(pe + 20)..], 0xFFFF); break;
            case "OptionalHeaderTiny": BinaryPrimitives.WriteUInt16LittleEndian(bytes[(pe + 20)..], 20); break;
            case "BadMagic": BinaryPrimitives.WriteUInt16LittleEndian(bytes[optional..], 0x107); break;
            case "ZeroFileAlignment": BinaryPrimitives.WriteUInt32LittleEndian(bytes[(optional + 36)..], 0); break;
            case "OddSectionAlignment": BinaryPrimitives.WriteUInt32LittleEndian(bytes[(optional + 32)..], 0x1800); break;
            case "FileAlignmentAboveSectionAlignment": BinaryPrimitives.WriteUInt32LittleEndian(bytes[(optional + 36)..], 0x10000); break;
            case "HeadersPastTheEnd": BinaryPrimitives.WriteUInt32LittleEndian(bytes[(optional + 60)..], 0xFFFFFFFF); break;
            case "SectionRawSizeHuge": BinaryPrimitives.WriteUInt32LittleEndian(bytes[(table + 16)..], 0xFFFFFFFF); break;
            case "SectionRawPointerHuge": BinaryPrimitives.WriteUInt32LittleEndian(bytes[(table + 20)..], 0xFFFFFFFF); break;
            case "SectionsOutOfOrder": BinaryPrimitives.WriteUInt32LittleEndian(bytes[(table + 12)..], 0x40000); break;
            case "ResourceDirectoryNowhere": BinaryPrimitives.WriteUInt32LittleEndian(bytes[(optional + 112 + 16)..], 0x7000_0000); break;
            case "RootEntryCountHuge": BinaryPrimitives.WriteUInt16LittleEndian(bytes[(rsrcAt + 14)..], 0xFFFF); break;
            case "EntryPointsToItself": BinaryPrimitives.WriteUInt32LittleEndian(bytes[(rsrcAt + 20)..], 0x8000_0000); break; // the type directory is the root again
            case "DataSizeHuge": SetFirstDataEntry(bytes, rsrcAt, size: 0xFFFFFFFF); break;
            case "DataOutsideSections": SetFirstDataEntry(bytes, rsrcAt, rva: 0x7000_0000); break;
            case "NameOutsideDirectory": BinaryPrimitives.WriteUInt32LittleEndian(bytes[(rsrcAt + 16)..], 0x8000_0000 | 0x00FF_0000); break;
            case "NameLengthHuge": NameWithLength(bytes, rsrcAt, 0xFFFF); break;
            case "AliasedDirectories": file = AliasedDirectoriesFile(); break;
            default: throw new InvalidOperationException(damage);
        }

        Assert.Throws<ExportException>(() => Rewrite(file));
    }

    private static void SetFirstDataEntry(Span<byte> file, int rsrcAt, uint? size = null, uint? rva = null)
    {
        // root -> type dir -> name dir -> language entry -> data entry: follow the first entry of each.
        int type = (int)(BinaryPrimitives.ReadUInt32LittleEndian(file[(rsrcAt + 20)..]) & 0x7FFF_FFFF);
        int name = (int)(BinaryPrimitives.ReadUInt32LittleEndian(file[(rsrcAt + type + 20)..]) & 0x7FFF_FFFF);
        int entry = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[(rsrcAt + name + 20)..]);
        if (rva is { } r)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file[(rsrcAt + entry)..], r);
        }

        if (size is { } s)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file[(rsrcAt + entry + 4)..], s);
        }
    }

    private static void NameWithLength(Span<byte> file, int rsrcAt, ushort length)
    {
        // Make the root's first entry a name whose string (at the start of the directory, which is a header) claims 64 KiB of characters.
        BinaryPrimitives.WriteUInt32LittleEndian(file[(rsrcAt + 16)..], 0x8000_0000 | 0x20);
        BinaryPrimitives.WriteUInt16LittleEndian(file[(rsrcAt + 0x20)..], length);
        BinaryPrimitives.WriteUInt16LittleEndian(file[(rsrcAt + 12)..], 1); // one named entry, no ids
        BinaryPrimitives.WriteUInt16LittleEndian(file[(rsrcAt + 14)..], 0);
    }

    /// <summary>A resource section whose directories point to each other so that reading them naively takes forever: 65535 entries in the root, each to a directory of 65535 entries.</summary>
    private static byte[] AliasedDirectoriesFile()
    {
        const int Count = 0xFFFF;
        int level = 16 + (8 * Count);
        return new TinyPe { MinHeaderSize = 0x400 }
            .Section(".text", new byte[10], TinyPe.TextFlags)
            .RawResources(_ =>
            {
                byte[] data = new byte[level * 2 + 16];
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), Count);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(level + 14), Count);
                for (int i = 0; i < Count; i++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16 + (8 * i)), (uint)i);
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20 + (8 * i)), 0x8000_0000 | (uint)level);
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(level + 16 + (8 * i)), (uint)i);
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(level + 20 + (8 * i)), 0x8000_0000 | (uint)(level * 2)); // an empty directory
                }

                return data;
            })
            .Build();
    }

    [Fact]
    public void RandomDamageNeverEndsInAnythingButAnExportException()
    {
        byte[] original = Typical(certificate: new byte[100]);
        var random = new Random(20261114);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int refused = 0;

        for (int round = 0; round < 4000; round++)
        {
            byte[] file = [.. original];
            int flips = random.Next(1, 5);
            for (int i = 0; i < flips; i++)
            {
                // Mostly in the headers and the resource directory, where the fields are.
                int at = random.Next(3) == 0 ? random.Next(file.Length) : random.Next(0, 0x600);
                file[at] = (byte)random.Next(256);
            }

            try
            {
                Rewrite(file);
            }
            catch (ExportException)
            {
                refused++;
            }
        }

        Assert.True(refused > 100, "the damage should be noticed often");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }
}
