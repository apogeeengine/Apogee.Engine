using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Weft.Export;

/// <summary>
/// Reads the oldest macOS version a Mach-O file runs on (<c>LC_BUILD_VERSION</c>'s <c>minos</c>, or <c>LC_VERSION_MIN_MACOSX</c>'s <c>version</c>), for the bundle's <c>LSMinimumSystemVersion</c>.
/// Versions are 32-bit numbers, <c>major &lt;&lt; 16 | minor &lt;&lt; 8 | patch</c>. A file that cannot be read, or has no version, has none: this is information for a plist, so it never fails an export.
/// </summary>
internal static class MachOBuildVersion
{
    /// <summary>The platform number of macOS in <c>LC_BUILD_VERSION</c> (<c>PLATFORM_MACOS</c>).</summary>
    private const uint PlatformMacOs = 1;

    /// <summary>A universal binary lists a handful of processors: more than this is not read.</summary>
    private const uint MaxFatArchitectures = 16;

    /// <summary>11.0, the oldest macOS that runs on Apple silicon, and what a program without a version is taken to need.</summary>
    public const uint Default = 11u << 16;

    /// <summary>The version that macOS needs to run every one of <paramref name="files"/> for the processor <paramref name="cpuType"/>: the newest of their minimums, but at least <see cref="Default"/>.</summary>
    public static uint Required(IEnumerable<string> files, uint cpuType)
    {
        uint required = Default;
        foreach (string file in files)
        {
            if (Read(file, cpuType) is { } version && version > required)
            {
                required = version;
            }
        }

        return required;
    }

    /// <summary>The version as a plist value: <c>11.0</c>, or <c>13.3.1</c> when there is a patch level.</summary>
    public static string Format(uint version)
    {
        uint major = version >> 16;
        uint minor = (version >> 8) & 0xFF;
        uint patch = version & 0xFF;
        return patch == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}")
            : string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}.{patch}");
    }

    /// <summary>The minimum version of the file's image for <paramref name="cpuType"/> (the file itself when it is thin, its slice when it is universal); null when it has none, or is not a Mach-O file of that processor.</summary>
    internal static uint? Read(string path, uint cpuType)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadFrom(stream, cpuType, Path.GetFileName(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ExportException)
        {
            return null;
        }
    }

    internal static uint? ReadFrom(Stream stream, uint cpuType, string name)
    {
        long length = stream.Length;
        if (length < 8)
        {
            return null;
        }

        byte[] start = new byte[8];
        MachOFile.ReadAt(stream, 0, start, name);
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(start);
        if (magic is 0xCAFEBABE)
        {
            // A universal binary (struct fat_header, big-endian: magic, nfat_arch), then nfat_arch entries of struct fat_arch, 20 bytes: cputype, cpusubtype, offset, size, align.
            uint count = BinaryPrimitives.ReadUInt32BigEndian(start.AsSpan(4));
            if (count > MaxFatArchitectures || 8 + (long)count * 20 > length)
            {
                return null;
            }

            byte[] entries = new byte[count * 20];
            MachOFile.ReadAt(stream, 8, entries, name);
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> entry = entries.AsSpan(i * 20, 20);
                long offset = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                long size = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
                if (BinaryPrimitives.ReadUInt32BigEndian(entry) == cpuType && offset + size <= length)
                {
                    return ReadThin(stream, offset, size, cpuType, name);
                }
            }

            return null;
        }

        return MachOFile.IsFatMagic(magic) ? null : ReadThin(stream, 0, length, cpuType, name);
    }

    private static uint? ReadThin(Stream stream, long start, long length, uint cpuType, string name)
    {
        MachOImage image = MachOFile.Read(stream, start, length, name);
        if (image.CpuType != cpuType)
        {
            return null;
        }

        uint? newest = null;
        foreach (LoadCommand command in image.Commands)
        {
            ReadOnlySpan<byte> bytes = image.CommandBytes.AsSpan(command.Offset, command.Size);
            uint? version = null;
            if (command.Command == MachOFile.BuildVersionCommand && bytes.Length >= 24 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) == PlatformMacOs)
            {
                version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
            }
            else if (command.Command == MachOFile.VersionMinMacOsCommand && bytes.Length >= 16)
            {
                version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
            }

            if (version is { } found && (newest is null || found > newest))
            {
                newest = found;
            }
        }

        return newest;
    }
}
