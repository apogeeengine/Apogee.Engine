using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content;
using Apogee.Content.Packages;
using Apogee.Content.Packs;
using Xunit;

namespace Apogee.Export.Tests;

public class MacPackagingTests
{
    private static readonly ExportTarget s_arm64 = new("osx", "arm64");
    private static readonly ExportTarget s_x64 = new("osx", "x64");

    private const string GameXml = """<Game name="Fixture Game" organization="Test Studio" version="1.2.3" executable="FixtureGame" identifier="org.test.fixturegame" copyright="Copyright 2026 Test Studio"><Icon path="icon.png"/></Game>""";

    private static readonly byte[] s_keyBytes = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();

    /// <summary>A thin dylib with only an <c>LC_BUILD_VERSION</c>, as the dylibs of a runtime have.</summary>
    private static byte[] Dylib(uint minimumOs, uint cpuType) => SyntheticMachO.Build(0x1000, [new(2, 12)], minimumOs: minimumOs, cpuType: cpuType).Bytes;

    /// <summary>A game folder with a manifest and a <paramref name="iconSize"/> pixel icon, and a published runtime for <paramref name="target"/> whose program is <paramref name="program"/>.</summary>
    private static (string Game, string Runtime) Make(Scratch scratch, ExportTarget target, byte[] program, int iconSize = 256)
    {
        scratch.Write("game/game.xml", GameXml);
        scratch.Write("game/icon.png", Png.Encode(iconSize, iconSize, Enumerable.Repeat((byte)200, iconSize * iconSize * 4).ToArray()));
        scratch.Write("game/main.luau", "print('hi')");
        string runtime = FakeRuntime.Make(scratch, target);
        scratch.Write("runtime/Apogee.Runtime", program);
        return (scratch.Path("game"), runtime);
    }

    private static ExportResult Export(Scratch scratch, ExportTarget target, byte[] program, PackKey? key = null, int iconSize = 256)
    {
        (string game, string runtime) = Make(scratch, target, program, iconSize);
        return GameExporter.Export(new ExportOptions { GameFolder = game, OutputFolder = scratch.Path("out"), Target = target, RuntimeDirectory = runtime, Key = key });
    }

    private static PlatformPackagingContext ContextFor(Scratch scratch, ExportTarget target, byte[] program, byte[]? icon = null)
    {
        string output = scratch.Path("out");
        Assert.True(GameManifest.TryParse(GameXml, "game.xml", out GameManifest? manifest, out _));
        ExportLayout layout = ExportLayout.For(target, manifest!, output);
        Directory.CreateDirectory(layout.GameRoot);
        Directory.CreateDirectory(layout.ExecutableDirectory);
        File.WriteAllBytes(layout.ExecutablePath, program);
        return new PlatformPackagingContext
        {
            Options = new ExportOptions { GameFolder = scratch.Path("game"), OutputFolder = output, Target = target },
            Manifest = manifest!,
            Layout = layout,
            IconFile = icon,
            Warnings = [],
            Log = static _ => { },
        };
    }

    [Fact]
    public void AMacExportIsABundle()
    {
        using var scratch = new Scratch();

        ExportResult result = Export(scratch, s_arm64, FakeRuntime.Executable(s_arm64));

        string contents = Path.GetFullPath(scratch.Path("out/Fixture Game.app/Contents")); // with the OS's separators, as the result has
        Assert.Equal(Path.Combine(contents, "MacOS", "FixtureGame"), result.Executable);
        Assert.Equal(Path.Combine(contents, "Resources"), result.GameRoot);
        Assert.Equal(
            [
                "Fixture Game.app/Contents/Info.plist",
                "Fixture Game.app/Contents/MacOS/FixtureGame",
                "Fixture Game.app/Contents/MacOS/Shaders/imported/x.spv",
                "Fixture Game.app/Contents/MacOS/libflecs.dylib",
                "Fixture Game.app/Contents/PkgInfo",
                "Fixture Game.app/Contents/Resources/data/game.pack",
                "Fixture Game.app/Contents/Resources/game.icns",
                "Fixture Game.app/Contents/Resources/game.xml",
            ],
            result.Files);
        Assert.Equal("APPL????", File.ReadAllText(Path.Combine(contents, "PkgInfo")));
        Assert.Contains("<key>CFBundleIconFile</key>", File.ReadAllText(Path.Combine(contents, "Info.plist")), StringComparison.Ordinal);

        // The bundle holds the icon, so the exported game.xml names none (the runtime draws no window icon on macOS, the bundle's is the app's).
        GameManifest exported = GameManifest.Load(result.GameRoot, out IReadOnlyList<string> problems)!;
        Assert.Empty(problems);
        Assert.Null(exported.Icon);
    }

    [Fact]
    public void TheIcnsHasTheIconInIt()
    {
        using var scratch = new Scratch();

        Export(scratch, s_arm64, FakeRuntime.Executable(s_arm64), iconSize: 512);

        byte[] icns = File.ReadAllBytes(scratch.Path("out/Fixture Game.app/Contents/Resources/game.icns"));
        Assert.Equal("icns"u8.ToArray(), icns[..4]);
        Assert.Equal(icns.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(icns.AsSpan(4)));
        Assert.Equal("icp4"u8.ToArray(), icns[8..12]);
    }

    [Fact]
    public void AGameWithoutAnIconHasNoIcnsAndNoIconKey()
    {
        using var scratch = new Scratch();
        PlatformPackagingContext context = ContextFor(scratch, s_x64, FakeRuntime.Executable(s_x64), icon: null);

        new MacPackaging().Apply(context);

        Assert.False(File.Exists(Path.Combine(context.Layout.GameRoot, "game.icns")));
        Assert.DoesNotContain("CFBundleIconFile", File.ReadAllText(Path.Combine(context.Layout.Bundle!, "Contents", "Info.plist")), StringComparison.Ordinal);
        Assert.Null(context.ExportedIcon);
    }

    [Fact]
    public void ASmallIconIsWarnedAbout()
    {
        using var scratch = new Scratch();
        PlatformPackagingContext context = ContextFor(scratch, s_x64, FakeRuntime.Executable(s_x64), icon: Png.Encode(128, 128, new byte[128 * 128 * 4]));

        new MacPackaging().Apply(context);

        Assert.Contains(context.Warnings, w => w.Contains("128x128", StringComparison.Ordinal));
        Assert.Contains(context.Warnings, w => w.Contains("1024x1024", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIconThatIsNotSquareFailsTheExport()
    {
        using var scratch = new Scratch();
        PlatformPackagingContext context = ContextFor(scratch, s_x64, FakeRuntime.Executable(s_x64), icon: Png.Encode(8, 4, new byte[8 * 4 * 4]));

        Assert.Contains("square", Assert.Throws<ExportException>(() => new MacPackaging().Apply(context)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExecutableIsExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("a Windows host has no Unix file modes");
            return;
        }

        using var scratch = new Scratch();

        ExportResult result = Export(scratch, s_x64, FakeRuntime.Executable(s_x64));

        const UnixFileMode Expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        Assert.Equal(Expected, File.GetUnixFileMode(result.Executable));
    }

    [Fact]
    public void AWindowsHostWarnsThatTheExecutableBitIsNotSet()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("only a Windows host has no Unix file modes");
            return;
        }

        using var scratch = new Scratch();

        ExportResult result = Export(scratch, s_x64, FakeRuntime.Executable(s_x64));

        Assert.Contains(result.Warnings, w => w.Contains("macOS", StringComparison.Ordinal) && w.Contains("executable bit", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSignatureIsRightAfterTheKeyIsWrittenIntoTheExecutable()
    {
        using var scratch = new Scratch();
        SyntheticMachO program = SyntheticMachO.Build(0x8000, [new(2, 14), new(1, 12)], embed: PackKeySlot.Template.ToArray(), cpuType: 0x0100000C);

        ExportResult result = Export(scratch, s_arm64, program.Bytes, new PackKey(s_keyBytes));

        byte[] exported = File.ReadAllBytes(result.Executable);
        Assert.Equal(program.Bytes.Length, exported.Length);
        Assert.False(exported.AsSpan().SequenceEqual(program.Bytes)); // the key is in
        Assert.True(program.HashesAreRight(exported)); // and the page hashes are those of the program as it is now
        Assert.False(program.HashesAreRight(AsExportedWithoutRehash(program, exported))); // (they were not when only the key was written)

        // The key is in the slot, and reads back.
        PackKey? key = PackKeySlot.Decode(exported.AsSpan(FakeRuntime.SlotOffset(exported), 68));
        Assert.Equal(s_keyBytes, key!.Bytes.ToArray());
        Assert.DoesNotContain(result.Warnings, w => w.Contains("signature", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSignatureIsRightAfterTheKeyIsWrittenIntoBothCopiesOfTheSlot()
    {
        using var scratch = new Scratch();
        byte[] template = PackKeySlot.Template.ToArray();
        SyntheticMachO program = SyntheticMachO.Build(0x8000, [new(2, 14), new(1, 12)], embed: [.. template, .. new byte[0x4100], .. template], cpuType: 0x0100000C);

        ExportResult result = Export(scratch, s_arm64, program.Bytes, new PackKey(s_keyBytes));

        byte[] exported = File.ReadAllBytes(result.Executable);
        Assert.True(program.HashesAreRight(exported));
        Assert.False(program.HashesAreRight(AsExportedWithoutRehash(program, exported)));
        foreach (int at in new[] { 0x400, 0x400 + 68 + 0x4100 })
        {
            Assert.Equal(s_keyBytes, PackKeySlot.Decode(exported.AsSpan(at, 68))!.Bytes.ToArray());
        }

        Assert.DoesNotContain(result.Warnings, w => w.Contains("signature", StringComparison.Ordinal));
    }

    /// <summary>The exported program with the signature's hashes put back as they were in the runtime: what the file would be had only the key been written.</summary>
    private static byte[] AsExportedWithoutRehash(SyntheticMachO original, byte[] exported)
    {
        byte[] copy = (byte[])exported.Clone();
        original.Bytes.AsSpan(original.SignatureOffset).CopyTo(copy.AsSpan(original.SignatureOffset));
        return copy;
    }

    [Fact]
    public void AnExportWithoutAKeyLeavesTheSignedProgramAsItWas()
    {
        using var scratch = new Scratch();
        SyntheticMachO program = SyntheticMachO.Build(0x8000, [new(2, 14)], embed: PackKeySlot.Template.ToArray());

        ExportResult result = Export(scratch, s_arm64, program.Bytes);

        Assert.Equal(program.Bytes, File.ReadAllBytes(result.Executable));
    }

    [Fact]
    public void AProgramSignedWithACertificateFailsTheExportWithWhatToDo()
    {
        using var scratch = new Scratch();
        SyntheticMachO program = SyntheticMachO.Build(0x8000, [new(2, 14)], embed: PackKeySlot.Template.ToArray(), wrapperLength: 16);

        ExportException e = Assert.Throws<ExportException>(() => Export(scratch, s_arm64, program.Bytes, new PackKey(s_keyBytes)));

        Assert.Contains("sign the bundle after the export", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArm64ProgramWithoutASignatureIsWarnedAbout()
    {
        using var scratch = new Scratch();

        ExportResult arm = Export(scratch, s_arm64, FakeRuntime.Executable(s_arm64));

        Assert.Contains(arm.Warnings, w => w.Contains("no code signature", StringComparison.Ordinal) && w.Contains("Apple silicon", StringComparison.Ordinal));
    }

    [Fact]
    public void AnX64ProgramWithoutASignatureIsFine()
    {
        using var scratch = new Scratch();

        ExportResult result = Export(scratch, s_x64, FakeRuntime.Executable(s_x64));

        Assert.DoesNotContain(result.Warnings, w => w.Contains("signature", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMinimumSystemVersionIsTheNewestOneTheProgramAndItsLibrariesNeed()
    {
        using var scratch = new Scratch();
        PlatformPackagingContext context = ContextFor(scratch, s_arm64, SyntheticMachO.Build(0x2000, [new(2, 14)], minimumOs: 0x000B0000).Bytes);
        File.WriteAllBytes(Path.Combine(context.Layout.ExecutableDirectory, "libold.dylib"), Dylib(0x000B0000, MachOFile.CpuTypeArm64));
        File.WriteAllBytes(Path.Combine(context.Layout.ExecutableDirectory, "libnew.dylib"), Dylib(0x000D0301, MachOFile.CpuTypeArm64));
        File.WriteAllBytes(Path.Combine(context.Layout.ExecutableDirectory, "libother.dylib"), Dylib(0x000F0000, MachOFile.CpuTypeX64)); // another processor: not looked at
        File.WriteAllBytes(Path.Combine(context.Layout.ExecutableDirectory, "libbroken.dylib"), [1, 2, 3]); // not a Mach-O file: skipped

        new MacPackaging().Apply(context);

        Assert.Contains("<key>LSMinimumSystemVersion</key>\n\t\t<string>13.3.1</string>", File.ReadAllText(Path.Combine(context.Layout.Bundle!, "Contents", "Info.plist")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMinimumSystemVersionIsAtLeastElevenAndElevenWhenNothingSaysOtherwise()
    {
        using var scratch = new Scratch();
        PlatformPackagingContext older = ContextFor(scratch, s_x64, SyntheticMachO.Build(0x2000, [new(2, 12)], minimumOs: 0x000A0F00, cpuType: MachOFile.CpuTypeX64).Bytes);
        new MacPackaging().Apply(older);
        Assert.Contains("<string>11.0</string>", File.ReadAllText(Path.Combine(older.Layout.Bundle!, "Contents", "Info.plist")), StringComparison.Ordinal);

        using var other = new Scratch();
        PlatformPackagingContext none = ContextFor(other, s_x64, FakeRuntime.Executable(s_x64));
        new MacPackaging().Apply(none);
        Assert.Contains("<string>11.0</string>", File.ReadAllText(Path.Combine(none.Layout.Bundle!, "Contents", "Info.plist")), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePlatformStepNeedsABundleLayout()
    {
        using var scratch = new Scratch();
        PlatformPackagingContext context = ContextFor(scratch, s_x64, FakeRuntime.Executable(s_x64));
        PlatformPackagingContext flat = new()
        {
            Options = context.Options,
            Manifest = context.Manifest,
            Layout = ExportLayout.For(new ExportTarget("linux", "x64"), context.Manifest, scratch.Path("flat")),
            Warnings = [],
            Log = static _ => { },
        };

        Assert.Throws<ExportException>(() => new MacPackaging().Apply(flat));
    }

    [Fact]
    public void ABuildVersionIsReadFromAUniversalBinarysSliceForTheProcessor()
    {
        byte[] arm = Dylib(0x000B0000, MachOFile.CpuTypeArm64);
        byte[] x64 = Dylib(0x000A0F00, MachOFile.CpuTypeX64);
        byte[] fat = new byte[8 + 40];
        BinaryPrimitives.WriteUInt32BigEndian(fat, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4), 2);
        int offset = 0x4000;
        foreach ((uint cpu, byte[] image, int entry) in new[] { (MachOFile.CpuTypeX64, x64, 8), (MachOFile.CpuTypeArm64, arm, 28) })
        {
            BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(entry), cpu);
            BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(entry + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(entry + 12), (uint)image.Length);
            offset += 0x4000;
        }

        byte[] file = new byte[0xC000];
        fat.CopyTo(file, 0);
        x64.CopyTo(file, 0x4000);
        arm.CopyTo(file, 0x8000);

        using var stream = new MemoryStream(file);
        Assert.Equal(0x000A0F00u, MachOBuildVersion.ReadFrom(stream, MachOFile.CpuTypeX64, "fat"));
        Assert.Equal(0x000B0000u, MachOBuildVersion.ReadFrom(stream, MachOFile.CpuTypeArm64, "fat"));
        Assert.Null(MachOBuildVersion.ReadFrom(stream, 0x12345, "fat"));
    }

    [Fact]
    public void AHostileUniversalBinaryHasNoVersionAndNoException()
    {
        byte[] fat = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(fat, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4), uint.MaxValue); // a count of architectures that cannot be
        using var many = new MemoryStream(fat);
        Assert.Null(MachOBuildVersion.ReadFrom(many, MachOFile.CpuTypeArm64, "fat"));

        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(8), MachOFile.CpuTypeArm64);
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(16), uint.MaxValue); // an offset beyond the file
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(20), uint.MaxValue);
        using var beyond = new MemoryStream(fat);
        Assert.Null(MachOBuildVersion.ReadFrom(beyond, MachOFile.CpuTypeArm64, "fat"));
    }
}
