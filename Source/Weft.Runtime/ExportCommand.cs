using System;
using Weft.Content.Packs;
using Weft.Export;
using Weft.Host;

namespace Weft.Runtime;

/// <summary>The <c>--export</c> command: exports a game folder (see <see cref="GameExporter"/>). Exit codes: 0 fine, 1 bad arguments, 2 the export failed.</summary>
internal static class ExportCommand
{
    /// <summary>Exports the game of <see cref="RuntimeOptions.Path"/> into <see cref="RuntimeOptions.Output"/>, for <see cref="RuntimeOptions.Target"/>.</summary>
    public static int Run(RuntimeOptions options)
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

        return Export(options, key);
    }

    /// <summary>As <see cref="Run"/>, with the key given instead of read from the environment.</summary>
    public static int Export(RuntimeOptions options, PackKey? key)
    {
        if (!ExportTarget.TryParse(options.Target ?? "", out ExportTarget target, out string? problem))
        {
            Console.Error.WriteLine(problem);
            return 1;
        }

        try
        {
            ExportResult result = GameExporter.Export(
                new ExportOptions
                {
                    GameFolder = options.Path,
                    OutputFolder = options.Output,
                    Target = target,
                    RuntimeDirectory = options.RuntimeDirectory,
                    Cooked = options.Cooked,
                    Console = options.Console,
                    Key = key,
                    Importers = ScriptSession.Importers,
                },
                Console.WriteLine);
            foreach (string warning in result.Warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }

            Console.WriteLine($"Exported {result.Files.Count} files. Run {result.Executable}");
            return 0;
        }
        catch (ExportException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }
}
