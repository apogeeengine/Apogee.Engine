using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Content.Packs;
using Apogee.Core.Logging;
using Apogee.DevTools;
using Apogee.Graphics;
using Apogee.Graphics.Shaders;
using Apogee.Platform;
using Apogee.Rendering.Shaders;
using Apogee.Host;
using Apogee.Scripting;

namespace Apogee.Runtime;

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
        try
        {
            ScriptSession.RegisterLibraries();
            using var host = new ImGuiHost();
            using ScriptSession session = game ? ScriptSession.StartGame(options.Path, options.UserFolder, options.Watch, realAudio: !options.Headless, realSteam: !options.Headless) : ScriptSession.Start(path, realAudio: !options.Headless);
            if (session.Steam.RestartRequested)
            {
                return 0; // Steam starts the game again, through itself
            }

            int frames;
            int result;
            if (!options.Headless)
            {
                result = RuntimeLoop.Windowed(options, host, session, "Apogee: " + name, out frames);
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
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        catch (Exception e) when (e is Apogee.Graphics.GraphicsException or ShaderCompileException or ContentException or PlatformException or IOException or FormatException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    /// <summary>
    /// Publishes a mod folder as a Workshop item of the game in <c>./steam_appid.txt</c> (or updates one). It makes something public under the author's Steam account, so without
    /// <c>--confirm</c> it only says what it would send.
    /// </summary>
    public static int WorkshopUpload(RuntimeOptions options)
    {
        string folder = System.IO.Path.GetFullPath(options.Path);
        var upload = new Apogee.Steam.WorkshopUpload
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
                "friends" => Apogee.Steam.WorkshopVisibility.FriendsOnly,
                "unlisted" => Apogee.Steam.WorkshopVisibility.Unlisted,
                "public" => Apogee.Steam.WorkshopVisibility.Public,
                _ => Apogee.Steam.WorkshopVisibility.Private,
            },
        };

        Console.WriteLine($"Workshop upload of {folder}");
        Console.WriteLine($"  item:       {(upload.ItemId is null ? "a new item" : "update " + upload.ItemId)}");
        Console.WriteLine($"  title:      {upload.Title ?? "(unchanged)"}");
        Console.WriteLine($"  visibility: {options.WorkshopVisibility}");
        Console.WriteLine($"  tags:       {(upload.Tags.Count == 0 ? "(none)" : string.Join(", ", upload.Tags))}");
        if (!File.Exists(System.IO.Path.Combine(folder, "mod.xml")))
        {
            Console.Error.WriteLine($"{folder} has no mod.xml: it is not a mod folder.");
            return 1;
        }

        if (!options.Confirm)
        {
            Console.WriteLine("Nothing was sent. Add --confirm to upload (the Steam client must be running, with the game's steam_appid.txt in the current folder).");
            return 0;
        }

        ScriptSession.RegisterLibraries();
        using var steam = new Apogee.Steam.SteamSystem(new Apogee.Steam.SteamOptions { RestartIfNecessary = false });
        if (!steam.IsActive)
        {
            Console.Error.WriteLine(steam.Status);
            return 2;
        }

        Apogee.Steam.WorkshopUploadResult? outcome = null;
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
                Apogee.Graphics.GpuInfo[] gpus = GraphicsDevice.EnumerateDevices(new GraphicsDeviceOptions { Backend = backend });
                if (gpus.Length == 0)
                {
                    Console.WriteLine($"{name}: unavailable: no GPU found");
                }

                foreach (Apogee.Graphics.GpuInfo gpu in gpus)
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
    /// Packs the files of a folder (the paths in the pack are relative to it; names that start with a dot are left out). Encrypted when <c>APOGEE_PACK_KEY</c> is set. With <c>--base</c> it is a
    /// patch pack: the packs given (in order, with the same key) are what the game has already, and the new pack holds the files that are new or changed, and a removal for each that is gone.
    /// </summary>
    public static int Pack(RuntimeOptions options)
    {
        string folder = System.IO.Path.GetFullPath(options.Path);
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"{folder} is not a folder.");
            return 1;
        }

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

        List<string> paths = [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Where(p => !p.Split('/').Any(segment => segment.StartsWith('.')))
            .Order(StringComparer.Ordinal)];
        if (options.Cooked)
        {
            var project = new VirtualFileSystem();
            project.Mount("res://", new DirectorySource(folder));
            IReadOnlyList<ImportEntry> entries = AssetImporter.LoadIndex(project);
            if (entries.Count == 0)
            {
                Console.Error.WriteLine($"{folder} has nothing imported (no imported/index.xml): run --import first.");
                return 1;
            }

            // What an artifact replaces (the asset, the files it was made from, its settings) is left out.
            var replaced = new HashSet<string>(StringComparer.Ordinal);
            foreach (ImportEntry entry in entries)
            {
                replaced.Add(entry.Source);
                replaced.Add(entry.Source + ".import.xml");
                replaced.UnionWith(entry.Dependencies);
            }

            paths = [.. paths.Where(p => !replaced.Contains(p))];
        }

        bool invalid = false;
        foreach (string path in paths)
        {
            if (!VfsPath.TryParse("pack://" + path, out _, out string? problem))
            {
                Console.Error.WriteLine($"{path}: {problem}");
                invalid = true;
            }
        }

        if (invalid)
        {
            return 1;
        }

        var existing = new VirtualFileSystem();
        var opened = new List<PackSource>();
        try
        {
            foreach (string basePack in options.Bases)
            {
                PackSource source = PackSource.Open(basePack, key);
                opened.Add(source);
                existing.Mount("res://", source);
            }

            using var writer = new PackWriter(options.Output, new PackWriterOptions { Key = key, SplitBytes = options.SplitMegabytes * 1024 * 1024, CompressionLevel = options.Level });
            long raw = 0;
            int added = 0;
            int unchanged = 0;
            foreach (string path in paths)
            {
                byte[] data = File.ReadAllBytes(System.IO.Path.Combine(folder, path));
                if (options.Bases.Count > 0 && existing.FileExists("res://" + path) && existing.ReadAllBytes("res://" + path).AsSpan().SequenceEqual(data))
                {
                    unchanged++;
                    continue;
                }

                writer.Add(path, data);
                raw += data.Length;
                added++;
            }

            int removed = 0;
            if (options.Bases.Count > 0)
            {
                var present = new HashSet<string>(paths, StringComparer.Ordinal);
                foreach (string gone in existing.EnumerateFiles("res://", recursive: true).Select(p => p["res://".Length..]).Where(p => !present.Contains(p)))
                {
                    writer.Remove(gone);
                    removed++;
                }
            }

            IReadOnlyList<string> parts = writer.Finish();
            long packed = parts.Sum(p => new FileInfo(p).Length);
            Console.WriteLine($"{System.IO.Path.GetFileName(options.Output)}: {added} files ({raw / 1024.0 / 1024.0:F1} MiB) in {parts.Count} part(s), {packed / 1024.0 / 1024.0:F1} MiB{(key is null ? "" : ", encrypted")}"
                + (options.Bases.Count > 0 ? $"; {unchanged} unchanged, {removed} removed" : ""));
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        finally
        {
            foreach (PackSource source in opened)
            {
                source.Dispose();
            }
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
        string definitions = System.IO.Path.Combine(options.Path, "apogee.d.luau");
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
            ],
            HiddenClasses = [nameof(SmokeActor)],
        };
}
