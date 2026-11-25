using System;
using System.Globalization;
using System.Linq;
using Weft.Content.Xml;
using Weft.Core.Assets;

namespace Weft.Content.Assets;

/// <summary>
/// An authored document (<see cref="AssetFormKind.Authored"/>: a scene, a prefab) as read from a file of its format: the tree, the version it declares and the UID in its root. This is the one
/// place that reads the bytes of such a file (text or cooked, as <see cref="XmlFile"/> does) and checks what every authored format has in common: the root element is the format's
/// (<see cref="AssetFormat.RootElement"/>), the version is a number the engine reads (<see cref="AssetFormat.Version"/>), and, when a schema is given, the document matches it. What is particular to
/// a format (a prefab has exactly one root entity) stays with the code that owns the format.
/// </summary>
/// <param name="Root">The root element.</param>
/// <param name="Version">The <c>version</c> attribute, or 0 when the document has none (a missing version is accepted).</param>
/// <param name="Uid">The <c>uid</c> attribute of the root, or <see cref="AssetUid.None"/> when there is none or it is not a valid UID.</param>
public sealed record AuthoredDocument(XmlNode Root, int Version, AssetUid Uid)
{
    /// <summary>Reads the document in <paramref name="bytes"/>, text (with or without a byte order mark) or cooked, and checks it as <see cref="Check"/> does.</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="format">The authored format the file must be.</param>
    /// <param name="origin">The path to name in errors.</param>
    /// <param name="schema">When given, the document must also match it (errors only: warnings are ignored).</param>
    /// <exception cref="XmlParseException">The bytes are text and not well-formed XML.</exception>
    /// <exception cref="ContentException">The bytes are cooked and damaged, or cooked by a newer engine.</exception>
    /// <exception cref="FormatException">The document is not of the format: another root, a version that is not a number or is newer than the engine reads, or it does not match the schema.</exception>
    public static AuthoredDocument Read(ReadOnlySpan<byte> bytes, AssetFormat format, string origin, SchemaSet? schema = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        return Check(XmlFile.Read(bytes, origin), format, origin, schema);
    }

    /// <summary>
    /// Checks a document that was read already: the root, the version and, when <paramref name="schema"/> is given, the schema. <paramref name="origin"/> names the document in errors;
    /// with none they say "the document".
    /// </summary>
    /// <exception cref="FormatException">The document is not of the format.</exception>
    public static AuthoredDocument Check(XmlNode root, AssetFormat format, string? origin = null, SchemaSet? schema = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(format);
        if (format.RootElement is null)
        {
            throw new ArgumentException($"{format.Label} is not an XML format.", nameof(format));
        }

        string label = format.Label.ToLowerInvariant();
        if (root.Name != format.RootElement)
        {
            throw new FormatException($"{origin ?? "the document"} is not a {label}: its root is <{root.Name}>, not <{format.RootElement}>");
        }

        string named = origin is null ? "" : " " + origin;
        int version = 0;
        if (root.Attribute("version") is { } versionText)
        {
            if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
            {
                throw new FormatException($"the {label}{named} has the version '{versionText}', which is not a number");
            }

            if (version > format.Version)
            {
                throw new FormatException($"the {label}{named} is version {version}; this engine reads up to {format.Version}");
            }
        }

        if (schema is not null)
        {
            DecodeResult result = schema.Decode(root, origin);
            string[] errors = [.. result.Problems.Where(p => p.Severity == ProblemSeverity.Error).Select(p => p.ToString())];
            if (errors.Length > 0)
            {
                throw new FormatException($"the {label}{named} does not match its schema:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
            }
        }

        return new AuthoredDocument(root, version, AssetUid.TryParse(root.Attribute("uid") ?? "", out AssetUid uid) ? uid : AssetUid.None);
    }
}
