using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Apogee.Export.Windows;

/// <summary>
/// The icon of a Windows program: one <c>RT_ICON</c> resource for each size and an <c>RT_GROUP_ICON</c> resource that lists them, which is how a <c>.ico</c> file is stored in an executable. Explorer
/// picks the size it needs from the list; <c>ExtractIcon</c> (and so SDL, for the window and the task bar) takes the first group, which is the one with the id 1.
/// <para>
/// The sizes below 256 are 32-bit DIBs, the format every Windows version reads: a <c>BITMAPINFOHEADER</c> whose height is twice the icon's (the image and then a mask are stacked), the pixels as
/// bottom-up BGRA, and a 1-bit AND mask, all zero, because the alpha channel does the masking. The 256 size is a PNG file, which Windows Vista and later read (and which is far smaller).
/// </para>
/// </summary>
internal static class IconResource
{
    /// <summary>The sizes in the icon, smallest first: those Explorer, the task bar, title bars and the Alt+Tab list ask for.</summary>
    public static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256];

    /// <summary>The id of the icon group, which <c>ExtractIcon</c> finds first.</summary>
    public const int GroupId = 1;

    private const int HeaderSize = 40;
    private const int GroupHeaderSize = 6;
    private const int GroupEntrySize = 14;
    private const int PngSize = 256;

    /// <summary>
    /// Adds the icon's resources to <paramref name="resources"/>: the images as <c>RT_ICON</c> with the ids 1 to 7 in order of size, and the group as <c>RT_GROUP_ICON</c> number <see cref="GroupId"/>.
    /// A size above the image is made by scaling it up, which <paramref name="icon"/> notes in its warnings.
    /// </summary>
    /// <param name="resources">Where the resources go.</param>
    /// <param name="icon">The game's icon.</param>
    /// <param name="language">The language of the resources (<c>LANGID</c>).</param>
    public static void Add(IDictionary<ResourceKey, byte[]> resources, IconImages icon, int language)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(icon);

        // GRPICONDIR: idReserved(2) = 0, idType(2) = 1 (icon), idCount(2); then one GRPICONDIRENTRY each.
        byte[] group = new byte[GroupHeaderSize + (GroupEntrySize * Sizes.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(4), (ushort)Sizes.Length);
        for (int i = 0; i < Sizes.Length; i++)
        {
            int size = Sizes[i];
            byte[] image = size == PngSize ? icon.Png(size) : Dib(icon.Rgba(size), size);
            int id = i + 1;
            resources[new ResourceKey(ResourceType.Icon, id, language)] = image;

            // GRPICONDIRENTRY: bWidth(1) bHeight(1) (0 means 256) bColorCount(1) = 0 bReserved(1) = 0 wPlanes(2) = 1 wBitCount(2) = 32 dwBytesInRes(4) nID(2).
            Span<byte> entry = group.AsSpan(GroupHeaderSize + (GroupEntrySize * i), GroupEntrySize);
            entry[0] = (byte)(size == 256 ? 0 : size);
            entry[1] = entry[0];
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)image.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[12..], (ushort)id);
        }

        resources[new ResourceKey(ResourceType.GroupIcon, GroupId, language)] = group;
    }

    /// <summary>
    /// One image of an icon as the data of an <c>RT_ICON</c> resource: <c>BITMAPINFOHEADER</c> (40 bytes: size, width, <b>height doubled</b>, 1 plane, 32 bits, no compression, the byte count of the
    /// pixels and the mask, the rest 0), then the pixels as BGRA from the bottom row up (an RGBA image is the other way round in channel and row order), then the AND mask: 1 bit a pixel, each row padded
    /// to 4 bytes, all zero.
    /// </summary>
    /// <param name="rgba">The image, RGBA8, top row first, not premultiplied.</param>
    /// <param name="size">The width and height.</param>
    internal static byte[] Dib(byte[] rgba, int size)
    {
        int pixels = size * size * 4;
        int maskRow = (size + 31) / 32 * 4;
        byte[] dib = new byte[HeaderSize + pixels + (maskRow * size)];
        Span<byte> header = dib.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], size);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], size * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)(pixels + (maskRow * size)));

        for (int y = 0; y < size; y++)
        {
            int from = (size - 1 - y) * size * 4; // the file's first row is the image's last
            int to = HeaderSize + (y * size * 4);
            for (int x = 0; x < size; x++)
            {
                int source = from + (x * 4);
                int target = to + (x * 4);
                dib[target] = rgba[source + 2];
                dib[target + 1] = rgba[source + 1];
                dib[target + 2] = rgba[source];
                dib[target + 3] = rgba[source + 3];
            }
        }

        return dib;
    }
}
