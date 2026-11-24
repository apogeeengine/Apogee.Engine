using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Weft.Content;
using Weft.Content.FileSystem;
using Weft.Content.Import;
using Weft.Content.Packages;
using Weft.Content.Packs;
using Weft.Core.Logging;
using Weft.Export;
using Weft.DevTools;
using Weft.Graphics;
using Weft.Graphics.Shaders;
using Weft.Platform;
using Weft.Rendering.Shaders;
using Weft.Host;
using Weft.Scripting;

namespace Weft.Runtime;

/// <summary>What the runtime does for each of its commands. Exit codes: 0 fine, 1 bad arguments or failed tests, 2 a script or the engine failed.</summary>
internal static class Commands
{
    public static int LuauTest(RuntimeOptions options)
    {
        ScriptSession.RegisterLibraries();
        if (!Directory.Exists(options.Path))
        {
            Console.Error.WriteLine($"{options.Path} is not a folder.");
            return 1;
        }

        TestRunResult run = LuauTestRunner.Run(options.Path, options.Filter, Console.Out);
        return run.Success ? 0 : 1;
    }

    public static int Run(RuntimeOptions options)
    {
        bool game = options.Command == RuntimeCommand.Game;
        if (game ? !Directory.Exists(options.Path) : !File.Exists(options.Path))
        {
            Console.Error.WriteLine($"{options.Path} does not exist.");
            return 1;
        }

        string path = System.IO.Path.GetFullPath(options.Path).Replace('\\', '/');
        string name = System.IO.Path.GetFileName(path);
        GameManifest? manifest = null;
        if (game && !TryLoadManifest(path, out manifest))
        {
            return 2;
        }

        return Execute(options, game ? path : null, manifest, name,
            () => game ? ScriptSession.StartGame(options.Path, options.UserFolder, options.Watch, realAudio: !options.Headless, realSteam: !options.Headless) : ScriptSession.Start(path, realAudio: !options.Headless));
    }

    /// <summary>
    /// Runs the game that is next to the program: <paramref name="root"/> holds its <c>game.wgam</c> and its <c>data/*.wpak</c>, and nothing else of the folder is the game's (<see cref="GameStart.PacksOnly"/>).
    /// The window is the manifest's.
    /// </summary>
    /// <param name="key">The key of the packs; null: the one an export wrote into the executable, else the environment's.</param>
    public static int Launch(RuntimeOptions options, string root, PackKey? key = null)
    {
        if (!TryLoadManifest(root, out GameManifest? manifest))
        {
            return 2;
        }

        if (manifest is null)
        {
            Console.Error.WriteLine($"No {GameManifest.FileName} in {root}.");
            return 1;
        }

        // The key an export wrote into the executable (PackKeySlot); without one the session asks the environment (WEFT_PACK_KEY).
        PackKey? packKey;
        try
        {
            packKey = key ?? PackKeySlot.TryRead();
        }
        catch (System.IO.InvalidDataException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        return Execute(options, root, manifest, manifest.Name,
            () => ScriptSession.StartGame(new GameStart { Folder = root, UserFolder = options.UserFolder, RealAudio = !options.Headless, RealSteam = !options.Headless, PacksOnly = true, Key = packKey }));
    }

    /// <summary>The manifest of a game folder (null when there is none); false, with the problems on stderr, when it is wrong.</summary>
    private static bool TryLoadManifest(string folder, out GameManifest? manifest)
    {
        manifest = GameManifest.Load(folder, out IReadOnlyList<string> problems);
        if (problems.Count == 0)
        {
            return true;
        }

        Console.Error.WriteLine($"{GameManifest.FileName} of {folder} is wrong:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
        return false;
    }

    /// <summary>The loops of <c>--run</c>, <c>--game</c> and a launch: the window is the manifest's (its name for the title, its size unless <c>--size</c> was given), or the old title of a game without one.</summary>
    private static int Execute(RuntimeOptions options, string? root, GameManifest? manifest, string name, Func<ScriptSession> start)
    {
        if (manifest is not null)
        {
            options.ApplyWindow(manifest.Window);
        }

        FileLogSink? logFile = null;
        try
        {
            // A game writes its log into its user folder; a single script (--run) is a dev tool and has none.
            logFile = root is null ? null : RunDiagnostics.Start(ScriptSession.UserDirectoryFor(root, options.UserFolder, manifest), manifest?.Name ?? name, manifest?.Version.ToString());
            ScriptSession.RegisterLibraries();
            using var host = new ImGuiHost();
            using ScriptSession session = start();
            if (session.Steam.RestartRequested)
            {
                return 0; // Steam starts the game again, through itself
            }

            int frames;
            int result;
            if (!options.Headless)
            {
                result = RuntimeLoop.Windowed(options, host, session, manifest?.Name ?? "Weft: " + name, out frames, manifest, root);
            }
            else if (options.Screenshot is not null)
            {
                result = RuntimeLoop.Offscreen(options, host, session, out frames);
            }
            else
            {
                result = RuntimeLoop.Headless(options, host, session, out frames);
            }

            Log.Info("Runtime", $"{name}: {frames} frames, {session.World.Frame} run");
            return result;
        }
        catch (ScriptException e)
        {
            return Fail(e, logFile);
        }
        catch (Exception e) when (e is Weft.Graphics.GraphicsException or ShaderCompileException or ContentException or PlatformException or IOException or FormatException)
        {
            return Fail(e, logFile);
        }
        finally
        {
            logFile?.Dispose();
        }
    }

    /// <summary>Says why the run failed, on stderr and in the run's log file (which is still open).</summary>
    private static int Fail(Exception e, FileLogSink? logFile)
    {
        Console.Error.WriteLine(e.Message);
        logFile?.Write(new LogEntry(DateTimeOffset.Now, LogLevel.Error, "Runtime", e.Message));
        return 2;
    }

    /// <summary>
    /// Publishes a mod folder as a Workshop item of the game in <c>./steam_appid.txt</c> (or updates one). It makes something public under the author's Steam account, so without
    /// <c>--confirm</c> it only says what it would send.
    /// </summary>
    public static int WorkshopUpload(RuntimeOptions options)
    {
        string folder = System.IO.Path.GetFullPath(options.Path);
        var upload = new Weft.Steam.WorkshopUpload
        {
            Folder = folder,
            ItemId = options.WorkshopItem,
            Title = options.WorkshopTitle,
            Description = options.WorkshopDescription,
            Preview = options.WorkshopPreview,
            Tags = options.WorkshopTags,
            ChangeNote = options.WorkshopNote,
            Visibility = options.WorkshopVisibility switch
            {
                "friends" => Weft.Steam.WorkshopVisibility.FriendsOnly,
                "unlisted" => Weft.Steam.WorkshopVisibility.Unlisted,
                "public" => Weft.Steam.WorkshopVisibility.Public,
                _ => Weft.Steam.WorkshopVisibility.Private,
            },
        };

        Console.WriteLine($"Workshop upload of {folder}");
        Console.WriteLine($"  item:       {(upload.ItemId is null ? "a new item" : "update " + upload.ItemId)}");
        Console.WriteLine($"  title:      {upload.Title ?? "(unchanged)"}");
        Console.WriteLine($"  visibility: {options.WorkshopVisibility}");
        Console.WriteLine($"  tags:       {(upload.Tags.Count == 0 ? "(none)" : string.Join(", ", upload.Tags))}");
        if (!File.Exists(System.IO.Path.Combine(folder, "mod.wmod")))
        {
            Console.Error.WriteLine($"{folder} has no mod.wmod: it is not a mod folder.");
            return 1;
        }

        if (!options.Confirm)
        {
            Console.WriteLine("Nothing was sent. Add --confirm to upload (the Steam client must be running, with the game's steam_appid.txt in the current folder).");
            return 0;
        }

        ScriptSession.RegisterLibraries();
        using var steam = new Weft.Steam.SteamSystem(new Weft.Steam.SteamOptions { RestartIfNecessary = false });
        if (!steam.IsActive)
        {
            Console.Error.WriteLine(steam.Status);
            return 2;
        }

        Weft.Steam.WorkshopUploadResult? outcome = null;
        steam.UploadWorkshopItem(upload, result => outcome = result);
        DateTime deadline = DateTime.UtcNow.AddMinutes(30);
        string lastStage = "";
        while (outcome is null && DateTime.UtcNow < deadline)
        {
            steam.Update();
            (string stage, ulong sent, ulong total) = steam.UploadProgress();
            if (stage.Length > 0 && stage != lastStage)
            {
                Console.WriteLine($"  {stage}{(total > 0 ? $" ({sent} of {total} bytes)" : "")}");
                lastStage = stage;
            }

            System.Threading.Thread.Sleep(100);
        }

        if (outcome is not { } done)
        {
            Console.Error.WriteLine("Steam did not answer in 30 minutes.");
            return 2;
        }

        Console.WriteLine(done.Ok ? $"Uploaded: workshop item {done.ItemId} (https://steamcommunity.com/sharedfiles/filedetails/?id={done.ItemId})" : $"Failed: {done.Message}");
        if (done.NeedsLegalAgreement)
        {
            Console.WriteLine("Steam asks you to accept the Workshop legal agreement first: https://steamcommunity.com/sharedfiles/workshoplegalagreement");
        }

        return done.Ok ? 0 : 2;
    }

    /// <summary>
    /// Lists the GPUs of each graphics API, one line each, for people and for CI (which runs the Metal tests only when a line says <c>metal: … ok</c>):
    /// <c>vulkan: Name (Discrete, Vulkan 1.3) ok</c>, <c>metal: Name (Integrated, Metal 3, macOS 15.1.0) unusable: tier 2 argument buffers</c>, or <c>metal: unavailable: reason</c>.
    /// </summary>
    public static int GpuInfo()
    {
        foreach (GraphicsBackend backend in new[] { GraphicsBackend.Vulkan, GraphicsBackend.Metal })
        {
            string name = backend.ToString().ToLowerInvariant();
            try
            {
                Weft.Graphics.GpuInfo[] gpus = GraphicsDevice.EnumerateDevices(new GraphicsDeviceOptions { Backend = backend });
                if (gpus.Length == 0)
                {
                    Console.WriteLine($"{name}: unavailable: no GPU found");
                }

                foreach (Weft.Graphics.GpuInfo gpu in gpus)
                {
                    Console.WriteLine($"{name}: {gpu.Name} ({gpu.Kind}, {gpu.Api}) {(gpu.Suitable ? "ok" : "unusable: " + string.Join("; ", gpu.Reasons))}");
                }
            }
            catch (Exception e) when (e is GraphicsException or DllNotFoundException or EntryPointNotFoundException)
            {
                Console.WriteLine($"{name}: unavailable: {e.Message}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Packs the files of a folder (the paths in the pack are relative to it; names that start with a dot are left out). Encrypted when <c>WEFT_PACK_KEY</c> is set. With <c>--base</c> it is a
    /// patch pack: the packs given (in order, with the same key) are what the game has already, and the new pack holds the files that are new or changed, and a removal for each that is gone.
    /// </summary>
    public static int Pack(RuntimeOptions options)
    {
        PackKey? key;
        try
        {
            key = PackKey.FromEnvironment();
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"{PackKey.EnvironmentVariable}: {e.Message}");
            return 1;
        }

        try
        {
            PackReport report = GamePacker.Pack(new PackRequest
            {
                Folder = options.Path,
                Output = options.Output,
                Cooked = options.Cooked,
                Key = key,
                Bases = options.Bases,
                SplitBytes = options.SplitMegabytes * 1024 * 1024,
                CompressionLevel = options.Level,
            });
            Console.WriteLine($"{System.IO.Path.GetFileName(options.Output)}: {report.Added} files ({report.RawBytes / 1024.0 / 1024.0:F1} MiB) in {report.Parts.Count} part(s), {report.PackedBytes / 1024.0 / 1024.0:F1} MiB{(key is null ? "" : ", encrypted")}"
                + (report.CookedDocuments > 0 ? $", {report.CookedDocuments} cooked" : "")
                + (options.Bases.Count > 0 ? $"; {report.Unchanged} unchanged, {report.Removed} removed" : ""));
            return 0;
        }
        catch (ContentException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    public static int Import(RuntimeOptions options)
    {
        string folder = System.IO.Path.GetFullPath(options.Path);
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"{folder} is not a folder.");
            return 1;
        }

        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(folder, writable: true));
        try
        {
            ImportReport report = new AssetImporter(files, ScriptSession.Importers).Run(options.Force);
            Console.WriteLine($"Imported {report.Imported.Count}, up to date {report.UpToDate.Count}, removed {report.Removed.Count}, failed {report.Failed.Count}.");
            foreach ((string source, string message) in report.Failed)
            {
                Console.Error.WriteLine($"{source}: {message}");
            }

            return report.Success ? 0 : 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    public static int LuauDefs(RuntimeOptions options)
    {
        ScriptSession.RegisterLibraries();
        Directory.CreateDirectory(options.Path);
        string definitions = System.IO.Path.Combine(options.Path, "weft.d.luau");
        File.WriteAllText(definitions, DefinitionWriter.Write(HostDefinitions()), new UTF8Encoding(false));

        // luau-lsp resolves require("@engine/…") from copies of the engine's modules next to the definitions.
        string engine = System.IO.Path.Combine(options.Path, "engine");
        Directory.CreateDirectory(engine);
        foreach ((string name, string source) in LuauVm.EngineModules)
        {
            string file = System.IO.Path.Combine(engine, name + ".luau");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, source, new UTF8Encoding(false));
        }

        Log.Info("Runtime", $"Wrote {definitions} and {LuauVm.EngineModules.Count} engine modules");
        return 0;
    }

    public static int LuauDocs(RuntimeOptions options)
    {
        ScriptSession.RegisterLibraries();
        ReferenceWriter.WriteTo(options.Path, HostDefinitions());
        Log.Info("Runtime", $"Wrote the Luau API reference into {options.Path}");
        return 0;
    }

    /// <summary>The globals the host sets in its VMs and the classes it keeps out of the definitions, which <c>--luau-defs</c> and <c>--luau-docs</c> describe.</summary>
    private static DefinitionOptions HostDefinitions() =>
        new DefinitionOptions
        {
            Globals =
            [
                new HostGlobal("world", "World", "The world the script runs in (--run sets it)."),
                new HostGlobal("physics", "PhysicsWorld", "The physics of the world: give entities bodies with `physics:add_body`, ask what is where with `physics:raycast`. It is stepped before the world each frame."),
                new HostGlobal("audio", "AudioSystem", "The game's sound: `audio:load_sound` and the sounds it plays need OpenAL Soft installed (`audio.is_active` says); an entity with the AudioListener component is the ear."),
                new HostGlobal("steam", "SteamSystem", "The game's connection to Steam (the base game only; mods cannot use it): achievements, stats, leaderboards, rich presence, the overlay, cloud saves and Workshop mods. `steam.is_active` says whether Steam is running; when it is not every call does nothing."),
                new HostGlobal("net", "NetSystem", "The game's network (the base game only; mods cannot use it): `net:listen(port)` and `net:connect(address)` make `NetConnection`s, `connection:send(text)` sends, and the signals `connection_requested`, `connected`, `message` and `disconnected` say what happened. `net.is_active` says whether the network library is installed; when it is not every call does nothing."),
                new HostGlobal("models", "ModelLibrary", "The models entities draw: `models:load(path)` returns the id to put in a `ModelRenderer` component (the same id for the same path, 0 means none). A game with an entity that has the `MainCamera` component is drawn through it; `Light` entities light it."),
                new HostGlobal("ui", "UiSystem", "The game's user interface: `ui:load_document(path)` loads RML from a file, `ui:create_document()` makes an empty one; documents can carry `<script>`s, `on*` attributes and data models (`ui:open_data_model`); elements are `UiElement`s you make, move and listen to. It is sized to the screen, fed the player's input, and drawn over the picture."),
                new HostGlobal("input", "InputActionRouter", "The player's input as actions: define them on `input.map`, set `input.contexts` each frame, read `input:get_strength(action)` and connect `input.action_fired`."),
                new HostGlobal("settings", "Settings", "The player's settings, kept in `user://settings.xml`: `settings:define(key, spec)` the ones your game or mod needs, `settings:get` and `settings:set` them, and connect `settings.changed`. The engine's own (`window.*`, `graphics.vsync`, `audio.volume`) are defined; a mod can change only `mod.<its id>.*`."),
            ],
            HiddenClasses = [nameof(SmokeActor)],
        };
}
