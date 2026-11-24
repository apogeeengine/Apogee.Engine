using System;
using System.IO;
using System.Text;
using System.Xml;
using Weft.Content.Packages;

namespace Weft.Export;

/// <summary>
/// The <c>Info.plist</c> of an exported macOS bundle, in the XML property list format, which <c>plutil -lint</c> reads. The text of the game's <c>game.xml</c> goes through an XML writer, so a name with
/// <c>&amp;</c> or <c>&lt;</c> in it is escaped.
/// </summary>
internal static class InfoPlist
{
    /// <summary>The file name of the icon in the bundle's <c>Resources</c> folder, which <c>CFBundleIconFile</c> names.</summary>
    public const string IconFileName = "game.icns";

    /// <summary>The text of <c>Contents/PkgInfo</c>: the package type <c>APPL</c> and the creator code <c>????</c> (none), which old tools still look for.</summary>
    public const string PackageInfo = "APPL????";

    /// <summary>The text of the plist for <paramref name="manifest"/>.</summary>
    /// <param name="manifest">The game: its name, identifier, executable, version and copyright.</param>
    /// <param name="hasIcon">Whether the bundle has <see cref="IconFileName"/>.</param>
    /// <param name="minimumSystemVersion">The oldest macOS the bundle runs on, as <c>LSMinimumSystemVersion</c> (for example <c>11.0</c>).</param>
    public static string Build(GameManifest manifest, bool hasIcon, string minimumSystemVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(minimumSystemVersion);
        var text = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", OmitXmlDeclaration = true, NewLineChars = "\n", NewLineHandling = NewLineHandling.Replace, ConformanceLevel = ConformanceLevel.Document };
        using (XmlWriter xml = XmlWriter.Create(text, settings))
        {
            xml.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
            xml.WriteStartElement("plist");
            xml.WriteAttributeString("version", "1.0");
            xml.WriteStartElement("dict");
            Entry(xml, "CFBundleName", manifest.Name);
            Entry(xml, "CFBundleDisplayName", manifest.Name);
            Entry(xml, "CFBundleIdentifier", manifest.Identifier);
            Entry(xml, "CFBundleExecutable", manifest.Executable);
            Entry(xml, "CFBundleVersion", manifest.Version.ToString());
            Entry(xml, "CFBundleShortVersionString", manifest.Version.ToString());
            Entry(xml, "CFBundlePackageType", "APPL");
            if (hasIcon)
            {
                Entry(xml, "CFBundleIconFile", IconFileName);
            }

            Entry(xml, "CFBundleInfoDictionaryVersion", "6.0");
            Entry(xml, "LSMinimumSystemVersion", minimumSystemVersion);
            xml.WriteElementString("key", "NSHighResolutionCapable");
            xml.WriteStartElement("true");
            xml.WriteEndElement();
            Entry(xml, "LSApplicationCategoryType", "public.app-category.games");
            if (manifest.Copyright.Length > 0)
            {
                Entry(xml, "NSHumanReadableCopyright", manifest.Copyright);
            }

            xml.WriteEndElement(); // dict
            xml.WriteEndElement(); // plist
        }

        return text.Append('\n').ToString();
    }

    private static void Entry(XmlWriter xml, string key, string value)
    {
        xml.WriteElementString("key", key);
        xml.WriteElementString("string", value);
    }
}
