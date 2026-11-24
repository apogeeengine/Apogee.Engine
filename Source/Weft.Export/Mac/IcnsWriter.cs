using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Weft.Export;

/// <summary>
/// Writes the macOS icon file format, <c>icns</c>: the characters <c>icns</c>, the length of the whole file (4 bytes, big-endian), and the images, each as its type (four characters), its length
/// including these 8 bytes (big-endian) and a PNG file. The images are the sizes Finder and the Dock use, from 16 to 1024 pixels; a size above the game's icon is left out, so nothing in the file is
/// an enlarged copy, and macOS scales the biggest image that is there.
/// </summary>
public static class IcnsWriter
{
    /// <summary>The images of an icon file: the type of the entry, and the width and height in pixels. The <c>@2x</c> types hold the next size up, for a high-density screen.</summary>
    internal static readonly (string Type, int Size)[] Entries =
    [
        ("icp4", 16),
        ("icp5", 32),
        ("icp6", 64),
        ("ic07", 128),
        ("ic08", 256),
        ("ic09", 512),
        ("ic10", 1024),
        ("ic11", 32), // 16x16 at 2x
        ("ic12", 64), // 32x32 at 2x
        ("ic13", 256), // 128x128 at 2x
        ("ic14", 512), // 256x256 at 2x
    ];

    /// <summary>The smallest image that macOS makes the file's icons from, and so the smallest an icon may be without its smallest size being a scaled-up copy.</summary>
    private const int SmallestSize = 16;

    /// <summary>The size from which a missing image is worth a warning.</summary>
    private const int LargeSize = 512;

    /// <summary>Makes the <c>icns</c> file of <paramref name="icon"/>.</summary>
    /// <param name="icon">The game's icon.</param>
    /// <param name="warnings">Gets a warning when the images of 512 pixels or more are missing because the icon is smaller (and the icon's own warnings are in <see cref="IconImages.Warnings"/>).</param>
    public static byte[] Build(IconImages icon, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentNullException.ThrowIfNull(warnings);
        using var body = new MemoryStream();
        var left = new List<int>();
        foreach ((string type, int size) in Entries)
        {
            // An icon smaller than 16 pixels still gets its smallest image, made bigger (the icon warns); no other size is made bigger.
            if (size > icon.Size && !(size == SmallestSize && type == "icp4"))
            {
                if (size >= LargeSize && !left.Contains(size))
                {
                    left.Add(size);
                }

                continue;
            }

            WriteEntry(body, type, icon.Png(size));
        }

        if (left.Count > 0)
        {
            left.Sort();
            warnings.Add($"The icon is {icon.Size}x{icon.Size}, so the macOS icon has no {string.Join(" or ", left.ConvertAll(s => $"{s}x{s}"))} image; 1024x1024 is advised for macOS.");
        }

        byte[] file = new byte[8 + body.Length];
        "icns"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), (uint)file.Length);
        body.ToArray().CopyTo(file, 8);
        return file;
    }

    private static void WriteEntry(Stream stream, string type, byte[] png)
    {
        Span<byte> header = stackalloc byte[8];
        System.Text.Encoding.ASCII.GetBytes(type, header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)(8 + png.Length));
        stream.Write(header);
        stream.Write(png);
    }
}
