using Apogee.Core.Graphics;
using System;
using System.Collections.Generic;
using Apogee.Native.Ktx;
using KtxApi = Apogee.Native.Ktx.Ktx;

namespace Apogee.Content;

/// <summary>Loads KTX 2 files. Basis Universal data (ETC1S and UASTC) is transcoded to the best format the GPU samples: BC7, ASTC 4x4, or RGBA8.</summary>
public static unsafe class Ktx2Loader
{
    /// <summary>The file magic of a KTX 2 file (<c>«KTX 20»\r\n\x1A\n</c>).</summary>
    public static ReadOnlySpan<byte> Magic => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsKtx2(ReadOnlySpan<byte> file) => file.StartsWith(Magic);

    /// <summary>Whether the file says its colors are sRGB: the transfer function in its data format descriptor, which holds for RGBA8 and for Basis Universal data alike.</summary>
    /// <exception cref="ContentException">It is not a readable KTX 2 file.</exception>
    public static bool IsSrgb(string name, ReadOnlySpan<byte> file)
    {
        Texture2* texture;
        Result result;
        fixed (byte* data = file)
        {
            result = KtxApi.Texture2CreateFromMemory(data, (nuint)file.Length, TextureCreateFlagBits.NoFlags, &texture);
        }

        if (result != Result.Success)
        {
            throw new ContentException($"Texture '{name}' is not a readable KTX 2 file: {result}");
        }

        try
        {
            return KtxApi.Texture2GetTransferFunction(texture) == KhrDfTransfer.Srgb;
        }
        finally
        {
            KtxApi.Texture2Destroy(texture);
        }
    }

    /// <param name="srgb">Whether the colors are sRGB (base color, emissive) or linear (normals, roughness…); it picks the sampled format, whatever the file says.</param>
    public static TextureData Load(string name, ReadOnlySpan<byte> file, bool srgb, TextureCaps caps, SamplerData sampler)
    {
        Texture2* texture;
        Result result;
        fixed (byte* data = file)
        {
            result = KtxApi.Texture2CreateFromMemory(data, (nuint)file.Length, TextureCreateFlagBits.LoadImageData, &texture);
        }

        if (result != Result.Success)
        {
            throw new ContentException($"Texture '{name}' is not a readable KTX 2 file: {result}");
        }

        try
        {
            if (texture->NumLayers > 1 || texture->IsCubemap || texture->BaseDepth > 1)
            {
                throw new ContentException($"Texture '{name}' is an array, a cube map or a 3D texture; only 2D textures are supported.");
            }

            PixelFormat format;
            if (KtxApi.Texture2NeedsTranscoding(texture))
            {
                (TranscodeFmt target, format) = caps switch
                {
                    { Bc: true } => (TranscodeFmt.TtfBc7Rgba, srgb ? PixelFormat.Bc7SrgbBlock : PixelFormat.Bc7UnormBlock),
                    { Astc: true } => (TranscodeFmt.TtfAstc4x4Rgba, srgb ? PixelFormat.Astc4X4SrgbBlock : PixelFormat.Astc4X4UnormBlock),
                    _ => (TranscodeFmt.TtfRgba32, srgb ? PixelFormat.R8G8B8A8Srgb : PixelFormat.R8G8B8A8Unorm),
                };
                result = KtxApi.Texture2TranscodeBasis(texture, target, 0);
                if (result != Result.Success)
                {
                    throw new ContentException($"Texture '{name}' could not be transcoded to {format}: {result}");
                }
            }
            else
            {
                format = SampledFormat(name, (PixelFormat)texture->VkFormat, srgb, caps);
            }

            int width = checked((int)texture->BaseWidth);
            int height = checked((int)texture->BaseHeight);
            var mips = new List<byte[]>((int)texture->NumLevels);
            for (uint level = 0; level < texture->NumLevels; level++)
            {
                nuint offset;
                result = KtxApi.Texture2GetImageOffset(texture, level, 0, 0, &offset);
                if (result != Result.Success)
                {
                    throw new ContentException($"Texture '{name}' has no level {level}: {result}");
                }

                int size = LevelSize(format, Math.Max(1, width >> (int)level), Math.Max(1, height >> (int)level));
                mips.Add(new ReadOnlySpan<byte>(texture->PData + offset, size).ToArray());
            }

            return new TextureData(name, format, width, height, mips, srgb, sampler);
        }
        finally
        {
            KtxApi.Texture2Destroy(texture);
        }
    }

    /// <summary>A file that already holds sampled data (uncompressed RGBA8, or BC7/ASTC blocks): use it as it is, if the GPU can.</summary>
    private static PixelFormat SampledFormat(string name, PixelFormat stored, bool srgb, TextureCaps caps)
    {
        switch (stored)
        {
            case PixelFormat.R8G8B8A8Unorm or PixelFormat.R8G8B8A8Srgb:
                return srgb ? PixelFormat.R8G8B8A8Srgb : PixelFormat.R8G8B8A8Unorm;
            case PixelFormat.Bc7UnormBlock or PixelFormat.Bc7SrgbBlock when caps.Bc:
                return srgb ? PixelFormat.Bc7SrgbBlock : PixelFormat.Bc7UnormBlock;
            case PixelFormat.Astc4X4UnormBlock or PixelFormat.Astc4X4SrgbBlock when caps.Astc:
                return srgb ? PixelFormat.Astc4X4SrgbBlock : PixelFormat.Astc4X4UnormBlock;
            default:
                throw new ContentException($"Texture '{name}' is stored as {stored}, which this GPU cannot sample or the engine does not load.");
        }
    }

    private static int LevelSize(PixelFormat format, int width, int height) => format switch
    {
        PixelFormat.R8G8B8A8Unorm or PixelFormat.R8G8B8A8Srgb => width * height * 4,
        _ => ((width + 3) / 4) * ((height + 3) / 4) * 16,
    };
}
