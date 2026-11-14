using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;
using XmlNode = Apogee.Content.Xml.XmlNode;

namespace Apogee.Content.Packages;

/// <summary>The window a game opens with.</summary>
/// <param name="Width">The width in pixels of the client area.</param>
/// <param name="Height">The height in pixels of the client area.</param>
/// <param name="Fullscreen">Whether the window starts fullscreen.</param>
/// <param name="Resizable">Whether the player can resize the window.</param>
public readonly record struct GameWindow(int Width, int Height, bool Fullscreen, bool Resizable)
{
    /// <summary>1280 x 720, windowed, resizable.</summary>
    public static GameWindow Default { get; } = new(1280, 720, false, true);
}

/// <summary>
/// The <c>game.xml</c> at the root of a game folder: the game's name, who made it, its version, the folder of the player's files, its Steam app id, and the window it opens with. Checked against
/// the engine's schema (<c>Packages/game.xsd</c>) and further: the version is major.minor.patch, and the paths it names (the entry scene and the icon) are relative and stay inside the game.
/// A game without one still runs from a folder (it has no version and its user folder is named after the folder), but cannot be exported.
/// </summary>
public sealed class GameManifest
{
    /// <summary>The name of the file, at the root of the game folder.</summary>
    public const string FileName = "game.xml";

    private static readonly Lazy<SchemaSet> s_schema = new(LoadSchema);

    private GameManifest(string name, string organization, PackageVersion version)
    {
        Name = name;
        Organization = organization;
        Version = version;
        Executable = DefaultExecutable(name);
        Identifier = DefaultIdentifier(organization, Executable);
        UserFolder = name;
    }

    /// <summary>The name to show: the window title, the bundle name, the product name of an executable.</summary>
    public string Name { get; }

    /// <summary>Who made the game: the folder that holds the player's files of every game of one maker, and the company of an executable.</summary>
    public string Organization { get; }

    /// <summary>The version of the game: what the <c>gameVersion</c> of each package is checked against.</summary>
    public PackageVersion Version { get; }

    /// <summary>The name of the executable of an export, without its extension. Letters and digits of <see cref="Name"/> when the manifest does not say.</summary>
    public string Executable { get; private set; }

    /// <summary>The reverse-DNS name of the game (<c>com.studio.game</c>): the bundle identifier on macOS, the application id on Linux.</summary>
    public string Identifier { get; private set; }

    /// <summary>The name of the folder of the player's files, under the organization's. <see cref="Name"/> when the manifest does not say.</summary>
    public string UserFolder { get; private set; }

    /// <summary>A short description of the game.</summary>
    public string Description { get; private set; } = "";

    /// <summary>The copyright notice of the game.</summary>
    public string Copyright { get; private set; } = "";

    /// <summary>The game's Steam app id, 0 for a game that is not on Steam.</summary>
    public uint SteamAppId { get; private set; }

    /// <summary>The scene that is loaded before the packages' scripts run, as a path inside the game (<c>scenes/main.ascene</c>), or null.</summary>
    public string? EntryScene { get; private set; }

    /// <summary>A square PNG as a path inside the game folder, or null.</summary>
    public string? Icon { get; private set; }

    /// <summary>The window the game opens with.</summary>
    public GameWindow Window { get; private set; } = GameWindow.Default;

    private static SchemaSet LoadSchema()
    {
        using Stream stream = typeof(GameManifest).Assembly.GetManifestResourceStream("Packages/game.xsd") ?? throw new InvalidOperationException("The embedded schema of game.xml is missing.");
        using var reader = new StreamReader(stream);
        var set = new SchemaSet();
        set.LoadText(reader.ReadToEnd(), "game.xsd");
        return set;
    }

    /// <summary>Reads a manifest. <paramref name="problems"/> lists everything wrong, as <c>file:line: message</c>; the manifest is null when anything is.</summary>
    /// <param name="text">The text of the file.</param>
    /// <param name="origin">The file it came from, for the problems.</param>
    public static bool TryParse(string text, string origin, out GameManifest? manifest, out IReadOnlyList<string> problems)
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

        if (node.Name != "Game")
        {
            errors.Add($"{origin}:{node.Line}: the root element is <{node.Name}>, not <Game>.");
            return false;
        }

        DecodeResult result = s_schema.Value.Decode(node, origin);
        errors.AddRange(result.Problems.Where(p => p.Severity == ProblemSeverity.Error).Select(p => p.ToString())); // file:line: Game.version: what is wrong
        if (errors.Count > 0 || result.Value is not DefRecord record)
        {
            return false;
        }

        string name = Text(record, "name");
        string organization = Text(record, "organization");
        if (name.Trim().Length == 0)
        {
            errors.Add($"{origin}:{node.Line}: the name of the game is empty.");
        }

        if (organization.Trim().Length == 0)
        {
            errors.Add($"{origin}:{node.Line}: the organization is empty.");
        }

        if (!PackageVersion.TryParse(Text(record, "version"), out PackageVersion version))
        {
            errors.Add($"{origin}:{node.Line}: the version '{Text(record, "version")}' is not major.minor.patch.");
        }

        if (errors.Count > 0)
        {
            return false;
        }

        var built = new GameManifest(name, organization, version)
        {
            Description = Text(record, "description"),
            Copyright = Text(record, "copyright"),
            SteamAppId = (uint)((record["steamAppId"] as DefNumber)?.Value ?? 0),
        };
        if (Text(record, "executable") is { Length: > 0 } executable)
        {
            built.Executable = executable;
            built.Identifier = DefaultIdentifier(organization, executable);
        }

        if (Text(record, "identifier") is { Length: > 0 } identifier)
        {
            built.Identifier = identifier;
        }

        if (Text(record, "userFolder") is { Length: > 0 } userFolder)
        {
            built.UserFolder = userFolder;
        }

        built.EntryScene = RelativePath(record, node, "EntryScene", origin, errors);
        built.Icon = RelativePath(record, node, "Icon", origin, errors);
        if (built.Icon is { } icon && !icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{origin}:{LineOf(node, "Icon")}: the icon '{icon}' is not a .png file.");
        }

        if (record["Window"] is DefRecord window)
        {
            built.Window = new GameWindow(
                (int)((window["width"] as DefNumber)?.Value ?? GameWindow.Default.Width),
                (int)((window["height"] as DefNumber)?.Value ?? GameWindow.Default.Height),
                (window["fullscreen"] as DefBool)?.Value ?? GameWindow.Default.Fullscreen,
                (window["resizable"] as DefBool)?.Value ?? GameWindow.Default.Resizable);
        }

        if (errors.Count > 0)
        {
            return false;
        }

        manifest = built;
        return true;
    }

    /// <summary>
    /// Reads the <c>game.xml</c> in <paramref name="folder"/>. Returns null, with no problems, when the folder has none (a game does not need one to run from a folder). Returns null with
    /// <paramref name="problems"/> when the file is wrong.
    /// </summary>
    /// <exception cref="IOException">The file exists and cannot be read.</exception>
    public static GameManifest? Load(string folder, out IReadOnlyList<string> problems)
    {
        string file = System.IO.Path.Combine(folder, FileName);
        if (!File.Exists(file))
        {
            problems = [];
            return null;
        }

        TryParse(File.ReadAllText(file, Encoding.UTF8), file.Replace('\\', '/'), out GameManifest? manifest, out problems);
        return manifest;
    }

    /// <summary>The manifest as XML, with every value written out (the defaults too), so that reading it again gives the same manifest.</summary>
    public string ToXml()
    {
        var text = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, NewLineChars = "\n", NewLineHandling = NewLineHandling.Replace };
        using (XmlWriter xml = XmlWriter.Create(text, settings))
        {
            xml.WriteStartElement("Game");
            xml.WriteAttributeString("name", Name);
            xml.WriteAttributeString("organization", Organization);
            xml.WriteAttributeString("version", Version.ToString());
            xml.WriteAttributeString("executable", Executable);
            xml.WriteAttributeString("identifier", Identifier);
            xml.WriteAttributeString("userFolder", UserFolder);
            if (SteamAppId != 0)
            {
                xml.WriteAttributeString("steamAppId", SteamAppId.ToString(CultureInfo.InvariantCulture));
            }

            if (Description.Length > 0)
            {
                xml.WriteAttributeString("description", Description);
            }

            if (Copyright.Length > 0)
            {
                xml.WriteAttributeString("copyright", Copyright);
            }

            if (EntryScene is not null)
            {
                xml.WriteStartElement("EntryScene");
                xml.WriteAttributeString("path", EntryScene);
                xml.WriteEndElement();
            }

            if (Icon is not null)
            {
                xml.WriteStartElement("Icon");
                xml.WriteAttributeString("path", Icon);
                xml.WriteEndElement();
            }

            xml.WriteStartElement("Window");
            xml.WriteAttributeString("width", Window.Width.ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("height", Window.Height.ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("fullscreen", Window.Fullscreen ? "true" : "false");
            xml.WriteAttributeString("resizable", Window.Resizable ? "true" : "false");
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        return text.Append('\n').ToString();
    }

    /// <summary>
    /// The folder of the player's files: <c>&lt;local application data&gt;/&lt;organization&gt;/&lt;user folder&gt;</c>, each made a valid folder name by <see cref="SanitizeFileName"/>
    /// (<c>~/.local/share</c> on Linux, <c>AppData/Local</c> on Windows, <c>~/Library/Application Support</c> on macOS).
    /// </summary>
    /// <param name="localAppData">The folder that stands for the local application data; the platform's by default.</param>
    public string UserDirectory(string? localAppData = null) =>
        System.IO.Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SanitizeFileName(Organization), SanitizeFileName(UserFolder));

    /// <summary>
    /// Makes <paramref name="name"/> a name a folder can have on every platform: <c>&lt;&gt;:"/\|?*</c> and control characters are dropped, and so are trailing dots and spaces. What is left empty
    /// (or nothing but dots and spaces, which would name the folder itself or its parent) becomes <c>Game</c>.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (!char.IsControl(c) && "<>:\"/\\|?*".IndexOf(c, StringComparison.Ordinal) < 0)
            {
                text.Append(c);
            }
        }

        string clean = text.ToString().TrimEnd('.', ' ');
        return clean.Length == 0 ? "Game" : clean;
    }

    /// <summary>The letters and digits of the name (ASCII), at most 64; <c>Game</c> when there are none.</summary>
    private static string DefaultExecutable(string name)
    {
        string letters = new([.. name.Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')).Take(64)]);
        return letters.Length == 0 ? "Game" : letters;
    }

    /// <summary><c>com.&lt;organization&gt;.&lt;executable&gt;</c>, lowercase, each part with letters, digits and dashes only.</summary>
    private static string DefaultIdentifier(string organization, string executable) => $"com.{Segment(organization, "studio")}.{Segment(executable, "game")}";

    private static string Segment(string text, string fallback)
    {
        string clean = new([.. text.ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')]);
        return clean.Length == 0 ? fallback : clean;
    }

    private static string Text(DefRecord record, string name) => record[name] is DefText text ? text.Value : "";

    private static int LineOf(XmlNode node, string child) => node.Children.Find(c => c.Name == child)?.Line ?? node.Line;

    /// <summary>The <c>path</c> of an optional child element, checked to be a plain relative path inside the game.</summary>
    private static string? RelativePath(DefRecord record, XmlNode node, string element, string origin, List<string> errors)
    {
        if (record[element] is not DefRecord child)
        {
            return null;
        }

        string path = Text(child, "path");
        if (path.Length == 0 || path.StartsWith('/') || path.EndsWith('/') || path.Contains("://", StringComparison.Ordinal) || !VfsPath.TryParse("res://" + path, out _, out _))
        {
            errors.Add($"{origin}:{LineOf(node, element)}: the path '{path}' of the <{element}> must be a plain relative path inside the game (no '..', no scheme, no drive).");
            return null;
        }

        return path;
    }
}
