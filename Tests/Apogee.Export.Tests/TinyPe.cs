using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Apogee.Export.Tests;

/// <summary>
/// Makes a small Windows executable (headers, sections, optionally a resource section, a certificate or an overlay) that is laid out like a linker's output, so that the PE code can be tested on any
/// host and on files that are damaged in a chosen way. Written separately from the code it tests: plain offsets from the PE format specification.
/// </summary>
internal sealed class TinyPe
{
    public const uint TextFlags = 0x6000_0020;
    public const uint DataFlags = 0xC000_0040;
    public const uint ResourceFlags = 0x4000_0040;
    public const uint RelocFlags = 0x4200_0040;

    private readonly List<(string Name, Func<uint, byte[]> Data, uint Flags)> _sections = [];
    private int _resourceIndex = -1;

    /// <summary>PE32+ (64-bit) when true, PE32 otherwise.</summary>
    public bool Pe32Plus { get; init; } = true;

    /// <summary>3 is a console program, 2 a windowed one.</summary>
    public ushort Subsystem { get; init; } = 3;

    /// <summary>Where the PE signature is (<c>e_lfanew</c>); with the default, a section table has room for a few headers up to 0x200.</summary>
    public int PeOffset { get; init; } = 0x40;

    /// <summary>At least this many bytes of headers (a linker leaves much room; without it the section table ends close to the first section).</summary>
    public int MinHeaderSize { get; init; }

    public uint FileAlignment { get; init; } = 0x200;

    public uint SectionAlignment { get; init; } = 0x1000;

    /// <summary>A signature: bytes after the sections, which the certificate directory points to.</summary>
    public byte[]? Certificate { get; init; }

    /// <summary>Bytes after the sections that are not a certificate.</summary>
    public byte[]? Overlay { get; init; }

    /// <summary>A stored checksum, which a rewrite must replace.</summary>
    public uint CheckSum { get; init; }

    /// <summary>Adds a section with the bytes <paramref name="data"/>.</summary>
    public TinyPe Section(string name, byte[] data, uint flags) => Section(name, _ => data, flags);

    /// <summary>Adds a section whose bytes depend on its address.</summary>
    public TinyPe Section(string name, Func<uint, byte[]> data, uint flags)
    {
        _sections.Add((name, data, flags));
        return this;
    }

    /// <summary>Adds a <c>.rsrc</c> section that holds the resource directory, made by <see cref="TestResources.Build"/>.</summary>
    public TinyPe Resources(IEnumerable<(object Type, object Name, int Language, byte[] Data)> resources)
    {
        List<(object, object, int, byte[])> list = [.. resources.Select(r => (r.Type, r.Name, r.Language, r.Data))];
        _resourceIndex = _sections.Count;
        return Section(".rsrc", rva => TestResources.Build(list, rva), ResourceFlags);
    }

    /// <summary>Adds a <c>.rsrc</c> section with the bytes <paramref name="data"/> (made for the section's address) as they are, which the resource directory points to.</summary>
    public TinyPe RawResources(Func<uint, byte[]> data)
    {
        _resourceIndex = _sections.Count;
        return Section(".rsrc", data, ResourceFlags);
    }

    public byte[] Build()
    {
        int optionalSize = Pe32Plus ? 240 : 224;
        int tableOffset = PeOffset + 24 + optionalSize;
        uint headers = (uint)AlignUp(Math.Max(MinHeaderSize, tableOffset + (40 * _sections.Count)), FileAlignment);

        // Place the sections one after the other.
        var placed = new List<(string Name, byte[] Data, uint Flags, uint Va, uint Pointer, uint RawSize)>();
        uint va = (uint)AlignUp(headers, SectionAlignment);
        uint pointer = headers;
        foreach ((string name, Func<uint, byte[]> data, uint flags) in _sections)
        {
            byte[] bytes = data(va);
            uint rawSize = (uint)AlignUp(bytes.Length, FileAlignment);
            placed.Add((name, bytes, flags, va, pointer, rawSize));
            va = (uint)AlignUp(va + Math.Max(1, bytes.Length), SectionAlignment);
            pointer += rawSize;
        }

        int length = (int)pointer;
        int certificateAt = (int)AlignUp(length, 8);
        if (Certificate is not null)
        {
            length = certificateAt + Certificate.Length;
        }

        int overlayAt = length;
        if (Overlay is not null)
        {
            length += Overlay.Length;
        }

        byte[] file = new byte[length];
        file[0] = (byte)'M';
        file[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x3C), PeOffset);
        "PE\0\0"u8.CopyTo(file.AsSpan(PeOffset));

        // COFF header: Machine, NumberOfSections, ... SizeOfOptionalHeader at +16, Characteristics at +18.
        Span<byte> coff = file.AsSpan(PeOffset + 4, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(coff, 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[2..], (ushort)placed.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[16..], (ushort)optionalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[18..], 0x0022);

        Span<byte> optional = file.AsSpan(PeOffset + 24, optionalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(optional, (ushort)(Pe32Plus ? 0x20B : 0x10B));
        uint initialized = (uint)placed.Where(s => s.Flags != TextFlags).Sum(s => (long)s.RawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[4..], placed.Count > 0 ? placed[0].RawSize : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[8..], initialized);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[16..], placed.Count > 0 ? placed[0].Va : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[20..], placed.Count > 0 ? placed[0].Va : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[32..], SectionAlignment);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[36..], FileAlignment);
        BinaryPrimitives.WriteUInt16LittleEndian(optional[40..], 6); // MajorOperatingSystemVersion
        BinaryPrimitives.WriteUInt16LittleEndian(optional[48..], 6); // MajorSubsystemVersion
        BinaryPrimitives.WriteUInt32LittleEndian(optional[56..], (uint)AlignUp(va, SectionAlignment)); // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(optional[60..], headers);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[64..], CheckSum);
        BinaryPrimitives.WriteUInt16LittleEndian(optional[68..], Subsystem);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[(Pe32Plus ? 108 : 92)..], 16); // NumberOfRvaAndSizes

        Span<byte> directories = optional[(Pe32Plus ? 112 : 96)..];
        if (_resourceIndex >= 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(directories[16..], placed[_resourceIndex].Va);
            BinaryPrimitives.WriteUInt32LittleEndian(directories[20..], (uint)placed[_resourceIndex].Data.Length);
        }

        if (Certificate is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(directories[32..], (uint)certificateAt);
            BinaryPrimitives.WriteUInt32LittleEndian(directories[36..], (uint)Certificate.Length);
            Certificate.CopyTo(file, certificateAt);
        }

        Overlay?.CopyTo(file, overlayAt);

        for (int i = 0; i < placed.Count; i++)
        {
            (string name, byte[] data, uint flags, uint sectionVa, uint at, uint rawSize) = placed[i];
            Span<byte> header = file.AsSpan(tableOffset + (40 * i), 40);
            Encoding.ASCII.GetBytes(name, header[..8]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], sectionVa);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], rawSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], at);
            BinaryPrimitives.WriteUInt32LittleEndian(header[36..], flags);
            data.CopyTo(file, (int)at);
        }

        return file;
    }

    /// <summary>A typical runtime: code with some data (and so a slot for the pack key), a manifest in the resources, and <c>.reloc</c> last, like a linker's output.</summary>
    public static TinyPe Typical(byte[] textData, ushort subsystem = 3, byte[]? certificate = null) => new TinyPe { Subsystem = subsystem, Certificate = certificate, MinHeaderSize = 0x400 }
        .Section(".text", textData, TextFlags)
        .Section(".rdata", new byte[300], DataFlags)
        .Resources([(24, 1, 0x0409, "<assembly>a manifest</assembly>"u8.ToArray())])
        .Section(".reloc", new byte[64], RelocFlags);

    public static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;
}
