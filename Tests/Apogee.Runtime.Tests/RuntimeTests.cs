using System;
using System.IO;
using Apogee.Native.Tests;
using Apogee.Host;
using Apogee.Runtime;
using Xunit;
using Lua = Apogee.Native.Luau.Lua;

namespace Apogee.Runtime.Tests;

public class OptionsTests
{
    private static RuntimeOptions Parse(params string[] args)
    {
        Assert.True(RuntimeOptions.TryParse(args, out RuntimeOptions options, out string? problem), problem);
        return options;
    }

    private static string Fail(params string[] args)
    {
        Assert.False(RuntimeOptions.TryParse(args, out _, out string? problem));
        return problem!;
    }

    [Fact]
    public void NoArgumentsIsTheSmokeTest() => Assert.Equal(RuntimeCommand.Smoke, Parse().Command);

    [Fact]
    public void EachCommandTakesItsValue()
    {
        RuntimeOptions test = Parse("--luau-test", "Tests/Luau", "--luau-filter", "vectors");
        Assert.Equal((RuntimeCommand.LuauTest, "Tests/Luau", "vectors"), (test.Command, test.Path, test.Filter));

        RuntimeOptions run = Parse("--run", "main.luau", "--frames", "120", "--headless");
        Assert.Equal((RuntimeCommand.Run, "main.luau", 120, true), (run.Command, run.Path, run.Frames, run.Headless));

        Assert.Equal((RuntimeCommand.LuauDefs, "out"), (Parse("--luau-defs", "out").Command, Parse("--luau-defs", "out").Path));
        Assert.Equal((RuntimeCommand.LuauDocs, "docs"), (Parse("--luau-docs", "docs").Command, Parse("--luau-docs", "docs").Path));
        Assert.Equal(RuntimeCommand.Help, Parse("--help").Command);
        Assert.Null(Parse("--run", "main.luau").Frames);
    }

    [Fact]
    public void AGameIsAFolderWithAUserFolderOfItsOwn()
    {
        RuntimeOptions game = Parse("--game", "MyGame", "--user", "saves", "--headless", "--frames", "3");
        Assert.Equal((RuntimeCommand.Game, "MyGame", "saves", 3), (game.Command, game.Path, game.UserFolder, game.Frames));
        Assert.Equal("--user goes with --game.", Fail("--run", "a.luau", "--user", "x"));
        Assert.Equal("--user needs a value.", Fail("--game", "g", "--user"));
        Assert.Equal("--screenshot goes with --headless.", Fail("--game", "g", "--screenshot", "x.png"));
    }

    [Fact]
    public void ImportAndCookedHaveTheirOwnOptions()
    {
        RuntimeOptions import = Parse("--import", "game", "--force");
        Assert.Equal((RuntimeCommand.Import, "game", true), (import.Command, import.Path, import.Force));
        Assert.True(Parse("--pack", "a", "b", "--cooked").Cooked);
        Assert.Equal("--cooked goes with --pack.", Fail("--run", "a.luau", "--cooked"));
        Assert.Equal("--force goes with --import.", Fail("--pack", "a", "b", "--force"));
    }

    [Fact]
    public void WatchGoesWithGame()
    {
        Assert.True(Parse("--game", "g", "--watch").Watch);
        Assert.Equal("--watch goes with --game.", Fail("--run", "a.luau", "--watch"));
    }

    [Fact]
    public void PackingTakesAFolderAnOutputAndItsOptions()
    {
        RuntimeOptions pack = Parse("--pack", "Content", "data/game.pack", "--split-mb", "64", "--level", "15", "--base", "a.pack", "--base", "b.pack");
        Assert.Equal((RuntimeCommand.Pack, "Content", "data/game.pack", 64L, 15), (pack.Command, pack.Path, pack.Output, pack.SplitMegabytes, pack.Level));
        Assert.Equal(["a.pack", "b.pack"], pack.Bases);
        Assert.Equal("--pack needs a folder and the pack file to write.", Fail("--pack", "Content"));
        Assert.Equal("--level expects 1 to 22, not '30'.", Fail("--pack", "a", "b", "--level", "30"));
        Assert.Equal("--split-mb expects a number of megabytes, not '0'.", Fail("--pack", "a", "b", "--split-mb", "0"));
        Assert.Equal("--split-mb, --level and --base go with --pack.", Fail("--run", "a.luau", "--level", "5"));
    }

    [Fact]
    public void TheWindowAndTheImageHaveASizeAndTheGpuCanBeChosen()
    {
        RuntimeOptions options = Parse("--run", "main.luau", "--headless", "--size", "640x360", "--screenshot", "out.png", "--overlay", "--gpu", "llvmpipe");
        Assert.Equal((640, 360, "out.png", true, "llvmpipe"), (options.Width, options.Height, options.Screenshot, options.Overlay, options.Gpu));
        Assert.Equal((1280, 720), (Parse("--run", "main.luau").Width, Parse("--run", "main.luau").Height));
    }

    [Fact]
    public void MistakesAreReportedWithWhatWasWrong()
    {
        Assert.Equal("Unknown argument '--nope'.", Fail("--nope"));
        Assert.Equal("--run needs a value.", Fail("--run"));
        Assert.Equal("--run needs a value.", Fail("--run", "--frames", "3"));
        Assert.Equal("--frames expects a number of frames, not 'many'.", Fail("--run", "a.luau", "--frames", "many"));
        Assert.Equal("--frames expects a number of frames, not '-1'.", Fail("--run", "a.luau", "--frames", "-1"));
        Assert.Equal("--luau-filter goes with --luau-test.", Fail("--run", "a.luau", "--luau-filter", "x"));
        Assert.Equal("--size needs <width>x<height>, at least 16x16.", Fail("--run", "a.luau", "--size", "wide"));
        Assert.Equal("--size needs <width>x<height>, at least 16x16.", Fail("--run", "a.luau", "--size", "8x8"));
        Assert.Equal("--screenshot goes with --headless.", Fail("--run", "a.luau", "--screenshot", "out.png"));
        Assert.Equal("--headless, --frames, --screenshot, --overlay and --gpu go with --run or --game.", Fail("--luau-defs", "out", "--headless"));
    }
}

public sealed class Scratch : IDisposable
{
    public Scratch()
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        NativeRequirements.Require(typeof(Apogee.Native.Flecs.Ecs).Assembly, "flecs");
        Root = Path.Combine(Path.GetTempPath(), "apogee-runtime-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Write(string name, string source)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a failure.
        }
    }
}

// The scripts' gui callbacks and ImGui's "frame open" flag are process-wide, so the tests that run a script share a collection with the GPU test and
// never overlap.
[Xunit.Collection("gpu")]
public class CommandTests
{
    private static void Game(Scratch scratch, string id, string manifestInner, bool user, params (string Path, string Text)[] files)
    {
        string folder = user ? $"user/mods/{id}" : $"game/packages/{id}";
        scratch.Write($"{folder}/mod.xml", $"<Mod id=\"{id}\" version=\"1.0.0\">{manifestInner}</Mod>");
        foreach ((string path, string text) in files)
        {
            scratch.Write($"{folder}/{path}", text);
        }
    }

    [Fact]
    public void AGameLoadsItsPackagesAndItsModsAndAModThatFailsDoesNotStopIt()
    {
        using var scratch = new Scratch();
        Game(scratch, "core", "<Content path=\"data\"/><Script path=\"main.luau\"/>", user: false,
            ("data/crates.xml", "<Crates><Crate id=\"wooden\" weight=\"5\"/></Crates>"),
            ("main.luau", "fs.write_text('user://report.txt', 'crates=' .. #defs.all('Crate') .. ' weight=' .. defs.get('Crate', 'wooden').weight)"));
        Game(scratch, "bigger", "<Dependency id=\"core\"/><Content path=\"data\"/><Script path=\"main.luau\"/>", user: true,
            ("data/patch.xml", "<Crate id=\"wooden\" patch=\"true\" weight=\"9\"/>"),
            ("main.luau", "fs.write_text('user://moddata/bigger/hello.txt', 'hi from a mod')"));
        Game(scratch, "broken", "<Dependency id=\"core\"/><Script path=\"main.luau\"/>", user: true, ("main.luau", "error('mod bug')"));
        string user = System.IO.Path.Combine(scratch.Root, "user");

        int exit = Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", user, "--headless", "--frames", "3"));

        Assert.Equal(0, exit);
        // The base game's script ran after every package's content was loaded, so it saw the mod's patch.
        Assert.Equal("crates=1 weight=9", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
        Assert.Equal("hi from a mod", File.ReadAllText(System.IO.Path.Combine(user, "moddata", "bigger", "hello.txt")));
    }

    [Fact]
    public void AGameCanEndTheRunAndIsToldBySetEscapeQuitsWhatEscapeDoes()
    {
        using var scratch = new Scratch();
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false,
            ("main.luau", """
                local Ticker = world:component("Ticker", {})
                world:add(world:create_entity(), Ticker)
                local frame = 0
                world:system("Count", { Ticker }, function()
                    frame += 1
                    fs.write_text("user://frames.txt", tostring(frame))
                    if frame == 3 then
                        game.quit()
                    end
                end)
                game.set_escape_quits(false)
                """));
        string user = System.IO.Path.Combine(scratch.Root, "user");

        // Fifty frames were asked for; the script ended the run at its third.
        int exit = Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", user, "--headless", "--frames", "50"));

        Assert.Equal(0, exit);
        Assert.Equal("3", File.ReadAllText(System.IO.Path.Combine(user, "frames.txt")));
        Assert.False(Apogee.Host.GameLibrary.EscapeQuits);

        // The next run starts with the defaults again.
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "print('nothing')"));
        Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", user, "--headless", "--frames", "1"));
        Assert.True(Apogee.Host.GameLibrary.EscapeQuits);
        Assert.False(Apogee.Host.GameLibrary.QuitRequested);
    }

    private static string PackedGame(Scratch scratch, Apogee.Content.Packs.PackKey? key, string reportText)
    {
        string data = System.IO.Path.Combine(scratch.Root, "game", "data");
        Directory.CreateDirectory(data);
        using (var writer = new Apogee.Content.Packs.PackWriter(System.IO.Path.Combine(data, "game.pack"), new Apogee.Content.Packs.PackWriterOptions { Key = key }))
        {
            writer.Add("packages/core/mod.xml", System.Text.Encoding.UTF8.GetBytes("<Mod id=\"core\" version=\"1.0.0\"><Script path=\"main.luau\"/></Mod>"));
            writer.Add("packages/core/main.luau", System.Text.Encoding.UTF8.GetBytes($"fs.write_text('user://report.txt', '{reportText}')"));
            writer.Finish();
        }

        return System.IO.Path.Combine(scratch.Root, "game");
    }

    [Fact]
    public void AGameRunsFromItsPacksAndALaterPatchPackWins()
    {
        using var scratch = new Scratch();
        string game = PackedGame(scratch, null, "from the base pack");
        string user = System.IO.Path.Combine(scratch.Root, "user");

        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", user, "--headless")));
        Assert.Equal("from the base pack", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));

        using (var patch = new Apogee.Content.Packs.PackWriter(System.IO.Path.Combine(game, "data", "patch.pack")))
        {
            patch.Add("packages/core/main.luau", "fs.write_text('user://report.txt', 'from the patch pack')"u8.ToArray());
            patch.Finish();
        }

        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", user, "--headless")));
        Assert.Equal("from the patch pack", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
    }

    [Fact]
    public void AFolderIsPackedAndAPatchPackHoldsOnlyWhatChanged()
    {
        using var scratch = new Scratch();
        scratch.Write("src/packages/core/mod.xml", "<Mod id=\"core\" version=\"1.0.0\"><Script path=\"main.luau\"/></Mod>");
        scratch.Write("src/packages/core/main.luau", "fs.write_text('user://report.txt', 'version one')");
        scratch.Write("src/packages/core/old.txt", "to be removed");
        scratch.Write("src/.hidden/secret.txt", "left out");
        string data = System.IO.Path.Combine(scratch.Root, "game", "data");
        string user = System.IO.Path.Combine(scratch.Root, "user");

        Assert.Equal(0, Commands.Pack(Options("--pack", System.IO.Path.Combine(scratch.Root, "src"), System.IO.Path.Combine(data, "game.pack"))));
        using (var pack = Apogee.Content.Packs.PackSource.Open(System.IO.Path.Combine(data, "game.pack")))
        {
            Assert.Equal(3, pack.FileCount);
        }

        File.WriteAllText(System.IO.Path.Combine(scratch.Root, "src", "packages", "core", "main.luau"), "fs.write_text('user://report.txt', 'version two')");
        File.Delete(System.IO.Path.Combine(scratch.Root, "src", "packages", "core", "old.txt"));
        Assert.Equal(0, Commands.Pack(Options("--pack", System.IO.Path.Combine(scratch.Root, "src"), System.IO.Path.Combine(data, "patch.pack"), "--base", System.IO.Path.Combine(data, "game.pack"))));
        using (var patch = Apogee.Content.Packs.PackSource.Open(System.IO.Path.Combine(data, "patch.pack")))
        {
            Assert.Equal(1, patch.FileCount); // only main.luau changed
            Assert.Equal(Apogee.Content.FileSystem.FileKind.Removed, ((Apogee.Content.FileSystem.IFileSource)patch).Kind("packages/core/old.txt"));
        }

        Assert.Equal(0, Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", user, "--headless")));
        Assert.Equal("version two", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
        Assert.Equal(1, Commands.Pack(Options("--pack", System.IO.Path.Combine(scratch.Root, "nope"), System.IO.Path.Combine(data, "x.pack"))));
    }

    [Fact]
    public void AnImportedProjectIsPackedWithItsArtifactsInsteadOfItsSources()
    {
        using var scratch = new Scratch();
        byte[] rgba = new byte[16 * 16 * 4];
        new Random(3).NextBytes(rgba);
        Directory.CreateDirectory(System.IO.Path.Combine(scratch.Root, "src", "textures"));
        File.WriteAllBytes(System.IO.Path.Combine(scratch.Root, "src", "textures", "ground.png"), Apogee.Content.Png.Encode(16, 16, rgba));
        scratch.Write("src/readme.txt", "kept");
        string src = System.IO.Path.Combine(scratch.Root, "src");
        string pack = System.IO.Path.Combine(scratch.Root, "out", "game.pack");

        Assert.Equal(1, Commands.Pack(Options("--pack", src, pack, "--cooked"))); // nothing imported yet
        Assert.Equal(0, Commands.Import(Options("--import", src)));
        Assert.True(File.Exists(System.IO.Path.Combine(src, "imported", "textures", "ground.png.ktx2")));
        Assert.Equal(0, Commands.Pack(Options("--pack", src, pack, "--cooked")));

        using var packed = Apogee.Content.Packs.PackSource.Open(pack);
        var files = new Apogee.Content.FileSystem.VirtualFileSystem();
        files.Mount("res://", packed);
        Assert.Equal(["res://imported/index.xml", "res://imported/textures/ground.png.ktx2", "res://readme.txt"], files.EnumerateFiles("res://", recursive: true));
        Assert.Equal(16, new Apogee.Content.Import.ContentLoader(files).LoadTexture("res://textures/ground.png", true, Apogee.Content.TextureCaps.None).Width);

        // A second import changes nothing, and --force does it all again.
        Assert.Equal(0, Commands.Import(Options("--import", src)));
        Assert.Equal(0, Commands.Import(Options("--import", src, "--force")));
        Assert.Equal(1, Commands.Import(Options("--import", System.IO.Path.Combine(scratch.Root, "nope"))));
        File.WriteAllText(System.IO.Path.Combine(src, "textures", "broken.png"), "not an image");
        Assert.Equal(2, Commands.Import(Options("--import", src)));
    }

    [Fact]
    public void ShadersAreCompiledByImportAndPackedWithoutTheirSourcesOrTheCompiler()
    {
        Apogee.Native.Tests.NativeRequirements.Require(typeof(Apogee.Native.Slang.Slang).Assembly, "slang_c");
        using var scratch = new Scratch();
        string src = System.IO.Path.Combine(scratch.Root, "src");
        string pack = System.IO.Path.Combine(scratch.Root, "out", "game.pack");
        scratch.Write("src/shaders/library.slang", "float scale() { return 2.0; }\n");
        scratch.Write("src/shaders/run.slang", "import library;\nRWStructuredBuffer<float> output;\n[shader(\"compute\")] [numthreads(8, 1, 1)]\nvoid run(uint3 id : SV_DispatchThreadID) { output[id.x] = scale(); }\n");

        Assert.Equal(0, Commands.Import(Options("--import", src)));
        Assert.True(File.Exists(System.IO.Path.Combine(src, "imported", "shaders", "run.slang.ashader")));
        Assert.False(File.Exists(System.IO.Path.Combine(src, "imported", "shaders", "library.slang.ashader")));
        Assert.Equal(0, Commands.Pack(Options("--pack", src, pack, "--cooked")));

        using var packed = Apogee.Content.Packs.PackSource.Open(pack);
        var files = new Apogee.Content.FileSystem.VirtualFileSystem();
        files.Mount("res://", packed);
        Assert.Equal(["res://imported/index.xml", "res://imported/shaders/run.slang.ashader"], files.EnumerateFiles("res://", recursive: true));
        var library = new Apogee.Rendering.Shaders.ShaderLibrary(files, createCompiler: _ => throw new InvalidOperationException("the compiler was started"));
        Assert.Equal(["run"], library.Load("res://shaders/run.slang").EntryPoints);
    }

    [Fact]
    public void AnEncryptedGameNeedsTheKeyFromTheEnvironment()
    {
        if (!System.Security.Cryptography.AesGcm.IsSupported)
        {
            Assert.Skip("AES-GCM is not available on this machine.");
        }

        const string Hex = "0f0e0d0c0b0a09080706050403020100ffeeddccbbaa99887766554433221100";
        using var scratch = new Scratch();
        string game = PackedGame(scratch, Apogee.Content.Packs.PackKey.Parse(Hex), "sealed ok");
        string user = System.IO.Path.Combine(scratch.Root, "user");
        string? before = Environment.GetEnvironmentVariable("APOGEE_PACK_KEY");
        try
        {
            Environment.SetEnvironmentVariable("APOGEE_PACK_KEY", null);
            Assert.Equal(2, Commands.Run(Options("--game", game, "--user", user, "--headless")));

            Environment.SetEnvironmentVariable("APOGEE_PACK_KEY", Hex.Replace('0', '1'));
            Assert.Equal(2, Commands.Run(Options("--game", game, "--user", user, "--headless")));

            Environment.SetEnvironmentVariable("APOGEE_PACK_KEY", "not a key");
            Assert.Equal(2, Commands.Run(Options("--game", game, "--user", user, "--headless")));

            Environment.SetEnvironmentVariable("APOGEE_PACK_KEY", Hex);
            Assert.Equal(0, Commands.Run(Options("--game", game, "--user", user, "--headless")));
            Assert.Equal("sealed ok", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APOGEE_PACK_KEY", before);
        }
    }

    [Fact]
    public void ABaseGameSeesSteamAndSavesToTheCloudFolderWhileAModCannotTouchSteam()
    {
        using var scratch = new Scratch();
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false,
            ("main.luau", "fs.write_text('cloud://slot1/save.txt', 'day 3') fs.write_text('user://report.txt', tostring(steam.is_active) .. ' ' .. steam.status)"));
        Game(scratch, "nosy", "<Dependency id=\"core\"/><Script path=\"main.luau\"/>", user: true, ("main.luau", "fs.write_text('user://moddata/nosy/result.txt', tostring(steam.is_active))"));
        string user = System.IO.Path.Combine(scratch.Root, "user");

        Assert.Equal(0, Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", user, "--headless", "--frames", "2")));

        // Headless runs do not connect to Steam: the global is there and inactive, and the cloud is a folder of the player's files.
        Assert.StartsWith("false ", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
        Assert.Equal("day 3", File.ReadAllText(System.IO.Path.Combine(user, "cloud", "slot1", "save.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(user, "moddata", "nosy", "result.txt"))); // the mod's use of `steam` was refused
    }

    [Fact]
    public void AWatchedGameCanBeImportedIntoSoItsResFolderIsWritable()
    {
        using var scratch = new Scratch();
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "local ok = fs.write_text('res://probe.txt', 'x') fs.write_text('user://report.txt', tostring(ok))"));
        string game = System.IO.Path.Combine(scratch.Root, "game");
        string user = System.IO.Path.Combine(scratch.Root, "user");

        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", user, "--headless")));
        Assert.Equal("nil", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", user, "--headless", "--frames", "5", "--watch")));
        Assert.Equal("true", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
    }

    [Fact]
    public void AWatchedGamesShaderEditsReachItsShaderLibraryAndAnUnwatchedGameHasNone()
    {
        NativeRequirements.Require(typeof(Apogee.Native.Slang.Slang).Assembly, "slang_c");
        using var scratch = new Scratch();
        const string Shader = "RWStructuredBuffer<float> output;\n[shader(\"compute\")] [numthreads(8, 1, 1)]\nvoid run(uint3 id : SV_DispatchThreadID) { output[id.x] = 1.0; }\n";
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "local x = 1"), ("shaders/own.slang", Shader));
        string game = System.IO.Path.Combine(scratch.Root, "game");
        string user = System.IO.Path.Combine(scratch.Root, "user");
        string cache = System.IO.Path.Combine(scratch.Root, "cache");
        string shader = System.IO.Path.Combine(game, "packages", "core", "shaders", "own.slang");

        using (ScriptSession session = ScriptSession.StartGame(game, user, watch: true))
        {
            Apogee.Rendering.Shaders.ShaderLibrary library = session.CreateShaderLibrary(cache);
            Assert.Equal(["run"], library.Load("res://packages/core/shaders/own.slang").EntryPoints);
            Assert.NotEmpty(library.Load("shaders://imgui.slang").EntryPoints); // the engine's own shaders are in the game's file system
            var changed = new System.Collections.Generic.List<string>();
            library.ModulesChanged += changed.AddRange;

            File.WriteAllText(shader, Shader + "// edited\n");
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (changed.Count == 0 && timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                session.Reload(); // the watcher waits until the file has been quiet for a moment
                System.Threading.Thread.Sleep(20);
            }

            Assert.Contains("res://packages/core/shaders/own.slang", changed);
            Assert.DoesNotContain("shaders://imgui.slang", changed);
        }

        using ScriptSession unwatched = ScriptSession.StartGame(game, user, watch: false);
        Assert.Null(unwatched.Reloader);
        Assert.Equal(["run"], unwatched.CreateShaderLibrary(cache).Load("res://packages/core/shaders/own.slang").EntryPoints);
    }

    [Fact]
    public void ABasePackageThatFailsIsAnErrorOfTheGame()
    {
        using var scratch = new Scratch();
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "error('base bug')"));

        Assert.Equal(2, Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", System.IO.Path.Combine(scratch.Root, "user"), "--headless")));
    }

    [Fact]
    public void AGameFolderThatIsNotThereIsOne()
    {
        using var scratch = new Scratch();

        Assert.Equal(1, Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "nope"), "--headless")));
    }

    [Fact]
    public void AGameWithNoPackagesStillRuns()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(System.IO.Path.Combine(scratch.Root, "game"));

        Assert.Equal(0, Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", System.IO.Path.Combine(scratch.Root, "user"), "--headless", "--frames", "2")));
    }

    private static RuntimeOptions Options(params string[] args)
    {
        Assert.True(RuntimeOptions.TryParse(args, out RuntimeOptions options, out string? problem), problem);
        return options;
    }

    [Fact]
    public void ARunningScriptSeesTheWorldAndItsSystemsRunForTheFrames()
    {
        using var scratch = new Scratch();
        string script = scratch.Write("main.luau", @"
            local Spin = world:component('Spin', { angle = 'f32' })
            local e = world:create_entity('wheel')
            world:set(e, Spin, { angle = 0 })
            world:system('Turn', { Spin }, function(_, spin) spin.angle += 1 end)
            print('main ran')");

        Assert.Equal(0, Commands.Run(Options("--run", script, "--headless", "--frames", "30")));
    }

    [Fact]
    public void TheSessionsInputRouterIsTheScriptsGlobalAndFiresInTheFrames()
    {
        using var scratch = new Scratch();
        string script = scratch.Write("main.luau", @"
            input.map:add_action('jump', { 'world' }, { 'Space' })
            input.map:add_action('forward', { 'world' }, { 'W' }, true)
            input.contexts = { 'world' }
            fired = 0
            input.action_fired:connect(function(action) if action == 'jump' then fired += 1 end end)");

        using ScriptSession session = ScriptSession.Start(script.Replace('\\', '/'));
        Assert.True(session.Input.Map!.HasAction("jump"));
        var fired = new System.Collections.Generic.List<string>();
        session.Input.ActionFired.Connect(fired.Add);

        // What the devices do on an event: the router sees it, and the next frame fires the action and reads the strength.
        session.Input.Press("Space", Apogee.Input.Modifiers.None, Apogee.Input.InputDevices.Now);
        session.Input.Press("W", Apogee.Input.Modifiers.None, Apogee.Input.InputDevices.Now);
        session.Progress(1f / 60);
        Assert.Equal(["jump"], fired);
        Assert.Equal(1f, session.Input.GetStrength("forward"));
        session.EndFrame();
    }

    [Fact]
    public void TheSessionsPhysicsIsTheScriptsGlobalAndFallsInTheFrames()
    {
        using var scratch = new Scratch();
        string script = scratch.Write("main.luau", @"
            local Transform = world:component('Transform')
            ball = world:create_entity('ball')
            world:set(ball, Transform, { Position = Vector3(0, 10, 0), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
            local desc = BodyDesc.new()
            desc.shape = PhysicsShape.sphere(0.5)
            physics:add_body(ball, desc)");

        using ScriptSession session = ScriptSession.Start(script.Replace('\\', '/'));
        Assert.Equal(1, session.Physics.BodyCount);
        Apogee.Reflection.Entity ball = session.World.Lookup("ball");

        for (int frame = 0; frame < 60; frame++)
        {
            session.Progress(1f / 60); // physics first, then the world
        }

        double y = session.World.Get<Apogee.Ecs.WorldTransform>(ball).Position.Y;
        Assert.InRange(y, 4.5, 6.0); // about a second of free fall from 10 m: 10 - 4.9
        session.EndFrame();
    }

    [Fact]
    public void TheSessionsAudioIsTheScriptsGlobalAndUpdatesInTheFrames()
    {
        using var scratch = new Scratch();
        string script = scratch.Write("main.luau", @"
            local Transform = world:component('Transform')
            local ear = world:create_entity('ear')
            world:set(ear, Transform, { Position = Vector3(0, 0, 0), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
            world:set(ear, world:component('AudioListener'), { Index = 0 })
            audio:get_bus('music').volume = 0.5
            heard = audio.is_active");

        using ScriptSession session = ScriptSession.Start(script.Replace('\\', '/'));
        Assert.Equal(Apogee.Audio.AudioSystem.IsInstalled(), session.Audio.IsActive); // active exactly when OpenAL Soft is installed (it mixes into nowhere here)
        for (int frame = 0; frame < 10; frame++)
        {
            session.Progress(1f / 60);
        }

        Assert.Equal(0.5f, session.Audio.GetBus("music").Volume);
        session.EndFrame();
    }

    [Fact]
    public void AScriptThatFailsIsExitCodeTwoAndAMissingFileIsOne()
    {
        using var scratch = new Scratch();
        string bad = scratch.Write("bad.luau", "error('no good')");

        Assert.Equal(2, Commands.Run(Options("--run", bad, "--headless")));
        Assert.Equal(1, Commands.Run(Options("--run", Path.Combine(scratch.Root, "missing.luau"), "--headless")));
    }

    [Fact]
    public void ARunnerThatHasTestsFailingIsExitCodeOne()
    {
        using var scratch = new Scratch();
        scratch.Write("ok/a.test.luau", "local test = require('@engine/test')\ntest.it('passes', function() end)");

        Assert.Equal(0, Commands.LuauTest(Options("--luau-test", Path.Combine(scratch.Root, "ok"))));

        scratch.Write("bad/b.test.luau", "local test = require('@engine/test')\ntest.it('fails', function() test.expect(1).toBe(2) end)");
        Assert.Equal(1, Commands.LuauTest(Options("--luau-test", Path.Combine(scratch.Root, "bad"))));
        Assert.Equal(1, Commands.LuauTest(Options("--luau-test", Path.Combine(scratch.Root, "nothing-here"))));
    }

    [Fact]
    public void TheDefinitionsAndTheEngineModulesAreWritten()
    {
        using var scratch = new Scratch();
        string target = Path.Combine(scratch.Root, "defs");

        Assert.Equal(0, Commands.LuauDefs(Options("--luau-defs", target)));

        string definitions = File.ReadAllText(Path.Combine(target, "apogee.d.luau"));
        Assert.Contains("declare extern type World extends EngineObject with", definitions);
        Assert.DoesNotContain('\r', definitions);
        Assert.True(File.Exists(Path.Combine(target, "engine", "test.luau")));
    }

    [Fact]
    public void TheLuauApiReferenceIsWritten()
    {
        using var scratch = new Scratch();
        string target = Path.Combine(scratch.Root, "api");

        Assert.Equal(0, Commands.LuauDocs(Options("--luau-docs", target)));

        string reference = File.ReadAllText(Path.Combine(target, "reference.md"));
        Assert.Contains("# Luau API reference", reference);
        Assert.Contains("- `physics`: [`PhysicsWorld`](classes/PhysicsWorld.md).", reference);
        Assert.DoesNotContain('\r', reference);
        Assert.Contains("Extends `EngineObject`.", File.ReadAllText(Path.Combine(target, "classes", "World.md")));
    }
}

/// <summary>A game whose scripts put a model and a camera in the world: the runtime draws it, and a game without a camera still draws only its panels.</summary>
public sealed class WorldRenderingTests : Apogee.Graphics.Tests.GpuTest
{
    public WorldRenderingTests(Apogee.Graphics.Tests.GpuFixture gpu)
        : base(gpu)
    {
    }

    private static string? SampleModel()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string candidate = Path.Combine(directory, "Tests", "Apogee.Rendering.Tests", "Fixtures", "models", "NormalTangentTest.glb");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static RuntimeOptions Options(params string[] args)
    {
        Assert.True(RuntimeOptions.TryParse(args, out RuntimeOptions options, out string? problem), problem);
        return options;
    }

    private static string Scene(Scratch scratch, string script)
    {
        scratch.Write("game/packages/scene/mod.xml", "<Mod id=\"scene\" version=\"1.0.0\"><Script path=\"main.luau\"/></Mod>");
        scratch.Write("game/packages/scene/main.luau", script);
        return Path.Combine(scratch.Root, "game");
    }

    private static int DistinctColors(string screenshot)
    {
        Apogee.Content.TextureData image = Apogee.Content.ImageDecoder.Decode("shot", File.ReadAllBytes(screenshot), srgb: false, Apogee.Content.SamplerData.Default, generateMips: false);
        var colors = new System.Collections.Generic.HashSet<uint>();
        byte[] pixels = image.Mips[0];
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            colors.Add(BitConverter.ToUInt32(pixels, i));
        }

        return colors.Count;
    }

    [Fact]
    public void AGameWithAMainCameraDrawsItsModelsAndLights()
    {
        string? model = SampleModel();
        if (model is null)
        {
            Assert.Skip("the model fixture is not here");
        }

        using var scratch = new Scratch();
        string game = Scene(scratch, """
            local Transform = world:component("Transform")
            local ModelRenderer = world:component("ModelRenderer")
            local Light = world:component("Light")
            local CameraComponent = world:component("CameraComponent")
            local MainCamera = world:component("MainCamera")

            local id = models:load("res://packages/scene/thing.glb")
            assert(id ~= 0 and id == models:load("res://packages/scene/thing.glb"), "one id per path")
            assert(not pcall(function() models:load("res://packages/scene/nothing.glb") end), "a path with no file is an error")

            local thing = world:create_entity("thing")
            world:set(thing, Transform, { Position = Vector3(0, 0, 0), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
            world:set(thing, ModelRenderer, { Model = id })

            local lamp = world:create_entity("lamp")
            world:set(lamp, Transform, { Position = Vector3(1, 2, 3), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
            world:set(lamp, Light, { Color = Vector3(1, 1, 1), Intensity = 60, Range = 20, InnerCone = 0, OuterCone = 0, Kind = 1 })

            local eye = world:create_entity("eye")
            world:set(eye, Transform, { Position = Vector3(0, 0.5, 3), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
            world:set(eye, CameraComponent, { FovDegrees = 60, Near = 0.05, ClusterFar = 100 })
            world:add(eye, MainCamera)
            """);
        File.Copy(model, Path.Combine(game, "packages", "scene", "thing.glb"));
        string shot = Path.Combine(scratch.Root, "scene.png");

        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", Path.Combine(scratch.Root, "user"), "--headless", "--size", "320x180", "--frames", "3", "--screenshot", shot)));

        Assert.True(DistinctColors(shot) > 30, "the model is in the picture: more than the flat sky");
    }

    [Fact]
    public void AGameWithoutACameraDrawsOnlyItsPanelsOverTheBackground()
    {
        using var scratch = new Scratch();
        string game = Scene(scratch, "print('no camera')");
        string shot = Path.Combine(scratch.Root, "panels.png");

        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", Path.Combine(scratch.Root, "user"), "--headless", "--size", "64x36", "--frames", "2", "--screenshot", shot)));

        Assert.Equal(1, DistinctColors(shot)); // the cleared background and nothing else: no scene renderer was made
    }

    [Fact]
    public void PhysicsDebugDrawingAndScriptLinesAreDrawnOverTheWorld()
    {
        using var scratch = new Scratch();
        string body = """
            local Transform = world:component("Transform")
            local CameraComponent = world:component("CameraComponent")
            local MainCamera = world:component("MainCamera")
            local function place(position)
                local entity = world:create_entity()
                world:set(entity, Transform, { Position = position, Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
                return entity
            end
            local box = place(Vector3(0, 0, 0))
            local desc = BodyDesc.new()
            desc.shape = PhysicsShape.box(Vector3(0.5, 0.5, 0.5))
            desc.motion = "static"
            physics:add_body(box, desc)
            %s
            local eye = place(Vector3(0, 0.5, 4))
            world:set(eye, CameraComponent, { FovDegrees = 60, Near = 0.05, ClusterFar = 100 })
            world:add(eye, MainCamera)
            """;
        string plain = Scene(scratch, body.Replace("%s", ""));
        string shotPlain = Path.Combine(scratch.Root, "plain.png");
        Assert.Equal(0, Commands.Run(Options("--game", plain, "--user", Path.Combine(scratch.Root, "user"), "--headless", "--size", "160x90", "--frames", "2", "--screenshot", shotPlain)));
        Assert.Equal(1, DistinctColors(shotPlain)); // a camera and a body, and nothing to see: the sky

        using var scratch2 = new Scratch();
        string debug = Scene(scratch2, body.Replace("%s", "physics.debug_draw = true\nworld:system('Lines', { CameraComponent }, function() debug_draw.line(Vector3(-2, 0, 0), Vector3(2, 1, 0), Vector3(1, 0, 0)) end)"));
        string shotDebug = Path.Combine(scratch2.Root, "debug.png");
        Assert.Equal(0, Commands.Run(Options("--game", debug, "--user", Path.Combine(scratch2.Root, "user"), "--headless", "--size", "160x90", "--frames", "2", "--screenshot", shotDebug)));
        Assert.True(DistinctColors(shotDebug) > 1, "the body's wireframe and the script's line are in the picture");
    }

    private static (byte R, byte G, byte B) PixelAt(string screenshot, int x, int y)
    {
        Apogee.Content.TextureData image = Apogee.Content.ImageDecoder.Decode("shot", File.ReadAllBytes(screenshot), srgb: false, Apogee.Content.SamplerData.Default, generateMips: false);
        int offset = ((y * image.Width) + x) * 4;
        return (image.Mips[0][offset], image.Mips[0][offset + 1], image.Mips[0][offset + 2]);
    }

    [Fact]
    public void AGamesDocumentIsDrawnOverTheBackgroundAndAScriptHandlerRunsOnAClick()
    {
        using var scratch = new Scratch();
        string game = Scene(scratch, """
            local doc = ui:load_document_text('<rml><head><style>#box { margin: 20px; width: 50px; height: 50px; background-color: #ff0000; }</style></head><body><div id="box"></div></body></rml>')
            assert(doc ~= nil, "the document loads")
            doc:show()
            local clicks = 0
            doc:by_id("box"):listen("click", function(event)
                clicks += 1
                fs.write_text("user://clicked.txt", event.type .. ":" .. event.target.id .. ":" .. clicks)
            end)

            -- A system runs every frame: the first time it clicks the box, as a pointer would.
            local Ticker = world:component("Ticker", {})
            world:add(world:create_entity(), Ticker)
            local done = false
            world:system("ClickOnce", { Ticker }, function()
                if not done then
                    done = true
                    doc:by_id("box"):click()
                end
            end)
            """);
        string shot = Path.Combine(scratch.Root, "ui.png");
        string user = Path.Combine(scratch.Root, "user");

        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", user, "--headless", "--size", "160x90", "--frames", "3", "--screenshot", shot)));

        (byte r, byte g, byte b) = PixelAt(shot, 40, 40);
        Assert.True(r > 240 && g < 15 && b < 15, $"the box is red: {r},{g},{b}");
        (r, g, b) = PixelAt(shot, 120, 70);
        Assert.True(Math.Abs(r - 18) <= 2 && Math.Abs(g - 20) <= 2 && Math.Abs(b - 26) <= 2, $"the rest is the runtime's background: {r},{g},{b}");
        Assert.Equal("click:box:1", File.ReadAllText(Path.Combine(user, "clicked.txt")));
    }

    private static void AssertColor(string screenshot, int x, int y, int r, int g, int b, string what)
    {
        (byte pr, byte pg, byte pb) = PixelAt(screenshot, x, y);
        Assert.True(Math.Abs(pr - r) <= 3 && Math.Abs(pg - g) <= 3 && Math.Abs(pb - b) <= 3, $"{what}: expected about ({r}, {g}, {b}), got ({pr}, {pg}, {pb}) at {x},{y}");
    }

    [Fact]
    public void ADocumentsOwnScriptsAndDataModelRunInTheRuntime()
    {
        using var scratch = new Scratch();
        string game = Scene(scratch, """
            local doc = ui:load_document("mod://scene/ui/panel.rml")
            assert(doc ~= nil, "the document loads")
            doc:show()

            -- A system clicks the box on the second frame, as a pointer would: its onclick is in the document, its function in the document's script.
            local Ticker = world:component("Ticker", {})
            world:add(world:create_entity(), Ticker)
            local frame = 0
            world:system("Click", { Ticker }, function()
                frame += 1
                if frame == 2 then
                    doc:by_id("box"):click()
                end
            end)
            """);
        scratch.Write("game/packages/scene/ui/panel.rml", """
            <rml>
            <head>
              <style>#box { margin: 20px; width: 50px; height: 50px; }</style>
              <script src="panel.luau"/>
            </head>
            <body data-model="panel">
              <div id="box" data-style-background-color="color" onclick="press(event)"></div>
            </body>
            </rml>
            """);
        scratch.Write("game/packages/scene/ui/panel.luau", """
            local data = { color = "#ff0000", presses = 0 }
            local model = ui:open_data_model("panel", data)

            function press(event)
                data.presses += 1
                data.color = "#00ff00"
                model:dirty("color")
                fs.write_text("user://pressed.txt", `{event.type}:{data.presses}`)
            end
            """);
        string shot = Path.Combine(scratch.Root, "ui.png");
        string user = Path.Combine(scratch.Root, "user");

        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", user, "--headless", "--size", "160x90", "--frames", "6", "--screenshot", shot)));

        AssertColor(shot, 40, 40, 0, 255, 0, "the box, green after the click changed the model");
        Assert.Equal("click:1", File.ReadAllText(Path.Combine(user, "pressed.txt")));
    }
}
