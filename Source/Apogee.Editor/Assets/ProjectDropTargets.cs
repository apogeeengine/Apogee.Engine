using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Apogee.Content.FileSystem;

namespace Apogee.Editor.Assets;

/// <summary>
/// The open project's files as <see cref="DropPlan"/> and <see cref="ImportJob"/> ask about them. <c>res://</c> is the game folder (the same assumption <see cref="Editing.VirtualPaths"/> makes); a link
/// in it counts as something that is there even when what it points to is gone, because writing to it would write through it, to wherever it leads. Safe from any thread: it keeps no state.
/// </summary>
public sealed class ProjectDropTargets(VirtualFileSystem files, string gameFolder) : IDropTargets
{
    /// <inheritdoc/>
    public bool Exists(string virtualPath) => files.Exists(virtualPath) || IsLinkAt(virtualPath);

    /// <inheritdoc/>
    public bool IsFolder(string virtualPath) => files.DirectoryExists(virtualPath);

    /// <inheritdoc/>
    public bool CanWrite(string virtualPath) => files.CanWrite(virtualPath);

    /// <inheritdoc/>
    public bool IsLink(string virtualPath) => IsLinkAt(virtualPath);

    private bool IsLinkAt(string virtualPath)
    {
        const string root = "res://";
        if (!virtualPath.StartsWith(root, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            string physical = Path.Combine(gameFolder, virtualPath[root.Length..].Replace('/', Path.DirectorySeparatorChar));
            return new FileInfo(physical).LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
/// "An importer is running": the one rule that keeps two of them from writing <c>imported/index.xml</c> at once. The editor's worker (<see cref="ImportJob"/>) and the main thread's use of the hot reloader
/// (which imports what changed, see <c>EditorProject.Update</c>) and of the asset browser's re-import each take it before they start and give it back when they end; a taker that finds it held does not
/// import, it waits for another frame. It is not a lock that blocks: the main thread must never wait for the worker.
/// </summary>
public sealed class ImportGate
{
    // One per game folder for the whole process, not one per open project: closing a project while a job still runs (a Basis encode cannot be cut short) and opening the same folder again must not give two importers
    // two gates. Few entries, ever (one per folder the editor opened).
    private static readonly ConcurrentDictionary<string, ImportGate> s_byFolder = new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private int _held;

    /// <summary>The gate of the game folder <paramref name="gameFolder"/>: the same object for the same folder, however it is spelled (a trailing slash, a link to it).</summary>
    public static ImportGate ForFolder(string gameFolder) =>
        s_byFolder.GetOrAdd(Path.TrimEndingDirectorySeparator(Apogee.Editor.Editing.VirtualPaths.RealPath(Path.GetFullPath(gameFolder))), _ => new ImportGate());

    /// <summary>Someone holds the gate.</summary>
    public bool IsHeld => Volatile.Read(ref _held) != 0;

    /// <summary>Takes the gate: false when it is held. Whoever gets true must call <see cref="Exit"/> exactly once, from any thread.</summary>
    public bool TryEnter() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;

    /// <summary>Gives the gate back.</summary>
    public void Exit() => Volatile.Write(ref _held, 0);
}
