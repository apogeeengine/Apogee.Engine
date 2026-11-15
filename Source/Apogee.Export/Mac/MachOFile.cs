using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Apogee.Export;

/// <summary>One load command of a Mach-O file: its <c>cmd</c> and where it is in <see cref="MachOImage.Commands"/>.</summary>
/// <param name="Command">The <c>cmd</c> field (<c>LC_*</c>).</param>
/// <param name="Offset">The offset of the command's first byte (its <c>cmd</c> field) in the bytes of all the load commands.</param>
/// <param name="Size">The <c>cmdsize</c> field: the command's length in bytes, at least 8, a multiple of 8, and within the load commands.</param>
internal readonly record struct LoadCommand(uint Command, int Offset, int Size);

/// <summary>The checked header and load commands of a thin 64-bit little-endian Mach-O file.</summary>
internal sealed class MachOImage
{
    public MachOImage(uint cpuType, byte[] commandBytes, IReadOnlyList<LoadCommand> commands)
    {
        CpuType = cpuType;
        CommandBytes = commandBytes;
        Commands = commands;
    }

    /// <summary>The header's <c>cputype</c> (<c>0x01000007</c> x86-64, <c>0x0100000C</c> arm64).</summary>
    public uint CpuType { get; }

    /// <summary>The bytes of all the load commands, from just after the 32-byte header: <c>sizeofcmds</c> of them.</summary>
    public byte[] CommandBytes { get; }

    /// <summary>The <c>ncmds</c> load commands, in order.</summary>
    public IReadOnlyList<LoadCommand> Commands { get; }
}

/// <summary>
/// Reads the part of a Mach-O file that the export looks at: the header and the load commands. The input may be hostile, so every length and offset of the file is checked against the file's
/// length, in <c>long</c> arithmetic, before it is used, and nothing is allocated that is not small and bounded.
/// <para>
/// The 64-bit header (<c>struct mach_header_64</c>, all fields little-endian here) is 32 bytes: <c>magic</c> (0), <c>cputype</c> (4), <c>cpusubtype</c> (8), <c>filetype</c> (12), <c>ncmds</c> (16),
/// <c>sizeofcmds</c> (20), <c>flags</c> (24) and <c>reserved</c> (28). The load commands follow it directly; each starts with <c>cmd</c> (4 bytes) and <c>cmdsize</c> (4 bytes).
/// </para>
/// </summary>
internal static class MachOFile
{
    /// <summary>The size of <c>struct mach_header_64</c>.</summary>
    public const int HeaderSize = 32;

    /// <summary><c>LC_CODE_SIGNATURE</c>: a <c>linkedit_data_command</c>, 16 bytes: <c>cmd</c>, <c>cmdsize</c>, <c>dataoff</c> (at 8), <c>datasize</c> (at 12).</summary>
    public const uint CodeSignatureCommand = 0x1D;

    /// <summary><c>LC_VERSION_MIN_MACOSX</c>: <c>cmd</c>, <c>cmdsize</c>, <c>version</c> (at 8), <c>sdk</c> (at 12).</summary>
    public const uint VersionMinMacOsCommand = 0x24;

    /// <summary><c>LC_BUILD_VERSION</c>: <c>cmd</c>, <c>cmdsize</c>, <c>platform</c> (at 8), <c>minos</c> (at 12), <c>sdk</c> (at 16), <c>ntools</c> (at 20).</summary>
    public const uint BuildVersionCommand = 0x32;

    /// <summary>The <c>cputype</c> of x86-64 (<c>CPU_TYPE_X86 | CPU_ARCH_ABI64</c>).</summary>
    public const uint CpuTypeX64 = 0x01000007;

    /// <summary>The <c>cputype</c> of arm64 (<c>CPU_TYPE_ARM | CPU_ARCH_ABI64</c>).</summary>
    public const uint CpuTypeArm64 = 0x0100000C;

    /// <summary>More bytes of load commands than this are refused: real programs have a few kilobytes of them.</summary>
    public const int MaxCommandBytes = 4 << 20;

    /// <summary>The <c>cputype</c> for an export target's processor (<c>x64</c> or <c>arm64</c>).</summary>
    public static uint CpuTypeOf(string arch) => arch == "arm64" ? CpuTypeArm64 : CpuTypeX64;

    /// <summary>Whether the first four bytes are the magic of a universal (fat) binary, in either byte order and with 32- or 64-bit offsets.</summary>
    public static bool IsFatMagic(uint bigEndianMagic) => bigEndianMagic is 0xCAFEBABE or 0xBEBAFECA or 0xCAFEBABF or 0xBFBAFECA;

    /// <summary>
    /// Reads the header and load commands of the thin 64-bit Mach-O file that is the <paramref name="length"/> bytes at <paramref name="start"/> of <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">A seekable stream.</param>
    /// <param name="start">Where the Mach-O file starts in the stream (0 unless it is a slice of a universal binary).</param>
    /// <param name="length">The length of the Mach-O file; everything the header says is checked against it.</param>
    /// <param name="name">The file's name, for the messages.</param>
    /// <exception cref="ExportException">The bytes are not a thin 64-bit little-endian Mach-O, or a length or offset in them does not fit the file.</exception>
    public static MachOImage Read(Stream stream, long start, long length, string name)
    {
        if (length < HeaderSize)
        {
            throw new ExportException($"{name} is too short to be a Mach-O file ({length} bytes).");
        }

        byte[] header = new byte[HeaderSize];
        ReadAt(stream, start, header, name);

        // The magic is compared as the four bytes read big-endian: 0xCFFAEDFE is MH_MAGIC_64 stored little-endian (the only layout read here).
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (IsFatMagic(magic))
        {
            throw new ExportException($"{name} is a universal (fat) Mach-O binary; a thin binary expected, published for one processor (osx-arm64 or osx-x64).");
        }

        if (magic != 0xCFFAEDFE)
        {
            throw new ExportException(magic is 0xCEFAEDFE or 0xFEEDFACE or 0xFEEDFACF
                ? $"{name} is a Mach-O file that is 32-bit or big-endian; a 64-bit little-endian one is expected."
                : $"{name} is not a Mach-O file.");
        }

        uint cpuType = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        uint commandCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        uint commandBytes = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));

        // sizeofcmds is bounded twice: by a constant (so a hostile file cannot make this allocate much) and by the file (the commands must be inside it).
        if (commandBytes > MaxCommandBytes || HeaderSize + (long)commandBytes > length)
        {
            throw new ExportException($"{name} has load commands of {commandBytes} bytes, which do not fit the file ({length} bytes) or are far larger than any program's.");
        }

        // Every load command is at least 8 bytes (cmd and cmdsize), so the count has a ceiling that the next loop could not exceed anyway.
        if (commandCount > commandBytes / 8)
        {
            throw new ExportException($"{name} says it has {commandCount} load commands, which do not fit in {commandBytes} bytes.");
        }

        byte[] commandData = new byte[commandBytes];
        ReadAt(stream, start + HeaderSize, commandData, name);

        var commands = new List<LoadCommand>((int)commandCount);
        int offset = 0;
        for (uint i = 0; i < commandCount; i++)
        {
            if (offset + 8L > commandData.Length)
            {
                throw new ExportException($"{name}: load command {i} starts outside the load commands.");
            }

            uint command = BinaryPrimitives.ReadUInt32LittleEndian(commandData.AsSpan(offset));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(commandData.AsSpan(offset + 4));
            if (size < 8 || size % 8 != 0 || size > commandData.Length - offset)
            {
                throw new ExportException($"{name}: load command {i} (0x{command:X}) has a size of {size} bytes, which is not valid here.");
            }

            commands.Add(new LoadCommand(command, offset, (int)size));
            offset += (int)size;
        }

        return new MachOImage(cpuType, commandData, commands);
    }

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="position"/>; a short file is an <see cref="ExportException"/>.</summary>
    public static void ReadAt(Stream stream, long position, Span<byte> buffer, string name)
    {
        try
        {
            stream.Position = position;
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException e)
        {
            throw new ExportException($"{name} ends before the bytes at offset {position}.", e);
        }
    }
}
