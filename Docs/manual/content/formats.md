# File formats

Every file the engine makes or reads that is not a format everyone already knows has its own extension, and that extension starts with `w`: `.wscn`, `.wmdl`, `.wpak`… This page is the reference for all of
them: what each holds, how it is laid out, which version it is and which versions a reader takes, and whether the engine reads it today. The formats everyone knows keep their names: `.luau`, `.slang`, `.rml`,
`.rcss`, `.glb`, `.gltf`, `.png`, `.jpg`, `.ogg`, `.wav`, `.flac`, `.mp3`, `.ttf`, `.otf`, `.xsd`, and `.ktx2` (what an imported image is today).

Two kinds of format exist, and the engine keeps them apart:

- **XML formats** are what people and tools write and read: scenes, prefabs, manifests, import settings, and the formats planned below. They are UTF-8 text, they have an embedded `.xsd` where the engine reads
  them today, and a version control system shows and merges them as text. XML is the only authored format: there is no other text format in the engine.
- **Binary formats** are what the engine makes for itself: imported models and shaders, packs, and cooked XML. Every one starts with a four-letter magic (the extension in capitals, `WXMB` for cooked XML), is little-endian, and is
  never edited by hand; it is made again from its source.

**Status** says whether the engine reads the format now. *Implemented* means the code reads and writes it as described here (this page was written from the code). *Planned: not read by the engine yet* is a
design: the layout may still change when the format is built, and nothing in the engine opens such a file. A game or a tool must not depend on a planned format.

## The table

| Extension | Holds | Form | Magic | Status |
| --- | --- | --- | --- | --- |
| [`.wscn`](#wscn-scene) | a scene: entities, components, prefab instances | XML; cooked XML in packs | `WXMB` when cooked | implemented |
| [`.wprf`](#wprf-prefab) | a prefab | XML; cooked XML in packs | `WXMB` when cooked | implemented |
| [`.wimp`](#wimp-import-settings) | the import settings, the UID, the hash and the sub-asset UIDs of the source file it sits beside | XML | none | implemented (version 2) |
| [`.wgam`](#wgam-game-manifest) | a game's manifest (`game.wgam`) | XML | none | implemented |
| [`.wmod`](#wmod-package-manifest) | a package's or mod's manifest (`mod.wmod`) | XML | none | implemented |
| [`.wpak`](#wpak-pack) | a pack: a game's files, compressed and optionally encrypted | binary, own layout | `WPAK`, or a hash of the key | implemented |
| [`.wmdl`](#wmdl-model) | an imported model | binary, own layout | `WMDL` | implemented (version 1); version 2 planned |
| [`.wshd`](#wshd-shader) | a compiled shader module | binary, own layout | `WSHD` | implemented (version 1); on the common container planned |
| [cooked XML](#cooked-xml-wxmb) | any XML document of a cooked pack, as a tree | binary, own layout | `WXMB` | implemented |
| [`.ktx2`](#ktx2-textures) | an imported image (a standard KTX 2 file) | binary, Khronos format | the KTX 2 identifier (`«KTX 20»`) | implemented; replaced by `.wtex` when that lands |
| [`.wtex`](#wtex-texture) | a texture, in a portable form and in a GPU form | binary, common container | `WTEX` | planned |
| [`.wsnd`](#wsnd-sound) | an imported sound | binary, common container | `WSND` | planned |
| [`.wmat`](#wmat-material) | a material and its parameters | XML | none | planned |
| [`.wdat`](#wdat-data-record) | one typed data record, referenced by an asset reference | XML | none | planned |
| [`.wcur`](#wcur-curve) | a curve: float or colour over time | XML | none | planned |
| [`.wloc`](#wloc-string-table) | the strings of one language | XML | none | planned |
| [`.wfnt`](#wfnt-font-family) | a font family: faces and fallbacks | XML | none | planned |
| [`.wphy`](#wphy-physics-material) | a physics material | XML | none | planned |
| [`.wsfx`](#wsfx-sound-events) | sound events (and, later, mixer snapshots) | XML | none | planned |
| [`.wgph`](#wgph-node-graph) | a generic node graph | XML | none | planned |
| [`.wcol`](#wcol-collision-shapes) | cooked collision shapes of a model | binary, common container | `WCOL` | planned |
| [`.wanm`](#wanm-animation-clip) | an animation clip | binary, common container | `WANM` | planned |
| [`.wagr`](#wagr-animation-graph) | an animation graph | XML | none | planned |
| [`.wfx`](#wfx-particle-effect) | a particle effect | XML | none | planned |
| [`.wnav`](#wnav-navigation-mesh) | navigation mesh tiles | binary, common container | `WNAV` | planned |
| [`.wter`](#wter-terrain) | terrain: an XML header and binary tile blocks | mixed | to be decided | planned |
| [`.wsky`](#wsky-sky-and-environment) | a sky and its environment | XML | none | planned |
| [`.wpp`](#wpp-post-process-volume) | a post-process volume | XML | none | planned |
| [`.wsav`](#wsav-save-slot) | a save slot | XML | none | planned |
| [`.wwld`](#wwld-world) | a world made of streamed scene cells | XML | none | planned |
| [`.wbak`](#wbak-baked-lighting) | baked lighting: lightmaps and probe volumes | binary, common container | `WBAK` | planned |

Files that are not assets and are not in this table: `imported/index.xml` and `imported/assets.xml` ([below](#files-that-belong-to-no-asset)), the player's `user://settings.xml`
([Settings](../platform/settings.md)), the definitions in a package's `data/*.xml` with their `schemas/*.xsd` ([Definitions and schemas](defs.md)), and the cache of compiled shaders under the user's folder (a
private file, `WSHC2`, which nothing else reads).

**The table is the source of truth.** The extensions, the magics, the root elements and the versions of the formats the engine reads are one table in the code (`AssetFormats` in `Weft.Content`, see [Asset identity](assets.md#kinds-of-files)), and the importers, the hot reload, the editor, the packer and the readers go by it. A format that is *planned* below has no row until the code that reads it lands.

## Conventions

- **Little-endian**, everywhere. A magic is written as the four ASCII letters in this order: `WPAK` is the bytes `57 50 41 4B`. Tables below write offsets from the start of the file.
- **String** (in the formats that are not a container): the length in bytes as a 7-bit encoded integer (one byte for under 128, as .NET's `BinaryWriter` writes it), then the UTF-8 bytes. **Vector** is its
  `float32` components in turn (`Vector3` is 12 bytes), a **matrix** is 16 `float32` in row-major order (`M11`, `M12`… `M44`), and a **bool** is one byte.
- **UID**: `uid://` and 13 characters of lower-case Crockford base32, `uid://[0-9a-hjkmnp-tv-z]{13}` ([Asset identity](assets.md)). A UID is 64 random bits and never zero.
- **XML documents** are UTF-8 (a byte order mark is allowed). Where a format has a `version` attribute, a reader refuses a version **newer** than the one it knows, and says so; a binary format says what
  it takes in its own section, because some take only the version they were written with. A file that is wrong is a `ContentException` (or a `FormatException` for a scene or a prefab) that names the file and,
  for XML, the line.
- **Names do not convert.** A file that still has an extension these formats had before this naming is not read as any of them; see [File names](content.md#file-names).

## Sidecars and artifacts

One pattern holds for everything that is *imported* from a file made by other tools:

```text
models/car.glb                       the source: kept, edited in the tool that made it
models/car.glb.wimp                  the sidecar: XML, kept in version control, holds the UID and the import settings
imported/models/car.glb.wmdl         the artifact: binary, made by --import, never edited
```

- The **source** stays where it is and is what artists work on. Nothing in a game needs it once it is imported and cooked.
- The **sidecar** is the source's path plus `.wimp`. It is small, it is XML, and it belongs in version control: the identity (UID) and the settings of an asset are decisions, and a diff of them makes sense. It is
  made on the first import ([Asset import](import.md#settings)).
- The **artifact** is in the `imported/` folder at the root of the game or the package, at the path of the source with the extension of the artifact added: `imported/<source path>.<extension>`
  (`imported/textures/ground.png.ktx2`, `imported/lit.slang.wshd`). There is **no suffix** to tell it from the source (nothing like a `_c`): the folder and the extension are enough. The artifact is a cache, so
  it is made again from the source and the sidecar whenever its key changes ([Asset import](import.md#when-something-is-imported-again)); a game decides for itself whether it keeps it in version control.
- A **pack holds the same bytes** as the folder. `--pack --cooked` puts each artifact at its path in the pack and leaves out the source and the sidecar ([Packs](packs.md)); the UID list `imported/assets.xml` goes along, so a
  UID still names its asset. The exceptions are documents: a scene, a prefab and the definitions of the base packages are stored as [cooked XML](#cooked-xml-wxmb) at the path of their source.
- **Models** are imported to a binary `.wmdl`. There is no authored XML model format: the source of a model is a glTF file, and a model that is described in XML instead is only an idea (see [wmdl](#wmdl-model)).
- Authored documents (a scene, a prefab, the planned XML formats) are their own source: they have no sidecar and no artifact, and their UID is the `uid` attribute of the root element.

## Formats the engine reads today

### wscn: scene

**Purpose.** The entities of a world with their components, as an XML document. The editor saves and opens them, a game's entry scene is one, scripts and tools make and read them. Details and the C# and Luau API:
[Scenes](scenes.md).

**Layout.** The root is `<Scene>`; the schema is `scene.xsd` (embedded in `Weft.Ecs`).

| Part | Meaning |
| --- | --- |
| `<Scene version uid>` | `version` is required by the schema: 1, or 2 when the scene holds a prefab instance. `uid` is the file's identity, optional (text made by `SaveScene` has none; a saved file has one). |
| `<Components>` | optional; the components that scripts defined, each `<Component name>` with `<Field name kind [assetType]>` children. Components of the engine and the game are not listed. |
| `<Entity id name [prefab]>` | an entity, any number. `id` is its number in the file (from 1). Its children are its components (an element named after the component, one attribute per field, empty for a tag) and its child entities (more `<Entity>`). |
| `<Entity prefab="path\|uid">` | an instance of a [prefab](#wprf-prefab): it holds only what the instance changes: component elements (changes to the root), `<Entity>` children (added below the root) and `<Override entity name remove>` elements (changes to any other entity of the instance). |

```xml
<?xml version="1.0" encoding="utf-8"?>
<Scene version="1">
  <Components>
    <Component name="Ticker">
      <Field name="count" kind="Int32"/>
      <Field name="look" kind="AssetRef" assetType="model"/>
    </Component>
  </Components>
  <Entity id="1" name="ground">
    <Transform Position="0 -0.5 0" Rotation="0 0 0 1" Scale="1 1 1"/>
    <ModelRenderer Model="res://models/ground.glb|uid://8c1q0w3e5r7ty"/>
    <Entity id="2" name="crate">
      <Transform Position="1.25 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
      <Ticker count="3"/>
    </Entity>
  </Entity>
  <Entity id="3" name="camera"><Transform Position="0 2 -6" Rotation="0 0 0 1" Scale="1 1 1"/><MainCamera/></Entity>
</Scene>
```

Field text is the same in every culture: `true`/`false`, decimal integers, the shortest text that reads back as the same float, vectors and quaternions as numbers separated by spaces, an `Entity` field as an `id` of the
file (`2/3` inside an instance), an `AssetRef` as `path|uid://…` ([Field text](scenes.md#field-text)). Loading is forgiving (an unknown component or attribute is logged and skipped); what is an error is a document that is not
XML, a root other than `<Scene>`, and a version newer than the engine's.

**Versioning.** The writer produces version 1, and version 2 for a scene that holds a prefab instance, so that an engine that does not know instances refuses the file instead of loading them empty. The reader accepts
1 and 2 and refuses a larger number; a `version` attribute that is missing is accepted although the schema requires it. A new version is made when a change would make an older engine load a file wrongly.

**In a pack.** `--pack --cooked` stores the scene as [cooked XML](#cooked-xml-wxmb) at the same path; every reader takes both forms ([Cooked scenes](scenes.md#cooked-scenes)).

**Status.** Implemented. Owning page: [Scenes](scenes.md).

### wprf: prefab

**Purpose.** A piece of a scene that is made again and again: one entity with everything below it, saved as XML. A scene or another prefab holds an instance as one element that names the prefab and says only what it changes.
Details: [Prefabs](prefabs.md).

**Layout.** The root is `<Prefab>`; the schema is `prefab.xsd` (embedded in `Weft.Ecs`). The entity elements and the field text are those of a [scene](#wscn-scene).

| Part | Meaning |
| --- | --- |
| `<Prefab version uid nextId>` | `version` is required by the schema (1). `uid` is the file's identity. `nextId` is the id the next new entity takes: above every id in the file, never lowered. |
| `<Components>` | optional, as in a scene. |
| `<Entity id name>` | **exactly one**, the root; everything else is below it. Unlike a scene's, an `id` does not change when the file is saved: scenes that instance the prefab name its entities by id. |
| `<Entity prefab="…">`, `<Override …>` | a nested prefab instance, as in a scene. |

```xml
<?xml version="1.0" encoding="utf-8"?>
<Prefab version="1" uid="uid://7h2k4m6p8q0rs" nextId="4">
  <Entity id="1" name="Lamp">
    <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
    <Entity id="2" name="Bulb">
      <Transform Position="0 1.8 0" Rotation="0 0 0 1" Scale="1 1 1"/>
      <Light Color="1 0.9 0.7" Intensity="80" Range="12" InnerCone="0" OuterCone="0" Kind="1"/>
    </Entity>
    <Entity id="3" name="Base"><Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
  </Entity>
</Prefab>
```

**Versioning.** The version is 1. The reader refuses a larger number and accepts a missing `version`; a `nextId` that is missing or not above every id is worked out again, with a warning. A file with no root entity or with two
is an error.

**In a pack.** Stored as [cooked XML](#cooked-xml-wxmb) at the same path by `--pack --cooked` ([Cooked builds](prefabs.md#cooked-builds)).

**Status.** Implemented. Owning page: [Prefabs](prefabs.md).

### wimp: import settings

**Purpose.** The sidecar of a source file: `car.glb.wimp` sits beside `car.glb`, holds the asset's UID, remembers what the importer made of it, and changes how it is imported. See [Sidecars and artifacts](#sidecars-and-artifacts).

**Layout.** The root is `<Import>`; the schema is `import.xsd` (embedded in `Weft.Content`). Every attribute is optional; the order of the child elements is free.

| Attribute | For | Meaning |
| --- | --- | --- |
| `version` (1) | any asset | the version of this format: 2. A sidecar without it is version 1 |
| `uid` | any asset | the identity of the asset; written by the first import, never changed by hand. It is not part of the import key: minting or changing it imports nothing again. |
| `sourceHash` | any asset | the SHA-256 of the source file as the last import read it, 64 lower-case hexadecimal digits; written by the importer, not part of the key |
| `srgb` (true) | images | colour maps are sRGB; normal maps, roughness and masks are linear |
| `normalMap` (false) | images | the image is a normal map: linear, and encoded for accuracy |
| `compression` (`uastc`) | images, textures of models | `uastc` or `none` (8-bit RGBA) |
| `mips` (true) | images | build the mip chain |
| `optimize` (true) | models | merge duplicate vertices and reorder for the GPU's vertex cache |

| Element | For | Meaning | Used by the engine |
| --- | --- | --- | --- |
| `<Variant>` with `<Define name [value]>` children | shaders | one more version of the shader to compile | yes |
| `<SubAsset name uid/>` (up to 4096) | models | a file the import makes from the asset (`material/paint`, `texture/albedo`) and the UID it was given; minted once per name, written by the importer, kept by name through every import | yes: the model importer asks for one per material and texture; the files themselves are not written yet |
| `<MaterialRemap slot ref/>` | models | use the material asset `ref` (path or `uid://`, a `.wmat`) for the model's material slot `slot` | read, not applied yet |
| `<Socket name node position rotation/>` | models | a named attachment point on a node of the model (empty `node` is the root); `position` is `x y z` (default `0 0 0`), `rotation` a quaternion `x y z w` (default `0 0 0 1`) | read (`ImportSettings.Sockets`), not written into the model yet |
| `<Clip name loop start end/>` | animation | a named part of the source's animation, `start`/`end` in seconds | kept, not read by anything |
| `<Lod screenSize reduction/>` | models | a level of detail: from this share of the screen's height, this share of the triangles taken away | kept, not read by anything |
| `<Collision shape/>` | models | `none`, `box`, `convex` or `mesh` | kept, not read by anything |

```xml
<Import version="2" uid="uid://4k2m9x7q1w3ze" sourceHash="9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08" srgb="false">
  <SubAsset name="material/paint" uid="uid://0123456789abc"/>
  <MaterialRemap slot="paint" ref="materials/paint.wmat"/>
  <Socket name="hand" node="arm.R" position="0 0.1 0" rotation="0 0 0 1"/>
  <Clip name="walk" loop="true" start="0" end="1.5"/>
  <Collision shape="convex"/>
</Import>
```

```xml
<Import>
  <Variant><Define name="SHADOWS"/><Define name="QUALITY" value="3"/></Variant>
</Import>
```

**Versioning.** The reader accepts version 1 (no `version` attribute) and 2, and refuses a larger number ("written by a newer engine"). The next import of an asset rewrites its sidecar as version 2, in the same file: the
engine edits the text (the root's attributes and the `<SubAsset>` elements), so comments, layout, quotes, line endings and every element it does not use stay character for character; there is no command and no other
migration. The settings, minus the attributes and elements the importer writes (`uid`, `version`, `sourceHash`, `<SubAsset>`), are part of the import key, whose version is `AssetImporter.ImporterVersion` (4): when the
importer changes what it writes, that number goes up and every artifact is made again.

**Limits.** A sidecar over 1 Mi characters, with more than 4096 sub-assets, a repeated UID or name, a UID that is not one, or an unknown element or attribute is refused with a message that names the file.

**Status.** Implemented (version 2): `uid`, `sourceHash`, `<SubAsset>`, `<Variant>` and the settings are written or read by the engine; `<MaterialRemap>` and `<Socket>` are read and available to the model importer and will be
applied by the model steps that follow; `<Clip>`, `<Lod>` and `<Collision>` are validated and preserved and have no reader yet. Owning page: [Asset import](import.md#settings); the UID: [Asset identity](assets.md#where-a-uid-lives).

### wgam: game manifest

**Purpose.** What a game is called, who made it, which version it is, where the player's files go, its Steam app id and what its window looks like. It is the file `game.wgam` at the root of a game folder.
Details: [The game manifest](game-manifest.md).

**Layout.** The root is `<Game>`; the schema is `game.xsd` (embedded in `Weft.Content`).

| Part | Meaning |
| --- | --- |
| `<Game name organization version [executable identifier userFolder steamAppId description copyright]>` | `name`, `organization` and `version` (`major.minor.patch`) are required |
| `<EntryScene path/>` | the scene loaded before the packages' scripts run |
| `<Icon path/>` | a square PNG, 256 pixels or more |
| `<Window width height fullscreen resizable/>` | the window the game opens with |

```xml
<Game name="Fixture Game" organization="Weft" version="1.0.0" executable="FixtureGame" identifier="org.weft.fixturegame">
  <EntryScene path="scenes/main.wscn"/>
  <Icon path="icon.png"/>
  <Window width="1280" height="720" fullscreen="false" resizable="true"/>
</Game>
```

**Versioning.** The file has no format version: the schema is the format, and a change that adds an optional attribute or element is compatible. The `version` attribute is the *game's* version, which the `gameVersion`
ranges of mods are checked against. A `game.wgam` that does not match the schema, or breaks a rule it cannot say (a path with `..`, an icon that is not square), is an error of the game: it does not start.

**Status.** Implemented. Owning page: [The game manifest](game-manifest.md).

### wmod: package manifest

**Purpose.** What a package is (the base game's, or a mod's), what it needs and what it brings. It is the file `mod.wmod` at the root of a package folder, of a `.zip`, or of a Workshop folder. Details:
[Packages and mods](packages.md).

**Layout.** The root is `<Mod>`; the schema is `mod.xsd` (embedded in `Weft.Content`).

| Part | Meaning |
| --- | --- |
| `<Mod id version [name author description apiVersion gameVersion]>` | `id` (lowercase letters, digits, `_`, up to 64) is the name of the package's folder; `version` is `major.minor.patch`; `apiVersion` (default 1) is the package API it was written for; `gameVersion` is a version range (default any) |
| `<Dependency id [version]/>` | a package that must be there, and a version range of it |
| `<Script path/>` | a `.luau` file run when the package loads, in the order written |
| `<Content path/>`, `<Schema path/>` | a folder of definitions (`.xml`), a folder of schemas (`.xsd`) |

```xml
<Mod id="bigger_crates" name="Bigger Crates" version="1.2.0" author="Ada" apiVersion="1" gameVersion="&gt;=1.0 &lt;3">
  <Dependency id="base_game" version="^1.0"/>
  <Script path="main.luau"/>
  <Content path="data"/>
  <Schema path="schemas"/>
</Mod>
```

**Versioning.** The file has no format version. `apiVersion` is the version of the *package API* (what a script and a definition can rely on), not of this format: a package that needs a newer one than the engine
has does not load, with a reason the game can show. A manifest is never cooked: it stays text so that players and tools can open it.

**Status.** Implemented. Owning page: [Packages and mods](packages.md).

### wpak: pack

**Purpose.** A game's files in one file or a few: each zstd-compressed when that pays, optionally sealed with AES-256-GCM, with an index of paths. A shipped game's `res://` is its packs. Details, the key and what
encryption does and does not do: [Packs](packs.md).

**Layout.** One *part* is a file; a pack bigger than its limit goes on in `name_001.wpak`, `name_002.wpak`… (up to 1000 parts; a file is never split). Each part:

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | magic: `WPAK` without a key; with a key the first four bytes of `SHA-256("Weft pack magic" + key)` (changed if that is `WPAK` or 0), so that an encrypted pack has no signature |
| 4 | 4 | format version (`u32`): 1 |
| 8 | 4 | flags: 1 = the index is sealed, 2 = another part follows |
| 12 | 4 | zero |
| 16 | 8 | offset of the index |
| 24 | 8 | stored length of the index |
| 32 | 8 | raw length of the index |
| 40 | 4 | number of files in this part |
| 44 | 4 | zero |
| 48 | 32 | random salt of the part |
| 80 | … | the files' data, each aligned to 16 bytes, then the index |

The **index** is zstd-compressed (level 19) and, with a key, sealed. Raw, it is a `u32` count and, per file: the path (a string), `u8` flags (1 = compressed with zstd, 2 = sealed, 4 = removed: the path is taken away from
the packs below this one, and has no data), `i64` offset, `i64` stored length, `i64` raw length and 16 bytes (the start of the SHA-256 of the raw file). A file is compressed when it has 64 bytes or more and that saves at
least 10%; compression comes first and encryption after. A **sealed** blob is `nonce (12) | tag (16) | ciphertext` (AES-256-GCM), with the file's path as associated data so that it cannot be moved to another path; the key
of a part is `HKDF-SHA256(pack key, salt)`. A later part overrides an earlier one, and a patch pack mounted over a base replaces, adds and removes.

**Versioning.** The version is 1 and the reader takes exactly that: any other number is refused with the version it found. A change of layout makes a new number.

**Planned.** A later version of the index may add a dependency table per file (which other files an asset needs), so that a loader can read what a scene needs before it asks for it. Nothing reads or writes it yet.

**Status.** Implemented. Owning page: [Packs](packs.md).

### wmdl: model

**Purpose.** A glTF model after import (`ModelData`): ready to load without a glTF parser or any image decoding. Vertex and index buffers use meshoptimizer's lossless codecs (the geometry comes back bit for bit),
and the textures are embedded as KTX 2 files. It is made by `--import` as `imported/<path>.wmdl` ([Asset import](import.md#what-it-makes)); `ContentLoader` loads it in place of the glTF.

**Layout (version 1).** `ModelFile`. A single stream, in this order; a count is `i32`, and a blob is an `i32` length and that many bytes.

| Part | Fields |
| --- | --- |
| Header | magic `WMDL` (`u32`), version (`u32`, 1), name (string), bounds minimum and maximum (two `Vector3`) |
| Textures | count; per texture: name (string), `srgb` (bool), filter (`u8`), mipmaps (bool), wrap U and wrap V (`u8` each), a blob holding a [KTX 2 file](#ktx2-textures) |
| Materials | count; per material: name, base colour factor (`Vector4`), base colour texture, metallic factor, roughness factor (`f32`), metallic-roughness texture, normal texture, normal scale, occlusion texture, occlusion strength, emissive factor (`Vector3`), emissive texture, alpha mode (`u8`), alpha cutoff (`f32`), double-sided (bool), unlit (bool) |
| Meshes | count; per mesh: name, material index (`i32`), bounds (two `Vector3`), vertex count, index count, a blob of vertices (meshoptimizer vertex codec, 48-byte vertices), a bool (the indices are a triangle list), a blob of indices (meshoptimizer index codec for a triangle list, raw `u32` otherwise) |
| Instances | count; per instance: mesh index (`i32`), world matrix |
| Lights | count; per light: name, kind (`u8`), colour (`Vector3`), intensity, range, inner cone, outer cone (`f32`), position, direction (`Vector3`) |

A *texture reference* in a material is a bool (present), then the texture index (`i32`), the UV set (`i32`), offset and scale (`Vector2`) and rotation (`f32`). Counts and blob lengths are checked against the size of the
file before anything is allocated for them: a damaged file is a `ContentException` that names it, never anything else.

**Versioning.** The version is 1 and the reader takes exactly that; any other is refused with "import it again". Importing is cheap to repeat, so no older version is kept readable.

**Planned: version 2.** A model on the [common container](#the-common-binary-container-planned): a small `META` block (name, bounds, meshes and submeshes, material slots with their parameters and an optional `.wmat`
remap, texture references by sub-asset UID, nodes, sockets, a table of levels of detail) and a pair of buffer blocks per mesh (`VTX<n>`, `IDX<n>`, with the same meshoptimizer codecs), deterministic, readable by
`--dump`. The textures leave the file and become sub-assets, `imported/<model path>/<n>.wtex`, each with a UID kept in the model's sidecar. Version 1 would not be read: the model is imported again.
An authored XML model format, to describe a model without a glTF file, is only an idea: no engine code reads one.

**Status.** Implemented (version 1). Owning page: [Asset import](import.md#what-it-makes).

### wshd: shader

**Purpose.** A Slang shader module compiled ahead of time, so that a shipped game never loads the compiler: `imported/<path>.wshd`. One file serves Vulkan and Metal. See
[Compiled ahead of time](../graphics/graphics.md#compiled-ahead-of-time-shaderbundle) and [Asset import](import.md#shaders).

**Layout (format version 1).** `ShaderBundle`. A single stream, strings and `i32` counts as in `wmdl`.

| Part | Fields |
| --- | --- |
| Header | magic `WSHD` (`u32`), format version (`i32`, 1), shader ABI (`i32`, 2), module name (string), variant count |
| Per variant | the variant's name (its defines sorted by name as `A=1;B=2`; empty for the default one), the reflection (the compiler's description of the shader's parameters and entry points, as text), the compiler's warnings (string), the number of code blobs |
| Per code blob | backend (`u8`: 1 SPIR-V, 2 Metal shading language source, 3 DXIL, reserved: nothing writes it), entry point name (string), length (`i32`) and the code |

A reader takes the code of the backend it runs on and skips the others. The reflection is that of the first backend written (SPIR-V).

**Versioning.** Two numbers. The *format version* (1) is the layout above; the *shader ABI* (2) is what compiled code assumes about the engine (the bindless descriptor layout and the push constants every pipeline
shares), and goes up whenever that changes. A reader takes exactly its own number of each: a bundle of another ABI is refused with "import it again", not left to crash the driver. (A cache of compiled shaders
under the user's folder is a separate private file, `WSHC2`; it is not a format of the engine's content.)

**Planned.** The bundle on the common container: one block per target (SPIR-V, Metal) and one for the reflection, a bumped format version, and a dumper for `--dump`.

**Status.** Implemented. Owning pages: [Asset import](import.md#shaders), [Graphics](../graphics/graphics.md#compiled-ahead-of-time-shaderbundle).

### Cooked XML (WXMB)

**Purpose.** The tree of any XML document in a binary encoding, so that a shipped game parses no text for its scenes, prefabs, definitions and UID list. `--pack --cooked` stores such a document in place of its source,
at the same path. It is not typed by the reflection registry: it holds the `XmlNode` tree, so it does not depend on which components the engine had when the file was cooked. A tree is read back as it was written.
See [Cooked XML](import.md#cooked-xml).

**Layout (version 1).** `XmlBinary`. Little-endian; a *varint* is an unsigned 32-bit LEB128 (7 bits per byte).

| Part | Fields |
| --- | --- |
| Header, 16 bytes | magic `WXMB` (`u32`), version (`u16`, 1), flags (`u16`, none defined: 0), string count (`u32`), element count (`u32`) |
| Strings | per string: byte length (varint), UTF-8 bytes; in the order the writer first used them; string 0 is the empty string |
| Elements | in document order, root first: name (varint, a string index), line (varint), attribute count (varint), per attribute: name and value (varint string indexes), text (varint string index), child count (varint) |

The same tree always gives the same bytes. The origin of an element is not stored: the reader gives every element the path it was asked to read, so an error still names the file and the line. Every count is checked
against the bytes left before the reader makes room for it, and nesting is limited as in the text parser.

**Versioning.** The reader takes every version from 1 up to the one it knows; a file from a newer engine, version 0, or a flag this engine does not know is refused with "cook again". A text XML file can never be mistaken for it: those begin with `<`, a byte order mark or white space, and `XmlFile.Read` tells them apart by the magic.

**Status.** Implemented. Owning page: [Asset import](import.md#cooked-xml).

### ktx2 textures

**Purpose.** An imported image today. A `.png` or `.jpg` is imported to `imported/<path>.ktx2`, a standard Khronos KTX 2 file with its whole mip chain: **Basis Universal UASTC** blocks (one file for every
platform, transcoded at load to BC7, ASTC 4x4 or RGBA8, whichever the GPU samples), or 8-bit RGBA with `compression="none"`. Either way the levels are supercompressed with zstd. The textures embedded in a
[`.wmdl`](#wmdl-model) are the same files. Because it is a standard container, any KTX 2 tool opens it.

**Layout.** As the KTX 2 specification: an identifier (`«KTX 20»`, then `\r\n\x1A\n`), a header, a level index (the smallest level first in the file), a data format descriptor that says what the bytes are (colour
space included) and the levels. Details of what the importer writes and its settings: [GPU texture compression](import.md#gpu-texture-compression).

**Versioning.** KTX 2 is versioned by Khronos; libktx reads it. The engine's own part is the importer's fingerprint (the encoder settings), which is part of the import key.

**Planned.** `.wtex` replaces this form: [wtex](#wtex-texture). A `.ktx2` would then be an importable *source*, not an artifact.

**Status.** Implemented. Owning page: [Asset import](import.md#gpu-texture-compression).

## The common binary container (planned)

*Planned: not read by the engine yet.* Every binary artifact the engine makes, apart from packs and cooked XML, is to share one container: `.wmdl`, `.wtex`, `.wsnd` and `.wshd`, and the planned `.wanm`, `.wcol`,
`.wnav` and `.wbak`. One reader and writer, one place for the UID and the import hash, one `--dump`.

**Header** (64 bytes, little-endian):

| Field | Size | Meaning |
| --- | --- | --- |
| magic | `u32` | `WMDL`, `WTEX`, `WSND`, `WSHD`… |
| container version | `u16` | 1 |
| format version | `u16` | the version of the format inside (of `.wmdl`, of `.wtex`…) |
| flags | `u32` | |
| header size | `u32` | |
| block count | `u32` | |
| block table offset | `u64` | |
| UID | `u64` | the asset's UID, copied from the sidecar; 0 = none (the artifact then belongs to a sub-asset that has none, or to a file without a sidecar) |
| import hash | 16 bytes | the first 16 bytes of the import key: the settings, the source hash and the importer's fingerprint |
| reserved | | zero |

**Block table.** One row per block: kind (a four-character code), codec (`u8`: none, zstd, meshoptimizer vertex, meshoptimizer index), flags (`u8`), alignment as a power of two (`u8`), reserved, offset (`u64`), stored
size (`u64`), raw size (`u64`) and the CRC-32C of the raw bytes (`u32`). Block 0 is always `META`: small, read in one go, a versioned little-endian structure per format whose strings are in a string block. Big blocks
(`VTX0`…, `IDX0`, `MIP3`…) are aligned to 4096 bytes so that a streamer can read or map them directly.

**Deterministic.** A writer adds blocks in a stable order, pads with zeros and stores no time stamp: the same input makes the same bytes.

**Hostile input.** A reader checks every size and offset against the length of the file before it allocates, caps the block count (4096) and the `META` size (16 MiB), and refuses a block whose CRC does not match, a block
that overlaps another or lies past the end. Anything else wrong is a `ContentException`.

**`--dump`.** `Weft.Runtime --dump <file>` is planned to write any artifact as text on the standard output: the header, the blocks, and `META` as `name = value` lines; a cooked XML file as XML text; a pack as its
list of files. `--dump <file.wtex> --ktx2 <out>` would write a texture as a KTX 2 file.

Packs and cooked XML keep their own layouts (above); only their magics follow the naming.

## Planned formats

Everything from here on is *planned: not read by the engine yet*. The text says what each format is meant to hold; the names of elements and attributes are fixed when the format is built, with its schema and its tests.
Authored formats are XML with a root `version`, a `uid` attribute on the root (the asset's identity), an embedded `.xsd`, and are cooked as [cooked XML](#cooked-xml-wxmb) in a shipped pack.

### wtex: texture

**Purpose.** One container for every texture, in two forms: a **portable** form that import writes (UASTC blocks, transcoded at load as today) and a **GPU** form that export writes (blocks the GPU samples directly,
so there is no transcoding at load).

Why not stay with KTX 2: it already gives a level index with the smallest level first, per-level zstd, key/value data, the colour space in the descriptor, and it can hold BC7, BC5, BC6H and ASTC payloads. What an
own container adds is: the same header, UID, import hash, `--dump` and reader as every other artifact; mips aligned to 4096 bytes (KTX 2 aligns levels to the block size), a **resident tail** (all mips of 64 KiB or less in
one block at the front, so one read gives a usable texture) and typed engine fields instead of key/value strings; and a GPU form that is read without libktx, uploaded straight from the decompressed span into the
staging buffer. The cost is that KTX tools cannot open a `.wtex`; the payload is the same bytes a KTX 2 level holds, so a `.wtex` and a `.ktx2` convert to each other by rewrapping (`--dump x.wtex --ktx2 out.ktx2`).

**Layout.** On the [common container](#the-common-binary-container-planned), magic `WTEX`.

`META`:

| Field | Meaning |
| --- | --- |
| width, height, depth, layers, faces, mip count | the shape of the texture |
| payload | `Uastc`, `Rgba8`, `Bc7`, `Bc5`, `Bc4`, `Bc6hU`, `Rgba16f` |
| flags | **Portable** (the UASTC import form), `Srgb`, `NormalMapRG`, `AlphaCoverage`, `Premultiplied`, `Hdr`, `Cube`, `Array` |
| alpha cutoff | the coverage reference |
| texture group | `World`, `Character`, `Ui`, `Effects`, `Lightmap`: a quality setting per group |
| LOD bias | `s8` |
| tail mip index | where the resident tail starts |
| sampler hints | wrap and filter (a model's material slot may override them) |

Blocks: `TAIL` (the mips from the tail index on, smallest first) and `MIP<n>` (each larger mip its own block, zstd per block); all faces and layers of a level are in its block.

**Forms.** Import writes the **Portable** form: UASTC, or RGBA8 for `compression="none"` and for textures under 4 pixels (and, later, BC6H or half-float RGBA for HDR). The development runtime, and mods (which are
never cooked), transcode a portable texture at load, as a `.ktx2` is today. `--pack --cooked` and export rewrite every portable texture to the **GPU form of the desktop profile**: colour to BC7, normal maps
(`NormalMapRG`) to BC5, single-channel masks to BC4. Every desktop Vulkan GPU, lavapipe and Apple's Macs have BC; a GPU form met by a GPU without it is a `ContentException` that says so. The result is cached by import
hash (`imported/.cooked/<hash>.wtex`, an export cache, not kept in version control). The GPU form is 10 to 30 percent bigger on disk than RDO-compressed UASTC with zstd; that is accepted for no work at load.

**Versioning.** The format version in the container header; a reader takes the versions it knows and refuses the others with "import it again".

**Status.** Planned. Related page today: [Asset import](import.md#gpu-texture-compression).

### wsnd: sound

**Purpose.** An imported sound: `imported/<path>.wsnd` for an `.ogg`, `.wav`, `.flac` or `.mp3`. The audio system reads those four formats from the source today; the artifact would add the sample rate,
the channel count, the number of frames, loop points, a loudness gain and a stream flag.

**Layout.** On the [common container](#the-common-binary-container-planned), magic `WSND`: `META` with those fields, and a payload block that is either the source's bytes unchanged or, for a sound that is preloaded,
16-bit PCM.

**Status.** Planned. Related page today: [Audio](../audio/index.md).

### wmat: material

**Purpose.** A material, or an instance of one: the shader template, typed parameters, texture asset references, and a `parent` it overrides. Model materials from glTF would be imported as `.wmat` sub-assets, and a
model's material slot could be remapped to another `.wmat` through its sidecar.

**Layout.** XML; the parameters are a generic list of names and values, which is why the structure is expected to stay stable. A `parent` chain is resolved with the overrides applied, and a cycle is refused.

**Status.** Planned. Related page today: [Rendering](../graphics/rendering.md).

### wdat: data record

**Purpose.** One typed record that other assets refer to with an asset reference (an enemy's stats, a recipe). The root element names its type, and the type is checked against the package's schemas (`schemas/*.xsd`). It
differs from a [definition](defs.md): definitions are merged tables with no identity, a `.wdat` is one asset with a UID. A script would load one as a table.

**Layout.** XML; the root element is the type's name.

**Status.** Planned. Related page today: [Definitions and schemas](defs.md).

### wcur: curve

**Purpose.** A curve of floats or colours over time: keys (time, value, in and out tangent, interpolation) and pre and post wrap modes. Interpolation is constant, linear or cubic Hermite. A tween could take one as its
easing.

**Layout.** XML.

**Status.** Planned. Related page today: [Tweens](../scripting/tweens.md).

### wloc: string table

**Purpose.** The strings of one language: keys, text, plural forms by CLDR category, and notes for translators. Tables merge in the order of the packages.

**Layout.** XML.

**Status.** Planned. No page yet.

### wfnt: font family

**Purpose.** A font family: its faces (regular, bold, italic, each a reference to a `.ttf` or `.otf`), a fallback chain and a default size.

**Layout.** XML.

**Status.** Planned. Related page today: [User interface](../ui/index.md).

### wphy: physics material

**Purpose.** Friction, restitution, density, and the sound and effect a surface plays, for a body to take by reference.

**Layout.** XML.

**Status.** Planned. Related page today: [Physics](../physics/physics.md).

### wsfx: sound events

**Purpose.** A bank of named sound events: each with clips and their weights, volume and pitch ranges, a bus, a 3D range and a cooldown. Later a second kind of root would hold mixer snapshots (bus levels).

**Layout.** XML.

**Status.** Planned. Related page today: [Audio](../audio/index.md).

### wgph: node graph

**Purpose.** A generic graph: nodes (an id, a type, a position, properties), pins, links and comments, with a `kind` that says what the graph is for (a material graph is a `.wgph` of kind `material`). The layout is
generic; what node types a kind has, and how pins are typed, is up to the feature that uses it.

**Layout.** XML. A reader checks that node ids are unique, that every link names an existing pin and that no link is repeated.

**Status.** Planned. No page yet.

### wcol: collision shapes

**Purpose.** The cooked collision shapes of a model: convex hulls, a mesh BVH, in the physics library's binary form. Today shapes are built when a body is made.

**Layout.** On the [common container](#the-common-binary-container-planned), magic `WCOL`; kept apart from a physics material because an authored setting and a cooked result differ.

**Status.** Planned. Related page today: [Physics](../physics/physics.md).

### wanm: animation clip

**Purpose.** A clip: tracks (compressed with meshoptimizer), events and root motion.

**Layout.** On the [common container](#the-common-binary-container-planned), magic `WANM`.

**Status.** Planned. No page yet.

### wagr: animation graph

**Purpose.** An animation graph: blend trees, state machines and parameters, laid out as a [`.wgph`](#wgph-node-graph) with states.

**Layout.** XML.

**Status.** Planned. No page yet.

### wfx: particle effect

**Purpose.** A particle effect: emitters, modules and curves (references to `.wcur`).

**Layout.** XML.

**Status.** Planned. No page yet.

### wnav: navigation mesh

**Purpose.** Navigation mesh tiles, as Recast and Detour make them.

**Layout.** On the [common container](#the-common-binary-container-planned), magic `WNAV`.

**Status.** Planned. No page yet.

### wter: terrain

**Purpose.** Terrain: an XML header (size, layers, materials) and binary tile blocks (height, splat).

**Layout.** The one mixed format: how the header and the blocks are stored together is decided when it is built.

**Status.** Planned. No page yet.

### wsky: sky and environment

**Purpose.** A sky: the atmosphere, the sun, a reference to an HDR cube map, fog and ambient light. The parameters are those of the exposure and tone-mapping work ([Exposure and tone mapping](../graphics/tone-mapping.md)).

**Layout.** XML.

**Status.** Planned. No page yet.

### wpp: post-process volume

**Purpose.** A volume of post-processing: exposure, tone mapping, bloom and the like, with a blend shape and a priority. The parameter set is fixed with the renderer's post-processing, which is why the format waits.

**Layout.** XML.

**Status.** Planned. Related page today: [Exposure and tone mapping](../graphics/tone-mapping.md).

### wsav: save slot

**Purpose.** A save slot: its version, the game's version, migrations, the scene payload and the script data. It belongs to the library that saves games.

**Layout.** XML.

**Status.** Planned. No page yet.

### wwld: world

**Purpose.** A world made of cells that stream in: bounds and a `.wscn` per cell, and load distances.

**Layout.** XML.

**Status.** Planned. No page yet.

### wbak: baked lighting

**Purpose.** Baked lighting: lightmap atlases (references to `.wtex`) and probe or spherical-harmonics volumes.

**Layout.** On the [common container](#the-common-binary-container-planned), magic `WBAK`.

**Status.** Planned. No page yet.

## Files that belong to no asset

These are caches and records that the tools write; they have no UID and no schema of their own in the table above.

- **`imported/index.xml`**: which source each artifact came from and which files it was made from, so that only what changed is imported again. The root is `<Imports version>`, and `version` is
  `AssetImporter.ImporterVersion` (4). The reader does not check it: the number is also part of every import key, so a new one makes every artifact stale. Each `<Import source artifact key>` has `<Dependency path/>`
  children. A missing or damaged index is read as empty, and everything is imported again.
- **`imported/assets.xml`**: the list of UIDs and paths of the imported assets, sorted by path: `<Assets version="1">` with `<Asset uid path/>` children. It is a copy of what the sidecars and the authored documents
  hold, to make lookups fast; a pack carries it as cooked XML, and in a cooked pack it is the only record of the imported assets' UIDs. A missing, damaged or newer list is read as empty.

```xml
<Imports version="4">
  <Import source="models/car.gltf" artifact="imported/models/car.gltf.wmdl" key="9c1e…">
    <Dependency path="models/car.bin"/>
  </Import>
</Imports>
```

```xml
<Assets version="1">
  <Asset uid="uid://4k2m9x7q1w3ze" path="res://models/car.gltf"/>
</Assets>
```
