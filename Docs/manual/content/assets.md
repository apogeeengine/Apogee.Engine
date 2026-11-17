# Asset identity: paths and UIDs

An asset is named by its **path** (`res://models/car.glb`), which people can read, and has a **UID** (`uid://4k2m9x7q1w3ze`), which stays the same when the file is renamed or moved. A reference holds both, so
that a path which no longer leads to a file can be followed by its UID to where the file went.

```
res://models/car.glb|uid://4k2m9x7q1w3ze
```

The path is tried first and wins while its file exists. When it does not, the UID finds the file (and the engine logs that the reference is out of date, so that whoever saves the document that holds it
writes the new path).

## The values

`AssetUid` and `AssetRef` are in `Apogee.Core.Assets`.

- **`AssetUid`** is 64 random bits, never zero (zero is `AssetUid.None`, "no UID"). Its text is `uid://` and 13 characters of lower-case Crockford base32 (`0-9 a-h j k m n p-t v-z`, no `i l o u`): `uid://[0-9a-hjkmnp-tv-z]{13}`.
  Parsing ignores case and refuses anything else, including all zeros.
- **`AssetRef`** is a path and a UID. Its text is `path|uid://…`; a path alone and `uid://…` alone are valid too. A `|` cannot be part of a path, so the text splits without doubt, and a reference moves through
  an XML attribute or a Luau string as one value: a patch that replaces the path replaces the UID with it.

## Where a UID lives

| Asset | UID in | Example |
| --- | --- | --- |
| An imported file (`.glb`, `.png`, `.slang`…) | the `uid` attribute of its [`<asset>.import.xml`](import.md#settings) | `<Import uid="uid://4k2m9x7q1w3ze" srgb="false"/>` |
| An authored file (`.ascene`, `.aprefab`) | the `uid` attribute of the document's root | `<Scene version="1" uid="uid://…">`, `<Prefab version="1" uid="uid://…" nextId="4">` |
| The whole project | `imported/assets.xml`, a list written by the import | `<Asset uid="uid://…" path="res://models/car.glb"/>` |

`AuthoredAssets.Extensions` lists the authored kinds: [scenes](scenes.md) and [prefabs](prefabs.md). The sidecar and the document are the truth; `assets.xml` is a copy that makes lookups fast. It is rewritten by every import (only when its text changes),
sorted by path, and a pack carries it, so a shipped game finds a UID without scanning. The paths in it are written as the folder looked when it was imported (`res://…`); a package that was imported on its
own and is mounted at `mod://id/` is read with its paths under `mod://id/`. In a `--pack --cooked` pack the list is [cooked XML](import.md#cooked-xml) and is the only record of the imported assets' UIDs
(their sidecars and sources are left out), so `AssetDatabase` believes a cooked list without looking for the files.

## When a UID is made

- **`Apogee.Runtime --import`** (`AssetImporter.Run`) gives a UID to every imported file that has none, by creating or editing its `.import.xml`, and to every authored file that has none, by adding the attribute to its root.
  Only those two attributes are written: comments, indentation, quote style, line endings and a byte order mark in the file stay as they were, and an attribute that is already there is never changed.
  The editor's single-asset `Reimport` does the same for its asset.
- **Never at run time, except when a scene is saved.** A game, a mod, a pack or a read-only folder is never written to by the engine. Their assets without a UID simply have none, and a reference to them is by path.
- A file that is not XML (a damaged document) has nowhere to hold a UID and is left alone.

Something that rewrites a `.import.xml` (a settings panel) must keep the UID: `ImportSidecar.SetUid(newText, ImportSidecar.ReadUid(oldText))`. Likewise `AuthoredAssets.SetUid` for a document that is saved
over an existing one. `World.SaveSceneFile` does this for scenes: it mints a UID for a new file and keeps the one an existing file has (see [scene files](scenes.md#scene-files-and-their-uid)), and a scene that is saved
from the editor is therefore an authored asset with a UID from its first save, without waiting for an import.

### The UID does not change the import key

An artifact is imported again when its [key](import.md#when-something-is-imported-again) changes, and the key hashes the text of the `.import.xml`. A UID is written into that text after the first import, so the key
is made from `ImportSettings.KeyText(text)`: the same text without the root's `uid` attribute (and a sidecar that holds only a UID counts as no sidecar). Minting or changing a UID therefore imports nothing again, and
the keys made before UIDs existed are still valid. Changing a real setting still imports the asset again.

## Moves, deleted sidecars and copies

- **Rename or move** an asset **together with its `.import.xml`**: the UID goes with it, and every reference that has it still finds the file (`AssetDatabase.Resolve`).
- **Move without the sidecar:** the file is a new asset with a new UID, and references to the old UID cannot follow. The import says so in the log when UIDs disappeared in a run that made new ones.
- **Delete the sidecar** (and leave the file): the next import makes it again with the **same** UID, from `assets.xml`.
- **Copy an asset with its sidecar:** two files now have one UID. The import keeps the UID on the original (the path `assets.xml` recorded for it, or the first path in order when there is no record) and gives
  the others a new one, and lists them in `ImportReport.Reassigned`. A copy made on a root that cannot be written to keeps the shared UID with a warning; the UID then names the original.

## From C#

```csharp
var assets = new AssetDatabase(files);        // or new ContentLoader(files).Assets
assets.Roots.Add("mod://cool/");               // res:// is the default; add each mounted package, in load order

AssetRef car = assets.RefOf("res://models/car.glb");          // path and UID
string? where = assets.Resolve(car);                           // the path if the file is there, else wherever the UID's file is, else null
string path = assets.ResolvePath("uid://4k2m9x7q1w3ze");      // a uid:// becomes a path; a path is returned as it is; an unknown uid is a ContentException
AssetUid uid = assets.UidOf("res://textures/ground.png");
```

- The database reads the files and never writes. To find a UID it reads each root's `imported/assets.xml`, checks that the file it names is still there and still has that UID, and otherwise scans the files of its roots
  once. It remembers what it read until `Refresh()` (`ContentLoader.Refresh()` calls it, after an import).
- When two files have the same UID, the one `assets.xml` names wins, or else the first root in the list (load order, for mods), and `Duplicates` lists the group.
- **Package paths:** a package under `res://packages/<id>/` (or `res://dev_mods/<id>/`) is also `mod://<id>/`. If `mod://<id>/` is one of the roots, `PathOf` answers with the `mod://` address, the one the rest
  of the engine uses, and the scan does not count the files twice.
- `ContentLoader.ResolveAssetPath(text)` is what a loader that takes "a path or a `uid://`" calls first.

## Renaming and moving assets (for tools)

An editor that renames or moves an asset has two jobs: move the file with its identity, and make the documents that name it say the new path. A reference that carries a UID keeps working
without the second job (the UID finds the file, and the engine logs that the path is out of date), but a path-only reference does not, and out-of-date paths are noise. `AssetDatabase.Move` does both;
`AssetReferences` has the pieces.

```csharp
MoveReport preview = assets.Move("res://models/car.glb", "res://vehicles/truck.glb", defFolders: ["res://data"], dryRun: true);
MoveReport done    = assets.Move("res://models/car.glb", "res://vehicles/truck.glb", defFolders: ["res://data"]);

IReadOnlyList<AssetReferenceSite> uses = AssetReferences.Find(assets, assets.RefOf("res://models/car.glb"), ["res://data"], includeScripts: true);
RewriteReport report = AssetReferences.Rewrite(assets, "res://models/car.glb", "res://vehicles/truck.glb", ["res://data"], dryRun: true);
```

### What is searched

- **Documents:** every authored file (`AuthoredAssets.Extensions`: scenes and prefabs) in the roots of the database, and every `.xml` file under the `defFolders` you name (definitions). Sidecars (`.import.xml`) hold settings,
  not references, and are not searched; they move with their asset.
- **References:** an attribute whose whole text is an [`AssetRef`](#the-values). Text inside elements is not rewritten (it is reported, see below).
- **What counts as the asset:** an attribute whose path is the asset's path, or whose UID is the asset's UID and whose path does not lead to some *other* existing file. The second condition is the rule
  of `Resolve` (the path wins while its file is there): a reference that carries the UID of a file and the path of its copy means the copy, and renaming the original must not touch it. When a UID is on two
  files (a copy that was not imported yet), the engine resolves it to the one `PathOf` names; renaming the *other* file does not take a stale-path reference with that UID, only the references that spell its own path.
- **Package files:** the two addresses of a package file, `res://packages/id/x` and `mod://id/x`, are the same path, and a rewritten reference keeps the form it was written in. A `mod://id/x` is searched
  and rewritten as both `res://packages/id/x` and `res://dev_mods/id/x` (the mount does not say which folder backs it): if the same id exists in both folders, their references are treated alike.
- **Scripts:** with `includeScripts` (always, in `Rewrite` and `Move`) the `.luau` files that spell the old path (or, in `Find`, the UID) are listed with an empty `Attribute`. They are **reported and never rewritten**: a
  string in code may be built, compared or logged. A `uid://` in a script follows the file and is not reported by `Rewrite`. The search is linear in the size of the script, and at most 10,000 mentions are listed per script.
- **Paths that are not attribute references:** `RewriteReport.Unrewritable` lists, and `Move` adds a warning for each, the places that name the old path and are **not rewritten**: `<EntryScene path>` and `<Icon path>`
  in `game.xml`, `<Script path>` in a package's `mod.xml` (relative paths there; the site's value is the full path), and the text inside an element of a document (`<Model>res://models/car.glb</Model>`; `Attribute` is
  empty). Edit them by hand; they break if the file moves. A path in a `.rml` or `.rcss` file is not looked at.

### What is written

`Rewrite` changes the path part of each matching reference and nothing else. It edits the **text** of the file, so comments, indentation, quote style, line endings, a byte order mark and the order of
every other attribute stay exactly as they were (a scene is under version control, and a diff of a rename should show the renamed paths). Everything from the `|` on stays character for character: a UID written
in capitals is still in capitals. The UID is never changed; a UID alone has nothing to rewrite and
is listed in `Unchanged`, as is a reference that already says the new path. It may be called before the file moves or after (the UID is read from whichever path has the file). A scene that is a **symbolic
link** is written through: the file it leads to is rewritten and the link stays. A file with several **hard links** is replaced by a new file, and the other names keep the old text.

Each edit is checked twice: before it, the attributes found in the text must be the attributes of the parsed document, in the same order with the same names, and a value to change must decode to what the
parse says; after it, the new text is parsed again and must be the old document with only the chosen values changed (same elements, text and attributes). A file that fails a check is skipped.

Every file is written the same way (`DirectorySource`): the new bytes go to a hidden file beside it with a unique name, are flushed to the disk, and are then renamed over the file, so a crash or a full disk leaves
the old file whole and never an empty one. The file keeps its permissions.

### All or nothing

1. Every new text is made and checked in memory first. Nothing is written if the request is wrong: the same path, a missing file, both paths existing, a folder as either path, and for `Move` a target inside the source or inside
   a file, `game.xml` and `mod.xml`, and a package's own `imported/`. A rename that only changes the case of a name on a file system that ignores case is refused with that explanation (paths are compared with their case: do it in two steps).
2. Files that cannot be used are **skipped with a reason** in `RewriteReport.Skipped`, never half-written: unreadable, larger than `AssetReferences.MaxDocumentBytes` (64 MB), naming the asset more than
   `AssetReferences.MaxReferencesPerDocument` (100,000) times, over the memory a rewrite may take (`MaxPlannedBytes`, 256 MB for the old and the new text of all the documents), not valid UTF-8 (the text is
   written back, so a replaced byte would be damage; a file that cannot be read as UTF-8 and does not name the asset is no concern), not well-formed XML or with a document type, a cooked document (rewritten from its edited
   source instead), and anything in a read-only root such as a pack or a mod (its references are found, and not rewritten; their UIDs still find the asset).
3. **A skipped document that could have been written refuses the rename.** Its references to the asset would break silently, so `Rewrite` and `Move` throw a `ContentException` before any write or move (a
   `dryRun` does not throw: its `Skipped` list is what to show the user, with `SkippedDocument.Blocking` true for these). Pass `allowSkipped: true` to go on after the user agreed. Documents in read-only roots never
   block: nothing could have rewritten them.
4. Before the first write every file is read again and must be what the plan was made from: a scene the editor saved in between stops the rename, with nothing written. Then the files are written one by one,
   each checked again just before its own write (the editor can save while the earlier ones are being written). If a check fails or a write fails, **whatever the exception**, the ones already written get their old
   bytes back, but only a file that still holds what was written: one saved since is left as the person saved it and named in the error (`saved since, left as it is`), never overwritten with older text. A file that
   cannot be put back is named in the error.
5. `dryRun: true` runs all of it except the writes and returns the same report with `Applied` false: use it for a confirmation dialog.
6. Everything that can go wrong before the first write (a folder that cannot be listed or that is deleted during the scan, a file that cannot be opened, a source of your own that throws) comes out of `Find`,
   `Rewrite` and `Move` as a `ContentException` that says nothing was changed; only a null argument is an `ArgumentNullException`. Listing the project is bounded (24 folders deep, 100,000 folders), so a link that
   leads back to a folder above it ends in an error, not in a tree without end.

### Move

`AssetDatabase.Move(from, to)` is for a file in the game folder (`res://`; a pack or a mod is read-only). It refuses a folder (move the files in it, one call each), an `.import.xml` (it follows its asset), the
`imported/` cache (also a package's own) and hidden folders, `game.xml` and `mod.xml`, and a target that exists. Then, in this order: the references are rewritten, the file and its `.import.xml` move (so the UID goes with it), the imported artifact moves
to the name the importer would give it, and `imported/index.xml` and `imported/assets.xml` say the new path. If a file cannot move, the files already moved (the last first) and then the documents are put back. If a
moved file cannot be put back, the undoing stops there with the asset at its new path, where the documents (left as they are) name it: a document never names a file that did not move. The import data is
a cache: if it cannot be updated that is a warning, and the next import repairs it. The artifact stays valid, because the import key does not depend on the path: nothing imports again (call
`AssetImporter.Reimport` if you want to be sure). `MoveReport.Warnings` also says when another asset reads the moved one by path (a glTF's image), or the moved asset reads others by relative path and
changed folder: those imports can fail until their files are updated; and one line for each place in `RewriteReport.Unrewritable`.

**Running the same `Move` again finishes an interrupted one.** If the process died, or an error left things half done, after the documents were rewritten (the references then already say the new path and are listed
as `Unchanged`) or after the asset moved but before its `.import.xml` and import data did, call `Move(from, to)` with the same arguments: it moves what is left and brings the import data up to date.

## From Luau

Everything that loads an asset by path takes a `uid://` as well: `models:load("uid://4k2m9x7q1w3ze")` gives the same id as `models:load("res://models/car.glb")` when that UID names that file, and it follows the file
if it was moved. A `uid://` that no file has is an error. The global `assets` (`AssetLibrary`) is for asking about identity:

```lua
assets:uid_of("res://models/car.glb")            -- "uid://4k2m9x7q1w3ze", or nil when the file does not exist or has none
assets:ref_of("res://models/car.glb")            -- "res://models/car.glb|uid://4k2m9x7q1w3ze": what to store to find it again
assets:resolve("uid://4k2m9x7q1w3ze")            -- "res://models/car.glb"
assets:resolve("res://old/car.glb|uid://4k2m9x7q1w3ze")  -- the path if its file is there, otherwise where the UID's file is; nil if neither
```

`resolve` takes a path, a `uid://` or a `path|uid://…` reference and returns a path or nil; text that is none of these is an error. The UIDs are looked for under `res://` and in each package that loads, in load order.
`ModelRenderer.Model` is a number in scripts (`models:load`'s id) and is saved in scenes as such a reference: see [asset fields](../core/ecs.md#asset-fields).

A game cooked with `--pack --cooked` has the artifacts of its imported assets and not their sources. A path that names an imported source still resolves (the pack's index says it was imported), and
`models:load` accepts it, so scenes and scripts written with source paths run the same shipped.

## Tests

`Tests/Apogee.Core.Tests/AssetRefTests.cs` covers the text forms. `Tests/Apogee.Content.Tests/AssetUidTests.cs` covers the UID in sidecars (made, kept, edited in place with comments, quotes, CRLF and a byte order mark), the key that ignores it
(nothing is imported again; a real setting still does), a deleted sidecar, copies and the file that moved with its UID, authored files, `assets.xml`, read-only roots, packages, and an asset that is renamed and
still resolves.

`Tests/Apogee.Content.Tests/AssetReferencesTests.cs` covers finding and rewriting references (by path and by a stale path's UID, comments, quotes, CRLF and a byte order mark kept, similar paths left alone, packages, read-only
roots, hostile and cooked files, a writable document that was skipped, a failed write put back for any exception, a file saved since or in between, a file that cannot be put back, symbolic links, reading that goes
wrong, a script with 500,000 mentions, a UID on two files, a UID in capitals, `game.xml`, `mod.xml` and element text, previews) and `Move` (sidecar, artifact and lists, a model used by two scenes that still resolves through its UID,
refusals, a failed move put back, a move that cannot be undone and the same call finishing it, an interrupted one finished). `Tests/Apogee.Content.Tests/FileSystemTests.cs` covers the durable write and the limits of a listing.

`Tests/Apogee.Ecs.Tests/SceneAssetTests.cs` covers asset fields in scenes (saved as path and UID, loaded from either, a moved asset found through its UID, an unregistered type), `GamePackerTests` the path-only reference in a cooked pack,
`Tests/Apogee.Rendering.Tests/ModelLibraryPathTests.cs` `uid://` in `ModelLibrary.Load`, and `RuntimeTests.AModelLoadsByUidAndASavedSceneFindsAModelThatWasMoved` the `assets` global and a scene saved by a script that loads after the model moved.
