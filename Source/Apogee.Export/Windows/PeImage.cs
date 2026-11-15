using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Apogee.Export.Windows;

/// <summary>One entry of a PE file's section table (<c>IMAGE_SECTION_HEADER</c>, 40 bytes).</summary>
/// <param name="Name">The 8 byte <c>Name</c>, up to the first NUL.</param>
/// <param name="VirtualSize">The size of the section in memory (<c>VirtualSize</c>).</param>
/// <param name="VirtualAddress">The RVA of the section (<c>VirtualAddress</c>).</param>
/// <param name="SizeOfRawData">The size of the section in the file (<c>SizeOfRawData</c>), a multiple of <c>FileAlignment</c>.</param>
/// <param name="PointerToRawData">The offset of the section in the file (<c>PointerToRawData</c>).</param>
/// <param name="Characteristics">The flags (<c>Characteristics</c>).</param>
internal readonly record struct PeSection(string Name, uint VirtualSize, uint VirtualAddress, uint SizeOfRawData, uint PointerToRawData, uint Characteristics)
{
    /// <summary>The offset in the file where the section's data ends.</summary>
    public long RawEnd => (long)PointerToRawData + SizeOfRawData;

    /// <summary>How much of the section the loader maps and a reader may use: the smaller of the two sizes (a <c>VirtualSize</c> of 0 means the raw size).</summary>
    public long Extent => Math.Min(SizeOfRawData, VirtualSize == 0 ? SizeOfRawData : VirtualSize);

    /// <summary>The RVA after the last byte the section occupies in memory.</summary>
    public long VirtualEnd => (long)VirtualAddress + (VirtualSize != 0 ? VirtualSize : SizeOfRawData);
}

/// <summary>
/// A 64-bit (PE32+) or 32-bit (PE32) Windows executable held in memory, read and changed field by field. Only what the export touches is understood: the headers, the section table, the data
/// directories and the file's tail. Every read is checked against the file's length, so a damaged or hostile file is an <see cref="ExportException"/>, never an out-of-range access.
/// <para>
/// The layout (Microsoft PE format): the DOS header, whose <c>e_lfanew</c> at 0x3C points to the signature <c>PE\0\0</c>; the COFF file header (20 bytes); the optional header
/// (<c>SizeOfOptionalHeader</c> bytes: fields, then the data directory table); the section table (40 bytes a section); room up to <c>SizeOfHeaders</c>; then the sections' raw data.
/// </para>
/// </summary>
internal sealed class PeImage
{
    /// <summary>The size of one <c>IMAGE_SECTION_HEADER</c>.</summary>
    public const int SectionHeaderSize = 40;

    /// <summary>The most sections the Windows loader accepts.</summary>
    public const int MaxSections = 96;

    /// <summary>Index of the resource table in the data directories (<c>IMAGE_DIRECTORY_ENTRY_RESOURCE</c>).</summary>
    public const int ResourceDirectory = 2;

    /// <summary>Index of the certificate table in the data directories (<c>IMAGE_DIRECTORY_ENTRY_SECURITY</c>); its "address" is a file offset, not an RVA.</summary>
    public const int CertificateDirectory = 4;

    /// <summary><c>IMAGE_SUBSYSTEM_WINDOWS_GUI</c>: a program with windows and no console.</summary>
    public const ushort SubsystemGui = 2;

    /// <summary><c>IMAGE_SUBSYSTEM_WINDOWS_CUI</c>: a console program.</summary>
    public const ushort SubsystemConsole = 3;

    private const ushort Magic32 = 0x10B;
    private const ushort Magic64 = 0x20B;
    private const int CoffHeaderSize = 20;
    private const int MaxDirectories = 16;
    private const long MaxAlignment = 0x1000_0000;

    // Offsets in the optional header, which are the same in both formats up to ImageBase (PE32 has BaseOfData and a 4 byte ImageBase, PE32+ an 8 byte ImageBase).
    private const int OffsetSizeOfInitializedData = 8;
    private const int OffsetSectionAlignment = 32;
    private const int OffsetFileAlignment = 36;
    private const int OffsetSizeOfImage = 56;
    private const int OffsetSizeOfHeaders = 60;
    private const int OffsetCheckSum = 64;
    private const int OffsetSubsystem = 68;
    private const int OffsetDllCharacteristics = 70;

    private readonly int _peOffset;
    private readonly int _optionalOffset;
    private readonly int _directoryCountOffset;
    private readonly int _directoryTableOffset;
    private readonly int _sectionTableOffset;
    private readonly List<PeSection> _sections = [];

    private PeImage(byte[] bytes, int peOffset, int optionalOffset, bool is64Bit, int sizeOfOptionalHeader)
    {
        Bytes = bytes;
        _peOffset = peOffset;
        _optionalOffset = optionalOffset;
        Is64Bit = is64Bit;
        _directoryCountOffset = optionalOffset + (is64Bit ? 108 : 92);
        _directoryTableOffset = optionalOffset + (is64Bit ? 112 : 96);
        _sectionTableOffset = optionalOffset + sizeOfOptionalHeader;
    }

    /// <summary>The file. It is longer or shorter after <see cref="RemoveOverlay"/> and <see cref="Resize"/>; the array is replaced then, so read this property again.</summary>
    public byte[] Bytes { get; private set; }

    /// <summary>Whether the optional header is PE32+ (64-bit).</summary>
    public bool Is64Bit { get; }

    /// <summary>The sections, in section table order (which is also the order of their RVAs).</summary>
    public IReadOnlyList<PeSection> Sections => _sections;

    /// <summary><c>SectionAlignment</c>: sections start at a multiple of it in memory.</summary>
    public uint SectionAlignment { get; private set; }

    /// <summary><c>FileAlignment</c>: sections start at a multiple of it in the file, and their raw size is one.</summary>
    public uint FileAlignment { get; private set; }

    /// <summary><c>SizeOfImage</c>: the memory the loader reserves; a multiple of <see cref="SectionAlignment"/>.</summary>
    public uint SizeOfImage { get; private set; }

    /// <summary><c>SizeOfHeaders</c>: the size of everything before the first section, a multiple of <see cref="FileAlignment"/>.</summary>
    public uint SizeOfHeaders { get; private set; }

    /// <summary>The number of data directories the optional header declares (<c>NumberOfRvaAndSizes</c>), at most 16.</summary>
    public int DirectoryCount { get; private set; }

    /// <summary>
    /// The offset in the file where the section data ends: the end of the last section (<c>PointerToRawData + SizeOfRawData</c>), or <see cref="SizeOfHeaders"/> when no section has data.
    /// What follows is the overlay.
    /// </summary>
    public long SectionsEnd
    {
        get
        {
            long end = SizeOfHeaders;
            foreach (PeSection section in _sections)
            {
                if (section.SizeOfRawData != 0)
                {
                    end = Math.Max(end, section.RawEnd);
                }
            }

            return end;
        }
    }

    /// <summary>The current <c>SizeOfInitializedData</c>.</summary>
    public uint SizeOfInitializedData => U32(Bytes, _optionalOffset + OffsetSizeOfInitializedData);

    /// <summary><c>Subsystem</c>: <see cref="SubsystemConsole"/> or <see cref="SubsystemGui"/> for the programs we handle.</summary>
    public ushort Subsystem => U16(Bytes, _optionalOffset + OffsetSubsystem);

    /// <summary>Whether the image has <c>IMAGE_DLLCHARACTERISTICS_FORCE_INTEGRITY</c> (0x80) in <c>DllCharacteristics</c>: Windows then loads it only if it is signed.</summary>
    public bool ForcesIntegrity => (U16(Bytes, _optionalOffset + OffsetDllCharacteristics) & 0x0080) != 0;

    /// <summary>The offset in the file of the first byte after the section table that a new section header would use.</summary>
    public int NextSectionHeaderOffset => _sectionTableOffset + (SectionHeaderSize * _sections.Count);

    /// <summary>
    /// Reads the headers of <paramref name="bytes"/> and checks that they describe a file we can change: the DOS header and <c>PE\0\0</c> signature, a PE32 or PE32+ optional header that fits its
    /// declared size, power-of-two alignments, a section table inside the headers, sections whose data is in the file and whose RVAs rise without overlapping. The array is used as it is (and changed
    /// by the setters): pass a copy.
    /// </summary>
    /// <exception cref="ExportException">The file is not a Windows executable, is damaged, or is laid out in a way this code does not handle.</exception>
    public static PeImage Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        // DOS header: "MZ" at 0, e_lfanew (the offset of the PE signature) at 0x3C. The loader needs it to be a multiple of 4.
        if (bytes.Length < 0x40 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            throw new ExportException("The executable is not a Windows program: it has no MZ header.");
        }

        long peOffset = U32(bytes, 0x3C);
        if (peOffset < 0x40 || (peOffset & 3) != 0 || peOffset + 4 + CoffHeaderSize > bytes.Length)
        {
            throw new ExportException("The executable's PE header offset (e_lfanew) is outside the file.");
        }

        if (bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E' || bytes[peOffset + 2] != 0 || bytes[peOffset + 3] != 0)
        {
            throw new ExportException("The executable has no PE signature.");
        }

        // COFF file header, after the signature: Machine(2) NumberOfSections(2) TimeDateStamp(4) PointerToSymbolTable(4) NumberOfSymbols(4) SizeOfOptionalHeader(2) Characteristics(2).
        int coff = (int)peOffset + 4;
        int sectionCount = U16(bytes, coff + 2);
        int sizeOfOptionalHeader = U16(bytes, coff + 16);
        int optional = coff + CoffHeaderSize;
        if (sectionCount > MaxSections)
        {
            throw new ExportException($"The executable declares {sectionCount} sections; Windows loads at most {MaxSections}.");
        }

        if (optional + 2 > bytes.Length)
        {
            throw new ExportException("The executable ends inside its headers.");
        }

        ushort magic = U16(bytes, optional);
        bool is64Bit = magic == Magic64;
        if (!is64Bit && magic != Magic32)
        {
            throw new ExportException($"The executable's optional header has magic 0x{magic:X4}: it is neither a PE32 nor a PE32+ file.");
        }

        var image = new PeImage(bytes, (int)peOffset, optional, is64Bit, sizeOfOptionalHeader);
        image.ReadOptionalHeader(sizeOfOptionalHeader);
        image.ReadSections(sectionCount);
        return image;
    }

    /// <summary>The "address" and size of data directory <paramref name="index"/>; zero for a directory beyond <see cref="DirectoryCount"/>. For the certificate table the address is a file offset.</summary>
    public (uint Address, uint Size) GetDirectory(int index)
    {
        if ((uint)index >= MaxDirectories || index >= DirectoryCount)
        {
            return (0, 0);
        }

        int at = _directoryTableOffset + (8 * index);
        return (U32(Bytes, at), U32(Bytes, at + 4));
    }

    /// <summary>Sets data directory <paramref name="index"/> (<c>IMAGE_DATA_DIRECTORY</c>: <c>VirtualAddress</c>, <c>Size</c>).</summary>
    /// <exception cref="ExportException">The optional header does not declare that many directories.</exception>
    public void SetDirectory(int index, uint address, uint size)
    {
        if ((uint)index >= MaxDirectories || index >= DirectoryCount)
        {
            if (address == 0 && size == 0)
            {
                return;
            }

            throw new ExportException($"The executable's optional header declares {DirectoryCount} data directories and has no room for number {index}.");
        }

        int at = _directoryTableOffset + (8 * index);
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(at), address);
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(at + 4), size);
    }

    /// <summary>
    /// Finds where the <paramref name="length"/> bytes at <paramref name="rva"/> are in the file. They must lie entirely inside one section's data (the part the loader maps), or it is not found.
    /// </summary>
    public bool TryRvaToOffset(uint rva, long length, out int offset)
    {
        foreach (PeSection section in _sections)
        {
            if (rva >= section.VirtualAddress && rva - section.VirtualAddress + length <= section.Extent)
            {
                offset = (int)(section.PointerToRawData + (rva - section.VirtualAddress));
                return true;
            }
        }

        offset = 0;
        return false;
    }

    /// <summary>The index of the section that holds <paramref name="rva"/>, or -1.</summary>
    public int SectionIndexOf(uint rva)
    {
        for (int i = 0; i < _sections.Count; i++)
        {
            if (rva >= _sections[i].VirtualAddress && rva - _sections[i].VirtualAddress < _sections[i].Extent)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Removes what follows the last section. That is either nothing, or the certificate table of a signed file (<c>IMAGE_DIRECTORY_ENTRY_SECURITY</c>, which lies at the end of the file and which
    /// we cannot keep valid), possibly with zero padding around it. Anything else (an installer's payload, a self-extracting archive) belongs to the program and would be lost or misread.
    /// </summary>
    /// <returns>True when a certificate table was removed.</returns>
    /// <exception cref="ExportException">There is data after the sections that is not a certificate table.</exception>
    public bool RemoveOverlay()
    {
        long end = SectionsEnd;
        (uint address, uint size) = GetDirectory(CertificateDirectory);
        bool signed = address != 0 || size != 0;
        if (signed)
        {
            // The certificate table's offset is in the file; it must be after the sections and inside the file, with only zero bytes (alignment) around it.
            long certificateEnd = (long)address + size;
            if (address < end || certificateEnd > Bytes.Length || !IsZero(end, address - end) || !IsZero(certificateEnd, Bytes.Length - certificateEnd))
            {
                throw new ExportException("The executable has data after its sections that is not a signature (the certificate table is not at the end of the file): it is an installer or has an archive attached, and rewriting it would lose that.");
            }

            SetDirectory(CertificateDirectory, 0, 0);
        }
        else if (Bytes.Length != end)
        {
            throw new ExportException($"The executable has {Bytes.Length - end} bytes of data after its last section (an overlay): it is not a plain Apogee runtime, and rewriting it would lose that.");
        }

        Resize((int)end);
        return signed;
    }

    /// <summary>Changes the file's length (new bytes are zero). This replaces <see cref="Bytes"/>.</summary>
    public void Resize(int length)
    {
        if (length != Bytes.Length)
        {
            byte[] resized = new byte[length];
            Buffer.BlockCopy(Bytes, 0, resized, 0, Math.Min(length, Bytes.Length));
            Bytes = resized;
        }
    }

    /// <summary>Sets the section at <paramref name="index"/> (an existing one, or the next one after the last: then the count is raised): its header is written into the section table.</summary>
    /// <exception cref="ExportException">The section table has no room: the zero bytes between it and the first section's data are fewer than a header.</exception>
    public void SetSection(int index, PeSection section)
    {
        if (index == _sections.Count)
        {
            if (index >= MaxSections)
            {
                throw new ExportException($"The executable already has {MaxSections} sections, the most Windows loads.");
            }

            CheckRoomForSectionHeader();
            _sections.Add(section);
            BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(_peOffset + 4 + 2), (ushort)_sections.Count);
        }
        else
        {
            _sections[index] = section;
        }

        // IMAGE_SECTION_HEADER: Name(8) VirtualSize(4) VirtualAddress(4) SizeOfRawData(4) PointerToRawData(4) PointerToRelocations(4) PointerToLinenumbers(4)
        // NumberOfRelocations(2) NumberOfLinenumbers(2) Characteristics(4). The relocation and line number fields stay as they were (zero for a new section).
        Span<byte> header = Bytes.AsSpan(_sectionTableOffset + (SectionHeaderSize * index), SectionHeaderSize);
        Span<byte> name = header[..8];
        name.Clear();
        Encoding.ASCII.GetBytes(section.Name.AsSpan(0, Math.Min(8, section.Name.Length)), name);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], section.VirtualSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], section.VirtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], section.SizeOfRawData);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], section.PointerToRawData);
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..], section.Characteristics);
    }

    /// <summary>Sets <c>SizeOfImage</c>.</summary>
    public void SetSizeOfImage(uint value)
    {
        SizeOfImage = value;
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(_optionalOffset + OffsetSizeOfImage), value);
    }

    /// <summary>Sets <c>SizeOfInitializedData</c>: the combined raw size of the sections with initialized data (the loader does not use it, tools show it).</summary>
    public void SetSizeOfInitializedData(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(_optionalOffset + OffsetSizeOfInitializedData), value);

    /// <summary>Sets <c>Subsystem</c> (<c>IMAGE_SUBSYSTEM_WINDOWS_GUI</c> is 2, <c>IMAGE_SUBSYSTEM_WINDOWS_CUI</c> is 3).</summary>
    public void SetSubsystem(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(_optionalOffset + OffsetSubsystem), value);

    /// <summary>
    /// Writes <c>CheckSum</c>, the checksum <c>CheckSumMappedFile</c> of <c>imagehlp.dll</c> computes: with the field taken as zero, the file is summed as little-endian 16-bit words (an odd last
    /// byte is a word with a zero high byte) in a 32-bit register, and after each word the carry out of the low 16 bits is added back in (one's-complement addition); the 16 bit result is then added
    /// to the file's length. Windows checks it for drivers, boot-start services and system libraries only, but tools compare it.
    /// </summary>
    public void UpdateCheckSum()
    {
        int field = _optionalOffset + OffsetCheckSum;
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(field), 0);
        ReadOnlySpan<byte> file = Bytes;
        uint sum = 0;
        int i = 0;
        for (; i + 1 < file.Length; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16LittleEndian(file[i..]);
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        if (i < file.Length)
        {
            sum += file[i];
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        sum = ((sum >> 16) + sum) & 0xFFFF;
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(field), unchecked(sum + (uint)file.Length));
    }

    /// <summary>Rounds <paramref name="value"/> up to a multiple of <paramref name="alignment"/> (a power of two).</summary>
    public static long AlignUp(long value, long alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static ushort U16(byte[] bytes, long offset)
    {
        if (offset < 0 || offset + 2 > bytes.Length)
        {
            throw new ExportException("The executable ends inside its headers (it is truncated or damaged).");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)offset));
    }

    private static uint U32(byte[] bytes, long offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
        {
            throw new ExportException("The executable ends inside its headers (it is truncated or damaged).");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset));
    }

    private bool IsZero(long offset, long length) => length <= 0 || Bytes.AsSpan((int)offset, (int)length).IndexOfAnyExcept((byte)0) < 0;

    private void ReadOptionalHeader(int sizeOfOptionalHeader)
    {
        // The fixed fields up to the data directories, and the 8 byte directory entries after them, must fit the optional header the COFF header declares, and the file.
        int fixedSize = _directoryTableOffset - _optionalOffset;
        if (sizeOfOptionalHeader < fixedSize || _optionalOffset + (long)sizeOfOptionalHeader > Bytes.Length)
        {
            throw new ExportException("The executable's optional header is cut short (it is truncated or damaged).");
        }

        SectionAlignment = U32(Bytes, _optionalOffset + OffsetSectionAlignment);
        FileAlignment = U32(Bytes, _optionalOffset + OffsetFileAlignment);
        if (!IsPowerOfTwo(SectionAlignment) || !IsPowerOfTwo(FileAlignment) || FileAlignment > SectionAlignment || SectionAlignment > MaxAlignment)
        {
            throw new ExportException($"The executable's alignments are not valid (section alignment {SectionAlignment}, file alignment {FileAlignment}).");
        }

        SizeOfImage = U32(Bytes, _optionalOffset + OffsetSizeOfImage);
        SizeOfHeaders = U32(Bytes, _optionalOffset + OffsetSizeOfHeaders);
        if (SizeOfHeaders > Bytes.Length)
        {
            throw new ExportException("The executable's SizeOfHeaders is past the end of the file.");
        }

        uint declared = U32(Bytes, _directoryCountOffset);
        DirectoryCount = (int)Math.Min(declared, MaxDirectories);
        if (_directoryTableOffset - _optionalOffset + (8L * DirectoryCount) > sizeOfOptionalHeader)
        {
            // The loader ignores directories that do not fit; so do we: they read as empty.
            DirectoryCount = (sizeOfOptionalHeader - fixedSize) / 8;
        }
    }

    private void ReadSections(int count)
    {
        long tableEnd = _sectionTableOffset + ((long)SectionHeaderSize * count);
        if (tableEnd > Bytes.Length || tableEnd > SizeOfHeaders)
        {
            throw new ExportException("The executable's section table does not fit in its headers (it is truncated or damaged).");
        }

        long previousEnd = 0;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> header = Bytes.AsSpan(_sectionTableOffset + (SectionHeaderSize * i), SectionHeaderSize);
            ReadOnlySpan<byte> raw = header[..8];
            int length = raw.IndexOf((byte)0);
            string name = Encoding.ASCII.GetString(length < 0 ? raw : raw[..length]);
            var section = new PeSection(
                name,
                BinaryPrimitives.ReadUInt32LittleEndian(header[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[20..]),
                BinaryPrimitives.ReadUInt32LittleEndian(header[36..]));

            if (section.SizeOfRawData != 0 && (section.RawEnd > Bytes.Length || section.PointerToRawData < SizeOfHeaders))
            {
                throw new ExportException($"Section {name} of the executable is outside the file or inside the headers (it is truncated or damaged).");
            }

            // The loader needs the sections in memory in the order of the table, not overlapping.
            if (section.VirtualAddress < previousEnd)
            {
                throw new ExportException($"Section {name} of the executable overlaps the one before it, or the sections are not in order of their addresses.");
            }

            previousEnd = section.VirtualEnd;
            _sections.Add(section);
        }
    }

    private void CheckRoomForSectionHeader()
    {
        // A new header goes right after the last. The space up to the first section's data is padding to FileAlignment, which is zero when nothing uses it (a linker may keep a bound import
        // table or Rich header there: not in the section table's slack, but we only use the slack if it is all zero).
        long next = NextSectionHeaderOffset;
        long limit = SizeOfHeaders;
        foreach (PeSection section in _sections)
        {
            if (section.SizeOfRawData != 0)
            {
                limit = Math.Min(limit, section.PointerToRawData);
            }
        }

        if (next + SectionHeaderSize > limit)
        {
            throw new ExportException("The executable's headers have no room for another section header: it needs 40 free bytes between the section table and the first section's data. The runtime was linked with no spare header space, so the resources cannot be added.");
        }

        if (!IsZero(next, SectionHeaderSize))
        {
            throw new ExportException("The space after the executable's section table, where a new section header would go, is in use.");
        }
    }

    private static bool IsPowerOfTwo(uint value) => value != 0 && (value & (value - 1)) == 0;
}
