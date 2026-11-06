using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Apogee.Content.FileSystem;
using Apogee.Core.Logging;

namespace Apogee.Steam;

/// <summary>
/// The Workshop items Steam installed, as a folder of mod folders for the package finder: each is listed under the id its <c>mod.xml</c> gives (a package's id is the name of its folder), and
/// the files are Steam's own, read-only, where Steam put them. The list is made when the game starts, so an item installed later is found at the next start.
/// </summary>
public sealed class WorkshopSource : IFileSource
{
    private readonly SortedDictionary<string, DirectorySource> _mods = new(StringComparer.Ordinal);

    /// <summary>Lists the given item folders (item id and folder, as <see cref="SteamSystem.InstalledWorkshopItems"/> gives them).</summary>
    public WorkshopSource(IEnumerable<(string ItemId, string Folder)> items)
    {
        foreach ((string item, string folder) in items)
        {
            string? id = ReadId(Path.Combine(folder, "mod.xml"));
            if (id is null)
            {
                Log.Warn("Steam", $"Workshop item {item} ({folder}) has no mod.xml with an id: it is not a mod.");
            }
            else if (!_mods.TryAdd(id, new DirectorySource(folder)))
            {
                Log.Warn("Steam", $"Workshop item {item} has the mod id '{id}', which another item has too: the first one is used.");
            }
        }
    }

    public string Description => "Steam Workshop";

    private static string? ReadId(string manifest)
    {
        try
        {
            return File.Exists(manifest) ? XDocument.Load(manifest).Root?.Attribute("id")?.Value : null;
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool Find(string path, out DirectorySource? mod, out string rest)
    {
        int slash = path.IndexOf('/', StringComparison.Ordinal);
        string id = slash < 0 ? path : path[..slash];
        rest = slash < 0 ? "" : path[(slash + 1)..];
        return _mods.TryGetValue(id, out mod);
    }

    public FileKind Kind(string path)
    {
        if (path.Length == 0)
        {
            return FileKind.Directory;
        }

        return Find(path, out DirectorySource? mod, out string rest) ? mod!.Kind(rest) : FileKind.None;
    }

    public byte[] ReadAllBytes(string path) =>
        Find(path, out DirectorySource? mod, out string rest) ? mod!.ReadAllBytes(rest) : throw new FileNotFoundException($"There is no Workshop file '{path}'.", path);

    public Stream OpenRead(string path) =>
        Find(path, out DirectorySource? mod, out string rest) ? mod!.OpenRead(rest) : throw new FileNotFoundException($"There is no Workshop file '{path}'.", path);

    public void List(string directory, List<(string Name, FileKind Kind)> into)
    {
        if (directory.Length == 0)
        {
            foreach (string id in _mods.Keys)
            {
                into.Add((id, FileKind.Directory));
            }
        }
        else if (Find(directory, out DirectorySource? mod, out string rest))
        {
            mod!.List(rest, into);
        }
    }

    public string? PhysicalPath(string path) => Find(path, out DirectorySource? mod, out string rest) ? mod!.PhysicalPath(rest) : null;
}
