using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Apogee.Content.FileSystem;
using Apogee.Core.Assets;

namespace Apogee.Content.Assets;

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
public sealed record SkippedDocument(string Document, string Reason);

/// <summary>What <see cref="AssetReferences.Rewrite"/> did, or in a preview would do.</summary>
/// <param name="Changes">The attributes that are rewritten to the new path (a reference that carries a UID keeps it).</param>
/// <param name="Unchanged">References to the asset that need no change: a <c>uid://</c> alone follows the file by itself, and a reference that already says the new path.</param>
/// <param name="ScriptMentions">Script strings that mention the asset (a path, or its UID). They are reported and never rewritten: a script is code, and a string in it may be built or compared.</param>
/// <param name="Skipped">The documents that were not used, each with the reason. A document in a read-only root that refers to the asset is here: its references were found and not rewritten. A file that is not well-formed is here too; it may or may not have held references.</param>
/// <param name="Documents">The files that were changed (in a preview, that would be).</param>
/// <param name="Applied">False for a preview: nothing was written.</param>
public sealed record RewriteReport(
    IReadOnlyList<ReferenceChange> Changes,
    IReadOnlyList<AssetReferenceSite> Unchanged,
    IReadOnlyList<AssetReferenceSite> ScriptMentions,
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
        List<string> paths = target.HasPath && VfsPath.TryParse(target.Path, out string canonical, out _) ? AssetDatabase.SpellingsOf(canonical) : [];
        var matcher = new ReferenceSearch.Matcher(assets.Files, target.Uid, paths);
        return [.. ReferenceSearch.Search(assets, matcher, defFolders, includeScripts).Where(d => d.Problem is null).SelectMany(d => d.Hits).Select(h => h.Site)];
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
    /// <exception cref="ContentException">A path is not valid, they are the same, neither or both have a file, a file changed while the references were being prepared, or a write failed (the files already written were put back, and the message says so; if one could not be put back it is named).</exception>
    public static RewriteReport Rewrite(AssetDatabase assets, string oldPath, string newPath, IReadOnlyList<string>? defFolders = null, bool dryRun = false, bool includeScripts = true)
    {
        ArgumentNullException.ThrowIfNull(assets);
        assets.Refresh();
        (string from, string to) = CheckMove(assets.Files, oldPath, newPath);
        AssetUid uid = assets.UidOf(from);
        if (uid.IsNone)
        {
            uid = assets.UidOf(to);
        }

        ReferencePlan plan = Plan(assets, uid, from, to, defFolders, includeScripts);
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

        bool oldExists = files.FileExists(from);
        bool newExists = files.FileExists(to);
        if (oldExists && newExists)
        {
            throw new ContentException($"Both '{from}' and '{to}' exist: renaming would make two assets one.");
        }

        if (!oldExists && !newExists)
        {
            throw new ContentException($"There is no file at '{from}' or at '{to}'.");
        }

        return (from, to);
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
    internal static ReferencePlan Plan(AssetDatabase assets, AssetUid uid, string from, string to, IReadOnlyList<string>? defFolders, bool includeScripts)
    {
        var matcher = new ReferenceSearch.Matcher(assets.Files, uid, [.. AssetDatabase.SpellingsOf(from), .. AssetDatabase.SpellingsOf(to)]);
        var plan = new PlanBuilder(assets.Files, to);
        List<string> newSpellings = AssetDatabase.SpellingsOf(to);
        foreach (ReferenceSearch.DocumentResult document in ReferenceSearch.Search(assets, matcher, defFolders, includeScripts))
        {
            if (document.Problem is not null)
            {
                plan.Skipped.Add(new SkippedDocument(document.Path, document.Problem));
            }
            else if (document.IsScript)
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

    private sealed class PlanBuilder(VirtualFileSystem files, string newPath)
    {
        public List<ReferenceChange> Changes { get; } = [];

        public List<AssetReferenceSite> Unchanged { get; } = [];

        public List<AssetReferenceSite> Mentions { get; } = [];

        public List<SkippedDocument> Skipped { get; } = [];

        private readonly List<DocumentEdit> _edits = [];

        public ReferencePlan Build() =>
            new(new RewriteReport(Changes, Unchanged, Mentions, Skipped, [.. _edits.Select(e => e.Path)], Applied: false), _edits);

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
                newValues[hit.Ordinal] = updated.ToString();
            }

            if (changes.Count == 0)
            {
                return;
            }

            if (Unwritable(document, changes.Count) is { } reason)
            {
                Skipped.Add(new SkippedDocument(document.Path, reason));
                return;
            }

            try
            {
                string text = ReferenceText.Replace(document.Text!, document.Tree!, newValues, document.Path);
                _edits.Add(new DocumentEdit(document.Path, document.Bytes!, new UTF8Encoding(false).GetBytes(text)));
                Changes.AddRange(changes);
            }
            catch (ContentException e)
            {
                Skipped.Add(new SkippedDocument(document.Path, e.Message));
            }
        }

        private string? Unwritable(ReferenceSearch.DocumentResult document, int count)
        {
            if (!files.CanWrite(document.Path))
            {
                return $"read-only: its {count} reference(s) to the asset were found and not rewritten";
            }

            return document.Text is null ? $"cooked: its {count} reference(s) to the asset cannot be rewritten in place; cook it again from the edited source" : null;
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
    /// Writes every edit, or none. First every file is read again and has to be what the plan was made from (something saved it in between: stop, nothing is written); then the files are written one
    /// by one (each write puts the new file in place whole), and if one fails the ones already written get their old bytes back.
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
        try
        {
            foreach (DocumentEdit edit in edits)
            {
                touched.Add(edit); // before the write: a write that fails half way is put back too
                files.WriteAllBytes(edit.Path, edit.Updated);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            List<string> stuck = Restore(files, touched);
            throw new ContentException(stuck.Count == 0
                ? $"Writing the references failed ({e.Message}): the files written so far were put back, nothing was changed."
                : $"Writing the references failed ({e.Message}) and these files could not be put back, restore them from version control: {string.Join(", ", stuck)}.", e);
        }
    }

    private static bool IsUnchanged(VirtualFileSystem files, DocumentEdit edit)
    {
        try
        {
            return files.ReadAllBytes(edit.Path).AsSpan().SequenceEqual(edit.Original);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Writes the original bytes of every edit back; returns the paths that could not be.</summary>
    internal static List<string> Restore(VirtualFileSystem files, IReadOnlyList<DocumentEdit> edits)
    {
        var stuck = new List<string>();
        foreach (DocumentEdit edit in edits.Reverse())
        {
            try
            {
                files.WriteAllBytes(edit.Path, edit.Original);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                stuck.Add(edit.Path);
            }
        }

        return stuck;
    }
}
