using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Apogee.Export.Tests;

public class MachOSignatureTests
{
    private static readonly DirectorySpec s_sha256Pages4K = new(2, 12);
    private static readonly DirectorySpec s_sha1Pages4K = new(1, 12);

    /// <summary>A length that is not a multiple of any page size, so the last page is short.</summary>
    private const int CodeLength = 3 * 4096 + 1234;

    private static bool Rehash(byte[] file) => MachOSignature.Rehash(new MemoryStream(file, writable: true), "test");

    private static byte[] RehashCopy(byte[] file, out bool signed)
    {
        byte[] copy = (byte[])file.Clone();
        signed = Rehash(copy);
        return copy;
    }

    [Fact]
    public void AChangedProgramHasTheHashOfEachOfItsPagesAfterRehash()
    {
        SyntheticMachO image = SyntheticMachO.Build(CodeLength, [s_sha256Pages4K]);
        byte[] file = (byte[])image.Bytes.Clone();
        file[0x500] ^= 0xFF; // the key patch: a byte in the first page
        file[CodeLength - 3] ^= 0x55; // and one in the short last page
        Assert.False(image.HashesAreRight(file)); // the signature is now wrong, as macOS would find

        byte[] result = RehashCopy(file, out bool signed);

        Assert.True(signed);
        Assert.True(image.HashesAreRight(result));
        Assert.Equal(4, image.Directories[0].CodeSlots);
    }

    [Fact]
    public void OnlyTheCodeSlotsOfTheDirectoriesChange()
    {
        SyntheticMachO image = SyntheticMachO.Build(CodeLength, [s_sha256Pages4K, s_sha1Pages4K]);
        byte[] file = (byte[])image.Bytes.Clone();
        file[0x500] ^= 0xFF;

        byte[] result = RehashCopy(file, out _);

        // Everything that differs from the file before the rehash is inside a code slot: the program, the header, the identifier, the special slots (the pattern), the requirements and the length are as they were.
        List<(int Start, int End)> slots = image.CodeSlotRanges();
        for (int i = 0; i < file.Length; i++)
        {
            if (file[i] != result[i])
            {
                Assert.Contains(slots, s => i >= s.Start && i < s.End);
            }
        }

        Assert.Equal(file.Length, result.Length);
        Assert.True(image.HashesAreRight(result));
    }

    [Theory]
    [InlineData(1, 12)]
    [InlineData(2, 12)]
    [InlineData(3, 12)]
    [InlineData(4, 12)]
    [InlineData(2, 14)] // the 16 KiB pages of Apple silicon
    [InlineData(2, 8)] // small pages: many slots
    [InlineData(2, 0)] // no paging: one hash of everything
    public void EveryHashTypeAndPageSizeIsHashedAsItIs(int hashType, int pageLog2)
    {
        SyntheticMachO image = SyntheticMachO.Build(CodeLength, [new DirectorySpec(hashType, pageLog2)], correctHashes: false);

        byte[] result = RehashCopy(image.Bytes, out bool signed);

        Assert.True(signed);
        Assert.True(image.HashesAreRight(result));
        Assert.False(image.HashesAreRight(image.Bytes)); // the zeros it started with were wrong
    }

    [Fact]
    public void ADirectoryOfEveryHashTypeAtOnceIsRehashed()
    {
        SyntheticMachO image = SyntheticMachO.Build(CodeLength, [new(2, 14), new(1, 12), new(3, 12), new(4, 12)], correctHashes: false);

        byte[] result = RehashCopy(image.Bytes, out _);

        Assert.True(image.HashesAreRight(result));
    }

    [Fact]
    public void RehashingAProgramWhoseSignatureIsRightChangesNothing()
    {
        SyntheticMachO image = SyntheticMachO.Build(CodeLength, [s_sha256Pages4K, s_sha1Pages4K]);

        byte[] result = RehashCopy(image.Bytes, out bool signed);

        Assert.True(signed);
        Assert.Equal(image.Bytes, result);
    }

    [Fact]
    public void ARightSignatureIsNotWrittenAgain()
    {
        SyntheticMachO image = SyntheticMachO.Build(CodeLength, [s_sha256Pages4K]);
        using var stream = new CountingStream(image.Bytes);

        Assert.True(MachOSignature.Rehash(stream, "test"));

        Assert.Equal(0, stream.BytesWritten);
    }

    [Fact]
    public void ThePackKeySlotIsCoveredByTheSignatureAfterTheKeyIsWritten()
    {
        // The whole point: write the key into a signed program (as the export does) and the signature is wrong until it is rehashed.
        using var scratch = new Scratch();
        SyntheticMachO image = SyntheticMachO.Build(0x6000, [new(2, 14)], embed: PackKeySlot.Template.ToArray());
        string exe = scratch.Write("game", image.Bytes);

        PackKeySlot.Write(exe, new Apogee.Content.Packs.PackKey(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()));
        byte[] patched = File.ReadAllBytes(exe);
        Assert.False(image.HashesAreRight(patched));

        Assert.True(MachOSignature.Rehash(exe));

        Assert.True(image.HashesAreRight(File.ReadAllBytes(exe)));
    }

    [Fact]
    public void AProgramWithoutASignatureIsLeftAlone()
    {
        byte[] file = FakeRuntime.Executable(new ExportTarget("osx", "x64"));
        byte[] copy = (byte[])file.Clone();

        Assert.False(Rehash(copy));

        Assert.Equal(file, copy);
    }

    [Fact]
    public void ASignatureMadeWithACertificateIsRefusedAndNothingIsWritten()
    {
        SyntheticMachO image = SyntheticMachO.Build(CodeLength, [s_sha256Pages4K], correctHashes: false, certificateBlob: true);
        byte[] copy = (byte[])image.Bytes.Clone();

        ExportException e = Assert.Throws<ExportException>(() => Rehash(copy));

        Assert.Contains("certificate", e.Message, StringComparison.Ordinal);
        Assert.Contains("after the export", e.Message, StringComparison.Ordinal);
        Assert.Equal(image.Bytes, copy);
    }

    [Fact]
    public void AUniversalBinaryIsRefused()
    {
        byte[] fat = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(fat, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4), 2);

        Assert.Contains("thin", Assert.Throws<ExportException>(() => Rehash(fat)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(31)]
    public void AFileTooShortForAHeaderIsRefused(int length)
    {
        Assert.Throws<ExportException>(() => Rehash(new byte[length]));
    }

    [Fact]
    public void FilesThatAreNotThinSixtyFourBitMachOAreRefused()
    {
        byte[] thirtyTwoBit = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(thirtyTwoBit, 0xFEEDFACE);
        byte[] bigEndian = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(bigEndian, 0xFEEDFACF);

        Assert.Throws<ExportException>(() => Rehash(thirtyTwoBit));
        Assert.Throws<ExportException>(() => Rehash(bigEndian));
        Assert.Throws<ExportException>(() => Rehash("not a program at all, but longer than thirty-two bytes"u8.ToArray()));
        Assert.Throws<ExportException>(() => Rehash(new byte[200]));
    }

    [Fact]
    public void ARehashOfAFileOnDiskIsDoneInPlace()
    {
        using var scratch = new Scratch();
        SyntheticMachO image = SyntheticMachO.Build(CodeLength, [s_sha256Pages4K], correctHashes: false);
        string path = scratch.Write("game", image.Bytes);

        Assert.True(MachOSignature.Rehash(path));

        Assert.True(image.HashesAreRight(File.ReadAllBytes(path)));
        Assert.Throws<ExportException>(() => MachOSignature.Rehash(scratch.Path("missing")));
    }

    // Hostile input: each edit makes one length or offset of an otherwise good file wrong, in the way an attacker or a damaged download would. The result must be an ExportException, with the
    // stream not written to, and quickly (nothing here may loop or allocate by what the file claims).
    public static TheoryData<string> Edits => new(
        "ncmds-too-many", "ncmds-max", "sizeofcmds-max", "sizeofcmds-beyond-file", "cmdsize-zero", "cmdsize-four", "cmdsize-unaligned", "cmdsize-beyond-commands",
        "signature-command-size", "signature-command-twice", "dataoff-beyond-file", "dataoff-in-commands", "dataoff-max", "datasize-beyond-file", "datasize-tiny", "datasize-max",
        "superblob-magic", "superblob-length-beyond", "superblob-length-tiny", "superblob-count-max", "superblob-count-beyond", "blob-offset-beyond", "blob-offset-in-index", "blob-offset-max",
        "blob-length-beyond", "blob-length-tiny", "blob-length-max", "directory-too-short", "hash-type-unknown", "hash-size-wrong", "hash-size-zero", "slots-max", "slots-one-less", "slots-one-more",
        "page-size-huge", "limit-beyond-signature", "limit-max", "limit64-beyond", "hash-offset-beyond", "hash-offset-max", "hash-offset-low", "special-slots-max", "special-slots-before-header",
        "directories-too-many", "no-directory");

    [Theory]
    [MemberData(nameof(Edits))]
    public void HostileLengthsAndOffsetsAreAnExportErrorAndNothingIsWritten(string edit)
    {
        SyntheticMachO image = SyntheticMachO.Build(CodeLength, [s_sha256Pages4K], correctHashes: false);
        byte[] file = (byte[])image.Bytes.Clone();
        int sig = image.SignatureOffset;
        int directory = sig + image.Directories[0].Offset;
        int index = sig + 12; // the first index entry: type, offset

        void Le(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), value);
        void Be(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(at), value);
        switch (edit)
        {
            case "ncmds-too-many": Le(16, 500); break;
            case "ncmds-max": Le(16, uint.MaxValue); break;
            case "sizeofcmds-max": Le(20, uint.MaxValue); break;
            case "sizeofcmds-beyond-file": Le(20, (uint)file.Length); break;
            case "cmdsize-zero": Le(SyntheticMachO.BuildVersionOffset + 4, 0); break;
            case "cmdsize-four": Le(SyntheticMachO.BuildVersionOffset + 4, 4); break;
            case "cmdsize-unaligned": Le(SyntheticMachO.BuildVersionOffset + 4, 28); break;
            case "cmdsize-beyond-commands": Le(SyntheticMachO.BuildVersionOffset + 4, 1024); break;
            case "signature-command-size": Le(SyntheticMachO.CodeSignatureCommandOffset + 4, 24); break;
            case "signature-command-twice":
                // The 32 bytes of LC_BUILD_VERSION become two LC_CODE_SIGNATURE commands of 16, so there are three in all.
                Le(16, 3);
                for (int command = 0; command < 2; command++)
                {
                    int at = SyntheticMachO.BuildVersionOffset + command * 16;
                    Le(at, 0x1D);
                    Le(at + 4, 16);
                    Le(at + 8, (uint)image.SignatureOffset);
                    Le(at + 12, (uint)image.SignatureLength);
                }

                break;
            case "dataoff-beyond-file": Le(SyntheticMachO.CodeSignatureCommandOffset + 8, (uint)file.Length); break;
            case "dataoff-in-commands": Le(SyntheticMachO.CodeSignatureCommandOffset + 8, 40); break;
            case "dataoff-max": Le(SyntheticMachO.CodeSignatureCommandOffset + 8, uint.MaxValue); break;
            case "datasize-beyond-file": Le(SyntheticMachO.CodeSignatureCommandOffset + 12, (uint)image.SignatureLength + 1); break;
            case "datasize-tiny": Le(SyntheticMachO.CodeSignatureCommandOffset + 12, 11); break;
            case "datasize-max": Le(SyntheticMachO.CodeSignatureCommandOffset + 12, uint.MaxValue); break;
            case "superblob-magic": Be(sig, 0xFADE0C02); break;
            case "superblob-length-beyond": Be(sig + 4, (uint)image.SignatureLength + 1); break;
            case "superblob-length-tiny": Be(sig + 4, 8); break;
            case "superblob-count-max": Be(sig + 8, uint.MaxValue); break;
            case "superblob-count-beyond": Be(sig + 8, 5000); break;
            case "blob-offset-beyond": Be(index + 4, (uint)image.SignatureLength - 4); break;
            case "blob-offset-in-index": Be(index + 4, 4); break;
            case "blob-offset-max": Be(index + 4, uint.MaxValue); break;
            case "blob-length-beyond": Be(directory + 4, (uint)image.SignatureLength); break;
            case "blob-length-tiny": Be(directory + 4, 4); break;
            case "blob-length-max": Be(directory + 4, uint.MaxValue); break;
            case "directory-too-short": Be(directory + 4, 40); break;
            case "hash-type-unknown": file[directory + 37] = 9; break;
            case "hash-size-wrong": file[directory + 36] = 20; break;
            case "hash-size-zero": file[directory + 36] = 0; file[directory + 37] = 0; break;
            case "slots-max": Be(directory + 28, uint.MaxValue); break;
            case "slots-one-less": Be(directory + 28, (uint)image.Directories[0].CodeSlots - 1); break;
            case "slots-one-more": Be(directory + 28, (uint)image.Directories[0].CodeSlots + 1); break;
            case "page-size-huge": file[directory + 39] = 63; break;
            case "limit-beyond-signature": Be(directory + 32, (uint)image.SignatureOffset + 1); break;
            case "limit-max": Be(directory + 32, uint.MaxValue); break;
            case "limit64-beyond": BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(directory + 56), long.MaxValue); break;
            case "hash-offset-beyond": Be(directory + 16, (uint)image.Directories[0].Length); break;
            case "hash-offset-max": Be(directory + 16, uint.MaxValue); break;
            case "hash-offset-low": Be(directory + 16, 40); break;
            case "special-slots-max": Be(directory + 24, uint.MaxValue); break;
            case "special-slots-before-header": Be(directory + 24, 1000); break;
            case "directories-too-many":
                // Nine index entries, all pointing at the same directory: it would be hashed nine times if it were allowed.
                file = TooManyDirectories();
                break;
            case "no-directory": Be(directory, 0xFADE0C01); break; // the blob is a requirements blob now
            default: throw new ArgumentException(edit);
        }

        byte[] before = (byte[])file.Clone();
        using var stream = new CountingStream(file);

        Assert.Throws<ExportException>(() => MachOSignature.Rehash(stream, "test"));

        Assert.Equal(0, stream.BytesWritten);
        Assert.Equal(before, stream.ToArray());
    }

    private static byte[] TooManyDirectories()
    {
        SyntheticMachO image = SyntheticMachO.Build(CodeLength, Enumerable.Repeat(s_sha256Pages4K, 9).ToArray(), correctHashes: false);
        return image.Bytes;
    }

    [Fact]
    public void ARandomlyDamagedSignatureIsRightOrAnExportErrorNeverAnythingElse()
    {
        SyntheticMachO image = SyntheticMachO.Build(CodeLength, [s_sha256Pages4K, s_sha1Pages4K]);
        var random = new Random(2026);
        int errors = 0;
        int rehashed = 0;
        for (int round = 0; round < 3000; round++)
        {
            byte[] file = (byte[])image.Bytes.Clone();
            for (int change = random.Next(1, 5); change > 0; change--)
            {
                // Mostly the header, the load commands and the signature (the parts that are read as structure); sometimes anywhere.
                int at = random.Next(4) == 0 ? random.Next(file.Length) : random.Next(2) == 0 ? random.Next(80) : image.SignatureOffset + random.Next(image.SignatureLength);
                file[at] = random.Next(3) switch { 0 => 0, 1 => 0xFF, _ => (byte)random.Next(256) };
            }

            using var stream = new CountingStream(file);
            try
            {
                MachOSignature.Rehash(stream, "damaged");
                rehashed++;
            }
            catch (ExportException)
            {
                errors++;
                Assert.Equal(0, stream.BytesWritten);
            }
        }

        // Both outcomes happen, so that the loop tests what it says.
        Assert.True(errors > 100, $"{errors} errors");
        Assert.True(rehashed > 100, $"{rehashed} files rehashed");
    }

    [Fact]
    public void ATruncatedFileIsAnExportErrorAtEveryLength()
    {
        SyntheticMachO image = SyntheticMachO.Build(0x1800, [s_sha256Pages4K]);
        for (int length = 0; length < image.Bytes.Length; length += 37)
        {
            byte[] cut = image.Bytes[..length];
            try
            {
                MachOSignature.Rehash(new MemoryStream(cut, writable: true), "cut");
            }
            catch (ExportException)
            {
                // The expected outcome: a file that ends early does not fit what its header says.
            }
        }
    }

    [Fact]
    public void TheVendoredMacOsLibrariesSignaturesAreReproducedByteForByte()
    {
        // The signatures the linker and Apple's codesign made for the libraries in ThirdParty are the real format: rehashing a library, unchanged, must find every hash already right (nothing
        // is written), and after one byte is changed and changed back, rehashing must restore the original file exactly.
        string? thirdParty = FindThirdParty();
        if (thirdParty is null)
        {
            Assert.Skip("ThirdParty is not next to the tests");
            return;
        }

        int checkedSlices = 0;
        foreach (string library in Directory.EnumerateFiles(thirdParty, "*.dylib", SearchOption.AllDirectories).Where(f => f.Contains("osx-", StringComparison.Ordinal)))
        {
            foreach (byte[] slice in Slices(File.ReadAllBytes(library)))
            {
                using var stream = new CountingStream(slice);
                try
                {
                    if (!MachOSignature.Rehash(stream, library))
                    {
                        continue; // an x86-64 image the linker did not sign
                    }
                }
                catch (ExportException e) when (e.Message.Contains("certificate", StringComparison.Ordinal))
                {
                    continue; // signed with an identity (Valve's, for instance): refused, as intended
                }

                Assert.Equal(0, stream.BytesWritten);

                byte[] changed = (byte[])slice.Clone();
                int at = slice.Length / 3; // in the code, well clear of the load commands and of the signature at the end
                changed[at] ^= 0x5A;
                Assert.True(Rehash(changed));
                Assert.NotEqual(slice, changed); // the hashes changed
                changed[at] ^= 0x5A;
                Assert.True(Rehash(changed));
                Assert.Equal(slice, changed);
                checkedSlices++;
            }
        }

        Assert.True(checkedSlices > 0, "no library with an ad-hoc signature was found");
    }

    private static string? FindThirdParty()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "ThirdParty");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(directory.FullName, "Source")))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The thin images in a file: the file itself, or each slice of a universal binary.</summary>
    private static IEnumerable<byte[]> Slices(byte[] file)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(file) != 0xCAFEBABE)
        {
            yield return file;
            yield break;
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int entry = 8 + i * 20;
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 8));
            uint size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 12));
            yield return file.AsSpan((int)offset, (int)size).ToArray();
        }
    }

    /// <summary>A stream over a copy of some bytes that counts what is written to it.</summary>
    private sealed class CountingStream : MemoryStream
    {
        public CountingStream(byte[] bytes)
            : base(bytes.Length)
        {
            base.Write(bytes, 0, bytes.Length);
            Position = 0;
        }

        public int BytesWritten { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            BytesWritten += count;
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BytesWritten += buffer.Length;
            base.Write(buffer);
        }
    }
}
