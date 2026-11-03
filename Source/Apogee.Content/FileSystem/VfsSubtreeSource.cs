using System.Collections.Generic;
using System.IO;

namespace Apogee.Content.FileSystem;

/// <summary>
/// A folder of the virtual file system as a source of its own: <c>res://packages/cool</c> seen as the root of a package, so that it can be mounted at <c>mod://cool</c> beside the
/// packages that are zips. Read-only, whatever the folder is made of (a real folder, a pack, a zip).
/// </summary>
public sealed class VfsSubtreeSource(VirtualFileSystem files, string root) : IFileSource
{
    private readonly string _root = VfsPath.Parse(root);

    public string Description => _root;

    /// <summary>The folder of the virtual file system this source is a view of.</summary>
    public string Root => _root;

    public FileKind Kind(string path) => files.KindOf(Join(path));

    public byte[] ReadAllBytes(string path) => files.ReadAllBytes(Join(path));

    public Stream OpenRead(string path) => files.OpenRead(Join(path));

    public void List(string directory, List<(string Name, FileKind Kind)> into)
    {
        string full = Join(directory);
        foreach (string file in files.EnumerateFiles(full))
        {
            into.Add((VfsPath.FileName(file), FileKind.File));
        }

        foreach (string folder in files.EnumerateDirectories(full))
        {
            into.Add((VfsPath.FileName(folder), FileKind.Directory));
        }
    }

    public string? PhysicalPath(string path) => files.TryGetPhysicalPath(Join(path));

    private string Join(string path) => path.Length == 0 ? _root : _root.EndsWith('/') ? _root + path : _root + "/" + path;
}
