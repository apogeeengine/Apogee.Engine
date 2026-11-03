using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Apogee.Content.FileSystem;
using Apogee.Content.Packs;
using Xunit;

namespace Apogee.Content.Tests;

public class PackKeyTests
{
    private const string Hex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    [Fact]
    public void AKeyIs64HexDigits()
    {
        Assert.NotNull(PackKey.Parse(Hex));
        Assert.NotNull(PackKey.Parse("  " + Hex.ToUpperInvariant() + "\n"));
        Assert.Throws<FormatException>(() => PackKey.Parse(Hex[..62]));
        Assert.Throws<FormatException>(() => PackKey.Parse(Hex + "00"));
        Assert.Throws<FormatException>(() => PackKey.Parse("zz" + Hex[2..]));
        Assert.Throws<ArgumentException>(() => new PackKey(new byte[16]));
    }

    [Fact]
    public void ItNeverShowsItself()
    {
        Assert.DoesNotContain("0102", PackKey.Parse(Hex).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheEnvironmentHasItOrNot()
    {
        const string Variable = "APOGEE_TEST_PACK_KEY";
        Environment.SetEnvironmentVariable(Variable, null);
        Assert.Null(PackKey.FromEnvironment(Variable));
        Environment.SetEnvironmentVariable(Variable, "");
        Assert.Null(PackKey.FromEnvironment(Variable));
        Environment.SetEnvironmentVariable(Variable, Hex);
        Assert.NotNull(PackKey.FromEnvironment(Variable));
        Environment.SetEnvironmentVariable(Variable, "not a key");
        Assert.Throws<FormatException>(() => PackKey.FromEnvironment(Variable));
        Environment.SetEnvironmentVariable(Variable, null);
    }
}

public class PackTests
{
    private static readonly PackKey s_key = PackKey.Parse("1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100");
    private static readonly PackKey s_otherKey = PackKey.Parse("ffeeddccbbaa99887766554433221100ffeeddccbbaa99887766554433221100");

    private static byte[] Text(int length) => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", length / 45 + 1)))[..length];

    private static byte[] Noise(int length, int seed = 1)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Write(TempFolder folder, string name, PackWriterOptions? options, params (string Path, byte[] Data)[] files)
    {
        string path = Path.Combine(folder.Root, name);
        using var writer = new PackWriter(path, options);
        foreach ((string file, byte[] data) in files)
        {
            writer.Add(file, data);
        }

        writer.Finish();
        return path;
    }

    private static void RequireAes()
    {
        if (!AesGcm.IsSupported)
        {
            Assert.Skip("AES-GCM is not available on this machine.");
        }
    }

    [Fact]
    public void FilesComeBackAsTheyWentInAndTheListingIsThePacks()
    {
        using var folder = new TempFolder();
        byte[] text = Text(10_000);
        byte[] noise = Noise(5_000);
        string path = Write(folder, "game.pack", null, ("readme.txt", text), ("textures/noise.bin", noise), ("textures/sub/tiny.txt", "hi"u8.ToArray()), ("empty.dat", []));

        using var pack = PackSource.Open(path);

        Assert.Equal((4, 1, false), (pack.FileCount, pack.PartCount, pack.IsEncrypted));
        Assert.Equal(text, pack.ReadAllBytes("readme.txt"));
        Assert.Equal(noise, pack.ReadAllBytes("textures/noise.bin"));
        Assert.Equal("hi", Encoding.UTF8.GetString(pack.ReadAllBytes("textures/sub/tiny.txt")));
        Assert.Empty(pack.ReadAllBytes("empty.dat"));
        Assert.Equal((FileKind.File, FileKind.Directory, FileKind.Directory, FileKind.None), (pack.Kind("readme.txt"), pack.Kind("textures"), pack.Kind("textures/sub"), pack.Kind("nope")));
        var entries = new List<(string Name, FileKind Kind)>();
        pack.List("textures", entries);
        Assert.Equal([("noise.bin", FileKind.File), ("sub", FileKind.Directory)], entries.OrderBy(e => e.Name, StringComparer.Ordinal));
        Assert.Throws<FileNotFoundException>(() => pack.ReadAllBytes("nope"));
    }

    [Fact]
    public void OnlyWhatCompressesWellIsCompressedAndAPackIsSmallerThanItsFiles()
    {
        using var folder = new TempFolder();
        byte[] text = Text(200_000);
        byte[] noise = Noise(50_000);
        string path = Write(folder, "game.pack", null, ("text.txt", text), ("noise.bin", noise));

        long size = new FileInfo(path).Length;

        Assert.True(size < 120_000, $"{size} bytes for 250000 (the text compresses, the noise is stored)");
        Assert.True(size > 50_000);
    }

    [Fact]
    public void ThePackMountsInTheFileSystemLikeAFolder()
    {
        using var folder = new TempFolder();
        string path = Write(folder, "game.pack", null, ("packages/core/mod.xml", "<Mod/>"u8.ToArray()), ("packages/core/data/a.xml", Text(500)));
        using var pack = PackSource.Open(path);
        var files = new VirtualFileSystem();
        files.Mount("res://", pack);

        Assert.Equal("<Mod/>", files.ReadAllText("res://packages/core/mod.xml"));
        Assert.Equal(["res://packages/core"], files.EnumerateDirectories("res://packages"));
        Assert.Equal(["res://packages/core/data/a.xml", "res://packages/core/mod.xml"], files.EnumerateFiles("res://", recursive: true));
        Assert.Null(files.TryGetPhysicalPath("res://packages/core/mod.xml"));
    }

    [Fact]
    public void AStoredFileIsStreamedWithoutReadingItWhole()
    {
        using var folder = new TempFolder();
        byte[] noise = Noise(8_000, 3);
        string path = Write(folder, "game.pack", null, ("noise.bin", noise), ("text.txt", Text(8_000)));
        using var pack = PackSource.Open(path);

        using (Stream stream = pack.OpenRead("noise.bin"))
        {
            Assert.Equal(8_000, stream.Length);
            stream.Seek(1_000, SeekOrigin.Begin);
            byte[] part = new byte[100];
            stream.ReadExactly(part);
            Assert.Equal(noise[1_000..1_100], part);
            stream.Seek(-10, SeekOrigin.End);
            Assert.Equal(10, stream.Read(new byte[50]));
            Assert.Equal(0, stream.Read(new byte[50]));
        }

        using Stream compressed = pack.OpenRead("text.txt");
        Assert.Equal(8_000, compressed.Length);
    }

    [Fact]
    public void WithAKeyTheContentIsSealedAndTheMagicIsNotRecognizable()
    {
        RequireAes();
        using var folder = new TempFolder();
        byte[] secret = Noise(4_000, 9);
        string plain = Write(folder, "plain.pack", null, ("secret.bin", secret), ("readme.txt", Text(3_000)));
        string sealedPack = Write(folder, "sealed.pack", new PackWriterOptions { Key = s_key }, ("secret.bin", secret), ("readme.txt", Text(3_000)));

        byte[] plainBytes = File.ReadAllBytes(plain);
        byte[] sealedBytes = File.ReadAllBytes(sealedPack);

        Assert.Equal("APGK", Encoding.ASCII.GetString(plainBytes, 0, 4));
        Assert.NotEqual("APGK", Encoding.ASCII.GetString(sealedBytes, 0, 4));
        Assert.NotEqual(sealedBytes[..4], File.ReadAllBytes(Write(folder, "other.pack", new PackWriterOptions { Key = s_otherKey }, ("a", [1])))[..4]);
        Assert.True(plainBytes.AsSpan().IndexOf(secret) >= 0);
        Assert.True(sealedBytes.AsSpan().IndexOf(secret) < 0);
        Assert.True(sealedBytes.AsSpan().IndexOf(secret.AsSpan(0, 64)) < 0);

        using var pack = PackSource.Open(sealedPack, s_key);
        Assert.True(pack.IsEncrypted);
        Assert.Equal(secret, pack.ReadAllBytes("secret.bin"));
        Assert.Equal(Text(3_000), pack.ReadAllBytes("readme.txt"));
    }

    [Fact]
    public void AKeyOnAPlainPackIsIgnoredAndTwoPacksOfTheSameFilesAreNotTheSameBytes()
    {
        RequireAes();
        using var folder = new TempFolder();
        byte[] data = Noise(2_000, 4);
        string plain = Write(folder, "plain.pack", null, ("a.bin", data));
        string one = Write(folder, "one.pack", new PackWriterOptions { Key = s_key }, ("a.bin", data));
        string two = Write(folder, "two.pack", new PackWriterOptions { Key = s_key }, ("a.bin", data));

        using (var pack = PackSource.Open(plain, s_key))
        {
            Assert.Equal(data, pack.ReadAllBytes("a.bin"));
        }

        Assert.NotEqual(File.ReadAllBytes(one), File.ReadAllBytes(two)); // a salt per pack and a nonce per file
        using var first = PackSource.Open(one, s_key);
        using var second = PackSource.Open(two, s_key);
        Assert.Equal(first.ReadAllBytes("a.bin"), second.ReadAllBytes("a.bin"));
    }

    [Fact]
    public void WithoutTheKeyOrWithAWrongOneOpeningSaysSo()
    {
        RequireAes();
        using var folder = new TempFolder();
        string path = Write(folder, "sealed.pack", new PackWriterOptions { Key = s_key }, ("a.bin", Noise(100)));

        Assert.Contains(PackKey.EnvironmentVariable, Assert.Throws<IOException>(() => PackSource.Open(path)).Message);
        Assert.Contains("the key is wrong", Assert.Throws<IOException>(() => PackSource.Open(path, s_otherKey)).Message);
        Assert.DoesNotContain("1f1e1d1c", Assert.Throws<IOException>(() => PackSource.Open(path, s_otherKey)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedByteInASealedPackIsFound()
    {
        RequireAes();
        using var folder = new TempFolder();
        string path = Write(folder, "sealed.pack", new PackWriterOptions { Key = s_key }, ("a.bin", Noise(1_000, 5)), ("b.bin", Noise(1_000, 6)));
        byte[] bytes = File.ReadAllBytes(path);
        byte[] changed = [.. bytes];
        changed[200] ^= 0x01; // inside the first file's data

        File.WriteAllBytes(path, changed);
        using (var pack = PackSource.Open(path, s_key))
        {
            Assert.Contains("a.bin", Assert.Throws<IOException>(() => pack.ReadAllBytes("a.bin")).Message);
            Assert.Equal(1_000, pack.ReadAllBytes("b.bin").Length); // the other file is fine
        }

        changed = [.. bytes];
        changed[^5] ^= 0x01; // inside the index
        File.WriteAllBytes(path, changed);
        Assert.Throws<IOException>(() => PackSource.Open(path, s_key));
    }

    [Fact]
    public void ChangedBytesInAPlainPackAreFoundByTheirChecksumsAndHashes()
    {
        using var folder = new TempFolder();
        string path = Write(folder, "game.pack", null, ("noise.bin", Noise(1_000, 7)), ("text.txt", Text(5_000)));
        byte[] bytes = File.ReadAllBytes(path);

        byte[] changed = [.. bytes];
        changed[PackFormatHeader + 10] ^= 0x01; // inside the stored (noise) file: its hash no longer matches
        File.WriteAllBytes(path, changed);
        using (var pack = PackSource.Open(path))
        {
            Assert.Contains("noise.bin", Assert.Throws<IOException>(() => pack.ReadAllBytes("noise.bin")).Message);
        }

        changed = [.. bytes];
        changed[PackFormatHeader + 1_010] ^= 0x40; // inside the compressed text file
        File.WriteAllBytes(path, changed);
        using (var pack = PackSource.Open(path))
        {
            Assert.Throws<IOException>(() => pack.ReadAllBytes("text.txt"));
            Assert.Equal(1_000, pack.ReadAllBytes("noise.bin").Length);
        }
    }

    private const int PackFormatHeader = 80;

    [Fact]
    public void ACutOffOrForeignFileIsNotAPack()
    {
        using var folder = new TempFolder();
        string path = Write(folder, "game.pack", null, ("a.bin", Noise(10_000)));
        byte[] bytes = File.ReadAllBytes(path);

        File.WriteAllBytes(path, bytes[..(bytes.Length - 20)]);
        Assert.Throws<IOException>(() => PackSource.Open(path));
        File.WriteAllBytes(path, bytes[..40]);
        Assert.Contains("is not a pack", Assert.Throws<IOException>(() => PackSource.Open(path)).Message);
        File.WriteAllBytes(path, Noise(500));
        Assert.Throws<IOException>(() => PackSource.Open(path));
        Assert.Throws<IOException>(() => PackSource.Open(Path.Combine(folder.Root, "missing.pack")));
    }

    [Fact]
    public void ABigPackIsSplitIntoPartsThatAreAllNeeded()
    {
        using var folder = new TempFolder();
        var files = Enumerable.Range(0, 12).Select(i => ($"data/f{i:D2}.bin", Noise(8_000, 100 + i))).ToArray();
        string path = Path.Combine(folder.Root, "game.pack");
        using (var writer = new PackWriter(path, new PackWriterOptions { SplitBytes = 30_000 }))
        {
            foreach ((string file, byte[] data) in files)
            {
                writer.Add(file, data);
            }

            Assert.True(writer.Finish().Count >= 3);
        }

        Assert.True(File.Exists(Path.Combine(folder.Root, "game_001.pack")));
        using (var pack = PackSource.Open(path))
        {
            Assert.True(pack.PartCount >= 3);
            Assert.Equal(12, pack.FileCount);
            foreach ((string file, byte[] data) in files)
            {
                Assert.Equal(data, pack.ReadAllBytes(file));
            }
        }

        int last = Directory.EnumerateFiles(folder.Root, "game_*.pack").Count();
        File.Delete(Path.Combine(folder.Root, $"game_{last:D3}.pack"));
        Assert.Contains($"part {last} of the pack is missing", Assert.Throws<IOException>(() => PackSource.Open(path)).Message);
    }

    [Fact]
    public void AFileBiggerThanThePartSizeGetsAPartOfItsOwnAndNothingIsSplit()
    {
        using var folder = new TempFolder();
        byte[] big = Noise(50_000, 11);
        string path = Path.Combine(folder.Root, "game.pack");
        using (var writer = new PackWriter(path, new PackWriterOptions { SplitBytes = 10_000 }))
        {
            writer.Add("small1", Noise(100, 1));
            writer.Add("big", big);
            writer.Add("small2", Noise(100, 2));
            writer.Finish();
        }

        using var pack = PackSource.Open(path);
        Assert.Equal(3, pack.PartCount);
        Assert.Equal(big, pack.ReadAllBytes("big"));
    }

    [Fact]
    public void ASplitSealedPackNeedsOneKeyForAllItsParts()
    {
        RequireAes();
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Root, "game.pack");
        using (var writer = new PackWriter(path, new PackWriterOptions { SplitBytes = 12_000, Key = s_key }))
        {
            for (int i = 0; i < 6; i++)
            {
                writer.Add($"f{i}", Noise(6_000, i));
            }

            writer.Finish();
        }

        using var pack = PackSource.Open(path, s_key);
        Assert.True(pack.PartCount > 1);
        Assert.Equal(Noise(6_000, 5), pack.ReadAllBytes("f5"));
    }

    [Fact]
    public void APatchPackReplacesAddsAndRemovesFilesOfThePacksBelowIt()
    {
        using var folder = new TempFolder();
        string basePath = Write(folder, "game.pack", null, ("a.txt", "base a"u8.ToArray()), ("b.txt", "base b"u8.ToArray()), ("dir/c.txt", "base c"u8.ToArray()), ("dir/d.txt", "base d"u8.ToArray()));
        string patchPath = Path.Combine(folder.Root, "patch.pack");
        using (var writer = new PackWriter(patchPath))
        {
            writer.Add("a.txt", "patched a"u8.ToArray());
            writer.Add("new.txt", "brand new"u8.ToArray());
            writer.Remove("b.txt");
            writer.Remove("dir/c.txt");
            writer.Finish();
        }

        using var baseGame = PackSource.Open(basePath);
        using var patch = PackSource.Open(patchPath);
        var files = new VirtualFileSystem();
        files.Mount("res://", baseGame);
        files.Mount("res://", patch);

        Assert.Equal("patched a", files.ReadAllText("res://a.txt"));
        Assert.Equal("brand new", files.ReadAllText("res://new.txt"));
        Assert.False(files.FileExists("res://b.txt"));
        Assert.False(files.FileExists("res://dir/c.txt"));
        Assert.Equal("base d", files.ReadAllText("res://dir/d.txt"));
        Assert.Equal(["res://a.txt", "res://new.txt"], files.EnumerateFiles("res://"));
        Assert.Equal(["res://dir/d.txt"], files.EnumerateFiles("res://dir"));
        Assert.Equal(2, patch.FileCount);
        Assert.Throws<FileNotFoundException>(() => patch.ReadAllBytes("b.txt"));
    }

    [Fact]
    public void AnEmptyPackIsAPack()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Root, "empty.pack");
        using (var writer = new PackWriter(path))
        {
            Assert.Single(writer.Finish());
        }

        using var pack = PackSource.Open(path);
        Assert.Equal(0, pack.FileCount);
    }

    [Fact]
    public void ThePathsOfAPackAreCheckedLikeAnyVirtualPath()
    {
        using var folder = new TempFolder();
        using var writer = new PackWriter(Path.Combine(folder.Root, "x.pack"));

        writer.Add("ok.txt", [1]);

        Assert.Throws<ArgumentException>(() => writer.Add("ok.txt", [2]));
        Assert.Throws<ArgumentException>(() => writer.Remove("ok.txt"));
        Assert.Throws<VfsException>(() => writer.Add("../evil.txt", [1]));
        Assert.Throws<VfsException>(() => writer.Add("a//b", [1]));
        Assert.Throws<VfsException>(() => writer.Add("nul", [1]));
        Assert.Throws<ArgumentException>(() => writer.Add("", [1]));
        writer.Finish();
        Assert.Throws<InvalidOperationException>(() => writer.Add("late.txt", [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackWriter("y.pack", new PackWriterOptions { CompressionLevel = 0 }));
    }

    [Fact]
    public void ManyThreadsCanReadAPackAtOnce()
    {
        using var folder = new TempFolder();
        var files = Enumerable.Range(0, 30).Select(i => ($"f{i}", i % 2 == 0 ? Text(5_000 + i) : Noise(3_000 + i, i))).ToArray();
        string path = Write(folder, "game.pack", null, files);
        using var pack = PackSource.Open(path);

        System.Threading.Tasks.Parallel.For(0, 200, i =>
        {
            (string file, byte[] data) = files[i % files.Length];
            Assert.Equal(data, pack.ReadAllBytes(file));
        });
    }
}
