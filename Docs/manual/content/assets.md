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
| An authored file (`.ascene`) | the `uid` attribute of the document's root | `<Scene version="1" uid="uid://…">` |
| The whole project | `imported/assets.xml`, a list written by the import | `<Asset uid="uid://…" path="res://models/car.glb"/>` |

`AuthoredAssets.Extensions` lists the authored kinds; [scenes](scenes.md) are the first. The sidecar and the document are the truth; `assets.xml` is a copy that makes lookups fast. It is rewritten by every import (only when its text changes),
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

`Tests/Apogee.Ecs.Tests/SceneAssetTests.cs` covers asset fields in scenes (saved as path and UID, loaded from either, a moved asset found through its UID, an unregistered type), `GamePackerTests` the path-only reference in a cooked pack,
`Tests/Apogee.Rendering.Tests/ModelLibraryPathTests.cs` `uid://` in `ModelLibrary.Load`, and `RuntimeTests.AModelLoadsByUidAndASavedSceneFindsAModelThatWasMoved` the `assets` global and a scene saved by a script that loads after the model moved.
