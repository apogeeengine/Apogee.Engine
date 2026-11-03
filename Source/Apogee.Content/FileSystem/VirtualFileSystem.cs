using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Apogee.Content.FileSystem;

/// <summary>One source mounted at a prefix of the virtual file system.</summary>
/// <param name="Prefix">A canonical path: <c>res://</c>, <c>mod://my_mod</c>.</param>
public sealed record Mount(string Prefix, IFileSource Source);

/// <summary>
/// The engine's view of its files: <c>res://</c> (the game's content, from folders and packs), <c>user://</c> (what the player's machine keeps: saves, settings, mods) and
/// <c>mod://&lt;id&gt;/</c> (a mod's own files), each made of mounted <see cref="IFileSource"/>s. A path is looked up in every source mounted at a prefix of it, the one mounted last
/// first, so that a patch pack or a mod's folder overrides what is under it; a source can also <see cref="FileKind.Removed"/> a file. Listings are the union of the sources, sorted
/// ordinally. Only a writable source takes writes, and a path that is not valid (see <see cref="VfsPath"/>) is refused, so a script cannot name anything outside its mounts. Paths are
/// case-sensitive, even on a file system that is not: write them as they are on disk.
/// </summary>
public sealed class VirtualFileSystem
{
    private readonly Lock _lock = new();
    private Mount[] _mounts = [];

    /// <summary>The mounts in the order they were added; later ones override earlier ones.</summary>
    public IReadOnlyList<Mount> Mounts => _mounts;

    /// <summary>
    /// The folder the player's files of a game go in: <c>&lt;local application data&gt;/&lt;organization&gt;/&lt;game&gt;</c> (<c>~/.local/share</c> on Linux, <c>AppData/Local</c>
    /// on Windows, <c>~/Library/Application Support</c> on macOS).
    /// </summary>
    public static string DefaultUserDirectory(string organization, string game) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), organization, game);

    /// <summary>Mounts <paramref name="source"/> at <paramref name="prefix"/>, over what is mounted there already.</summary>
    public void Mount(string prefix, IFileSource source)
    {
        string canonical = VfsPath.Parse(prefix);
        lock (_lock)
        {
            _mounts = [.. _mounts, new Mount(canonical, source)];
        }
    }

    /// <summary>Takes <paramref name="source"/> out of every prefix it is mounted at. The source is not disposed.</summary>
    public bool Unmount(IFileSource source)
    {
        lock (_lock)
        {
            Mount[] kept = [.. _mounts.Where(m => m.Source != source)];
            bool removed = kept.Length != _mounts.Length;
            _mounts = kept;
            return removed;
        }
    }

    // ---- Queries --------------------------------------------------------------------------------------------------------------------

    public bool FileExists(string path) => KindOf(path) == FileKind.File;

    public bool DirectoryExists(string path) => KindOf(path) == FileKind.Directory;

    public bool Exists(string path) => KindOf(path) != FileKind.None;

    /// <summary>What is at <paramref name="path"/>: a file, a directory (also the root of a scheme, and the folders above a mount), or <see cref="FileKind.None"/>. A path that is not valid is None.</summary>
    public FileKind KindOf(string path)
    {
        if (!VfsPath.TryParse(path, out string canonical, out _))
        {
            return FileKind.None;
        }

        Mount[] mounts = _mounts;
        for (int i = mounts.Length - 1; i >= 0; i--)
        {
            if (!TryRelative(mounts[i], canonical, out string relative))
            {
                continue;
            }

            FileKind kind = relative.Length == 0 ? FileKind.Directory : mounts[i].Source.Kind(relative);
            if (kind == FileKind.Removed)
            {
                return FileKind.None;
            }

            if (kind != FileKind.None)
            {
                return kind;
            }
        }

        // A folder that only exists because something is mounted under it (mod:// holds the mods' folders).
        return mounts.Any(m => m.Prefix != canonical && VfsPath.IsUnder(m.Prefix, canonical)) ? FileKind.Directory : FileKind.None;
    }

    // ---- Reading --------------------------------------------------------------------------------------------------------------------

    /// <exception cref="FileNotFoundException">No source has the file.</exception>
    /// <exception cref="VfsException">The path is not valid.</exception>
    public byte[] ReadAllBytes(string path)
    {
        (Mount mount, string relative) = Find(path);
        return mount.Source.ReadAllBytes(relative);
    }

    public Stream OpenRead(string path)
    {
        (Mount mount, string relative) = Find(path);
        return mount.Source.OpenRead(relative);
    }

    /// <summary>The file as UTF-8 text, without a byte order mark.</summary>
    public string ReadAllText(string path)
    {
        byte[] bytes = ReadAllBytes(path);
        ReadOnlySpan<byte> span = bytes;
        if (span.StartsWith(Encoding.UTF8.Preamble))
        {
            span = span[Encoding.UTF8.Preamble.Length..];
        }

        return Encoding.UTF8.GetString(span);
    }

    /// <summary>The path of the file in the real file system, when the source that has it is a folder: for watching it, or for a library that only takes a path. Null otherwise.</summary>
    public string? TryGetPhysicalPath(string path)
    {
        if (!VfsPath.TryParse(path, out string canonical, out _))
        {
            return null;
        }

        Mount[] mounts = _mounts;
        for (int i = mounts.Length - 1; i >= 0; i--)
        {
            if (TryRelative(mounts[i], canonical, out string relative) && relative.Length > 0)
            {
                FileKind kind = mounts[i].Source.Kind(relative);
                if (kind == FileKind.File)
                {
                    return mounts[i].Source.PhysicalPath(relative);
                }

                if (kind != FileKind.None)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>The files directly in <paramref name="directory"/> (or under it with <paramref name="recursive"/>) whose name matches <paramref name="pattern"/> (<c>*</c> and <c>?</c>), as full paths, sorted.</summary>
    public List<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false)
    {
        string root = VfsPath.Parse(directory);
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach ((string name, FileKind kind) in Children(current))
            {
                string child = Join(current, name);
                if (kind == FileKind.Directory)
                {
                    if (recursive)
                    {
                        pending.Push(child);
                    }
                }
                else if (Matches(pattern, name))
                {
                    result.Add(child);
                }
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>The folders directly inside <paramref name="directory"/>, as full paths, sorted.</summary>
    public List<string> EnumerateDirectories(string directory)
    {
        string root = VfsPath.Parse(directory);
        return [.. Children(root).Where(c => c.Kind == FileKind.Directory).Select(c => Join(root, c.Name)).Order(StringComparer.Ordinal)];
    }

    // ---- Writing --------------------------------------------------------------------------------------------------------------------

    /// <exception cref="UnauthorizedAccessException">The path is not in a writable source (only <c>user://</c> is one by default).</exception>
    public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        (Mount mount, string relative) = Writable(path);
        mount.Source.WriteAllBytes(relative, bytes);
    }

    public void WriteAllText(string path, string text) => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));

    public void Delete(string path)
    {
        (Mount mount, string relative) = Writable(path);
        mount.Source.Delete(relative);
    }

    public void CreateDirectory(string path)
    {
        (Mount mount, string relative) = Writable(path);
        mount.Source.CreateDirectory(relative);
    }

    // ---- Lookup ---------------------------------------------------------------------------------------------------------------------

    /// <summary>The source with the file, and the file's path in it.</summary>
    private (Mount Mount, string Relative) Find(string path)
    {
        string canonical = VfsPath.Parse(path);
        Mount[] mounts = _mounts;
        for (int i = mounts.Length - 1; i >= 0; i--)
        {
            if (!TryRelative(mounts[i], canonical, out string relative) || relative.Length == 0)
            {
                continue;
            }

            FileKind kind = mounts[i].Source.Kind(relative);
            if (kind == FileKind.Removed)
            {
                break;
            }

            if (kind == FileKind.File)
            {
                return (mounts[i], relative);
            }

            if (kind == FileKind.Directory)
            {
                throw new UnauthorizedAccessException($"'{canonical}' is a folder, not a file.");
            }
        }

        throw new FileNotFoundException($"There is no file '{canonical}'.", canonical);
    }

    /// <summary>The mount that holds <paramref name="path"/> (the one mounted last of those at a prefix of it), if it takes writes.</summary>
    private (Mount Mount, string Relative) Writable(string path)
    {
        string canonical = VfsPath.Parse(path);
        Mount[] mounts = _mounts;
        for (int i = mounts.Length - 1; i >= 0; i--)
        {
            if (TryRelative(mounts[i], canonical, out string relative) && relative.Length > 0)
            {
                return mounts[i].Source.CanWrite
                    ? (mounts[i], relative)
                    : throw new UnauthorizedAccessException($"'{canonical}' is read-only: it is in {mounts[i].Source.Description}.");
            }
        }

        throw new UnauthorizedAccessException($"'{canonical}' is not in any mount, so nothing can be written there.");
    }

    /// <summary>The entries inside a canonical directory: every source's, merged from the lowest to the highest, with removals and the mounts below it.</summary>
    private List<(string Name, FileKind Kind)> Children(string directory)
    {
        var merged = new Dictionary<string, FileKind>(StringComparer.Ordinal);
        var listing = new List<(string Name, FileKind Kind)>();
        foreach (Mount mount in _mounts)
        {
            if (!TryRelative(mount, directory, out string relative))
            {
                continue;
            }

            listing.Clear();
            mount.Source.List(relative, listing);
            foreach ((string name, FileKind kind) in listing)
            {
                if (kind == FileKind.Removed)
                {
                    merged.Remove(name);
                }
                else if (!merged.TryGetValue(name, out FileKind existing) || existing != FileKind.File || kind == FileKind.File)
                {
                    merged[name] = kind;
                }
            }
        }

        // A mounted prefix below this folder is a folder here, whatever the sources above it hold.
        foreach (Mount mount in _mounts)
        {
            if (mount.Prefix != directory && VfsPath.IsUnder(mount.Prefix, directory))
            {
                string below = mount.Prefix[directory.Length..].TrimStart('/');
                int slash = below.IndexOf('/', StringComparison.Ordinal);
                merged[slash < 0 ? below : below[..slash]] = FileKind.Directory;
            }
        }

        return [.. merged.Select(p => (p.Key, p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal)];
    }

    /// <summary>The path inside the mount when <paramref name="canonical"/> is at or under the mount's prefix.</summary>
    private static bool TryRelative(Mount mount, string canonical, out string relative)
    {
        relative = "";
        if (!VfsPath.IsUnder(canonical, mount.Prefix))
        {
            return false;
        }

        relative = canonical[mount.Prefix.Length..].TrimStart('/');
        return true;
    }

    private static string Join(string directory, string name) => directory.EndsWith('/') ? directory + name : directory + "/" + name;

    /// <summary><c>*</c> matches any run of characters and <c>?</c> one, case-sensitively.</summary>
    private static bool Matches(string pattern, string name)
    {
        int p = 0;
        int n = 0;
        int star = -1;
        int mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n]))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
