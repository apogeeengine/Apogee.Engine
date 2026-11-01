using System;

namespace Apogee.Core.Graphics;

/// <summary>Sizes and kinds of the formats the engine uses.</summary>
public static class FormatInfo
{
    /// <summary>Bytes per texel for an uncompressed format, or per block for a block-compressed one, and the block's size in texels.</summary>
    public static (int BytesPerBlock, int BlockWidth, int BlockHeight) Block(PixelFormat format) => format switch
    {
        PixelFormat.R8Unorm or PixelFormat.R8Uint or PixelFormat.R8Snorm => (1, 1, 1),
        PixelFormat.R8G8Unorm or PixelFormat.R16Sfloat or PixelFormat.R16Uint or PixelFormat.D16Unorm => (2, 1, 1),
        PixelFormat.R8G8B8A8Unorm or PixelFormat.R8G8B8A8Srgb or PixelFormat.B8G8R8A8Unorm or PixelFormat.B8G8R8A8Srgb or PixelFormat.A2B10G10R10UnormPack32
            or PixelFormat.R32Sfloat or PixelFormat.R32Uint or PixelFormat.D32Sfloat or PixelFormat.R16G16Sfloat or PixelFormat.B10G11R11UfloatPack32 => (4, 1, 1),
        PixelFormat.R16G16B16A16Sfloat or PixelFormat.R32G32Sfloat or PixelFormat.R16G16B16A16Unorm => (8, 1, 1),
        PixelFormat.R32G32B32A32Sfloat => (16, 1, 1),
        PixelFormat.Bc1RgbUnormBlock or PixelFormat.Bc1RgbSrgbBlock or PixelFormat.Bc1RgbaUnormBlock or PixelFormat.Bc1RgbaSrgbBlock or PixelFormat.Bc4UnormBlock => (8, 4, 4),
        PixelFormat.Bc3UnormBlock or PixelFormat.Bc3SrgbBlock or PixelFormat.Bc5UnormBlock or PixelFormat.Bc7UnormBlock or PixelFormat.Bc7SrgbBlock => (16, 4, 4),
        PixelFormat.Astc4X4UnormBlock or PixelFormat.Astc4X4SrgbBlock => (16, 4, 4),
        _ => throw new NotSupportedException($"The size of {format} is not known."),
    };

    /// <summary>Bytes in one mip level of an image of this format.</summary>
    public static ulong LevelSize(PixelFormat format, uint width, uint height, uint depth = 1)
    {
        (int bytes, int blockWidth, int blockHeight) = Block(format);
        ulong blocksX = (width + (uint)blockWidth - 1) / (uint)blockWidth;
        ulong blocksY = (height + (uint)blockHeight - 1) / (uint)blockHeight;
        return blocksX * blocksY * depth * (ulong)bytes;
    }

    public static bool IsDepth(PixelFormat format) =>
        format is PixelFormat.D16Unorm or PixelFormat.D32Sfloat or PixelFormat.D24UnormS8Uint or PixelFormat.D32SfloatS8Uint or PixelFormat.X8D24UnormPack32 or PixelFormat.D16UnormS8Uint;

    public static bool HasStencil(PixelFormat format) =>
        format is PixelFormat.D24UnormS8Uint or PixelFormat.D32SfloatS8Uint or PixelFormat.D16UnormS8Uint;
}
