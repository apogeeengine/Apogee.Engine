using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Content.Packages;
using Apogee.Content.Packs;
using Xunit;

namespace Apogee.Export.Tests;

public class GameExporterTests
{
    private const string Hex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    private const string GameXml = """
        <Game name="Fixture Game" organization="Test Studio" version="1.2.3" executable="FixtureGame" identifier="org.test.fixturegame" steamAppId="480">
          <Icon path="icon.png"/>
          <Window width="800" height="600"/>
        </Game>
        """;

    /// <summary>Takes <c>.dat</c> files and makes their text in capitals, so that the import has something to do without any native library.</summary>
    private sealed class DatImporter : IAssetImporter
    {
        public string Name => "dat";

        public IReadOnlyList<string> Extensions { get; } = [".dat"];

        public string Fingerprint => "1";

        public ImportResult? Import(ImportContext context) => new(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(context.Bytes).ToUpperInvariant()), ".up");
    }

    /// <summary>A game folder, a fake runtime for a target, and an output folder, in a scratch folder.</summary>
    private sealed class Setup : IDisposable
    {
        private readonly Scratch _scratch = new();

        public Setup(ExportTarget? runtimeOf = null)
        {
            _scratch.Write("game/game.xml", GameXml);
            _scratch.Write("game/icon.png", Png.Encode(256, 256, new byte[256 * 256 * 4]));
            _scratch.Write("game/packages/core/mod.xml", "<Mod id=\"core\" version=\"1.0.0\"/>");
            _scratch.Write("game/packages/core/main.luau", "print('hi')");
            _scratch.Write("game/models/a.dat", "one");
            _scratch.Write("game/dev_mods/dev/mod.xml", "<Mod id=\"dev\" version=\"1.0.0\"/>");
            _scratch.Write("game/data/stale.pack", "an old pack of the development folder");
            _scratch.Write("game/steam_appid.txt", "480");
            _scratch.Write("game/.git/config", "left out");
            Runtime = FakeRuntime.Make(_scratch, runtimeOf ?? new ExportTarget("linux", "x64"));
        }

        public string Game => _scratch.Path("game");

        public string Out => _scratch.Path("out");

        public string Runtime { get; }

        public Scratch Scratch => _scratch;

        public ExportOptions Options(ExportTarget target, bool cooked = false, PackKey? key = null, string? runtime = null) => new()
        {
            GameFolder = Game,
            OutputFolder = Out,
            Target = target,
            RuntimeDirectory = runtime ?? Runtime,
            Cooked = cooked,
            Key = key,
            Importers = [new DatImporter()],
        };

        /// <summary>The paths in the pack of an export.</summary>
        public static List<string> PackedFiles(string pack, PackKey? key)
        {
            using PackSource source = PackSource.Open(pack, key);
            var files = new VirtualFileSystem();
            files.Mount("res://", source);
            return [.. files.EnumerateFiles("res://", recursive: true).Select(p => p["res://".Length..]).Order(StringComparer.Ordinal)];
        }

        public void Dispose() => _scratch.Dispose();
    }

    public static TheoryData<string> Targets => new("linux-x64", "win-x64", "osx-arm64", "osx-x64");

    [Theory]
    [MemberData(nameof(Targets))]
    public void TheLayoutIsRightForEachTarget(string rid)
    {
        Assert.True(ExportTarget.TryParse(rid, out ExportTarget target, out _));
        using var setup = new Setup(target);

        ExportResult result = GameExporter.Export(setup.Options(target));

        string exeDirectory = target.Os == "osx" ? Path.Combine(setup.Out, "Fixture Game.app", "Contents", "MacOS") : setup.Out;
        string root = target.Os == "osx" ? Path.Combine(setup.Out, "Fixture Game.app", "Contents", "Resources") : setup.Out;
        string exe = Path.Combine(exeDirectory, target.Os == "win" ? "FixtureGame.exe" : "FixtureGame");
        string library = target.Os == "win" ? "flecs.dll" : target.Os == "osx" ? "libflecs.dylib" : "libflecs.so";

        Assert.Equal(exe, result.Executable);
        Assert.Equal(root, result.GameRoot);
        Assert.True(File.Exists(exe));
        Assert.True(File.Exists(Path.Combine(exeDirectory, library)));
        Assert.True(File.Exists(Path.Combine(exeDirectory, "Shaders", "imported", "x.spv")));
        Assert.True(File.Exists(Path.Combine(root, "game.xml")));
        Assert.True(File.Exists(Path.Combine(root, "data", "game.pack")));
        Assert.True(File.Exists(Path.Combine(setup.Out, GameExporter.MarkerFile)));
        Assert.Contains(Path.GetRelativePath(setup.Out, exe).Replace('\\', '/'), result.Files);
        Assert.DoesNotContain(GameExporter.MarkerFile, result.Files);
        Assert.Equal(target, RuntimeFolder.Detect(exe));

        // The exported game.xml is the game's, read by the runtime from beside the program (the icon is in the executable or the bundle, except on Linux where the step writes icon.png).
        GameManifest exported = GameManifest.Load(root, out IReadOnlyList<string> problems)!;
        Assert.Empty(problems);
        Assert.Equal(("Fixture Game", "Test Studio", "1.2.3", "FixtureGame", 480u, 800, 600), (exported.Name, exported.Organization, exported.Version.ToString(), exported.Executable, exported.SteamAppId, exported.Window.Width, exported.Window.Height));
        Assert.Equal(target.Os == "linux" ? "icon.png" : null, exported.Icon);
    }

    [Fact]
    public void TheExecutableIsRenamedAndDebugFilesAreLeftOut()
    {
        using var setup = new Setup();

        ExportResult result = GameExporter.Export(setup.Options(new ExportTarget("linux", "x64")));

        // No Apogee.Runtime, pdb, dbg, lib, slang library or source, steam_appid.txt, or the runtime folder's own game.xml and data.
        // On Linux the step adds the icon and the desktop entry.
        Assert.Equal(["FixtureGame", "Shaders/imported/x.spv", "data/game.pack", "game.xml", "icon.png", "libflecs.so", "org.test.fixturegame.desktop"], result.Files);
        Assert.Equal(FakeRuntime.Executable(new ExportTarget("linux", "x64")), File.ReadAllBytes(result.Executable)); // with no key the program is copied as it is
        Assert.Equal("<Game/>", File.ReadAllText(Path.Combine(setup.Runtime, "game.xml"))); // the runtime folder itself is not touched
    }

    [Fact]
    public void DevModsGameXmlAndDataAreNotPacked()
    {
        using var setup = new Setup();

        ExportResult result = GameExporter.Export(setup.Options(new ExportTarget("linux", "x64")));

        Assert.Equal(["models/a.dat", "packages/core/main.luau", "packages/core/mod.xml"], Setup.PackedFiles(Path.Combine(result.GameRoot, "data", "game.pack"), null));
    }

    [Fact]
    public void ACookedExportPacksTheArtifactsInsteadOfTheSources()
    {
        using var setup = new Setup();

        ExportResult result = GameExporter.Export(setup.Options(new ExportTarget("linux", "x64"), cooked: true));

        List<string> packed = Setup.PackedFiles(Path.Combine(result.GameRoot, "data", "game.pack"), null);
        Assert.DoesNotContain("models/a.dat", packed);
        Assert.Contains(packed, p => p.StartsWith("imported/", StringComparison.Ordinal) && p.EndsWith(".up", StringComparison.Ordinal));
        Assert.Contains("imported/index.xml", packed);
        Assert.Contains("packages/core/main.luau", packed);
        Assert.DoesNotContain("icon.png", packed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheGameFolderIsNotWrittenTo(bool cooked)
    {
        using var setup = new Setup();
        string before = FakeRuntime.Fingerprint(setup.Game);

        GameExporter.Export(setup.Options(new ExportTarget("linux", "x64"), cooked));

        Assert.Equal(before, FakeRuntime.Fingerprint(setup.Game));
        Assert.False(Directory.Exists(Path.Combine(setup.Game, "imported")));
    }

    [Fact]
    public void ACookedExportOfAGameWithNothingToImportStillWorks()
    {
        using var setup = new Setup();
        File.Delete(Path.Combine(setup.Game, "models", "a.dat"));

        ExportResult result = GameExporter.Export(setup.Options(new ExportTarget("linux", "x64"), cooked: true));

        Assert.Equal(["packages/core/main.luau", "packages/core/mod.xml"], Setup.PackedFiles(Path.Combine(result.GameRoot, "data", "game.pack"), null).Where(p => !p.StartsWith("imported/", StringComparison.Ordinal)));
    }

    [Fact]
    public void WithAKeyThePacksAreEncryptedAndTheExecutableHoldsTheKey()
    {
        using var setup = new Setup();
        PackKey key = PackKey.Parse(Hex);

        ExportResult result = GameExporter.Export(setup.Options(new ExportTarget("linux", "x64"), key: key));

        string pack = Path.Combine(result.GameRoot, "data", "game.pack");
        Assert.NotEqual("APGK", Encoding.ASCII.GetString(File.ReadAllBytes(pack), 0, 4));
        Assert.Contains("packages/core/main.luau", Setup.PackedFiles(pack, key));
        byte[] exe = File.ReadAllBytes(result.Executable);
        PackKey? embedded = PackKeySlot.Decode(exe.AsSpan(FakeRuntime.SlotOffset(exe), 68));
        Assert.Equal(key.Bytes.ToArray(), embedded!.Bytes.ToArray());
        Assert.DoesNotContain(result.Warnings, w => w.Contains("not encrypted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWindowsExportHasTheIconTheVersionAndTheKey(bool console)
    {
        var windows = new ExportTarget("win", "x64");
        using var setup = new Setup(windows);
        PackKey key = PackKey.Parse(Hex);

        ExportResult result = GameExporter.Export(setup.Options(windows, key: key) with { Console = console });

        byte[] exe = File.ReadAllBytes(result.Executable);
        using var reader = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(exe));
        Assert.Equal(console ? System.Reflection.PortableExecutable.Subsystem.WindowsCui : System.Reflection.PortableExecutable.Subsystem.WindowsGui, reader.PEHeaders.PEHeader!.Subsystem);
        List<TestResource> resources = TestResources.Read(exe);
        Assert.Equal(7, resources.Count(r => r.Type == "#3"));
        Assert.Single(resources, r => r.Type == "#14");
        Assert.Contains("Fixture Game", Encoding.Unicode.GetString(resources.Single(r => r.Type == "#16").Data), StringComparison.Ordinal);
        Assert.Contains(resources, r => r.Type == "#24"); // the runtime's manifest

        // The key is in the file once, and decodes.
        PackKey? embedded = PackKeySlot.Decode(exe.AsSpan(FakeRuntime.SlotOffset(exe), 68));
        Assert.Equal(key.Bytes.ToArray(), embedded!.Bytes.ToArray());
        Assert.Equal(-1, exe.AsSpan(FakeRuntime.SlotOffset(exe) + 1).IndexOf(PackKeySlot.Template[..16]));
        Assert.Null(GameManifest.Load(result.GameRoot, out _)!.Icon); // the icon is in the executable
    }

    [Fact]
    public void WithoutAKeyThePacksArePlainAndTheSlotIsEmptyWithAWarning()
    {
        using var setup = new Setup();

        ExportResult result = GameExporter.Export(setup.Options(new ExportTarget("linux", "x64")));

        Assert.Equal("APGK", Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(result.GameRoot, "data", "game.pack")), 0, 4));
        byte[] exe = File.ReadAllBytes(result.Executable);
        Assert.Null(PackKeySlot.Decode(exe.AsSpan(FakeRuntime.SlotOffset(exe), 68)));
        Assert.Contains(result.Warnings, w => w.Contains("not encrypted", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOutputFolderThatIsNotAnExportIsNotCleared()
    {
        using var setup = new Setup();
        setup.Scratch.Write("out/precious.txt", "mine");

        string message = Assert.Throws<ExportException>(() => GameExporter.Export(setup.Options(new ExportTarget("linux", "x64")))).Message;

        Assert.Contains("not empty", message, StringComparison.Ordinal);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(setup.Out, "precious.txt")));
    }

    [Fact]
    public void AnEarlierExportIsReplaced()
    {
        using var setup = new Setup();
        ExportTarget target = new("linux", "x64");
        GameExporter.Export(setup.Options(target));
        setup.Scratch.Write("out/leftover.txt", "from before");
        setup.Scratch.Write("out/extra/deep.txt", "from before");

        ExportResult again = GameExporter.Export(setup.Options(target));

        Assert.False(File.Exists(Path.Combine(setup.Out, "leftover.txt")));
        Assert.False(Directory.Exists(Path.Combine(setup.Out, "extra")));
        Assert.True(File.Exists(again.Executable));
    }

    [Fact]
    public void TheOutputAndTheGameMustNotContainEachOther()
    {
        using var setup = new Setup();
        ExportTarget target = new("linux", "x64");

        ExportOptions inside = setup.Options(target) with { OutputFolder = Path.Combine(setup.Game, "build") };
        Assert.Contains("inside the game folder", Assert.Throws<ExportException>(() => GameExporter.Export(inside)).Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(setup.Game, "build")));

        ExportOptions around = setup.Options(target) with { OutputFolder = setup.Scratch.Root };
        Assert.Contains("inside the output folder", Assert.Throws<ExportException>(() => GameExporter.Export(around)).Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(setup.Game, "game.xml")));
    }

    [Fact]
    public void AGameWithoutGameXmlCannotBeExported()
    {
        using var setup = new Setup();
        File.Delete(Path.Combine(setup.Game, "game.xml"));

        Assert.Contains("has no game.xml", Assert.Throws<ExportException>(() => GameExporter.Export(setup.Options(new ExportTarget("linux", "x64")))).Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(setup.Out));
    }

    [Fact]
    public void ABrokenGameXmlIsReportedWithItsProblems()
    {
        using var setup = new Setup();
        setup.Scratch.Write("game/game.xml", "<Game name=\"No version\" organization=\"Test\"/>");

        string message = Assert.Throws<ExportException>(() => GameExporter.Export(setup.Options(new ExportTarget("linux", "x64")))).Message;

        Assert.Contains("game.xml", message, StringComparison.Ordinal);
        Assert.Contains("version", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuntimeOfAnotherTargetStopsTheExportBeforeAnythingIsWritten()
    {
        using var setup = new Setup();

        Assert.Contains("holds a linux-x64 runtime", Assert.Throws<ExportException>(() => GameExporter.Export(setup.Options(new ExportTarget("win", "x64")))).Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(setup.Out));
    }

    [Fact]
    public void AMissingIconIsAWarning()
    {
        using var setup = new Setup();
        File.Delete(Path.Combine(setup.Game, "icon.png"));

        ExportResult result = GameExporter.Export(setup.Options(new ExportTarget("linux", "x64")));

        Assert.Contains(result.Warnings, w => w.Contains("icon.png", StringComparison.Ordinal) && w.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExecutableNameThatIsAFileOfTheRuntimeIsRefused()
    {
        using var setup = new Setup();
        setup.Scratch.Write("game/game.xml", GameXml.Replace("executable=\"FixtureGame\"", "executable=\"libflecs.so\"", StringComparison.Ordinal));

        Assert.Contains("is the name of a file of the runtime", Assert.Throws<ExportException>(() => GameExporter.Export(setup.Options(new ExportTarget("linux", "x64")))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogSaysWhatIsDone()
    {
        using var setup = new Setup();
        var lines = new List<string>();

        GameExporter.Export(setup.Options(new ExportTarget("linux", "x64"), key: PackKey.Parse(Hex)), lines.Add);

        Assert.Contains(lines, l => l.StartsWith("Exporting Fixture Game 1.2.3 for linux-x64", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Packed ", StringComparison.Ordinal) && l.Contains("encrypted", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(Hex, StringComparison.OrdinalIgnoreCase));
    }
}
