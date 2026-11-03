using System;
using System.IO;
using System.IO.Compression;

namespace Apogee.Content;

/// <summary>A minimal PNG writer for 8-bit RGBA images: screenshots and test files. Reading goes through stb_image.</summary>
public static class Png
{
    private static readonly uint[] s_crcTable = BuildCrcTable();

    /// <summary>Encodes tightly packed RGBA8 pixels, top row first.</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException($"{width}x{height} RGBA needs {width * height * 4} bytes, got {rgba.Length}.", nameof(rgba));
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        WriteBigEndian(header[..4], (uint)width);
        WriteBigEndian(header[4..8], (uint)height);
        header[8] = 8;      // bit depth
        header[9] = 6;      // color type: RGBA
        WriteChunk(output, "IHDR"u8, header);

        // Each row is a filter byte (0: none) and the pixels, deflated as one zlib stream.
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(rgba.Slice(y * width * 4, width * 4));
            }
        }

        WriteChunk(output, "IDAT"u8, raw.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        WriteBigEndian(number, (uint)data.Length);
        output.Write(number);

        uint crc = 0xFFFFFFFF;
        crc = Update(crc, type);
        crc = Update(crc, data);
        output.Write(type);
        output.Write(data);
        WriteBigEndian(number, crc ^ 0xFFFFFFFF);
        output.Write(number);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static void WriteBigEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
