using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Weft.Content.Formats;

/// <summary>
/// CRC-32C (Castagnoli, the polynomial <c>0x1EDC6F41</c>, reflected: <c>0x82F63B78</c>) of a span of bytes. It uses the processor's instruction where there is one (SSE 4.2, ARMv8) and a table otherwise; the
/// two give the same value, which is what the block table of a <see cref="BinaryAsset"/> stores. The check value of <c>"123456789"</c> is <c>0xE3069283</c>.
/// </summary>
public static class Crc32C
{
    private const uint Polynomial = 0x82F63B78;

    private static readonly uint[] s_table = BuildTable();

    /// <summary>The CRC-32C of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(uint.MaxValue, data);

    /// <summary>The CRC-32C of <paramref name="first"/> followed by <paramref name="second"/>, without joining them.</summary>
    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) => ~Update(Update(uint.MaxValue, first), second);

    /// <summary>Takes <paramref name="data"/> into a running CRC register (the complement of a CRC; start with <see cref="uint.MaxValue"/>).</summary>
    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        if (Sse42.X64.IsSupported)
        {
            ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(data);
            ulong wide = crc;
            foreach (ulong word in words)
            {
                wide = Sse42.X64.Crc32(wide, word);
            }

            crc = (uint)wide;
            foreach (byte value in data[(words.Length * 8)..])
            {
                crc = Sse42.Crc32(crc, value);
            }

            return crc;
        }

        if (Crc32.Arm64.IsSupported)
        {
            ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(data);
            foreach (ulong word in words)
            {
                crc = Crc32.Arm64.ComputeCrc32C(crc, word);
            }

            foreach (byte value in data[(words.Length * 8)..])
            {
                crc = Crc32.ComputeCrc32C(crc, value);
            }

            return crc;
        }

        return UpdateSoftware(crc, data);
    }

    /// <summary>The same value as <see cref="Compute(ReadOnlySpan{byte})"/>, by the table alone (what the tests compare the instruction with).</summary>
    internal static uint ComputeSoftware(ReadOnlySpan<byte> data) => ~UpdateSoftware(uint.MaxValue, data);

    private static uint UpdateSoftware(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            crc = s_table[(byte)(crc ^ value)] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? Polynomial ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
