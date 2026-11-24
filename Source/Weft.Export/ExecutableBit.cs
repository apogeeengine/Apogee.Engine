using System;
using System.IO;

namespace Weft.Export;

/// <summary>Marks an exported program executable, for the platforms (Linux, macOS) whose file systems have a mode.</summary>
internal static class ExecutableBit
{
    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>
    /// Sets the mode of the executable to <c>rwxr-xr-x</c>. On a Windows computer there are no Unix modes, so a warning says that the bit must be set when the export is unpacked on
    /// <paramref name="targetSystem"/>.
    /// </summary>
    /// <param name="context">The export; its executable is the file, its warnings get the warning.</param>
    /// <param name="targetSystem">The operating system the export is for, as the warning names it.</param>
    public static void Set(PlatformPackagingContext context, string targetSystem)
    {
        if (OperatingSystem.IsWindows())
        {
            context.Warnings.Add($"This computer does not have Unix file modes, so {Path.GetFileName(context.Layout.ExecutablePath)} is not marked executable: set the executable bit (chmod +x) when the export is unpacked on {targetSystem}, or export from {targetSystem}{(targetSystem == "Linux" ? " or WSL" : "")}.");
            return;
        }

        File.SetUnixFileMode(context.Layout.ExecutablePath, ExecutableMode);
    }
}
