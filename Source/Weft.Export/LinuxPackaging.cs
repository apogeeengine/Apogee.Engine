using System;
using System.Buffers;
using System.IO;
using System.Text;
using Weft.Content.Packages;

namespace Weft.Export;

/// <summary>
/// Linux: the executable is made executable, the icon is written as <c>icon.png</c> (the runtime draws the window's icon from the file named by the exported <c>game.xml</c>), and a desktop entry,
/// <c>&lt;identifier&gt;.desktop</c>, is written for whoever installs the game (a distribution package, a Flatpak, an AppImage). The entry is a template: nothing in the export reads it, and Steam does not use it.
/// </summary>
internal sealed class LinuxPackaging : IPlatformPackaging
{
    /// <summary>The name of the icon file, in the game's root.</summary>
    public const string IconFileName = "icon.png";

    /// <summary>The width and height of the icon file.</summary>
    public const int IconSize = 256;

    /// <summary>The characters that make an <c>Exec</c> argument need quotes.</summary>
    private static readonly SearchValues<char> s_execReserved = SearchValues.Create(" \t\n\"'\\><~|&;$*?#()`");

    public void Apply(PlatformPackagingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ExecutableBit.Set(context, "Linux");

        bool hasIcon = false;
        if (context.IconFile is { } file)
        {
            var icon = IconImages.Load(file);
            File.WriteAllBytes(Path.Combine(context.Layout.GameRoot, IconFileName), icon.Png(IconSize));
            context.Warnings.AddRange(icon.Warnings);
            context.ExportedIcon = IconFileName;
            context.Log($"Wrote {IconFileName} ({IconSize}x{IconSize})");
            hasIcon = true;
        }

        string desktop = context.Manifest.Identifier + ".desktop";
        File.WriteAllText(Path.Combine(context.Layout.Output, desktop), DesktopEntry(context.Manifest, hasIcon), new UTF8Encoding(false));
        context.Log($"Wrote {desktop}");
    }

    /// <summary>
    /// The text of the game's desktop entry (the Desktop Entry Specification): <c>Exec</c> is the executable's name, as it is found on the <c>PATH</c> once an installer has put the game there,
    /// <c>Icon</c> is the identifier, the name an icon theme file would have, and <c>StartupWMClass</c> is the identifier, which the runtime sets as the application id of its window.
    /// </summary>
    /// <param name="manifest">The game.</param>
    /// <param name="hasIcon">Whether the game has an icon: without one the entry has no <c>Icon</c>.</param>
    internal static string DesktopEntry(GameManifest manifest, bool hasIcon)
    {
        var text = new StringBuilder();
        text.Append("[Desktop Entry]\n");
        text.Append("Type=Application\n");
        text.Append("Version=1.0\n");
        text.Append("Name=").Append(EscapeValue(manifest.Name)).Append('\n');
        if (manifest.Description.Length > 0)
        {
            text.Append("Comment=").Append(EscapeValue(manifest.Description)).Append('\n');
        }

        text.Append("Exec=").Append(EscapeValue(QuoteExecArgument(manifest.Executable))).Append('\n');
        if (hasIcon)
        {
            text.Append("Icon=").Append(EscapeValue(manifest.Identifier)).Append('\n');
        }

        text.Append("Terminal=false\n");
        text.Append("Categories=Game;\n");
        text.Append("StartupWMClass=").Append(EscapeValue(manifest.Identifier)).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Escapes a value of type <c>string</c>: a backslash, a new line, a tab and a carriage return are written as <c>\\</c>, <c>\n</c>, <c>\t</c> and <c>\r</c>, and a leading space as <c>\s</c>
    /// (readers drop leading spaces otherwise). Other control characters are not allowed in a desktop file and become spaces. A semicolon is only special in the values that are lists, which these are not.
    /// </summary>
    internal static string EscapeValue(string value)
    {
        var text = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '\\':
                    text.Append("\\\\");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case ' ' when i == 0:
                    text.Append("\\s");
                    break;
                default:
                    text.Append(char.IsControl(c) ? ' ' : c);
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// One argument of an <c>Exec</c> line: <c>%</c> is doubled (it starts a field code), and an argument with a character the specification reserves is put in double quotes, with <c>"</c>, a backtick,
    /// <c>$</c> and a backslash escaped by a backslash. (The backslashes are doubled again by <see cref="EscapeValue"/>, the string level of the same rule.)
    /// </summary>
    internal static string QuoteExecArgument(string argument)
    {
        string text = argument.Replace("%", "%%", StringComparison.Ordinal);
        if (text.AsSpan().IndexOfAny(s_execReserved) < 0)
        {
            return text;
        }

        var quoted = new StringBuilder(text.Length + 2).Append('"');
        foreach (char c in text)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                quoted.Append('\\');
            }

            quoted.Append(c);
        }

        return quoted.Append('"').ToString();
    }
}
