using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Apogee.Core.Assets;
using Apogee.Content.Xml;

namespace Apogee.Content.Assets;

/// <summary>
/// Reads and edits the start tag of a document's root element <em>as text</em>. A UID goes into a file people also edit by hand (a <c>.import.xml</c>, a scene), so it is added,
/// replaced or taken out by changing only the characters of one attribute: comments, indentation, quote style, line endings and the byte order mark all stay as they were. Going through
/// <see cref="XmlNode"/> and writing the document again would lose the comments and reformat the file.
/// <para>
/// The tag is found with two small patterns: what may come before the root (a byte order mark, spaces, the XML declaration, comments) and then <c>&lt;name</c>; then one attribute at a time
/// (<c>name="value"</c> or <c>name='value'</c>) until <c>&gt;</c> or <c>/&gt;</c>. If the text does not look like that, <see cref="TryFind"/> says so and nothing is edited.
/// </para>
/// </summary>
internal static partial class RootTag
{
    private const int RegexTimeoutMilliseconds = 1000;

    /// <summary>One attribute of the root tag: where it is in the text.</summary>
    /// <param name="Name">The attribute's name.</param>
    /// <param name="Start">Where the white space before the name starts, so that removing <c>[Start, End)</c> leaves the tag as it was without the attribute.</param>
    /// <param name="End">Just after the closing quote.</param>
    /// <param name="ValueStart">The first character of the value, after the opening quote.</param>
    /// <param name="ValueEnd">Just after the last character of the value, at the closing quote.</param>
    public readonly record struct Attribute(string Name, int Start, int End, int ValueStart, int ValueEnd);

    /// <summary>The root start tag.</summary>
    /// <param name="Name">The element's name.</param>
    /// <param name="Attributes">Its attributes in the order they are written.</param>
    /// <param name="InsertAt">Where a new attribute goes: after the last one, or after the name when there is none (before any white space that precedes <c>&gt;</c> or <c>/&gt;</c>, so the tag keeps its look).</param>
    public sealed record Tag(string Name, IReadOnlyList<Attribute> Attributes, int InsertAt)
    {
        /// <summary>The attribute called <paramref name="name"/>, if the tag has it.</summary>
        public Attribute? Find(string name)
        {
            foreach (Attribute attribute in Attributes)
            {
                if (attribute.Name == name)
                {
                    return attribute;
                }
            }

            return null;
        }
    }

    // A byte order mark, then any mix of spaces, processing instructions (the XML declaration) and comments, then the root's "<name". (\s* inside a group that repeats would back
    // up exponentially on text that does not match, so each white-space character is its own alternative.)
    [GeneratedRegex(@"\A\uFEFF?(?:\s|<\?.*?\?>|<!--.*?-->)*<(?<name>[^\s/>!?<]+)", RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex RootStart();

    // \G: the match has to start exactly where the previous one stopped, so attributes are read one after the other and nothing later in the file can be mistaken for one.
    [GeneratedRegex(@"\G(?<space>\s+)(?<name>[^\s=/>]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex AttributeAt();

    [GeneratedRegex(@"\G\s*/?>", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex TagEndAt();

    /// <summary>Finds the root start tag of <paramref name="text"/>; false when the text does not start with a well-shaped one (not XML, JSON, a damaged tag).</summary>
    public static bool TryFind(string text, [NotNullWhen(true)] out Tag? tag)
    {
        tag = null;
        try
        {
            Match start = RootStart().Match(text);
            if (!start.Success)
            {
                return false;
            }

            var attributes = new List<Attribute>();
            int position = start.Index + start.Length;
            int insertAt = position;
            while (true)
            {
                Match match = AttributeAt().Match(text, position);
                if (!match.Success)
                {
                    break;
                }

                Group value = match.Groups["value"];
                attributes.Add(new Attribute(match.Groups["name"].Value, match.Index, match.Index + match.Length, value.Index, value.Index + value.Length));
                position = match.Index + match.Length;
                insertAt = position;
            }

            if (!TagEndAt().IsMatch(text, position))
            {
                return false;
            }

            tag = new Tag(start.Groups["name"].Value, attributes, insertAt);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The text with the root's <paramref name="name"/> attribute set to <paramref name="value"/> (a value without characters that XML escapes): replaced in place, keeping its quotes, or added after the last attribute.</summary>
    public static string Set(string text, Tag tag, string name, string value) =>
        tag.Find(name) is { } existing
            ? string.Concat(text.AsSpan(0, existing.ValueStart), value.AsSpan(), text.AsSpan(existing.ValueEnd))
            : string.Concat(text.AsSpan(0, tag.InsertAt), $" {name}=\"{value}\"", text.AsSpan(tag.InsertAt));

    /// <summary>The text without the root's <paramref name="name"/> attribute (unchanged when there is none), and the white space before it.</summary>
    public static string Remove(string text, Tag tag, string name) =>
        tag.Find(name) is { } existing ? string.Concat(text.AsSpan(0, existing.Start), text.AsSpan(existing.End)) : text;

    /// <summary>
    /// The root's <c>uid</c> as text, <see cref="AssetUid.None"/> when there is none or it is not a valid UID. The element must be called <paramref name="rootName"/> when that is given.
    /// </summary>
    public static AssetUid ReadUid(string text, string? rootName = null)
    {
        if (!TryFind(text, out Tag? tag) || (rootName is not null && tag.Name != rootName) || tag.Find("uid") is not { } uid)
        {
            return AssetUid.None;
        }

        return AssetUid.TryParse(text.AsSpan(uid.ValueStart, uid.ValueEnd - uid.ValueStart), out AssetUid value) ? value : AssetUid.None;
    }

    /// <summary>
    /// Sets the root's <c>uid</c> (see <see cref="Set"/>) and checks the result: it has to parse as XML and its root has to say that UID, so that a surprise in the text (a tag that
    /// looked right and was not) is an error instead of a damaged file.
    /// </summary>
    /// <exception cref="ContentException">The text has no root start tag this class can edit, or the edited text is not the document it should be.</exception>
    public static string SetUid(string text, AssetUid uid, string origin)
    {
        if (!TryFind(text, out Tag? tag))
        {
            throw new ContentException($"{origin}: cannot add a uid: the file does not start with an XML element.");
        }

        string result = Set(text, tag, "uid", uid.ToString());
        try
        {
            // XmlReader does not take a byte order mark that is a character in a string, so the check reads the text without it.
            XmlNode root = XmlNode.Parse(result.TrimStart('\uFEFF'), origin);
            if (root.Attribute("uid") != uid.ToString())
            {
                throw new ContentException($"{origin}: adding the uid did not work.");
            }
        }
        catch (XmlParseException e)
        {
            throw new ContentException($"{origin}: cannot add a uid: {e.Message}", e);
        }

        return result;
    }
}
