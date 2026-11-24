using System;
using System.IO;
using Weft.Build.Bindings;

// Build orchestration entry point: ./build.sh <command> [arguments].
string root = FindRepositoryRoot();
switch (args.Length > 0 ? args[0] : "")
{
    case "bindings":
        return BindingsCommand.Run(root, args[1..]);
    case "sdk":
        return Weft.Build.SdkCommand.Run(root, args[1..]);
    case "docs":
        return Weft.Build.DocsCommand.Run(root, args[1..]);
    default:
        Console.Error.WriteLine($"Unknown command: {string.Join(' ', args)}\nCommands: bindings [library…], sdk steam <archive>, docs [serve]");
        return 1;
}

static string FindRepositoryRoot()
{
    for (string? directory = Directory.GetCurrentDirectory(); directory is not null; directory = Path.GetDirectoryName(directory))
    {
        if (File.Exists(Path.Combine(directory, "Weft.slnx")))
        {
            return directory;
        }
    }

    throw new InvalidOperationException("Run from inside the repository (Weft.slnx not found).");
}
