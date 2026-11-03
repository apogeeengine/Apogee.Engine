using System;
using System.Collections.Generic;
using System.IO;

namespace Apogee.Content.FileSystem;

/// <summary>
/// A folder of the real file system. Paths are joined to the folder after they were checked to hold only plain names, and the result is checked to be inside it. A link
/// inside the folder that points out of it is followed, as the operating system does: do not mount a folder whose contents you do not trust to be links-free.
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

        foreach (FileSystemInfo entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
        {
            into.Add((entry.Name, entry is DirectoryInfo ? FileKind.Directory : FileKind.File));
        }
    }

    public string? PhysicalPath(string path) => File.Exists(Full(path)) ? Full(path) : null;

    public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        RequireWritable();
        string full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Written beside the file and moved over it, so that a crash or a full disk leaves the old file whole.
        string temporary = full + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, full, overwrite: true);
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
