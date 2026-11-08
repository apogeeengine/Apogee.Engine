using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Apogee.Samples.Viewer;

/// <summary>The command line of the viewer. Parsing is by hand: it has to stay trim and NativeAOT safe.</summary>
internal sealed class Options
{
    public const string Usage = """
        Apogee.Samples.Viewer: the engine's viewer.

          --scene <courtyard|file.gltf>   what to show (default: courtyard, a generated colonnade with the sample models)
          --assets <dir>                  where the sample models are (default: Samples/Viewer/Assets, found above the program)
          --no-models                     leave the sample models out of the courtyard
          --lights <n>                    animated point lights (default 256)
          --camera x,y,z,yaw,pitch        start position in meters and angles in degrees
          --headless                      no window: render offscreen
          --frames <n>                    stop after n frames (headless default: 1)
          --size <w>x<h>                  the window or the headless image (default 1280x720)
          --overlay                       draw the dev overlay in headless images (the window always has it; F1 toggles)
          --screenshot <file.png>         headless: write the last frame
          --gpu <name>                    prefer the GPU whose name contains this (APOGEE_GPU)
          --no-vsync                      do not wait for vertical blank
          --watch                         window: use the shaders' source files (from the repository) and rebuild the pipelines when you save one
          --help                          this text

        In the window: WASD move, Q/E down/up, Shift faster, hold the right mouse button to look, the wheel changes speed, F1 the overlay, Esc quits.
        Exit codes: 0 fine, 1 bad arguments, 2 failure, 3 validation errors.
        """;

    public string Scene { get; private set; } = "courtyard";

    public string? AssetsDirectory { get; private set; }

    public bool NoModels { get; private set; }

    public int Lights { get; private set; } = 256;

    public (Vector3 Position, float Yaw, float Pitch)? Camera { get; private set; }

    public bool Headless { get; private set; }

    public int? Frames { get; private set; }

    public int Width { get; private set; } = 1280;

    public int Height { get; private set; } = 720;

    public bool Overlay { get; private set; }

    public string? Screenshot { get; private set; }

    public string? Gpu { get; private set; }

    public bool VSync { get; private set; } = true;

    public bool Watch { get; private set; }

    public bool Help { get; private set; }

    /// <summary>Parses the arguments; returns an error message, or null when they are fine.</summary>
    public static string? TryParse(string[] args, out Options options)
    {
        options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            string? Value()
            {
                if (i + 1 >= args.Length)
                {
                    return null;
                }

                return args[++i];
            }

            switch (name)
            {
                case "--help" or "-h" or "-?":
                    options.Help = true;
                    break;
                case "--headless":
                    options.Headless = true;
                    break;
                case "--overlay":
                    options.Overlay = true;
                    break;
                case "--no-models":
                    options.NoModels = true;
                    break;
                case "--no-vsync":
                    options.VSync = false;
                    break;
                case "--watch":
                    options.Watch = true;
                    break;
                case "--scene" or "--assets" or "--screenshot" or "--gpu":
                    string? text = Value();
                    if (text is null)
                    {
                        return $"{name} needs a value.";
                    }

                    switch (name)
                    {
                        case "--scene": options.Scene = text; break;
                        case "--assets": options.AssetsDirectory = text; break;
                        case "--screenshot": options.Screenshot = text; break;
                        default: options.Gpu = text; break;
                    }

                    break;
                case "--lights" or "--frames":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                    {
                        return $"{name} needs a whole number.";
                    }

                    if (name == "--lights")
                    {
                        options.Lights = count;
                    }
                    else
                    {
                        options.Frames = count;
                    }

                    break;
                case "--size":
                    string[]? size = Value()?.Split('x');
                    if (size is not { Length: 2 } || !int.TryParse(size[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w) || !int.TryParse(size[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h) || w < 16 || h < 16)
                    {
                        return "--size needs <width>x<height>, at least 16x16.";
                    }

                    options.Width = w;
                    options.Height = h;
                    break;
                case "--camera":
                    string[]? parts = Value()?.Split(',');
                    float[] numbers = new float[5];
                    if (parts is not { Length: 5 } || parts.Select((p, k) => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[k])).Contains(false))
                    {
                        return "--camera needs x,y,z,yaw,pitch.";
                    }

                    options.Camera = (new Vector3(numbers[0], numbers[1], numbers[2]), numbers[3] * MathF.PI / 180f, numbers[4] * MathF.PI / 180f);
                    break;
                default:
                    return $"Unknown option {name}.";
            }
        }

        if (options.Screenshot is not null && !options.Headless)
        {
            return "--screenshot needs --headless.";
        }

        return null;
    }
}
