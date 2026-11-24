using System;
using System.Collections.Generic;
using System.IO;
using Weft.Content.FileSystem;

namespace Weft.Ecs.Tests;

/// <summary>A folder whose chosen writes throw, as a full or failing disk does (see <see cref="FailOn"/>); everything else is the folder's.</summary>
public sealed class FaultySource(DirectorySource inner) : IFileSource
{
    private HashSet<int> _failing = [];
    private int _writes;

    /// <summary>How many writes were tried since <see cref="FailOn"/>.</summary>
    public int Writes => _writes;

    /// <summary>From now on, the writes with these numbers (the first is 1) fail.</summary>
    public void FailOn(params int[] writes)
    {
        _failing = [.. writes];
        _writes = 0;
    }

    public string Description => inner.Description;

    public bool CanWrite => inner.CanWrite;

    public FileKind Kind(string path) => inner.Kind(path);

    public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);

    public Stream OpenRead(string path) => inner.OpenRead(path);

    public void List(string directory, List<(string Name, FileKind Kind)> into) => inner.List(directory, into);

    public string? PhysicalPath(string path) => inner.PhysicalPath(path);

    public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        _writes++;
        if (_failing.Contains(_writes))
        {
            throw new IOException($"No space left on device (injected, write {_writes} of {path}).");
        }

        inner.WriteAllBytes(path, bytes);
    }

    public void Delete(string path) => inner.Delete(path);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public void Move(string source, string target) => inner.Move(source, target);
}
