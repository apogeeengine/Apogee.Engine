using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Weft.Content.FileSystem;
using Weft.Content.Import;
using Weft.Core.Assets;

namespace Weft.Content.Assets;

/// <summary>What <see cref="AssetDatabase.Move"/> did, or in a preview would do.</summary>
/// <param name="From">The asset's old path.</param>
/// <param name="To">Its new path.</param>
/// <param name="Files">The files that moved (or would): the asset, its <c>.import.xml</c> and its imported artifact, each by its new path.</param>
/// <param name="References">The documents whose references were rewritten, and the ones that could not be.</param>
/// <param name="Warnings">Things a person should look at: another asset reads this one by relative path, or the import data could not be updated.</param>
/// <param name="Applied">False for a preview: nothing was moved or written.</param>
public sealed record MoveReport(string From, string To, IReadOnlyList<string> Files, RewriteReport References, IReadOnlyList<string> Warnings, bool Applied);

public sealed partial class AssetDatabase
{
    private const string GameRoot = "res://";

    /// <summary>
    /// Moves or renames the asset at <paramref name="from"/> to <paramref name="to"/> (both in the game folder, <c>res://</c>) and keeps everything that names it working:
    /// <list type="bullet">
    /// <item>the file, its <c>.import.xml</c> (so the UID goes with it) and its imported artifact move together;</item>
    /// <item>the references in documents are rewritten (<see cref="AssetReferences.Rewrite"/>: scenes and the definition folders in <paramref name="defFolders"/>), keeping their UIDs;</item>
    /// <item><c>imported/index.xml</c> and <c>imported/assets.xml</c> say the new path. The artifact is still good (the import key does not depend on the path), so nothing imports again; an
    /// editor that wants to be sure can call <see cref="AssetImporter.Reimport"/>.</item>
    /// </list>
    /// All or nothing for what matters: the documents are checked and prepared before anything is touched, the files are then moved, and if that fails the documents and the files are put back. The
    /// import data is a cache; if it cannot be updated that is a warning and the next import repairs it. <c>.luau</c> scripts are never rewritten: the ones that mention the old path are listed in
    /// <see cref="RewriteReport.ScriptMentions"/>. Only a file can be moved here (a folder is moved by moving what is in it, one call each).
    /// </summary>
    /// <param name="from">The asset's path. It must be a file, and not an <c>.import.xml</c> (that follows its asset) or something in <c>imported/</c>.</param>
    /// <param name="to">The new path. Nothing may be there, neither a file nor a folder, and no <c>.import.xml</c>.</param>
    /// <param name="defFolders">Virtual folders whose <c>.xml</c> files are searched for references as well as the authored files.</param>
    /// <param name="dryRun">Report what would happen and change nothing.</param>
    /// <param name="allowSkipped">Go on although a writable document could not be used (not well-formed, not UTF-8, too big, cooked…) and its references to the asset will therefore break. By default that refuses the move before anything is written; see <see cref="SkippedDocument.Blocking"/>.</param>
    /// <exception cref="ContentException">Something is wrong with the request (a path, a read-only file, a target that exists), a writable document was skipped and <paramref name="allowSkipped"/> is false, a document changed while it was being prepared, or a write failed. Nothing is left half done: the message says if something could not be put back.</exception>
    public MoveReport Move(string from, string to, IReadOnlyList<string>? defFolders = null, bool dryRun = false, bool allowSkipped = false)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        Prepared move = AssetReferences.Guard("Preparing the move", () => Prepare(from, to, defFolders));
        if (dryRun)
        {
            return new MoveReport(move.Source, move.Target, move.Moved, move.Plan.Report, move.Warnings, Applied: false);
        }

        AssetReferences.RefuseSkipped(move.Plan.Report, allowSkipped);
        AssetReferences.Apply(files, move.Plan.Edits);
        MoveFiles(move.Moves, move.Plan);
        List<string> warnings = [.. move.Warnings, .. UpdateImportData(move.Source, move.Target, move.Entry, move.Artifact)];
        Refresh();
        return new MoveReport(move.Source, move.Target, move.Moved, move.Plan.Report with { Applied = true }, warnings, Applied: true);
    }

    /// <summary>Everything a move needs, worked out before the first write.</summary>
    private sealed record Prepared(string Source, string Target, List<(string From, string To)> Moves, ImportEntry? Entry, AssetReferences.ReferencePlan Plan, List<string> Warnings, string? Artifact, List<string> Moved);

    private Prepared Prepare(string from, string to, IReadOnlyList<string>? defFolders)
    {
        Refresh(); // the files are the truth, and the caller may have changed them since the last question
        (string source, string target) = AssetReferences.CheckMove(files, from, to);
        ImportIndex index = ImportIndex.Load(files);
        ImportEntry? entry = index.Find(source[GameRoot.Length..]);
        List<(string From, string To)> moves = MovesOf(source, target, entry is not null);

        AssetReferences.ReferencePlan plan = AssetReferences.Plan(this, UidForMove(source, target), source, target, defFolders, includeScripts: true);
        List<string> warnings = [.. ImportWarnings(index, entry, source, target), .. plan.Report.Unrewritable.Select(u => UnrewritableWarning(u, source))];
        string? artifact = entry is null ? null : MovedArtifact(entry, source, target);
        List<string> moved = [.. moves.Select(m => m.To)];
        if (entry is not null && artifact != entry.Artifact)
        {
            moved.Add(GameRoot + artifact);
        }

        return new Prepared(source, target, moves, entry, plan, warnings, artifact, moved);
    }

    // ---- The request ------------------------------------------------------------------------------------------------------------------

    // A package's own import cache, res://packages/id/imported/ (or dev_mods): the cache of a package that is imported as a folder of its own.
    private static bool InPackageCache(string relative)
    {
        foreach (string folder in s_packageFolders)
        {
            if (relative.StartsWith(folder, StringComparison.Ordinal))
            {
                int slash = relative.IndexOf('/', folder.Length);
                if (slash > 0 && relative.AsSpan(slash + 1).StartsWith(Import.AssetImporter.CacheFolder + "/", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsManifestPath(string path) => path == GameRoot + "game.xml" || VfsPath.FileName(path) == "mod.xml";

    // The UID of the asset being moved: from the file where it is (at the old path, or at the new one when it has moved), or from the .import.xml an interrupted move left behind.
    private AssetUid UidForMove(string source, string target)
    {
        AssetUid uid = UidOf(source);
        if (uid.IsNone)
        {
            uid = UidOf(target);
        }

        if (uid.IsNone && files.FileExists(source + ImportSidecar.Suffix))
        {
            uid = ImportSidecar.ReadUid(files.ReadAllText(source + ImportSidecar.Suffix));
        }

        return uid;
    }

    // The asset, and its .import.xml when it has one: each moves from the first path to the second. Checks everything that can be checked without changing a file.
    // A move that was interrupted after the asset moved (its .import.xml or its import data was left behind) is finished: only what is left is moved.
    private List<(string From, string To)> MovesOf(string source, string target, bool imported)
    {
        if (!source.StartsWith(GameRoot, StringComparison.Ordinal) || !target.StartsWith(GameRoot, StringComparison.Ordinal))
        {
            throw new ContentException($"Only assets of the game folder ({GameRoot}) can be moved: '{source}' to '{target}'. Packs and mods are read-only.");
        }

        if (source.EndsWith(ImportSidecar.Suffix, StringComparison.Ordinal) || target.EndsWith(ImportSidecar.Suffix, StringComparison.Ordinal))
        {
            throw new ContentException($"An {ImportSidecar.Suffix} follows its asset and cannot be moved on its own: move the asset.");
        }

        if (IsCacheOrHidden(source[GameRoot.Length..]) || IsCacheOrHidden(target[GameRoot.Length..]) || InPackageCache(source[GameRoot.Length..]) || InPackageCache(target[GameRoot.Length..]))
        {
            throw new ContentException($"'{source}' to '{target}': assets are not kept in the import cache ({Import.AssetImporter.CacheFolder}/, also a package's own) or in hidden folders.");
        }

        if (IsManifestPath(source) || IsManifestPath(target))
        {
            throw new ContentException($"'{source}' to '{target}': game.xml and mod.xml are read from where they are, not found by their path. They are not assets and are not moved here.");
        }

        for (string? above = VfsPath.ParentOf(target); above is not null; above = VfsPath.ParentOf(above))
        {
            if (above == source || files.FileExists(above))
            {
                throw new ContentException(above == source
                    ? $"'{target}' is inside '{source}', which is a file: nothing was moved."
                    : $"'{above}' is a file, so '{target}' cannot be made inside it: nothing was moved.");
            }
        }

        if (!files.FileExists(source))
        {
            if (files.FileExists(target) && (files.FileExists(source + ImportSidecar.Suffix) || imported))
            {
                return ResumedMoves(source, target);
            }

            throw new ContentException(files.DirectoryExists(source)
                ? $"'{source}' is a folder: move the files in it, one at a time."
                : $"There is no file at '{source}' to move (is it already at '{target}'?).");
        }

        var moves = new List<(string, string)> { (source, target) };
        if (files.FileExists(source + ImportSidecar.Suffix))
        {
            moves.Add((source + ImportSidecar.Suffix, target + ImportSidecar.Suffix));
        }

        foreach ((string moveFrom, string moveTo) in moves)
        {
            if (files.Exists(moveTo))
            {
                throw new ContentException($"'{moveTo}' already exists: nothing was moved.");
            }

            if (!files.CanWrite(moveFrom) || !files.CanWrite(moveTo))
            {
                throw new ContentException($"'{moveFrom}' or '{moveTo}' is in a read-only place: nothing was moved.");
            }
        }

        if (files.Exists(target + ImportSidecar.Suffix))
        {
            throw new ContentException($"'{target + ImportSidecar.Suffix}' is already there (left over from another asset): nothing was moved.");
        }

        return moves;
    }

    // What is left of a move whose asset is already at the target: its .import.xml, when that was left behind. Nothing else to move (the import data is brought up to date after).
    private List<(string From, string To)> ResumedMoves(string source, string target)
    {
        var moves = new List<(string, string)>();
        string sidecar = source + ImportSidecar.Suffix;
        if (files.FileExists(sidecar))
        {
            if (files.Exists(target + ImportSidecar.Suffix))
            {
                throw new ContentException($"'{sidecar}' is left over from an earlier move, but '{target + ImportSidecar.Suffix}' exists too: nothing was moved.");
            }

            if (!files.CanWrite(sidecar) || !files.CanWrite(target + ImportSidecar.Suffix))
            {
                throw new ContentException($"'{sidecar}' or '{target + ImportSidecar.Suffix}' is in a read-only place: nothing was moved.");
            }

            moves.Add((sidecar, target + ImportSidecar.Suffix));
        }

        return moves;
    }

    // Moves the files one after the other. If one fails, the ones moved go back (the last first) and then the documents are written as they were; the exception says what could not be undone.
    // The asset moves first, so it is the last to go back: if a file cannot be put back, the undoing stops there, the asset is still at the new path, and the documents (which name that path) are left
    // as they are. A document never names a file that did not move, and running the same move again finishes it.
    private void MoveFiles(List<(string From, string To)> moves, AssetReferences.ReferencePlan plan)
    {
        var done = new List<(string From, string To)>();
        try
        {
            foreach ((string from, string to) in moves)
            {
                files.Move(from, to);
                done.Add((from, to));
            }
        }
        catch (Exception e)
        {
            // Any exception: a source that is not a folder may throw anything.
            string? stoppedAt = null;
            foreach ((string from, string to) in Enumerable.Reverse(done))
            {
                try
                {
                    files.Move(to, from);
                }
                catch (Exception)
                {
                    stoppedAt = to;
                    break;
                }
            }

            if (stoppedAt is not null)
            {
                throw new ContentException(
                    $"Moving failed ({e.Message}) and '{stoppedAt}' could not be put back. The references name the new path, which the asset has: run the same move again to finish it, or restore the files from version control.", e);
            }

            List<string> stuck = AssetReferences.Restore(files, plan.Edits);
            throw new ContentException(stuck.Count == 0
                ? $"Moving failed ({e.Message}): the references and the files were put back, nothing was changed."
                : $"Moving failed ({e.Message}) and these files could not be put back, restore them from version control: {string.Join(", ", stuck)}.", e);
        }
    }

    // ---- The import data ----------------------------------------------------------------------------------------------------------------

    // Where the artifact goes: the importer names it imported/<source><extension>, so it follows its source. Null when the asset was not imported; an unusual name stays where it is.
    private static string? MovedArtifact(ImportEntry entry, string source, string target)
    {
        string oldPrefix = $"{Import.AssetImporter.CacheFolder}/{source[GameRoot.Length..]}";
        return entry.Artifact.StartsWith(oldPrefix, StringComparison.Ordinal)
            ? $"{Import.AssetImporter.CacheFolder}/{target[GameRoot.Length..]}{entry.Artifact[oldPrefix.Length..]}"
            : entry.Artifact;
    }

    // A place that names the moved file and that nothing here rewrites: the manifests' paths, and text inside an element.
    private static string UnrewritableWarning(AssetReferenceSite site, string source)
    {
        string where = site.Line > 0 ? $"{site.Document}:{site.Line}" : site.Document;
        return site.Attribute.Length > 0
            ? $"{where}: <{site.Element} {site.Attribute}> names {source}, which moved; edit it by hand, it is not rewritten."
            : $"{where}: the text of <{site.Element}> names {source}, which moved; edit it by hand, element text is not rewritten.";
    }

    // Other assets that read this one (a glTF's image) will not find it under the old path; and an asset that reads others by relative path may not find them from its new folder.
    private static List<string> ImportWarnings(ImportIndex index, ImportEntry? entry, string source, string target)
    {
        string oldRelative = source[GameRoot.Length..];
        List<string> warnings =
        [
            .. index.Entries
                .Where(e => e.Source != oldRelative && e.Dependencies.Contains(oldRelative))
                .Select(e => $"{e.Source} reads {oldRelative}, which moved: it is out of date and may fail to import until it names the new path."),
        ];

        if (entry is { Dependencies.Count: > 0 } && VfsPath.DirectoryOf(source) != VfsPath.DirectoryOf(target))
        {
            warnings.Add($"{oldRelative} reads {entry.Dependencies.Count} other file(s) by relative path (a glTF's buffers and images): from its new folder it may not find them. Move them with it.");
        }

        return warnings;
    }

    // Updates imported/index.xml and imported/assets.xml for the move. They are caches that the next import rebuilds, so a failure is a warning and not an error.
    private List<string> UpdateImportData(string source, string target, ImportEntry? entry, string? artifact)
    {
        try
        {
            if (entry is not null)
            {
                MoveArtifact(entry, artifact!);
                RenameEntry(entry, source[GameRoot.Length..], target[GameRoot.Length..], artifact!);
            }

            RenameListRow(source, target);
            return [];
        }
        catch (Exception e)
        {
            // Whatever it was: the files are moved and the documents say so, and this is only the cache.
            return [$"The import data could not be updated ({e.Message}): import the project again to repair it."];
        }
    }

    private void MoveArtifact(ImportEntry entry, string artifact)
    {
        if (artifact == entry.Artifact || !files.FileExists(GameRoot + entry.Artifact))
        {
            return;
        }

        if (files.FileExists(GameRoot + artifact))
        {
            files.Delete(GameRoot + artifact); // an artifact left over from an earlier asset with that name; the one that moves is the good one
        }

        files.Move(GameRoot + entry.Artifact, GameRoot + artifact);
    }

    private void RenameEntry(ImportEntry entry, string oldRelative, string newRelative, string artifact)
    {
        ImportIndex index = ImportIndex.Load(files);
        var renamed = new ImportEntry(newRelative, artifact, entry.Key, entry.Dependencies);
        ImportIndex.Save(files, index.Entries.Where(e => e.Source != oldRelative).Append(renamed).OrderBy(e => e.Source, StringComparer.Ordinal));
    }

    // The project's list of UIDs is only changed if there is one; a project that was never imported does not get one from a move.
    private void RenameListRow(string source, string target)
    {
        if (!files.FileExists(GameRoot + Import.AssetImporter.CacheFolder + "/" + ListFileName))
        {
            return;
        }

        Dictionary<string, AssetUid> rows = Load(files);
        if (rows.Remove(source, out AssetUid uid))
        {
            rows[target] = uid;
            Save(files, rows);
        }
    }
}
