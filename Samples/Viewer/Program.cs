using System;
using System.IO;
using Apogee.Core.Logging;
using Apogee.Samples.Viewer;

// The engine's viewer. See Options.Usage, or run it with --help.
if (Options.TryParse(args, out Options options) is { } problem)
{
    Console.Error.WriteLine($"{problem}\n\n{Options.Usage}");
    return 1;
}

if (options.Help)
{
    Console.WriteLine(Options.Usage);
    return 0;
}

Log.AddSink(new ConsoleSink());
string shaderCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apogee", "ShaderCache");
if (options.Gpu is not null)
{
    Environment.SetEnvironmentVariable("APOGEE_GPU", options.Gpu);
}

try
{
    return options.Headless ? Headless.Run(options, shaderCache) : Viewer.Run(options, shaderCache);
}
catch (Exception e) when (e is Apogee.Graphics.GraphicsException or Apogee.Graphics.Shaders.ShaderCompileException or Apogee.Content.ContentException or Apogee.Platform.PlatformException or IOException)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
