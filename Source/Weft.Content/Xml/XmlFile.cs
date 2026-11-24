using System;
using System.Text;
using Weft.Content.FileSystem;

namespace Weft.Content.Xml;

/// <summary>
/// Reads an XML file of the game, whichever form it is in: text, as it is written and edited, or cooked (<see cref="XmlBinary"/>), as a shipped pack holds it. A cooked file keeps the path of
/// its source, so this is the one way to read an authored or definition file; reading it as text (<see cref="VirtualFileSystem.ReadAllText"/>) would give the binary.
/// </summary>
public static class XmlFile
{
    /// <summary>The document at <paramref name="path"/>.</summary>
    /// <exception cref="System.IO.FileNotFoundException">No source has the file.</exception>
    /// <exception cref="XmlParseException">The file is text and is not well-formed XML.</exception>
    /// <exception cref="ContentException">The file is cooked and damaged, or cooked by a newer engine.</exception>
    public static XmlNode Read(VirtualFileSystem files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Read(files.ReadAllBytes(path), path);
    }

    /// <summary>The document in <paramref name="bytes"/>: cooked when it starts with the magic, UTF-8 text (with or without a byte order mark) otherwise.</summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="origin">The path to name in errors, and what <see cref="XmlNode.Origin"/> becomes.</param>
    /// <exception cref="XmlParseException">The bytes are text and not well-formed XML.</exception>
    /// <exception cref="ContentException">The bytes are cooked and damaged, or cooked by a newer engine.</exception>
    public static XmlNode Read(ReadOnlySpan<byte> bytes, string origin)
    {
        if (XmlBinary.IsBinary(bytes))
        {
            return XmlBinary.Read(bytes, origin);
        }

        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            bytes = bytes[Encoding.UTF8.Preamble.Length..];
        }

        return XmlNode.Parse(Encoding.UTF8.GetString(bytes), origin);
    }
}
