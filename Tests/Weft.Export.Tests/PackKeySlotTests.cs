using System;
using System.IO;
using System.Linq;
using Weft.Content.Packs;
using Xunit;

namespace Weft.Export.Tests;

public class PackKeySlotTests
{
    private const string Hex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    private static byte[] KeyBytes(PackKey key) => key.Bytes.ToArray();

    [Fact]
    public void AKeyIsWrittenIntoTheOnlySlotAndReadBack()
    {
        using var scratch = new Scratch();
        string exe = scratch.Write("game", FakeRuntime.Executable(new ExportTarget("linux", "x64")));
        byte[] before = File.ReadAllBytes(exe);
        int offset = FakeRuntime.SlotOffset(before);
        PackKey key = PackKey.Parse(Hex);

        Assert.Equal(1, PackKeySlot.Write(exe, key));

        byte[] after = File.ReadAllBytes(exe);
        Assert.Equal(before.Length, after.Length);
        // Only the slot changed, and what the routine the running game uses reads from it is the key.
        for (int i = 0; i < before.Length; i++)
        {
            if (i < offset || i >= offset + 68)
            {
                Assert.Equal(before[i], after[i]);
            }
        }

        PackKey? read = PackKeySlot.Decode(after.AsSpan(offset, 68));
        Assert.NotNull(read);
        Assert.Equal(KeyBytes(key), KeyBytes(read!));
    }

    [Fact]
    public void AnEmptySlotHasNoKeyAndADevelopmentRunHasNoSlotKey()
    {
        Assert.Null(PackKeySlot.Decode(PackKeySlot.Template));
        Assert.Equal(68, PackKeySlot.Template.Length);
        // This program was not exported: the slot in its own data is the empty template.
        Assert.Null(PackKeySlot.TryRead());
    }

    [Fact]
    public void ASlotCanBeWrittenAgain()
    {
        using var scratch = new Scratch();
        string exe = scratch.Write("game", FakeRuntime.Executable(new ExportTarget("win", "x64")));

        PackKeySlot.Write(exe, PackKey.Parse(Hex));
        PackKey second = PackKey.Parse(new string('a', 64));
        PackKeySlot.Write(exe, second);

        byte[] file = File.ReadAllBytes(exe);
        Assert.Equal(KeyBytes(second), KeyBytes(PackKeySlot.Decode(file.AsSpan(FakeRuntime.SlotOffset(file), 68))!));
    }

    private static byte[] Padded(params byte[][] parts) => [.. parts.SelectMany(static p => (byte[])[.. new byte[37], .. p]), .. new byte[37]];

    [Fact]
    public void NoSlotIsAnError()
    {
        using var scratch = new Scratch();
        string none = scratch.Write("none", new byte[4096]);
        Assert.Contains("no pack key slot", Assert.Throws<ExportException>(() => PackKeySlot.Write(none, PackKey.Parse(Hex))).Message, StringComparison.Ordinal);

        string missing = scratch.Path("missing");
        Assert.Throws<ExportException>(() => PackKeySlot.Write(missing, PackKey.Parse(Hex)));
    }

    [Fact]
    public void EveryCopyOfTheSlotIsPatchedWithTheSameBytesAndReadBack()
    {
        using var scratch = new Scratch();
        byte[] template = PackKeySlot.Template.ToArray();
        byte[] before = Padded(template, template);
        string exe = scratch.Write("two", before);
        PackKey key = PackKey.Parse(Hex);

        int copies = PackKeySlot.Write(exe, key);

        Assert.Equal(2, copies);
        byte[] after = File.ReadAllBytes(exe);
        int first = before.AsSpan().IndexOf(template);
        int second = before.AsSpan(first + 1).IndexOf(template) + first + 1;
        Assert.True(second > first);
        Assert.Equal(after.AsSpan(first, 68).ToArray(), after.AsSpan(second, 68).ToArray()); // the same salt and masked key
        Assert.Equal(KeyBytes(key), KeyBytes(PackKeySlot.Decode(after.AsSpan(first, 68))!));
        Assert.Equal(KeyBytes(key), KeyBytes(PackKeySlot.Decode(after.AsSpan(second, 68))!));
        for (int i = 0; i < before.Length; i++)
        {
            if ((i < first || i >= first + 68) && (i < second || i >= second + 68))
            {
                Assert.Equal(before[i], after[i]);
            }
        }

        Assert.Equal([(first, 68), (second, 68)], PackKeySlot.Locate(exe).Select(static r => ((int)r.Offset, r.Length)));
    }

    [Fact]
    public void AFileWithOneSlotReportsOneCopy()
    {
        using var scratch = new Scratch();
        string exe = scratch.Write("game", FakeRuntime.Executable(new ExportTarget("linux", "x64")));

        Assert.Equal(1, PackKeySlot.Write(exe, PackKey.Parse(Hex)));
        Assert.Single(PackKeySlot.Locate(exe));
    }

    [Fact]
    public void AMagicWithoutTheRestOfTheTemplateIsNotASlotAndIsNotPatched()
    {
        using var scratch = new Scratch();
        byte[] template = PackKeySlot.Template.ToArray();
        byte[] lookalike = [.. template[..16], .. Enumerable.Repeat((byte)0xEE, 52)];
        byte[] before = Padded(template, lookalike);
        string exe = scratch.Write("game", before);

        Assert.Equal(1, PackKeySlot.Write(exe, PackKey.Parse(Hex)));

        byte[] after = File.ReadAllBytes(exe);
        int fake = before.AsSpan().IndexOf(lookalike);
        Assert.Equal(lookalike, after.AsSpan(fake, 68).ToArray());

        // With nothing but the lookalike there is no slot at all.
        string only = scratch.Write("only", Padded(lookalike));
        Assert.Contains("no pack key slot", Assert.Throws<ExportException>(() => PackKeySlot.Write(only, PackKey.Parse(Hex))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASlotCutByTheEndOfTheFileIsAnErrorAndNothingIsWritten()
    {
        using var scratch = new Scratch();
        byte[] template = PackKeySlot.Template.ToArray();
        byte[] before = [.. new byte[20], .. template, .. new byte[20], .. template[..40]];
        string exe = scratch.Write("cut", before);

        Assert.Contains("ends inside its pack key slot", Assert.Throws<ExportException>(() => PackKeySlot.Write(exe, PackKey.Parse(Hex))).Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(exe));
    }

    [Fact]
    public void FarMoreCopiesThanACompilerEmitsIsRefused()
    {
        using var scratch = new Scratch();
        byte[] template = PackKeySlot.Template.ToArray();
        byte[] before = Padded([.. Enumerable.Repeat(template, 17)]);
        string exe = scratch.Write("many", before);

        Assert.Contains("more than 16 pack key slots", Assert.Throws<ExportException>(() => PackKeySlot.Write(exe, PackKey.Parse(Hex))).Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(exe));
    }

    [Fact]
    public void TheKeyIsNotInTheFileInTheClear()
    {
        using var scratch = new Scratch();
        string exe = scratch.Write("game", FakeRuntime.Executable(new ExportTarget("osx", "arm64")));
        PackKey key = PackKey.Parse(Hex);

        PackKeySlot.Write(exe, key);

        byte[] file = File.ReadAllBytes(exe);
        Assert.Equal(-1, file.AsSpan().IndexOf(KeyBytes(key)));
        Assert.Equal(-1, file.AsSpan().IndexOf(KeyBytes(key).AsSpan(0, 8)));
        Assert.Equal(-1, file.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(Hex)));
    }

    [Fact]
    public void ASlotOfAnotherFormatIsRefused()
    {
        byte[] slot = PackKeySlot.Encode(KeyBytes(PackKey.Parse(Hex)), new byte[16]);
        slot[16] = 2;

        Assert.Throws<InvalidDataException>(() => PackKeySlot.Decode(slot));
        Assert.Throws<InvalidDataException>(() => PackKeySlot.Decode(slot.AsSpan(0, 60)));
    }
}
