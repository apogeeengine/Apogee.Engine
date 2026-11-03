using System;
using System.Buffers;
using System.Collections.Generic;

namespace Apogee.Content.FileSystem;

/// <summary>
/// Paths of the virtual file system: <c>scheme://a/b/c.txt</c>, always with <c>/</c>, case-sensitive. <see cref="Parse"/> is strict, for the paths a program or a script
/// names: no <c>.</c> or <c>..</c> segments, no empty ones, nothing a Windows file system would treat differently (a trailing dot or space, device names, <c>:</c>, wildcards),
/// so that a path means the same on every platform and cannot leave its mount. <see cref="Normalize"/> and <see cref="Combine"/> are lenient, for resolving a path a file
/// refers to (a texture next to a model, a <c>require("../x")</c>): <c>..</c> climbs but stops at the root of the scheme. They work on plain paths too.
/// </summary>
public static class VfsPath
{
    private static readonly HashSet<string> s_deviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    private static readonly SearchValues<char> s_forbidden = SearchValues.Create("\\:*?\"<>|");

    /// <summary>True for <c>scheme://…</c>: a lowercase scheme of letters, digits and underscores that starts with a letter, so a Windows drive (<c>C:/</c>) is not one.</summary>
    public static bool IsVirtual(string path) => TrySplit(path, out _, out _);

    /// <summary>Splits <c>scheme://rest</c> into <c>scheme</c> and <c>rest</c>.</summary>
    public static bool TrySplit(string path, out string scheme, out string rest)
    {
        scheme = rest = "";
        int colon = path.IndexOf(':', StringComparison.Ordinal);
        if (colon < 2 || path.Length < colon + 3 || path[colon + 1] != '/' || path[colon + 2] != '/')
        {
            return false;
        }

        if (!(path[0] is >= 'a' and <= 'z'))
        {
            return false;
        }

        for (int i = 1; i < colon; i++)
        {
            if (!(path[i] is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        scheme = path[..colon];
        rest = path[(colon + 3)..];
        return true;
    }

    /// <summary>The canonical form of <paramref name="path"/>: its scheme, then its segments, with no trailing slash (the root of a scheme is <c>res://</c>).</summary>
    /// <exception cref="VfsException">The path is not a valid virtual path.</exception>
    public static string Parse(string path) =>
        TryParse(path, out string canonical, out string? problem) ? canonical : throw new VfsException($"'{path}' is not a valid path: {problem}.");

    public static bool TryParse(string path, out string canonical, out string? problem)
    {
        canonical = "";
        problem = null;
        if (!TrySplit(path, out string scheme, out string rest))
        {
            problem = "it does not start with a scheme such as res:// or user://";
            return false;
        }

        if (rest.EndsWith('/'))
        {
            rest = rest[..^1];
        }

        if (rest.Length > 0)
        {
            foreach (string segment in rest.Split('/'))
            {
                if (!IsValidSegment(segment, out problem))
                {
                    return false;
                }
            }
        }

        canonical = scheme + "://" + rest;
        return true;
    }

    /// <summary>True when <paramref name="name"/> can be one segment of a path on every platform.</summary>
    public static bool IsValidSegment(string name, out string? problem)
    {
        problem = null;
        if (name.Length == 0)
        {
            problem = "it has an empty segment";
        }
        else if (name is "." or "..")
        {
            problem = $"it has a '{name}' segment";
        }
        else if (name.EndsWith('.') || name.EndsWith(' '))
        {
            problem = $"'{name}' ends with a dot or a space";
        }
        else if (name.AsSpan().IndexOfAny(s_forbidden) >= 0)
        {
            problem = $"'{name}' has a character that is not allowed in names (\\ : * ? \" < > |)";
        }
        else if (name.AsSpan().IndexOfAnyInRange('\0', '\u001f') >= 0)
        {
            problem = $"'{name}' has a control character";
        }
        else
        {
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            if (s_deviceNames.Contains(dot < 0 ? name : name[..dot]))
            {
                problem = $"'{name}' is a reserved device name on Windows";
            }
        }

        return problem is null;
    }

    /// <summary>Forward slashes, no <c>.</c> or <c>..</c> segments (a <c>..</c> at the root is dropped), no trailing slash. Keeps a scheme or a leading slash.</summary>
    public static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        bool isVirtual = TrySplit(path, out string scheme, out string rest);
        if (!isVirtual)
        {
            rest = path;
        }

        var parts = new List<string>();
        foreach (string part in rest.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        string joined = string.Join('/', parts);
        if (isVirtual)
        {
            return scheme + "://" + joined;
        }

        return path.StartsWith('/') ? "/" + joined : joined;
    }

    /// <summary>The folder of a path: <c>res://a/b.txt</c> gives <c>res://a</c>, and <c>res://b.txt</c> gives the root <c>res://</c>.</summary>
    public static string DirectoryOf(string path)
    {
        if (TrySplit(path, out string scheme, out string rest))
        {
            int inner = rest.LastIndexOf('/');
            return inner < 0 ? scheme + "://" : scheme + "://" + rest[..inner];
        }

        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    /// <summary>The folder above a folder, or null at the root.</summary>
    public static string? ParentOf(string directory)
    {
        if (TrySplit(directory, out _, out string rest))
        {
            return rest.Length == 0 ? null : DirectoryOf(directory);
        }

        return directory.Length <= 1 ? null : DirectoryOf(directory);
    }

    /// <summary>The last segment.</summary>
    public static string FileName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary><paramref name="relative"/> as seen from <paramref name="directory"/>: a path with a scheme stands for itself, a leading slash is the root of the directory's scheme. Not normalized.</summary>
    public static string Combine(string directory, string relative)
    {
        if (relative.Length == 0)
        {
            return directory;
        }

        if (IsVirtual(relative))
        {
            return relative;
        }

        if (relative.StartsWith('/'))
        {
            return TrySplit(directory, out string scheme, out _) ? scheme + "://" + relative.TrimStart('/') : relative;
        }

        return directory.EndsWith('/') ? directory + relative : directory + "/" + relative;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> or inside it. Both canonical or normalized.</summary>
    public static bool IsUnder(string path, string directory) =>
        path == directory
        || path.StartsWith(directory, StringComparison.Ordinal) && (directory.EndsWith('/') || path[directory.Length] == '/');
}
