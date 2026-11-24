using System;
using System.IO;
using System.Linq;
using Weft.Native.Tests;
using Weft.Host;
using Weft.Runtime;
using Xunit;
using Lua = Weft.Native.Luau.Lua;

namespace Weft.Runtime.Tests;

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
    public void NoArgumentsLaunchesTheGameNextToTheProgram() => Assert.Equal(RuntimeCommand.Launch, Parse().Command);

    [Fact]
    public void SmokeIsAFlag() => Assert.Equal(RuntimeCommand.Smoke, Parse("--smoke").Command);

    [Fact]
    public void ALaunchTakesTheRunOptions()
    {
        RuntimeOptions launch = Parse("--headless", "--frames", "3", "--user", "u", "--size", "640x360", "--gpu", "llvmpipe", "--screenshot", "s.png", "--overlay");
        Assert.Equal((RuntimeCommand.Launch, true, 3, "u", 640, 360), (launch.Command, launch.Headless, launch.Frames, launch.UserFolder, launch.Width, launch.Height));
        Assert.True(launch.SizeGiven);
        Assert.False(Parse("--headless").SizeGiven);
        Assert.Equal(["llvmpipe", "s.png", "True"], [launch.Gpu, launch.Screenshot, launch.Overlay.ToString()]);
    }

    [Fact]
    public void ALaunchIgnoresWhatItDoesNotKnowButACommandDoesNot()
    {
        Assert.True(RuntimeOptions.TryParse(["+connect_lobby", "109775241", "--headless", "-psn_0_12345"], out RuntimeOptions launch, out string? problem, gameNextToProgram: true), problem);
        Assert.Equal(RuntimeCommand.Launch, launch.Command);
        Assert.True(launch.Headless);
        Assert.Equal(["+connect_lobby", "109775241", "-psn_0_12345"], launch.Ignored);

        // Without a game next to the program, and after a command, an unknown argument is a mistake as before.
        Assert.Equal("Unknown argument '+connect_lobby'.", Fail("+connect_lobby", "1"));
        Assert.False(RuntimeOptions.TryParse(["--pack", "a", "b", "+connect_lobby"], out _, out string? afterPack, gameNextToProgram: true));
        Assert.Equal("Unknown argument '+connect_lobby'.", afterPack);
        Assert.False(RuntimeOptions.TryParse(["--smoke", "--nope"], out _, out _, gameNextToProgram: true));
        Assert.False(RuntimeOptions.TryParse(["--nope", "--game", "g"], out _, out _, gameNextToProgram: true));
    }

    [Fact]
    public void WatchDoesNotGoWithALaunch() => Assert.Equal("--watch goes with --game.", Fail("--watch"));

    [Fact]
    public void TheWindowComesFromTheManifestUnlessTheSizeIsGiven()
    {
        var window = new Weft.Content.Packages.GameWindow(1920, 1080, Fullscreen: true, Resizable: false);

        RuntimeOptions plain = Parse();
        plain.ApplyWindow(window);
        Assert.Equal((1920, 1080, true, false), (plain.Width, plain.Height, plain.Fullscreen, plain.Resizable));

        RuntimeOptions sized = Parse("--size", "640x360");
        sized.ApplyWindow(window);
        Assert.Equal((640, 360, true, false), (sized.Width, sized.Height, sized.Fullscreen, sized.Resizable));

        // Without a game.wgam the window is as it always was.
        RuntimeOptions none = Parse();
        Assert.Equal((1280, 720, false, true), (none.Width, none.Height, none.Fullscreen, none.Resizable));
    }

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
        Assert.Equal("--user goes with --game, or with no command.", Fail("--run", "a.luau", "--user", "x"));
        Assert.Equal("--user needs a value.", Fail("--game", "g", "--user"));
        Assert.Equal("--screenshot goes with --headless.", Fail("--game", "g", "--screenshot", "x.png"));
    }

    [Fact]
    public void ImportAndCookedHaveTheirOwnOptions()
    {
        RuntimeOptions import = Parse("--import", "game", "--force");
        Assert.Equal((RuntimeCommand.Import, "game", true), (import.Command, import.Path, import.Force));
        Assert.True(Parse("--pack", "a", "b", "--cooked").Cooked);
        Assert.Equal("--cooked goes with --pack or --export.", Fail("--run", "a.luau", "--cooked"));
        Assert.Equal("--force goes with --import.", Fail("--pack", "a", "b", "--force"));
    }

    [Fact]
    public void ExportTakesItsOptions()
    {
        RuntimeOptions export = Parse("--export", "Game", "Bin/export", "--target", "win-x64", "--cooked", "--runtime", "Bin/publish/win", "--console");
        Assert.Equal((RuntimeCommand.Export, "Game", "Bin/export", "win-x64", true, "Bin/publish/win", true), (export.Command, export.Path, export.Output, export.Target, export.Cooked, export.RuntimeDirectory, export.Console));

        RuntimeOptions plain = Parse("--target", "linux-x64", "--export", "Game", "out");
        Assert.Equal((false, null, false), (plain.Cooked, plain.RuntimeDirectory, plain.Console));
    }

    [Fact]
    public void ExportMistakesAreReported()
    {
        Assert.Equal("--export needs the game folder and the folder to export into.", Fail("--export", "Game"));
        Assert.Equal("--export needs the game folder and the folder to export into.", Fail("--export", "Game", "--target", "win-x64"));
        Assert.StartsWith("--export needs --target <rid>: linux-x64, win-x64, osx-arm64, osx-x64.", Fail("--export", "Game", "out"), StringComparison.Ordinal);
        Assert.Equal("--target needs a value.", Fail("--export", "Game", "out", "--target"));
        Assert.Equal("--target, --runtime and --console go with --export.", Fail("--pack", "a", "b", "--console"));
        Assert.Equal("--target, --runtime and --console go with --export.", Fail("--game", "g", "--target", "win-x64"));
        Assert.Equal("--target, --runtime and --console go with --export.", Fail("--runtime", "x"));
        Assert.Equal("--user goes with --game, or with no command.", Fail("--export", "g", "o", "--target", "linux-x64", "--user", "u"));
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
        RuntimeOptions pack = Parse("--pack", "Content", "data/game.wpak", "--split-mb", "64", "--level", "15", "--base", "a.wpak", "--base", "b.wpak");
        Assert.Equal((RuntimeCommand.Pack, "Content", "data/game.wpak", 64L, 15), (pack.Command, pack.Path, pack.Output, pack.SplitMegabytes, pack.Level));
        Assert.Equal(["a.wpak", "b.wpak"], pack.Bases);
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
        Assert.Equal("--headless, --frames, --screenshot, --overlay and --gpu go with --run, --game or no command.", Fail("--luau-defs", "out", "--headless"));
    }
}

public sealed class Scratch : IDisposable
{
    public Scratch()
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        NativeRequirements.Require(typeof(Weft.Native.Flecs.Ecs).Assembly, "flecs");
        Root = Path.Combine(Path.GetTempPath(), "weft-runtime-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
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
        scratch.Write($"{folder}/mod.wmod", $"<Mod id=\"{id}\" version=\"1.0.0\">{manifestInner}</Mod>");
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
    public void AGameRunWritesALogInItsUserFolder()
    {
        using var scratch = new Scratch();
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "print('hello from the log test')"));
        string user = System.IO.Path.Combine(scratch.Root, "user");

        Assert.Equal(0, Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", user, "--headless", "--frames", "2")));

        string log = File.ReadAllText(Assert.Single(Directory.GetFiles(System.IO.Path.Combine(user, "logs"), "*.log")));
        Assert.Contains("[Packages] core 1.0.0", log, StringComparison.Ordinal);
        Assert.Contains("[Runtime] game:", log, StringComparison.Ordinal);
        Assert.EndsWith("End of run." + Environment.NewLine, log, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(user, "logs"), "*.running")); // it ended cleanly
    }

    [Fact]
    public void AGameStartFindsThePreviousRunThatDidNotEnd()
    {
        using var scratch = new Scratch();
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "-- nothing"));
        string logs = System.IO.Path.Combine(scratch.Root, "user", "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(System.IO.Path.Combine(logs, "20260101-000000-7.log"), "12:00:00.000 INF [Game] the last line of a dead run\n");
        // A pid that no process has: the largest a pid can be on every OS here.
        File.WriteAllText(System.IO.Path.Combine(logs, "20260101-000000-7.running"), "pid=2147483646\nstart=0\nlog=20260101-000000-7.log\n");

        Assert.Equal(0, Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", System.IO.Path.Combine(scratch.Root, "user"), "--headless", "--frames", "1")));

        string report = File.ReadAllText(Assert.Single(Directory.GetFiles(System.IO.Path.Combine(scratch.Root, "user", "crashes"), "crash-*-unclean-exit*.txt")));
        Assert.Contains("did not end cleanly", report, StringComparison.Ordinal);
        Assert.Contains("the last line of a dead run", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelLoadsByUidAndASavedSceneFindsAModelThatWasMoved()
    {
        using var scratch = new Scratch();
        var uid = Weft.Core.Assets.AssetUid.New();
        scratch.Write("game/models/thing.glb", "stands in for a model: loading one only needs the file");
        scratch.Write("game/models/thing.glb.wimp", $"<Import uid=\"{uid}\"/>");
        Game(scratch, "scene", "<Script path=\"main.luau\"/>", user: false, ("main.luau", $$"""
            local ModelRenderer = world:component("ModelRenderer")
            local id = models:load("res://models/thing.glb")
            assert(id ~= 0 and models:load("{{uid}}") == id, "the uid and the path share one id")
            assert(not pcall(function() models:load("{{Weft.Core.Assets.AssetUid.New()}}") end), "an unknown uid is an error")

            assert(assets:uid_of("res://models/thing.glb") == "{{uid}}")
            assert(assets:uid_of("res://models/none.glb") == nil)
            assert(assets:resolve("{{uid}}") == "res://models/thing.glb")
            assert(assets:resolve("res://gone.glb|{{uid}}") == "res://models/thing.glb", "the uid follows a stale path")
            assert(assets:resolve("res://models/thing.glb") == "res://models/thing.glb")
            assert(assets:resolve("res://nothing.glb") == nil)
            assert(assets:ref_of("res://models/thing.glb") == "res://models/thing.glb|{{uid}}")
            assert(not pcall(function() assets:resolve("uid://nonsense") end), "text that is not a reference is an error")

            local thing = world:create_entity("thing")
            world:set(thing, ModelRenderer, { Model = id })
            fs.write_text("user://scene.txt", world:save_scene())
            """));
        string game = System.IO.Path.Combine(scratch.Root, "game");
        string user = System.IO.Path.Combine(scratch.Root, "user");

        // The first run saves a scene with the model; the model is then renamed and moved with its settings, and the second run loads that scene.
        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", user, "--headless", "--frames", "1")));
        Assert.Contains($"res://models/thing.glb|{uid}", File.ReadAllText(System.IO.Path.Combine(user, "scene.txt")));

        Directory.CreateDirectory(System.IO.Path.Combine(game, "props"));
        File.Move(System.IO.Path.Combine(game, "models", "thing.glb"), System.IO.Path.Combine(game, "props", "vehicle.glb"));
        File.Move(System.IO.Path.Combine(game, "models", "thing.glb.wimp"), System.IO.Path.Combine(game, "props", "vehicle.glb.wimp"));
        Directory.Delete(System.IO.Path.Combine(game, "packages", "scene"), recursive: true);
        Game(scratch, "check", "<Script path=\"main.luau\"/>", user: false, ("main.luau", """
            local ModelRenderer = world:component("ModelRenderer")
            world:load_scene(fs.read_text("user://scene.txt"))
            local model = world:get(world:lookup("thing"), ModelRenderer).Model
            fs.write_text("user://loaded.txt", tostring(model == models:load("res://props/vehicle.glb")) .. " " .. tostring(model ~= 0))
            """));
        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", user, "--headless", "--frames", "1")));
        Assert.Equal("true true", File.ReadAllText(System.IO.Path.Combine(user, "loaded.txt")));
    }

    [Fact]
    public void AGameLoadsASceneFileAndFindsAMovedModelThroughItsUidAfterAnImport()
    {
        using var scratch = new Scratch();
        var modelUid = Weft.Core.Assets.AssetUid.New();
        scratch.Write("game/models/thing.glb", "stands in for a model: loading one only needs the file");
        scratch.Write("game/models/thing.glb.wimp", $"<Import uid=\"{modelUid}\"/>");
        scratch.Write("game/scenes/level.wscn", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Scene version="1">
              <Entity id="1" name="thing"><ModelRenderer Model="res://models/thing.glb|{modelUid}"/></Entity>
            </Scene>
            """);
        string game = System.IO.Path.Combine(scratch.Root, "game");
        string user = System.IO.Path.Combine(scratch.Root, "user");

        // The import gives the scene its UID (it fails on the stand-in model, which is no glTF: that does not stop the UIDs).
        Assert.Equal(2, Commands.Import(Options("--import", game)));
        Weft.Core.Assets.AssetUid sceneUid = Weft.Content.Assets.AuthoredAssets.ReadUid(File.ReadAllBytes(System.IO.Path.Combine(game, "scenes", "level.wscn")));
        Assert.False(sceneUid.IsNone);

        // The scene and the model are both renamed and moved: nothing at the old paths, only the UIDs lead on.
        Directory.CreateDirectory(System.IO.Path.Combine(game, "props"));
        Directory.CreateDirectory(System.IO.Path.Combine(game, "levels"));
        File.Move(System.IO.Path.Combine(game, "models", "thing.glb"), System.IO.Path.Combine(game, "props", "vehicle.glb"));
        File.Move(System.IO.Path.Combine(game, "models", "thing.glb.wimp"), System.IO.Path.Combine(game, "props", "vehicle.glb.wimp"));
        File.Move(System.IO.Path.Combine(game, "scenes", "level.wscn"), System.IO.Path.Combine(game, "levels", "first.wscn"));
        Game(scratch, "check", "<Script path=\"main.luau\"/>", user: false, ("main.luau", $$"""
            local ModelRenderer = world:component("ModelRenderer")
            assert(not pcall(function() world:load_scene_file("res://scenes/level.wscn") end), "the old path leads nowhere")
            assert(not pcall(function() world:load_scene_file("uid://0000000000zzz") end), "an unknown uid is an error")
            local made = world:load_scene_file("{{sceneUid}}")
            local model = world:get(world:lookup("thing"), ModelRenderer).Model
            fs.write_text("user://loaded.txt", #made .. " " .. tostring(model == models:load("res://props/vehicle.glb")) .. " " .. tostring(model ~= 0))
            """));

        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", user, "--headless", "--frames", "1")));
        Assert.Equal("1 true true", File.ReadAllText(System.IO.Path.Combine(user, "loaded.txt")));
    }

    [Fact]
    public void AGameInstantiatesAPrefabFromLuau()
    {
        using var scratch = new Scratch();
        scratch.Write("game/prefabs/lamp.wprf", """
            <?xml version="1.0" encoding="utf-8"?>
            <Prefab version="1" nextId="4">
              <Entity id="1" name="Lamp">
                <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
                <Entity id="2" name="Bulb"><Transform Position="0 1.8 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
                <Entity id="3" name="Base"><Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
              </Entity>
            </Prefab>
            """);
        string game = System.IO.Path.Combine(scratch.Root, "game");
        string user = System.IO.Path.Combine(scratch.Root, "user");

        // The import gives the prefab its UID: it is authored, so it has no sidecar.
        Assert.Equal(0, Commands.Import(Options("--import", game)));
        Weft.Core.Assets.AssetUid uid = Weft.Content.Assets.AuthoredAssets.ReadUid(File.ReadAllBytes(System.IO.Path.Combine(game, "prefabs", "lamp.wprf")));
        Assert.False(uid.IsNone);
        Game(scratch, "check", "<Script path=\"main.luau\"/>", user: false, ("main.luau", $$"""
            assert(not pcall(function() world:instantiate("res://prefabs/none.wprf") end), "a missing prefab is an error")
            assert(not pcall(function() world:instantiate("uid://4k2m9x7q1w3ze") end), "an unknown uid is an error")
            local lamp = world:instantiate("{{uid}}")
            local again = world:instantiate("res://prefabs/lamp.wprf", lamp)
            fs.write_text("user://prefab.txt", #world:children(lamp) .. " " .. tostring(world:prefab_of(lamp) == "res://prefabs/lamp.wprf|{{uid}}") .. " " .. tostring(world:prefab_of(world:children(lamp)[1])) .. " " .. #world:children(again))
            """));

        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", user, "--headless", "--frames", "1")));
        Assert.Equal("3 true nil 2", File.ReadAllText(System.IO.Path.Combine(user, "prefab.txt")));   // Bulb and Base, and the second instance made under the first
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
        Assert.False(Weft.Host.GameLibrary.EscapeQuits);

        // The next run starts with the defaults again.
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "print('nothing')"));
        Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", user, "--headless", "--frames", "1"));
        Assert.True(Weft.Host.GameLibrary.EscapeQuits);
        Assert.False(Weft.Host.GameLibrary.QuitRequested);
    }

    private static string PackedGame(Scratch scratch, Weft.Content.Packs.PackKey? key, string reportText)
    {
        string data = System.IO.Path.Combine(scratch.Root, "game", "data");
        Directory.CreateDirectory(data);
        using (var writer = new Weft.Content.Packs.PackWriter(System.IO.Path.Combine(data, "game.wpak"), new Weft.Content.Packs.PackWriterOptions { Key = key }))
        {
            writer.Add("packages/core/mod.wmod", System.Text.Encoding.UTF8.GetBytes("<Mod id=\"core\" version=\"1.0.0\"><Script path=\"main.luau\"/></Mod>"));
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

        using (var patch = new Weft.Content.Packs.PackWriter(System.IO.Path.Combine(game, "data", "patch.wpak")))
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
        scratch.Write("src/packages/core/mod.wmod", "<Mod id=\"core\" version=\"1.0.0\"><Script path=\"main.luau\"/></Mod>");
        scratch.Write("src/packages/core/main.luau", "fs.write_text('user://report.txt', 'version one')");
        scratch.Write("src/packages/core/old.txt", "to be removed");
        scratch.Write("src/.hidden/secret.txt", "left out");
        string data = System.IO.Path.Combine(scratch.Root, "game", "data");
        string user = System.IO.Path.Combine(scratch.Root, "user");

        Assert.Equal(0, Commands.Pack(Options("--pack", System.IO.Path.Combine(scratch.Root, "src"), System.IO.Path.Combine(data, "game.wpak"))));
        using (var pack = Weft.Content.Packs.PackSource.Open(System.IO.Path.Combine(data, "game.wpak")))
        {
            Assert.Equal(3, pack.FileCount);
        }

        File.WriteAllText(System.IO.Path.Combine(scratch.Root, "src", "packages", "core", "main.luau"), "fs.write_text('user://report.txt', 'version two')");
        File.Delete(System.IO.Path.Combine(scratch.Root, "src", "packages", "core", "old.txt"));
        Assert.Equal(0, Commands.Pack(Options("--pack", System.IO.Path.Combine(scratch.Root, "src"), System.IO.Path.Combine(data, "patch.wpak"), "--base", System.IO.Path.Combine(data, "game.wpak"))));
        using (var patch = Weft.Content.Packs.PackSource.Open(System.IO.Path.Combine(data, "patch.wpak")))
        {
            Assert.Equal(1, patch.FileCount); // only main.luau changed
            Assert.Equal(Weft.Content.FileSystem.FileKind.Removed, ((Weft.Content.FileSystem.IFileSource)patch).Kind("packages/core/old.txt"));
        }

        Assert.Equal(0, Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", user, "--headless")));
        Assert.Equal("version two", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
        Assert.Equal(1, Commands.Pack(Options("--pack", System.IO.Path.Combine(scratch.Root, "nope"), System.IO.Path.Combine(data, "x.wpak"))));
    }

    [Fact]
    public void AnImportedProjectIsPackedWithItsArtifactsInsteadOfItsSources()
    {
        using var scratch = new Scratch();
        byte[] rgba = new byte[16 * 16 * 4];
        new Random(3).NextBytes(rgba);
        Directory.CreateDirectory(System.IO.Path.Combine(scratch.Root, "src", "textures"));
        File.WriteAllBytes(System.IO.Path.Combine(scratch.Root, "src", "textures", "ground.png"), Weft.Content.Png.Encode(16, 16, rgba));
        scratch.Write("src/readme.txt", "kept");
        string src = System.IO.Path.Combine(scratch.Root, "src");
        string pack = System.IO.Path.Combine(scratch.Root, "out", "game.wpak");

        Assert.Equal(1, Commands.Pack(Options("--pack", src, pack, "--cooked"))); // nothing imported yet
        Assert.Equal(0, Commands.Import(Options("--import", src)));
        Assert.True(File.Exists(System.IO.Path.Combine(src, "imported", "textures", "ground.png.ktx2")));
        Assert.Equal(0, Commands.Pack(Options("--pack", src, pack, "--cooked")));

        using var packed = Weft.Content.Packs.PackSource.Open(pack);
        var files = new Weft.Content.FileSystem.VirtualFileSystem();
        files.Mount("res://", packed);
        Assert.Equal(["res://imported/assets.xml", "res://imported/index.xml", "res://imported/textures/ground.png.ktx2", "res://readme.txt"], files.EnumerateFiles("res://", recursive: true));
        Assert.Equal(16, new Weft.Content.Import.ContentLoader(files).LoadTexture("res://textures/ground.png", true, Weft.Content.TextureCaps.None).Width);

        // A second import changes nothing, and --force does it all again.
        Assert.Equal(0, Commands.Import(Options("--import", src)));
        Assert.Equal(0, Commands.Import(Options("--import", src, "--force")));
        Assert.Equal(1, Commands.Import(Options("--import", System.IO.Path.Combine(scratch.Root, "nope"))));
        File.WriteAllText(System.IO.Path.Combine(src, "textures", "broken.png"), "not an image");
        Assert.Equal(2, Commands.Import(Options("--import", src)));
    }

    [Fact]
    public void ACookedPackHoldsItsScenesAsBinaryXmlAtTheSamePath()
    {
        using var scratch = new Scratch();
        byte[] rgba = new byte[16 * 16 * 4];
        Directory.CreateDirectory(System.IO.Path.Combine(scratch.Root, "src", "textures"));
        File.WriteAllBytes(System.IO.Path.Combine(scratch.Root, "src", "textures", "ground.png"), Weft.Content.Png.Encode(16, 16, rgba));
        scratch.Write("src/scenes/start.wscn", "<Scene version=\"1\"><Entity id=\"1\" name=\"ground\"/></Scene>");
        string src = System.IO.Path.Combine(scratch.Root, "src");
        string pack = System.IO.Path.Combine(scratch.Root, "out", "game.wpak");

        Assert.Equal(0, Commands.Import(Options("--import", src)));
        Assert.Equal(0, Commands.Pack(Options("--pack", src, pack, "--cooked")));

        using var packed = Weft.Content.Packs.PackSource.Open(pack);
        var files = new Weft.Content.FileSystem.VirtualFileSystem();
        files.Mount("res://", packed);
        Assert.True(Weft.Content.Xml.XmlBinary.IsBinary(files.ReadAllBytes("res://scenes/start.wscn")));
        Assert.Equal("ground", Weft.Content.Xml.XmlFile.Read(files, "res://scenes/start.wscn").Children[0].Attribute("name"));
        Assert.NotEqual(Weft.Core.Assets.AssetUid.None, Weft.Content.Assets.AuthoredAssets.ReadUid(files.ReadAllBytes("res://scenes/start.wscn")));
    }

    private static string FixtureFolder(string name)
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = System.IO.Path.GetDirectoryName(directory))
        {
            string candidate = System.IO.Path.Combine(directory, "Tests", "Fixtures", name);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"Tests/Fixtures/{name} was not found above {AppContext.BaseDirectory}.");
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (string directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(System.IO.Path.Combine(to, System.IO.Path.GetRelativePath(from, directory)));
        }

        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, System.IO.Path.Combine(to, System.IO.Path.GetRelativePath(from, file)));
        }
    }

    [Fact]
    public void ACookedGameLoadsItsSceneAndDefsFromThePack()
    {
        if (!System.Security.Cryptography.AesGcm.IsSupported)
        {
            Assert.Skip("AES-GCM is not available on this machine.");
        }

        const string Hex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
        using var scratch = new Scratch();
        string game = System.IO.Path.Combine(scratch.Root, "game");
        CopyFolder(FixtureFolder("ShippedGame"), game);
        string fromFolder = System.IO.Path.Combine(scratch.Root, "user-folder");
        string fromPack = System.IO.Path.Combine(scratch.Root, "user-pack");

        // The game as it is in its folder: the scene and the definitions are text.
        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", fromFolder, "--headless", "--frames", "2")));
        string scene = File.ReadAllText(System.IO.Path.Combine(fromFolder, "scene.txt"));
        string def = File.ReadAllText(System.IO.Path.Combine(fromFolder, "def.txt"));
        Assert.Equal("6 entities, a main camera and its child", scene);   // the camera and its marker, and the scene's instance of a crate (its lid, the lid's handle and its body)
        Assert.Equal("fixture weighs 3, 2 tags, read from Crates", def);

        // Import, pack cooked and encrypted, and ship only the pack with the manifest.
        string shipped = System.IO.Path.Combine(scratch.Root, "shipped");
        Directory.CreateDirectory(System.IO.Path.Combine(shipped, "data"));
        File.Copy(System.IO.Path.Combine(game, "game.wgam"), System.IO.Path.Combine(shipped, "game.wgam"));
        string pack = System.IO.Path.Combine(shipped, "data", "game.wpak");
        string? before = Environment.GetEnvironmentVariable("WEFT_PACK_KEY");
        try
        {
            Environment.SetEnvironmentVariable("WEFT_PACK_KEY", Hex);
            Assert.Equal(0, Commands.Import(Options("--import", game)));
            Assert.Equal(0, Commands.Pack(Options("--pack", game, pack, "--cooked")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEFT_PACK_KEY", before);
        }

        // Every scene, prefab and definition file in the pack is cooked; the manifests, the document and the scripts stay text.
        Weft.Content.Packs.PackKey key = Weft.Content.Packs.PackKey.Parse(Hex);
        using (var packed = Weft.Content.Packs.PackSource.Open(pack, key))
        {
            var files = new Weft.Content.FileSystem.VirtualFileSystem();
            files.Mount("res://", packed);
            string[] cooked = ["res://packages/core/scenes/start.wscn", "res://packages/core/prefabs/crate.wprf", "res://packages/core/prefabs/lid.wprf", "res://packages/core/data/crates.xml", "res://imported/assets.xml"];
            foreach (string path in files.EnumerateFiles("res://", recursive: true))
            {
                bool isDocument = path.EndsWith(".wscn", StringComparison.Ordinal) || path.EndsWith(".wprf", StringComparison.Ordinal) || (path.StartsWith("res://packages/core/data/", StringComparison.Ordinal) && path.EndsWith(".xml", StringComparison.Ordinal));
                Assert.Equal(isDocument || path == "res://imported/assets.xml", Weft.Content.Xml.XmlBinary.IsBinary(files.ReadAllBytes(path)));
            }

            Assert.All(cooked, path => Assert.True(Weft.Content.Xml.XmlBinary.IsBinary(files.ReadAllBytes(path)), path));
            Assert.StartsWith("<Mod ", files.ReadAllText("res://packages/core/mod.wmod"), StringComparison.Ordinal);
            Assert.StartsWith("<rml>", files.ReadAllText("res://packages/core/ui/fixture.rml"), StringComparison.Ordinal);
            Assert.StartsWith("<Mod ", files.ReadAllText("res://dev_mods/patcher/mod.wmod"), StringComparison.Ordinal);
            Assert.False(files.FileExists("res://icon.png")); // the source of an imported asset is not in a cooked pack
        }

        Assert.False(File.Exists(System.IO.Path.Combine(shipped, "icon.png")));
        Assert.Equal(0, Commands.Launch(Options("--user", fromPack, "--headless", "--frames", "2"), shipped.Replace('\\', '/'), key));

        // The cooked game read what the game in its folder read.
        Assert.Equal(scene, File.ReadAllText(System.IO.Path.Combine(fromPack, "scene.txt")));
        Assert.Equal(def, File.ReadAllText(System.IO.Path.Combine(fromPack, "def.txt")));
        Assert.Equal("core ran", File.ReadAllText(System.IO.Path.Combine(fromPack, "ran.txt")));
        Assert.Equal("document ran", File.ReadAllText(System.IO.Path.Combine(fromPack, "document.txt")));
    }

    [Fact]
    public void ACookedGameInstantiatesPrefabsFromThePack()
    {
        using var scratch = new Scratch();
        string game = System.IO.Path.Combine(scratch.Root, "game");
        CopyFolder(FixtureFolder("ShippedGame"), game);
        string fromFolder = System.IO.Path.Combine(scratch.Root, "user-folder");
        string fromPack = System.IO.Path.Combine(scratch.Root, "user-pack");

        // The game as it is in its folder: the scene holds an instance of a prefab that holds another (with an override of a handle), and the script makes one more.
        Assert.Equal(0, Commands.Run(Options("--game", game, "--user", fromFolder, "--headless", "--frames", "2")));
        const string Expected = "scene handle 90, made handle 30, 2 children, 2 in the scene's";
        Assert.Equal(Expected, File.ReadAllText(System.IO.Path.Combine(fromFolder, "prefab.txt")));

        // Import and pack cooked (the import gives a prefab its UID; the fixture's have one already), then ship the pack with the manifest alone: the game's sources are deleted.
        string shipped = System.IO.Path.Combine(scratch.Root, "shipped");
        Directory.CreateDirectory(System.IO.Path.Combine(shipped, "data"));
        File.Copy(System.IO.Path.Combine(game, "game.wgam"), System.IO.Path.Combine(shipped, "game.wgam"));
        string pack = System.IO.Path.Combine(shipped, "data", "game.wpak");
        Assert.Equal(0, Commands.Import(Options("--import", game)));
        Assert.Equal(0, Commands.Pack(Options("--pack", game, pack, "--cooked")));
        Directory.Delete(game, recursive: true);

        // Each prefab in the pack is binary XML at its own path (it starts with the magic of cooked XML), and it still has its UID.
        using (var packed = Weft.Content.Packs.PackSource.Open(pack))
        {
            var files = new Weft.Content.FileSystem.VirtualFileSystem();
            files.Mount("res://", packed);
            foreach (string path in new[] { "res://packages/core/prefabs/crate.wprf", "res://packages/core/prefabs/lid.wprf" })
            {
                byte[] bytes = files.ReadAllBytes(path);
                Assert.True(Weft.Content.Xml.XmlBinary.IsBinary(bytes), path);
                Assert.Equal("WXMB", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
                Assert.NotEqual(Weft.Core.Assets.AssetUid.None, Weft.Content.Assets.AuthoredAssets.ReadUid(bytes));
            }

            Weft.Content.Xml.XmlNode crate = Weft.Content.Xml.XmlFile.Read(files, "res://packages/core/prefabs/crate.wprf");
            Assert.Equal("Prefab", crate.Name);
            Assert.Equal("mod://core/prefabs/lid.wprf|uid://4n6v1c8t2m7zh", crate.Children.First(c => c.Name == "Entity").Children.First(c => c.Name == "Entity").Attribute("prefab"));
        }

        // The cooked game, from the pack alone, makes the same entities.
        Assert.Equal(0, Commands.Launch(Options("--user", fromPack, "--headless", "--frames", "2"), shipped.Replace('\\', '/')));
        Assert.Equal(Expected, File.ReadAllText(System.IO.Path.Combine(fromPack, "prefab.txt")));
    }

    [Fact]
    public void ShadersAreCompiledByImportAndPackedWithoutTheirSourcesOrTheCompiler()
    {
        Weft.Native.Tests.NativeRequirements.Require(typeof(Weft.Native.Slang.Slang).Assembly, "slang_c");
        using var scratch = new Scratch();
        string src = System.IO.Path.Combine(scratch.Root, "src");
        string pack = System.IO.Path.Combine(scratch.Root, "out", "game.wpak");
        scratch.Write("src/shaders/library.slang", "float scale() { return 2.0; }\n");
        scratch.Write("src/shaders/run.slang", "import library;\nRWStructuredBuffer<float> output;\n[shader(\"compute\")] [numthreads(8, 1, 1)]\nvoid run(uint3 id : SV_DispatchThreadID) { output[id.x] = scale(); }\n");

        Assert.Equal(0, Commands.Import(Options("--import", src)));
        Assert.True(File.Exists(System.IO.Path.Combine(src, "imported", "shaders", "run.slang.wshd")));
        Assert.False(File.Exists(System.IO.Path.Combine(src, "imported", "shaders", "library.slang.wshd")));
        Assert.Equal(0, Commands.Pack(Options("--pack", src, pack, "--cooked")));

        using var packed = Weft.Content.Packs.PackSource.Open(pack);
        var files = new Weft.Content.FileSystem.VirtualFileSystem();
        files.Mount("res://", packed);
        Assert.Equal(["res://imported/assets.xml", "res://imported/index.xml", "res://imported/shaders/run.slang.wshd"], files.EnumerateFiles("res://", recursive: true));
        var library = new Weft.Rendering.Shaders.ShaderLibrary(files, createCompiler: _ => throw new InvalidOperationException("the compiler was started"));
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
        string game = PackedGame(scratch, Weft.Content.Packs.PackKey.Parse(Hex), "sealed ok");
        string user = System.IO.Path.Combine(scratch.Root, "user");
        string? before = Environment.GetEnvironmentVariable("WEFT_PACK_KEY");
        try
        {
            Environment.SetEnvironmentVariable("WEFT_PACK_KEY", null);
            Assert.Equal(2, Commands.Run(Options("--game", game, "--user", user, "--headless")));

            Environment.SetEnvironmentVariable("WEFT_PACK_KEY", Hex.Replace('0', '1'));
            Assert.Equal(2, Commands.Run(Options("--game", game, "--user", user, "--headless")));

            Environment.SetEnvironmentVariable("WEFT_PACK_KEY", "not a key");
            Assert.Equal(2, Commands.Run(Options("--game", game, "--user", user, "--headless")));

            Environment.SetEnvironmentVariable("WEFT_PACK_KEY", Hex);
            Assert.Equal(0, Commands.Run(Options("--game", game, "--user", user, "--headless")));
            Assert.Equal("sealed ok", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEFT_PACK_KEY", before);
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
        NativeRequirements.Require(typeof(Weft.Native.Slang.Slang).Assembly, "slang_c");
        using var scratch = new Scratch();
        const string Shader = "RWStructuredBuffer<float> output;\n[shader(\"compute\")] [numthreads(8, 1, 1)]\nvoid run(uint3 id : SV_DispatchThreadID) { output[id.x] = 1.0; }\n";
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "local x = 1"), ("shaders/own.slang", Shader));
        string game = System.IO.Path.Combine(scratch.Root, "game");
        string user = System.IO.Path.Combine(scratch.Root, "user");
        string cache = System.IO.Path.Combine(scratch.Root, "cache");
        string shader = System.IO.Path.Combine(game, "packages", "core", "shaders", "own.slang");

        using (ScriptSession session = ScriptSession.StartGame(game, user, watch: true))
        {
            Weft.Rendering.Shaders.ShaderLibrary library = session.CreateShaderLibrary(cache);
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
    public void AGameKeepsItsSettingsInItsUserFolder()
    {
        using var scratch = new Scratch();
        scratch.Write("game/game.wgam", "<Game name=\"Settled\" organization=\"Test\" version=\"1.0.0\"><Window width=\"1024\" height=\"600\"/></Game>");
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", """
            settings:define("game.difficulty", { type = "choice", default = "normal", choices = { "easy", "normal", "hard" } })
            input.map:add_action("jump", { "world" }, { "Space" })
            local report = `{settings:get("game.difficulty")} {settings:get("window.width")} {input.map:get_bindings("jump")[1]}`
            fs.write_text("user://report.txt", report)
            settings:set("game.difficulty", "hard")
            settings:set("audio.volume", 0.4)
            input.map:set_bindings("jump", { "Hold J" })
            """));
        string game = System.IO.Path.Combine(scratch.Root, "game");
        string user = System.IO.Path.Combine(scratch.Root, "user");

        using (ScriptSession first = ScriptSession.StartGame(game, user))
        {
            Assert.Equal("normal 1024 Space", File.ReadAllText(System.IO.Path.Combine(user, "report.txt"))); // game.wgam's window is the default
            Assert.Equal(0.4f, first.Audio.Volume);
            Assert.True(first.Settings.IsDirty);
        }

        // The session wrote the file when it ended: what differs from the defaults, and the rebinding.
        Assert.Equal(
            "<settings version=\"1\">\n  <value key=\"audio.volume\">0.4</value>\n  <value key=\"game.difficulty\">hard</value>\n  <bindings>\n    <action id=\"jump\">Hold J</action>\n  </bindings>\n</settings>\n",
            File.ReadAllText(System.IO.Path.Combine(user, "settings.xml")));

        using ScriptSession second = ScriptSession.StartGame(game, user);
        Assert.Equal("hard 1024 Hold J", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
        Assert.Equal(0.4f, second.Audio.Volume);
        Assert.Equal(0.4, second.Settings.GetNumber("audio.volume"));
    }

    [Fact]
    public void ABasePackageThatFailsIsAnErrorOfTheGame()
    {
        using var scratch = new Scratch();
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "error('base bug')"));

        Assert.Equal(2, Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", System.IO.Path.Combine(scratch.Root, "user"), "--headless")));
    }

    private sealed class CapturingSink : Weft.Core.Logging.ILogSink
    {
        public System.Collections.Generic.List<Weft.Core.Logging.LogEntry> Entries { get; } = [];

        public void Write(in Weft.Core.Logging.LogEntry entry)
        {
            lock (Entries)
            {
                Entries.Add(entry);
            }
        }
    }

    [Fact]
    public void AGameXmlSetsTheGameVersionThatModsAreCheckedAgainst()
    {
        using var scratch = new Scratch();
        scratch.Write("game/game.wgam", "<Game name=\"Versioned\" organization=\"Test\" version=\"1.4.0\"/>");
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "fs.write_text('user://core.txt', 'core')"));
        foreach ((string id, string range) in new[] { ("future", "^2.0"), ("current", "^1.0") })
        {
            scratch.Write($"user/mods/{id}/mod.wmod", $"<Mod id=\"{id}\" version=\"1.0.0\" gameVersion=\"{range}\"><Dependency id=\"core\"/><Script path=\"main.luau\"/></Mod>");
            scratch.Write($"user/mods/{id}/main.luau", $"fs.write_text('user://moddata/{id}/ran.txt', 'ran')");
        }

        string user = System.IO.Path.Combine(scratch.Root, "user");
        var sink = new CapturingSink();
        Weft.Core.Logging.Log.AddSink(sink);
        int exit;
        try
        {
            exit = Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", user, "--headless", "--frames", "1"));
        }
        finally
        {
            Weft.Core.Logging.Log.RemoveSink(sink);
        }

        Assert.Equal(0, exit);
        Assert.True(File.Exists(System.IO.Path.Combine(user, "core.txt")));
        Assert.True(File.Exists(System.IO.Path.Combine(user, "moddata", "current", "ran.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(user, "moddata", "future", "ran.txt")));
        lock (sink.Entries)
        {
            Assert.Contains(sink.Entries, e => e.Message.Contains("future", StringComparison.Ordinal) && e.Message.Contains("needs game version ^2.0, but this is 1.4.0", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheSteamAppIdComesFromGameXmlAndSteamAppidTxtIsAFallbackWithAWarning()
    {
        using var scratch = new Scratch();
        string folder = System.IO.Path.Combine(scratch.Root, "game");
        Directory.CreateDirectory(folder);
        Weft.Content.Packages.GameManifest WithId(string id) =>
            Weft.Content.Packages.GameManifest.TryParse($"<Game name=\"a\" organization=\"b\" version=\"1.0.0\"{id}/>", "game.wgam", out Weft.Content.Packages.GameManifest? m, out _) ? m! : throw new InvalidOperationException();

        Assert.Equal(0u, ScriptSession.SteamAppIdFor(null, folder, out string? warning));
        Assert.Null(warning);

        scratch.Write("game/steam_appid.txt", "1234\n");
        Assert.Equal(1234u, ScriptSession.SteamAppIdFor(null, folder, out warning));
        Assert.Contains("steam_appid.txt", warning, StringComparison.Ordinal);
        Assert.Contains("game.wgam", warning, StringComparison.Ordinal);
        Assert.Equal(1234u, ScriptSession.SteamAppIdFor(WithId(""), folder, out warning)); // a manifest that names no app id leaves the file as it was
        Assert.NotNull(warning);

        Assert.Equal(480u, ScriptSession.SteamAppIdFor(WithId(" steamAppId=\"480\""), folder, out warning));
        Assert.Null(warning);

        scratch.Write("game/steam_appid.txt", "not a number");
        Assert.Equal(0u, ScriptSession.SteamAppIdFor(null, folder, out _));
    }

    [Fact]
    public void TheEntrySceneIsLoadedBeforeTheScripts()
    {
        using var scratch = new Scratch();
        using (var world = new Weft.Ecs.World())
        {
            world.CreateEntity("hero");
            scratch.Write("game/scenes/main.wscn", world.SaveScene());
        }

        scratch.Write("game/game.wgam", "<Game name=\"Scened\" organization=\"Test\" version=\"1.0.0\"><EntryScene path=\"scenes/main.wscn\"/></Game>");
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "fs.write_text('user://seen.txt', world:lookup('hero') ~= 0 and 'hero is here' or 'no hero')"));
        string user = System.IO.Path.Combine(scratch.Root, "user");

        Assert.Equal(0, Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", user, "--headless", "--frames", "1")));

        Assert.Equal("hero is here", File.ReadAllText(System.IO.Path.Combine(user, "seen.txt")));
    }

    [Fact]
    public void AnEntrySceneThatIsNotThereIsAnErrorOfTheGame()
    {
        using var scratch = new Scratch();
        scratch.Write("game/game.wgam", "<Game name=\"Scened\" organization=\"Test\" version=\"1.0.0\"><EntryScene path=\"scenes/missing.wscn\"/></Game>");
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "fs.write_text('user://ran.txt', 'ran')"));
        string user = System.IO.Path.Combine(scratch.Root, "user");

        Assert.Equal(2, Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", user, "--headless", "--frames", "1")));
        Assert.False(File.Exists(System.IO.Path.Combine(user, "ran.txt")));
    }

    [Fact]
    public void ABrokenGameXmlIsAnErrorOfTheGame()
    {
        using var scratch = new Scratch();
        scratch.Write("game/game.wgam", "<Game name=\"No version\" organization=\"Test\"/>");
        Game(scratch, "core", "<Script path=\"main.luau\"/>", user: false, ("main.luau", "fs.write_text('user://ran.txt', 'ran')"));
        string user = System.IO.Path.Combine(scratch.Root, "user");

        Assert.Equal(2, Commands.Run(Options("--game", System.IO.Path.Combine(scratch.Root, "game"), "--user", user, "--headless", "--frames", "1")));
        Assert.False(File.Exists(System.IO.Path.Combine(user, "ran.txt")));

        var thrown = Assert.Throws<Weft.Content.ContentException>(() => ScriptSession.StartGame(System.IO.Path.Combine(scratch.Root, "game"), user));
        Assert.Contains("game.wgam:1:", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("version", thrown.Message, StringComparison.Ordinal);
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

    private static string Slashes(string path) => path.Replace('\\', '/');

    [Fact]
    public void TheGameIsFoundNextToTheProgramOrInTheBundlesResources()
    {
        using var scratch = new Scratch();
        string beside = System.IO.Path.Combine(scratch.Root, "beside");
        scratch.Write("beside/game.wgam", "<Game name=\"Beside\" organization=\"Test\" version=\"1.0.0\"/>");
        // The locator answers with forward slashes on every OS.
        Assert.Equal(Slashes(beside), GameLocator.Find(beside));
        Assert.Equal(Slashes(beside), GameLocator.Find(beside + "/"));

        string bundle = System.IO.Path.Combine(scratch.Root, "Beside.app", "Contents");
        scratch.Write("Beside.app/Contents/Resources/game.wgam", "<Game name=\"Bundled\" organization=\"Test\" version=\"1.0.0\"/>");
        Directory.CreateDirectory(System.IO.Path.Combine(bundle, "MacOS"));
        Assert.Equal(Slashes(System.IO.Path.Combine(bundle, "Resources")), GameLocator.Find(System.IO.Path.Combine(bundle, "MacOS")));

        // The program's own folder wins over the bundle's Resources, and a folder with neither has no game.
        scratch.Write("Beside.app/Contents/MacOS/game.wgam", "<Game name=\"Own\" organization=\"Test\" version=\"1.0.0\"/>");
        Assert.Equal(Slashes(System.IO.Path.Combine(bundle, "MacOS")), GameLocator.Find(System.IO.Path.Combine(bundle, "MacOS")));
        Directory.CreateDirectory(System.IO.Path.Combine(scratch.Root, "empty"));
        Assert.Null(GameLocator.Find(System.IO.Path.Combine(scratch.Root, "empty")));
    }

    private static string ShippedRoot(Scratch scratch, string gameXml)
    {
        string root = System.IO.Path.Combine(scratch.Root, "shipped");
        scratch.Write("shipped/game.wgam", gameXml);
        scratch.Write("shipped/secret.txt", "loose, so not part of the game");
        scratch.Write("shipped/packages/loose/mod.wmod", "<Mod id=\"loose\" version=\"1.0.0\"><Script path=\"main.luau\"/></Mod>");
        scratch.Write("shipped/packages/loose/main.luau", "fs.write_text('user://loose.txt', 'a loose package ran')");
        Directory.CreateDirectory(System.IO.Path.Combine(root, "data"));
        using var writer = new Weft.Content.Packs.PackWriter(System.IO.Path.Combine(root, "data", "game.wpak"));
        writer.Add("packages/core/mod.wmod", "<Mod id=\"core\" version=\"1.0.0\"><Script path=\"main.luau\"/></Mod>"u8.ToArray());
        writer.Add("packages/core/main.luau", System.Text.Encoding.UTF8.GetBytes("""
            local greeting = fs.read_text("res://greeting.txt")
            fs.write_text("user://report.txt", "secret=" .. tostring(fs.exists("res://secret.txt")) .. " greeting=" .. tostring(greeting))
            """));
        writer.Add("greeting.txt", "hello from the pack"u8.ToArray());
        writer.Finish();
        return root;
    }

    [Fact]
    public void AShippedGameSeesOnlyItsPacks()
    {
        using var scratch = new Scratch();
        string root = ShippedRoot(scratch, "<Game name=\"Shipped\" organization=\"Test\" version=\"1.0.0\"/>");
        string user = System.IO.Path.Combine(scratch.Root, "user");

        int exit = Commands.Launch(Options("--user", user, "--headless", "--frames", "2"), root);

        Assert.Equal(0, exit);
        Assert.Equal("secret=false greeting=hello from the pack", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(user, "loose.txt")));

        // A game folder run with --game still sees its loose files.
        Assert.Equal(0, Commands.Run(Options("--game", root, "--user", user, "--headless", "--frames", "1")));
        Assert.Equal("secret=true greeting=hello from the pack", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
        Assert.True(File.Exists(System.IO.Path.Combine(user, "loose.txt")));
    }

    /// <summary>A runtime folder for the machine running the test: a native program of its platform (the .NET host) with the pack key slot appended, and a library.</summary>
    private static string FakeRuntimeFolder(Scratch scratch)
    {
        bool windows = OperatingSystem.IsWindows();
        string folder = System.IO.Path.Combine(scratch.Root, "runtime");
        Directory.CreateDirectory(folder);

        // On Linux the program is the .NET host with the slot appended. A Windows export rewrites the program's resources, which needs a layout it can read (an appended slot would be an overlay), so there
        // the program is a small executable that has the slot in its code.
        // On macOS the slot has to be inside the code the program's signature covers (the export writes it there and signs the program again), which an appended one is not: the program is a small
        // signed Mach-O for the machine's architecture that has the slot in its code.
        byte[] program = windows
            ? Weft.Export.Tests.TinyPe.Typical(Weft.Export.PackKeySlot.Template.ToArray()).Build()
            : OperatingSystem.IsMacOS()
                ? Weft.Export.Tests.SyntheticMachO.Build(0x8000, [new(2, 14)], embed: Weft.Export.PackKeySlot.Template.ToArray(), cpuType: Weft.Export.ExportTarget.Host.Arch == "arm64" ? 0x0100000Cu : 0x01000007u).Bytes
                : [.. File.ReadAllBytes(Environment.ProcessPath!), .. Weft.Export.PackKeySlot.Template.ToArray()];
        File.WriteAllBytes(System.IO.Path.Combine(folder, windows ? "Weft.Runtime.exe" : "Weft.Runtime"), program);
        File.WriteAllText(System.IO.Path.Combine(folder, windows ? "flecs.dll" : "libflecs.so"), "a native library");
        return folder;
    }

    [Fact]
    public void AnExportedGameRunsFromItsFolderOnItsEncryptedPacks()
    {
        Assert.True(Weft.Export.ExportTarget.TryParse(Weft.Export.ExportTarget.Host.Rid, out _, out _), "the machine running the tests is not a platform the engine exports for");
        using var scratch = new Scratch();
        scratch.Write("game/game.wgam", "<Game name=\"Exported\" organization=\"Test\" version=\"1.0.0\" executable=\"ExportedGame\"/>");
        scratch.Write("game/secret.txt", "it is in the pack");
        scratch.Write("game/dev_mods/dev/mod.wmod", "<Mod id=\"dev\" version=\"1.0.0\"><Script path=\"main.luau\"/></Mod>");
        scratch.Write("game/dev_mods/dev/main.luau", "fs.write_text('user://dev.txt', 'a dev mod ran')");
        scratch.Write("game/packages/core/mod.wmod", "<Mod id=\"core\" version=\"1.0.0\"><Script path=\"main.luau\"/></Mod>");
        scratch.Write("game/packages/core/main.luau", "fs.write_text('user://report.txt', 'secret=' .. fs.read_text('res://secret.txt'))");
        string runtime = FakeRuntimeFolder(scratch);
        string output = System.IO.Path.Combine(scratch.Root, "export");
        string user = System.IO.Path.Combine(scratch.Root, "user");
        Weft.Content.Packs.PackKey key = Weft.Content.Packs.PackKey.Parse("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

        RuntimeOptions export = Options("--export", System.IO.Path.Combine(scratch.Root, "game"), output, "--target", Weft.Export.ExportTarget.Host.Rid, "--runtime", runtime, "--cooked");
        Assert.Equal(0, ExportCommand.Export(export, key));

        string root = Weft.Export.ExportTarget.Host.Os == "osx" ? System.IO.Path.Combine(output, "Exported.app", "Contents", "Resources") : output;
        Assert.True(File.Exists(System.IO.Path.Combine(root, "game.wgam")));
        string pack = System.IO.Path.Combine(root, "data", "game.wpak");
        Assert.NotEqual("WPAK", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(pack), 0, 4));
        Assert.Equal(0, Commands.Launch(Options("--user", user, "--headless", "--frames", "2"), root.Replace('\\', '/'), key));
        Assert.Equal("secret=it is in the pack", File.ReadAllText(System.IO.Path.Combine(user, "report.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(user, "dev.txt")));
    }

    [Fact]
    public void ABadExportIsAnExitCodeAndNothingIsWritten()
    {
        using var scratch = new Scratch();
        string output = System.IO.Path.Combine(scratch.Root, "export");
        Directory.CreateDirectory(System.IO.Path.Combine(scratch.Root, "game"));

        Assert.Equal(1, ExportCommand.Export(Options("--export", System.IO.Path.Combine(scratch.Root, "game"), output, "--target", "win-arm64"), null));
        Assert.Equal(2, ExportCommand.Export(Options("--export", System.IO.Path.Combine(scratch.Root, "game"), output, "--target", "linux-x64", "--runtime", FakeRuntimeFolder(scratch)), null));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ALaunchWithoutAGameXmlOrWithABrokenOneIsAnError()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(System.IO.Path.Combine(scratch.Root, "none"));
        Assert.Equal(1, Commands.Launch(Options("--headless"), System.IO.Path.Combine(scratch.Root, "none")));

        scratch.Write("broken/game.wgam", "<Game name=\"No version\" organization=\"Test\"/>");
        Assert.Equal(2, Commands.Launch(Options("--headless"), System.IO.Path.Combine(scratch.Root, "broken")));
    }

    [Fact]
    public void NoGameNextToTheProgramIsExitCodeOne()
    {
        using var scratch = new Scratch();
        string program = typeof(Commands).Assembly.Location;
        Assert.Null(GameLocator.Find(System.IO.Path.GetDirectoryName(program)!));

        // The runtime as a program, started from its own output folder (no game.wgam there): it says what to pass instead and exits with 1.
        var info = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = scratch.Root };
        info.ArgumentList.Add(program);
        using var process = System.Diagnostics.Process.Start(info)!;
        string stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("No game.wgam next to the program", stderr, StringComparison.Ordinal);
        Assert.Contains("--smoke", stderr, StringComparison.Ordinal);
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
        session.Input.Press("Space", Weft.Input.Modifiers.None, Weft.Input.InputDevices.Now);
        session.Input.Press("W", Weft.Input.Modifiers.None, Weft.Input.InputDevices.Now);
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
        Weft.Reflection.Entity ball = session.World.Lookup("ball");

        for (int frame = 0; frame < 60; frame++)
        {
            session.Progress(1f / 60); // physics first, then the world
        }

        double y = session.World.Get<Weft.Ecs.WorldTransform>(ball).Position.Y;
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
        Assert.Equal(Weft.Audio.AudioSystem.IsInstalled(), session.Audio.IsActive); // active exactly when OpenAL Soft is installed (it mixes into nowhere here)
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

        string definitions = File.ReadAllText(Path.Combine(target, "weft.d.luau"));
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
public sealed class WorldRenderingTests : Weft.Graphics.Tests.GpuTest
{
    public WorldRenderingTests(Weft.Graphics.Tests.GpuFixture gpu)
        : base(gpu)
    {
    }

    private static string? SampleModel()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string candidate = Path.Combine(directory, "Tests", "Weft.Rendering.Tests", "Fixtures", "models", "NormalTangentTest.glb");
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
        scratch.Write("game/packages/scene/mod.wmod", "<Mod id=\"scene\" version=\"1.0.0\"><Script path=\"main.luau\"/></Mod>");
        scratch.Write("game/packages/scene/main.luau", script);
        return Path.Combine(scratch.Root, "game");
    }

    private static int DistinctColors(string screenshot)
    {
        Weft.Content.TextureData image = Weft.Content.ImageDecoder.Decode("shot", File.ReadAllBytes(screenshot), srgb: false, Weft.Content.SamplerData.Default, generateMips: false);
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
        Weft.Content.TextureData image = Weft.Content.ImageDecoder.Decode("shot", File.ReadAllBytes(screenshot), srgb: false, Weft.Content.SamplerData.Default, generateMips: false);
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
