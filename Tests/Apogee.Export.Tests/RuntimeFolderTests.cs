using System;
using System.IO;
using Xunit;

namespace Apogee.Export.Tests;

public class RuntimeFolderTests
{
    [Theory]
    [InlineData("linux", "x64")]
    [InlineData("linux", "arm64")]
    [InlineData("win", "x64")]
    [InlineData("win", "arm64")]
    [InlineData("osx", "x64")]
    [InlineData("osx", "arm64")]
    public void TheRidIsReadFromTheExecutableHeader(string os, string arch)
    {
        using var scratch = new Scratch();
        var target = new ExportTarget(os, arch);
        string file = scratch.Write("program", FakeRuntime.Header(target));

        Assert.Equal(target, RuntimeFolder.Detect(file));
    }

    [Fact]
    public void WhatIsNotAProgramOrIsUniversalIsRefused()
    {
        using var scratch = new Scratch();
        string text = scratch.Write("readme", "just text, long enough to hold a header if it had one........................................");
        Assert.Contains("not a Windows (PE), Linux (ELF) or macOS (Mach-O) program", Assert.Throws<ExportException>(() => RuntimeFolder.Detect(text)).Message, StringComparison.Ordinal);

        byte[] fat = new byte[64];
        fat[0] = 0xCA;
        fat[1] = 0xFE;
        fat[2] = 0xBA;
        fat[3] = 0xBE;
        string universal = scratch.Write("fat", fat);
        Assert.Contains("thin binary expected", Assert.Throws<ExportException>(() => RuntimeFolder.Detect(universal)).Message, StringComparison.Ordinal);

        byte[] other = FakeRuntime.Header(new ExportTarget("linux", "x64"));
        other[18] = 0x08; // MIPS
        other[19] = 0;
        string mips = scratch.Write("mips", other);
        Assert.Contains("processor the engine does not support", Assert.Throws<ExportException>(() => RuntimeFolder.Detect(mips)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderOfTheRightRidIsAccepted()
    {
        using var scratch = new Scratch();
        string folder = FakeRuntime.Make(scratch, new ExportTarget("win", "x64"));

        Assert.Equal(folder, RuntimeFolder.Resolve(folder, new ExportTarget("win", "x64")));
    }

    [Fact]
    public void AFolderOfAnotherRidIsRefusedWithWhatToDo()
    {
        using var scratch = new Scratch();
        string folder = FakeRuntime.Make(scratch, new ExportTarget("linux", "x64"));

        string message = Assert.Throws<ExportException>(() => RuntimeFolder.Resolve(folder, new ExportTarget("win", "x64"))).Message;

        Assert.Contains("holds a linux-x64 runtime", message, StringComparison.Ordinal);
        Assert.Contains("--target win-x64 needs one published for win-x64", message, StringComparison.Ordinal);
        Assert.Contains("apogee-win-x64", message, StringComparison.Ordinal);
        Assert.Contains("--runtime <dir>", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderWithoutTheProgramOrNoFolderIsRefused()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(scratch.Path("empty"));

        Assert.Contains("is not a published runtime", Assert.Throws<ExportException>(() => RuntimeFolder.Resolve(scratch.Path("empty"), new ExportTarget("linux", "x64"))).Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", Assert.Throws<ExportException>(() => RuntimeFolder.Resolve(scratch.Path("nothing"), new ExportTarget("linux", "x64"))).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Apogee.Runtime", true)]
    [InlineData("Apogee.Runtime.exe", true)]
    [InlineData("libflecs.so", true)]
    [InlineData("flecs.dll", true)]
    [InlineData("Shaders/imported/x.spv", true)]
    [InlineData("Shaders/imported/sub/y.spv", true)]
    [InlineData("Apogee.Runtime.pdb", false)]
    [InlineData("Apogee.Runtime.dbg", false)]
    [InlineData("Apogee.Runtime.dSYM/Contents/Info.plist", false)]
    [InlineData("Apogee.Runtime.lib", false)]
    [InlineData("Apogee.Runtime.exp", false)]
    [InlineData("libslang.so", false)]
    [InlineData("slang-glslang.dll", false)]
    [InlineData("libslang-rt.dylib", false)]
    [InlineData("Shaders/a.slang", false)]
    [InlineData("Shaders\\lighting\\b.slang", false)]
    [InlineData("Shaders/forward.slang.import.xml", false)]
    [InlineData("Shaders/lighting/c.slang.import.xml", false)]
    [InlineData("Shaders/imported/forward.slang.import.xml", true)]
    [InlineData("steam_appid.txt", false)]
    [InlineData("game.xml", false)]
    [InlineData("data/game.pack", false)]
    [InlineData("data", true)]
    [InlineData("Shaders/notes.txt", true)]
    public void DebugFilesTheCompilerAndShaderSourcesDoNotShip(string path, bool ships) => Assert.Equal(ships, RuntimeFolder.Ships(path));
}
