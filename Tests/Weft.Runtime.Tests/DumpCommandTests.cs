using System;
using System.Buffers.Binary;
using System.IO;
using Weft.Content;
using Weft.Content.Formats;
using Weft.Content.Packs;
using Weft.Content.Textures;
using Weft.Native.Tests;
using Weft.Core.Assets;
using Weft.Runtime;
using Xunit;

namespace Weft.Runtime.Tests;

/// <summary><c>--dump</c>: the command line and what the command writes.</summary>
[Collection("gpu")]
public class DumpCommandTests
{
    private static RuntimeOptions Options(params string[] args)
    {
        Assert.True(RuntimeOptions.TryParse(args, out RuntimeOptions options, out string? problem), problem);
        return options;
    }

    private static (int Exit, string Out, string Error) Run(string file, string? ktx2 = null)
    {
        TextWriter oldOut = Console.Out;
        TextWriter oldError = Console.Error;
        var output = new StringWriter();
        var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            return (Commands.Dump(ktx2 is null ? Options("--dump", file) : Options("--dump", file, "--ktx2", ktx2)), output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }

    [Fact]
    public void DumpTakesAFile()
    {
        RuntimeOptions options = Options("--dump", "crate.wmdl");

        Assert.Equal((RuntimeCommand.Dump, "crate.wmdl"), (options.Command, options.Path));
        Assert.False(RuntimeOptions.TryParse(["--dump"], out _, out string? problem));
        Assert.Equal("--dump needs a value.", problem);
    }

    [Fact]
    public void DumpWritesTheHeaderAndTheBlocksOfAContainer()
    {
        string folder = Path.Combine(Path.GetTempPath(), "weft-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var writer = new BinaryAssetWriter(BinaryAsset.Fourcc("DUMP"), 2) { Uid = new AssetUid(0x1234) };
            writer.AddMeta(new byte[16]);
            writer.AddBlock(BinaryAsset.Fourcc("DATA"), new byte[100]);
            string file = Path.Combine(folder, "a.bin");
            File.WriteAllBytes(file, writer.ToArray());

            (int exit, string output, string error) = Run(file);

            Assert.Equal(0, exit);
            Assert.Equal("", error);
            Assert.Contains("DUMP, version 2", output, StringComparison.Ordinal);
            Assert.Contains(new AssetUid(0x1234).ToString(), output, StringComparison.Ordinal);
            Assert.Contains(" DATA ", output, StringComparison.Ordinal);
            Assert.Contains("META", output, StringComparison.Ordinal);

            Assert.Equal(1, Run(Path.Combine(folder, "missing.bin")).Exit);
            File.WriteAllBytes(file, new byte[10]);
            (int bad, _, string message) = Run(file);
            Assert.Equal(1, bad);
            Assert.Contains("a.bin", message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void DumpWritesATextureAndItsKtx2()
    {
        string folder = Path.Combine(Path.GetTempPath(), "weft-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var info = new WtexInfo { Width = 16, Height = 16, MipCount = 5, Payload = WtexPayload.Bc7, Flags = WtexTraits.Srgb };
            var levels = new ReadOnlyMemory<byte>[5];
            for (int level = 0; level < levels.Length; level++)
            {
                levels[level] = new byte[info.LevelSize(level)];
            }

            string file = Path.Combine(folder, "a.png.wtex");
            string ktx2 = Path.Combine(folder, "a.ktx2");
            File.WriteAllBytes(file, WtexFile.Write(info, levels, new WtexWriteOptions { Uid = new AssetUid(0x77) }));

            (int exit, string output, string error) = Run(file, ktx2);

            Assert.Equal(0, exit);
            Assert.Equal("", error);
            Assert.Contains("payload = Bc7", output, StringComparison.Ordinal);
            Assert.Contains(new AssetUid(0x77).ToString(), output, StringComparison.Ordinal);
            Assert.Contains("wrote " + ktx2, output, StringComparison.Ordinal);
            byte[] written = File.ReadAllBytes(ktx2);
            Assert.True(written.AsSpan().StartsWith(Ktx2Loader.Magic));
            Assert.Equal(146u, BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(12)));    // BC7 sRGB
            Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(20)));

            // Without --ktx2 nothing is written; the output may not be the input; and only a texture can be written as one.
            File.Delete(ktx2);
            Assert.Equal(0, Run(file).Exit);
            Assert.False(File.Exists(ktx2));
            Assert.Equal(1, Run(file, file).Exit);
            Assert.Equal(1, Run(file, Path.Combine(folder, ".", "a.png.wtex")).Exit);
            string other = Path.Combine(folder, "b.bin");
            var container = new BinaryAssetWriter(BinaryAsset.Fourcc("DUMP"), 1);
            container.AddMeta(new byte[16]);
            File.WriteAllBytes(other, container.ToArray());
            (int notTexture, _, string message) = Run(other, ktx2);
            Assert.Equal(1, notTexture);
            Assert.Contains("not a .wtex", message, StringComparison.Ordinal);
            Assert.False(File.Exists(ktx2));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Ktx2GoesWithDump()
    {
        Assert.Equal("a.ktx2", Options("--dump", "a.wtex", "--ktx2", "a.ktx2").Ktx2Output);
        Assert.Null(Options("--dump", "a.wtex").Ktx2Output);
        Assert.False(RuntimeOptions.TryParse(["--ktx2", "a.ktx2"], out _, out string? problem));
        Assert.Equal("--ktx2 goes with --dump.", problem);
        Assert.False(RuntimeOptions.TryParse(["--dump", "a.wtex", "--ktx2"], out _, out problem));
        Assert.Equal("--ktx2 needs a value.", problem);
    }

    [Fact]
    public void DumpListsAPackAndABadKeyInTheEnvironmentDoesNotStopIt()
    {
        NativeRequirements.Require(typeof(Weft.Native.Zstd.Zstd).Assembly, "zstd");
        string folder = Path.Combine(Path.GetTempPath(), "weft-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "src", "sub"));
        File.WriteAllText(Path.Combine(folder, "src", "a.txt"), "hello");
        File.WriteAllText(Path.Combine(folder, "src", "sub", "b.txt"), "world");
        string pack = Path.Combine(folder, "game.wpak");
        string? old = Environment.GetEnvironmentVariable(PackKey.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PackKey.EnvironmentVariable, null);
            Assert.Equal(0, Commands.Pack(Options("--pack", Path.Combine(folder, "src"), pack)));

            // A key that is not one is not looked at for a pack that has no key, and is never written out.
            Environment.SetEnvironmentVariable(PackKey.EnvironmentVariable, "this is not a key, it is a secret");
            (int exit, string output, string error) = Run(pack);

            Assert.Equal(0, exit);
            Assert.Contains("2 files", output, StringComparison.Ordinal);
            Assert.Contains("  a.txt", output, StringComparison.Ordinal);
            Assert.Contains("  sub/b.txt", output, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", output + error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PackKey.EnvironmentVariable, old);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ADamagedOrHostilePackIsRefusedWithExitCodeOne()
    {
        string folder = Path.Combine(Path.GetTempPath(), "weft-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string? old = Environment.GetEnvironmentVariable(PackKey.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(PackKey.EnvironmentVariable, null);
            string pack = Path.Combine(folder, "bad.wpak");
            File.WriteAllBytes(pack, new byte[200]);
            (int notPack, _, string notPackMessage) = Run(pack);
            Assert.Equal(1, notPack);
            Assert.Contains("bad.wpak", notPackMessage, StringComparison.Ordinal);

            // An index whose offset and length add up past the end of the world (and wrap around to a negative number).
            byte[] header = new byte[200];
            BinaryPrimitives.WriteUInt32LittleEndian(header, AssetFormatsMagic());
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), 1UL << 62);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), 1UL << 62);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), 100);
            File.WriteAllBytes(pack, header);
            (int hostile, _, string hostileMessage) = Run(pack);
            Assert.Equal(1, hostile);
            Assert.Contains("damaged", hostileMessage, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PackKey.EnvironmentVariable, old);
            Directory.Delete(folder, recursive: true);
        }
    }

    private static uint AssetFormatsMagic() => Weft.Content.Assets.AssetFormats.Pack.Magic;
}
