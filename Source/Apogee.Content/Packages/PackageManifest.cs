using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;

namespace Apogee.Content.Packages;

/// <summary>A package that another needs, and which versions of it.</summary>
public sealed record PackageDependency(string Id, VersionRange Version);

/// <summary>
/// The <c>mod.xml</c> at the root of a package. Checked against the engine's schema (<c>Packages/mod.xsd</c>) and further: the id is lowercase letters, digits and underscores
/// (and not one the engine uses: <c>engine</c>, <c>self</c>, <c>res</c>, <c>user</c>, <c>mod</c>), versions and ranges parse, and the paths it names are relative and stay inside the package.
/// </summary>
public sealed class PackageManifest
{
    /// <summary>The newest API of the engine that packages are written against. A package that needs a newer one is not loaded.</summary>
    public const int EngineApiVersion = 1;

    private static readonly HashSet<string> s_reserved = new(StringComparer.Ordinal) { "engine", "self", "res", "user", "mod" };
    private static readonly Lazy<SchemaSet> s_schema = new(LoadSchema);

    private PackageManifest(string id)
    {
        Id = id;
    }

    public string Id { get; }

    /// <summary>The name to show; the id when the manifest has none.</summary>
    public string Name { get; private set; } = "";

    public PackageVersion Version { get; private set; }

    public string Author { get; private set; } = "";

    public string Description { get; private set; } = "";

    public int ApiVersion { get; private set; } = 1;

    /// <summary>The versions of the game this package works with.</summary>
    public VersionRange GameVersion { get; private set; } = VersionRange.Any;

    public IReadOnlyList<PackageDependency> Dependencies { get; private set; } = [];

    /// <summary>Scripts to run, as paths inside the package, in order.</summary>
    public IReadOnlyList<string> Scripts { get; private set; } = [];

    /// <summary>Folders of definitions, as paths inside the package.</summary>
    public IReadOnlyList<string> ContentFolders { get; private set; } = [];

    /// <summary>Folders of schemas, as paths inside the package.</summary>
    public IReadOnlyList<string> SchemaFolders { get; private set; } = [];

    private static SchemaSet LoadSchema()
    {
        using Stream stream = typeof(PackageManifest).Assembly.GetManifestResourceStream("Packages/mod.xsd") ?? throw new InvalidOperationException("The embedded schema of mod.xml is missing.");
        using var reader = new StreamReader(stream);
        var set = new SchemaSet();
        set.LoadText(reader.ReadToEnd(), "mod.xsd");
        return set;
    }

    /// <summary>Reads a manifest. <paramref name="problems"/> lists everything wrong, with lines; the manifest is null when anything is.</summary>
    public static bool TryParse(string text, string origin, out PackageManifest? manifest, out IReadOnlyList<string> problems)
    {
        manifest = null;
        var errors = new List<string>();
        problems = errors;

        XmlNode node;
        try
        {
            node = XmlNode.Parse(text, origin);
        }
        catch (XmlParseException e)
        {
            errors.Add(e.Message);
            return false;
        }

        if (node.Name != "Mod")
        {
            errors.Add($"{origin}:{node.Line}: the root element is <{node.Name}>, not <Mod>.");
            return false;
        }

        DecodeResult result = s_schema.Value.Decode(node, origin);
        errors.AddRange(result.Problems.Where(p => p.Severity == ProblemSeverity.Error).Select(p => $"{p.File}:{p.Line}: {p.Message}"));
        if (errors.Count > 0 || result.Value is not DefRecord record)
        {
            return false;
        }

        string id = Text(record, "id");
        if (s_reserved.Contains(id))
        {
            errors.Add($"{origin}:{node.Line}: '{id}' is a name the engine uses: a package cannot have it as its id.");
        }

        var built = new PackageManifest(id) { Name = Text(record, "name") is { Length: > 0 } name ? name : id, Author = Text(record, "author"), Description = Text(record, "description") };
        if (!PackageVersion.TryParse(Text(record, "version"), out PackageVersion version))
        {
            errors.Add($"{origin}:{node.Line}: the version '{Text(record, "version")}' is not major.minor.patch.");
        }

        built.Version = version;
        built.ApiVersion = (int)((record["apiVersion"] as DefNumber)?.Value ?? 1);
        built.GameVersion = Range(Text(record, "gameVersion"), $"the gameVersion of '{id}'", origin, node.Line, errors);

        var dependencies = new List<PackageDependency>();
        foreach (DefValue entry in Items(record, "Dependency"))
        {
            var dependency = (DefRecord)entry;
            string dependencyId = Text(dependency, "id");
            dependencies.Add(new PackageDependency(dependencyId, Range(Text(dependency, "version"), $"the version of the dependency '{dependencyId}'", origin, node.Line, errors)));
            if (dependencyId == id)
            {
                errors.Add($"{origin}:{node.Line}: '{id}' depends on itself.");
            }
        }

        built.Dependencies = dependencies;
        built.Scripts = Paths(record, "Script", origin, node.Line, errors, requireScript: true);
        built.ContentFolders = Paths(record, "Content", origin, node.Line, errors, requireScript: false);
        built.SchemaFolders = Paths(record, "Schema", origin, node.Line, errors, requireScript: false);

        if (errors.Count > 0)
        {
            return false;
        }

        manifest = built;
        return true;
    }

    private static string Text(DefRecord record, string name) => record[name] is DefText text ? text.Value : "";

    private static IEnumerable<DefValue> Items(DefRecord record, string name) => record[name] is DefList list ? list.Items : [];

    private static VersionRange Range(string text, string what, string origin, int line, List<string> errors)
    {
        if (VersionRange.TryParse(text, out VersionRange? range, out string? problem))
        {
            return range!;
        }

        errors.Add($"{origin}:{line}: {what}: {problem}");
        return VersionRange.Any;
    }

    private static List<string> Paths(DefRecord record, string name, string origin, int line, List<string> errors, bool requireScript)
    {
        var paths = new List<string>();
        foreach (DefValue entry in Items(record, name))
        {
            string path = Text((DefRecord)entry, "path");
            if (path.Length == 0 || path.StartsWith('/') || path.Contains("://", StringComparison.Ordinal) || !VfsPath.TryParse("pkg://" + path.TrimEnd('/'), out _, out _))
            {
                errors.Add($"{origin}:{line}: the path '{path}' of a <{name}> must be a plain relative path inside the package (no '..', no scheme, no drive).");
            }
            else if (requireScript && !path.EndsWith(".luau", StringComparison.Ordinal))
            {
                errors.Add($"{origin}:{line}: the script '{path}' is not a .luau file.");
            }
            else
            {
                paths.Add(path.TrimEnd('/'));
            }

        }

        return paths;
    }
}
