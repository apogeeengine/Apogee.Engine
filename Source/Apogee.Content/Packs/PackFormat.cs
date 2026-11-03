using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Apogee.Native.Zstd;
using ZstdApi = Apogee.Native.Zstd.Zstd;

namespace Apogee.Content.Packs;

[Flags]
internal enum PackFlags : uint
{
    None = 0,

    /// <summary>The index is sealed with AES-GCM (the pack has a key).</summary>
    IndexEncrypted = 1,

    /// <summary>Another part (<c>name_001.pack</c>…) follows this one.</summary>
    PartsFollow = 2,
}

[Flags]
internal enum EntryFlags : byte
{
    None = 0,

    /// <summary>Compressed with zstd (before it was sealed).</summary>
    Compressed = 1,

    /// <summary>Sealed with AES-GCM.</summary>
    Encrypted = 2,

    /// <summary>The path is taken away from the packs below this one; there is no data.</summary>
    Removed = 4,
}

/// <summary>The parts of a pack that is a file on disk, in one place: the layout, the magic, the key, and what is done to the bytes.</summary>
/// <remarks>
/// A part, little-endian: an 80-byte header (magic, version, flags, index offset, stored and raw length of the index, file count, a random 32-byte salt), the entries' data (each
/// aligned to 16 bytes), and the index at <c>IndexOffset</c>. An entry's bytes are the file, zstd-compressed when that saves at least 10% (and the file has 64 bytes or more), then sealed
/// when there is a key. The index lists, per path: flags, offset, stored length, raw length and the first 16 bytes of the SHA-256 of the raw file; it is zstd-compressed and sealed too.
/// A key never encrypts directly: each part's key is HKDF-SHA256 of the pack key and the part's salt. A sealed blob is <c>nonce (12) | tag (16) | ciphertext</c> with the entry's path
/// as associated data, so an entry cannot be moved to another path. The magic is <c>APGK</c> without a key, and with one four bytes of a hash of the key (an encrypted pack has no
/// recognizable signature).
/// </remarks>
internal static unsafe class PackFormat
{
    public const int HeaderSize = 80;
    public const uint Version = 1;
    public const uint PlainMagic = 0x4B475041; // "APGK"
    public const int Alignment = 16;
    public const int SealOverhead = 12 + 16;
    public const int MinimumCompressedSize = 64;

    private static readonly byte[] s_indexContext = "apogee pack index"u8.ToArray();

    public static uint MagicFor(PackKey? key)
    {
        if (key is null)
        {
            return PlainMagic;
        }

        byte[] input = [.. "Apogee pack magic"u8, .. key.Bytes];
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(input));
        return magic is PlainMagic or 0 ? magic ^ 0x80808081 : magic;
    }

    public static byte[] DeriveKey(PackKey key, ReadOnlySpan<byte> salt) => HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Bytes.ToArray(), 32, salt.ToArray(), "Apogee pack v1"u8.ToArray());

    public static byte[] Hash(ReadOnlySpan<byte> data) => SHA256.HashData(data)[..16];

    public static byte[] PathContext(string path) => Encoding.UTF8.GetBytes(path);

    public static byte[] IndexContext() => s_indexContext;

    /// <summary>Seals <paramref name="plaintext"/>: a new random nonce each time, <paramref name="context"/> authenticated but not stored.</summary>
    public static byte[] Seal(byte[] key, ReadOnlySpan<byte> plaintext, byte[] context)
    {
        byte[] result = new byte[SealOverhead + plaintext.Length];
        Span<byte> nonce = result.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, result.AsSpan(SealOverhead), result.AsSpan(12, 16), context);
        return result;
    }

    /// <exception cref="CryptographicException">The key is wrong, or the bytes or the context were changed.</exception>
    public static byte[] Open(byte[] key, ReadOnlySpan<byte> sealedBytes, byte[] context)
    {
        if (sealedBytes.Length < SealOverhead)
        {
            throw new CryptographicException("The sealed data is too short.");
        }

        byte[] plaintext = new byte[sealedBytes.Length - SealOverhead];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(sealedBytes[..12], sealedBytes[SealOverhead..], sealedBytes.Slice(12, 16), plaintext, context);
        return plaintext;
    }

    /// <summary>Compresses with zstd at <paramref name="level"/>, with a checksum in the frame (so damage is found on reading) and on as many threads as the machine has.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, int level)
    {
        CCtx* context = ZstdApi.CreateCCtx();
        try
        {
            Check(ZstdApi.CCtxSetParameter(context, CParameter.CompressionLevel, level));
            Check(ZstdApi.CCtxSetParameter(context, CParameter.ChecksumFlag, 1));
            Check(ZstdApi.CCtxSetParameter(context, CParameter.NbWorkers, data.Length > 4 * 1024 * 1024 ? Math.Min(Environment.ProcessorCount, 8) : 0));
            byte[] output = new byte[(int)ZstdApi.CompressBound((nuint)data.Length)];
            fixed (byte* source = data)
            fixed (byte* target = output)
            {
                nuint size = ZstdApi.Compress2(context, target, (nuint)output.Length, source, (nuint)data.Length);
                Check(size);
                return output.AsSpan(0, (int)size).ToArray();
            }
        }
        finally
        {
            ZstdApi.FreeCCtx(context);
        }
    }

    /// <summary>Decompresses a frame that must hold exactly <paramref name="rawLength"/> bytes.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> compressed, long rawLength)
    {
        fixed (byte* source = compressed)
        {
            ulong declared = ZstdApi.GetFrameContentSize(source, (nuint)compressed.Length);
            if (declared != (ulong)rawLength || rawLength > int.MaxValue)
            {
                throw new CryptographicException("The compressed data does not hold what the index says.");
            }

            byte[] output = new byte[rawLength];
            fixed (byte* target = output)
            {
                nuint size = ZstdApi.Decompress(target, (nuint)output.Length, source, (nuint)compressed.Length);
                Check(size);
                return size == (nuint)rawLength ? output : throw new CryptographicException("The compressed data is shorter than the index says.");
            }
        }
    }

    private static void Check(nuint result)
    {
        if (ZstdApi.IsError(result) != 0)
        {
            throw new CryptographicException("zstd: " + (Apogee.Interop.Utf8.FromPointer(ZstdApi.GetErrorName(result)) ?? "error"));
        }
    }
}
