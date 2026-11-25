using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Weft.Build;

/// <summary>
/// The values of <c>Engine.props</c> at the root of the repository: the one place the engine's version is written. The build reads the same file with MSBuild (<c>Directory.Build.props</c>) and with CMake
/// (<c>Native/CMakeLists.txt</c>); this is the reader for the tools (the docs build, <c>./build.sh version check</c>).
/// </summary>
/// <param name="Major">The major version.</param>
/// <param name="Minor">The minor version.</param>
/// <param name="Patch">The patch version.</param>
/// <param name="Status"><c>alpha</c>, <c>beta</c>, <c>rc&lt;N&gt;</c> or <c>stable</c>.</param>
/// <param name="Codename">The name of the release, or an empty text.</param>
/// <param name="CopyrightYear">The year in the copyright notice.</param>
/// <param name="ApiVersion">The level of the mod API.</param>
public sealed partial record EngineProps(int Major, int Minor, int Patch, string Status, string Codename, int CopyrightYear, int ApiVersion)
{
    /// <summary>The name of the file, at the root of the repository.</summary>
    public const string FileName = "Engine.props";

    /// <summary><c>0.2.0</c>: the numbers.</summary>
    public string Core => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>The version as text: <c>0.2.0-alpha</c>, and just <c>1.0.0</c> for a stable release. The git tag of the release is <c>v</c> and this.</summary>
    public string Text => Status == "stable" ? Core : Core + "-" + Status;

    /// <summary>Reads <c>Engine.props</c> at <paramref name="root"/>.</summary>
    /// <exception cref="InvalidDataException">The file is missing or wrong; the message says what is.</exception>
    public static EngineProps Load(string root)
    {
        string file = Path.Combine(root, FileName);
        if (!File.Exists(file))
        {
            throw new InvalidDataException($"{FileName} is missing from {root}.");
        }

        return TryParse(File.ReadAllText(file), out EngineProps? props, out string? problem) ? props! : throw new InvalidDataException($"{FileName}: {problem}");
    }

    /// <summary>Reads the text of an <c>Engine.props</c>: an MSBuild <c>Project</c> with a <c>PropertyGroup</c> that has the seven values. <paramref name="problem"/> says what is wrong when it returns false.</summary>
    public static bool TryParse(string text, out EngineProps? props, out string? problem)
    {
        props = null;
        problem = null;
        XDocument document;
        try
        {
            document = XDocument.Parse(text);
        }
        catch (System.Xml.XmlException e)
        {
            problem = "it is not well-formed XML: " + e.Message;
            return false;
        }

        if (document.Root is not { Name.LocalName: "Project" } project)
        {
            problem = "the root element is not <Project>.";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement property in project.Elements().Where(e => e.Name.LocalName == "PropertyGroup").SelectMany(g => g.Elements()))
        {
            values[property.Name.LocalName] = property.Value.Trim();
        }

        string[] names = ["WeftVersionMajor", "WeftVersionMinor", "WeftVersionPatch", "WeftVersionStatus", "WeftVersionCodename", "WeftCopyrightYear", "WeftApiVersion"];
        string[] missing = [.. names.Where(n => !values.ContainsKey(n))];
        if (missing.Length > 0)
        {
            problem = "missing " + string.Join(", ", missing.Select(n => "<" + n + ">")) + ".";
            return false;
        }

        if (!TryNumber(values["WeftVersionMajor"], 65535, out int major) || !TryNumber(values["WeftVersionMinor"], 65535, out int minor) || !TryNumber(values["WeftVersionPatch"], 65535, out int patch))
        {
            problem = "the version parts (WeftVersionMajor, WeftVersionMinor, WeftVersionPatch) must be numbers from 0 to 65535 (a Windows version has 16 bits per part).";
            return false;
        }

        string status = values["WeftVersionStatus"];
        if (!StatusPattern().IsMatch(status))
        {
            problem = $"WeftVersionStatus is '{status}', not alpha, beta, rc<N> (rc1, rc2, ...) or stable.";
            return false;
        }

        string codename = values["WeftVersionCodename"];
        if (!CodenamePattern().IsMatch(codename))
        {
            problem = "WeftVersionCodename may hold letters, digits, spaces, dots and dashes only.";
            return false;
        }

        if (!TryNumber(values["WeftCopyrightYear"], 9999, out int year) || year < 2000)
        {
            problem = "WeftCopyrightYear must be a year (2000 to 9999).";
            return false;
        }

        if (!TryNumber(values["WeftApiVersion"], 65535, out int api) || api < 1)
        {
            problem = "WeftApiVersion must be a number, 1 or more.";
            return false;
        }

        props = new EngineProps(major, minor, patch, status, codename, year, api);
        return true;
    }

    private static bool TryNumber(string text, int max, out int value)
    {
        value = 0;
        return text.Length is > 0 and <= 5 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= max;
    }

    [GeneratedRegex("^(alpha|beta|rc[0-9]+|stable)$")]
    private static partial Regex StatusPattern();

    [GeneratedRegex(@"^[A-Za-z0-9 .\-]*$")]
    private static partial Regex CodenamePattern();
}
