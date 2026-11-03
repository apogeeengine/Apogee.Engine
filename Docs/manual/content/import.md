# Asset import

Source assets (glTF models, PNG and JPEG images, Slang shaders) are made for artists; a game wants its own formats, ready to load. *Importing* turns one into the other and keeps the result, so that it
is done once, not at every start.

```sh
Apogee.Runtime --import Samples/Mods/res            # import what changed (in the folder's imported/)
Apogee.Runtime --import Samples/Mods/res --force    # import everything again
./build.sh pack Samples/Mods/res Bin/data/game.pack --cooked   # pack the artifacts instead of the sources they replace
```

## What it makes

| Source | Artifact | |
| --- | --- | --- |
| `.glb`, `.gltf` | `imported/<path>.amodel` | the model as the engine uses it (`ModelFile`): vertex and index buffers compressed with meshoptimizer's lossless codecs (the geometry comes back bit for bit), the textures embedded as GPU-compressed KTX 2 files (below), the rest plain fields. Loads without a glTF parser or any image decoding |
| `.slang` | `imported/<path>.ashader` | a shader module compiled ahead of time (`ShaderBundle`, [Graphics](../graphics/graphics.md#compiled-ahead-of-time-shaderbundle)): SPIR-V (Vulkan) and Metal shading language for each entry point, and the reflection, for the module without defines and for each `<Variant>` of its settings. A module with no `[shader(…)]` entry point (a library such as `common.slang`) is not an asset; it is a **dependency** of every shader that imports it, so editing it imports those shaders again |
| `.png`, `.jpg`, `.jpeg` | `imported/<path>.ktx2` | a KTX 2 file with its whole mip chain: **Basis Universal UASTC** blocks (below), or with `compression="none"` 8-bit RGBA (`Ktx2Writer`). Either way the levels are supercompressed with zstd, which libktx inflates when it loads |

`imported/index.xml` says which source each artifact came from and which other files it was made from (a glTF's `.bin` and its textures):

```xml
<Imports version="1">
  <Import source="models/car.gltf" artifact="imported/models/car.gltf.amodel" key="9c1e…">
    <Dependency path="models/car.bin"/>
    <Dependency path="models/wood.png"/>
  </Import>
</Imports>
```

An image that a model reads is part of that model and is not imported on its own. Files and folders whose name starts with a dot, and the `imported` folder itself, are never imported.

## GPU texture compression

Textures are cooked to **Basis Universal UASTC** inside KTX 2: one file for every platform, which `Ktx2Loader` transcodes when it loads to what the GPU samples (BC7 on desktop, ASTC 4x4 on mobile and Apple, RGBA8 where
neither exists; `TextureCaps` says which). The encoder is the one in libktx (KTX-Software 4.4.2, [Native libraries](../native/native-libraries.md#libktx-apogeenativektx)); `BasisEncoder` drives it:

- **Colour and data maps**: UASTC level 2 (the default speed/quality trade-off) with RDO at quality 1.0, which shapes the blocks so that zstd squeezes them further, then zstd level 18 over the levels.
- **Normal maps** (a material's normal texture inside a model, or an image whose settings say `normalMap="true"`): UASTC level 3, no RDO, and linear whatever `srgb` says: errors in a normal show as lighting noise, so these take
  the slower and more accurate settings. They stay RGB, so shaders read them as they always did. (libktx has a "normal map" mode that keeps two channels as RRRG, which would need the shaders and a BC5 target to go with it;
  it is not used.)
- **sRGB or linear** is written in the file (the data format descriptor's transfer function); `ContentLoader` reads it from there, so a caller cannot get it wrong. The mip chain is the one the importer built (sRGB averaged in linear light).
- A texture with a side under 4 pixels stays RGBA8 (a block would be bigger than the texture). `compression="none"` in the settings keeps RGBA8 for anything, such as a UI image that must not show block artifacts.
- The encoder is deterministic (the same bytes on any number of cores) and **slow**, which is what the import cache is for: nothing is encoded again until the image, its settings or the encoder settings (part of the importer's
  fingerprint) change.

Measured on the 11 MB `BoomBox.glb` sample (four 2048 x 2048 PNGs; Linux, 28 threads, Release):

| | RGBA8 + zstd (`compression="none"`) | UASTC (the default) |
| --- | --- | --- |
| `.amodel` artifact | 11.7 MB | **5.5 MB** (5.2 MiB) |
| in a `--cooked` pack | 11.7 MB | **5.5 MB** |
| textures on the GPU (all levels) | 85.3 MiB | **21.3 MiB** (BC7: 4x less) |
| first import | 1.0 s | 18.7 s (139 s of CPU) |
| import with nothing changed | 0.07 s | 0.07 s |

The artifact used to be about the size of its source; now it is half, and a GPU holds a quarter of the memory.

## When something is imported again

Each artifact has a **key**: the SHA-256 of the importer's version, the asset's `.import.xml` settings, the asset itself, and every file the import read for it. `--import` makes an artifact
again only when its key is different, so changing a texture of a model, or a model's buffer, re-imports the model, and touching nothing re-imports nothing. An artifact whose source is
gone is deleted. A failed import (a file that is not a PNG, a settings file that is wrong) is reported with its file, does not stop the others, exits with code 2, and leaves the artifact the
game had (and its index entry) until the source is fixed.

### Settings

An `<asset>.import.xml` next to an asset (checked against the engine's schema) changes how it is imported; every attribute is optional:

```xml
<Import srgb="false" mips="false" optimize="false"/>
```

`srgb` (default true) is for images: colour maps are sRGB, normal maps, roughness and masks are linear. `mips` (default true) builds the mip chain. `compression` is `uastc` (the default) or `none`, for images
and for the textures of a model; `normalMap` (default false) says that an image is a normal map (linear, encoded for accuracy). `optimize` (default true) is for models: merge duplicate vertices and reorder for the GPU's
vertex cache. A model's textures take their sRGB or linear setting, and whether they are normal maps, from how its materials use them.

For a **shader**, `<Variant>` children each list the preprocessor defines of one more version to compile (the version with no defines is always compiled); the shader is loaded in a variant by passing the same defines to `ShaderLibrary.Load`:

```xml
<Import>
  <Variant><Define name="SHADOWS"/><Define name="QUALITY" value="3"/></Variant>
</Import>
```

## Loading

`ContentLoader` is what a game calls: it takes the artifact when there is one and the source otherwise, so the same call works in the editor (sources), in a development run that has imported
(artifacts) and in a shipped game (artifacts in a pack). A mod's files are not imported by the game, but a mod may be imported as a folder of its own and ship its `imported/` (a mod that brings compiled
shaders does); the loader finds it at `mod://id/imported`:

```csharp
var content = new ContentLoader(files);
ModelData car = content.LoadModel("res://models/car.gltf");
TextureData ground = content.LoadTexture("res://textures/ground.png", srgb: true, caps);
```

It does not check that an artifact is current: importing is the step that does (and hot reload, in the editor, runs it when a file changes). The textures of an imported model or image are transcoded for the GPU in `ContentOptions.TextureCaps` / the `caps` argument, as those of a source model are.

## Packing

`--pack … --cooked` leaves out every source that has an artifact, the files it was made from and its `.import.xml`, and packs the rest with `imported/`. It needs an import first. A shipped game
therefore holds `models/car.gltf.amodel` and `textures/ground.png.ktx2`, not the glTF, the `.bin` and the PNGs, and `ContentLoader.LoadModel("res://models/car.gltf")` finds the artifact by the
path of the source it replaced.

## Shaders

Shipped games never load the Slang compiler. `./build.sh aot` publishes the runtime and then runs `Apogee.Runtime --import` on its `Shaders` folder, which writes `Shaders/imported/*.ashader`; the
renderers load those (`ShaderLibrary`, [Rendering](../graphics/rendering.md#where-shaders-come-from-shaderlibrary)). In development nothing needs importing: a shader without an artifact is compiled and cached, as before. The bundle records the
engine's shader ABI, so a mod's old compiled shader is refused with "import it again" rather than crashing the driver; to make a mod's shaders, import the mod's folder with the engine's dev runtime. CI runs the published runtime
with the compiler libraries and every `.slang` source deleted, running the fixture game, to prove it.

## What gets cooked, and what does not (the plan)

**A rule first: source formats always load.** Mods are Luau and data, are never imported, and ship as folders or zips; the editor works on sources. So every loader keeps a source path
(as `ContentLoader` does), and cooking is only an optimization for the base game: a faster, sometimes smaller artifact that is a cache of the source. No format exists *only* cooked.

**Use a standard container where there is one**, and an engine format only where there is none. So no `.atex`: textures stay KTX 2, which the engine already reads (including GPU-compressed
data that is transcoded at load). That leaves `.amodel` as the one engine-specific binary so far, because glTF is an interchange format and there is no standard runtime one.

| Kind | Authored as | Cooked? | Artifact |
| --- | --- | --- | --- |
| Models | glTF | **yes, done** | `.amodel` (meshoptimizer) |
| Textures | PNG, JPEG | **yes, done**: Basis UASTC in KTX 2, transcoded to BC7/ASTC/RGBA8 at load | `.ktx2` |
| Shaders | Slang | **yes, done**: SPIR-V and Metal shading language and reflection ahead of time, in one bundle (DXIL has a slot), so a shipped game does not carry the compiler | `.ashader` |
| Materials | XML definition (a `Material` type with a schema: textures by path, factors, alpha mode) | **not for now.** They are small data, which mods must be able to author and patch like any definition; a model's built-in materials are already in `.amodel` | none (an `.amat` only if profiling asks) |
| Scenes, prefabs | XML: entities and their components, written and read through the reflection registry | **not for now**, and the same for everything that is a definition. Later, one *generic* cook: the decoded tree of any XML file (`DefValue`) as a binary file, so that scenes, prefabs and definitions share one importer and one loader | generic binary, e.g. `.abin` |
| Audio | WAV, FLAC, MP3 and Ogg Vorbis, decoded when loaded (streamed if asked) | already compressed by the formats; a cook would only trade load time for size | none |
| Scripts | Luau | no (a bytecode cache could come later; it is not needed to ship) | none |

**How it grows.** `AssetImporter` is a small registry. An importer (`IAssetImporter`) is a name, the extensions it takes, a **fingerprint** (part of the key: its version, and the versions and options of the tools it calls, such as Slang's, so
changing what it writes imports again), and a function from the source and its settings to an artifact, or to nothing for a file that is not an asset on its own. It reads files through the `ImportContext`, which records them as the
artifact's dependencies. `new AssetImporter(files, importers)` runs them in the order of the list (a file an earlier importer read is not imported by a later one); `AssetImporter.Defaults` is models and images and the runtime adds
`ShaderImporter`, which lives in Apogee.Rendering because Content does not reference Graphics. A new kind of asset is a new importer and a `Load…` that takes its artifact; the index, the keys, the sidecar settings, `--import`
and `--pack --cooked` stay as they are.

**What is open for you to decide:** whether a `.amat` is ever worth having. It blocks nothing.

## Tests

`Tests/Apogee.Content.Tests`: GPU compression (transcoding to BC7, ASTC and RGBA8 and how close it stays, the colour space read from the file, normal maps, determinism, the sidecar, a model's textures and the caps of the loader), the registry (order, an asset that is not one, what an earlier importer read, the fingerprint, a package's own artifacts, shader variants in the settings), the KTX 2 writer read back by libktx's reader (sRGB and linear, odd sizes, one level or a chain, the data format descriptor), the model file (every field bit for
bit, deterministic, a mesh that is not a triangle list, damaged and foreign files), and the importer (artifacts and the index, nothing imported twice, a change to the source, a dependency or
the settings, deleted sources, failures that keep the old artifact, hidden folders, a damaged index, settings checked against their schema) and `ContentLoader` (the artifact is the one
used, a source otherwise, a mod's files). `Tests/Apogee.Graphics.Tests`: the shader bundle (variants, damaged files, another ABI, backends that are not written yet). `Tests/Apogee.Rendering.Tests`: the shader importer and `ShaderLibrary` (dependencies, variants, the compiler never started when an artifact exists, no compiler no artifact, a mod's artifacts). `Tests/Apogee.Runtime.Tests` runs `--import` and `--pack --cooked` end to end, for shaders too.

## Status and re-importing one asset

`AssetImporter.StatusOf("models/car.glb")` says where one asset stands (`AssetStatus`: `NotImportable`, `NotImported`, `UpToDate`, or `Stale` when the file, its `.import.xml` or what the import read changed) and `Reimport(source)`
imports just that asset again, leaving the rest of the index as it is. The [editor](../tools/editor.md)'s asset browser uses them.
