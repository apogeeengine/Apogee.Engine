using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Weft.Content.Xml;

namespace Weft.Content.Assets;

/// <summary>
/// Changes attribute values of an XML document <em>as text</em>, so that everything else in the file stays as people wrote it: comments, indentation, quotes, line endings, the byte order
/// mark, the order and spelling of every other attribute. Going through <see cref="XmlNode"/> and writing the document again would lose the comments and reformat the whole file, which is
/// not acceptable for a scene that is under version control.
/// <para>
/// How it stays safe: the document has already been read by <see cref="XmlNode.Parse"/> (so it is well-formed and has no DTD). <see cref="Scan"/> then walks the text once, linearly, and finds
/// the start tag of every element and the exact characters of every attribute value. The k-th attribute found in the text must be the k-th attribute of the parsed tree, with the same name,
/// and a value that is edited must decode to exactly what the tree says it is; if any of that is not true the edit is refused (<see cref="ContentException"/>) and the file is left alone.
/// After the edit <see cref="Replace"/> reads the new text again and checks that it is the old document with the chosen values changed and nothing else: the same elements, attributes and text.
/// </para>
/// </summary>
internal static class ReferenceText
{
    /// <summary>One attribute in the text: <c>Name="value"</c>, where <c>[ValueStart, ValueEnd)</c> are the characters between the quotes.</summary>
    internal readonly record struct Span(string Element, string Name, int ValueStart, int ValueEnd, char Quote);

    /// <summary>
    /// Every attribute of every start tag of <paramref name="text"/>, in the order they are written. Null when the text is not shaped as expected (a document type, an unfinished tag): it is
    /// then not edited. The characters that can hide a tag (comments, processing instructions, CDATA) are skipped, and a quoted value may hold <c>&gt;</c>.
    /// </summary>
    public static List<Span>? Scan(string text)
    {
        var spans = new List<Span>();
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('<', i);
            if (open < 0)
            {
                break;
            }

            int next = SkipMarkup(text, open, spans);
            if (next < 0)
            {
                return null;
            }

            i = next;
        }

        return spans;
    }

    // The index after the markup that starts at text[open], or -1 when it is damaged. A start tag adds its attributes to spans.
    private static int SkipMarkup(string text, int open, List<Span> spans)
    {
        if (text.AsSpan(open).StartsWith("<!--", StringComparison.Ordinal))
        {
            return After(text, "-->", open + 4);
        }

        if (text.AsSpan(open).StartsWith("<![CDATA[", StringComparison.Ordinal))
        {
            return After(text, "]]>", open + 9);
        }

        if (text.AsSpan(open).StartsWith("<?", StringComparison.Ordinal))
        {
            return After(text, "?>", open + 2);
        }

        if (open + 1 < text.Length && text[open + 1] == '!')
        {
            return -1; // <!DOCTYPE: the parser refuses it, and nothing here knows how to read it
        }

        if (open + 1 < text.Length && text[open + 1] == '/')
        {
            return After(text, ">", open + 2);
        }

        return ScanStartTag(text, open, spans);
    }

    private static int After(string text, string end, int from)
    {
        int index = text.IndexOf(end, from, StringComparison.Ordinal);
        return index < 0 ? -1 : index + end.Length;
    }

    private static int ScanStartTag(string text, int open, List<Span> spans)
    {
        int position = open + 1;
        int nameEnd = NameEnd(text, position);
        if (nameEnd == position)
        {
            return -1;
        }

        string element = text[position..nameEnd];
        position = nameEnd;
        while (true)
        {
            position = SkipSpace(text, position);
            if (position >= text.Length)
            {
                return -1;
            }

            if (text[position] == '>')
            {
                return position + 1;
            }

            if (text[position] == '/')
            {
                return position + 1 < text.Length && text[position + 1] == '>' ? position + 2 : -1;
            }

            position = ScanAttribute(text, element, position, spans);
            if (position < 0)
            {
                return -1;
            }
        }
    }

    // One name="value" at text[position]; the index after the closing quote, or -1.
    private static int ScanAttribute(string text, string element, int position, List<Span> spans)
    {
        int nameEnd = NameEnd(text, position);
        if (nameEnd == position)
        {
            return -1;
        }

        int equals = SkipSpace(text, nameEnd);
        if (equals >= text.Length || text[equals] != '=')
        {
            return -1;
        }

        int quoteAt = SkipSpace(text, equals + 1);
        if (quoteAt >= text.Length || text[quoteAt] is not ('"' or '\''))
        {
            return -1;
        }

        int close = text.IndexOf(text[quoteAt], quoteAt + 1);
        if (close < 0)
        {
            return -1;
        }

        spans.Add(new Span(element, text[position..nameEnd], quoteAt + 1, close, text[quoteAt]));
        return close + 1;
    }

    // A name runs up to white space, '=', '>' or '/'.
    private static int NameEnd(string text, int from)
    {
        int i = from;
        while (i < text.Length && !IsSpace(text[i]) && text[i] is not ('=' or '>' or '/'))
        {
            i++;
        }

        return i;
    }

    private static int SkipSpace(string text, int from)
    {
        int i = from;
        while (i < text.Length && IsSpace(text[i]))
        {
            i++;
        }

        return i;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n';

    // ---- Values -----------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What an attribute value written as <paramref name="raw"/> means: the five named entities and character references are replaced, and a white-space character written as itself becomes a
    /// space (a line break pair is one space), as XML says. Null for an entity this does not know, so that the edit is refused rather than guessed.
    /// </summary>
    public static string? Decode(string raw)
    {
        var value = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '&')
            {
                int semicolon = raw.IndexOf(';', i);
                string? entity = semicolon < 0 ? null : Entity(raw[(i + 1)..semicolon]);
                if (entity is null)
                {
                    return null;
                }

                value.Append(entity);
                i = semicolon;
            }
            else if (c == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n')
            {
                // "\r\n" is one line break, so one space; the loop's next step skips the "\n".
                value.Append(' ');
                i++;
            }
            else
            {
                value.Append(c is '\t' or '\r' or '\n' ? ' ' : c);
            }
        }

        return value.ToString();
    }

    private static string? Entity(string name)
    {
        switch (name)
        {
            case "lt":
                return "<";
            case "gt":
                return ">";
            case "amp":
                return "&";
            case "quot":
                return "\"";
            case "apos":
                return "'";
        }

        if (name.Length < 2 || name[0] != '#')
        {
            return null;
        }

        bool hex = name[1] is 'x';
        string digits = name[(hex ? 2 : 1)..];
        bool parsed = int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int code);
        return parsed && code is > 0 and <= 0x10FFFF && !(code is >= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : null;
    }

    /// <summary>The text of an attribute value as written between <paramref name="quote"/> marks: <c>&amp;</c>, <c>&lt;</c>, the quote itself and the white space that would be changed on reading are escaped.</summary>
    public static string Encode(string value, char quote)
    {
        var text = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            text.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '"' when quote == '"' => "&quot;",
                '\'' when quote == '\'' => "&apos;",
                '\t' => "&#9;",
                '\n' => "&#10;",
                '\r' => "&#13;",
                _ => c.ToString(),
            });
        }

        return text.ToString();
    }

    // ---- The edit ---------------------------------------------------------------------------------------------------------------------

    /// <summary>The attributes of a parsed document in document order (elements first, then their children), each with its element: the order <see cref="Scan"/> finds them in the text.</summary>
    public static List<(XmlNode Element, XmlProperty Attribute)> Attributes(XmlNode root)
    {
        var all = new List<(XmlNode, XmlProperty)>();
        Collect(root, all);
        return all;
    }

    private static void Collect(XmlNode element, List<(XmlNode, XmlProperty)> all)
    {
        foreach (XmlProperty attribute in element.Attributes)
        {
            all.Add((element, attribute));
        }

        foreach (XmlNode child in element.Children)
        {
            Collect(child, all);
        }
    }

    /// <summary>
    /// <paramref name="text"/> with the attributes numbered in <paramref name="newValues"/> (their place in <see cref="Attributes"/>) given those values, and every other character exactly as it was.
    /// <paramref name="tree"/> is the parse of <paramref name="text"/>.
    /// </summary>
    /// <exception cref="ContentException">The text cannot be edited safely: it does not line up with its parse, or the result is not the same document with only those values changed. Nothing is returned then.</exception>
    public static string Replace(string text, XmlNode tree, IReadOnlyDictionary<int, string> newValues, string origin)
    {
        List<(XmlNode Element, XmlProperty Attribute)> before = Attributes(tree);
        List<Span> spans = Scan(text) ?? throw new ContentException("the text could not be read to edit it in place.");
        CheckAlignment(text, before, spans, newValues, origin);

        var result = new StringBuilder(text.Length);
        int position = 0;
        foreach (int index in SortedKeys(newValues))
        {
            Span span = spans[index];
            result.Append(text, position, span.ValueStart - position).Append(Encode(newValues[index], span.Quote));
            position = span.ValueEnd;
        }

        string edited = result.Append(text, position, text.Length - position).ToString();
        CheckResult(edited, tree, before, newValues, origin);
        return edited;
    }

    private static List<int> SortedKeys(IReadOnlyDictionary<int, string> values)
    {
        List<int> keys = [.. values.Keys];
        keys.Sort();
        return keys;
    }

    // The k-th attribute of the text is the k-th attribute of the tree (same element, same name), and the ones to change are written as the tree says they are.
    private static void CheckAlignment(string text, List<(XmlNode Element, XmlProperty Attribute)> attributes, List<Span> spans, IReadOnlyDictionary<int, string> newValues, string origin)
    {
        if (spans.Count != attributes.Count)
        {
            throw new ContentException($"the text has {spans.Count} attributes and its parse {attributes.Count}, so it cannot be edited in place.");
        }

        for (int i = 0; i < spans.Count; i++)
        {
            if (spans[i].Name != attributes[i].Attribute.Name || spans[i].Element != attributes[i].Element.Name)
            {
                throw new ContentException($"the attribute {spans[i].Name} of <{spans[i].Element}> is not where the parse expects it, so the text cannot be edited in place.");
            }
        }

        foreach (int index in newValues.Keys)
        {
            if (index < 0 || index >= spans.Count)
            {
                throw new ContentException($"there is no attribute number {index}.");
            }

            if (Decode(text[spans[index].ValueStart..spans[index].ValueEnd]) != attributes[index].Attribute.Value)
            {
                throw new ContentException($"the attribute {spans[index].Name} of <{spans[index].Element}> is written in a way that cannot be edited in place.");
            }
        }
    }

    // Reads the edited text and compares it with the original document: same elements in the same order with the same text and attributes, the chosen values changed.
    private static void CheckResult(string edited, XmlNode tree, List<(XmlNode Element, XmlProperty Attribute)> before, IReadOnlyDictionary<int, string> newValues, string origin)
    {
        XmlNode after;
        try
        {
            after = XmlNode.Parse(edited.TrimStart('\uFEFF'), origin);
        }
        catch (XmlParseException e)
        {
            throw new ContentException($"the edited text is not valid XML ({e.Reason}); the file was not changed.", e);
        }

        List<(XmlNode Element, XmlProperty Attribute)> now = Attributes(after);
        bool same = now.Count == before.Count && SameShape(tree, after);
        for (int i = 0; same && i < now.Count; i++)
        {
            string expected = newValues.TryGetValue(i, out string? changed) ? changed : before[i].Attribute.Value;
            same = now[i].Attribute.Name == before[i].Attribute.Name && now[i].Attribute.Value == expected;
        }

        if (!same)
        {
            throw new ContentException("the edited text is not the original document with only the references changed; the file was not changed.");
        }
    }

    private static bool SameShape(XmlNode a, XmlNode b)
    {
        if (a.Name != b.Name || a.Text != b.Text || a.Children.Count != b.Children.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Children.Count; i++)
        {
            if (!SameShape(a.Children[i], b.Children[i]))
            {
                return false;
            }
        }

        return true;
    }
}
