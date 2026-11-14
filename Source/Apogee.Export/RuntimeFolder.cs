using System;
using System.Buffers.Binary;
using System.IO;

namespace Apogee.Export;

/// <summary>
/// The published runtime that an export is made from: a folder with the NativeAOT <c>Apogee.Runtime</c> (<c>./build.sh aot</c>, or the CI artifact <c>apogee-&lt;rid&gt;</c>), its native libraries and its
/// compiled shaders. The runtime is a native program of one platform, so the folder has to be published for the target; this class finds it and checks, by reading the executable's header.
/// </summary>
public static class RuntimeFolder
{
    /// <summary>The name of the runtime's program, without <c>.exe</c>.</summary>
    public const string ProgramName = "Apogee.Runtime";

    /// <summary>
    /// The runtime folder to export <paramref name="target"/> from: <paramref name="given"/> (<c>--runtime</c>); else the folder of the running program when that is the NativeAOT runtime itself; else the
    /// <c>Bin/publish/runtime</c> of the nearest folder above the running program or the current folder (a checkout of the engine). Checked to be published for <paramref name="target"/>.
    /// </summary>
    /// <exception cref="ExportException">There is no runtime folder, or it is for another platform.</exception>
    public static string Resolve(string? given, ExportTarget target)
    {
        string? folder = given is not null ? Path.GetFullPath(given) : FindRuntime();
        if (folder is null)
        {
            throw new ExportException($"No published runtime found. Publish one with ./build.sh aot (on {target.Rid}'s operating system), or pass the folder of one with --runtime <dir>; CI uploads one per platform as the artifact apogee-<rid>.");
        }

        if (!Directory.Exists(folder))
        {
            throw new ExportException($"The runtime folder {folder} does not exist.");
        }

        // The program of the target's system, else the other system's, so that the message can say which runtime the folder holds.
        string program = ProgramPath(folder, target.Os == "win");
        if (!File.Exists(program))
        {
            string other = ProgramPath(folder, target.Os != "win");
            program = File.Exists(other)
                ? other
                : throw new ExportException($"{folder} holds no {ProgramName}{(target.Os == "win" ? ".exe" : "")}: it is not a published runtime (./build.sh aot).");
        }

        ExportTarget found = Detect(program);
        if (found != target)
        {
            throw new ExportException($"{folder} holds a {found.Rid} runtime; --target {target.Rid} needs one published for {target.Rid} (./build.sh aot on that operating system, or the CI artifact apogee-{target.Rid}), passed with --runtime <dir>.");
        }

        return folder;
    }

    /// <summary>The runtime's program in <paramref name="folder"/>: <c>Apogee.Runtime.exe</c> for Windows, <c>Apogee.Runtime</c> otherwise.</summary>
    public static string ProgramPath(string folder, bool windows) => Path.Combine(folder, windows ? ProgramName + ".exe" : ProgramName);

    /// <summary>Reads the operating system and processor an executable is for from its header (PE for Windows, ELF for Linux, thin Mach-O for macOS).</summary>
    /// <exception cref="ExportException">The file is none of these, is for a processor the engine does not support, or is a universal (fat) Mach-O.</exception>
    public static ExportTarget Detect(string executable)
    {
        byte[] head = new byte[4096];
        int read;
        using (FileStream file = File.OpenRead(executable))
        {
            read = file.Read(head, 0, head.Length);
        }

        ReadOnlySpan<byte> bytes = head.AsSpan(0, read);
        string name = Path.GetFileName(executable);
        if (bytes.Length >= 64 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z')
        {
            int pe = BinaryPrimitives.ReadInt32LittleEndian(bytes[0x3C..]);
            if (pe > 0 && pe + 6 <= bytes.Length && bytes[pe..(pe + 4)].SequenceEqual("PE\0\0"u8))
            {
                return BinaryPrimitives.ReadUInt16LittleEndian(bytes[(pe + 4)..]) switch
                {
                    0x8664 => new ExportTarget("win", "x64"),
                    0xAA64 => new ExportTarget("win", "arm64"),
                    ushort machine => throw new ExportException($"{name} is a Windows program for a processor the engine does not support (machine 0x{machine:X4})."),
                };
            }
        }

        if (bytes.Length >= 20 && bytes[0] == 0x7F && bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F')
        {
            bool bigEndian = bytes[5] == 2;
            ushort machine = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bytes[18..]) : BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]);
            return machine switch
            {
                0x3E => new ExportTarget("linux", "x64"),
                0xB7 => new ExportTarget("linux", "arm64"),
                _ => throw new ExportException($"{name} is a Linux program for a processor the engine does not support (e_machine 0x{machine:X4})."),
            };
        }

        if (bytes.Length >= 8)
        {
            uint magic = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            if (magic is 0xCAFEBABE or 0xBEBAFECA or 0xCAFEBABF or 0xBFBAFECA)
            {
                throw new ExportException($"{name} is a universal (fat) Mach-O binary; a thin binary expected, published for one processor (osx-arm64 or osx-x64).");
            }

            if (magic == 0xCFFAEDFE)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) switch
                {
                    0x01000007 => new ExportTarget("osx", "x64"),
                    0x0100000C => new ExportTarget("osx", "arm64"),
                    uint cpu => throw new ExportException($"{name} is a macOS program for a processor the engine does not support (cputype 0x{cpu:X8})."),
                };
            }
        }

        throw new ExportException($"{name} is not a Windows (PE), Linux (ELF) or macOS (Mach-O) program: {executable}.");
    }

    /// <summary>
    /// Whether a file of the runtime folder goes into an export. Left out: debug files (<c>*.pdb</c>, <c>*.dbg</c>, <c>*.dSYM</c>), import libraries (<c>*.lib</c>, <c>*.exp</c>), the Slang compiler's
    /// libraries (a shipped game loads compiled shaders and never the compiler) and the shader sources (<c>Shaders/*.slang</c>; <c>Shaders/imported</c> stays), and what only a development
    /// folder has: <c>steam_appid.txt</c> (the dev switch of Steam), <c>game.xml</c> and <c>data/</c> (the export writes its own).
    /// </summary>
    /// <param name="relativePath">The path in the runtime folder, with <c>/</c> or <c>\</c> as separators.</param>
    public static bool Ships(string relativePath)
    {
        string path = relativePath.Replace('\\', '/').TrimStart('/');
        string[] segments = path.Split('/');
        string file = segments[^1];
        if (segments.Length == 1 && (file is "steam_appid.txt" or "game.xml"))
        {
            return false;
        }

        if (segments[0] == "data" && segments.Length > 1)
        {
            return false;
        }

        foreach (string segment in segments)
        {
            if (segment.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".exp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (segments.Length == 1 && file.Contains("slang", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (segments[0] == "Shaders" && segments.Length > 1 && segments[1] != "imported" && file.EndsWith(".slang", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string? FindRuntime()
    {
        string own = AppContext.BaseDirectory;

        // The NativeAOT runtime itself (an apphost of the CoreCLR build has its Apogee.Runtime.dll beside it and is no runtime to ship).
        if (File.Exists(ProgramPath(own, OperatingSystem.IsWindows())) && !File.Exists(Path.Combine(own, ProgramName + ".dll")))
        {
            return Path.GetFullPath(own);
        }

        foreach (string start in new[] { own, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "Bin", "publish", "runtime");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
