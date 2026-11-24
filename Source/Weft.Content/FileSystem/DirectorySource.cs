using System;
using System.Collections.Generic;
using System.IO;

namespace Weft.Content.FileSystem;

/// <summary>
/// A folder of the real file system. Paths are joined to the folder after they were checked to hold only plain names, and the result is checked to be inside it. A link
/// inside the folder that points out of it is followed, as the operating system does: do not mount a folder whose contents you do not trust to be links-free.
/// <para>
/// A write is durable and whole: the bytes go to a hidden file of a unique name beside the target, are flushed to the disk, and the file is then renamed over the target, so a crash or a full
/// disk leaves the old file, and not an empty one. A file that is a <em>symbolic link</em> is written through: the new bytes go to the file the link leads to, and the link stays a link. A file
/// that has several <em>hard links</em> is replaced by a new file: the other names keep the old bytes (the operating system does not say which names a file has, so this cannot be seen from here).
/// </para>
/// </summary>
public sealed class DirectorySource : IFileSource
{
    private readonly string _root;

    public DirectorySource(string root, bool writable = false)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        CanWrite = writable;
    }

    public string Description => _root;

    /// <summary>The folder, as a full path.</summary>
    public string Root => _root;

    public bool CanWrite { get; }

    public FileKind Kind(string path)
    {
        string full = Full(path);
        if (File.Exists(full))
        {
            return FileKind.File;
        }

        return Directory.Exists(full) ? FileKind.Directory : FileKind.None;
    }

    public byte[] ReadAllBytes(string path)
    {
        string full = Full(path);
        return File.Exists(full) ? File.ReadAllBytes(full) : throw new FileNotFoundException($"There is no file '{path}' in {_root}.", full);
    }

    public Stream OpenRead(string path)
    {
        string full = Full(path);
        return File.Exists(full) ? new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : throw new FileNotFoundException($"There is no file '{path}' in {_root}.", full);
    }

    public void List(string directory, List<(string Name, FileKind Kind)> into)
    {
        string full = Full(directory);
        if (!Directory.Exists(full))
        {
            return;
        }

        try
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                into.Add((entry.Name, entry is DirectoryInfo ? FileKind.Directory : FileKind.File));
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The folder was deleted since it was found: it has nothing in it now. (A folder that cannot be read is an error, and says so.)
        }
    }

    public string? PhysicalPath(string path) => File.Exists(Full(path)) ? Full(path) : null;

    public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        RequireWritable();
        string full = LinkTarget(Full(path));
        string directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);

        // Written beside the file, to disk, and moved over it, so that a crash or a full disk leaves the old file whole. The name is hidden and unique: it cannot be a file of the user's (the
        // old "<name>.tmp" could), and a leftover from a crash is not mistaken for an asset.
        string name = Path.GetFileName(full);
        string temporary = Path.Combine(directory, $".{(name.Length > 100 ? name[..100] : name)}.{Guid.NewGuid():N}.tmp");
        bool moved = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            KeepPermissions(full, temporary);
            File.Move(temporary, full, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved)
            {
                DeleteQuietly(temporary);
            }
        }
    }

    // A file that is a symbolic link is written where the link leads (a dangling link too: the file it names is made), so that the link stays a link.
    private static string LinkTarget(string full)
    {
        try
        {
            var info = new FileInfo(full);
            return info.LinkTarget is null ? full : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        catch (IOException)
        {
            return full; // a loop of links, or one that cannot be read: the write then replaces the link itself
        }
    }

    // A file replaced by a rename gets the mode of the one it replaces (an executable stays executable) where the system has modes.
    private static void KeepPermissions(string existing, string replacement)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(existing))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(replacement, File.GetUnixFileMode(existing));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The new file keeps the default mode: not worth failing the write for.
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temporary file that cannot be removed is a leftover, and the error that matters is the one being thrown.
        }
    }

    public void Delete(string path)
    {
        RequireWritable();
        string full = Full(path);
        if (path.Length == 0)
        {
            throw new UnauthorizedAccessException("The root of a mount cannot be deleted.");
        }

        if (File.Exists(full))
        {
            File.Delete(full);
        }
        else if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    public void Move(string source, string target)
    {
        RequireWritable();
        string fullSource = Full(source);
        string fullTarget = Full(target);
        if (!File.Exists(fullSource))
        {
            throw new FileNotFoundException($"There is no file '{source}' in {_root}.", fullSource);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);

        // One rename on the same volume, and it refuses to replace anything: the caller has checked that the target is free, and a file that appeared since is not ours to overwrite.
        File.Move(fullSource, fullTarget, overwrite: false);
    }

    public void CreateDirectory(string path)
    {
        RequireWritable();
        Directory.CreateDirectory(Full(path));
    }

    private void RequireWritable()
    {
        if (!CanWrite)
        {
            throw new UnauthorizedAccessException($"{_root} is read-only.");
        }
    }

    /// <summary>The real path of <paramref name="path"/>, which must be made of plain names.</summary>
    private string Full(string path)
    {
        if (path.Length == 0)
        {
            return _root;
        }

        foreach (string segment in path.Split('/'))
        {
            if (!VfsPath.IsValidSegment(segment, out string? problem))
            {
                throw new VfsException($"'{path}' is not a valid path: {problem}.");
            }
        }

        string full = Path.GetFullPath(Path.Combine(_root, path));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new VfsException($"'{path}' leaves {_root}.");
        }

        return full;
    }
}
