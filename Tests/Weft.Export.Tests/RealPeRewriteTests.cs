using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using Weft.Content;
using Weft.Content.Packages;
using Xunit;

namespace Weft.Export.Tests;

/// <summary>
/// The Windows step on executables a real linker made, not ones the tests synthesize: the engine's own vendored libraries, MSVC-linked PE32+ files with seven sections, the resources before the relocations,
/// and in <c>steam_api64.dll</c> an Authenticode signature and a resource type with a name. They are Git LFS files: a checkout without them skips these tests.
/// </summary>
public class RealPeRewriteTests
{
    private static readonly ExportTarget s_windows = new("win", "x64");

    /// <summary>The bytes of a vendored library, or null when the checkout has no real file there (no repository around the tests, or an LFS pointer).</summary>
    private static byte[]? Vendored(string relative)
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string file = Path.Combine(directory, "ThirdParty", relative);
            if (File.Exists(Path.Combine(directory, "Weft.slnx")) && File.Exists(file))
            {
                byte[] bytes = File.ReadAllBytes(file);
                return bytes.Length > 4096 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z' ? bytes : null;
            }
        }

        return null;
    }

    private static GameManifest Manifest()
    {
        const string Xml = """<Game name="Real Game" organization="Test Studio" version="2.0.1" executable="FixtureGame" description="Checks real files." copyright="(c) Test"/>""";
        Assert.True(GameManifest.TryParse(Xml, "game.xml", out GameManifest? manifest, out IReadOnlyList<string> problems), string.Join("\n", problems));
        return manifest!;
    }

    /// <summary>Puts <paramref name="input"/> where an export has it and runs the Windows step, with an icon.</summary>
    private static (byte[] Output, List<string> Warnings, string Path) Package(Scratch scratch, byte[] input, bool console = false)
    {
        string path = scratch.Write("out/FixtureGame.exe", input);
        GameManifest manifest = Manifest();
        byte[] rgba = new byte[256 * 256 * 4];
        Array.Fill(rgba, (byte)180);
        var context = new PlatformPackagingContext
        {
            Options = new ExportOptions { GameFolder = scratch.Path("game"), OutputFolder = scratch.Path("out"), Target = s_windows, Console = console },
            Manifest = manifest,
            Layout = ExportLayout.For(s_windows, manifest, scratch.Path("out")),
            IconFile = Png.Encode(256, 256, rgba),
            Warnings = [],
            Log = static _ => { },
        };
        new WindowsPackaging().Apply(context);
        return (File.ReadAllBytes(path), context.Warnings, path);
    }

    /// <summary>
    /// Everything that is not the resource section is as it was: the original sections' headers and bytes (the old resource section only renamed, and left as it was), the directories the program
    /// runs by (imports, exceptions, relocations, debug, load configuration), and every resource but the icon and version ones, byte for byte.
    /// </summary>
    private static void AssertTheRestIsUntouched(byte[] input, byte[] output)
    {
        using var before = new PEReader(new MemoryStream(input));
        using var after = new PEReader(new MemoryStream(output));
        var oldSections = before.PEHeaders.SectionHeaders;
        var newSections = after.PEHeaders.SectionHeaders;

        Assert.Equal(oldSections.Length + 1, newSections.Length); // the resource section is not last in these files: a new one is added after the last
        Assert.Equal(".rsrc", newSections[^1].Name);
        for (int i = 0; i < oldSections.Length; i++)
        {
            SectionHeader a = oldSections[i];
            SectionHeader b = newSections[i];
            Assert.Equal(a.Name == ".rsrc" ? ".rsrc0" : a.Name, b.Name);
            Assert.Equal((a.VirtualAddress, a.VirtualSize, a.PointerToRawData, a.SizeOfRawData, a.SectionCharacteristics), (b.VirtualAddress, b.VirtualSize, b.PointerToRawData, b.SizeOfRawData, b.SectionCharacteristics));
            Assert.True(input.AsSpan((int)a.PointerToRawData, a.SizeOfRawData).SequenceEqual(output.AsSpan((int)b.PointerToRawData, b.SizeOfRawData)), $"the bytes of section {a.Name} changed");
        }

        PEHeader x = before.PEHeaders.PEHeader!;
        PEHeader y = after.PEHeaders.PEHeader!;
        Assert.Equal((x.ImportTableDirectory, x.ExceptionTableDirectory, x.BaseRelocationTableDirectory, x.DebugTableDirectory, x.LoadConfigTableDirectory), (y.ImportTableDirectory, y.ExceptionTableDirectory, y.BaseRelocationTableDirectory, y.DebugTableDirectory, y.LoadConfigTableDirectory));
        Assert.Equal((x.AddressOfEntryPoint, x.ImageBase, x.SectionAlignment, x.FileAlignment, x.SizeOfHeaders), (y.AddressOfEntryPoint, y.ImageBase, y.SectionAlignment, y.FileAlignment, y.SizeOfHeaders));

        Dictionary<string, byte[]> resourcesBefore = TestResources.Read(input).ToDictionary(r => r.Key, r => r.Data);
        Dictionary<string, byte[]> resourcesAfter = TestResources.Read(output).ToDictionary(r => r.Key, r => r.Data);
        foreach ((string key, byte[] data) in resourcesBefore.Where(r => !(r.Key.StartsWith("#3/", StringComparison.Ordinal) || r.Key.StartsWith("#14/", StringComparison.Ordinal) || r.Key.StartsWith("#16/", StringComparison.Ordinal))))
        {
            Assert.Equal(data, resourcesAfter[key]); // the manifest, a named type...
        }

        Assert.Equal(7, resourcesAfter.Keys.Count(k => k.StartsWith("#3/", StringComparison.Ordinal)));
        Assert.Single(resourcesAfter.Keys, k => k.StartsWith("#14/", StringComparison.Ordinal));
        Assert.Single(resourcesAfter.Keys, k => k.StartsWith("#16/", StringComparison.Ordinal));
    }

    [Fact]
    public void AnMsvcLinkedLibraryKeepsEverythingButItsResources()
    {
        byte[]? input = Vendored("sdl/lib/win-x64/SDL3.dll");
        Assert.SkipUnless(input is not null, "ThirdParty/sdl/lib/win-x64/SDL3.dll is not in this checkout (Git LFS)");
        using var scratch = new Scratch();

        (byte[] output, List<string> warnings, _) = Package(scratch, input!);

        AssertTheRestIsUntouched(input!, output);
        PEHeaders headers = PeChecks.AssertConsistent(output); // layout, contiguity, ordered resource directories, the checksum
        Assert.Equal(Subsystem.WindowsGui, headers.PEHeader!.Subsystem);
        Assert.DoesNotContain(warnings, w => w.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ASignedLibraryLosesItsSignatureWithAWarningAndKeepsItsNamedResource()
    {
        byte[]? input = Vendored("steamworks/lib/win-x64/steam_api64.dll");
        Assert.SkipUnless(input is not null, "ThirdParty/steamworks/lib/win-x64/steam_api64.dll is not in this checkout (Git LFS)");
        Assert.NotEqual(0, new PEReader(new MemoryStream(input!)).PEHeaders.PEHeader!.CertificateTableDirectory.Size); // the fixture is signed
        using var scratch = new Scratch();

        (byte[] output, List<string> warnings, _) = Package(scratch, input!);

        AssertTheRestIsUntouched(input!, output);
        PEHeaders headers = PeChecks.AssertConsistent(output);
        Assert.Equal((0, 0), (headers.PEHeader!.CertificateTableDirectory.RelativeVirtualAddress, headers.PEHeader.CertificateTableDirectory.Size));
        Assert.Contains(warnings, w => w.Contains("signature", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(TestResources.Read(output), r => r.Type == "SCID"); // a resource type with a name, kept
    }

    [Fact]
    public void TheConsoleSubsystemIsKeptOnAskForARealLibraryToo()
    {
        byte[]? input = Vendored("sdl/lib/win-x64/SDL3.dll");
        Assert.SkipUnless(input is not null, "ThirdParty/sdl/lib/win-x64/SDL3.dll is not in this checkout (Git LFS)");
        byte[] console = [.. input!];
        console[BitConverter.ToInt32(console, 0x3C) + 24 + 68] = 3; // the library is a windowed one; make it a console program to see the option
        using var scratch = new Scratch();

        (byte[] windowed, _, _) = Package(scratch, console);
        using var other = new Scratch();
        (byte[] kept, _, _) = Package(other, console, console: true);

        Assert.Equal(Subsystem.WindowsGui, new PEReader(new MemoryStream(windowed)).PEHeaders.PEHeader!.Subsystem);
        Assert.Equal(Subsystem.WindowsCui, new PEReader(new MemoryStream(kept)).PEHeaders.PEHeader!.Subsystem);
        PeChecks.AssertConsistent(windowed);
        PeChecks.AssertConsistent(kept);
    }

    [Fact]
    public void LlvmReadobjSeesTheSameFile()
    {
        // The documented manual check, run when llvm-readobj is installed: an independent implementation of the PE format reads the headers, the sections and the resource tree.
        string? tool = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "llvm-readobj.exe" : "llvm-readobj")).FirstOrDefault(File.Exists);
        byte[]? input = Vendored("sdl/lib/win-x64/SDL3.dll");
        Assert.SkipUnless(tool is not null && input is not null, "llvm-readobj is not on the PATH, or SDL3.dll is not in this checkout");
        using var scratch = new Scratch();
        (_, _, string path) = Package(scratch, input!);

        var start = new ProcessStartInfo(tool!, ["--file-headers", "--sections", "--coff-resources", path]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using Process process = Process.Start(start)!;
        string text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("IMAGE_SUBSYSTEM_WINDOWS_GUI", text, StringComparison.Ordinal);
        Assert.Contains("Name: .rsrc0", text, StringComparison.Ordinal);
        Assert.True(text.LastIndexOf("Name: .rsrc ", StringComparison.Ordinal) > text.LastIndexOf("Name: .reloc", StringComparison.Ordinal), ".rsrc is the last section");
        Assert.Contains("Type: ICON (ID 3)", text, StringComparison.Ordinal);
        Assert.Contains("Type: GROUP_ICON (ID 14)", text, StringComparison.Ordinal);
        Assert.Contains("Type: VERSIONINFO (ID 16)", text, StringComparison.Ordinal);
        Assert.Contains("Type: MANIFEST (ID 24)", text, StringComparison.Ordinal);
    }
}
