# Content loading

`Weft.Content` is how the engine gets its files: the virtual file system (below), and the readers that turn model and texture files into plain CPU data that the
renderer uploads (no GPU types except the `PixelFormat` enum). XML definitions, packs and mods build on the file system and come next.

## File system

A `VirtualFileSystem` gives the engine, and the scripts of mods, one view of where files are. Paths are `scheme://a/b/c.txt`:

| Prefix | Holds | Writable |
| --- | --- | --- |
| `res://` | the game's content: its folders, and its packs and patch packs over them | no |
| `user://` | what the player's machine keeps for the game: saves, settings, downloaded mods | yes |
| `mod://<id>/` | one mod's own files | no |

The program decides what is mounted where, with `Mount(prefix, source)`; a source is an `IFileSource`: a `DirectorySource` (a folder, read-only unless asked to be writable),
a `ZipSource` (a zipped mod), and later a pack. A path is looked up in every source mounted at a prefix of it, **the one mounted last first**, so a mod's folder or a patch
pack overrides what is under it. A source can also *remove* a file (a patch pack does): it is gone for everything mounted before that source. Prefixes nest
(`res://mods/cool` over `res://`), and a folder that only exists because something is mounted below it (`mod://`) is listed.

```csharp
var vfs = new VirtualFileSystem();
vfs.Mount("res://", new DirectorySource("Content"));
vfs.Mount("user://", new DirectorySource(VirtualFileSystem.DefaultUserDirectory("Acme", "Truck"), writable: true));
vfs.Mount("mod://cool", new ZipSource("mods/cool.zip"));

byte[] bytes = vfs.ReadAllBytes("res://textures/ground.png");
string text = vfs.ReadAllText("mod://cool/mod.wmod");                 // UTF-8, without a byte order mark
foreach (string file in vfs.EnumerateFiles("res://defs", "*.xml", recursive: true)) { ... }   // sorted by ordinal order
vfs.WriteAllText("user://saves/slot1.xml", xml);                     // user:// only; res:// is read-only
```

Rules that keep a path meaning the same everywhere (and a script inside its mounts): a path has a lowercase scheme, `/` separators, and segments that are plain names: no
`.` or `..`, no empty segment, no `\ : * ? " < > |` or control characters, nothing that ends in a dot or a space, and no Windows device name (`nul`, `con.txt`). A path
that breaks a rule is a `VfsException` (an `IOException`) when it is read or written, and simply not there for `Exists` and its kin. Paths are **case-sensitive**,
even on a file system that is not (a texture asked for as `Ground.PNG` would load on Windows and fail on Linux): write them as they are on disk, in lowercase by convention.
Only a writable source takes a write, a missing file is a `FileNotFoundException`, and a write goes to a hidden temporary file of a unique name, is flushed to the disk and is renamed over the target, so that a crash or a full disk leaves the old file whole (a symbolic link is written through and stays a link; a file with several hard links is replaced by a new file). `EnumerateFiles` takes `maxDepth` and `maxDirectories` for a folder that may hold links leading back to a folder above them. Zip entries whose
names break the rules are skipped (with a warning), so no entry can name a place outside the zip. A `DirectorySource` follows links inside its folder as the OS does: do not
mount a folder that you do not trust to be free of links.

`CanWrite(path)` says whether a write would be accepted, and `Move(from, to)` moves a file to a path that has nothing yet (inside one folder source it is a single rename that
refuses to replace anything; between two sources it is a copy and a delete). Tools use them to rename an asset (see [renaming and moving assets](assets.md#renaming-and-moving-assets-for-tools)).

`VfsPath` has the helpers: `Parse` (strict), and lenient `Normalize`, `Combine`, `DirectoryOf` for resolving what a file refers to, where `..` climbs but stops at the root of the scheme.
`TryGetPhysicalPath` gives the real path of a file that a folder holds (to watch it, or for a library that only takes paths).

### What reads through it

- **Scripts:** `new LuauVm(new LuauVmOptions { Files = new VfsScriptFiles(vfs) })` and `vm.AddRoot("res://")` (or `"mod://cool"`): `require("./x")`, `.luaurc` aliases and `init.luau`
  work on virtual paths, and chunk names, errors and `OwnerOfModule` see them (`res://main.luau:12`).
- **Models:** `ContentOptions.ReadFile = vfs.ReadAllBytes` and `GltfLoader.Load("res://models/car.gltf", options)`. The textures and the external `.bin` buffers a model refers to are
  read through it too (cgltf is given our allocator and file reader), relative to the model, so a model in a pack or a zip loads like one on disk.
- **Textures:** `ImageDecoder` and `Ktx2Loader` take bytes: pass them `vfs.ReadAllBytes(path)`.
- Not yet: shaders. Slang compiles from real files and finds its imports itself, so the engine's shaders stay next to the program.

## File names

The engine's own formats have an extension that starts with `w`; the formats everyone knows (`.luau`, `.slang`, `.glb`, `.gltf`, `.png`, `.xsd`, `.rml`…) keep theirs. Binary formats start with a
four-letter magic: the extension in capitals, and `WXMB` for cooked XML. [File formats](formats.md) is the reference for each of them: layout, versions, and which are planned and not read yet.

| File | Holds | Form | Magic |
| --- | --- | --- | --- |
| `.wscn` | a [scene](scenes.md) | XML; binary XML when cooked | `WXMB` when cooked |
| `.wprf` | a [prefab](prefabs.md) | XML; binary XML when cooked | `WXMB` when cooked |
| `.wimp` | the [import settings](import.md#settings) of the file it sits beside (`car.glb.wimp`) and its UID | XML | none |
| `.wgam` | a game's [manifest](game-manifest.md) (`game.wgam`, at the root of the game folder) | XML | none |
| `.wmod` | a [package's or mod's manifest](packages.md) (`mod.wmod`, at the root of the package, also inside a zip or a Workshop folder) | XML | none |
| `.wpak` | a [pack](packs.md) (`game.wpak`; a bigger one goes on in `game_001.wpak`…) | binary | `WPAK`, or four bytes of a hash of the key when encrypted |
| `.wmdl` | an imported model (`imported/car.glb.wmdl`) | binary | `WMDL` |
| `.wshd` | a compiled shader (`imported/lit.slang.wshd`) | binary | `WSHD` |

A file that still has one of the names these formats had before is not read as any of them: it is a file like any other, and nothing converts it (a scene or a prefab is plain XML, so renaming it is enough;
import and pack again for the rest).

## glTF models

```csharp
ModelData model = GltfLoader.Load("Models/Sponza/Sponza.gltf", new ContentOptions
{
    TextureCaps = new TextureCaps(Bc: device.SupportsBc, Astc: device.SupportsAstc),
});
```

`GltfLoader` reads `.gltf` (with its `.bin` and images) and `.glb`, and turns the **default scene** into a `ModelData`:

- `Meshes` — one `MeshData` per glTF primitive that the scene uses: `Vertex[]` (48 bytes: position, normal, tangent+handedness, UV0 — the
  struct the vertex shader pulls from a buffer), `uint[]` indices, a material index and bounds. A mesh used by several nodes is loaded once.
- `Instances` — each mesh placed in the world with its node hierarchy's world matrix (`System.Numerics` row-vector convention).
- `Materials` — glTF's metallic-roughness model: factors and `TextureRef`s (texture index, UV set, `KHR_texture_transform`), emissive already
  multiplied by `KHR_materials_emissive_strength`, alpha mode and cutoff, double-sided, `KHR_materials_unlit`.
- `Textures` — decoded and ready to upload (below). The same image used as a base color (sRGB) and as a normal map (linear) is two entries.
- `Lights` — `KHR_lights_punctual` lights in world space (position, direction, color, intensity in lux or candela, range, cone angles).

What the file leaves out is filled in as glTF specifies: no normals gives flat normals; a normal-mapped material without tangents gets them
computed from the UVs. By default (`OptimizeMeshes`) meshoptimizer merges duplicate vertices and reorders both buffers for the vertex cache and
memory fetch, keeping exactly the same triangles. Triangle strips and fans become lists; points and lines are skipped with a warning. Only
`TEXCOORD_0` is read. Draco and `EXT_meshopt_compression` models are refused with an error that says so; any problem with a file is a
`ContentException` naming the file.

## Textures

PNG, JPEG, BMP and TGA go through stb_image to RGBA8 with a full mip chain, built with a box filter; sRGB images are averaged in linear light, as
the GPU's filtering does, and alpha never is. **KTX 2** files (the glTF `KHR_texture_basisu` extension, or any `.ktx2`) are read by libktx, and
Basis Universal data is transcoded at load to what the GPU has: BC7 (`TextureCaps.Bc`), else ASTC 4x4 (`TextureCaps.Astc`), else RGBA8. The file's
own mip levels are kept. A `TextureData` carries the `PixelFormat` (the sRGB variant for base color and emissive, linear for the rest — decided by how
the material uses it, not by the file), the size, the bytes of every mip, and the sampler state (filter, mipmaps, wrap).

`Png.Encode(width, height, rgba)` writes a PNG, for screenshots and test files.
