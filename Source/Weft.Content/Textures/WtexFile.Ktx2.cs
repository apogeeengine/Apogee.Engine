using System;
using System.Buffers.Binary;
using Weft.Content.Import;
using Weft.Native.Ktx;
using KtxApi = Weft.Native.Ktx.Ktx;

namespace Weft.Content.Textures;

/// <summary>What a KTX 2 file does not say, for <see cref="WtexFile.ReadKtx2"/>: the fields of a <c>.wtex</c> that are the engine's.</summary>
public sealed record WtexSettings
{
    /// <summary>Flags to add to the ones the file implies (<see cref="WtexTraits.NormalMapRG"/>, <see cref="WtexTraits.AlphaCoverage"/>, <see cref="WtexTraits.Premultiplied"/>, <see cref="WtexTraits.Portable"/>).</summary>
    public WtexTraits Flags { get; init; }

    /// <summary>The texture group.</summary>
    public WtexGroup Group { get; init; }

    /// <summary>The level of detail bias.</summary>
    public sbyte LodBias { get; init; }

    /// <summary>The alpha coverage reference.</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>How the texture is sampled.</summary>
    public SamplerData Sampler { get; init; } = SamplerData.Default;
}

/// <summary>A texture as <see cref="WtexFile.Write"/> takes it: the description and the bytes of every level, the largest first.</summary>
/// <param name="Info">The description.</param>
/// <param name="Levels">The levels.</param>
public sealed record WtexTexture(WtexInfo Info, byte[][] Levels);

public sealed partial class WtexFile
{
    /// <summary>The size of the header of a KTX 2 file, the level index follows it.</summary>
    private const int Ktx2HeaderSize = 80;

    /// <summary>The texture as a KTX 2 file (see <see cref="Ktx2Writer.Write(WtexInfo, System.Collections.Generic.IReadOnlyList{ReadOnlyMemory{byte}})"/>): the same level bytes, no supercompression.</summary>
    /// <exception cref="ContentException">A block of the file does not decode or its checksum fails.</exception>
    public byte[] ToKtx2() => Ktx2Writer.Write(Info, ReadLevels());

    /// <summary>
    /// A KTX 2 file as a texture: UASTC (with or without zstd), RGBA8, BC4, BC5, BC6H, BC7 or half-float RGBA, 2D, 3D, cube map or array. The levels are the bytes the KTX 2 file holds (libktx inflates the zstd
    /// supercompression of a file that has it); they are not decoded or encoded. ETC1S (BasisLZ) and zlib supercompression and every other format are refused.
    /// </summary>
    /// <param name="name">What the file is called in messages.</param>
    /// <param name="ktx2">The whole file.</param>
    /// <param name="settings">The engine's fields, which the file does not have; null for the defaults.</param>
    /// <exception cref="ContentException">The file is not a KTX 2 file this engine converts: it is damaged, or hostile, or of a kind that is not supported.</exception>
    public static unsafe WtexTexture ReadKtx2(string name, ReadOnlySpan<byte> ktx2, WtexSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        settings ??= new WtexSettings();
        WtexInfo info = Ktx2Info(name, ktx2, settings, out bool uastc);

        Texture2* texture;
        Result result;
        fixed (byte* data = ktx2)
        {
            result = KtxApi.Texture2CreateFromMemory(data, (nuint)ktx2.Length, TextureCreateFlagBits.LoadImageData, &texture);
        }

        if (result != Result.Success)
        {
            throw new ContentException($"'{name}' is not a readable KTX 2 file: {result}.");
        }

        try
        {
            if (texture->BaseWidth != (uint)info.Width || texture->BaseHeight != (uint)info.Height || texture->NumLevels != (uint)info.MipCount
                || texture->NumFaces != (uint)info.Faces || texture->VkFormat != (uint)Ktx2VkFormat(info.Payload, (info.Flags & WtexTraits.Srgb) != 0, uastc)
                || Math.Max(1, texture->BaseDepth) != (uint)info.Depth || Math.Max(1, texture->NumLayers) != (uint)info.Layers)
            {
                throw new ContentException($"'{name}' is not what its header says to libktx.");
            }

            bool needsTranscoding = KtxApi.Texture2NeedsTranscoding(texture);
            if (needsTranscoding != uastc || (uastc && KtxApi.Texture2GetColorModel(texture) != KhrDfModel.Uastc))
            {
                throw new ContentException($"'{name}' is not a UASTC texture. Only UASTC is converted of the Basis Universal formats; ETC1S is not.");
            }

            if (uastc && KtxApi.Texture2GetTransferFunction(texture) == KhrDfTransfer.Srgb)
            {
                info = info with { Flags = info.Flags | WtexTraits.Srgb }; // a UASTC texture says its colour space in its descriptor only
            }

            if (KtxApi.Texture2GetPremultipliedAlpha(texture))
            {
                info = info with { Flags = info.Flags | WtexTraits.Premultiplied };
            }

            if (info.Check() is { } problem)
            {
                throw new ContentException($"'{name}' is a texture that " + problem + ".");
            }

            var levels = new byte[info.MipCount][];
            for (int level = 0; level < levels.Length; level++)
            {
                nuint offset;
                result = KtxApi.Texture2GetImageOffset(texture, (uint)level, 0, 0, &offset);
                if (result != Result.Success)
                {
                    throw new ContentException($"'{name}' has no level {level}: {result}.");
                }

                long size = info.LevelSize(level);
                if ((nuint)size != texture->Vtbl->GetLevelSize((Texture*)texture, (uint)level) || offset > texture->DataSize || (nuint)size > texture->DataSize - offset)
                {
                    throw new ContentException($"'{name}' has level {level} that is not {size} bytes in the file.");
                }

                levels[level] = new ReadOnlySpan<byte>(texture->PData + offset, (int)size).ToArray();
            }

            return new WtexTexture(info, levels);
        }
        finally
        {
            KtxApi.Texture2Destroy(texture);
        }
    }

    /// <summary>
    /// A KTX 2 file as the bytes of a <c>.wtex</c>: <see cref="ReadKtx2"/> and <see cref="Write"/>. The level bytes are the file's.
    /// </summary>
    /// <param name="name">What the file is called in messages.</param>
    /// <param name="ktx2">The whole file.</param>
    /// <param name="settings">The engine's fields, which the file does not have; null for the defaults.</param>
    /// <param name="options">The UID, the import hash and the compression of the result; null for the defaults.</param>
    /// <exception cref="ContentException">The file is not a KTX 2 file this engine converts.</exception>
    public static byte[] FromKtx2(string name, ReadOnlySpan<byte> ktx2, WtexSettings? settings = null, WtexWriteOptions? options = null)
    {
        WtexTexture texture = ReadKtx2(name, ktx2, settings);
        var levels = new ReadOnlyMemory<byte>[texture.Levels.Length];
        for (int level = 0; level < levels.Length; level++)
        {
            levels[level] = texture.Levels[level];
        }

        return Write(texture.Info, levels, options);
    }

    private static uint Ktx2VkFormat(WtexPayload payload, bool srgb, bool uastc) => uastc ? 0 : WtexInfo.VkFormatOf(payload, srgb);

    /// <summary>
    /// What the header and the level index of a KTX 2 file say, checked before libktx sees the file: a texture that is too large, a level that is not the size of its place in the chain, a supercompression the
    /// engine does not read. Nothing is allocated here.
    /// </summary>
    private static WtexInfo Ktx2Info(string name, ReadOnlySpan<byte> file, WtexSettings settings, out bool uastc)
    {
        uastc = false;
        if (file.Length < Ktx2HeaderSize || !Ktx2Loader.IsKtx2(file))
        {
            throw new ContentException($"'{name}' is not a KTX 2 file.");
        }

        uint vkFormat = BinaryPrimitives.ReadUInt32LittleEndian(file[12..]);
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(file[20..]);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(file[24..]);
        uint depth = BinaryPrimitives.ReadUInt32LittleEndian(file[28..]);
        uint layers = BinaryPrimitives.ReadUInt32LittleEndian(file[32..]);
        uint faces = BinaryPrimitives.ReadUInt32LittleEndian(file[36..]);
        uint levelCount = BinaryPrimitives.ReadUInt32LittleEndian(file[40..]);
        uint scheme = BinaryPrimitives.ReadUInt32LittleEndian(file[44..]);

        if (scheme is not (0 or 2))
        {
            throw new ContentException(scheme == 1
                ? $"'{name}' is ETC1S (BasisLZ) texture data; only UASTC, and formats that need no transcoding, are converted."
                : $"'{name}' has the supercompression scheme {scheme}, which is not read; only none and zstd are.");
        }

        if (height == 0)
        {
            throw new ContentException($"'{name}' is a 1D texture; only 2D, 3D, cube map and array textures are converted.");
        }

        if (levelCount is 0 or > (uint)WtexInfo.MaxMips)
        {
            throw new ContentException(levelCount == 0
                ? $"'{name}' asks for its mip levels to be generated; a texture file has them."
                : $"'{name}' has {levelCount} mip levels; at most {WtexInfo.MaxMips} are read.");
        }

        if (faces is not (1 or 6) || width > WtexInfo.MaxSize || height > WtexInfo.MaxSize || depth > WtexInfo.MaxDepth || layers > WtexInfo.MaxLayers)
        {
            throw new ContentException($"'{name}' is a texture of {width}x{height}x{depth}, {layers} layers, {faces} faces; this engine reads at most {WtexInfo.MaxSize}x{WtexInfo.MaxSize}x{WtexInfo.MaxDepth}, {WtexInfo.MaxLayers} layers, and 1 or 6 faces.");
        }

        (WtexPayload payload, bool srgb) = vkFormat switch
        {
            0 => (WtexPayload.Uastc, false),
            37 => (WtexPayload.Rgba8, false),
            43 => (WtexPayload.Rgba8, true),
            145 => (WtexPayload.Bc7, false),
            146 => (WtexPayload.Bc7, true),
            141 => (WtexPayload.Bc5, false),
            139 => (WtexPayload.Bc4, false),
            143 => (WtexPayload.Bc6hU, false),
            97 => (WtexPayload.Rgba16f, false),
            _ => throw new ContentException($"'{name}' holds the Vulkan format {vkFormat}, which a texture file of the engine does not (UASTC, RGBA8, BC4, BC5, BC6H, BC7 and half-float RGBA do)."),
        };
        uastc = payload == WtexPayload.Uastc;

        WtexTraits flags = settings.Flags;
        if (payload is WtexPayload.Uastc or WtexPayload.Rgba8)
        {
            flags |= WtexTraits.Portable;
        }

        if (srgb)
        {
            flags |= WtexTraits.Srgb;
        }

        if (payload is WtexPayload.Bc6hU or WtexPayload.Rgba16f)
        {
            flags |= WtexTraits.Hdr;
        }

        if (faces == 6)
        {
            flags |= WtexTraits.Cube;
        }

        if (layers > 0)
        {
            flags |= WtexTraits.Array;
        }

        var info = new WtexInfo
        {
            Width = (int)width,
            Height = (int)height,
            Depth = (int)Math.Max(1, depth),
            Layers = (int)Math.Max(1, layers),
            Faces = (int)faces,
            MipCount = (int)levelCount,
            Payload = payload,
            Flags = flags,
            Group = settings.Group,
            LodBias = settings.LodBias,
            AlphaCutoff = settings.AlphaCutoff,
            Sampler = settings.Sampler,
        };
        if (info.Check() is { } problem)
        {
            throw new ContentException($"'{name}' is a texture that " + problem + ".");
        }

        // The level index: level i has its uncompressed size in it, which is what the shape says it is; so libktx is never asked to inflate more than the texture needs.
        if ((long)Ktx2HeaderSize + (levelCount * 24L) > file.Length)
        {
            throw new ContentException($"'{name}' is cut short: its level index is not in the file.");
        }

        for (int level = 0; level < info.MipCount; level++)
        {
            ulong uncompressed = BinaryPrimitives.ReadUInt64LittleEndian(file[(Ktx2HeaderSize + (level * 24) + 16)..]);
            if (uncompressed != (ulong)info.LevelSize(level))
            {
                throw new ContentException($"'{name}' has level {level} of {uncompressed} bytes; a texture of its shape has {info.LevelSize(level)}.");
            }
        }

        return info;
    }
}
