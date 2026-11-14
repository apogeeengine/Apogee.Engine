using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Apogee.Content.Xml;

/// <summary>
/// A binary encoding of an XML document, for cooked packs (<c>APXB</c>): the tree <see cref="XmlNode"/> holds, with every name and value in one string table and the nodes in document order.
/// It is smaller than the text and read without parsing it, and it gives back exactly the tree that was written: names, attributes in their order, text, lines and children. Read a file that
/// may be either form through <see cref="XmlFile"/>.
/// </summary>
/// <remarks>
/// Little-endian; a varint is an unsigned LEB128 of at most 32 bits.
/// <code>
/// header   u32 magic "APXB" (0x42585041) · u16 version · u16 flags (0) · u32 string count · u32 node count
/// strings  count x { varint byte length, UTF-8 bytes }    first-appearance order, string 0 is ""
/// nodes    pre-order, root first: varint name, varint line, varint attribute count,
///          attributes x { varint name, varint value }, varint text, varint child count
/// </code>
/// The reader checks every count against the bytes that are left before it allocates for it, so a damaged or hostile file can only make it throw a <see cref="ContentException"/>, or
/// allocate in proportion to the file's own size. <see cref="XmlNode.Origin"/> is not stored; the reader sets it to the path it was given.
/// </remarks>
public static class XmlBinary
{
    /// <summary>The version of the encoding that <see cref="Write"/> produces and the newest <see cref="Read"/> accepts.</summary>
    public const int Version = 1;

    private const uint Magic = 0x42585041; // "APXB" read as a little-endian u32
    private const int HeaderSize = 16;

    /// <summary>The fewest bytes a node takes (name, line, attribute count, text, child count: one varint each), so a count of nodes can be checked against the file.</summary>
    private const int MinNodeBytes = 5;

    private static readonly UTF8Encoding s_strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Whether the bytes start with the magic of a cooked XML file (a text XML file cannot: it begins with <c>&lt;</c>, a byte order mark or white space).</summary>
    public static bool IsBinary(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;

    /// <summary>The encoding of the tree. The same tree always gives the same bytes.</summary>
    /// <exception cref="ContentException">The tree nests deeper than the text parser allows (<see cref="XmlNode.MaxDepth"/>), has an element or attribute with no name, a negative line, or text that is not valid Unicode.</exception>
    public static byte[] Write(XmlNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        using var writer = new Writer();
        writer.WriteNode(root, 1);
        return writer.Finish();
    }

    /// <summary>The tree encoded in <paramref name="bytes"/>.</summary>
    /// <param name="bytes">A cooked XML file.</param>
    /// <param name="origin">The path to name in errors, and what <see cref="XmlNode.Origin"/> becomes on every element.</param>
    /// <exception cref="ContentException">The bytes are not a cooked XML file this engine can read: foreign, cooked by a newer engine, cut short or damaged.</exception>
    public static XmlNode Read(ReadOnlySpan<byte> bytes, string origin)
    {
        var reader = new Reader(bytes, origin);
        XmlNode root = reader.ReadNode(1);
        reader.Finish();
        return root;
    }

    /// <summary>
    /// The value of an attribute of the root element, or null when it has none, without reading anything else of the file (the UID of a cooked scene). Only the part it reads is checked.
    /// </summary>
    /// <exception cref="ContentException">The header, the string table or the root is damaged.</exception>
    internal static string? ReadRootAttribute(ReadOnlySpan<byte> bytes, string origin, string attribute)
    {
        var reader = new Reader(bytes, origin);
        return reader.ReadRootAttribute(attribute);
    }

    // ---- Writing --------------------------------------------------------------------------------------------------------------------

    private sealed class Writer : IDisposable
    {
        private readonly List<string> _strings = [""];
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal) { [""] = 0 };
        private readonly MemoryStream _nodes = new();
        private int _nodeCount;

        public void WriteNode(XmlNode node, int depth)
        {
            if (depth > XmlNode.MaxDepth + (node.Children.Count == 0 ? 1 : 0))
            {
                throw new ContentException($"The element <{node.Name}> is nested more than {XmlNode.MaxDepth} deep and cannot be cooked.");
            }

            if (node.Name.Length == 0)
            {
                throw new ContentException("An element with no name cannot be cooked.");
            }

            if (node.Line < 0)
            {
                throw new ContentException($"The element <{node.Name}> has a negative line number.");
            }

            _nodeCount++;
            Varint(Intern(node.Name));
            Varint((uint)node.Line);
            Varint((uint)node.Attributes.Count);
            foreach (XmlProperty attribute in node.Attributes)
            {
                if (attribute.Name.Length == 0)
                {
                    throw new ContentException($"The element <{node.Name}> has an attribute with no name and cannot be cooked.");
                }

                Varint(Intern(attribute.Name));
                Varint(Intern(attribute.Value));
            }

            Varint(Intern(node.Text));
            Varint((uint)node.Children.Count);
            foreach (XmlNode child in node.Children)
            {
                WriteNode(child, depth + 1);
            }
        }

        public void Dispose() => _nodes.Dispose();

        public byte[] Finish()
        {
            // The strings are encoded here, in the order they were first used, so the header (which comes first) can say how many there are.
            using var output = new MemoryStream();
            Span<byte> header = stackalloc byte[HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)_strings.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)_nodeCount);
            output.Write(header);

            foreach (string text in _strings)
            {
                byte[] utf8;
                try
                {
                    utf8 = s_strict.GetBytes(text);
                }
                catch (EncoderFallbackException e)
                {
                    throw new ContentException("A name or text holds characters that are not valid Unicode (an unpaired surrogate) and cannot be cooked.", e);
                }

                VarintTo(output, (uint)utf8.Length);
                output.Write(utf8);
            }

            _nodes.Position = 0;
            _nodes.CopyTo(output);
            return output.ToArray();
        }

        private uint Intern(string text)
        {
            if (!_ids.TryGetValue(text, out int id))
            {
                id = _strings.Count;
                _strings.Add(text);
                _ids.Add(text, id);
            }

            return (uint)id;
        }

        private void Varint(uint value) => VarintTo(_nodes, value);

        private static void VarintTo(MemoryStream stream, uint value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            stream.WriteByte((byte)value);
        }
    }

    // ---- Reading --------------------------------------------------------------------------------------------------------------------

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly string _origin;
        private readonly int[] _start;
        private readonly int[] _length;
        private readonly string?[] _decoded;
        private readonly uint _nodeCount;
        private int _position;
        private uint _nodesRead;

        public Reader(ReadOnlySpan<byte> bytes, string origin)
        {
            _bytes = bytes;
            _origin = origin;
            _position = 0;
            _nodesRead = 0;

            if (bytes.Length < HeaderSize)
            {
                throw Bad("it is too short to be a cooked XML file.");
            }

            if (!IsBinary(bytes))
            {
                throw Bad("it is not a cooked XML file.");
            }

            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
            if (version > Version)
            {
                throw Bad($"it was cooked by a newer engine (format version {version}, this one reads up to {Version}): cook again.");
            }

            if (version == 0)
            {
                throw Bad("its format version is 0.");
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != 0)
            {
                throw Bad("it sets flags this engine does not know: cook again.");
            }

            uint strings = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
            _nodeCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
            _position = HeaderSize;

            // Every string takes a byte at the least, every node MinNodeBytes: a count that cannot fit in what is left is damage, and is found before anything is allocated for it.
            if (strings == 0 || strings > bytes.Length - HeaderSize)
            {
                throw Bad($"it claims {strings} strings but has {bytes.Length - HeaderSize} bytes after its header.");
            }

            if (_nodeCount == 0 || (long)_nodeCount * MinNodeBytes > bytes.Length - HeaderSize)
            {
                throw Bad($"it claims {_nodeCount} elements but has {bytes.Length - HeaderSize} bytes after its header.");
            }

            _start = new int[strings];
            _length = new int[strings];
            _decoded = new string?[strings];
            for (int i = 0; i < _start.Length; i++)
            {
                uint length = Varint();
                if (length > Remaining)
                {
                    throw Bad($"string {i} is {length} bytes long but only {Remaining} are left.");
                }

                _start[i] = _position;
                _length[i] = (int)length;
                _position += (int)length;
            }

            if (_length[0] != 0)
            {
                throw Bad("its first string is not empty.");
            }
        }

        private readonly int Remaining => _bytes.Length - _position;

        public XmlNode ReadNode(int depth)
        {
            if (_nodesRead >= _nodeCount)
            {
                throw Bad("it has more elements than it says.");
            }

            _nodesRead++;
            string name = Name(Varint());
            int line = LineNumber();
            var node = new XmlNode(name, line) { Origin = _origin };

            uint attributes = Varint();
            if (attributes > Remaining / 2)
            {
                throw Bad($"<{name}> claims {attributes} attributes but only {Remaining} bytes are left.");
            }

            node.Attributes.Capacity = (int)attributes;
            for (uint i = 0; i < attributes; i++)
            {
                string attributeName = Name(Varint());
                node.Attributes.Add(new XmlProperty(attributeName, Str(Varint())));
            }

            node.Text = Str(Varint());

            uint children = Varint();
            if (children > _nodeCount - _nodesRead || children > Remaining / MinNodeBytes)
            {
                throw Bad($"<{name}> claims {children} children but fewer elements are left.");
            }

            // The text parser allows MaxDepth open elements and an empty one inside them, so a leaf may sit one level deeper than a parent.
            if (depth + (children == 0 ? 0 : 1) > XmlNode.MaxDepth + 1)
            {
                throw Bad($"the elements are nested more than {XmlNode.MaxDepth} deep.");
            }

            node.Children.Capacity = (int)children;
            for (uint i = 0; i < children; i++)
            {
                node.Children.Add(ReadNode(depth + 1));
            }

            return node;
        }

        public readonly void Finish()
        {
            if (_nodesRead != _nodeCount)
            {
                throw Bad($"it says {_nodeCount} elements but has {_nodesRead}.");
            }

            if (_position != _bytes.Length)
            {
                throw Bad($"{_bytes.Length - _position} bytes are left after the last element.");
            }
        }

        public string? ReadRootAttribute(string attribute)
        {
            _ = Varint(); // the root's name
            _ = LineNumber();
            uint attributes = Varint();
            if (attributes > Remaining / 2)
            {
                throw Bad($"the root claims {attributes} attributes but only {Remaining} bytes are left.");
            }

            for (uint i = 0; i < attributes; i++)
            {
                uint name = Varint();
                uint value = Varint();
                if (Str(name) == attribute)
                {
                    return Str(value);
                }
            }

            return null;
        }

        private int LineNumber()
        {
            uint line = Varint();
            return line <= int.MaxValue ? (int)line : throw Bad($"a line number of {line} is not possible.");
        }

        private string Name(uint index)
        {
            string name = Str(index);
            return name.Length > 0 ? name : throw Bad("an element or attribute has no name.");
        }

        private string Str(uint index)
        {
            if (index >= _decoded.Length)
            {
                throw Bad($"it refers to string {index} but has {_decoded.Length}.");
            }

            string? text = _decoded[index];
            if (text is null)
            {
                try
                {
                    text = s_strict.GetString(_bytes.Slice(_start[index], _length[index]));
                }
                catch (DecoderFallbackException e)
                {
                    throw new ContentException($"{_origin}: string {index} is not valid UTF-8.", e);
                }

                _decoded[index] = text;
            }

            return text;
        }

        private uint Varint()
        {
            uint value = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (_position >= _bytes.Length)
                {
                    throw Bad("it ends in the middle of a number.");
                }

                byte b = _bytes[_position++];
                if (shift == 28 && b > 0x0F)
                {
                    throw Bad("a number is larger than 32 bits.");
                }

                value |= (uint)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    return value;
                }
            }

            throw Bad("a number is larger than 32 bits.");
        }

        private readonly ContentException Bad(string message) => new($"{_origin}: {message}");
    }
}
