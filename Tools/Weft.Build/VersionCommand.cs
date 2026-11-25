using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Weft.Build;

/// <summary>
/// <c>./build.sh version</c> prints the engine's version (<c>Engine.props</c>); <c>./build.sh version check [--tag &lt;name&gt;]</c> is the check of the static checks workflow: <c>Engine.props</c> is valid, the
/// build reads it (<c>Directory.Build.props</c>, <c>Native/CMakeLists.txt</c>), nothing else sets a version, and a release tag is <c>v</c> and the version.
/// </summary>
public static partial class VersionCommand
{
    /// <summary>The properties that set a version of an assembly or a package. Only <c>Directory.Build.props</c> sets them, from <c>Engine.props</c>.</summary>
    private static readonly string[] s_versionProperties = ["Version", "VersionPrefix", "VersionSuffix", "AssemblyVersion", "FileVersion", "InformationalVersion", "PackageVersion"];

    private static readonly string[] s_skippedFolders = ["Bin", "obj", "ThirdParty", ".git", ".claude", ".vs", ".idea", "node_modules"];

    private static readonly string[] s_msbuildExtensions = [".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".proj"];

    /// <summary>Runs <c>version</c> or <c>version check</c>; the arguments are what follows <c>version</c>.</summary>
    /// <returns>0 fine, 1 a problem or a wrong command line.</returns>
    public static int Run(string root, string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                Console.WriteLine(EngineProps.Load(root).Text);
                return 0;
            }

            if (args[0] != "check" || !(args.Length == 1 || (args.Length == 3 && args[1] == "--tag" && args[2].Length > 0)))
            {
                Console.Error.WriteLine("Usage: ./build.sh version [check [--tag <name>]]");
                return 1;
            }

            string? tag = args.Length == 3 ? args[2] : null;

            IReadOnlyList<string> problems = Check(root, tag);
            foreach (string problem in problems)
            {
                Console.Error.WriteLine("version: " + problem);
            }

            if (problems.Count == 0)
            {
                Console.WriteLine($"version: {EngineProps.Load(root).Text}: fine" + (tag is null ? "" : $" (tag {tag})"));
            }

            return problems.Count == 0 ? 0 : 1;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("version: " + e.Message);
            return 1;
        }
    }

    /// <summary>Everything that is wrong with the way the repository at <paramref name="root"/> keeps the engine's version; empty when all is well.</summary>
    /// <param name="root">The root of the repository (where <c>Engine.props</c> is).</param>
    /// <param name="tag">The git tag the build is of (<c>v0.2.0</c> or <c>refs/tags/v0.2.0</c>), or null when it is not a tag build: the tag must be <c>v</c> and the version.</param>
    public static IReadOnlyList<string> Check(string root, string? tag = null)
    {
        var problems = new List<string>();
        string file = Path.Combine(root, EngineProps.FileName);
        if (!File.Exists(file))
        {
            problems.Add($"{EngineProps.FileName} is missing.");
            return problems;
        }

        if (!EngineProps.TryParse(File.ReadAllText(file), out EngineProps? props, out string? problem))
        {
            problems.Add($"{EngineProps.FileName}: {problem}");
            return problems;
        }

        if (tag is not null)
        {
            string name = tag.StartsWith("refs/tags/", StringComparison.Ordinal) ? tag["refs/tags/".Length..] : tag;
            if (name != "v" + props!.Text)
            {
                problems.Add($"the tag '{name}' is not 'v{props.Text}', the version of {EngineProps.FileName}: bump it in the commit that is tagged (Docs/contributing/releases.md).");
            }
        }

        RequireMention(root, "Directory.Build.props", problems);
        RequireMention(root, Path.Combine("Native", "CMakeLists.txt"), problems);
        ScanForVersions(root, root, problems);
        return problems;
    }

    /// <summary>The build file must read <c>Engine.props</c>: without that the version is written somewhere else.</summary>
    private static void RequireMention(string root, string relative, List<string> problems)
    {
        string path = Path.Combine(root, relative);
        if (!File.Exists(path))
        {
            problems.Add($"{relative.Replace('\\', '/')} is missing.");
        }
        else if (!File.ReadAllText(path).Contains(EngineProps.FileName, StringComparison.Ordinal))
        {
            problems.Add($"{relative.Replace('\\', '/')} does not read {EngineProps.FileName}.");
        }
    }

    private static void ScanForVersions(string root, string folder, List<string> problems)
    {
        foreach (string file in Directory.EnumerateFiles(folder))
        {
            string extension = Path.GetExtension(file);
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (s_msbuildExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                if (relative != "Directory.Build.props" && relative != EngineProps.FileName)
                {
                    CheckProject(file, relative, problems);
                }
            }
            else if (extension == ".cs" && AssemblyVersionAttribute().IsMatch(File.ReadAllText(file)))
            {
                problems.Add($"{relative}: sets an assembly version in code. The versions come from {EngineProps.FileName}.");
            }
        }

        foreach (string sub in Directory.EnumerateDirectories(folder))
        {
            if (!s_skippedFolders.Contains(Path.GetFileName(sub), StringComparer.Ordinal))
            {
                ScanForVersions(root, sub, problems);
            }
        }
    }

    private static void CheckProject(string file, string relative, List<string> problems)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(file, LoadOptions.SetLineInfo);
        }
        catch (System.Xml.XmlException)
        {
            return; // not ours to judge: the build reports a project that is not XML
        }

        foreach (XElement property in document.Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup" && s_versionProperties.Contains(e.Name.LocalName, StringComparer.Ordinal)))
        {
            int line = ((System.Xml.IXmlLineInfo)property).LineNumber;
            problems.Add($"{relative}:{line}: sets <{property.Name.LocalName}>. The versions come from {EngineProps.FileName} (through Directory.Build.props).");
        }
    }

    [GeneratedRegex(@"\[\s*assembly\s*:\s*(System\.Reflection\.)?Assembly(File|Informational)?Version(Attribute)?\s*\(")]
    private static partial Regex AssemblyVersionAttribute();
}
