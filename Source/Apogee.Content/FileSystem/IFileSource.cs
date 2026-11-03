using System;
using System.Collections.Generic;
using System.IO;

namespace Apogee.Content.FileSystem;

/// <summary>What a source knows about a path.</summary>
public enum FileKind
{
    /// <summary>Nothing: the sources below it may still have it.</summary>
    None,

    File,

    Directory,

    /// <summary>The source deletes the path: a patch pack that takes a file away from the packs below it. The path is gone for everything under this source.</summary>
    Removed,
}

/// <summary>
/// A folder, a zip file or a pack that a <see cref="VirtualFileSystem"/> mounts. Paths are relative to the source: canonical, with <c>/</c>, no leading slash, and
/// the empty string for the source's root (see <see cref="VfsPath"/>). Reads are safe from several threads.
/// </summary>
public interface IFileSource
{
    /// <summary>What the source is, for messages: a folder's path, a zip's file name.</summary>
    string Description { get; }

    FileKind Kind(string path);

    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    byte[] ReadAllBytes(string path);

    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    Stream OpenRead(string path);

    /// <summary>Adds the entries directly inside <paramref name="directory"/> to <paramref name="into"/>: their names and kinds, in any order (a source may list <see cref="FileKind.Removed"/> entries).</summary>
    void List(string directory, List<(string Name, FileKind Kind)> into);

    /// <summary>The path in the real file system, when there is one: for watching a file or handing it to a library that wants a path.</summary>
    string? PhysicalPath(string path) => null;

    bool CanWrite => false;

    /// <summary>Writes the file whole, making the folders above it; an interrupted write leaves the old file.</summary>
    void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => throw new UnauthorizedAccessException($"{Description} is read-only.");

    /// <summary>Deletes a file, or a folder with what is in it. Does nothing when there is none.</summary>
    void Delete(string path) => throw new UnauthorizedAccessException($"{Description} is read-only.");

    void CreateDirectory(string path) => throw new UnauthorizedAccessException($"{Description} is read-only.");
}

/// <summary>A path that is not valid, or that leaves its mount.</summary>
public sealed class VfsException : IOException
{
    public VfsException(string message)
        : base(message)
    {
    }
}
