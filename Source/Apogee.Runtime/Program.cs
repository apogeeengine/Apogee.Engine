using System;
using Apogee.Core.Logging;
using Apogee.Runtime;

Log.AddSink(new ConsoleSink());

if (!RuntimeOptions.TryParse(args, out RuntimeOptions options, out string? problem))
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
    default:
        // No command: load every native library through the bindings and run the smoke tests, which proves under NativeAOT that the
        // bindings, the library resolver, the function-pointer tables, the generated registry and the scripting layer all work.
        Smoke.Run();
        return 0;
}
