using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;
using Apogee.Core.Assets;

namespace Apogee.Content.Assets;

/// <summary>
/// Looking for the places that name an asset, for <see cref="AssetReferences"/>: which files to look at, how to read one without trusting it, and which attributes (or script strings) are the asset.
/// </summary>
internal static class ReferenceSearch
{
    /// <summary>The most mentions listed for one script. Rewrite and Move search every script, mods' too: one that spells a path millions of times must not fill the memory.</summary>
    internal const int MaxMentionsPerScript = 10_000;

    /// <summary>
    /// A reference found in a document. <see cref="Ordinal"/> is the attribute's place among all the attributes of the document (see <see cref="ReferenceText.Attributes"/>); -1 for a script.
    /// <see cref="Raw"/> is the text of the attribute as the file says it, so that a rewrite changes the path and keeps the rest (the spelling of the UID) as it was.
    /// </summary>
    internal readonly record struct Hit(int Ordinal, AssetReferenceSite Site, string Raw = "");

    /// <summary>
    /// What looking at one file gave: its references, or why it could not be used. <see cref="Bytes"/>, <see cref="Text"/> and <see cref="Tree"/> are what the references were found in (the text and the
    /// tree are null for a cooked document, and the tree for a script), so that the same read is the one an edit is made from.
    /// </summary>
    internal sealed record DocumentResult(string Path, bool IsScript, string? Problem, byte[]? Bytes, string? Text, XmlNode? Tree, IReadOnlyList<Hit> Hits);

    /// <summary>
    /// Decides whether an attribute is the asset. <paramref name="paths"/> are all the canonical spellings of the paths that mean it. A reference matches by its path when that is one of them, and
    /// by its UID when the UID is the asset's and the reference's path does not lead to another existing file (the path wins, as in <see cref="AssetDatabase.Resolve"/>).
    /// <para>
    /// A UID that is on two files (a copy that was not imported yet) belongs, as far as the engine is concerned, to the one <see cref="AssetDatabase.PathOf"/> names. When the asset being renamed is
    /// the other one, a reference that carries that UID and a stale path is the other file's, and is not matched; only references that spell the asset's own path are.
    /// </para>
    /// </summary>
    internal sealed class Matcher
    {
        private readonly VirtualFileSystem _files;
        private readonly AssetUid _uid;
        private readonly IReadOnlyList<string> _spellings;
        private readonly HashSet<string> _paths;
        private readonly List<string> _names;
        private readonly bool _uidIsTheAssets;

        public Matcher(AssetDatabase assets, AssetUid uid, IReadOnlyList<string> paths)
        {
            _files = assets.Files;
            _uid = uid;
            _spellings = paths;
            _paths = new HashSet<string>(paths, StringComparer.Ordinal);
            _names = [.. paths.Select(VfsPath.FileName).Distinct(StringComparer.Ordinal)];
            _uidIsTheAssets = !uid.IsNone && UidBelongsToPaths(assets, uid, paths);
        }

        // The UID names the asset when no file has it, when only the UID was asked for, when the file it names is one of the asset's paths, or when the asset's own paths have no file (it has moved,
        // and the UID names it where it went). It does not when the asset exists and another file is the one the UID names: then the asset is a copy.
        private bool UidBelongsToPaths(AssetDatabase assets, AssetUid uid, IReadOnlyList<string> paths)
        {
            string? named = assets.PathOf(uid);
            return named is null || paths.Count == 0 || _paths.Contains(named) || !paths.Any(_files.FileExists);
        }

        public AssetUid Uid => _uid;

        public IReadOnlyList<string> Paths => _spellings;

        /// <summary>Nothing to look for: no UID and no path.</summary>
        public bool IsEmpty => _uid.IsNone && _spellings.Count == 0;

        public bool Matches(AssetRef value)
        {
            bool valid = value.HasPath && VfsPath.IsVirtual(value.Path);
            string canonical = valid ? VfsPath.Normalize(value.Path) : "";
            if (valid && _paths.Contains(canonical))
            {
                return true;
            }

            if (!_uidIsTheAssets || value.Uid != _uid)
            {
                return false;
            }

            return !valid || !_files.FileExists(canonical);
        }

        /// <summary>
        /// A cheap test on the text of a document before it is parsed: does it contain the UID, or the file name of any of the paths? A document that does not cannot refer to the asset. Not used
        /// when a name contains a character the file might have written as an entity (it would be missed).
        /// </summary>
        public bool MightMention(string text)
        {
            if (!_uid.IsNone && text.Contains(_uid.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return _names.Exists(n => n.AsSpan().IndexOfAny('&', '\'') >= 0) || _names.Exists(n => text.Contains(n, StringComparison.Ordinal));
        }
    }

    // ---- Which files ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Looks at the documents of the database's roots (and the scripts, when asked) and returns what each says about the asset. Files with no mention of it are not returned; a file that cannot be
    /// used is, with its <see cref="DocumentResult.Problem"/>. Lazy: one file is read at a time.
    /// </summary>
    internal static IEnumerable<DocumentResult> Search(AssetDatabase assets, Matcher matcher, IReadOnlyList<string>? defFolders, bool includeScripts)
    {
        if (matcher.IsEmpty)
        {
            yield break;
        }

        List<string> folders = [.. (defFolders ?? []).SelectMany(f => AssetDatabase.SpellingsOf(CanonicalFolder(f)))];
        foreach (string file in assets.EnumerateRootFiles())
        {
            if (IsXmlDocument(file, folders))
            {
                if (LoadXml(assets.Files, file, matcher) is { } document)
                {
                    yield return document;
                }
            }
            else if (includeScripts && file.EndsWith(".luau", StringComparison.OrdinalIgnoreCase) && LoadScript(assets.Files, file, matcher) is { } script)
            {
                yield return script;
            }
        }
    }

    private static string CanonicalFolder(string folder) =>
        VfsPath.TryParse(folder, out string canonical, out string? problem) ? canonical : throw new ContentException($"'{folder}' is not a valid folder: {problem}.");

    private static bool IsXmlDocument(string file, List<string> folders)
    {
        if (AuthoredAssets.IsAuthored(file))
        {
            return true;
        }

        return file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            && !file.EndsWith(ImportSidecar.Suffix, StringComparison.OrdinalIgnoreCase)
            && folders.Exists(folder => VfsPath.IsUnder(file, folder));
    }

    // ---- Reading a file without trusting it ---------------------------------------------------------------------------------------------

    /// <summary>The bytes of the file, or the reason they cannot be used: unreadable, or larger than <see cref="AssetReferences.MaxDocumentBytes"/>.</summary>
    private static (byte[]? Bytes, string? Problem) ReadBounded(VirtualFileSystem files, string path)
    {
        try
        {
            using (Stream stream = files.OpenRead(path))
            {
                if (stream.CanSeek && stream.Length > AssetReferences.MaxDocumentBytes)
                {
                    return (null, $"larger than {AssetReferences.MaxDocumentBytes / (1024 * 1024)} MB");
                }
            }

            byte[] bytes = files.ReadAllBytes(path);
            return bytes.Length > AssetReferences.MaxDocumentBytes ? (null, $"larger than {AssetReferences.MaxDocumentBytes / (1024 * 1024)} MB") : (bytes, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"cannot be read: {e.Message}");
        }
    }

    // Strict: bytes that are not UTF-8 are an error and not a replacement character, because the text is written back and a replaced byte would be damage.
    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static DocumentResult? LoadXml(VirtualFileSystem files, string path, Matcher matcher)
    {
        (byte[]? bytes, string? problem) = ReadBounded(files, path);
        if (bytes is null)
        {
            return Failed(path, false, problem!);
        }

        if (XmlBinary.IsBinary(bytes))
        {
            return ParseCooked(path, bytes, matcher);
        }

        string text;
        try
        {
            text = s_strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // It cannot be edited. It is only worth a report when it could name the asset: a name in ASCII survives a lossy reading, so this finds a Latin-1 scene that has the path in it.
            return matcher.MightMention(Encoding.UTF8.GetString(bytes)) ? Failed(path, false, "not valid UTF-8 text") : null;
        }

        if (!matcher.MightMention(text))
        {
            return null;
        }

        try
        {
            XmlNode tree = XmlNode.Parse(text.TrimStart('\uFEFF'), path);
            return new DocumentResult(path, false, null, bytes, text, tree, References(path, tree, matcher));
        }
        catch (XmlParseException e)
        {
            return Failed(path, false, $"not well-formed XML: {e.Reason} (line {e.Line})");
        }
        catch (ContentException e)
        {
            return Failed(path, false, e.Message);
        }
    }

    private static DocumentResult? ParseCooked(string path, byte[] bytes, Matcher matcher)
    {
        try
        {
            XmlNode tree = XmlBinary.Read(bytes, path);
            List<Hit> hits = References(path, tree, matcher);
            return hits.Count == 0 ? null : new DocumentResult(path, false, null, bytes, null, tree, hits);
        }
        catch (ContentException e)
        {
            return Failed(path, false, $"damaged cooked document: {e.Message}");
        }
    }

    private static DocumentResult Failed(string path, bool isScript, string problem) => new(path, isScript, problem, null, null, null, []);

    // ---- XML attributes ---------------------------------------------------------------------------------------------------------------

    private static List<Hit> References(string path, XmlNode tree, Matcher matcher)
    {
        var hits = new List<Hit>();
        List<(XmlNode Element, XmlProperty Attribute)> attributes = ReferenceText.Attributes(tree);
        for (int ordinal = 0; ordinal < attributes.Count; ordinal++)
        {
            (XmlNode element, XmlProperty attribute) = attributes[ordinal];
            if (AssetRef.TryParse(attribute.Value, out AssetRef value) && !value.IsEmpty && matcher.Matches(value))
            {
                if (hits.Count >= AssetReferences.MaxReferencesPerDocument)
                {
                    throw new ContentException($"it names the asset more than {AssetReferences.MaxReferencesPerDocument} times");
                }

                hits.Add(new Hit(ordinal, new AssetReferenceSite(path, element.Line, element.Name, attribute.Name, value), attribute.Value));
            }
        }

        return hits;
    }

    // ---- Scripts ----------------------------------------------------------------------------------------------------------------------

    private static DocumentResult? LoadScript(VirtualFileSystem files, string path, Matcher matcher)
    {
        (byte[]? bytes, string? problem) = ReadBounded(files, path);
        if (bytes is null)
        {
            return Failed(path, true, problem!);
        }

        string text = Encoding.UTF8.GetString(bytes); // only read, never written back: a replacement character is harmless here
        List<Hit> hits = Mentions(path, text, matcher);
        return hits.Count == 0 ? null : new DocumentResult(path, true, null, bytes, text, null, hits);
    }

    /// <summary>
    /// Every place the text spells the asset's path (any of its spellings) or its UID, in text order, at most <see cref="MaxMentionsPerScript"/> of them (the first ones: a script that spells a
    /// path a million times is not read as a million rows). A path counts only when it is not part of a longer name (<c>a.glb</c> is not in <c>a.glb.bak</c> or <c>a.glbx</c>); a UID that follows a
    /// path as <c>path|uid://…</c> is part of that mention and not another one. One pass over the text for each thing looked for, and one for the line numbers: the time is linear in its length.
    /// </summary>
    private static List<Hit> Mentions(string path, string text, Matcher matcher)
    {
        var found = new List<(int Index, int Length, AssetRef Value)>();
        foreach (string spelling in matcher.Paths)
        {
            int count = 0;
            for (int at = text.IndexOf(spelling, StringComparison.Ordinal); at >= 0 && count < MaxMentionsPerScript; at = text.IndexOf(spelling, at + 1, StringComparison.Ordinal))
            {
                int end = at + spelling.Length;
                if (!IsWholeName(text, at, end))
                {
                    continue;
                }

                int uidLength = LengthOfUidAfter(text, end, matcher.Uid);
                found.Add((at, spelling.Length + uidLength, new AssetRef(spelling, uidLength > 0 ? matcher.Uid : AssetUid.None)));
                count++;
            }
        }

        AddUidMentions(text, matcher, found);
        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        if (found.Count > MaxMentionsPerScript)
        {
            found.RemoveRange(MaxMentionsPerScript, found.Count - MaxMentionsPerScript);
        }

        // The line numbers in one pass: count the line ends between one mention and the next.
        var hits = new List<Hit>(found.Count);
        int line = 1;
        int position = 0;
        foreach ((int index, _, AssetRef value) in found)
        {
            line += text.AsSpan(position, index - position).Count('\n');
            position = index;
            hits.Add(new Hit(-1, new AssetReferenceSite(path, line, "", "", value)));
        }

        return hits;
    }

    // The length of a "|uid://…" right after the path (0 when there is none) when it is the asset's UID.
    private static int LengthOfUidAfter(string text, int end, AssetUid uid)
    {
        if (uid.IsNone || end >= text.Length || text[end] != AssetRef.Separator)
        {
            return 0;
        }

        string uidText = uid.ToString();
        return string.Compare(text, end + 1, uidText, 0, uidText.Length, StringComparison.OrdinalIgnoreCase) == 0 ? 1 + uidText.Length : 0;
    }

    private static void AddUidMentions(string text, Matcher matcher, List<(int Index, int Length, AssetRef Value)> found)
    {
        if (matcher.Uid.IsNone)
        {
            return;
        }

        string uidText = matcher.Uid.ToString();

        // Where the UID of each "path|uid://…" mention starts: that UID is part of the mention and is not found again.
        var startsOfPathMentions = new HashSet<int>();
        foreach ((int index, int length, AssetRef value) in found)
        {
            if (value.Uid == matcher.Uid)
            {
                startsOfPathMentions.Add(index + length - uidText.Length);
            }
        }

        int count = 0;
        for (int at = text.IndexOf(uidText, StringComparison.OrdinalIgnoreCase); at >= 0 && count < MaxMentionsPerScript; at = text.IndexOf(uidText, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = at + uidText.Length;
            bool partOfLonger = end < text.Length && char.IsAsciiLetterOrDigit(text[end]);
            if (!partOfLonger && !startsOfPathMentions.Contains(at))
            {
                found.Add((at, uidText.Length, AssetRef.FromUid(matcher.Uid)));
                count++;
            }
        }
    }

    // The characters before and after must not make the match a piece of a longer path: "xres://a.glb", "res://a.glb.bak", "res://a.glbx", "res://a.glb/child".
    private static bool IsWholeName(string text, int start, int end)
    {
        if (start > 0 && (char.IsAsciiLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
        {
            return false;
        }

        if (end >= text.Length)
        {
            return true;
        }

        char next = text[end];
        if (char.IsAsciiLetterOrDigit(next) || next is '_' or '-' or '/')
        {
            return false;
        }

        // A dot ends a sentence ("see res://a.glb.") unless a name goes on after it ("res://a.glb.bak").
        return next != '.' || end + 1 >= text.Length || !char.IsAsciiLetterOrDigit(text[end + 1]);
    }
}
