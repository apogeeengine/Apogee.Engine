using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Weft.Content.Assets;

/// <summary>
/// Finds the elements directly inside a document's root <em>as text</em>, the way <see cref="RootTag"/> finds the root's attributes, so that one can be added, taken out or have an attribute changed
/// with every other character of the file staying as it was (comments, indentation, quotes, line endings, and elements this code knows nothing about). It is a scanner and not a parser: it skips comments,
/// CDATA sections and processing instructions, reads each start tag with its quoted attribute values, and counts start and end tags to find where an element ends. It is linear in the text and never
/// recurses. Whoever edits with it checks the result by parsing it, so a surprise in the text is an error and not a damaged file.
/// </summary>
internal static class RootChildren
{
    /// <summary>One element directly inside the root.</summary>
    /// <param name="Name">The element's name.</param>
    /// <param name="Start">Where its <c>&lt;</c> is.</param>
    /// <param name="End">Just after its end tag, or after its <c>/&gt;</c>.</param>
    /// <param name="Attributes">The attributes of its start tag, with where they are in the text.</param>
    public sealed record Child(string Name, int Start, int End, IReadOnlyList<RootTag.Attribute> Attributes)
    {
        /// <summary>The attribute called <paramref name="name"/>, if the start tag has it.</summary>
        public RootTag.Attribute? Find(string name)
        {
            foreach (RootTag.Attribute attribute in Attributes)
            {
                if (attribute.Name == name)
                {
                    return attribute;
                }
            }

            return null;
        }
    }

    /// <summary>The root start tag, the elements inside it in order, and where its end tag starts (-1 when the root has no content: <c>&lt;Import/&gt;</c>).</summary>
    public sealed record Layout(RootTag.Tag Tag, IReadOnlyList<Child> Children, int CloseStart);

    /// <summary>Reads the layout of <paramref name="text"/>; false when it is not a document this class can read (not XML, an unclosed element, a damaged tag).</summary>
    public static bool TryScan(string text, [NotNullWhen(true)] out Layout? layout)
    {
        layout = null;
        if (!RootTag.TryFind(text, out RootTag.Tag? tag))
        {
            return false;
        }

        if (tag.SelfClosing)
        {
            layout = new Layout(tag, [], -1);
            return true;
        }

        var children = new List<Child>();
        int position = tag.End;
        while (true)
        {
            int start = text.IndexOf('<', position);
            if (start < 0)
            {
                return false;
            }

            int skipped = SkipMarkup(text, start);
            if (skipped == -2)
            {
                return false;
            }

            if (skipped >= 0)
            {
                position = skipped;
                continue;
            }

            if (start + 1 < text.Length && text[start + 1] == '/')
            {
                int close = text.IndexOf('>', start);
                if (close < 0 || text.AsSpan(start + 2, close - start - 2).Trim().CompareTo(tag.Name, StringComparison.Ordinal) != 0)
                {
                    return false;
                }

                layout = new Layout(tag, children, start);
                return true;
            }

            if (!TryStartTag(text, start, out string name, out List<RootTag.Attribute>? attributes, out int end, out bool selfClosing) || (!selfClosing && !TrySkipContent(text, end, out end)))
            {
                return false;
            }

            children.Add(new Child(name, start, end, attributes));
            position = end;
        }
    }

    /// <summary>
    /// The text with <paramref name="element"/> (an element as text, <c>&lt;SubAsset name="a" uid="…"/&gt;</c>) added as the last element of the root, on a line of its own indented like the one before it when the
    /// root has its end tag on a line of its own, and a root that had no content (<c>&lt;Import a="b"/&gt;</c>) becomes one that has.
    /// </summary>
    public static string Append(string text, Layout layout, string element)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        RootTag.Tag tag = layout.Tag;
        if (layout.CloseStart < 0)
        {
            return $"{text[..tag.InsertAt]}>{newline}  {element}{newline}</{tag.Name}>{text[tag.End..]}";
        }

        int lineStart = layout.CloseStart == 0 ? 0 : text.LastIndexOf('\n', layout.CloseStart - 1) + 1;
        if (!IsBlank(text.AsSpan(lineStart, layout.CloseStart - lineStart)))
        {
            return string.Concat(text.AsSpan(0, layout.CloseStart), element, text.AsSpan(layout.CloseStart)); // <Import><A/></Import>: no layout to follow
        }

        string closeIndent = text[lineStart..layout.CloseStart];
        string indent = closeIndent + "  ";
        if (layout.Children.Count > 0)
        {
            Child last = layout.Children[^1];
            int childLine = last.Start == 0 ? 0 : text.LastIndexOf('\n', last.Start - 1) + 1;
            if (IsBlank(text.AsSpan(childLine, last.Start - childLine)))
            {
                indent = text[childLine..last.Start];
            }
        }

        return $"{text[..lineStart]}{indent}{element}{newline}{text[lineStart..]}";
    }

    /// <summary>The text with the value of <paramref name="attribute"/> replaced by <paramref name="value"/> (a value without characters XML escapes), quotes and everything else as they were.</summary>
    public static string SetValue(string text, RootTag.Attribute attribute, string value) =>
        string.Concat(text.AsSpan(0, attribute.ValueStart), value.AsSpan(), text.AsSpan(attribute.ValueEnd));

    /// <summary>The text without <paramref name="child"/> and the white space before it.</summary>
    public static string Remove(string text, Child child)
    {
        int start = child.Start;
        while (start > 0 && char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        return string.Concat(text.AsSpan(0, start), text.AsSpan(child.End));
    }

    private static bool IsBlank(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>If a comment, CDATA section or processing instruction starts at <paramref name="start"/>, the index after it; -1 when something else does; -2 when it is not closed.</summary>
    private static int SkipMarkup(string text, int start)
    {
        (string open, string close) = text.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal) ? ("<!--", "-->")
            : text.AsSpan(start).StartsWith("<![CDATA[", StringComparison.Ordinal) ? ("<![CDATA[", "]]>")
            : text.AsSpan(start).StartsWith("<?", StringComparison.Ordinal) ? ("<?", "?>")
            : ("", "");
        if (open.Length == 0)
        {
            return -1;
        }

        int end = text.IndexOf(close, start + open.Length, StringComparison.Ordinal);
        return end < 0 ? -2 : end + close.Length;
    }

    /// <summary>Reads the start tag at <paramref name="start"/> (a <c>&lt;</c>), with its attributes and where it ends.</summary>
    private static bool TryStartTag(string text, int start, out string name, [NotNullWhen(true)] out List<RootTag.Attribute>? attributes, out int end, out bool selfClosing)
    {
        name = "";
        attributes = null;
        end = 0;
        selfClosing = false;
        int i = start + 1;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('/' or '>'))
        {
            i++;
        }

        if (i == start + 1 || i >= text.Length)
        {
            return false;
        }

        name = text[(start + 1)..i];
        var found = new List<RootTag.Attribute>();
        while (true)
        {
            int spaceStart = i;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                return false;
            }

            if (text[i] == '>')
            {
                end = i + 1;
                attributes = found;
                return true;
            }

            if (text[i] == '/')
            {
                if (i + 1 >= text.Length || text[i + 1] != '>')
                {
                    return false;
                }

                end = i + 2;
                selfClosing = true;
                attributes = found;
                return true;
            }

            if (i == spaceStart)
            {
                return false; // an attribute follows white space
            }

            int nameStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('=' or '>' or '/'))
            {
                i++;
            }

            string attributeName = text[nameStart..i];
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length || text[i] != '=')
            {
                return false;
            }

            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length || text[i] is not ('"' or '\''))
            {
                return false;
            }

            int valueEnd = text.IndexOf(text[i], i + 1);
            if (valueEnd < 0)
            {
                return false;
            }

            found.Add(new RootTag.Attribute(attributeName, spaceStart, valueEnd + 1, i + 1, valueEnd));
            i = valueEnd + 1;
        }
    }

    /// <summary>Finds the end of an element whose start tag ended at <paramref name="from"/>: the index after its end tag.</summary>
    private static bool TrySkipContent(string text, int from, out int end)
    {
        end = 0;
        int depth = 1;
        int position = from;
        while (true)
        {
            int start = text.IndexOf('<', position);
            if (start < 0)
            {
                return false;
            }

            int skipped = SkipMarkup(text, start);
            if (skipped == -2)
            {
                return false;
            }

            if (skipped >= 0)
            {
                position = skipped;
                continue;
            }

            if (start + 1 < text.Length && text[start + 1] == '/')
            {
                int close = text.IndexOf('>', start);
                if (close < 0)
                {
                    return false;
                }

                position = close + 1;
                if (--depth == 0)
                {
                    end = position;
                    return true;
                }

                continue;
            }

            if (!TryStartTag(text, start, out _, out _, out int tagEnd, out bool selfClosing))
            {
                return false;
            }

            if (!selfClosing)
            {
                depth++;
            }

            position = tagEnd;
        }
    }
}
