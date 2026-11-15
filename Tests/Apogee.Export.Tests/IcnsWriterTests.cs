using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Apogee.Content;
using Xunit;

namespace Apogee.Export.Tests;

public class IcnsWriterTests
{
    private static IconImages Icon(int size) => IconImages.Load(Png.Encode(size, size, Enumerable.Repeat((byte)180, size * size * 4).ToArray()));

    /// <summary>The entries of an icns file: type, and the PNG inside. Checks the lengths on the way.</summary>
    private static List<(string Type, byte[] Png)> Entries(byte[] file)
    {
        Assert.Equal("icns", Encoding.ASCII.GetString(file, 0, 4));
        Assert.Equal((uint)file.Length, BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4)));
        var entries = new List<(string, byte[])>();
        int at = 8;
        while (at < file.Length)
        {
            Assert.True(at + 8 <= file.Length, "an entry header goes past the end");
            string type = Encoding.ASCII.GetString(file, at, 4);
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 4));
            Assert.True(length >= 8 && at + length <= file.Length, $"entry {type} has length {length}");
            entries.Add((type, file[(at + 8)..(at + length)]));
            at += length;
        }

        Assert.Equal(file.Length, at); // the entries add up to the file exactly
        return entries;
    }

    private static int PngSize(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        return width;
    }

    [Fact]
    public void AnIconOf1024PixelsHasEveryEntryAtItsSize()
    {
        var warnings = new List<string>();

        List<(string Type, byte[] Png)> entries = Entries(IcnsWriter.Build(Icon(1024), warnings));

        Assert.Equal(
            ["icp4:16", "icp5:32", "icp6:64", "ic07:128", "ic08:256", "ic09:512", "ic10:1024", "ic11:32", "ic12:64", "ic13:256", "ic14:512"],
            entries.Select(e => $"{e.Type}:{PngSize(e.Png)}"));
        Assert.Empty(warnings);
    }

    [Fact]
    public void NoEntryIsBiggerThanTheIconAndTheMissingBigOnesAreWarnedAbout()
    {
        var warnings = new List<string>();

        List<(string Type, byte[] Png)> entries = Entries(IcnsWriter.Build(Icon(256), warnings));

        Assert.Equal(["icp4", "icp5", "icp6", "ic07", "ic08", "ic11", "ic12", "ic13"], entries.Select(e => e.Type));
        Assert.All(entries, e => Assert.True(PngSize(e.Png) <= 256));
        string warning = Assert.Single(warnings);
        Assert.Contains("512x512 or 1024x1024", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIconOf512PixelsOnlyLacksThe1024Image()
    {
        var warnings = new List<string>();

        List<(string Type, byte[] Png)> entries = Entries(IcnsWriter.Build(Icon(512), warnings));

        Assert.Contains("ic09", entries.Select(e => e.Type));
        Assert.Contains("ic14", entries.Select(e => e.Type));
        Assert.DoesNotContain("ic10", entries.Select(e => e.Type));
        Assert.Contains("1024x1024", Assert.Single(warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void AnIconBelowSixteenPixelsStillHasASmallestImage()
    {
        List<(string Type, byte[] Png)> entries = Entries(IcnsWriter.Build(Icon(8), []));

        (string type, byte[] png) = Assert.Single(entries);
        Assert.Equal("icp4", type);
        Assert.Equal(16, PngSize(png));
    }

    [Fact]
    public void EachImageIsTheIconScaledToItsSize()
    {
        // A flat color stays that color at every size (the box filter averages), and an opaque icon stays opaque.
        List<(string Type, byte[] Png)> entries = Entries(IcnsWriter.Build(Icon(256), []));

        foreach ((string type, byte[] png) in entries)
        {
            TextureData decoded = ImageDecoder.Decode(type, png, srgb: false, SamplerData.Default, generateMips: false);
            Assert.Equal(PngSize(png), decoded.Width);
            Assert.Equal(new byte[] { 180, 180, 180, 180 }, decoded.Mips[0][..4]);
        }
    }
}
