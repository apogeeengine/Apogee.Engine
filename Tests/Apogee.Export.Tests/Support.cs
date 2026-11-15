using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Apogee.Export.Tests;

/// <summary>A folder that is deleted when the test is done.</summary>
internal sealed class Scratch : IDisposable
{
    public Scratch()
    {
        Root = Directory.CreateTempSubdirectory("apogee-export-test-").FullName;
    }

    public string Root { get; }

    public string Path(string relative) => System.IO.Path.Combine(Root, relative);

    public string Write(string relative, string text) => Write(relative, Encoding.UTF8.GetBytes(text));

    public string Write(string relative, byte[] data)
    {
        string full = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        return full;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

/// <summary>Headers of executables, and a published runtime folder made of them, so that every target can be exported from any host.</summary>
internal static class FakeRuntime
{
    /// <summary>The first bytes of a program for <paramref name="target"/>: what <c>RuntimeFolder.Detect</c> reads.</summary>
    public static byte[] Header(ExportTarget target)
    {
        byte[] bytes = new byte[0x200];
        switch (target.Os)
        {
            case "win":
                bytes[0] = (byte)'M';
                bytes[1] = (byte)'Z';
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x3C), 0x80);
                "PE\0\0"u8.CopyTo(bytes.AsSpan(0x80));
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84), (ushort)(target.Arch == "arm64" ? 0xAA64 : 0x8664));
                break;
            case "linux":
                "\x7f"u8.CopyTo(bytes);
                "ELF"u8.CopyTo(bytes.AsSpan(1));
                bytes[4] = 2; // 64-bit
                bytes[5] = 1; // little endian
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), (ushort)(target.Arch == "arm64" ? 0xB7 : 0x3E));
                break;
            default:
                BinaryPrimitives.WriteUInt32BigEndian(bytes, 0xCFFAEDFE);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), target.Arch == "arm64" ? 0x0100000Cu : 0x01000007u);
                break;
        }

        return bytes;
    }

    /// <summary>An executable: the header, noise, the pack key slot, noise.</summary>
    public static byte[] Executable(ExportTarget target)
    {
        byte[] noise = SHA512.HashData("noise"u8);
        if (target.Os == "win")
        {
            // A Windows export rewrites the executable's resources, so it must be a real PE file: the code is noise around the slot, with a manifest and relocations, and a console subsystem like the runtime's.
            return TinyPe.Typical([.. noise, .. PackKeySlot.Template.ToArray(), .. noise], machine: (ushort)(target.Arch == "arm64" ? 0xAA64 : 0x8664)).Build();
        }

        return [.. Header(target), .. noise, .. PackKeySlot.Template.ToArray(), .. noise];
    }

    /// <summary>Makes a published runtime folder for <paramref name="target"/>: the program, native libraries, compiled shaders, and the debug files, compiler and development files that an export leaves out.</summary>
    public static string Make(Scratch scratch, ExportTarget target, string folder = "runtime")
    {
        bool windows = target.Os == "win";
        string library = windows ? "flecs.dll" : target.Os == "osx" ? "libflecs.dylib" : "libflecs.so";
        scratch.Write($"{folder}/{(windows ? "Apogee.Runtime.exe" : "Apogee.Runtime")}", Executable(target));
        scratch.Write($"{folder}/{library}", "a native library");
        scratch.Write($"{folder}/Shaders/imported/x.spv", "compiled shader");
        scratch.Write($"{folder}/Shaders/a.slang", "shader source");
        scratch.Write($"{folder}/{(windows ? "slang.dll" : "libslang.so")}", "the compiler");
        scratch.Write($"{folder}/Apogee.Runtime.pdb", "debug");
        scratch.Write($"{folder}/Apogee.Runtime.dbg", "debug");
        scratch.Write($"{folder}/Apogee.Runtime.lib", "import library");
        scratch.Write($"{folder}/steam_appid.txt", "480");
        scratch.Write($"{folder}/game.xml", "<Game/>");
        scratch.Write($"{folder}/data/old.pack", "an old pack");
        return scratch.Path(folder);
    }

    /// <summary>The offset of the slot in an executable made by <see cref="Executable"/>.</summary>
    public static int SlotOffset(byte[] file) => file.AsSpan().IndexOf(PackKeySlot.Template[..16]);

    /// <summary>The bytes of every file under a folder, with their names: to tell whether anything changed.</summary>
    public static string Fingerprint(string folder) => string.Join(
        "\n",
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(e => System.IO.Path.GetRelativePath(folder, e) + (File.Exists(e) ? ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(e))) : "/")));
}
