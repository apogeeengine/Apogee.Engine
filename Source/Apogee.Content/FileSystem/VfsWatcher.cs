using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Apogee.Content.FileSystem;

/// <summary>
/// Notices when files of a <see cref="VirtualFileSystem"/> change, for hot reload. It watches every folder that backs a mount (zips and packs do not change under a running game) and gives
/// the changes as virtual paths: the one the file has through the mount it was found in, and every other path that is a view of it (a package at <c>res://packages/core</c> is also
/// <c>mod://core</c>). Saving a file usually makes several events (a temporary file, a rename, a write): a path is reported once, after it has been quiet for <c>debounce</c>.
/// Files and folders whose name starts with a dot, and editors' temporary files, are ignored. Create it after the mounts; call <see cref="TakeChanges"/> from the thread that owns what is reloaded.
/// </summary>
public sealed class VfsWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.Ordinal);
    private readonly Mount[] _mounts;
    private readonly long _debounceTicks;

    public VfsWatcher(VirtualFileSystem files, TimeSpan? debounce = null)
    {
        _mounts = [.. files.Mounts];
        _debounceTicks = (debounce ?? TimeSpan.FromMilliseconds(150)).Ticks;
        foreach (Mount mount in _mounts.Where(m => m.Source is DirectorySource))
        {
            var source = (DirectorySource)mount.Source;
            if (!Directory.Exists(source.Root))
            {
                continue;
            }

            var watcher = new FileSystemWatcher(source.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            watcher.Changed += (_, e) => Note(source.Root, mount.Prefix, e.FullPath);
            watcher.Created += (_, e) => NoteCreated(source.Root, mount.Prefix, e.FullPath);
            watcher.Deleted += (_, e) => Note(source.Root, mount.Prefix, e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                Note(source.Root, mount.Prefix, e.OldFullPath);
                NoteCreated(source.Root, mount.Prefix, e.FullPath);
            };
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    /// <summary>The paths that changed and have been quiet for the debounce time, sorted; each is returned once.</summary>
    public IReadOnlyList<string> TakeChanges()
    {
        long now = DateTime.UtcNow.Ticks;
        var ready = new List<string>();
        foreach (KeyValuePair<string, long> entry in _pending)
        {
            if (now - entry.Value >= _debounceTicks && _pending.TryRemove(entry))
            {
                ready.Add(entry.Key);
            }
        }

        ready.Sort(StringComparer.Ordinal);
        return ready;
    }

    /// <summary>
    /// A new file or folder. The files already inside a new folder are noted too: the operating system starts watching the folder a moment after it exists, so what was put in it in that
    /// moment (a folder copied in, a file made right after its folder) would be missed.
    /// </summary>
    private void NoteCreated(string root, string prefix, string fullPath)
    {
        Note(root, prefix, fullPath);
        try
        {
            if (Directory.Exists(fullPath))
            {
                foreach (string file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                {
                    Note(root, prefix, file);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The folder went away again: its removal is a change of its own.
        }
    }

    private void Note(string root, string prefix, string fullPath)
    {
        string relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (relative == "." || relative.Split('/').Any(IsIgnored))
        {
            return;
        }

        long now = DateTime.UtcNow.Ticks;
        string virtualPath = prefix.EndsWith('/') ? prefix + relative : prefix + "/" + relative;
        _pending[virtualPath] = now;
        foreach (Mount mount in _mounts)
        {
            if (mount.Source is VfsSubtreeSource view && VfsPath.IsUnder(virtualPath, view.Root) && virtualPath != view.Root)
            {
                string inside = virtualPath[view.Root.Length..].TrimStart('/');
                _pending[mount.Prefix.EndsWith('/') ? mount.Prefix + inside : mount.Prefix + "/" + inside] = now;
            }
        }
    }

    private static bool IsIgnored(string name) =>
        name.StartsWith('.') || name.EndsWith('~') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }
}
