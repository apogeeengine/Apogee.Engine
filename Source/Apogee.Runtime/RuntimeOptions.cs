using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

namespace Apogee.Runtime;

internal enum RuntimeCommand
{
    /// <summary>No arguments: load every native library and run the smoke tests.</summary>
    Smoke,
    Help,

    /// <summary>Run the <c>*.test.luau</c> files under a folder.</summary>
    LuauTest,

    /// <summary>Run a script in a world, for some frames.</summary>
    Run,

    /// <summary>Run a game folder: its packages and mods, in a world.</summary>
    Game,

    /// <summary>Write the Luau type definitions.</summary>
    LuauDefs,

    /// <summary>Write the Luau API as Markdown pages.</summary>
    LuauDocs,

    /// <summary>Pack the files of a folder.</summary>
    Pack,

    /// <summary>Import the assets of a folder into the engine's formats.</summary>
    Import,

    /// <summary>List the GPUs of every graphics API and whether the engine can use each.</summary>
    GpuInfo,

    /// <summary>Publish a mod folder as a Steam Workshop item (or update one).</summary>
    WorkshopUpload,
}

/// <summary>The command line of the runtime, parsed by hand so that it stays trim- and AOT-safe.</summary>
internal sealed class RuntimeOptions
{
    public const string Usage = """
        Usage: Apogee.Runtime [command]

          (no command)                         load the native libraries and run the smoke tests
          --luau-test <folder> [--luau-filter <text>]
                                               run the *.test.luau files under the folder; exit code 1 if any fails
          --run <file.luau> [--frames <n>]     run a script with a `world` global, in a window until it is closed (Esc), or for n frames
          --game <folder> [--user <folder>]    run a game: the folder is res://, its packages (res://packages/*) and the player's mods (user://mods) load and their scripts
                                               run in a world, then as --run does (--user: the folder of the player's files, default: the platform's for the game's name)
          --watch                              with --game: reload what changes while it runs (scripts that were required, definitions, assets); res:// becomes writable for re-imports
          --headless                           with --run or --game: no window and no GPU, n frames (default 1) of the world and the scripts' gui functions
          --screenshot <file.png>              with --headless (--run or --game): render the last frame, over the cleared background, to this image
          --size <w>x<h>                       the window or the image (default 1280x720)
          --overlay                            draw the dev overlay in --screenshot images (the window always has it; F1 toggles)
          --gpu <name>                         prefer the GPU whose name contains this (APOGEE_GPU)
          --pack <folder> <out.pack> [--split-mb <n>] [--level <1-22>] [--base <pack>]...
                                               pack the files of a folder (encrypted when APOGEE_PACK_KEY is set); with --base a patch pack: only what is new or changed, and removals
                                               --cooked: pack the imported artifacts instead of the sources they replace (run --import first)
          --import <folder> [--force]          import the models and images of a game folder into imported/ (only what changed, unless --force)
          --luau-defs <folder>                 write apogee.d.luau (and the engine's Luau modules) for luau-lsp
          --luau-docs <folder>                 write the Luau API as Markdown pages (the Manual's API reference)
          --workshop-upload <mod folder> [--item <id>] [--title <text>] [--description <text>] [--preview <image>] [--visibility private|friends|unlisted|public] [--tags <a,b>] [--note <text>] [--confirm]
                                               publish a mod folder (it has a mod.xml) as a Steam Workshop item of the game in ./steam_appid.txt, or update --item; without --confirm it only says what it would do
          --gpu-info                           list the GPUs of Vulkan and Metal, one line each: `vulkan: <name> (<kind>, <api>) ok` or `... unusable: <reasons>`
          --help

        Exit codes: 0 fine, 1 bad arguments or failed tests, 2 a script or the engine failed, 3 Vulkan validation errors.
        """;

    public RuntimeCommand Command { get; private set; } = RuntimeCommand.Smoke;

    /// <summary>The folder or file the command works on.</summary>
    public string Path { get; private set; } = "";

    /// <summary>With --pack: the pack to write.</summary>
    public string Output { get; private set; } = "";

    /// <summary>With --pack: the size at which a pack continues in a new part, in megabytes.</summary>
    public long SplitMegabytes { get; private set; } = 1536;

    /// <summary>With --pack: the zstd level.</summary>
    public int Level { get; private set; } = 12;

    /// <summary>With --pack: the packs the game has already, for a patch pack.</summary>
    public List<string> Bases { get; } = [];

    /// <summary>With --game: reload files that change while the game runs.</summary>
    public bool Watch { get; private set; }

    /// <summary>With --pack: pack imported artifacts instead of their sources.</summary>
    public bool Cooked { get; private set; }

    /// <summary>With --import: import everything again.</summary>
    public bool Force { get; private set; }

    /// <summary>With --game: where user:// is.</summary>
    public string? UserFolder { get; private set; }

    public string? Filter { get; private set; }

    /// <summary>How many frames to run; null runs a window until it is closed and one frame without.</summary>
    public int? Frames { get; private set; }

    public bool Headless { get; private set; }

    public int Width { get; private set; } = 1280;

    public int Height { get; private set; } = 720;

    public string? Screenshot { get; private set; }

    public string? Gpu { get; private set; }

    public bool Overlay { get; private set; }

    /// <summary>With --workshop-upload: the item to update (null makes a new one), its title, description, preview image, visibility, tags and change note.</summary>
    public string? WorkshopItem { get; private set; }

    public string? WorkshopTitle { get; private set; }

    public string? WorkshopDescription { get; private set; }

    public string? WorkshopPreview { get; private set; }

    public string WorkshopVisibility { get; private set; } = "private";

    public List<string> WorkshopTags { get; } = [];

    public string? WorkshopNote { get; private set; }

    /// <summary>With --workshop-upload: really upload (otherwise the command only describes the upload).</summary>
    public bool Confirm { get; private set; }

    public static bool TryParse(string[] args, out RuntimeOptions options, out string? problem)
    {
        options = new RuntimeOptions();
        problem = null;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument)
            {
                case "--help" or "-h" or "/?":
                    options.Command = RuntimeCommand.Help;
                    break;
                case "--luau-test":
                    options.Command = RuntimeCommand.LuauTest;
                    if (!TryValue(args, ref i, out string folder, out problem))
                    {
                        return false;
                    }

                    options.Path = folder;
                    break;
                case "--luau-filter":
                    if (!TryValue(args, ref i, out string filter, out problem))
                    {
                        return false;
                    }

                    options.Filter = filter;
                    break;
                case "--run":
                    options.Command = RuntimeCommand.Run;
                    if (!TryValue(args, ref i, out string file, out problem))
                    {
                        return false;
                    }

                    options.Path = file;
                    break;
                case "--game":
                    options.Command = RuntimeCommand.Game;
                    if (!TryValue(args, ref i, out string game, out problem))
                    {
                        return false;
                    }

                    options.Path = game;
                    break;
                case "--pack":
                    options.Command = RuntimeCommand.Pack;
                    if (i + 2 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) || args[i + 2].StartsWith("--", StringComparison.Ordinal))
                    {
                        problem = "--pack needs a folder and the pack file to write.";
                        return false;
                    }

                    options.Path = args[++i];
                    options.Output = args[++i];
                    break;
                case "--import":
                    options.Command = RuntimeCommand.Import;
                    if (!TryValue(args, ref i, out string importFolder, out problem))
                    {
                        return false;
                    }

                    options.Path = importFolder;
                    break;
                case "--workshop-upload":
                    options.Command = RuntimeCommand.WorkshopUpload;
                    if (!TryValue(args, ref i, out string modFolder, out problem))
                    {
                        return false;
                    }

                    options.Path = modFolder;
                    break;
                case "--item":
                    if (!TryValue(args, ref i, out string item, out problem))
                    {
                        return false;
                    }

                    options.WorkshopItem = item;
                    break;
                case "--title":
                    if (!TryValue(args, ref i, out string title, out problem))
                    {
                        return false;
                    }

                    options.WorkshopTitle = title;
                    break;
                case "--description":
                    if (!TryValue(args, ref i, out string description, out problem))
                    {
                        return false;
                    }

                    options.WorkshopDescription = description;
                    break;
                case "--preview":
                    if (!TryValue(args, ref i, out string preview, out problem))
                    {
                        return false;
                    }

                    options.WorkshopPreview = preview;
                    break;
                case "--visibility":
                    if (!TryValue(args, ref i, out string visibility, out problem))
                    {
                        return false;
                    }

                    if (visibility is not ("private" or "friends" or "unlisted" or "public"))
                    {
                        problem = $"--visibility expects private, friends, unlisted or public, not '{visibility}'.";
                        return false;
                    }

                    options.WorkshopVisibility = visibility;
                    break;
                case "--tags":
                    if (!TryValue(args, ref i, out string tags, out problem))
                    {
                        return false;
                    }

                    options.WorkshopTags.AddRange(tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--note":
                    if (!TryValue(args, ref i, out string note, out problem))
                    {
                        return false;
                    }

                    options.WorkshopNote = note;
                    break;
                case "--confirm":
                    options.Confirm = true;
                    break;
                case "--watch":
                    options.Watch = true;
                    break;
                case "--cooked":
                    options.Cooked = true;
                    break;
                case "--force":
                    options.Force = true;
                    break;
                case "--split-mb":
                    if (!TryValue(args, ref i, out string split, out problem))
                    {
                        return false;
                    }

                    if (!long.TryParse(split, NumberStyles.None, CultureInfo.InvariantCulture, out long megabytes) || megabytes == 0)
                    {
                        problem = $"--split-mb expects a number of megabytes, not '{split}'.";
                        return false;
                    }

                    options.SplitMegabytes = megabytes;
                    break;
                case "--level":
                    if (!TryValue(args, ref i, out string level, out problem))
                    {
                        return false;
                    }

                    if (!int.TryParse(level, NumberStyles.None, CultureInfo.InvariantCulture, out int zstdLevel) || zstdLevel is < 1 or > 22)
                    {
                        problem = $"--level expects 1 to 22, not '{level}'.";
                        return false;
                    }

                    options.Level = zstdLevel;
                    break;
                case "--base":
                    if (!TryValue(args, ref i, out string basePack, out problem))
                    {
                        return false;
                    }

                    options.Bases.Add(basePack);
                    break;
                case "--user":
                    if (!TryValue(args, ref i, out string user, out problem))
                    {
                        return false;
                    }

                    options.UserFolder = user;
                    break;
                case "--gpu-info":
                    options.Command = RuntimeCommand.GpuInfo;
                    break;
                case "--luau-defs":
                    options.Command = RuntimeCommand.LuauDefs;
                    if (!TryValue(args, ref i, out string target, out problem))
                    {
                        return false;
                    }

                    options.Path = target;
                    break;
                case "--luau-docs":
                    options.Command = RuntimeCommand.LuauDocs;
                    if (!TryValue(args, ref i, out string docsTarget, out problem))
                    {
                        return false;
                    }

                    options.Path = docsTarget;
                    break;
                case "--frames":
                    if (!TryValue(args, ref i, out string count, out problem))
                    {
                        return false;
                    }

                    if (!int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int frames))
                    {
                        problem = $"--frames expects a number of frames, not '{count}'.";
                        return false;
                    }

                    options.Frames = frames;
                    break;
                case "--headless":
                    options.Headless = true;
                    break;
                case "--overlay":
                    options.Overlay = true;
                    break;
                case "--screenshot":
                    if (!TryValue(args, ref i, out string screenshot, out problem))
                    {
                        return false;
                    }

                    options.Screenshot = screenshot;
                    break;
                case "--gpu":
                    if (!TryValue(args, ref i, out string gpu, out problem))
                    {
                        return false;
                    }

                    options.Gpu = gpu;
                    break;
                case "--size":
                    if (!TryValue(args, ref i, out string size, out problem))
                    {
                        return false;
                    }

                    string[] parts = size.Split('x');
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width)
                        || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height)
                        || width < 16 || height < 16)
                    {
                        problem = "--size needs <width>x<height>, at least 16x16.";
                        return false;
                    }

                    options.Width = width;
                    options.Height = height;
                    break;
                default:
                    problem = $"Unknown argument '{argument}'.";
                    return false;
            }
        }

        if (options.Filter is not null && options.Command != RuntimeCommand.LuauTest)
        {
            problem = "--luau-filter goes with --luau-test.";
            return false;
        }

        if (options.Watch && options.Command != RuntimeCommand.Game)
        {
            problem = "--watch goes with --game.";
            return false;
        }

        if (options.Cooked && options.Command != RuntimeCommand.Pack)
        {
            problem = "--cooked goes with --pack.";
            return false;
        }

        if (options.Force && options.Command != RuntimeCommand.Import)
        {
            problem = "--force goes with --import.";
            return false;
        }

        if (options.Command != RuntimeCommand.Pack && (options.Bases.Count > 0 || args.Contains("--split-mb") || args.Contains("--level")))
        {
            problem = "--split-mb, --level and --base go with --pack.";
            return false;
        }

        if (options.UserFolder is not null && options.Command != RuntimeCommand.Game)
        {
            problem = "--user goes with --game.";
            return false;
        }

        if (options.Command != RuntimeCommand.WorkshopUpload && (options.WorkshopItem is not null || options.WorkshopTitle is not null || options.WorkshopDescription is not null || options.WorkshopPreview is not null
            || options.WorkshopTags.Count > 0 || options.WorkshopNote is not null || options.Confirm || args.Contains("--visibility")))
        {
            problem = "--item, --title, --description, --preview, --visibility, --tags, --note and --confirm go with --workshop-upload.";
            return false;
        }

        if (options.Screenshot is not null && !options.Headless)
        {
            problem = "--screenshot goes with --headless.";
            return false;
        }

        if (options.Command is not (RuntimeCommand.Run or RuntimeCommand.Game) && (options.Headless || options.Screenshot is not null || options.Overlay || options.Gpu is not null || options.Frames is not null))
        {
            problem = "--headless, --frames, --screenshot, --overlay and --gpu go with --run or --game.";
            return false;
        }

        return true;
    }

    private static bool TryValue(string[] args, ref int index, out string value, out string? problem)
    {
        problem = null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = "";
            problem = $"{args[index]} needs a value.";
            return false;
        }

        value = args[++index];
        return true;
    }
}
