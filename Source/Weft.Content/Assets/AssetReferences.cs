using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Weft.Content.FileSystem;
using Weft.Core.Assets;

namespace Weft.Content.Assets;

/// <summary>One place in a document that names an asset: an attribute whose text is an <see cref="AssetRef"/> (or, for a script, a string that mentions the asset).</summary>
/// <param name="Document">The document, as the virtual path it was found at (<c>res://scenes/start.ascene</c>, <c>mod://core/scenes/a.ascene</c>).</param>
/// <param name="Line">The line of the element (of the text, for a script).</param>
/// <param name="Element">The element's name; empty for a script.</param>
/// <param name="Attribute">The attribute's name; empty for a script, where the mention is text and not an attribute.</param>
/// <param name="Value">What the attribute says, read as a reference. For a script the text that was found: a path, or a <c>uid://</c>.</param>
public sealed record AssetReferenceSite(string Document, int Line, string Element, string Attribute, AssetRef Value);

/// <summary>An attribute that is rewritten (or would be, in a preview): where it is and what it will say.</summary>
public sealed record ReferenceChange(AssetReferenceSite Site, AssetRef NewValue);

/// <summary>A file the search could not use, and why: the reason is written for a person (<c>read-only</c>, <c>not valid UTF-8</c>…).</summary>
/// <param name="Document">The document, as the virtual path it was found at.</param>
/// <param name="Reason">Why it was not used.</param>
/// <param name="Blocking">True when the document could be written: its references (if it has any) are then <em>not</em> rewritten and will break, so a rename that is applied is refused unless the caller allows it (<c>allowSkipped</c>). False for a document in a read-only root, which nothing could have rewritten.</param>
public sealed record SkippedDocument(string Document, string Reason, bool Blocking = false);

/// <summary>What <see cref="AssetReferences.Rewrite"/> did, or in a preview would do.</summary>
/// <param name="Changes">The attributes that are rewritten to the new path (a reference that carries a UID keeps it).</param>
/// <param name="Unchanged">References to the asset that need no change: a <c>uid://</c> alone follows the file by itself, and a reference that already says the new path.</param>
/// <param name="ScriptMentions">Script strings that mention the asset (a path, or its UID). They are reported and never rewritten: a script is code, and a string in it may be built or compared.</param>
/// <param name="Unrewritable">Other places that name the old path and are not rewritten, for the editor to show: the paths in <c>game.xml</c> (<c>&lt;EntryScene&gt;</c>, <c>&lt;Icon&gt;</c>) and <c>mod.xml</c> (<c>&lt;Script&gt;</c>), whose <see cref="AssetReferenceSite.Value"/> is the full path they name, and text inside an element of a document (<see cref="AssetReferenceSite.Attribute"/> is empty). They break if the file moves and nothing here fixes them.</param>
/// <param name="Skipped">The documents that were not used, each with the reason. A document in a read-only root that refers to the asset is here: its references were found and not rewritten. A file that is not well-formed is here too; it may or may not have held references.</param>
/// <param name="Documents">The files that were changed (in a preview, that would be).</param>
/// <param name="Applied">False for a preview: nothing was written.</param>
public sealed record RewriteReport(
    IReadOnlyList<ReferenceChange> Changes,
    IReadOnlyList<AssetReferenceSite> Unchanged,
    IReadOnlyList<AssetReferenceSite> ScriptMentions,
    IReadOnlyList<AssetReferenceSite> Unrewritable,
    IReadOnlyList<SkippedDocument> Skipped,
    IReadOnlyList<string> Documents,
    bool Applied);

/// <summary>
/// Finds the documents that refer to an asset and rewrites them when the asset is renamed or moved: what the editor's rename calls. A <em>document</em> is an authored file
/// (<see cref="AuthoredAssets.Extensions"/>: scenes now) or an <c>.xml</c> file in one of the definition folders the caller names, in any root of the <see cref="AssetDatabase"/>. A
/// <em>reference</em> is an attribute whose whole text is an <see cref="AssetRef"/> (<c>Model="res://models/car.glb|uid://4k2m9x7q1w3ze"</c>); text inside elements is not looked at.
/// <para>
/// What counts as a reference to the asset: an attribute whose path is the asset's path (as the engine writes it: the two addresses of a package file, <c>res://packages/id/x</c> and
/// <c>mod://id/x</c>, are the same), or whose UID is the asset's UID <em>and</em> whose path does not lead to some other file. That last condition is the engine's own rule, the path wins
/// while its file exists (<see cref="AssetDatabase.Resolve"/>): a reference that carries the UID of a copied file and the path of the copy means the copy.
/// </para>
/// <para>
/// Rewriting changes the characters of the path part of each reference and nothing else (<see cref="ReferenceText"/>); a UID is never touched, so a reference keeps resolving through it even if
/// a document is missed. It is all or nothing: every new text is made and checked before the first file is written, a file that changed in between stops the whole thing, and if a write fails the
/// files already written are put back. Nothing here deletes or half-writes a file.
/// </para>
/// </summary>
public static class AssetReferences
{
    /// <summary>Documents and scripts larger than this are skipped (a hostile or mistaken file must not make a rename read gigabytes).</summary>
    public const long MaxDocumentBytes = 64L * 1024 * 1024;

    /// <summary>The most references to one asset that one document may hold to be rewritten; a document with more is skipped (a plan keeps a record of each).</summary>
    public const int MaxReferencesPerDocument = 100_000;

    /// <summary>The most memory the texts of all the documents of one rewrite may take (the old and the new bytes of each): a document that would go over it is skipped, with the reason.</summary>
    public const long MaxPlannedBytes = 256L * 1024 * 1024;

    /// <summary>
    /// The places in the documents of the database's roots that name <paramref name="target"/>: by its UID when it has one (also when the path in the reference is out of date), and by its path.
    /// Files that cannot be read or are not well-formed are left out here (<see cref="Rewrite"/> reports them). Each site is a reference, in document order.
    /// </summary>
    /// <param name="assets">The roots to search.</param>
    /// <param name="target">The asset: a path, a UID, or both.</param>
    /// <param name="defFolders">Virtual folders (<c>res://data</c>) whose <c>.xml</c> files are searched as well as the authored files.</param>
    /// <param name="includeScripts">Also list the <c>.luau</c> files that mention the path or the UID (<see cref="AssetReferenceSite.Attribute"/> is empty for these).</param>
    public static IReadOnlyList<AssetReferenceSite> Find(AssetDatabase assets, AssetRef target, IReadOnlyList<string>? defFolders = null, bool includeScripts = false)
    {
        ArgumentNullException.ThrowIfNull(assets);
        return Guard("Looking for the references", () =>
        {
            List<string> paths = target.HasPath && VfsPath.TryParse(target.Path, out string canonical, out _) ? AssetDatabase.SpellingsOf(canonical) : [];
            var matcher = new ReferenceSearch.Matcher(assets, target.Uid, paths);
            return (IReadOnlyList<AssetReferenceSite>)[.. ReferenceSearch.Search(assets, matcher, defFolders, includeScripts).Where(d => d.Problem is null).SelectMany(d => d.Hits).Select(h => h.Site)];
        });
    }

    /// <summary>
    /// Runs the part of an operation that only reads. A folder that cannot be listed, a file that cannot be opened, a link that loops, a source of its own that throws: whatever the cause, the
    /// caller gets a <see cref="ContentException"/> (and nothing was changed, since nothing here writes). A null argument is a mistake of the caller and stays an <see cref="ArgumentNullException"/>.
    /// </summary>
    internal static T Guard<T>(string what, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is not (ContentException or ArgumentNullException or OutOfMemoryException))
        {
            throw new ContentException($"{what} failed: {e.Message} Nothing was changed.", e);
        }
    }

    /// <summary>
    /// Makes the references to the asset at <paramref name="oldPath"/> say <paramref name="newPath"/>. It may be called before the file is moved or after (the asset's UID is read from whichever of
    /// the two paths has the file), and with <paramref name="dryRun"/> it only reports what it would do. The database is refreshed first, since the files may just have moved.
    /// </summary>
    /// <param name="assets">The roots whose documents are rewritten. Only writable ones are; a document in a read-only root is reported in <see cref="RewriteReport.Skipped"/>.</param>
    /// <param name="oldPath">The path the asset had.</param>
    /// <param name="newPath">The path it has, or will have.</param>
    /// <param name="defFolders">Virtual folders whose <c>.xml</c> files are rewritten as well as the authored files.</param>
    /// <param name="dryRun">Report and write nothing.</param>
    /// <param name="includeScripts">List the scripts that mention the asset in <see cref="RewriteReport.ScriptMentions"/> (never rewritten).</param>
    /// <param name="allowSkipped">Go on although a writable document could not be used (it is not well-formed, not UTF-8, too big, cooked…): its references to the asset will not be rewritten. By default that refuses the rename (before anything is written), because the references would break silently; an editor passes true after it showed <see cref="RewriteReport.Skipped"/> to the user and they agreed.</param>
    /// <exception cref="ContentException">A path is not valid, they are the same, neither or both have a file, a writable document was skipped and <paramref name="allowSkipped"/> is false, a file changed while the references were being prepared, or a write failed (the files already written were put back, and the message says so; if one could not be put back it is named).</exception>
    public static RewriteReport Rewrite(AssetDatabase assets, string oldPath, string newPath, IReadOnlyList<string>? defFolders = null, bool dryRun = false, bool includeScripts = true, bool allowSkipped = false)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);
        ReferencePlan plan = Guard("Preparing the references", () =>
        {
            assets.Refresh();
            (string from, string to) = CheckMove(assets.Files, oldPath, newPath);
            AssetUid uid = assets.UidOf(from);
            if (uid.IsNone)
            {
                uid = assets.UidOf(to);
            }

            return Plan(assets, uid, from, to, defFolders, includeScripts);
        });
        if (!dryRun)
        {
            RefuseSkipped(plan.Report, allowSkipped);
        }

        if (dryRun || plan.Edits.Count == 0)
        {
            return plan.Report with { Applied = !dryRun };
        }

        Apply(assets.Files, plan.Edits);
        return plan.Report with { Applied = true };
    }

    /// <summary>The two paths of a rename, made canonical and checked: valid, different, and with a file at one of them (at the old one when the file has not moved yet, at the new one when it has).</summary>
    internal static (string From, string To) CheckMove(VirtualFileSystem files, string oldPath, string newPath)
    {
        string from = Canonical(oldPath);
        string to = Canonical(newPath);
        if (from == to)
        {
            throw new ContentException($"'{from}' is already the path: there is nothing to rename.");
        }

        if (files.DirectoryExists(to))
        {
            throw new ContentException($"'{to}' is a folder: an asset cannot be renamed to it. Name the file.");
        }

        if (files.DirectoryExists(from))
        {
            throw new ContentException($"'{from}' is a folder: rename the files in it, one at a time.");
        }

        bool oldExists = files.FileExists(from);
        bool newExists = files.FileExists(to);
        if (oldExists && newExists)
        {
            if (!IsListed(files, from) || !IsListed(files, to))
            {
                throw new ContentException($"'{from}' and '{to}' differ only in case and are the same file on this file system. Rename it in two steps, through a name that differs by more than case.");
            }

            throw new ContentException($"Both '{from}' and '{to}' exist: renaming would make two assets one.");
        }

        if (!oldExists && !newExists)
        {
            throw new ContentException($"There is no file at '{from}' or at '{to}'.");
        }

        return (from, to);
    }

    // Paths are compared with their case in the virtual file system, and a file system that ignores case finds a file under a spelling it is not listed with.
    private static bool IsListed(VirtualFileSystem files, string path)
    {
        string name = VfsPath.FileName(path);
        return files.EnumerateFiles(VfsPath.DirectoryOf(path)).Any(f => VfsPath.FileName(f) == name);
    }

    /// <summary>Stops a rename before anything is written when a document that could have been rewritten was skipped (see <see cref="SkippedDocument.Blocking"/>), unless the caller allowed it.</summary>
    /// <exception cref="ContentException">There is such a document and <paramref name="allowSkipped"/> is false.</exception>
    internal static void RefuseSkipped(RewriteReport report, bool allowSkipped)
    {
        List<SkippedDocument> blocking = [.. report.Skipped.Where(s => s.Blocking)];
        if (allowSkipped || blocking.Count == 0)
        {
            return;
        }

        string shown = string.Join("; ", blocking.Take(5).Select(s => $"{s.Document} ({s.Reason})"));
        string more = blocking.Count > 5 ? $" and {blocking.Count - 5} more" : "";
        throw new ContentException($"{blocking.Count} document(s) that can be written could not be searched or rewritten, so references to the asset in them would break: {shown}{more}. Nothing was changed. Fix them, or go on with allowSkipped.");
    }

    private static string Canonical(string path) =>
        VfsPath.TryParse(path, out string canonical, out string? problem) ? canonical : throw new ContentException($"'{path}' is not a valid path: {problem}.");

    // ---- Planning -------------------------------------------------------------------------------------------------------------------

    /// <summary>One document and its text before and after.</summary>
    internal sealed record DocumentEdit(string Path, byte[] Original, byte[] Updated);

    /// <summary>What a rewrite would do: the report (not applied) and the files to write.</summary>
    internal sealed record ReferencePlan(RewriteReport Report, IReadOnlyList<DocumentEdit> Edits);

    /// <summary>
    /// Works out every edit without writing anything: the documents that name the asset, the new text of each, and the ones that cannot be used. <paramref name="from"/> and <paramref name="to"/> are
    /// canonical. Both spellings are targets of the search: a reference that already says the new path is found, and is left as it is.
    /// </summary>
    internal static ReferencePlan Plan(AssetDatabase assets, AssetUid uid, string from, string to, IReadOnlyList<string>? defFolders, bool includeScripts, long maxPlannedBytes = MaxPlannedBytes)
    {
        var matcher = new ReferenceSearch.Matcher(assets, uid, [.. AssetDatabase.SpellingsOf(from), .. AssetDatabase.SpellingsOf(to)]);
        var plan = new PlanBuilder(assets.Files, to, maxPlannedBytes);
        List<string> newSpellings = AssetDatabase.SpellingsOf(to);
        foreach (ReferenceSearch.DocumentResult document in ReferenceSearch.Search(assets, matcher, defFolders, includeScripts))
        {
            if (document.Problem is not null)
            {
                plan.Skip(document.Path, document.Problem);
                continue;
            }

            // What cannot be rewritten is for a person; one that already says the new path is right, and a UID alone follows the file.
            if (document.Unrewritable is { } others)
            {
                plan.Unrewritable.AddRange(others.Where(s => s.Value.HasPath && !newSpellings.Contains(VfsPath.IsVirtual(s.Value.Path) ? VfsPath.Normalize(s.Value.Path) : s.Value.Path)));
            }

            if (document.IsScript)
            {
                // A UID in a script follows the file by itself, and a script that already says the new path is right: only the old path needs a person.
                plan.Mentions.AddRange(document.Hits.Select(h => h.Site).Where(s => s.Value.HasPath && !newSpellings.Contains(s.Value.Path)));
            }
            else if (document.Hits.Count > 0)
            {
                plan.Add(document);
            }
        }

        return plan.Build();
    }

    private sealed class PlanBuilder(VirtualFileSystem files, string newPath, long maxPlannedBytes)
    {
        private long _plannedBytes;

        public List<ReferenceChange> Changes { get; } = [];

        public List<AssetReferenceSite> Unchanged { get; } = [];

        public List<AssetReferenceSite> Mentions { get; } = [];

        public List<AssetReferenceSite> Unrewritable { get; } = [];

        public List<SkippedDocument> Skipped { get; } = [];

        private readonly List<DocumentEdit> _edits = [];

        public ReferencePlan Build() =>
            new(new RewriteReport(Changes, Unchanged, Mentions, Unrewritable, Skipped, [.. _edits.Select(e => e.Path)], Applied: false), _edits);

        /// <summary>Decides what to do with a document that has references: nothing to change, cannot be changed (and why), or its new text.</summary>
        public void Add(ReferenceSearch.DocumentResult document)
        {
            var newValues = new Dictionary<int, string>();
            var changes = new List<ReferenceChange>();
            foreach (ReferenceSearch.Hit hit in document.Hits)
            {
                AssetRef updated = Updated(hit.Site.Value);
                if (updated == hit.Site.Value)
                {
                    Unchanged.Add(hit.Site);
                    continue;
                }

                changes.Add(new ReferenceChange(hit.Site, updated));
                newValues[hit.Ordinal] = TextOf(updated, hit.Raw);
            }

            if (changes.Count == 0)
            {
                return;
            }

            if (Unwritable(document, changes.Count) is { } reason)
            {
                Skip(document.Path, reason);
                return;
            }

            if (_plannedBytes + (2L * document.Bytes!.Length) > maxPlannedBytes)
            {
                Skip(document.Path, $"the documents to rewrite together would take more than {maxPlannedBytes / (1024 * 1024)} MB of memory");
                return;
            }

            try
            {
                string text = ReferenceText.Replace(document.Text!, document.Tree!, newValues, document.Path);
                var edit = new DocumentEdit(document.Path, document.Bytes!, new UTF8Encoding(false).GetBytes(text));
                _plannedBytes += edit.Original.Length + edit.Updated.Length;
                _edits.Add(edit);
                Changes.AddRange(changes);
            }
            catch (ContentException e)
            {
                Skip(document.Path, e.Message);
            }
        }

        /// <summary>Records a document that was not used; it blocks the rename when the document could have been written.</summary>
        public void Skip(string path, string reason) => Skipped.Add(new SkippedDocument(path, reason, Blocking: files.CanWrite(path)));

        private string? Unwritable(ReferenceSearch.DocumentResult document, int count)
        {
            if (!files.CanWrite(document.Path))
            {
                return $"read-only: its {count} reference(s) to the asset were found and not rewritten";
            }

            return document.Text is null ? $"cooked: its {count} reference(s) to the asset cannot be rewritten in place; cook it again from the edited source" : null;
        }

        // The new text of the attribute: the new path, then the old text from the "|" on, character for character (a UID written in capitals stays in capitals).
        private static string TextOf(AssetRef updated, string raw)
        {
            int bar = raw.IndexOf(AssetRef.Separator, StringComparison.Ordinal);
            return bar < 0 || !updated.HasPath ? updated.ToString() : updated.Path + raw[bar..];
        }

        // The reference after the rename: the path part says the new path, spelled the way the reference spelled the old one; the UID stays; a UID alone has nothing to rewrite.
        private AssetRef Updated(AssetRef value) => value.HasPath ? new AssetRef(Spelling(value.Path), value.Uid) : value;

        private string Spelling(string written)
        {
            string canonical = VfsPath.IsVirtual(written) ? VfsPath.Normalize(written) : written;
            string form = FormOf(canonical);
            return AssetDatabase.SpellingsOf(newPath).Find(s => FormOf(s) == form) ?? newPath;
        }
    }

    // How a path addresses its file: through a mounted package (mod://), through a package folder under the game's, or plainly. A reference keeps its form when it is rewritten.
    private static string FormOf(string canonical)
    {
        if (canonical.StartsWith("mod://", StringComparison.Ordinal))
        {
            return "mod";
        }

        return canonical.StartsWith("res://packages/", StringComparison.Ordinal) ? "packages" : canonical.StartsWith("res://dev_mods/", StringComparison.Ordinal) ? "dev_mods" : "";
    }

    // ---- Applying -------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes every edit, or none. First every file is read again and has to be what the plan was made from (something saved it in between: stop, nothing is written); then the files are written
    /// one by one, each checked once more right before its own write (the first check only makes a mistake cheap: the last file would otherwise wait for all the writes before it), each write puts
    /// the new file in place whole, and if anything goes wrong, whatever the exception, the files already written get their old bytes back. A file is put back only if it still holds what was
    /// written: one saved since (by the user, or the editor's auto-save) is left as it is and named in the error, never overwritten with older text.
    /// </summary>
    /// <exception cref="ContentException">A file changed, or a write failed.</exception>
    internal static void Apply(VirtualFileSystem files, IReadOnlyList<DocumentEdit> edits)
    {
        foreach (DocumentEdit edit in edits)
        {
            if (!IsUnchanged(files, edit))
            {
                throw new ContentException($"{edit.Path} changed while its references were being prepared: nothing was changed. Try again.");
            }
        }

        var touched = new List<DocumentEdit>();
        string? failure = null;
        Exception? cause = null;
        try
        {
            foreach (DocumentEdit edit in edits)
            {
                if (!IsUnchanged(files, edit))
                {
                    failure = $"{edit.Path} was saved while the references were being written";
                    break;
                }

                touched.Add(edit); // before the write: a write that fails half way is put back too
                files.WriteAllBytes(edit.Path, edit.Updated);
            }
        }
        catch (Exception e)
        {
            // Any exception: a source that is not a folder may throw anything, and the files written so far must be put back whatever it was.
            failure = $"writing the references failed ({e.Message})";
            cause = e;
        }

        if (failure is null)
        {
            return;
        }

        List<string> stuck = Restore(files, touched);
        string message = stuck.Count == 0
            ? $"{failure}: the files written so far were put back, nothing was changed."
            : $"{failure}, and these files could not be put back, restore them from version control: {string.Join(", ", stuck)}.";
        throw cause is null ? new ContentException(message) : new ContentException(message, cause);
    }

    private static bool IsUnchanged(VirtualFileSystem files, DocumentEdit edit)
    {
        try
        {
            return files.ReadAllBytes(edit.Path).AsSpan().SequenceEqual(edit.Original);
        }
        catch (Exception)
        {
            return false; // a file that cannot be read is not the file the plan was made from
        }
    }

    /// <summary>
    /// Writes the original bytes of every edit back, but only into a file that still holds the new bytes this code wrote; returns what could not be put back, each with a reason when it was not a failure
    /// to write (a file that was saved since is left as the person saved it). A file that already holds its original bytes needs nothing.
    /// </summary>
    internal static List<string> Restore(VirtualFileSystem files, IReadOnlyList<DocumentEdit> edits)
    {
        var stuck = new List<string>();
        foreach (DocumentEdit edit in edits.Reverse())
        {
            try
            {
                byte[] now = files.ReadAllBytes(edit.Path);
                if (now.AsSpan().SequenceEqual(edit.Original))
                {
                    continue;
                }

                if (!now.AsSpan().SequenceEqual(edit.Updated))
                {
                    stuck.Add($"{edit.Path} (saved since, left as it is)");
                    continue;
                }

                files.WriteAllBytes(edit.Path, edit.Original);
            }
            catch (Exception)
            {
                stuck.Add(edit.Path);
            }
        }

        return stuck;
    }
}
