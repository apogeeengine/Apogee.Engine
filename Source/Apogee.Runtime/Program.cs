using System;
using Apogee.Core.Logging;
using Apogee.Runtime;

Log.AddSink(new ConsoleSink());

// A shipped game is this program with a game.xml and its packs beside it (or in a macOS bundle's Resources).
string? gameRoot = GameLocator.Find(AppContext.BaseDirectory);

if (!RuntimeOptions.TryParse(args, out RuntimeOptions options, out string? problem, gameNextToProgram: gameRoot is not null))
{
    Console.Error.WriteLine(problem);
    Console.Error.WriteLine(RuntimeOptions.Usage);
    return 1;
}

switch (options.Command)
{
    case RuntimeCommand.Help:
        Console.WriteLine(RuntimeOptions.Usage);
        return 0;
    case RuntimeCommand.LuauTest:
        return Commands.LuauTest(options);
    case RuntimeCommand.Run or RuntimeCommand.Game:
        return Commands.Run(options);
    case RuntimeCommand.Pack:
        return Commands.Pack(options);
    case RuntimeCommand.Export:
        return ExportCommand.Run(options);
    case RuntimeCommand.Import:
        return Commands.Import(options);
    case RuntimeCommand.LuauDefs:
        return Commands.LuauDefs(options);
    case RuntimeCommand.LuauDocs:
        return Commands.LuauDocs(options);
    case RuntimeCommand.GpuInfo:
        return Commands.GpuInfo();
    case RuntimeCommand.WorkshopUpload:
        return Commands.WorkshopUpload(options);
    case RuntimeCommand.Smoke:
        // Load every native library through the bindings and run the smoke tests, which proves under NativeAOT that the
        // bindings, the library resolver, the function-pointer tables, the generated registry and the scripting layer all work.
        Smoke.Run();
        return 0;
    default:
        if (gameRoot is null)
        {
            Console.Error.WriteLine($"No game.xml next to the program ({AppContext.BaseDirectory}): pass --game <folder>, --smoke or --help.");
            return 1;
        }

        foreach (string ignored in options.Ignored)
        {
            Log.Warn("Runtime", $"Ignoring the argument '{ignored}'.");
        }

        return Commands.Launch(options, gameRoot);
}
