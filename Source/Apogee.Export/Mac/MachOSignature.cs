using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Apogee.Export;

/// <summary>
/// Makes the ad-hoc code signature of a macOS program valid again after the program's bytes were changed (the export writes the pack key into it, see <see cref="PackKeySlot"/>).
/// <para>
/// <b>Why.</b> The linker (or <c>codesign -s -</c>) signs a program ad hoc: no certificate, only hashes. The signature is a blob at the end of the file, the <i>SuperBlob</i>, which holds a
/// <i>CodeDirectory</i>: a hash of every 4 KiB or 16 KiB page of the program, up to the start of the signature. The kernel of an Apple-silicon Mac checks a page's hash when it is mapped and kills
/// a program whose pages do not match ("Killed: 9"). Changing the key slot changes a page, so its hash in the CodeDirectory must be made again.
/// </para>
/// <para>
/// <b>What this does.</b> It finds <c>LC_CODE_SIGNATURE</c>, reads the SuperBlob, and for every CodeDirectory in it recomputes the hash of every page (with that CodeDirectory's own hash function
/// and page size) and writes the hashes back, in place. Nothing else changes: not the length of the file, not the Mach-O header, not the position or size of the signature, not any other blob
/// (the requirements and entitlements are hashes of themselves, in the CodeDirectory's special slots, and do not depend on the program's pages).
/// </para>
/// <para>
/// <b>What this does not do.</b> It does not sign: it only repairs a signature that was already there, and it is never right for a signature with a certificate. A signature made with an identity has
/// a CMS blob that signs the CodeDirectory, so a CodeDirectory that changes makes it invalid, and it cannot be repaired without the private key: that file is refused (sign after the export). There
/// is no notarization, no Developer ID signing, and nothing is added to a program that has no signature (an x86-64 program may have none, and macOS runs it; an arm64 one will not run). The
/// program must be a thin (single processor) 64-bit little-endian Mach-O.
/// </para>
/// <para>
/// The input is untrusted: every length and offset is checked against the length of the file or of the blob that holds it, in <c>long</c> arithmetic, before it is used; the number of
/// CodeDirectories is limited (so the file is hashed a few times at most); pages are hashed in small chunks, so memory use does not depend on the file. A file that fails a check is not
/// changed: all the hashes are made, in a copy of the blob, before anything is written.
/// </para>
/// </summary>
internal static class MachOSignature
{
    /// <summary>The SuperBlob magic, <c>CSMAGIC_EMBEDDED_SIGNATURE</c> (every field of the signature is big-endian).</summary>
    private const uint SuperBlobMagic = 0xFADE0CC0;

    /// <summary>The CodeDirectory magic, <c>CSMAGIC_CODEDIRECTORY</c>.</summary>
    private const uint CodeDirectoryMagic = 0xFADE0C02;

    /// <summary>The magic of the blob that holds a certificate-based signature (CMS), <c>CSMAGIC_BLOBWRAPPER</c>.</summary>
    private const uint BlobWrapperMagic = 0xFADE0B01;

    /// <summary>The size of a SuperBlob's header: <c>magic</c>, <c>length</c>, <c>count</c> (4 bytes each); followed by <c>count</c> index entries of <c>type</c> and <c>offset</c> (4 bytes each).</summary>
    private const int SuperBlobHeaderSize = 12;

    private const int BlobIndexSize = 8;

    /// <summary>The size of the part of a CodeDirectory that every version has: up to and including <c>spare2</c> (offset 40, 4 bytes). Enough to read the version.</summary>
    private const int CodeDirectoryMinimumSize = 44;

    /// <summary>The <c>flags</c> bit (offset 12) of an ad-hoc signature, <c>CS_ADHOC</c>: no certificate.</summary>
    private const uint AdHocFlag = 0x2;

    /// <summary>The CodeDirectory <c>version</c> that has <c>scatterOffset</c> (at offset 44), <c>CS_SUPPORTSSCATTER</c>.</summary>
    private const uint ScatterVersion = 0x20100;

    /// <summary>The CodeDirectory <c>version</c> that has <c>codeLimit64</c> (at offset 56), <c>CS_SUPPORTSCODELIMIT64</c>.</summary>
    private const uint CodeLimit64Version = 0x20300;

    /// <summary>A program is signed with the main CodeDirectory and up to five alternates (<c>CSSLOT_ALTERNATE_CODEDIRECTORIES</c>, 0x1000 to 0x1004): more than this is refused.</summary>
    private const int MaxCodeDirectories = 8;

    /// <summary>A signature of more bytes than this is refused: 100 MiB of program has less than 1 MiB of it.</summary>
    private const int MaxSignatureBytes = 64 << 20;

    private const int HashChunkSize = 64 << 10;

    /// <summary>
    /// Makes the ad-hoc signature of the program at <paramref name="executable"/> match the program's bytes again. See <see cref="MachOSignature"/> for what it does and does not do.
    /// </summary>
    /// <returns>True when the program has a signature (which now matches); false when it has none, and nothing was done.</returns>
    /// <exception cref="ExportException">The file is not a thin 64-bit Mach-O, its signature is malformed or is not an ad-hoc one, or the file could not be read or written.</exception>
    /// <param name="executable">The program.</param>
    /// <param name="mustBeCovered">When given, the file range (offset and length) that was changed and that the signature must cover: the pack key slot. See the stream overload.</param>
    public static bool Rehash(string executable, (long Offset, int Length)? mustBeCovered = null)
    {
        try
        {
            using FileStream stream = new(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return Rehash(stream, Path.GetFileName(executable), mustBeCovered);
        }
        catch (IOException e)
        {
            throw new ExportException($"{executable} could not be read or written: {e.Message}", e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new ExportException($"{executable} could not be read or written: {e.Message}", e);
        }
    }

    /// <summary>The same for a stream that is the program: seekable, readable and writable. Writes nothing unless the hashes changed, and then only the signature's blob.</summary>
    /// <param name="stream">The program.</param>
    /// <param name="name">What to call it in messages.</param>
    /// <param name="mustBeCovered">
    /// A range of the file that was changed (the pack key slot) and that must be inside the program's code, after the header and load commands and before the signature, so that the page hashes
    /// cover it; otherwise the rehash could not make the signature right, and it is an error. Not checked when the program has no signature.
    /// </param>
    internal static bool Rehash(Stream stream, string name, (long Offset, int Length)? mustBeCovered = null)
    {
        long fileLength = stream.Length;
        MachOImage image = MachOFile.Read(stream, 0, fileLength, name);
        if (!TryFindSignature(image, name, out long signatureOffset, out int signatureSize))
        {
            return false;
        }

        // The signature must be wholly inside the file and after the header and load commands (the code it hashes starts at offset 0 and ends where the signature starts).
        long commandsEnd = MachOFile.HeaderSize + (long)image.CommandBytes.Length;
        if (signatureSize < SuperBlobHeaderSize || signatureSize > MaxSignatureBytes || signatureOffset < commandsEnd || signatureOffset + signatureSize > fileLength)
        {
            throw new ExportException($"{name}: LC_CODE_SIGNATURE says the signature is {signatureSize} bytes at offset {signatureOffset}, which does not fit the file ({fileLength} bytes).");
        }

        if (mustBeCovered is { } changed && (changed.Length < 0 || changed.Offset < commandsEnd || changed.Offset > signatureOffset - changed.Length))
        {
            throw new ExportException($"{name}: the bytes changed by the export (offset {changed.Offset}, {changed.Length} bytes) are not in the code that the signature covers (from {commandsEnd} to {signatureOffset}).");
        }

        byte[] blob = new byte[signatureSize];
        MachOFile.ReadAt(stream, signatureOffset, blob, name);
        List<BlobRange> directories = FindCodeDirectories(blob, name);

        byte[] patched = (byte[])blob.Clone();
        foreach (BlobRange range in directories)
        {
            CodeDirectory directory = CodeDirectory.Parse(blob, range, signatureOffset, name);
            WritePageHashes(stream, directory, patched, name);
        }

        if (!patched.AsSpan().SequenceEqual(blob))
        {
            stream.Position = signatureOffset;
            stream.Write(patched);
            stream.Flush();
        }

        return true;
    }

    /// <summary>
    /// Finds <c>LC_CODE_SIGNATURE</c>, a <c>linkedit_data_command</c> (16 bytes: <c>cmd</c>, <c>cmdsize</c>, <c>dataoff</c> at 8, <c>datasize</c> at 12; little-endian like the rest of the load commands):
    /// the file offset and size of the signature. A file has at most one.
    /// </summary>
    private static bool TryFindSignature(MachOImage image, string name, out long offset, out int size)
    {
        offset = 0;
        size = 0;
        bool found = false;
        foreach (LoadCommand command in image.Commands)
        {
            if (command.Command != MachOFile.CodeSignatureCommand)
            {
                continue;
            }

            if (found)
            {
                throw new ExportException($"{name} has more than one LC_CODE_SIGNATURE.");
            }

            if (command.Size != 16)
            {
                throw new ExportException($"{name}: LC_CODE_SIGNATURE is {command.Size} bytes, not 16.");
            }

            offset = BinaryPrimitives.ReadUInt32LittleEndian(image.CommandBytes.AsSpan(command.Offset + 8));
            uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(image.CommandBytes.AsSpan(command.Offset + 12));
            size = dataSize > int.MaxValue ? int.MaxValue : (int)dataSize; // the caller refuses a size above MaxSignatureBytes
            found = true;
        }

        return found;
    }

    /// <summary>
    /// Reads the SuperBlob (<c>struct CS_SuperBlob</c>, big-endian: <c>magic</c> 0xFADE0CC0, <c>length</c>, <c>count</c>, then <c>count</c> entries of <c>type</c> and <c>offset</c> from the start of the SuperBlob)
    /// and returns where each CodeDirectory is. Every other blob (requirements, entitlements) is left alone; a CMS blob is an error, see <see cref="MachOSignature"/>.
    /// </summary>
    private static List<BlobRange> FindCodeDirectories(byte[] blob, string name)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(blob) != SuperBlobMagic)
        {
            throw new ExportException($"{name}: the code signature does not start with an embedded signature (SuperBlob) magic.");
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(4));
        uint count = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(8));
        if (length < SuperBlobHeaderSize || length > blob.Length || SuperBlobHeaderSize + (long)count * BlobIndexSize > length)
        {
            throw new ExportException($"{name}: the code signature's length ({length}) or blob count ({count}) does not fit its {blob.Length} bytes.");
        }

        var directories = new List<BlobRange>();
        long indexEnd = SuperBlobHeaderSize + (long)count * BlobIndexSize;
        for (uint i = 0; i < count; i++)
        {
            int entry = SuperBlobHeaderSize + (int)i * BlobIndexSize;
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(entry + 4)); // entry + 0 is the slot's type, which the blob's own magic says better

            // Each blob starts with its magic and length (8 bytes), after the index, and ends inside the SuperBlob.
            if (offset < indexEnd || offset + 8L > length)
            {
                throw new ExportException($"{name}: blob {i} of the code signature starts at {offset}, outside the signature.");
            }

            uint magic = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan((int)offset));
            uint blobLength = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan((int)offset + 4));
            if (blobLength < 8 || offset + (long)blobLength > length)
            {
                throw new ExportException($"{name}: blob {i} of the code signature is {blobLength} bytes long at {offset}, which does not fit the signature.");
            }

            // codesign -s - writes an empty wrapper (8 bytes: magic and length, no CMS message) in the signature slot; a signature made with a certificate has the CMS message in it.
            if (magic == BlobWrapperMagic && blobLength > 8)
            {
                throw Certificate(name);
            }

            if (magic == CodeDirectoryMagic)
            {
                if (directories.Count == MaxCodeDirectories)
                {
                    throw new ExportException($"{name}: the code signature has more than {MaxCodeDirectories} code directories.");
                }

                directories.Add(new BlobRange((int)offset, (int)blobLength));
            }
        }

        if (directories.Count == 0)
        {
            throw new ExportException($"{name}: the code signature has no code directory.");
        }

        return directories;
    }

    private static ExportException Certificate(string name) =>
        new($"{name} is signed with a certificate (its signature is not an ad-hoc one), and changing the program invalidates that. Publish the runtime unsigned (the linker's ad-hoc signature is the one this export repairs) and sign the bundle after the export, with codesign.");

    /// <summary>
    /// Computes the hash of every page of the program that <paramref name="directory"/> covers, and writes it into <paramref name="blob"/> (the signature's bytes) in the directory's code slots.
    /// Page <c>i</c> is the bytes <c>[i * pageSize, min((i + 1) * pageSize, codeLimit))</c> of the file; its hash is slot <c>i</c>, at <c>hashOffset + i * hashSize</c> in the directory.
    /// </summary>
    private static void WritePageHashes(Stream stream, CodeDirectory directory, byte[] blob, string name)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(directory.Algorithm);
        byte[] digest = new byte[hash.HashLengthInBytes];
        byte[] chunk = new byte[HashChunkSize];
        stream.Position = 0;
        long remaining = directory.CodeLimit;
        for (long slot = 0; slot < directory.CodeSlots; slot++)
        {
            long page = Math.Min(directory.PageSize, remaining);
            for (long left = page; left > 0;)
            {
                int read = (int)Math.Min(chunk.Length, left);
                try
                {
                    stream.ReadExactly(chunk.AsSpan(0, read));
                }
                catch (EndOfStreamException e)
                {
                    throw new ExportException($"{name} ends inside the code its signature covers.", e);
                }

                hash.AppendData(chunk, 0, read);
                left -= read;
            }

            remaining -= page;
            hash.GetHashAndReset(digest);

            // The slot is inside the directory (checked by Parse): the first HashSize bytes of the digest are kept (a "truncated" hash type is a longer hash cut short).
            long at = directory.Range.Offset + directory.HashOffset + slot * directory.HashSize;
            digest.AsSpan(0, directory.HashSize).CopyTo(blob.AsSpan((int)at, directory.HashSize));
        }
    }

    /// <summary>Where a blob is in the signature: its first byte, and its length.</summary>
    private readonly record struct BlobRange(int Offset, int Length);

    /// <summary>
    /// The fields of a CodeDirectory (<c>struct CS_CodeDirectory</c>, big-endian) that the page hashes need, checked. Offsets are from the start of the directory:
    /// <c>magic</c> 0, <c>length</c> 4, <c>version</c> 8, <c>flags</c> 12, <c>hashOffset</c> 16 (where slot 0 is; special slots precede it), <c>identOffset</c> 20, <c>nSpecialSlots</c> 24,
    /// <c>nCodeSlots</c> 28, <c>codeLimit</c> 32, <c>hashSize</c> 36 (1 byte), <c>hashType</c> 37 (1 byte), <c>platform</c> 38, <c>pageSize</c> 39 (1 byte, log2), <c>spare2</c> 40,
    /// then, from version 0x20100, <c>scatterOffset</c> 44, from 0x20200 <c>teamOffset</c> 48, from 0x20300 <c>spare3</c> 52 and <c>codeLimit64</c> 56 (8 bytes).
    /// </summary>
    private sealed class CodeDirectory
    {
        private CodeDirectory(BlobRange range, long hashOffset, long codeSlots, long codeLimit, int hashSize, HashAlgorithmName algorithm, long pageSize)
        {
            Range = range;
            HashOffset = hashOffset;
            CodeSlots = codeSlots;
            CodeLimit = codeLimit;
            HashSize = hashSize;
            Algorithm = algorithm;
            PageSize = pageSize;
        }

        public BlobRange Range { get; }

        /// <summary>The offset of code slot 0 from the start of the directory.</summary>
        public long HashOffset { get; }

        public long CodeSlots { get; }

        /// <summary>How many bytes of the file, from its start, the directory covers.</summary>
        public long CodeLimit { get; }

        /// <summary>The bytes of a hash in a slot.</summary>
        public int HashSize { get; }

        public HashAlgorithmName Algorithm { get; }

        /// <summary>The bytes one slot covers.</summary>
        public long PageSize { get; }

        /// <summary>The bytes of a CodeDirectory before its identifier and hashes can start, for a version: 44 up to <c>spare2</c>, then 48 (+ <c>scatterOffset</c>), 52 (+ <c>teamOffset</c>), 64 (+ <c>spare3</c>, <c>codeLimit64</c>) and 88 (+ <c>execSegBase</c>, <c>execSegLimit</c>, <c>execSegFlags</c>).</summary>
        private static int FixedSize(uint version) => version switch
        {
            >= 0x20400 => 88,
            >= 0x20300 => 64,
            >= 0x20200 => 52,
            >= 0x20100 => 48,
            _ => CodeDirectoryMinimumSize,
        };

        /// <summary>Reads and checks the CodeDirectory in the <paramref name="range"/> of <paramref name="blob"/> for a program whose signature is at <paramref name="signatureOffset"/>.</summary>
        public static CodeDirectory Parse(byte[] blob, BlobRange range, long signatureOffset, string name)
        {
            if (range.Length < CodeDirectoryMinimumSize)
            {
                throw new ExportException($"{name}: a code directory of {range.Length} bytes is too short.");
            }

            ReadOnlySpan<byte> bytes = blob.AsSpan(range.Offset, range.Length);
            uint version = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]);
            uint hashOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
            uint specialSlots = BinaryPrimitives.ReadUInt32BigEndian(bytes[24..]);
            uint codeSlots = BinaryPrimitives.ReadUInt32BigEndian(bytes[28..]);
            long codeLimit = BinaryPrimitives.ReadUInt32BigEndian(bytes[32..]);
            int hashSize = bytes[36];
            int hashType = bytes[37];
            int pageSizeLog2 = bytes[39];

            // A signature made with a certificate is not ad hoc: its CodeDirectory is signed, and cannot be changed.
            if ((flags & AdHocFlag) == 0)
            {
                throw Certificate(name);
            }

            // The fixed part of the directory has more fields in later versions, and a directory of a version must hold them all: a shorter one is malformed (the kernel refuses it too).
            int fixedSize = FixedSize(version);
            if (range.Length < fixedSize)
            {
                throw new ExportException($"{name}: a code directory of version 0x{version:X} is {range.Length} bytes, shorter than its {fixedSize} fixed bytes.");
            }

            // With a scatter vector (scatterOffset, offset 44) the code slots hash pieces of the file in an order that this does not follow. No tool writes one now.
            if (version >= ScatterVersion && BinaryPrimitives.ReadUInt32BigEndian(bytes[44..]) != 0)
            {
                throw new ExportException($"{name}: the code signature uses a scatter vector, which is not supported.");
            }

            // From version 0x20300 a non-zero codeLimit64 (needed only for programs of 4 GiB and more) is the limit.
            if (version >= CodeLimit64Version)
            {
                ulong codeLimit64 = BinaryPrimitives.ReadUInt64BigEndian(bytes[56..]);
                if (codeLimit64 != 0)
                {
                    codeLimit = codeLimit64 > long.MaxValue ? long.MaxValue : (long)codeLimit64;
                }
            }

            // The hash function: hashType 1 is SHA-1, 2 SHA-256, 3 SHA-256 cut to 20 bytes, 4 SHA-384 (the values of CS_HASHTYPE_*). hashSize must be the size the type has.
            (HashAlgorithmName algorithm, int expectedSize) = hashType switch
            {
                1 => (HashAlgorithmName.SHA1, 20),
                2 => (HashAlgorithmName.SHA256, 32),
                3 => (HashAlgorithmName.SHA256, 20),
                4 => (HashAlgorithmName.SHA384, 48),
                _ => throw new ExportException($"{name}: the code signature uses hash type {hashType}, which is not supported (SHA-1 and SHA-256 are what the linker and codesign use)."),
            };
            if (hashSize != expectedSize)
            {
                throw new ExportException($"{name}: the code signature has hashes of {hashSize} bytes, but hash type {hashType} has {expectedSize}.");
            }

            // The signature must not cover itself, nor more than the file holds.
            if (codeLimit > signatureOffset)
            {
                throw new ExportException($"{name}: the code signature covers {codeLimit} bytes, but it starts at {signatureOffset}.");
            }

            // The page size is 2^pageSize bytes; pageSize 0 means no paging: one hash of everything. The slot count follows from it, and a file that says another number is not a signature we understand.
            if (pageSizeLog2 > 30)
            {
                throw new ExportException($"{name}: the code signature has a page size of 2^{pageSizeLog2} bytes.");
            }

            long pageSize = pageSizeLog2 == 0 ? Math.Max(codeLimit, 1) : 1L << pageSizeLog2;
            long expectedSlots = (codeLimit + pageSize - 1) / pageSize;
            if (codeSlots != expectedSlots)
            {
                throw new ExportException($"{name}: the code signature has {codeSlots} code slots, but {expectedSlots} are needed for {codeLimit} bytes in pages of {pageSize}.");
            }

            // The special slots (negative indexes) are before hashOffset and after the fixed part; the code slots are after it; all must be in the directory.
            long specialStart = hashOffset - specialSlots * (long)hashSize;
            long codeEnd = hashOffset + codeSlots * (long)hashSize;
            if (specialStart < fixedSize || codeEnd > range.Length)
            {
                throw new ExportException($"{name}: the code signature's hash slots (offset {hashOffset}, {specialSlots} special and {codeSlots} code slots of {hashSize} bytes) do not fit its code directory ({range.Length} bytes).");
            }

            return new CodeDirectory(range, hashOffset, codeSlots, codeLimit, hashSize, algorithm, pageSize);
        }
    }
}
