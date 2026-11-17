# Prefabs

A prefab is a piece of a scene that can be made again and again: one entity with everything below it, saved as an XML document, a `.aprefab` file. A lamp with its model and its bulb, a crate with its
lid. A script or a tool instantiates it where it is needed, and every instance starts out the same. Like a [scene](scenes.md) it is XML, so a person can read it, a diff of two versions makes sense, and
it has a [UID](assets.md#where-a-uid-lives) in its root that survives renaming and moving the file.

This page is the format of a prefab file, the numbers its entities keep, and how to make instances and save a prefab from C# and Luau. An instance is, for now, a set of ordinary entities that came
from the file: changing them does not change the file, and a scene that holds one saves it as plain entities.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Prefab version="1" uid="uid://7h2k4m6p8q0rs" nextId="4">
  <Components>                                  <!-- only components a script defined, as in a scene -->
    <Component name="Flicker"><Field name="speed" kind="Float32"/><Field name="lamp" kind="Entity"/></Component>
  </Components>
  <Entity id="1" name="Lamp">                   <!-- exactly one root entity -->
    <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
    <ModelRenderer Model="res://models/lamp.glb|uid://3c5v7b9n1m2kx"/>
    <Entity id="2" name="Bulb">
      <Transform Position="0 1.8 0" Rotation="0 0 0 1" Scale="1 1 1"/>
      <Light Color="1 0.9 0.7" Intensity="80" Range="12" InnerCone="0" OuterCone="0" Kind="1"/>
      <Flicker speed="2" lamp="1"/>             <!-- an Entity field holds an id of this file: 1 is the root -->
    </Entity>
    <Entity id="3" name="Base"><Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
  </Entity>
</Prefab>
```

## The format

A prefab file is a scene with one root and stable ids. Everything inside `<Entity>` (components as elements, a field per attribute, child entities, field text, tags) is exactly the
[scene format](scenes.md#the-format); what differs is the root and the numbers.

- **`<Prefab version="1" uid="…" nextId="…">`** is the root. `version` is the format's version: a file from a newer engine (a larger number) is refused. `uid` is the file's identity, given when it is
  first saved or imported and kept after.
- **One `<Entity>`** is the root of the prefab, with everything else below it. A file with none or with two is a `FormatException`.
- **`<Components>`** is as in a scene: the components that scripts defined, so that the file defines them again when it is instantiated.
- **`id`** is the entity's number in the file: a whole number from 1 up, used once in the file. Entity-typed fields refer to it. Unlike a scene's, **it does not change when the file is saved**
  (see below). The id of the root need not be 1.
- **`name`** is optional, as in a scene. The root's name is the name the instance's root gets (a free one, `Lamp 2`, when its parent already has a `Lamp`).
- **`nextId`** is the id the next new entity takes. It is above every id in the file and is never lowered. A file written by hand without it, or with one that is not above every id, gets it worked out
  from the highest id (and the engine logs a warning), which is only wrong if the last entity was deleted.
- A prefab keeps its instances' references inside it: an Entity field of the file means an entity of the same instance, so two lamps each point at their own root.
- The component names `PrefabInstance` and `PrefabMember` are written by the engine, never to a file; a file that has them is skipped with a warning. `Entity` and `Override` cannot be component names
  (`RegisterComponent` and `DefineComponent` refuse them): `<Entity>` is a child entity, and `<Override>` is how an instance of a prefab will hold its changes.
- An `<Entity>` with a `prefab` attribute (an instance of one prefab inside another) is not expanded yet: it is skipped, with everything under it, and the engine logs a warning.

`prefab.xsd` (embedded in `Apogee.Ecs`, and at `Source/Apogee.Ecs/Scene/prefab.xsd`) describes the structure; the writer is tested against it.

## Ids stay the same

A scene numbers its entities `1..n` in order every time it is saved, so an id means nothing from one save to the next. A prefab's ids are the opposite on purpose: scenes that instance the prefab will name
its entities by id, and renumbering would silently point them somewhere else.

- Loading a prefab tags every entity it makes with **`PrefabMember { Id, Prefab }`**: its id in the file and the file (a handle of the world's [library](#the-library)). The root also gets **`PrefabInstance { Prefab }`**.
- **Saving** (`SavePrefabFile`) keeps the id of every entity that is a member of that file. A new entity (one made in code, or in an editor) takes `nextId` and raises it, and is tagged a member right away, so
  saving again keeps its id. An id that was deleted is never used again: `nextId` is read from the file being overwritten and kept.
- A copy of a member has its original's id until it is saved: the first of two with the same id keeps it, and the other takes a new one.

The two components are plain data and need no remapping: `Clone`, `Snapshot`, `Restore` and undo copy them as they are, and a [snapshot](../core/ecs.md) names the prefab by its file, as it does a
model, so it is still the same prefab in the next session.

## From C#

```csharp
Entity lamp = world.Instantiate("res://prefabs/lamp.aprefab", parent);   // parent is optional
Entity same = world.Instantiate("uid://7h2k4m6p8q0rs");                   // the file that has that UID, wherever it moved to

world.PrefabOf(lamp);                  // "res://prefabs/lamp.aprefab|uid://7h2k4m6p8q0rs"; null for an entity that is not an instance root
world.IsPrefabInstance(lamp);          // true
world.InstanceOf(world.Children(lamp)[0]);   // lamp: the instance an entity is part of, or none

world.SavePrefabFile("res://prefabs/crate.aprefab", crate);   // one entity and everything under it
```

- **`Instantiate`** takes a path, a `uid://…` or `path|uid://…` (the text form of an [`AssetRef`](assets.md)), or a handle (`world.Instantiate(uint prefab)`). It returns the root, and the entities under it are made
  in the order of the file. It reads without a game's per-mod confinement. Errors: a `uid://` no file has is a `ContentException`, a missing file a `FileNotFoundException`, a file that is not a prefab (another
  root, none or two roots, a bad or repeated id, a newer version) a `FormatException`, and a world without `Files` an `InvalidOperationException`. Nothing is made when it fails.
- **`SavePrefabFile(path, root)`** writes the entity and everything below it. The path must end in `.aprefab`. The file keeps its UID (a new one gets a new UID), the ids are kept as above, and an Entity
  field that points at something outside what is saved is left out. An instance of another prefab inside the tree is written as plain entities. A path that is not in a writable source is an `UnauthorizedAccessException`.

### The library

`world.Prefabs` is a `PrefabLibrary`: one number per prefab file, the same for a path, a `uid://…` or both. It is made when first asked for from the world's `Files` and `Assets`; a host that runs
several worlds hands them one library, so their numbers agree. `Load(pathOrRef)` gives the handle; `RefOf`, `PathOf` and `Document` go back from it, and a prefab is read from its file once.
A reference that names no file still gets a handle, which keeps its text (and is that file's handle once the file exists), so an instance of a prefab that is missing keeps what it was made from.
The handle is only meaningful in the run that made it: scenes and snapshots hold the reference (the field's `[Property(Asset = "prefab")]` is saved through the world's `prefab` asset type).

## From Luau

```lua
local lamp = world:instantiate("res://prefabs/lamp.aprefab", parent)   -- or "uid://…"; parent is optional
print(world:prefab_of(lamp))     -- res://prefabs/lamp.aprefab|uid://7h2k4m6p8q0rs
print(world:prefab_of(some))     -- nil: not the root of an instance
```

`instantiate` returns the root. Like `load_scene_file` it reads without the per-mod confinement check of `fs.read_text`: a script can instantiate a prefab of any package or of the game by its path or
UID, in a shipped game too (a pack holds it at the same path). The failures above are Luau errors. There is no Luau call that writes a prefab file.

## Files

`.aprefab` is an [authored asset](assets.md#where-a-uid-lives) (`AuthoredAssets.Extensions`): `--import` gives a file without a `uid` one by adding the attribute to its root, the asset database finds a
prefab by its UID, `--pack` stores it (`--cooked` as [cooked XML](import.md#cooked-xml), at the same path), and renaming or moving it keeps its UID (and what refers to it by `path|uid` still finds it).
