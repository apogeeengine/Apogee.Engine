using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Weft.Content.Xml;
using Weft.Core.Assets;

namespace Weft.Content.Assets;

/// <summary>
/// Assets that are written by people or tools as XML, rather than imported from another format: they carry their own UID, in the <c>uid</c> attribute of the document's root element
/// (<c>&lt;Scene version="1" uid="uid://…"&gt;</c>, <c>&lt;Prefab …&gt;</c>), and have no <c>.import.xml</c>. Scenes and prefabs now; materials add their extension to <see cref="Extensions"/> when they land.
/// </summary>
public static class AuthoredAssets
{
    /// <summary>The extensions of authored assets, with the dot, in lower case.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".ascene", ".aprefab"];

    /// <summary>Whether <paramref name="path"/> names an authored asset (by its extension, not case-sensitive).</summary>
    public static bool IsAuthored(string path) => Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The UID in the root of the document, or <see cref="AssetUid.None"/> when there is none, it is not valid, or the bytes are not an XML document (a damaged file). It reads as far
    /// as the root's start tag and no further, so it is cheap on a big scene, and it never throws for bad content. A cooked document (<see cref="XmlBinary"/>) is read the same way.
    /// </summary>
    public static AssetUid ReadUid(ReadOnlySpan<byte> document)
    {
        if (XmlBinary.IsBinary(document))
        {
            return ReadCookedUid(document);
        }

        using var stream = new MemoryStream(document.ToArray());
        return ReadUid(stream);
    }

    /// <summary>The UID in the root of the document in <paramref name="document"/> (see <see cref="ReadUid(ReadOnlySpan{byte})"/>).</summary>
    public static AssetUid ReadUid(Stream document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.CanSeek)
        {
            using var copy = new MemoryStream();
            document.CopyTo(copy);
            copy.Position = 0;
            return ReadUid(copy);
        }

        long start = document.Position;
        Span<byte> head = stackalloc byte[4];
        int read = document.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        document.Position = start;
        if (XmlBinary.IsBinary(head[..read]))
        {
            using var all = new MemoryStream();
            document.CopyTo(all);
            return ReadCookedUid(all.GetBuffer().AsSpan(0, (int)all.Length));
        }

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true };
        try
        {
            using var reader = XmlReader.Create(document, settings);
            return reader.MoveToContent() == XmlNodeType.Element && AssetUid.TryParse(reader.GetAttribute("uid") ?? "", out AssetUid uid) ? uid : AssetUid.None;
        }
        catch (XmlException)
        {
            return AssetUid.None;
        }
    }

    private static AssetUid ReadCookedUid(ReadOnlySpan<byte> document)
    {
        try
        {
            return AssetUid.TryParse(XmlBinary.ReadRootAttribute(document, "cooked document", "uid") ?? "", out AssetUid uid) ? uid : AssetUid.None;
        }
        catch (ContentException)
        {
            return AssetUid.None;
        }
    }

    /// <summary>
    /// The document's text with <paramref name="uid"/> as the UID of its root: replaced in place or added after the last attribute of the root tag, with everything else exactly as it was
    /// (comments, indentation, quotes, line endings). Overwriting a scene keeps the UID of the file it replaces by calling this on the new text.
    /// </summary>
    /// <exception cref="ContentException">The text is not an XML document the edit can be made in.</exception>
    public static string SetUid(string text, AssetUid uid)
    {
        if (uid.IsNone)
        {
            throw new ArgumentException("A document cannot be given no UID.", nameof(uid));
        }

        return RootTag.SetUid(text, uid, "the document");
    }
}
