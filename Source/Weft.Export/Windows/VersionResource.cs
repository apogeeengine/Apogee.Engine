using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Weft.Content.Packages;

namespace Weft.Export.Windows;

/// <summary>
/// The <c>RT_VERSION</c> resource: what Explorer shows in the Details tab of the program's properties, what task managers show as the program's description, and what installers compare.
/// <para>
/// The format is a tree of blocks, each <c>wLength</c> (the block's size, without the padding that follows it), <c>wValueLength</c>, <c>wType</c> (0 binary value, 1 text), the key as a NUL-terminated
/// UTF-16 string, padding to 4 bytes, the value, padding to 4 bytes, and the child blocks. <c>VS_VERSIONINFO</c> holds a <c>VS_FIXEDFILEINFO</c> (the numbers) and two children: <c>StringFileInfo</c>,
/// whose <c>StringTable</c> "040904B0" (language 0x0409, code page 1200 = UTF-16) holds the <c>String</c> blocks; and <c>VarFileInfo</c> with the <c>Translation</c> that says which tables there are.
/// </para>
/// </summary>
internal static class VersionResource
{
    /// <summary>The language of the resource, and of the string table: English (United States).</summary>
    public const int Language = 0x0409;

    private const int CodePageUnicode = 1200;
    private const uint FixedInfoSignature = 0xFEEF04BD;
    private const uint FixedInfoStructVersion = 0x0001_0000;
    private const uint FixedInfoFlagsMask = 0x3F;
    private const uint OsNtWindows32 = 0x0004_0004; // VOS_NT_WINDOWS32: VOS_NT << 16 | VOS__WINDOWS32
    private const uint FileTypeApp = 1; // VFT_APP

    /// <summary>The version resource of the game <paramref name="manifest"/>: its numbers are <c>major.minor.patch.0</c>, its strings are what the manifest says, and <c>EngineVersion</c> is the engine's (<see cref="Weft.Core.EngineVersion.Text"/>).</summary>
    /// <exception cref="ExportException">A part of the version is above 65535 (the resource holds 16 bit numbers), or the resource would be over 64 KiB.</exception>
    public static byte[] Build(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        PackageVersion version = manifest.Version;
        if (version.Major is < 0 or > ushort.MaxValue || version.Minor is < 0 or > ushort.MaxValue || version.Patch is < 0 or > ushort.MaxValue)
        {
            throw new ExportException($"The version {version} cannot be a Windows version: each part must be between 0 and 65535.");
        }

        string number = string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Patch}.0");
        string executable = manifest.Executable + ".exe";

        // The strings Explorer knows, in the order Microsoft's tools write them. The file's description is what task managers show: the game's name when it has no description.
        (string Key, string Value)[] strings =
        [
            ("CompanyName", manifest.Organization),
            ("FileDescription", manifest.Description.Length > 0 ? manifest.Description : manifest.Name),
            ("FileVersion", number),
            ("InternalName", manifest.Executable),
            ("LegalCopyright", manifest.Copyright),
            ("OriginalFilename", executable),
            ("ProductName", manifest.Name),
            ("ProductVersion", number),
            ("EngineVersion", Weft.Core.EngineVersion.Text), // the engine the executable is made of: the game's own versions are the ones above
        ];

        var block = new BlockWriter();
        int info = block.Begin("VS_VERSION_INFO", valueLength: 52, text: false);
        WriteFixedInfo(block, version);

        int fileInfo = block.Begin("StringFileInfo", valueLength: 0, text: true);
        int table = block.Begin("040904B0", valueLength: 0, text: true);
        foreach ((string key, string value) in strings)
        {
            block.WriteString(key, Clean(value));
        }

        block.End(table);
        block.End(fileInfo);

        int varInfo = block.Begin("VarFileInfo", valueLength: 0, text: true);
        int translation = block.Begin("Translation", valueLength: 4, text: false);
        block.WriteUInt32((uint)(Language | (CodePageUnicode << 16))); // the language in the low word, the code page in the high word
        block.End(translation);
        block.End(varInfo);
        block.End(info);
        return block.ToArray();
    }

    /// <summary>
    /// <c>VS_FIXEDFILEINFO</c> (52 bytes): <c>dwSignature</c>, <c>dwStrucVersion</c>, the file version and the product version (each two words: major and minor, then patch and build), the flag mask and
    /// flags, <c>dwFileOS</c>, <c>dwFileType</c>, <c>dwFileSubtype</c> and the date (not used).
    /// </summary>
    private static void WriteFixedInfo(BlockWriter block, PackageVersion version)
    {
        uint high = ((uint)version.Major << 16) | (uint)version.Minor;
        uint low = (uint)version.Patch << 16; // the fourth number, the build, is 0
        block.WriteUInt32(FixedInfoSignature);
        block.WriteUInt32(FixedInfoStructVersion);
        block.WriteUInt32(high);
        block.WriteUInt32(low);
        block.WriteUInt32(high);
        block.WriteUInt32(low);
        block.WriteUInt32(FixedInfoFlagsMask);
        block.WriteUInt32(0); // dwFileFlags
        block.WriteUInt32(OsNtWindows32);
        block.WriteUInt32(FileTypeApp);
        block.WriteUInt32(0); // dwFileSubtype
        block.WriteUInt32(0); // dwFileDateMS
        block.WriteUInt32(0); // dwFileDateLS
    }

    /// <summary>A string for a version block: a NUL ends it and a control character is no use in a Details tab, so those become spaces (NUL is dropped).</summary>
    private static string Clean(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c != '\0')
            {
                text.Append(char.IsControl(c) ? ' ' : c);
            }
        }

        return text.ToString();
    }

    /// <summary>Writes the blocks one after the other, keeping them 4-byte aligned and patching each <c>wLength</c> when the block ends.</summary>
    private sealed class BlockWriter
    {
        private readonly List<byte> _bytes = [];

        public byte[] ToArray()
        {
            if (_bytes.Count > ushort.MaxValue)
            {
                throw new ExportException("The version information is over 64 KiB (the description or the copyright is too long).");
            }

            return [.. _bytes];
        }

        /// <summary>Starts a block at the next 4-byte boundary: <c>wLength</c> (patched by <see cref="End"/>), <c>wValueLength</c>, <c>wType</c>, the key, and padding up to where the value goes.</summary>
        /// <returns>Where the block starts, for <see cref="End"/>.</returns>
        public int Begin(string key, int valueLength, bool text)
        {
            Align();
            int start = _bytes.Count;
            WriteUInt16(0);
            WriteUInt16((ushort)valueLength);
            WriteUInt16(text ? (ushort)1 : (ushort)0);
            WriteWideString(key);
            Align();
            return start;
        }

        /// <summary>Ends the block that <see cref="Begin"/> started at <paramref name="start"/>: its length is everything written since, without padding after it.</summary>
        public void End(int start)
        {
            int length = _bytes.Count - start;
            if (length > ushort.MaxValue)
            {
                throw new ExportException("The version information is over 64 KiB (the description or the copyright is too long).");
            }

            _bytes[start] = (byte)length;
            _bytes[start + 1] = (byte)(length >> 8);
        }

        /// <summary>A <c>String</c> block: the key and, as the value, the text with its NUL; <c>wValueLength</c> counts the characters with the NUL.</summary>
        public void WriteString(string key, string value)
        {
            int start = Begin(key, value.Length + 1, text: true);
            WriteWideString(value);
            End(start);
        }

        public void WriteUInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            foreach (byte b in buffer)
            {
                _bytes.Add(b);
            }
        }

        private void WriteUInt16(ushort value)
        {
            _bytes.Add((byte)value);
            _bytes.Add((byte)(value >> 8));
        }

        private void WriteWideString(string value)
        {
            _bytes.AddRange(Encoding.Unicode.GetBytes(value));
            WriteUInt16(0);
        }

        private void Align()
        {
            while (_bytes.Count % 4 != 0)
            {
                _bytes.Add(0);
            }
        }
    }
}
