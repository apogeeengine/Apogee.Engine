# Scenes

A scene is the entities of a world, with their components, saved as an XML document: a `.ascene` file. The editor saves and opens them, a game's entry scene is one, and scripts and tools make and read
them as text. Because a scene is XML like the rest of the game's data, a mod can patch it (see [Patching a scene](#patching-a-scene)), and a diff of two scenes is something a person can read.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Scene version="1">
  <Components>                                   <!-- only components a script defined -->
    <Component name="Ticker">
      <Field name="count" kind="Int32"/>
      <Field name="target" kind="Entity"/>
      <Field name="look" kind="AssetRef" assetType="model"/>
    </Component>
  </Components>
  <Entity id="1" name="ground">
    <Transform Position="0 -0.5 0" Rotation="0 0 0 1" Scale="1 1 1"/>
    <ModelRenderer Model="res://models/ground.glb|uid://8c1q0w3e5r7ty"/>
    <Entity id="2" name="crate">                  <!-- children nest; there is no parent attribute -->
      <Transform Position="1.25 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
      <Ticker count="3" target="1"/>
    </Entity>
  </Entity>
  <Entity id="3" name="camera">
    <Transform Position="0 2 -6" Rotation="0 0 0 1" Scale="1 1 1"/>
    <MainCamera/>                                  <!-- a tag: an empty element -->
  </Entity>
</Scene>
```

## The format

- **`<Scene version="1">`** is the root. `version` is the format's version; a scene from a newer engine (a larger number) is refused. It is 1, and 2 for a scene that holds a
  [prefab instance](#prefab-instances) (so that an engine that does not know instances refuses the file instead of loading them empty); the engine reads both. A scene file may also carry a `uid`
  attribute, its [identity](assets.md#where-a-uid-lives); text made by `SaveScene` has none, because a copy must not carry one.
- **`<Entity id="…" name="…">`** is an entity. `name` is optional. Its children are its components, as elements, and its child entities, as more `<Entity>` elements; a child entity is under its parent,
  so there is no `parent` attribute. `Entity` is therefore not a name a component can have (`RegisterComponent` and `DefineComponent` refuse it, and `Override`, which [prefab](prefabs.md) instances use).
- **`id`** is the entity's number in the file, not its id in a world. Entity-typed fields refer to it, and a [patch](#patching-a-scene) matches on it. It is a whole number from 1 up: an entity
  with another `id` (or none) loads, but nothing can refer to it, and when two entities have the same one, references go to the first and the engine warns. This is stricter than version 1 scenes were read
  before prefabs: two entities with one `id` used to send references to the later one, and `0`, a negative number, `+5` or `05` were accepted as ids and are not now (a field that holds such a number warns).
  Scenes the engine writes are not affected.
- **A component** is an element named after it, and each of its fields an attribute named after the field. A tag (or a component with no fields) is an empty element. A field the element does not
  name is zero when the scene is loaded.
- **What is written:** every field of every component, so that a scene reads without the code at hand and diffs cleanly; components in the order the entity has them, entities in hierarchy order
  (parents first, children in creation order). Saving a loaded scene again gives the same text.
- **What is left out:** `WorldTransform` (derived), components given to `IgnoreInScenes` (the host leaves out physics `Body`, which only means something in the run that made it), and an
  `Entity` or `AssetRef` field that points at nothing the file can name (an attribute left out; see below).

### Field text

The text is the same in every culture. Booleans are `true` and `false`, integers are decimal, floats are the shortest text that reads back as the same number (`NaN`, `Infinity` and `-Infinity`
are the special values), and a `Vector3`, a `Double3` position or a `Quaternion` is its numbers separated by spaces (`1 2 3`, `0 0 0 1`). 64-bit integers keep all their digits. `FieldText.Format` and
`FieldText.Parse` (in `Apogee.Reflection`) do this for every kind that needs no context.

Two kinds need a world:

- **`Entity`** is the `id` of the entity it points to in the same file; for an entity inside a [prefab instance](#prefab-instances) its address, `2/3`. A field that points outside the scene (a branch saved on
  its own, an entity not in the file) has its attribute left out, and loads as no entity. An `id` the file does not have, and text that is not an id or an address, is the same.
- **`AssetRef`** is an [asset reference](assets.md): `path|uid://…`, a path alone, or `uid://…` alone. In memory the field is a number, a handle that only means something in this run (a model's number in the
  model library); the scene holds what it names. The host says how each asset type is saved with `RegisterAssetType`, and registers `model` for `ModelRenderer.Model`. When the path leads to no file, the
  UID finds the asset where it was moved; a reference that leads nowhere leaves the field 0 and logs a warning, and the scene loads on. An asset the type cannot name (a model made in code has no path) is
  left out.

### Components made by scripts

A component that a script defined (`world:component("Ticker", { … })`) has no code behind it, so the scene carries its fields in `<Components>`: the name, and per field its name and its `kind`
(`Bool`, `Int8`, `UInt8`, `Int16`, `UInt16`, `Int32`, `UInt32`, `Int64`, `UInt64`, `Float32`, `Float64`, `Vector3`, `Double3`, `Quaternion`, `Entity` or `AssetRef`). Loading a scene into a world that has not
heard of the component defines it. A field of kind `AssetRef` also says what it names with `assetType="model"` (C#: the `assetTypes` argument of `World.DefineComponent`); without it the field cannot
be saved, and the world warns once. A component of the engine or the game (a C# struct) is not listed: its layout is the code's. If the world already has a component of that name with other fields,
the scene's is skipped with a warning and the existing one is used.

### Reading

Loading is forgiving, so that a scene outlives the code around it. A component or an attribute the world does not have is logged (with the line) and skipped; an attribute whose text is not a
value of its field is logged and the field stays zero; a name that is taken under the same parent gets a free one (`crate 2`). What is an error (a `FormatException`) is a document that is not a scene:
XML that is not well-formed, a root other than `<Scene>`, and a `version` newer than the engine's.

## Prefab instances

An `<Entity>` with a `prefab` attribute is an instance of a [prefab](prefabs.md): a whole piece of scene (an entity with everything below it) that lives in a `.aprefab` file and is made again at each
instance. The element does not repeat what the prefab holds, only what this instance changes:

```xml
<Scene version="2">
  <Entity id="1" name="Player">…</Entity>
  <Entity id="2" name="Corner" prefab="res://prefabs/corner.aprefab|uid://9w1e3r5t7y9ua">
    <Transform Position="10 0 4"/>                         <!-- a field of the root changed: the other fields stay the prefab's -->
    <Override entity="4" name="Old bench"><Transform remove="true"/></Override>   <!-- another entity of the prefab, found by its address: renamed, and one component removed -->
    <Override entity="3" remove="true"/>                   <!-- an entity removed with everything below it -->
    <Entity id="4" name="Trash can">…</Entity>              <!-- an entity added below the root -->
  </Entity>
  <Entity id="5" name="Switch"><Door target="2/2"/></Entity>   <!-- a scene entity pointing into the instance: entity 2 of the prefab instanced as 2 -->
</Scene>
```

- **`prefab`** is an [asset reference](assets.md) to the file: a path, a `uid://…`, or both (the engine writes both, so the instance still finds a prefab that moved). The `id` and `name` are the instance
  root's, as for any entity; the name is the scene's, and always written.
- **Component elements** directly inside change the root of the instance; **`<Override entity="address">`** changes any other entity of it; **`<Entity>`** children are added below the root.
  [Prefabs](prefabs.md#overrides) has the rules: how an entity is addressed (`4`, `2/2`), what `append="true"` and `remove="true"` mean on a component, and what is kept aside when the prefab no longer
  has what an override names.
- **Addresses in Entity fields.** An entity of the scene is its `id`; an entity that a prefab made is the address of its instance followed by its id in the prefab (`2/3`: entity 3 of the prefab that is
  instance 2), and `2/2/1` reaches into a prefab nested in it. What an override or an added entity holds is in the scene's numbering, so `lamp="1"` in the scene is the scene's entity 1; inside the
  prefab file the same text is the prefab's entity 1. Entities the instance made have no `id` of their own in the scene, which is why they are named by address.
- **Loading** makes the prefab's entities, applies the element on top, and creates the added entities after them. The entities of an instance are ordinary entities: `world:children`, components, scripts
  and physics see no difference, and `world:prefab_of(entity)` tells the root of an instance. An instance whose prefab file cannot be found or read is one empty entity, with a warning, and what its
  element holds is kept: [saved again](prefabs.md#how-overrides-are-stored), the scene has the same instance, so a prefab that is missing for a moment (a disabled mod) costs the scene nothing.
- **Saving** writes the instance back as the same kind of element, working out what differs from the prefab. A scene numbers its own entities again at each save, so the `id` of an instance and of what
  is added to it may change from one save to the next; what refers to them follows, and a scene saved and loaded again saves to the same text. The entities an instance made keep the place the prefab gives
  them, and a member of an instance cannot be moved under another parent.
- A branch of an instance saved or copied *without its root* (the clipboard of a selected member) is written as plain entities; a whole instance is one element.

## The schema

`scene.xsd` (embedded in `Apogee.Ecs`, and in the repository at `Source/Apogee.Ecs/Scene/scene.xsd`) describes the structure for people and editors: the root, `<Components>`, the entity element with its `id`
and `name`, and the form of a `uid`. It cannot describe the components, because those are the registry's; they are checked when the scene is loaded. The writer is tested against the schema.

## From C#

```csharp
string text = world.SaveScene();                  // every user entity
string branch = world.SaveScene(crate);           // one entity and everything under it: a prefab, the clipboard
List<Entity> made = world.LoadScene(text);        // in file order, parents first
world.LoadScene(text, parent: holder);            // the scene's entities become children of holder

XmlNode document = world.SaveSceneDocument(world.Roots());   // the same as a tree, to patch or inspect
world.LoadScene(document);
```

`LoadScene` returns the entities it made, so a caller can move or select them. `SaveScene` and `LoadScene` read and write text; the editor and the host decide where it is kept.

## Scene files and their UID

A scene kept as a file has an identity, a [UID](assets.md#where-a-uid-lives) in the `uid` attribute of its root, that survives renaming and moving the file. The world reads and writes files through
`World.Files` and `World.Assets`, which the host sets (a game's session and the editor do; a bare `new World()` has neither and cannot load a file).

```csharp
world.SaveSceneFile("res://scenes/level.ascene");              // every user entity
world.SaveSceneFile("res://prefabs/crate.ascene", crate);      // one entity and everything under it
List<Entity> made = world.LoadSceneFile("res://scenes/level.ascene", parent);
world.LoadSceneFile("uid://4k2m9x7q1w3ze");                    // the file that has that UID, wherever it is now
```

- **`SaveSceneFile`** keeps the UID of the file it overwrites: the old file's `uid` is read and written again, so a reference to the scene keeps meaning the same asset after every save. A new file gets a
  new UID. `SaveScene()` makes text with no `uid`, because a copy must not carry the identity of a file.
- **`LoadSceneFile`** takes a path or `uid://…`. It reads the file with `XmlFile.Read` ([cooked XML](import.md#cooked-xml)), so a scene cooked into a pack (`--pack --cooked` keeps its path) loads the same as the text one. A `uid://` that no
  file has is a `ContentException`, a missing file a `FileNotFoundException` and a file that is not a scene a `FormatException`.
- A scene that is renamed or moved keeps its UID, so `LoadSceneFile` and `SaveSceneFile` find it by `uid://…`; the [asset database](assets.md) is read again with `Refresh()` after files move on disk (an import does it).

The game's [entry scene](game-manifest.md) is loaded with `LoadSceneFile`.

## From Luau

```lua
local text = world:save_scene()
for _, entity in world:load_scene(text, parent) do   -- parent is optional
    print(world:get_name(entity))
end

local made = world:load_scene_file("res://scenes/level.ascene", parent)   -- or "uid://…"; parent is optional
```

`save_scene` returns the text and `load_scene` the list of entities it made; text that is not a scene is an error. `load_scene_file` returns the same list from a file. It reads without the per-mod confinement check of `fs.read_text`, as `models.load` and `ui:load_document` do:
a script can load a scene of any package or of the game by its path or UID. It loads a scene cooked into a shipped pack as well as the text file, at the same path.
`fs.read_text` of a cooked scene gives binary, so a script that wants the scene's XML itself reads it with [`xml.load`](defs.md#from-luau). There is no Luau call that writes a scene file; a script saves text with `fs.write_text`, which
keeps the confinement of a mod's files (and a file written that way has no UID until the next import gives it one).

## Cooked scenes

`--pack --cooked` stores each `.ascene` as [cooked XML](import.md#cooked-xml) at the path of its source: the same tree (names, attributes, text, the lines of the source) in a binary encoding, which is not tied to the
components the engine had when it was cooked, and keeps the `uid` of the root. `LoadSceneFile` and `world:load_scene_file` read both forms, and so does the asset database that finds a scene by `uid://…`, so a game
needs no change to ship its scenes cooked. The fixture game of the tests (`Tests/Fixtures/ShippedGame`) loads `mod://core/scenes/start.ascene` this way from an encrypted cooked pack. A scene in a mod is never
cooked (mods are Luau and data, shipped as folders and zips), and `LoadScene(text)`, which takes text, is for text.

## Patching a scene

A scene is an ordinary XML document, so it merges like a definition ([`DefDatabase.Merge`](defs.md)): children are matched by name and `id`, an attribute of the patch replaces the original's, and what the patch
does not name stays. A patch that moves entity 2 and nothing else:

```xml
<Scene version="1">
  <Entity id="2">
    <Transform Position="5 0 0"/>
  </Entity>
</Scene>
```

Merged into the scene above, `crate` keeps its rotation and scale and its `Ticker`; only `Position` changes. Because entities nest, a patch for a child names its parents too, each by `id`.
`patch`, `remove` and `append` are the attributes the merge reserves, so a field cannot be called any of them if its component is meant to be patched.

The merged tree loads with `world.LoadScene(node)`.
