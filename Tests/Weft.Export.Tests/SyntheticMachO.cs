using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Weft.Export.Tests;

/// <summary>What one CodeDirectory of a <see cref="SyntheticMachO"/> is: its hash type (1 SHA-1, 2 SHA-256, 3 SHA-256 cut to 20 bytes, 4 SHA-384) and page size (log2).</summary>
internal readonly record struct DirectorySpec(int HashType, int PageLog2);

/// <summary>Where a CodeDirectory is in a <see cref="SyntheticMachO"/>.</summary>
internal sealed record DirectoryLayout(DirectorySpec Spec, int Offset, int Length, int HashOffset, int CodeSlots, int HashSize, int SpecialStart);

/// <summary>
/// A thin 64-bit Mach-O file made by the test, with an ad-hoc signature in the format of the linker's: a SuperBlob with one CodeDirectory per spec (version 0x20400, an identifier, two special slots
/// holding a fixed pattern, and a hash for each page of the file up to the signature) and a requirements blob. The page hashes are computed here with the framework's hash functions, not by the code
/// that is tested: <see cref="Expected"/> is the independent way to know what a hash must be.
/// </summary>
internal sealed class SyntheticMachO
{
    public const int CommandsOffset = 32;
    public const int BuildVersionOffset = 32;
    public const int CodeSignatureCommandOffset = 64;
    public const int SpecialSlotPattern = 0xEE;

    private SyntheticMachO(byte[] bytes, int signatureOffset, int signatureLength, List<DirectoryLayout> directories)
    {
        Bytes = bytes;
        SignatureOffset = signatureOffset;
        SignatureLength = signatureLength;
        Directories = directories;
    }

    public byte[] Bytes { get; }

    /// <summary>The offset of the signature, which is also how many bytes of the program it covers.</summary>
    public int SignatureOffset { get; }

    public int SignatureLength { get; }

    public List<DirectoryLayout> Directories { get; }

    public static int HashSizeOf(int hashType) => hashType switch { 1 => 20, 2 => 32, 3 => 20, 4 => 48, _ => throw new ArgumentOutOfRangeException(nameof(hashType)) };

    /// <summary>The hash of page <paramref name="page"/> of <paramref name="file"/>, whose first <paramref name="codeLimit"/> bytes are covered in pages of 2^<paramref name="pageLog2"/>.</summary>
    public static byte[] Expected(byte[] file, int codeLimit, int pageLog2, int hashType, int page)
    {
        int pageSize = pageLog2 == 0 ? codeLimit : 1 << pageLog2;
        int start = page * pageSize;
        ReadOnlySpan<byte> data = file.AsSpan(start, Math.Min(pageSize, codeLimit - start));
        byte[] hash = hashType switch
        {
#pragma warning disable CA5350 // SHA-1 is the hash of the older code signatures; this is the independent check of it
            1 => SHA1.HashData(data),
#pragma warning restore CA5350
            2 => SHA256.HashData(data),
            3 => SHA256.HashData(data)[..20],
            4 => SHA384.HashData(data),
            _ => throw new ArgumentOutOfRangeException(nameof(hashType)),
        };
        return hash;
    }

    /// <summary>Builds the file: <paramref name="codeLength"/> bytes of program (at least 0x200), then the signature.</summary>
    /// <param name="codeLength">The length of the program, where the signature starts.</param>
    /// <param name="specs">The CodeDirectories, the first is slot 0 and the others alternates.</param>
    /// <param name="correctHashes">Whether the hashes in the CodeDirectories are right for the file as it is built (otherwise they are zeros).</param>
    /// <param name="embed">Bytes to put in the program at <paramref name="embedAt"/> (for example the pack key slot).</param>
    /// <param name="embedAt">Where.</param>
    /// <param name="minimumOs">The <c>minos</c> of the <c>LC_BUILD_VERSION</c> command.</param>
    /// <param name="cpuType">The header's <c>cputype</c>.</param>
    /// <param name="wrapperLength">Adds a blob wrapper (the signature slot 0x10000) of this length: 8 is the empty one that <c>codesign -s -</c> writes, more has a CMS message in it, as a signature made with a certificate does. 0: none.</param>
    /// <param name="flags">The CodeDirectory <c>flags</c>: 0x20002 is ad hoc and linker-signed, 0x2 ad hoc, without 0x2 a certificate signs.</param>
    public static SyntheticMachO Build(
        int codeLength,
        DirectorySpec[] specs,
        bool correctHashes = true,
        byte[]? embed = null,
        int embedAt = 0x400,
        uint minimumOs = 0x000B0000,
        uint cpuType = 0x0100000C,
        int wrapperLength = 0,
        uint flags = 0x20002)
    {
        // The program: header (32), LC_BUILD_VERSION (32) and LC_CODE_SIGNATURE (16), then bytes that are the same on every run.
        byte[] program = new byte[codeLength];
        new Random(12345).NextBytes(program.AsSpan(CommandsOffset + 48));
        embed?.CopyTo(program, embedAt);

        // The signature is built first for its length, which LC_CODE_SIGNATURE says; the hashes are filled in when the file is whole.
        const int IdentLength = 9;
        const int FixedSize = 88;
        var directories = new List<DirectoryLayout>();
        int blobsStart = 12 + (specs.Length + 1 + (wrapperLength > 0 ? 1 : 0)) * 8;
        int at = blobsStart;
        foreach (DirectorySpec spec in specs)
        {
            int hashSize = HashSizeOf(spec.HashType);
            int pageSize = spec.PageLog2 == 0 ? Math.Max(codeLength, 1) : 1 << spec.PageLog2;
            int slots = (codeLength + pageSize - 1) / pageSize;
            int specialStart = FixedSize + IdentLength;
            int hashOffset = specialStart + 2 * hashSize;
            int length = hashOffset + slots * hashSize;
            directories.Add(new DirectoryLayout(spec, at, length, hashOffset, slots, hashSize, specialStart));
            at += length;
        }

        int requirementsAt = at;
        at += 12;
        int certificateAt = at;
        if (wrapperLength > 0)
        {
            at += wrapperLength;
        }

        int signatureLength = at;
        byte[] file = new byte[codeLength + signatureLength];
        program.CopyTo(file, 0);

        // Header (mach_header_64, little-endian): MH_MAGIC_64, cputype, cpusubtype, filetype (MH_EXECUTE), ncmds, sizeofcmds, flags, reserved.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0), 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), cpuType);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 48);

        // LC_BUILD_VERSION: cmd 0x32, cmdsize 32, platform 1 (macOS), minos, sdk, ntools 1, tool 3 (ld), version.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(BuildVersionOffset), 0x32);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(BuildVersionOffset + 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(BuildVersionOffset + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(BuildVersionOffset + 12), minimumOs);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(BuildVersionOffset + 16), 0x000F0000);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(BuildVersionOffset + 20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(BuildVersionOffset + 24), 3);

        // LC_CODE_SIGNATURE: cmd 0x1D, cmdsize 16, dataoff, datasize.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(CodeSignatureCommandOffset), 0x1D);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(CodeSignatureCommandOffset + 4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(CodeSignatureCommandOffset + 8), (uint)codeLength);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(CodeSignatureCommandOffset + 12), (uint)signatureLength);

        // SuperBlob (big-endian): magic, length, count; the index (type, offset) of each blob.
        Span<byte> blob = file.AsSpan(codeLength, signatureLength);
        BinaryPrimitives.WriteUInt32BigEndian(blob, 0xFADE0CC0);
        BinaryPrimitives.WriteUInt32BigEndian(blob[4..], (uint)signatureLength);
        BinaryPrimitives.WriteUInt32BigEndian(blob[8..], (uint)(specs.Length + 1 + (wrapperLength > 0 ? 1 : 0)));
        int index = 12;
        for (int i = 0; i < specs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(blob[index..], i == 0 ? 0u : (uint)(0x1000 + i - 1)); // CSSLOT_CODEDIRECTORY, then CSSLOT_ALTERNATE_CODEDIRECTORIES
            BinaryPrimitives.WriteUInt32BigEndian(blob[(index + 4)..], (uint)directories[i].Offset);
            index += 8;
        }

        BinaryPrimitives.WriteUInt32BigEndian(blob[index..], 2); // CSSLOT_REQUIREMENTS
        BinaryPrimitives.WriteUInt32BigEndian(blob[(index + 4)..], (uint)requirementsAt);
        index += 8;
        BinaryPrimitives.WriteUInt32BigEndian(blob[requirementsAt..], 0xFADE0C01);
        BinaryPrimitives.WriteUInt32BigEndian(blob[(requirementsAt + 4)..], 12);
        if (wrapperLength > 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(blob[index..], 0x10000); // CSSLOT_SIGNATURESLOT
            BinaryPrimitives.WriteUInt32BigEndian(blob[(index + 4)..], (uint)certificateAt);
            BinaryPrimitives.WriteUInt32BigEndian(blob[certificateAt..], 0xFADE0B01);
            BinaryPrimitives.WriteUInt32BigEndian(blob[(certificateAt + 4)..], (uint)wrapperLength);
        }

        foreach (DirectoryLayout directory in directories)
        {
            WriteDirectory(blob.Slice(directory.Offset, directory.Length), directory, codeLength, flags);
        }

        var image = new SyntheticMachO(file, codeLength, signatureLength, directories);
        if (correctHashes)
        {
            foreach (DirectoryLayout directory in directories)
            {
                for (int page = 0; page < directory.CodeSlots; page++)
                {
                    Expected(file, codeLength, directory.Spec.PageLog2, directory.Spec.HashType, page).CopyTo(file, codeLength + directory.Offset + directory.HashOffset + page * directory.HashSize);
                }
            }
        }

        return image;
    }

    /// <summary>Makes the hashes of the file right for the bytes it has now, by the independent computation (what a valid signature of the file is).</summary>
    public void MakeHashesRight()
    {
        foreach (DirectoryLayout directory in Directories)
        {
            for (int page = 0; page < directory.CodeSlots; page++)
            {
                Expected(Bytes, SignatureOffset, directory.Spec.PageLog2, directory.Spec.HashType, page).CopyTo(Bytes, SignatureOffset + directory.Offset + directory.HashOffset + page * directory.HashSize);
            }
        }
    }

    /// <summary>Whether every page hash of every CodeDirectory of <paramref name="file"/> (laid out as this image is) is the hash of the page, computed independently.</summary>
    public bool HashesAreRight(byte[] file)
    {
        foreach (DirectoryLayout directory in Directories)
        {
            for (int page = 0; page < directory.CodeSlots; page++)
            {
                byte[] expected = Expected(file, SignatureOffset, directory.Spec.PageLog2, directory.Spec.HashType, page);
                if (!file.AsSpan(SignatureOffset + directory.Offset + directory.HashOffset + page * directory.HashSize, directory.HashSize).SequenceEqual(expected))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The absolute offset of the first byte of the hash slots (special and code) of each directory, and how far they go: the bytes a rehash may change.</summary>
    public List<(int Start, int End)> CodeSlotRanges() =>
        Directories.ConvertAll(d => (SignatureOffset + d.Offset + d.HashOffset, SignatureOffset + d.Offset + d.HashOffset + d.CodeSlots * d.HashSize));

    private static void WriteDirectory(Span<byte> directory, DirectoryLayout layout, int codeLength, uint flags)
    {
        BinaryPrimitives.WriteUInt32BigEndian(directory, 0xFADE0C02);
        BinaryPrimitives.WriteUInt32BigEndian(directory[4..], (uint)layout.Length);
        BinaryPrimitives.WriteUInt32BigEndian(directory[8..], 0x20400);
        BinaryPrimitives.WriteUInt32BigEndian(directory[12..], flags);
        BinaryPrimitives.WriteUInt32BigEndian(directory[16..], (uint)layout.HashOffset);
        BinaryPrimitives.WriteUInt32BigEndian(directory[20..], 88); // identOffset
        BinaryPrimitives.WriteUInt32BigEndian(directory[24..], 2); // nSpecialSlots
        BinaryPrimitives.WriteUInt32BigEndian(directory[28..], (uint)layout.CodeSlots);
        BinaryPrimitives.WriteUInt32BigEndian(directory[32..], (uint)codeLength);
        directory[36] = (byte)layout.HashSize;
        directory[37] = (byte)layout.Spec.HashType;
        directory[39] = (byte)layout.Spec.PageLog2;
        "test.exe\0"u8.CopyTo(directory[88..]);
        directory.Slice(layout.SpecialStart, 2 * layout.HashSize).Fill(SpecialSlotPattern);
    }
}
