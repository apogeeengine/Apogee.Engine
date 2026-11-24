using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using Weft.Content;
using Weft.Content.Packages;
using Weft.Content.Packs;
using Xunit;

namespace Weft.Export.Tests;

public class WindowsPackagingTests
{
    private static readonly ExportTarget s_windows = new("win", "x64");

    private const string Hex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    /// <summary>A version block as the tests read it: the structure that <c>VS_VERSIONINFO</c> is made of.</summary>
    private sealed record Block(string Key, int Type, byte[] Value, int ValueLength, List<Block> Children);

    private static GameManifest Fixture(string name = "Fixture Game", string description = "A game for the tests.", string copyright = "Copyright 2026 Test Studio", string version = "1.2.3")
    {
        string xml = $"""<Game name="{name}" organization="Test Studio" version="{version}" executable="FixtureGame" identifier="org.test.fixturegame" description="{description}" copyright="{copyright}"/>""";
        Assert.True(GameManifest.TryParse(xml, "game.wgam", out GameManifest? manifest, out IReadOnlyList<string> problems), string.Join("\n", problems));
        return manifest!;
    }

    /// <summary>A 256x256 icon: the top left quarter red, the bottom right quarter blue, the rest transparent.</summary>
    private static byte[] IconPng(int size = 256)
    {
        byte[] rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int at = ((y * size) + x) * 4;
                if (x < size / 2 && y < size / 2)
                {
                    (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = (255, 0, 0, 255);
                }
                else if (x >= size / 2 && y >= size / 2)
                {
                    (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = (0, 0, 255, 255);
                }
            }
        }

        return Png.Encode(size, size, rgba);
    }

    /// <summary>Runs the Windows step on a runtime-like executable, with the pack key written first like the export does.</summary>
    private static (PlatformPackagingContext Context, byte[] Before, byte[] After) Apply(Scratch scratch, GameManifest manifest, byte[]? icon, bool console = false, byte[]? executable = null, bool key = true)
    {
        string output = scratch.Path("out");
        string path = scratch.Write("out/FixtureGame.exe", executable ?? FakeRuntime.Executable(s_windows));
        if (key)
        {
            PackKeySlot.Write(path, PackKey.Parse(Hex));
        }

        byte[] before = File.ReadAllBytes(path);
        var context = new PlatformPackagingContext
        {
            Options = new ExportOptions { GameFolder = scratch.Path("game"), OutputFolder = output, Target = s_windows, Console = console },
            Manifest = manifest,
            Layout = ExportLayout.For(s_windows, manifest, output),
            IconFile = icon,
            Warnings = [],
            Log = static _ => { },
        };
        new WindowsPackaging().Apply(context);
        return (context, before, File.ReadAllBytes(path));
    }

    private static Block ParseBlock(byte[] data, int start, int parentEnd)
    {
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start));
        int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start + 2));
        int type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start + 4));
        int end = start + length;
        Assert.True(end <= parentEnd, "a block ends after its parent");
        Assert.Equal(0, start % 4);

        int keyEnd = start + 6;
        while (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(keyEnd)) != 0)
        {
            keyEnd += 2;
        }

        string key = Encoding.Unicode.GetString(data, start + 6, keyEnd - start - 6);
        int valueStart = (keyEnd + 2 + 3) / 4 * 4;
        int valueBytes = type == 1 ? valueLength * 2 : valueLength;
        Assert.True(valueStart + valueBytes <= end, "a value ends after its block");
        var block = new Block(key, type, data[valueStart..(valueStart + valueBytes)], valueLength, []);
        int child = (valueStart + valueBytes + 3) / 4 * 4;
        while (child < end)
        {
            Block c = ParseBlock(data, child, end);
            block.Children.Add(c);
            child = (child + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(child)) + 3) / 4 * 4;
        }

        return block;
    }

    private static Block Version(byte[] exe) => ParseBlock(TestResources.Single(exe, 16, 1), 0, TestResources.Single(exe, 16, 1).Length);

    private static Dictionary<string, string> Strings(Block version)
    {
        Block table = version.Children.Single(b => b.Key == "StringFileInfo").Children.Single();
        Assert.Equal("040904B0", table.Key);
        return table.Children.ToDictionary(s => s.Key, s => Encoding.Unicode.GetString(s.Value).TrimEnd('\0'));
    }

    [Fact]
    public void TheVersionResourceSaysWhatGameXmlSays()
    {
        using var scratch = new Scratch();

        (_, _, byte[] after) = Apply(scratch, Fixture(), icon: null);

        byte[] resource = TestResources.Single(after, 16, 1);
        Block version = ParseBlock(resource, 0, resource.Length);
        Assert.Equal("VS_VERSION_INFO", version.Key);
        Assert.Equal(resource.Length, BinaryPrimitives.ReadUInt16LittleEndian(resource)); // wLength is the whole resource
        Assert.Equal(52, version.Value.Length);

        // VS_FIXEDFILEINFO: signature, structure version, file version 1.2.3.0, product version 1.2.3.0, mask 0x3F, no flags, VOS_NT_WINDOWS32, VFT_APP, no subtype, no date.
        uint[] fixedInfo = [.. Enumerable.Range(0, 13).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(version.Value.AsSpan(i * 4)))];
        Assert.Equal([0xFEEF04BDu, 0x00010000u, 0x0001_0002u, 0x0003_0000u, 0x0001_0002u, 0x0003_0000u, 0x3Fu, 0u, 0x00040004u, 1u, 0u, 0u, 0u], fixedInfo);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["CompanyName"] = "Test Studio",
                ["FileDescription"] = "A game for the tests.",
                ["FileVersion"] = "1.2.3.0",
                ["InternalName"] = "FixtureGame",
                ["LegalCopyright"] = "Copyright 2026 Test Studio",
                ["OriginalFilename"] = "FixtureGame.exe",
                ["ProductName"] = "Fixture Game",
                ["ProductVersion"] = "1.2.3.0",
            },
            Strings(version));

        // The translation: language 0x0409, code page 1200 (the 4 bytes 09 04 B0 04).
        Block translation = version.Children.Single(b => b.Key == "VarFileInfo").Children.Single();
        Assert.Equal("Translation", translation.Key);
        Assert.Equal([0x09, 0x04, 0xB0, 0x04], translation.Value);
        Assert.Equal(4, translation.ValueLength);
        Assert.Equal(["StringFileInfo", "VarFileInfo"], version.Children.Select(b => b.Key));
    }

    [Fact]
    public void AGameWithoutADescriptionIsDescribedByItsName()
    {
        using var scratch = new Scratch();

        (_, _, byte[] after) = Apply(scratch, Fixture(description: "", copyright: ""), icon: null);

        Dictionary<string, string> strings = Strings(Version(after));
        Assert.Equal("Fixture Game", strings["FileDescription"]);
        Assert.Equal("", strings["LegalCopyright"]);
    }

    [Fact]
    public void ATextWithQuotesAndNonAsciiLettersIsWrittenAsItIs()
    {
        using var scratch = new Scratch();

        (_, _, byte[] after) = Apply(scratch, Fixture(name: "Fl&amp;ame Éé 日本", copyright: "(c) © 2026"), icon: null);

        Dictionary<string, string> strings = Strings(Version(after));
        Assert.Equal("Fl&ame Éé 日本", strings["ProductName"]);
        Assert.Equal("(c) © 2026", strings["LegalCopyright"]);
    }

    [Fact]
    public void AVersionPartAbove65535IsRefused()
    {
        using var scratch = new Scratch();

        Assert.Contains("65535", Assert.Throws<ExportException>(() => Apply(scratch, Fixture(version: "1.2.70000"), icon: null)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnormousDescriptionIsRefusedNotTruncated()
    {
        using var scratch = new Scratch();

        Assert.Contains("64 KiB", Assert.Throws<ExportException>(() => Apply(scratch, Fixture(description: new string('x', 40000)), icon: null)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIconGroupListsEveryImage()
    {
        using var scratch = new Scratch();

        (PlatformPackagingContext context, _, byte[] after) = Apply(scratch, Fixture(), IconPng());

        Dictionary<string, byte[]> resources = TestResources.Read(after).ToDictionary(r => r.Key, r => r.Data);
        byte[] group = resources["#14/#1/1033"];
        Assert.Equal((0, 1, 7), (BinaryPrimitives.ReadUInt16LittleEndian(group), BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(2)), BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4))));
        Assert.Equal(6 + (14 * 7), group.Length);
        Assert.Equal(7, resources.Keys.Count(k => k.StartsWith("#3/", StringComparison.Ordinal)));

        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        for (int i = 0; i < 7; i++)
        {
            ReadOnlySpan<byte> entry = group.AsSpan(6 + (14 * i), 14);
            int size = sizes[i];
            int id = BinaryPrimitives.ReadUInt16LittleEndian(entry[12..]);
            byte[] image = resources[$"#3/#{id}/1033"]; // every entry's id names an RT_ICON
            Assert.Equal(size == 256 ? 0 : size, entry[0]);
            Assert.Equal(size == 256 ? 0 : size, entry[1]);
            Assert.Equal((0, 0, 1, 32), (entry[2], entry[3], BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]), BinaryPrimitives.ReadUInt16LittleEndian(entry[6..])));
            Assert.Equal(image.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]));

            if (size == 256)
            {
                Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], image[..8]); // a PNG file
                Assert.Equal(256, BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(16))); // IHDR width
                Assert.Equal(256, BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(20)));
                continue;
            }

            int maskRow = (size + 31) / 32 * 4;
            Assert.Equal(40 + (size * size * 4) + (maskRow * size), image.Length);
            Assert.Equal((40u, size, size * 2, (ushort)1, (ushort)32, 0u), (BinaryPrimitives.ReadUInt32LittleEndian(image), BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(4)), BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(8)), BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(12)), BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14)), BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(16))));
            Assert.All(image[(40 + (size * size * 4))..], b => Assert.Equal(0, b)); // the AND mask is zero

            // Bottom-up BGRA: the image's top left (red) is in the file's last row, its first pixel; its bottom right (blue) is in the first row, the last pixel; the corners of the rest are clear.
            ReadOnlySpan<byte> pixels = image.AsSpan(40, size * size * 4);
            Assert.Equal([0, 0, 255, 255], pixels[((size - 1) * size * 4)..][..4].ToArray());
            Assert.Equal([255, 0, 0, 255], pixels[((size - 1) * 4)..][..4].ToArray());
            Assert.Equal(0, pixels[((size - 1) * size * 4) + ((size - 1) * 4) + 3]); // the image's top right: transparent
        }

        Assert.Empty(context.Warnings);
    }

    [Fact]
    public void ASmallIconIsWarnedAboutAndStillMakesEverySize()
    {
        using var scratch = new Scratch();

        (PlatformPackagingContext context, _, byte[] after) = Apply(scratch, Fixture(), IconPng(64));

        Assert.Contains(context.Warnings, w => w.Contains("64x64", StringComparison.Ordinal));
        Assert.Equal(7, TestResources.Read(after).Count(r => r.Type == "#3"));
    }

    [Fact]
    public void TheRuntimesOwnIconAndVersionAreReplacedAndItsManifestIsKept()
    {
        byte[] withOld = new TinyPe { MinHeaderSize = 0x400 }
            .Section(".text", [.. new byte[10], .. PackKeySlot.Template.ToArray()], TinyPe.TextFlags)
            .Resources([(3, 9, 0, [1]), (14, 5, 0, [2]), (16, 1, 0, [3]), (16, 1, 0x409, [4]), (24, 1, 0x409, [5, 5, 5])])
            .Build();
        using var scratch = new Scratch();

        (_, _, byte[] after) = Apply(scratch, Fixture(), IconPng(), executable: withOld);

        List<string> keys = [.. TestResources.Read(after).Select(r => r.Key)];
        Assert.DoesNotContain("#3/#9/0", keys);
        Assert.DoesNotContain("#14/#5/0", keys);
        Assert.DoesNotContain("#16/#1/0", keys);
        Assert.Contains("#16/#1/1033", keys);
        Assert.Equal([5, 5, 5], TestResources.Single(after, 24, 1));
    }

    [Fact]
    public void AGameWithoutAnIconKeepsTheRuntimesIcon()
    {
        byte[] withIcon = new TinyPe { MinHeaderSize = 0x400 }
            .Section(".text", [.. new byte[10], .. PackKeySlot.Template.ToArray()], TinyPe.TextFlags)
            .Resources([(3, 1, 0x409, [1]), (14, 1, 0x409, [2])])
            .Build();
        using var scratch = new Scratch();

        (_, _, byte[] after) = Apply(scratch, Fixture(), icon: null, executable: withIcon);

        Assert.Equal([1], TestResources.Single(after, 3, 1));
        Assert.Equal([2], TestResources.Single(after, 14, 1));
    }

    [Fact]
    public void ThePackKeyIsWhereItWasAndStillOpens()
    {
        using var scratch = new Scratch();

        (_, byte[] before, byte[] after) = Apply(scratch, Fixture(), IconPng());

        // The key was written into the executable first; the resources and the subsystem do not move or touch the code, so the slot is the same bytes at the same place, found once, and holds the key.
        int at = FakeRuntime.SlotOffset(before);
        Assert.Equal(at, FakeRuntime.SlotOffset(after));
        Assert.Equal(-1, after.AsSpan(at + 1).IndexOf(PackKeySlot.Template[..16]));
        Assert.Equal(before.AsSpan(at, 68).ToArray(), after.AsSpan(at, 68).ToArray());
        PackKey? key = PackKeySlot.Decode(after.AsSpan(at, 68));
        Assert.Equal(PackKey.Parse(Hex).Bytes.ToArray(), key!.Bytes.ToArray());
    }

    [Fact]
    public void EveryCopyOfThePackKeyIsWhereItWasAndStillOpens()
    {
        byte[] template = PackKeySlot.Template.ToArray();
        byte[] twoSlots = new TinyPe { MinHeaderSize = 0x400 }
            .Section(".text", [.. new byte[10], .. template, .. new byte[30], .. template, .. new byte[10]], TinyPe.TextFlags)
            .Resources([(24, 1, 0x409, [1])])
            .Section(".reloc", new byte[64], TinyPe.RelocFlags)
            .Build();
        using var scratch = new Scratch();

        (_, byte[] before, byte[] after) = Apply(scratch, Fixture(), IconPng(), executable: twoSlots);

        int first = before.AsSpan().IndexOf(template.AsSpan(0, 16));
        int second = before.AsSpan(first + 1).IndexOf(template.AsSpan(0, 16)) + first + 1;
        Assert.True(second > first);
        foreach (int at in new[] { first, second })
        {
            Assert.Equal(before.AsSpan(at, 68).ToArray(), after.AsSpan(at, 68).ToArray());
            Assert.Equal(PackKey.Parse(Hex).Bytes.ToArray(), PackKeySlot.Decode(after.AsSpan(at, 68))!.Bytes.ToArray());
        }
    }

    [Fact]
    public void TheExecutableIsAWindowedProgramUnlessTheConsoleIsKept()
    {
        using var scratch = new Scratch();
        (_, _, byte[] windowed) = Apply(scratch, Fixture(), IconPng());
        Assert.Equal(Subsystem.WindowsGui, new PEReader(new MemoryStream(windowed)).PEHeaders.PEHeader!.Subsystem);

        using var other = new Scratch();
        (_, _, byte[] console) = Apply(other, Fixture(), IconPng(), console: true);
        Assert.Equal(Subsystem.WindowsCui, new PEReader(new MemoryStream(console)).PEHeaders.PEHeader!.Subsystem);

        // Both are otherwise the same file, with the icon and the version either way.
        Assert.Equal(windowed.Length, console.Length);
        Assert.Equal(TestResources.Read(windowed).Select(r => r.Key), TestResources.Read(console).Select(r => r.Key));
    }

    [Fact]
    public void ARuntimeThatIsNotAPortableExecutableIsAnExportException()
    {
        using var scratch = new Scratch();

        Assert.Throws<ExportException>(() => Apply(scratch, Fixture(), IconPng(), executable: FakeRuntime.Header(new ExportTarget("linux", "x64")), key: false));
        Assert.Throws<ExportException>(() => Apply(scratch, Fixture(), IconPng(), executable: FakeRuntime.Header(s_windows), key: false)); // a header and nothing else
    }

    [Fact]
    public void WindowsReadsTheVersionInformation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FileVersionInfo reads version resources through Windows only");
        using var scratch = new Scratch();
        (_, _, byte[] after) = Apply(scratch, Fixture(), IconPng());
        string path = scratch.Write("check.exe", after);

        FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);

        Assert.Equal(("Fixture Game", "Test Studio", "1.2.3.0"), (info.ProductName, info.CompanyName, info.FileVersion));
    }
}
