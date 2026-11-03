using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace Apogee.Content.Xml;

/// <summary>A file is not well-formed XML. <see cref="Line"/> is where the reader gave up.</summary>
public sealed class XmlParseException : ContentException
{
    public XmlParseException(string origin, int line, string message)
        : base($"{origin}:{line}: {message}")
    {
        Origin = origin;
        Line = line;
        Reason = message;
    }

    /// <summary>What is wrong, without the file and line.</summary>
    public string Reason { get; }

    public string Origin { get; }

    public int Line { get; }
}

/// <summary>An attribute as written: its name (with a prefix, if it has one) and its text. (Not called XmlAttribute: a type with that ending is read as an attribute class.)</summary>
public readonly record struct XmlProperty(string Name, string Value);

/// <summary>
/// An element as content files and schemas are read: its name, its attributes in the order they were written, its children, its text, and the line it starts on. Comments and
/// processing instructions are dropped, a DTD is refused (nothing is loaded from outside the file), and names keep their prefix (<c>xs:element</c>, <c>xsi:type</c>): content is
/// not namespace-aware, so a prefix only has to be declared.
/// </summary>
public sealed class XmlNode
{
    public XmlNode(string name, int line = 0)
    {
        Name = name;
        Line = line;
    }

    /// <summary>The name as written, with its prefix.</summary>
    public string Name { get; }

    /// <summary>The name without its prefix.</summary>
    public string LocalName
    {
        get
        {
            int colon = Name.IndexOf(':', StringComparison.Ordinal);
            return colon < 0 ? Name : Name[(colon + 1)..];
        }
    }

    public int Line { get; set; }

    /// <summary>How deep elements may nest: far more than content needs, and little enough that the recursive code that walks a tree cannot run out of stack.</summary>
    public const int MaxDepth = 128;

    /// <summary>The file the element was read from (what <see cref="Parse"/> was given), or null for one made in code. Kept through <see cref="Clone"/>, so a problem in a merged definition can name the file it came from.</summary>
    public string? Origin { get; set; }

    public List<XmlProperty> Attributes { get; } = [];

    public List<XmlNode> Children { get; } = [];

    /// <summary>The text directly inside the element, trimmed (the pieces between children are joined with a space).</summary>
    public string Text { get; set; } = "";

    /// <summary>The value of the attribute, or null.</summary>
    public string? Attribute(string name)
    {
        foreach (XmlProperty attribute in Attributes)
        {
            if (attribute.Name == name)
            {
                return attribute.Value;
            }
        }

        return null;
    }

    public bool HasAttribute(string name) => Attribute(name) is not null;

    /// <summary>Sets the attribute, keeping its place if it is there.</summary>
    public void SetAttribute(string name, string value)
    {
        for (int i = 0; i < Attributes.Count; i++)
        {
            if (Attributes[i].Name == name)
            {
                Attributes[i] = new XmlProperty(name, value);
                return;
            }
        }

        Attributes.Add(new XmlProperty(name, value));
    }

    public bool RemoveAttribute(string name) => Attributes.RemoveAll(a => a.Name == name) > 0;

    /// <summary>The first child with this name (or local name), or null.</summary>
    public XmlNode? Child(string name) => Children.Find(c => c.Name == name || c.LocalName == name);

    /// <summary>A copy of the element and all that is in it.</summary>
    public XmlNode Clone()
    {
        var copy = new XmlNode(Name, Line) { Text = Text, Origin = Origin };
        copy.Attributes.AddRange(Attributes);
        foreach (XmlNode child in Children)
        {
            copy.Children.Add(child.Clone());
        }

        return copy;
    }

    // ---- Reading --------------------------------------------------------------------------------------------------------------------

    /// <summary>Reads the document in <paramref name="text"/>; <paramref name="origin"/> names it in errors (a path).</summary>
    /// <exception cref="XmlParseException">The text is not well-formed XML.</exception>
    public static XmlNode Parse(string text, string origin = "xml")
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = 64_000_000,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var info = (IXmlLineInfo)reader;
            var open = new Stack<XmlNode>();
            XmlNode? root = null;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var node = new XmlNode(reader.Name, info.LineNumber) { Origin = origin };
                        bool empty = reader.IsEmptyElement;
                        if (reader.MoveToFirstAttribute())
                        {
                            do
                            {
                                node.Attributes.Add(new XmlProperty(reader.Name, reader.Value));
                            }
                            while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }

                        if (open.Count > 0)
                        {
                            open.Peek().Children.Add(node);
                        }
                        else
                        {
                            root = node;
                        }

                        if (!empty)
                        {
                            open.Push(node);
                            if (open.Count > MaxDepth)
                            {
                                throw new XmlParseException(origin, info.LineNumber, $"the elements are nested more than {MaxDepth} deep.");
                            }
                        }

                        break;
                    case XmlNodeType.EndElement:
                        open.Pop();
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace:
                        XmlNode current = open.Peek();
                        current.Text = current.Text.Length == 0 ? reader.Value.Trim() : current.Text + " " + reader.Value.Trim();
                        break;
                }
            }

            return root ?? throw new XmlParseException(origin, 1, "the document has no root element.");
        }
        catch (XmlException e)
        {
            throw new XmlParseException(origin, e.LineNumber, e.Message);
        }
    }

    // ---- Writing --------------------------------------------------------------------------------------------------------------------

    /// <summary>The element as text: two spaces of indent, <c>\n</c> line ends, attributes in their order, empty elements as <c>&lt;a/&gt;</c>.</summary>
    public string ToXml()
    {
        var text = new StringBuilder();
        Write(text, 0);
        return text.ToString();
    }

    private void Write(StringBuilder text, int depth)
    {
        text.Append(' ', depth * 2).Append('<').Append(Name);
        foreach (XmlProperty attribute in Attributes)
        {
            text.Append(' ').Append(attribute.Name).Append("=\"").Append(Escape(attribute.Value, attribute: true)).Append('"');
        }

        if (Children.Count == 0 && Text.Length == 0)
        {
            text.Append("/>\n");
            return;
        }

        if (Children.Count == 0)
        {
            text.Append('>').Append(Escape(Text, attribute: false)).Append("</").Append(Name).Append(">\n");
            return;
        }

        text.Append(">\n");
        if (Text.Length > 0)
        {
            text.Append(' ', (depth + 1) * 2).Append(Escape(Text, attribute: false)).Append('\n');
        }

        foreach (XmlNode child in Children)
        {
            child.Write(text, depth + 1);
        }

        text.Append(' ', depth * 2).Append("</").Append(Name).Append(">\n");
    }

    private static string Escape(string value, bool attribute)
    {
        var text = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            text.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' when attribute => "&quot;",
                '\n' when attribute => "&#10;",
                '\r' when attribute => "&#13;",
                '\t' when attribute => "&#9;",
                _ => c.ToString(),
            });
        }

        return text.ToString();
    }
}
