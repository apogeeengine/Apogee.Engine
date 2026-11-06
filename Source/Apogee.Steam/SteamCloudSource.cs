using System;
using System.Collections.Generic;
using System.IO;
using Apogee.Content.FileSystem;
using Apogee.Interop;
using SteamApi = Apogee.Native.Steam.Steam;

namespace Apogee.Steam;

/// <summary>
/// The player's Steam Cloud files for the game, as a file source (mounted as <c>cloud://</c>): saves written there follow the player to their other machines. Steam keeps the files flat with
/// <c>/</c> in their names, so folders exist only as far as a file's name says. Steam limits the space and the size of a file; a write that Steam refuses throws an <see cref="IOException"/>.
/// Use <see cref="SteamSystem.CreateCloudSource"/>: when Steam is not running or the player switched the cloud off, it gives a plain folder instead, so a game saves the same way.
/// </summary>
internal sealed unsafe class SteamCloudSource : IFileSource
{
    private readonly SteamSystem _steam;

    internal SteamCloudSource(SteamSystem steam) => _steam = steam;

    public string Description => "Steam Cloud";

    public bool CanWrite => true;

    private Native.Steam.ISteamRemoteStorage* Storage => _steam.Storage;

    private List<(string Name, int Size)> Files()
    {
        int count = SteamApi.RemoteStorageGetFileCount(Storage);
        var files = new List<(string, int)>(count);
        for (int i = 0; i < count; i++)
        {
            int size;
            string? name = Utf8.FromPointer(SteamApi.RemoteStorageGetFileNameAndSize(Storage, i, &size));
            if (name is not null)
            {
                files.Add((name, size));
            }
        }

        return files;
    }

    private bool Exists(string path)
    {
        fixed (byte* name = Utf8.ToZeroTerminated(path))
        {
            return path.Length > 0 && SteamApi.RemoteStorageFileExists(Storage, name);
        }
    }

    public FileKind Kind(string path)
    {
        if (path.Length == 0)
        {
            return FileKind.Directory;
        }

        if (Exists(path))
        {
            return FileKind.File;
        }

        string prefix = path + "/";
        foreach ((string name, _) in Files())
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return FileKind.Directory;
            }
        }

        return FileKind.None;
    }

    public byte[] ReadAllBytes(string path)
    {
        fixed (byte* name = Utf8.ToZeroTerminated(path))
        {
            if (path.Length == 0 || !SteamApi.RemoteStorageFileExists(Storage, name))
            {
                throw new FileNotFoundException($"There is no cloud file '{path}'.", path);
            }

            byte[] bytes = new byte[SteamApi.RemoteStorageGetFileSize(Storage, name)];
            fixed (byte* data = bytes)
            {
                int read = SteamApi.RemoteStorageFileRead(Storage, name, data, bytes.Length);
                if (read != bytes.Length)
                {
                    throw new IOException($"Steam Cloud gave {read} of the {bytes.Length} bytes of '{path}'.");
                }
            }

            return bytes;
        }
    }

    public Stream OpenRead(string path) => new MemoryStream(ReadAllBytes(path), writable: false);

    public void List(string directory, List<(string Name, FileKind Kind)> into)
    {
        string prefix = directory.Length == 0 ? "" : directory + "/";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, _) in Files())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = name[prefix.Length..];
            int slash = rest.IndexOf('/', StringComparison.Ordinal);
            string entry = slash < 0 ? rest : rest[..slash];
            if (entry.Length > 0 && seen.Add(entry))
            {
                into.Add((entry, slash < 0 ? FileKind.File : FileKind.Directory));
            }
        }
    }

    public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* name = Utf8.ToZeroTerminated(path))
        fixed (byte* data = bytes)
        {
            if (path.Length == 0 || !SteamApi.RemoteStorageFileWrite(Storage, name, data, bytes.Length))
            {
                throw new IOException($"Steam Cloud did not take '{path}' ({bytes.Length} bytes): the file is too big, the name is not allowed, or the space is used up.");
            }
        }
    }

    public void Delete(string path)
    {
        if (Exists(path))
        {
            Remove(path);
            return;
        }

        string prefix = path + "/";
        foreach ((string name, _) in Files())
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                Remove(name);
            }
        }
    }

    private void Remove(string name)
    {
        fixed (byte* file = Utf8.ToZeroTerminated(name))
        {
            SteamApi.RemoteStorageFileDelete(Storage, file);
        }
    }

    public void CreateDirectory(string path)
    {
        // A cloud folder exists as far as a file in it does: nothing to make.
    }
}
