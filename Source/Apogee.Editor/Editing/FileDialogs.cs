using System;
using System.Collections.Generic;
using System.IO;
using Apogee.Platform;

namespace Apogee.Editor.Editing;

/// <summary>
/// The operating system's file dialogs as the editor uses them: one at a time, asynchronous. Each call returns at once, and the result reaches <c>done</c> later, on the main thread, from the frame loop
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

/// <summary>The system's dialogs (SDL's), attached to the editor's window.</summary>
public sealed class SdlFileDialogs(Window window) : IFileDialogs
{
    private bool _busy;

    /// <inheritdoc/>
    public bool Busy => _busy;

    /// <inheritdoc/>
    public bool OpenFile(IReadOnlyList<(string Name, string Pattern)> filters, string? defaultLocation, Action<FileDialogResult> done) =>
        Start(done, finished => FileDialog.OpenFile(window, filters, defaultLocation, many: false, finished));

    /// <inheritdoc/>
    public bool SaveFile(IReadOnlyList<(string Name, string Pattern)> filters, string? defaultLocation, Action<FileDialogResult> done) =>
        Start(done, finished => FileDialog.SaveFile(window, filters, defaultLocation, finished));

    /// <inheritdoc/>
    public bool OpenFolder(string? defaultLocation, Action<FileDialogResult> done) =>
        Start(done, finished => FileDialog.OpenFolder(window, defaultLocation, many: false, finished));

    private bool Start(Action<FileDialogResult> done, Action<Action<FileDialogResult>> show)
    {
        if (_busy)
        {
            return false;
        }

        _busy = true;
        void Finished(FileDialogResult result)
        {
            _busy = false;
            done(result);
        }

        try
        {
            show(Finished);
        }
        catch (Exception e) when (e is PlatformException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            // SDL could not even be asked (a library without the dialogs): reported like a dialog that could not be shown, so the caller falls back.
            Finished(FileDialogResult.Failed(e.Message));
        }

        return true;
    }
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
    /// <summary>
    /// The virtual path (<c>res://scenes/level.ascene</c>) of <paramref name="osPath"/> when it is inside <paramref name="gameFolder"/>, with <c>/</c> separators; null for a path outside the folder, the folder
    /// itself, or one that is not a usable path. Folder names are compared without regard to case on Windows and macOS, whose file systems ignore it.
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
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameFolder));
            full = Path.GetFullPath(osPath);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.Length <= root.Length + 1 || !full.StartsWith(root, comparison) || !IsSeparator(full[root.Length]))
        {
            return null;
        }

        string relative = full[(root.Length + 1)..];
        if (Path.DirectorySeparatorChar == '\\')
        {
            relative = relative.Replace('\\', '/');
        }
        else if (relative.Contains('\\', StringComparison.Ordinal))
        {
            return null; // On Linux and macOS a backslash is part of a file name; the virtual file system would read it as a separator.
        }

        return relative.Length == 0 ? null : "res://" + relative;
    }

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}
