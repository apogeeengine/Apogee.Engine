using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Content.Import;
using Weft.Content.Packages;
using Weft.Content.Xml;

namespace Weft.Content.Packs;

/// <summary>What <see cref="GamePacker"/> packs, and how.</summary>
public sealed record PackRequest
{
    /// <summary>The game folder: it is <c>res://</c>, and the paths in the pack are relative to it. Names that start with a dot are left out.</summary>
    public required string Folder { get; init; }

    /// <summary>The first part of the pack (<c>game.wpak</c>); a bigger pack goes on in <c>game_001.wpak</c>… beside it.</summary>
    public required string Output { get; init; }

    /// <summary>
    /// Pack what a shipped game needs: the imported artifacts instead of the sources, their settings and the files they were made from, and authored documents (<see cref="AuthoredAssets.Extensions"/>)
    /// and the UID list <c>imported/assets.xml</c> in their cooked form (<see cref="XmlBinary"/>) at the paths they have in the folder, and so are the definitions (the <c>.xml</c> files) in the content folders
    /// of the base packages (<c>packages/&lt;id&gt;/</c>, as their <c>mod.wmod</c> lists them). The manifests (<c>mod.wmod</c>), schemas and sidecars stay text. It needs the folder to have been imported (<c>imported/index.xml</c>,
    /// see <see cref="AssetImporter"/>) unless <see cref="AllowNothingImported"/> says otherwise. Nothing under <c>dev_mods/</c> is cooked.
    /// </summary>
    public bool Cooked { get; init; }

    /// <summary>
    /// With <see cref="Cooked"/>: a folder with nothing imported (no assets that need an importer, so no <c>imported/index.xml</c>) is packed with nothing dropped, instead of being an error. An export sets
    /// this, since a game of scripts and data has nothing to import.
    /// </summary>
    public bool AllowNothingImported { get; init; }

    /// <summary>Encrypts the pack (<see cref="PackKey.FromEnvironment"/> is for the caller to ask: the packer never reads the environment).</summary>
    public PackKey? Key { get; init; }

    /// <summary>
    /// Makes a patch pack: these packs (in order, with the same <see cref="Key"/>) are what the game has already, and the new pack holds the files that are new or changed, and a removal for each file that is gone.
    /// </summary>
    public IReadOnlyList<string> Bases { get; init; } = [];

    /// <summary>A part grows to about this many bytes before the next file starts a new one.</summary>
    public long SplitBytes { get; init; } = 1536L * 1024 * 1024;

    /// <summary>The zstd level, 1 (fast) to 22 (small).</summary>
    public int CompressionLevel { get; init; } = 12;

    /// <summary>
    /// Folder-relative prefixes of what to leave out, compared as plain text and case-sensitively (<c>dev_mods/</c> leaves out that folder; <c>dev_mods</c> would also leave out <c>dev_mods2/</c>). Both
    /// slashes work. They are applied to the files in the folder, so a patch pack with a prefix also removes what an earlier pack held under it.
    /// </summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];
}

/// <summary>What <see cref="GamePacker.Pack"/> did.</summary>
/// <param name="Added">Files written to the pack (all of them, or in a patch pack the new and changed ones).</param>
/// <param name="Unchanged">Files a patch pack left to its base packs because they are the same.</param>
/// <param name="Removed">Files a patch pack takes away from its base packs.</param>
/// <param name="CookedDocuments">How many of the added files are cooked XML documents (authored assets and the UID list).</param>
/// <param name="RawBytes">The size of the added files before compression.</param>
/// <param name="PackedBytes">The size of the parts on disk.</param>
/// <param name="Parts">The part files written, the first one first.</param>
public sealed record PackReport(int Added, int Unchanged, int Removed, int CookedDocuments, long RawBytes, long PackedBytes, IReadOnlyList<string> Parts);

/// <summary>
/// Packs a game folder into a pack (see <see cref="PackFormat"/>): the library behind <c>Weft.Runtime --pack</c>, and what an export calls. The sources of imported assets are dropped from a cooked pack,
/// authored XML and the definitions of the base packages are cooked in place, and a patch pack holds only what changed against its bases.
/// </summary>
public static class GamePacker
{
    private const string Root = "res://";

    /// <summary>The folder whose files are never cooked: dev mods are loose files, read as they are.</summary>
    private const string DevMods = "dev_mods/";

    /// <summary>The folder of the base packages in the game folder (<see cref="PackageOptions.BasePackagesPath"/>).</summary>
    private const string BasePackages = "packages/";

    /// <summary>Writes the pack.</summary>
    /// <exception cref="ContentException">The folder is not a folder; <see cref="PackRequest.Cooked"/> and nothing is imported; a path cannot be in a pack; an authored document is not well-formed.</exception>
    /// <exception cref="IOException">Reading or writing failed.</exception>
    /// <exception cref="ArgumentException">A base pack already has a part of the same path, an <see cref="PackRequest.Exclude"/> entry is empty, or the compression level is not 1 to 22.</exception>
    public static PackReport Pack(PackRequest request)
    {
        Plan plan = Plan.Make(request);
        var existing = new VirtualFileSystem();
        var opened = new List<PackSource>();
        try
        {
            foreach (string basePack in request.Bases)
            {
                PackSource source = PackSource.Open(basePack, request.Key);
                opened.Add(source);
                existing.Mount(Root, source);
            }

            using var writer = new PackWriter(request.Output, new PackWriterOptions { Key = request.Key, SplitBytes = request.SplitBytes, CompressionLevel = request.CompressionLevel });
            long raw = 0;
            int added = 0;
            int unchanged = 0;
            int cooked = 0;
            foreach (string path in plan.Paths)
            {
                byte[] data = File.ReadAllBytes(Path.Combine(plan.Folder, path));
                bool cook = plan.IsCooked(path);
                if (cook)
                {
                    data = XmlBinary.Write(XmlFile.Read(data, path));
                }

                if (request.Bases.Count > 0 && existing.FileExists(Root + path) && existing.ReadAllBytes(Root + path).AsSpan().SequenceEqual(data))
                {
                    unchanged++;
                    continue;
                }

                writer.Add(path, data);
                raw += data.Length;
                added++;
                if (cook)
                {
                    cooked++;
                }
            }

            int removed = 0;
            if (request.Bases.Count > 0)
            {
                var present = new HashSet<string>(plan.Paths, StringComparer.Ordinal);
                foreach (string gone in existing.EnumerateFiles(Root, recursive: true).Select(p => p[Root.Length..]).Where(p => !present.Contains(p)))
                {
                    writer.Remove(gone);
                    removed++;
                }
            }

            IReadOnlyList<string> parts = writer.Finish();
            return new PackReport(added, unchanged, removed, cooked, raw, parts.Sum(p => new FileInfo(p).Length), parts);
        }
        finally
        {
            foreach (PackSource source in opened)
            {
                source.Dispose();
            }
        }
    }

    /// <summary>
    /// The folder-relative paths <see cref="Pack"/> would put in the pack (a patch pack may hold fewer, since it leaves out what its bases have), in ordinal order: what is in the folder, less the names that
    /// start with a dot, <see cref="PackRequest.Exclude"/>, and when <see cref="PackRequest.Cooked"/> what the imported artifacts replace. A cooked document is at the same path as its source.
    /// </summary>
    /// <exception cref="ContentException">As for <see cref="Pack"/>, except for the documents, which this does not read.</exception>
    public static IReadOnlyList<string> PathsOf(PackRequest request) => Plan.Make(request).Paths;

    private sealed class Plan
    {
        public required string Folder { get; init; }

        public required IReadOnlyList<string> Paths { get; init; }

        private bool Cooked { get; init; }

        /// <summary>The definition files of the base packages: cooked when <see cref="Cooked"/>.</summary>
        private HashSet<string> Definitions { get; init; } = [];

        /// <summary>Whether the file at <paramref name="path"/> is written as binary XML.</summary>
        public bool IsCooked(string path) =>
            Cooked && !path.StartsWith(DevMods, StringComparison.Ordinal)
            && (AuthoredAssets.IsAuthored(path) || path == AssetImporter.CacheFolder + "/" + AssetDatabase.ListFileName || Definitions.Contains(path));

        public static Plan Make(PackRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            string folder = Path.GetFullPath(request.Folder);
            if (!Directory.Exists(folder))
            {
                throw new ContentException($"{folder} is not a folder.");
            }

            string[] exclude = [.. request.Exclude.Select(Prefix)];
            List<string> paths = [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
                .Where(p => !p.Split('/').Any(segment => segment.StartsWith('.')) && !exclude.Any(x => p.StartsWith(x, StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal)];
            if (request.Cooked)
            {
                var project = new VirtualFileSystem();
                project.Mount(Root, new DirectorySource(folder));
                IReadOnlyList<ImportEntry> entries = AssetImporter.LoadIndex(project);
                if (entries.Count == 0 && !request.AllowNothingImported)
                {
                    throw new ContentException($"{folder} has nothing imported (no imported/index.xml): run --import first.");
                }

                // What an artifact replaces (the asset, the files it was made from, its settings) is left out.
                var replaced = new HashSet<string>(StringComparer.Ordinal);
                foreach (ImportEntry entry in entries)
                {
                    replaced.Add(entry.Source);
                    replaced.Add(entry.Source + ImportSidecar.Suffix);
                    replaced.UnionWith(entry.Dependencies);
                }

                paths = [.. paths.Where(p => !replaced.Contains(p))];
            }

            List<string> problems = [];
            foreach (string path in paths)
            {
                if (!VfsPath.TryParse("pack://" + path, out _, out string? problem))
                {
                    problems.Add($"{path}: {problem}");
                }
            }

            if (problems.Count > 0)
            {
                throw new ContentException(string.Join(Environment.NewLine, problems));
            }

            return new Plan { Folder = folder, Paths = paths, Cooked = request.Cooked, Definitions = request.Cooked ? DefinitionsOf(folder, paths) : new HashSet<string>() };
        }

        /// <summary>
        /// The <c>.xml</c> files in the content folders (<c>&lt;Content path="…"/&gt;</c>) of each base package whose <c>mod.wmod</c> is among <paramref name="paths"/> and reads: what a
        /// <see cref="DefDatabase"/> loads from it. The manifest itself and the sidecars are never among them. A manifest that does not read is the game's error when it runs, so its package is packed as it is.
        /// </summary>
        private static HashSet<string> DefinitionsOf(string folder, List<string> paths)
        {
            string manifestName = PackageManifest.FileName;
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (string manifestPath in paths.Where(p => p.StartsWith(BasePackages, StringComparison.Ordinal) && p.EndsWith("/" + manifestName, StringComparison.Ordinal) && p.IndexOf('/', BasePackages.Length) == p.Length - manifestName.Length - 1))
            {
                if (!PackageManifest.TryParse(File.ReadAllText(Path.Combine(folder, manifestPath)), manifestPath, out PackageManifest? manifest, out _))
                {
                    continue;
                }

                string package = manifestPath[..^manifestName.Length];
                foreach (string content in manifest!.ContentFolders)
                {
                    string prefix = package + content + "/";
                    foreach (string path in paths.Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(path);
                    }
                }
            }

            return found;
        }

        private static string Prefix(string prefix)
        {
            string normal = prefix.Replace('\\', '/').TrimStart('/');
            return normal.Length > 0 ? normal : throw new ArgumentException("An excluded prefix cannot be empty: it would leave out everything.", nameof(prefix));
        }
    }
}
