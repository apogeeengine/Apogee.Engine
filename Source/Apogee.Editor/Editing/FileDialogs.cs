using System;
using System.Collections.Generic;
using System.IO;
using Apogee.Content.FileSystem;
using Apogee.Platform;

namespace Apogee.Editor.Editing;

/// <summary>
/// The operating system's file dialogs as the editor uses them: one at a time, asynchronous. Each call returns at once, and the result reaches <c>done</c> later, on the main thread, from <see cref="PlatformHost.PumpEvents"/> (which the frame loop calls first)
/// (see <see cref="FileDialog"/>). A fake (<see cref="ScriptedFileDialogs"/>) stands in for the system in tests.
/// </summary>
public interface IFileDialogs
{
    /// <summary>A dialog is open (or its result has not been delivered yet). The editor greys its menus meanwhile, and a new request is refused.</summary>
    bool Busy { get; }

    /// <summary>Asks for a file to open. False (and nothing happens) when a dialog is already <see cref="Busy"/>.</summary>
    bool OpenFile(IReadOnlyList<(string Name, string Pattern)> filters, string? defaultLocation, Action<FileDialogResult> done);

    /// <summary>Asks where to save a file. False when a dialog is already <see cref="Busy"/>.</summary>
    bool SaveFile(IReadOnlyList<(string Name, string Pattern)> filters, string? defaultLocation, Action<FileDialogResult> done);

    /// <summary>Asks for a folder. False when a dialog is already <see cref="Busy"/>.</summary>
    bool OpenFolder(string? defaultLocation, Action<FileDialogResult> done);
}

/// <summary>The system's dialogs (SDL's), attached to the editor's window. One at a time (see <see cref="DialogGate"/>), and a dialog that does not answer within <see cref="LostAfter"/> counts as lost.</summary>
public sealed class SdlFileDialogs(Window window) : IFileDialogs
{
    /// <summary>How long a dialog may stay open before the editor stops waiting for it: a person can take a while, so this is minutes.</summary>
    public static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(10);

    private readonly DialogGate _gate = new(LostAfter);

    /// <inheritdoc/>
    public bool Busy => _gate.Busy;

    /// <inheritdoc/>
    public bool OpenFile(IReadOnlyList<(string Name, string Pattern)> filters, string? defaultLocation, Action<FileDialogResult> done) =>
        _gate.Start(done, finished => FileDialog.OpenFile(window, filters, defaultLocation, many: false, finished));

    /// <inheritdoc/>
    public bool SaveFile(IReadOnlyList<(string Name, string Pattern)> filters, string? defaultLocation, Action<FileDialogResult> done) =>
        _gate.Start(done, finished => FileDialog.SaveFile(window, filters, defaultLocation, finished));

    /// <inheritdoc/>
    public bool OpenFolder(string? defaultLocation, Action<FileDialogResult> done) =>
        _gate.Start(done, finished => FileDialog.OpenFolder(window, defaultLocation, many: false, finished));
}

/// <summary>A dialog the test asked for and has not answered yet.</summary>
public sealed record ScriptedRequest(DialogRequestKind Kind, IReadOnlyList<(string Name, string Pattern)> Filters, string? DefaultLocation, Action<FileDialogResult> Done);

/// <summary>The three dialogs.</summary>
public enum DialogRequestKind
{
    OpenFile,
    SaveFile,
    OpenFolder,
}

/// <summary>
/// Dialogs for tests: a request waits in <see cref="Pending"/> until <see cref="Answer"/> gives it a result (running its continuation, as the frame loop would). Nothing is shown.
/// </summary>
public sealed class ScriptedFileDialogs : IFileDialogs
{
    /// <summary>The open request, or null.</summary>
    public ScriptedRequest? Pending { get; private set; }

    /// <summary>Every request that was made, in order, answered or not.</summary>
    public List<ScriptedRequest> History { get; } = [];

    /// <inheritdoc/>
    public bool Busy => Pending is not null;

    /// <inheritdoc/>
    public bool OpenFile(IReadOnlyList<(string Name, string Pattern)> filters, string? defaultLocation, Action<FileDialogResult> done) =>
        Add(new ScriptedRequest(DialogRequestKind.OpenFile, filters, defaultLocation, done));

    /// <inheritdoc/>
    public bool SaveFile(IReadOnlyList<(string Name, string Pattern)> filters, string? defaultLocation, Action<FileDialogResult> done) =>
        Add(new ScriptedRequest(DialogRequestKind.SaveFile, filters, defaultLocation, done));

    /// <inheritdoc/>
    public bool OpenFolder(string? defaultLocation, Action<FileDialogResult> done) =>
        Add(new ScriptedRequest(DialogRequestKind.OpenFolder, [], defaultLocation, done));

    private bool Add(ScriptedRequest request)
    {
        if (Pending is not null)
        {
            return false;
        }

        Pending = request;
        History.Add(request);
        return true;
    }

    /// <summary>Answers the open request: the user chose <paramref name="paths"/>.</summary>
    public void Choose(params string[] paths) => Answer(new FileDialogResult(paths, false, null));

    /// <summary>Answers the open request with a cancel.</summary>
    public void Cancel() => Answer(FileDialogResult.Cancel);

    /// <summary>Answers the open request with a failure (no dialog backend, say).</summary>
    public void Fail(string error) => Answer(FileDialogResult.Failed(error));

    /// <summary>Answers the open request with <paramref name="result"/>.</summary>
    public void Answer(FileDialogResult result)
    {
        ScriptedRequest request = Pending ?? throw new InvalidOperationException("No dialog is open.");
        Pending = null;
        request.Done(result);
    }
}

/// <summary>Where the paths of the operating system and the game's virtual paths meet.</summary>
public static class VirtualPaths
{
    private static StringComparison Comparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The virtual path (<c>res://scenes/level.ascene</c>) of <paramref name="osPath"/> when it is inside <paramref name="gameFolder"/>, with <c>/</c> separators; null for a path outside the folder, the folder
    /// itself, or one that is not a usable path. The game folder may be at the root of a drive, and may be (or sit under) a symbolic link while the dialog returns the resolved path: when the paths do not
    /// match as written they are compared with their links resolved. Names are compared without regard to case on Windows and macOS, whose file systems ignore it.
    /// <para>
    /// Limits: Windows 8.3 short names, and macOS Unicode normalization (NFC against NFD) are not reconciled, nor are case-insensitive mounts on Linux; such a path is reported as outside the game folder.
    /// </para>
    /// </summary>
    public static string? ToVirtual(string gameFolder, string osPath)
    {
        ArgumentNullException.ThrowIfNull(gameFolder);
        if (string.IsNullOrWhiteSpace(osPath))
        {
            return null;
        }

        string root;
        string full;
        try
        {
            root = WithSeparator(Path.GetFullPath(gameFolder));
            full = Path.GetFullPath(osPath);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string? relative = Inside(root, full) ?? Inside(WithSeparator(RealPath(root)), RealPath(full));
        if (relative is null)
        {
            return null;
        }

        if (Path.DirectorySeparatorChar == '\\')
        {
            relative = relative.Replace('\\', '/');
        }
        else if (relative.Contains('\\', StringComparison.Ordinal))
        {
            return null; // On Linux and macOS a backslash is part of a file name; the virtual file system would read it as a separator.
        }

        return "res://" + relative;
    }

    /// <summary>
    /// True when the file the virtual path <paramref name="virtualPath"/> names, as the game's file system sees it, is the file at <paramref name="osPath"/>; false when something else answers to that path
    /// (a pack mounted over the folder wins), or true when no file exists there yet (a new file).
    /// </summary>
    public static bool ResolvesTo(VirtualFileSystem files, string virtualPath, string osPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!files.FileExists(virtualPath))
        {
            return true;
        }

        string? physical = files.TryGetPhysicalPath(virtualPath);
        if (physical is null)
        {
            return false; // the file exists, but in a pack: it is not the file on disk
        }

        return string.Equals(RealPath(Path.GetFullPath(physical)), RealPath(Path.GetFullPath(osPath)), Comparison);
    }

    private static string WithSeparator(string folder) => folder.EndsWith(Path.DirectorySeparatorChar) || folder.EndsWith(Path.AltDirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;

    /// <summary>What follows <paramref name="root"/> (which ends with a separator) in <paramref name="full"/>, or null when it is not inside or is the folder itself.</summary>
    private static string? Inside(string root, string full) => full.Length > root.Length && full.StartsWith(root, Comparison) ? full[root.Length..] : null;

    /// <summary>
    /// <paramref name="fullPath"/> with the symbolic links of its existing parts resolved (what a dialog hands back); parts that do not exist are kept as they are. Gives up on a chain of links deeper than
    /// <paramref name="depth"/> allows and returns the path unresolved.
    /// </summary>
    internal static string RealPath(string fullPath, int depth = 0)
    {
        if (depth > 16)
        {
            return fullPath;
        }

        string root = Path.GetPathRoot(fullPath) ?? "";
        string current = root;
        string[] parts = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            string next = Path.Combine(current, part);
            try
            {
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    next = RealPath(target.FullName, depth + 1);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A part that cannot be inspected stays as it is.
            }

            current = next;
        }

        return current;
    }
}
