using Apogee.Core.Graphics;
using System;
using System.Collections.Generic;
using Apogee.Native.Ktx;
using KtxApi = Apogee.Native.Ktx.Ktx;

namespace Apogee.Content.Import;

/// <summary>How an imported texture is stored: GPU-compressed (Basis Universal UASTC inside KTX 2, transcoded when it is loaded) or as 8-bit RGBA.</summary>
public enum TextureCompression
{
    /// <summary>Basis Universal UASTC: one file for every platform, transcoded at load to BC7, ASTC 4x4 or RGBA8, whichever the GPU samples.</summary>
    Uastc = 0,

    /// <summary>8-bit RGBA, with the levels compressed by zstd in the file (<see cref="Ktx2Writer"/>). Every GPU samples it; it takes four times the memory.</summary>
    None = 1,
}

/// <summary>
/// Encodes an RGBA8 texture and its mip chain as UASTC in a KTX 2 file, with libktx's Basis Universal encoder: the blocks go through RDO (rate-distortion optimization, which makes them compress better) and the levels
/// are supercompressed with zstd. A color or data texture and a normal map get different settings: normal maps skip RDO and take a slower, more accurate encoder level, because errors in a normal are visible as lighting
/// noise. Normal maps stay RGB (libktx's own "normal map" mode would keep two channels as RRRG, which the shaders do not read). The encoder is slow, which is what the import cache is for; it is deterministic.
/// </summary>
public static unsafe class BasisEncoder
{
    /// <summary>What the encoder does, for the import key: change it and the textures are imported again.</summary>
    public const string Settings = "uastc color level 2 rdo 1.0 / normal level 3 no rdo / zstd 18 / ktx 4.4.2";

    private const int ZstdLevel = 18;

    /// <summary>The smallest side a texture is compressed at: UASTC works in 4x4 blocks, and a smaller texture would only grow.</summary>
    public const int MinimumSize = 4;

    /// <exception cref="ContentException">The texture is not RGBA8 or its levels are not the sizes of a mip chain, or the encoder failed.</exception>
    public static byte[] Encode(TextureData texture, bool normalMap)
    {
        if (texture.Format != PixelFormat.R8G8B8A8Srgb && texture.Format != PixelFormat.R8G8B8A8Unorm)
        {
            throw new ContentException($"Texture '{texture.Name}' is {texture.Format}; only RGBA8 is encoded.");
        }

        IReadOnlyList<byte[]> mips = texture.Mips;
        for (int level = 0; level < mips.Count; level++)
        {
            int expected = Math.Max(1, texture.Width >> level) * Math.Max(1, texture.Height >> level) * 4;
            if (mips[level].Length != expected)
            {
                throw new ContentException($"Texture '{texture.Name}': level {level} has {mips[level].Length} bytes, not {expected}.");
            }
        }

        var info = new TextureCreateInfo
        {
            VkFormat = (uint)texture.Format,
            BaseWidth = (uint)texture.Width,
            BaseHeight = (uint)texture.Height,
            BaseDepth = 1,
            NumDimensions = 2,
            NumLevels = (uint)mips.Count,
            NumLayers = 1,
            NumFaces = 1,
        };
        Texture2* ktx;
        Check(texture, "create the texture", KtxApi.Texture2Create(&info, TextureCreateStorageEnum.AllocStorage, &ktx));
        try
        {
            for (int level = 0; level < mips.Count; level++)
            {
                fixed (byte* pixels = mips[level])
                {
                    Check(texture, $"set level {level}", ktx->Vtbl->SetImageFromMemory((Texture*)ktx, (uint)level, 0, 0, pixels, (nuint)mips[level].Length));
                }
            }

            var parameters = new BasisParams
            {
                StructSize = (uint)sizeof(BasisParams),
                Uastc = true,
                ThreadCount = (uint)Math.Max(1, Environment.ProcessorCount),
                UastcFlags = normalMap ? (uint)UastcLevel.Slower : (uint)UastcLevel.Default,
                UastcRDO = !normalMap,
                UastcRDOQualityScalar = 1f,
                UastcRDONoMultithreading = true, // the same bytes whatever the number of cores
            };
            Check(texture, "encode it", KtxApi.Texture2CompressBasisEx(ktx, &parameters));
            Check(texture, "compress it with zstd", KtxApi.Texture2DeflateZstd(ktx, ZstdLevel));

            byte* bytes;
            nuint size;
            Check(texture, "write it", KtxApi.Texture2WriteToMemory(ktx, &bytes, &size));
            try
            {
                return new ReadOnlySpan<byte>(bytes, checked((int)size)).ToArray();
            }
            finally
            {
                Free(bytes);
            }
        }
        finally
        {
            KtxApi.Texture2Destroy(ktx);
        }
    }

    // ktx_pack_uastc_flag_bits_e: the speed against quality level in the low bits.
    private enum UastcLevel : uint
    {
        Default = 2,
        Slower = 3,
    }

    private static void Check(TextureData texture, string what, Result result)
    {
        if (result != Result.Success)
        {
            throw new ContentException($"Texture '{texture.Name}': could not {what}: {result}.");
        }
    }

    /// <summary>libktx allocated the file with malloc.</summary>
    private static void Free(byte* bytes) => System.Runtime.InteropServices.NativeMemory.Free(bytes);
}
