using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Apogee.Core.Logging;

namespace Apogee.Content.FileSystem;

/// <summary>
/// A zip file, for mods that ship as one. The directory is read when it is opened; entries whose names are not valid paths (<c>..</c>, absolute, a backslash that does not
/// make a valid path) are skipped with a warning, so that no entry can name a place outside the zip. Files are inflated on read, whole.
/// </summary>
public sealed class ZipSource : IFileSource, IDisposable
{
    private const string LogCategory = "Content";

    private readonly ZipArchive _archive;
    private readonly string _description;
    private readonly Dictionary<string, ZipArchiveEntry> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Name, FileKind Kind)>> _directories = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Opens the zip file at <paramref name="path"/>.</summary>
    public ZipSource(string path)
        : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), Path.GetFileName(path))
    {
    }

    /// <summary>Opens a zip from a stream (which this takes over: it is closed with the source).</summary>
    public ZipSource(Stream stream, string description)
    {
        _description = description;
        try
        {
            _archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException e)
        {
            stream.Dispose();
            throw new IOException($"'{description}' is not a valid zip file: {e.Message}", e);
        }

        _directories[""] = [];
        foreach (ZipArchiveEntry entry in _archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            bool isDirectory = name.EndsWith('/');
            if (isDirectory)
            {
                name = name[..^1];
            }

            if (name.Length == 0)
            {
                continue;
            }

            if (!VfsPath.TryParse("zip://" + name, out string canonical, out string? problem))
            {
                Log.Warn(LogCategory, $"{description}: the entry '{entry.FullName}' is skipped: {problem}.");
                continue;
            }

            string path = canonical["zip://".Length..];
            if (!isDirectory)
            {
                _files[path] = entry;
            }

            AddEntry(path, isDirectory ? FileKind.Directory : FileKind.File);
        }
    }

    public string Description => _description;

    /// <summary>Records <paramref name="path"/> in its folder, and the folders above it, which a zip need not list.</summary>
    private void AddEntry(string path, FileKind kind)
    {
        while (path.Length > 0)
        {
            int slash = path.LastIndexOf('/');
            string parent = slash < 0 ? "" : path[..slash];
            string name = path[(slash + 1)..];
            if (!_directories.TryGetValue(parent, out List<(string Name, FileKind Kind)>? children))
            {
                _directories[parent] = children = [];
            }

            if (children.Exists(c => c.Name == name))
            {
                return;
            }

            children.Add((name, kind));
            if (kind == FileKind.Directory && !_directories.ContainsKey(path))
            {
                _directories[path] = [];
            }

            path = parent;
            kind = FileKind.Directory;
        }
    }

    public FileKind Kind(string path)
    {
        if (_files.ContainsKey(path))
        {
            return FileKind.File;
        }

        return _directories.ContainsKey(path) ? FileKind.Directory : FileKind.None;
    }

    public byte[] ReadAllBytes(string path)
    {
        if (!_files.TryGetValue(path, out ZipArchiveEntry? entry))
        {
            throw new FileNotFoundException($"There is no file '{path}' in {_description}.");
        }

        lock (_lock)
        {
            using Stream stream = entry.Open();
            byte[] bytes = new byte[entry.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }

    public Stream OpenRead(string path) => new MemoryStream(ReadAllBytes(path), writable: false);

    public void List(string directory, List<(string Name, FileKind Kind)> into)
    {
        if (_directories.TryGetValue(directory, out List<(string Name, FileKind Kind)>? children))
        {
            into.AddRange(children);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _archive.Dispose();
        }
    }
}
