using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;

namespace Apogee.Content.Packages;

/// <summary>Where <see cref="PackageSet.Load"/> looks for packages, and what it checks them against.</summary>
public sealed record PackageOptions
{
    /// <summary>The game's own packages: one folder each, named by the package's id.</summary>
    public string BasePackagesPath { get; init; } = "res://packages";

    /// <summary>Mod folders of a development build.</summary>
    public string DevModsPath { get; init; } = "res://dev_mods";

    /// <summary>The player's mods: a folder or a <c>.zip</c> each, named by the id.</summary>
    public string UserModsPath { get; init; } = "user://mods";

    /// <summary>
    /// More folders of the player's mods (a folder each, named by the id), searched after <see cref="UserModsPath"/>: the Workshop items Steam installed, for example. They are the player's mods
    /// in every way: the player can switch them off, and a package with an id that was found earlier is ignored.
    /// </summary>
    public IReadOnlyList<string> ExtraUserModsPaths { get; init; } = [];

    /// <summary>The list of mods the player switched off.</summary>
    public string UserSettingsPath { get; init; } = "user://mods.xml";

    /// <summary>The version of the game, for the <c>gameVersion</c> of each package (null: not checked).</summary>
    public PackageVersion? GameVersion { get; init; }

    public int ApiVersion { get; init; } = PackageManifest.EngineApiVersion;
}

/// <summary>The mods the player switched off, in <c>user://mods.xml</c> (<c>&lt;Mods&gt;&lt;Disabled id="cool"/&gt;&lt;/Mods&gt;</c>). Only the game writes it, never a mod; base packages ignore it.</summary>
public static class UserModSettings
{
    public static HashSet<string> LoadDisabled(VirtualFileSystem files, string path = "user://mods.xml")
    {
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        if (!files.FileExists(path))
        {
            return disabled;
        }

        try
        {
            foreach (XmlNode node in XmlNode.Parse(files.ReadAllText(path), path).Children.Where(c => c.Name == "Disabled"))
            {
                if (node.Attribute("id") is { Length: > 0 } id)
                {
                    disabled.Add(id);
                }
            }
        }
        catch (XmlParseException)
        {
            // A damaged list switches nothing off.
        }

        return disabled;
    }

    public static void SaveDisabled(VirtualFileSystem files, IEnumerable<string> ids, string path = "user://mods.xml")
    {
        var root = new XmlNode("Mods");
        foreach (string id in ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var node = new XmlNode("Disabled");
            node.SetAttribute("id", id);
            root.Children.Add(node);
        }

        files.WriteAllText(path, root.ToXml());
    }
}

/// <summary>
/// The packages of a game: the base game's and the mods. <see cref="Load"/> finds them (<see cref="PackageOptions"/>), reads their manifests, decides which load and in which order
/// (<see cref="PackageSolver"/>), and mounts each one that loads at <c>mod://&lt;id&gt;/</c>, whatever it is (a folder, a zip), so that a package's files have one address everywhere.
/// What is wrong with a package that does not load is in its <see cref="SolvedPackage.Reason"/>; what is wrong with the search itself (a manifest that does not parse, an id that is not its
/// folder's name) is in <see cref="Problems"/>. Dispose it to take the mounts away.
/// </summary>
public sealed class PackageSet : IDisposable
{
    private readonly VirtualFileSystem _files;
    private readonly List<SolvedPackage> _mounted = [];
    private readonly List<string> _problems;

    private PackageSet(VirtualFileSystem files, SolveResult result, List<string> problems)
    {
        _files = files;
        Packages = result.Packages;
        LoadOrder = result.LoadOrder;
        _problems = problems;
        _problems.AddRange(result.Problems);
    }

    /// <summary>Every package found, whether it loads or not.</summary>
    public IReadOnlyList<SolvedPackage> Packages { get; }

    /// <summary>The ones that load, dependencies first.</summary>
    public IReadOnlyList<SolvedPackage> LoadOrder { get; }

    public IReadOnlyList<string> Problems => _problems;

    public SolvedPackage? Find(string id) => Packages.FirstOrDefault(p => p.Id == id);

    /// <summary>The folders of definitions of the packages that load, in load order, at their <c>mod://</c> addresses.</summary>
    public IReadOnlyList<DefFolder> ContentFolders => [.. LoadOrder.SelectMany(p => p.Manifest.ContentFolders.Select(f => new DefFolder(p.Id, Address(p, f))))];

    /// <summary>The folders of schemas of the packages that load, in load order.</summary>
    public IReadOnlyList<string> SchemaFolders => [.. LoadOrder.SelectMany(p => p.Manifest.SchemaFolders.Select(f => Address(p, f)))];

    /// <summary>The scripts of the packages that load: in load order, and in each package in the order its manifest lists them.</summary>
    public IReadOnlyList<(SolvedPackage Package, string Path)> Scripts => [.. LoadOrder.SelectMany(p => p.Manifest.Scripts.Select(s => (p, Address(p, s))))];

    /// <summary>The address of a path inside a package: <c>mod://&lt;id&gt;/&lt;path&gt;</c>.</summary>
    public static string Address(SolvedPackage package, string path) => $"mod://{package.Id}/{path}";

    /// <summary>
    /// Finds, solves and mounts the packages. <paramref name="files"/> must have what the options point at mounted (the game's content at <c>res://</c>, the player's folder at <c>user://</c>);
    /// a missing folder is simply no packages.
    /// </summary>
    public static PackageSet Load(VirtualFileSystem files, PackageOptions? options = null)
    {
        options ??= new PackageOptions();
        var problems = new List<string>();
        var candidates = new List<PackageCandidate>();
        var zips = new List<ZipSource>();

        Scan(files, PackageKind.Base, options.BasePackagesPath, zipsToo: false, candidates, zips, problems);
        Scan(files, PackageKind.Dev, options.DevModsPath, zipsToo: false, candidates, zips, problems);
        Scan(files, PackageKind.User, options.UserModsPath, zipsToo: true, candidates, zips, problems);
        foreach (string extra in options.ExtraUserModsPaths)
        {
            Scan(files, PackageKind.User, extra, zipsToo: false, candidates, zips, problems);
        }

        SolveResult result = PackageSolver.Solve(candidates, new SolveOptions(options.ApiVersion, options.GameVersion, UserModSettings.LoadDisabled(files, options.UserSettingsPath)));
        var set = new PackageSet(files, result, problems);
        foreach (SolvedPackage package in result.LoadOrder)
        {
            package.Source = package.Candidate.OpenSource();
            files.Mount("mod://" + package.Id, package.Source);
            set._mounted.Add(package);
        }

        // A zip that is not going to be used is closed now.
        foreach (ZipSource zip in zips.Where(z => !set._mounted.Any(p => p.Source == z)))
        {
            zip.Dispose();
        }

        return set;
    }

    private static void Scan(VirtualFileSystem files, PackageKind kind, string path, bool zipsToo, List<PackageCandidate> candidates, List<ZipSource> zips, List<string> problems)
    {
        if (!files.DirectoryExists(path))
        {
            return;
        }

        foreach (string folder in files.EnumerateDirectories(path))
        {
            string manifestPath = folder + "/mod.xml";
            if (!files.FileExists(manifestPath))
            {
                problems.Add($"{folder} has no mod.xml, so it is not a package.");
                continue;
            }

            if (Check(files.ReadAllText(manifestPath), manifestPath, VfsPath.FileName(folder), problems) is { } manifest)
            {
                string location = folder;
                candidates.Add(new PackageCandidate(manifest, kind, location, () => new VfsSubtreeSource(files, location)));
            }
        }

        if (!zipsToo)
        {
            return;
        }

        foreach (string file in files.EnumerateFiles(path, "*.zip"))
        {
            ZipSource zip;
            try
            {
                zip = new ZipSource(files.OpenRead(file), VfsPath.FileName(file));
            }
            catch (IOException e)
            {
                problems.Add($"{file}: {e.Message}");
                continue;
            }

            zips.Add(zip);
            if (((IFileSource)zip).Kind("mod.xml") != FileKind.File)
            {
                problems.Add($"{file} has no mod.xml at its root, so it is not a package.");
                continue;
            }

            string name = VfsPath.FileName(file)[..^".zip".Length];
            if (Check(System.Text.Encoding.UTF8.GetString(zip.ReadAllBytes("mod.xml")), file + "!mod.xml", name, problems) is { } manifest)
            {
                candidates.Add(new PackageCandidate(manifest, kind, file, () => zip));
            }
        }
    }

    /// <summary>The manifest in <paramref name="text"/> if it is valid and its id is <paramref name="expectedId"/> (the name of the folder or zip it is in).</summary>
    private static PackageManifest? Check(string text, string origin, string expectedId, List<string> problems)
    {
        if (!PackageManifest.TryParse(text, origin, out PackageManifest? manifest, out IReadOnlyList<string> errors))
        {
            problems.AddRange(errors);
            return null;
        }

        if (manifest!.Id != expectedId)
        {
            problems.Add($"{origin}: the id '{manifest.Id}' must be the name of the package's folder (or .zip), which is '{expectedId}'.");
            return null;
        }

        return manifest;
    }

    /// <summary>Loads the schemas of the packages that load (a package whose schema is wrong is reported in <see cref="Problems"/> and its schemas are left out).</summary>
    public SchemaSet LoadSchemas()
    {
        var schemas = new SchemaSet();
        foreach (string folder in SchemaFolders)
        {
            try
            {
                schemas.LoadFolder(_files, folder);
            }
            catch (ContentException e)
            {
                _problems.Add(e.Message);
            }
        }

        return schemas;
    }

    /// <summary>Reads the definitions of the packages that load and checks them against their schemas.</summary>
    public DefDatabase LoadDefs() => DefDatabase.Load(_files, ContentFolders, LoadSchemas());

    public void Dispose()
    {
        foreach (SolvedPackage package in _mounted)
        {
            _files.Unmount(package.Source!);
            (package.Source as IDisposable)?.Dispose();
        }

        _mounted.Clear();
    }
}
