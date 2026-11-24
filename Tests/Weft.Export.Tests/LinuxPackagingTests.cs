using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Weft.Content;
using Weft.Content.Packages;
using Xunit;

namespace Weft.Export.Tests;

public class LinuxPackagingTests
{
    private static readonly ExportTarget s_linux = new("linux", "x64");

    private static GameManifest Manifest(string xml)
    {
        Assert.True(GameManifest.TryParse(xml, "game.wgam", out GameManifest? manifest, out IReadOnlyList<string> problems), string.Join("\n", problems));
        return manifest!;
    }

    private static GameManifest Fixture(string name = "Fixture Game", string description = "A game for the tests.") => Manifest(
        $"""<Game name="{name}" organization="Test Studio" version="1.2.3" executable="FixtureGame" identifier="org.test.fixturegame" description="{description}"/>""");

    /// <summary>Runs the Linux step on a folder that has the executable, with an icon of <paramref name="iconSize"/> pixels (none when 0).</summary>
    private static PlatformPackagingContext Apply(Scratch scratch, GameManifest manifest, int iconSize, out string output)
    {
        output = scratch.Path("out");
        scratch.Write("out/FixtureGame", FakeRuntime.Executable(s_linux));
        var context = new PlatformPackagingContext
        {
            Options = new ExportOptions { GameFolder = scratch.Path("game"), OutputFolder = output, Target = s_linux },
            Manifest = manifest,
            Layout = ExportLayout.For(s_linux, manifest, output),
            IconFile = iconSize == 0 ? null : Png.Encode(iconSize, iconSize, Enumerable.Repeat((byte)200, iconSize * iconSize * 4).ToArray()),
            Warnings = [],
            Log = static _ => { },
        };
        new LinuxPackaging().Apply(context);
        return context;
    }

    [Fact]
    public void TheExecutableIsExecutable()
    {
        using var scratch = new Scratch();

        PlatformPackagingContext context = Apply(scratch, Fixture(), 256, out string output);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(context.Warnings, w => w.Contains("executable bit", StringComparison.Ordinal));
            return;
        }

        const UnixFileMode Expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        Assert.Equal(Expected, File.GetUnixFileMode(Path.Combine(output, "FixtureGame")));
        Assert.DoesNotContain(context.Warnings, w => w.Contains("executable bit", StringComparison.Ordinal));
    }

    [Fact]
    public void TheExecutableIsExecutableWhateverTheModeOfTheRuntimeFile()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("a Windows host has no Unix file modes");
            return;
        }

        using var scratch = new Scratch();
        scratch.Write("game/game.wgam", """<Game name="Fixture Game" organization="Test Studio" version="1.2.3" executable="FixtureGame" identifier="org.test.fixturegame"/>""");
        string runtime = FakeRuntime.Make(scratch, s_linux);
        File.SetUnixFileMode(Path.Combine(runtime, "Weft.Runtime"), UnixFileMode.UserRead | UnixFileMode.UserWrite); // a copy that lost its mode, as from an archive

        ExportResult result = GameExporter.Export(new ExportOptions { GameFolder = scratch.Path("game"), OutputFolder = scratch.Path("out"), Target = s_linux, RuntimeDirectory = runtime });

        Assert.True(File.GetUnixFileMode(result.Executable).HasFlag(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute));
    }

    [Fact]
    public void TheIconIsWrittenAtItsSize()
    {
        using var scratch = new Scratch();

        PlatformPackagingContext context = Apply(scratch, Fixture(), 512, out string output);

        byte[] file = File.ReadAllBytes(Path.Combine(output, "icon.png"));
        Assert.Equal("icon.png", context.ExportedIcon);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, file[..4]);
        Assert.Equal(256, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(16))); // IHDR width
        Assert.Equal(256, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(20))); // IHDR height
        Assert.DoesNotContain(context.Warnings, static w => !w.Contains("Unix file modes", StringComparison.Ordinal)); // a Windows host also warns that it cannot mark the executable
    }

    [Fact]
    public void ASmallIconIsWarnedAboutAndStillWritten()
    {
        using var scratch = new Scratch();

        PlatformPackagingContext context = Apply(scratch, Fixture(), 64, out string output);

        Assert.True(File.Exists(Path.Combine(output, "icon.png")));
        Assert.Contains(context.Warnings, w => w.Contains("64x64", StringComparison.Ordinal));
    }

    [Fact]
    public void AGameWithoutAnIconHasNoIconFileAndNoIconInItsEntry()
    {
        using var scratch = new Scratch();

        PlatformPackagingContext context = Apply(scratch, Fixture(), 0, out string output);

        Assert.Null(context.ExportedIcon);
        Assert.False(File.Exists(Path.Combine(output, "icon.png")));
        Assert.DoesNotContain("Icon=", File.ReadAllText(Path.Combine(output, "org.test.fixturegame.desktop")), StringComparison.Ordinal);
    }

    [Fact]
    public void AnIconThatIsNotSquareFailsTheExport()
    {
        using var scratch = new Scratch();
        GameManifest manifest = Fixture();
        string output = scratch.Path("out");
        scratch.Write("out/FixtureGame", FakeRuntime.Executable(s_linux));
        var context = new PlatformPackagingContext
        {
            Options = new ExportOptions { GameFolder = scratch.Path("game"), OutputFolder = output, Target = s_linux },
            Manifest = manifest,
            Layout = ExportLayout.For(s_linux, manifest, output),
            IconFile = Png.Encode(8, 4, new byte[8 * 4 * 4]),
            Warnings = [],
            Log = static _ => { },
        };

        Assert.Contains("square", Assert.Throws<ExportException>(() => new LinuxPackaging().Apply(context)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDesktopEntryNamesTheGame()
    {
        using var scratch = new Scratch();

        Apply(scratch, Fixture(), 256, out string output);

        string text = File.ReadAllText(Path.Combine(output, "org.test.fixturegame.desktop"));
        Assert.Equal(
            "[Desktop Entry]\nType=Application\nVersion=1.0\nName=Fixture Game\nComment=A game for the tests.\nExec=FixtureGame\nIcon=org.test.fixturegame\nTerminal=false\nCategories=Game;\nStartupWMClass=org.test.fixturegame\n",
            text);
    }

    [Fact]
    public void TheDesktopEntryEscapesValuesAsTheSpecificationSays()
    {
        // A semicolon is not special in a string value (only in lists, where Categories escapes its own), a backslash is, and so are a line break and leading space.
        GameManifest manifest = Fixture(name: "Cats; Dogs &amp; C:\\Games", description: "one&#10;two&#9;three");

        string text = LinuxPackaging.DesktopEntry(manifest, hasIcon: true);

        Assert.Contains("\nName=Cats; Dogs & C:\\\\Games\n", text, StringComparison.Ordinal);
        Assert.Contains("\nComment=one\\ntwo\\tthree\n", text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n'), l => l.StartsWith("Name=", StringComparison.Ordinal)); // no raw line break got into the file
        Assert.DoesNotContain("\t", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesAreEscapedByTheirRules()
    {
        Assert.Equal("\\sa b", LinuxPackaging.EscapeValue(" a b"));
        Assert.Equal("a\\\\b\\nc\\rd\\te", LinuxPackaging.EscapeValue("a\\b\nc\rd\te"));
        Assert.Equal("a b", LinuxPackaging.EscapeValue("a\u0007b")); // other control characters are not allowed in the file
        Assert.Equal("a;b", LinuxPackaging.EscapeValue("a;b"));
    }

    [Fact]
    public void AnExecLineQuotesWhatTheSpecificationReserves()
    {
        Assert.Equal("FixtureGame", LinuxPackaging.QuoteExecArgument("FixtureGame"));
        Assert.Equal("100%%", LinuxPackaging.QuoteExecArgument("100%"));
        Assert.Equal("\"my game\"", LinuxPackaging.QuoteExecArgument("my game"));
        Assert.Equal("\"a\\$b\\\\c\\\"d\"", LinuxPackaging.QuoteExecArgument("a$b\\c\"d"));

        // The quoting is then escaped as a string: the backslashes of the quoting are doubled in the file.
        GameManifest manifest = Fixture();
        Assert.Contains("\nExec=FixtureGame\n", LinuxPackaging.DesktopEntry(manifest, hasIcon: false), StringComparison.Ordinal);
        Assert.Equal("\"a\\\\$b\"", LinuxPackaging.EscapeValue(LinuxPackaging.QuoteExecArgument("a$b")));
    }

    [Fact]
    public void AnExportForLinuxHasTheIconInItsManifestAndTheEntryBesideIt()
    {
        using var scratch = new Scratch();
        scratch.Write("game/game.wgam", """<Game name="Fixture Game" organization="Test Studio" version="1.2.3" executable="FixtureGame" identifier="org.test.fixturegame"><Icon path="art/logo.png"/></Game>""");
        scratch.Write("game/art/logo.png", Png.Encode(256, 256, new byte[256 * 256 * 4]));
        string runtime = FakeRuntime.Make(scratch, s_linux);

        ExportResult result = GameExporter.Export(new ExportOptions { GameFolder = scratch.Path("game"), OutputFolder = scratch.Path("out"), Target = s_linux, RuntimeDirectory = runtime });

        GameManifest exported = GameManifest.Load(result.GameRoot, out IReadOnlyList<string> problems)!;
        Assert.Empty(problems);
        Assert.Equal("icon.png", exported.Icon);
        Assert.Contains("icon.png", result.Files);
        Assert.Contains("org.test.fixturegame.desktop", result.Files);
        Assert.DoesNotContain("art/logo.png", result.Files); // the game's own icon file is not shipped under its source name
    }
}
