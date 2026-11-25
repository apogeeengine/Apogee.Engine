using System.IO;
using Weft.Content.Assets;
using Weft.Content.Packages;

namespace Weft.Export;

/// <summary>
/// Where the parts of an export go in the output folder. Windows and Linux: everything in the output folder (the executable, its libraries, <c>game.wgam</c> and <c>data/</c>). macOS: a bundle,
/// <c>Name.app</c>, with the program and its libraries in <c>Contents/MacOS</c> (the runtime's folder) and the game's own files in <c>Contents/Resources</c>.
/// </summary>
public sealed class ExportLayout
{
    private ExportLayout(ExportTarget target, string output, string executableDirectory, string gameRoot, string executableName, string? bundle)
    {
        Target = target;
        Output = output;
        ExecutableDirectory = executableDirectory;
        GameRoot = gameRoot;
        ExecutablePath = Path.Combine(executableDirectory, executableName);
        Bundle = bundle;
    }

    /// <summary>The platform the layout is for.</summary>
    public ExportTarget Target { get; }

    /// <summary>The output folder the export was asked for.</summary>
    public string Output { get; }

    /// <summary>The folder with the executable and the native libraries (and <c>Shaders</c>).</summary>
    public string ExecutableDirectory { get; }

    /// <summary>The folder with <c>game.wgam</c> and <c>data/</c>: where the runtime looks for the game (see <c>GameLocator</c>).</summary>
    public string GameRoot { get; }

    /// <summary>The executable: <c>&lt;out&gt;/&lt;exe&gt;.exe</c> on Windows, <c>&lt;out&gt;/&lt;exe&gt;</c> on Linux, <c>&lt;out&gt;/&lt;Name&gt;.app/Contents/MacOS/&lt;exe&gt;</c> on macOS.</summary>
    public string ExecutablePath { get; }

    /// <summary>The <c>.app</c> folder on macOS; null elsewhere.</summary>
    public string? Bundle { get; }

    /// <summary>The first part of the game's pack: <c>&lt;game root&gt;/data/game.wpak</c>.</summary>
    public string PackPath => Path.Combine(GameRoot, "data", "game" + AssetFormats.Pack.Extension);

    /// <summary>The layout of the export of <paramref name="manifest"/> into <paramref name="output"/> for <paramref name="target"/>.</summary>
    public static ExportLayout For(ExportTarget target, GameManifest manifest, string output)
    {
        string full = Path.GetFullPath(output);
        string name = manifest.Executable + (target.Os == "win" ? ".exe" : "");
        if (target.Os == "osx")
        {
            string bundle = Path.Combine(full, GameManifest.SanitizeFileName(manifest.Name) + ".app");
            string contents = Path.Combine(bundle, "Contents");
            return new ExportLayout(target, full, Path.Combine(contents, "MacOS"), Path.Combine(contents, "Resources"), name, bundle);
        }

        return new ExportLayout(target, full, full, full, name, null);
    }
}
