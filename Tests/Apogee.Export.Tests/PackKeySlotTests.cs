using System;
using System.IO;
using Apogee.Content.Packs;
using Xunit;

namespace Apogee.Export.Tests;

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

        PackKeySlot.Write(exe, key);

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

    [Fact]
    public void NoSlotOrTwoSlotsIsAnError()
    {
        using var scratch = new Scratch();
        string none = scratch.Write("none", new byte[4096]);
        Assert.Contains("no pack key slot", Assert.Throws<ExportException>(() => PackKeySlot.Write(none, PackKey.Parse(Hex))).Message, StringComparison.Ordinal);

        byte[] one = FakeRuntime.Executable(new ExportTarget("linux", "x64"));
        string two = scratch.Write("two", [.. one, .. PackKeySlot.Template.ToArray()]);
        byte[] twoBefore = File.ReadAllBytes(two);
        Assert.Contains("more than one pack key slot", Assert.Throws<ExportException>(() => PackKeySlot.Write(two, PackKey.Parse(Hex))).Message, StringComparison.Ordinal);
        Assert.Equal(twoBefore, File.ReadAllBytes(two));

        string missing = scratch.Path("missing");
        Assert.Throws<ExportException>(() => PackKeySlot.Write(missing, PackKey.Parse(Hex)));
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
