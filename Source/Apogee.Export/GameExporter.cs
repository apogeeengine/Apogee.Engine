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

namespace Apogee.Export;

/// <summary>
/// Exports a game: a folder that runs on its own. It holds the runtime of the target platform renamed to the game's executable name, the runtime's native libraries and compiled shaders, the game's
/// files in <c>data/game.pack</c> (encrypted when there is a key, which is then also written into the executable, see <see cref="PackKeySlot"/>), and the game's <c>game.xml</c>. See <see cref="ExportLayout"/>
/// for where each goes. The game folder is only read: it is copied before it is imported.
/// </summary>
public static class GameExporter
{
    /// <summary>The file that marks an output folder as an export, so that a later export may clear it.</summary>
    public const string MarkerFile = ".apogee-export";

    /// <summary>The folder-relative names a game's pack never holds: the packs themselves, dev mods, the manifest (it is written beside the pack), and the Steam switch of a development folder.</summary>
    private static readonly string[] s_neverPacked = ["data/", "dev_mods/", GameManifest.FileName, "steam_appid.txt"];

    /// <summary>Exports the game.</summary>
    /// <param name="options">What to export and where.</param>
    /// <param name="log">Told what is being done, one line at a time; may be null.</param>
    /// <exception cref="ExportException">The game cannot be exported; the message says why.</exception>
    public static ExportResult Export(ExportOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        log ??= static _ => { };
        string gameFolder = Path.GetFullPath(options.GameFolder);
        if (!Directory.Exists(gameFolder))
        {
            throw new ExportException($"{gameFolder} is not a folder.");
        }

        GameManifest manifest = LoadManifest(gameFolder);
        string runtime = RuntimeFolder.Resolve(options.RuntimeDirectory, options.Target);
        ExportLayout layout = ExportLayout.For(options.Target, manifest, options.OutputFolder);
        PrepareOutput(gameFolder, layout.Output);
        var warnings = new List<string>();

        log($"Exporting {manifest.Name} {manifest.Version} for {options.Target.Rid} into {layout.Output}");
        CopyRuntime(runtime, layout, log);

        string[] exclude = [.. s_neverPacked, .. IconExclusion(manifest)];
        PackGame(options, gameFolder, layout, exclude, log);
        if (options.Key is null)
        {
            warnings.Add($"{PackKey.EnvironmentVariable} is not set: the packs are not encrypted.");
        }
        else
        {
            int copies = PackKeySlot.Write(layout.ExecutablePath, options.Key);
            log(copies == 1 ? "Wrote the pack key into the executable" : $"Wrote the pack key into the executable ({copies} places)");
        }

        byte[]? icon = ReadIcon(gameFolder, manifest, warnings);
        var context = new PlatformPackagingContext { Options = options, Manifest = manifest, Layout = layout, IconFile = icon, Warnings = warnings, Log = log };
        PlatformPackaging.For(options.Target).Apply(context);

        File.WriteAllText(Path.Combine(layout.GameRoot, GameManifest.FileName), manifest.WithIcon(context.ExportedIcon).ToXml(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(layout.Output, MarkerFile), $"{manifest.Name} {manifest.Version} for {options.Target.Rid}\n");

        List<string> files = [.. Directory.EnumerateFiles(layout.Output, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(layout.Output, f).Replace('\\', '/'))
            .Where(f => f != MarkerFile)
            .Order(StringComparer.Ordinal)];
        return new ExportResult(layout.ExecutablePath, layout.GameRoot, files, warnings);
    }

    private static GameManifest LoadManifest(string gameFolder)
    {
        GameManifest? manifest = GameManifest.Load(gameFolder, out IReadOnlyList<string> problems);
        if (problems.Count > 0)
        {
            throw new ExportException($"{GameManifest.FileName} of {gameFolder} is wrong:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
        }

        return manifest ?? throw new ExportException($"{gameFolder} has no {GameManifest.FileName}: a game needs one (name, organization, version) to be exported.");
    }

    /// <summary>Checks that the output folder may be written, and empties it when it holds an earlier export.</summary>
    private static void PrepareOutput(string gameFolder, string output)
    {
        StringComparison comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string game = Path.TrimEndingDirectorySeparator(gameFolder) + Path.DirectorySeparatorChar;
        string target = Path.TrimEndingDirectorySeparator(output) + Path.DirectorySeparatorChar;
        if (target.StartsWith(game, comparison))
        {
            throw new ExportException($"The output folder {output} is inside the game folder {gameFolder}: export somewhere else, or the export would be packed into the game.");
        }

        if (game.StartsWith(target, comparison))
        {
            throw new ExportException($"The game folder {gameFolder} is inside the output folder {output}: it would be deleted. Export somewhere else.");
        }

        if (File.Exists(output))
        {
            throw new ExportException($"{output} is a file.");
        }

        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            if (!File.Exists(Path.Combine(output, MarkerFile)))
            {
                throw new ExportException($"{output} is not empty and is not an earlier export (it has no {MarkerFile}): choose an empty folder, so that nothing of yours is deleted.");
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(output))
            {
                if (Directory.Exists(entry) && !File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }

        // Marked from the start: an export that fails halfway can be done again over its leftovers.
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, MarkerFile), "unfinished\n");
    }

    /// <summary>Copies what <see cref="RuntimeFolder.Ships"/> says into the executable's folder, the program under the game's executable name.</summary>
    private static void CopyRuntime(string runtime, ExportLayout layout, Action<string> log)
    {
        bool windows = layout.Target.Os == "win";
        string program = RuntimeFolder.ProgramPath(runtime, windows);
        Directory.CreateDirectory(layout.ExecutableDirectory);
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(runtime, file).Replace('\\', '/');
            if (!RuntimeFolder.Ships(relative))
            {
                continue;
            }

            string destination = string.Equals(file, program, StringComparison.Ordinal) ? layout.ExecutablePath : Path.Combine(layout.ExecutableDirectory, relative);
            if (File.Exists(destination))
            {
                throw new ExportException($"The executable name '{Path.GetFileName(layout.ExecutablePath)}' is the name of a file of the runtime ({relative}): give the game another executable in {GameManifest.FileName}.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
            count++;
        }

        log($"Copied {count} runtime files");
    }

    /// <summary>Packs the game into <c>data/game.pack</c>: the folder itself, or when cooked a copy of it that has been imported.</summary>
    private static void PackGame(ExportOptions options, string gameFolder, ExportLayout layout, string[] exclude, Action<string> log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(layout.PackPath)!);
        string? staging = null;
        try
        {
            string folder = gameFolder;
            if (options.Cooked)
            {
                staging = Directory.CreateTempSubdirectory("apogee-export-").FullName;
                CopyGame(gameFolder, staging, exclude);
                Import(staging, options.Importers, log);
                folder = staging;
            }

            PackReport report = GamePacker.Pack(new PackRequest
            {
                Folder = folder,
                Output = layout.PackPath,
                Cooked = options.Cooked,
                AllowNothingImported = true,
                Key = options.Key,
                Exclude = exclude,
            });
            log($"Packed {report.Added} files ({report.RawBytes / 1024.0 / 1024.0:F1} MiB) into {report.Parts.Count} part(s), {report.PackedBytes / 1024.0 / 1024.0:F1} MiB{(options.Key is null ? "" : ", encrypted")}"
                + (report.CookedDocuments > 0 ? $", {report.CookedDocuments} cooked" : ""));
        }
        catch (Exception e) when (e is ContentException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ExportException(e.Message, e);
        }
        finally
        {
            if (staging is not null)
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (IOException)
                {
                    // A temporary folder that could not be deleted is not worth failing an export for.
                }
            }
        }
    }

    /// <summary>Copies the game, less what <paramref name="exclude"/> and the dot-names leave out. The <c>imported/</c> folder comes along, so that artifacts that are up to date are not made again.</summary>
    private static void CopyGame(string gameFolder, string staging, string[] exclude)
    {
        foreach (string file in Directory.EnumerateFiles(gameFolder, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(gameFolder, file).Replace('\\', '/');
            if (relative.Split('/').Any(segment => segment.StartsWith('.')) || exclude.Any(x => relative.StartsWith(x, StringComparison.Ordinal)))
            {
                continue;
            }

            string destination = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static void Import(string folder, IReadOnlyList<IAssetImporter>? importers, Action<string> log)
    {
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(folder, writable: true));
        ImportReport report = new AssetImporter(files, importers).Run();
        log($"Imported {report.Imported.Count}, up to date {report.UpToDate.Count}, removed {report.Removed.Count}");
        if (!report.Success)
        {
            throw new ExportException("Importing the game failed:" + Environment.NewLine + string.Join(Environment.NewLine, report.Failed.Select(f => $"{f.Source}: {f.Message}")));
        }
    }

    /// <summary>The icon file is not part of the game's pack: it is in the executable or the bundle, or written beside the pack by the platform's step.</summary>
    private static IEnumerable<string> IconExclusion(GameManifest manifest) => manifest.Icon is { } icon ? [icon.Replace('\\', '/').TrimStart('/')] : [];

    private static byte[]? ReadIcon(string gameFolder, GameManifest manifest, List<string> warnings)
    {
        if (manifest.Icon is not { } icon)
        {
            return null;
        }

        string file = Path.Combine(gameFolder, icon);
        if (!File.Exists(file))
        {
            warnings.Add($"The icon {icon} named in {GameManifest.FileName} does not exist: the game has no icon.");
            return null;
        }

        return File.ReadAllBytes(file);
    }
}
