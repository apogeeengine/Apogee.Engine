# Prefabs

A prefab is a piece of a scene that can be made again and again: one entity with everything below it, saved as an XML document, a `.aprefab` file. A lamp with its model and its bulb, a crate with its
lid. A script or a tool instantiates it where it is needed, and every instance starts out the same. Like a [scene](scenes.md) it is XML, so a person can read it, a diff of two versions makes sense, and
it has a [UID](assets.md#where-a-uid-lives) in its root that survives renaming and moving the file.

This page is the format of a prefab file, the numbers its entities keep, how to make instances and save a prefab from C# and Luau, and how a [scene](scenes.md#prefab-instances) or another prefab
holds an instance: as one element that names the prefab and says only what it changes. An instance is a set of ordinary entities that came from the file: changing them does not change the file, and a
scene saves the changes as *overrides* of the prefab.

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
- An `<Entity>` with a `prefab` attribute is an instance of another prefab inside this one; see [Nested prefabs](#nested-prefabs). Its `id` is in this file's numbering like any entity's.

`prefab.xsd` (embedded in `Apogee.Ecs`, and at `Source/Apogee.Ecs/Scene/prefab.xsd`) describes the structure; the writer is tested against it.

## Ids stay the same

A scene numbers its entities `1..n` in order every time it is saved, so an id means nothing from one save to the next. A prefab's ids are the opposite on purpose: scenes that instance the prefab will name
its entities by id, and renumbering would silently point them somewhere else.

- Loading a prefab tags every entity it makes with **`PrefabMember { Id, Prefab }`**: its id in the file and the file (a handle of the world's [library](#the-library)). The root also gets **`PrefabInstance { Prefab }`**
  (and is a member of its own file, with the id of the file's root). A prefab nested in another has its own entities tagged with *its* file, and its root is a member of the outer file with the id of the
  instance element there.
- **Saving** (`SavePrefabFile`) keeps the id of every entity that is a member of that file. A new entity (one made in code, or in an editor) takes `nextId` and raises it, and is tagged a member right away, so
  saving again keeps its id. An id that was deleted is never used again: `nextId` is read from the file being overwritten and kept. What is added to a nested instance is numbered in the file too.
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
world.IsPrefabMember(world.Children(lamp)[0]);   // true: part of an instance without being its root, so it cannot be moved under another parent

world.SavePrefabFile("res://prefabs/crate.aprefab", crate);   // one entity and everything under it
```

- **`Instantiate`** takes a path, a `uid://…` or `path|uid://…` (the text form of an [`AssetRef`](assets.md)), or a handle (`world.Instantiate(uint prefab)`). It returns the root, and the entities under it are made
  in the order of the file, the prefabs inside it expanded. It reads without a game's per-mod confinement. Errors: a `uid://` no file has is a `ContentException`, a missing file a `FileNotFoundException`, a file
  that is not a prefab (another root, none or two roots, a bad or repeated id, a newer version, a root that is itself an instance of another prefab) or that would make more than `world.MaxLoadEntities` entities a
  `FormatException`, and a world without `Files` an `InvalidOperationException`. Nothing is made when it fails.
- **`SavePrefabFile(path, root)`** writes the entity and everything below it. The path must end in `.aprefab`. The file keeps its UID (a new one gets a new UID), the ids are kept as above, and an Entity
  field that points at something outside what is saved is left out. An instance of another prefab inside the tree is written as one element with its [overrides](#how-overrides-are-stored), numbered in this
  file; the root is written as an ordinary entity even when it is an instance (a prefab saved from an instance of itself holds its entities). A tree that holds an instance of the file being written, directly or
  through another prefab, is an `InvalidOperationException`, and nothing is written. A path that is not in a writable source is an `UnauthorizedAccessException`.

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

## Nested prefabs

A prefab can hold instances of other prefabs: an `<Entity>` with a **`prefab`** attribute, the same text an `AssetRef` has (a path, a `uid://…`, or both). Its `id` is its number in the file, like any
entity's, and its `name` is the name the root of the nested instance gets.

```xml
<Prefab version="1" uid="uid://9w1e3r5t7y9ua" nextId="5">
  <Entity id="1" name="Corner">
    <Transform Position="0 0 0" Rotation="0 0 0 1" Scale="1 1 1"/>
    <Entity id="2" name="Lamp" prefab="res://prefabs/lamp.aprefab|uid://7h2k4m6p8q0rs">   <!-- an instance: its id is in the corner's numbering -->
      <Transform Position="2 0 0"/>                                     <!-- a patch of the lamp's root: only the fields that change -->
      <Override entity="2"><Light Intensity="120"/></Override>          <!-- the lamp's Bulb (entity 2 of the lamp), in the lamp's numbering -->
    </Entity>
    <Entity id="3" name="Lamp 2" prefab="res://prefabs/lamp.aprefab|uid://7h2k4m6p8q0rs">
      <Transform Position="-2 0 0"/>
      <Override entity="3" remove="true"/>                              <!-- this one has no Base -->
    </Entity>
    <Entity id="4" name="Bench"><Transform Position="0 0 3" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
  </Entity>
</Prefab>
```

Loading a scene or a prefab, and `Instantiate`, expand every instance element by the rules below (`PrefabExpander`, in `Apogee.Ecs`) and then create all the entities. Prefabs are read from the world's
[library](#the-library), so an instance names its prefab by a path, a UID or both and finds the same file whichever it uses.

**Addresses.** Ids repeat from one prefab to the next (every lamp has an entity 2), so an entity is named by a path of ids, its **address**: `4` is entity 4 of the document, `4/2` entity 2 of the prefab
instanced as entity 4, `4/3/2` entity 2 of the prefab instanced as entity 3 of the prefab instanced as entity 4. Each id is a whole number from 1 up with no leading zero, and an address has at most 17
segments (the document's, and one for each of the 16 levels of nesting). The root of an instance is the instance's own address; `address/rootId` (the root's id in its own file) is accepted as another name for it.
An address does not follow the hierarchy: if the author of the prefab moves entity 3 under another entity, `…/3` is still entity 3.

**Scopes.** An Entity-typed field means an entity *of the document that holds it*. Inside a prefab file that is an id of the file (`lamp="1"` is the lamp's root), and it becomes an address under wherever the
file is instanced (`5/2` for the lamp that is entity 2 of a corner instanced as 5). Everything an instance element and its overrides hold is written in the scope of the document the instance is in, so a field in
an override inside `corner.aprefab` is an id of the corner, and one in a scene is an address of the scene. A field that names nothing (an id nobody has, text that is not an address) stays as it is and, as in
a scene, means no entity.

**Inner before outer.** The instances inside a prefab are expanded first, each with its own patches and overrides; the instance that holds the prefab then applies its overrides on top. An outer override
may reach into a nested instance with a longer address (`2/2` in an instance of the corner is the lamp's bulb).

**Names.** Siblings get unique names the way the scene loader gives them: the members of the prefab in file order, then the entities that were added, and a name that is already taken gets a number after it (`Part`, `Part 2`, `Part 3`…).

## Overrides

An instance element changes its prefab in three ways: component elements directly inside it, `<Entity>` children, and `<Override>` elements for any other entity of the instance.

```xml
<Entity id="2" name="Corner" prefab="res://prefabs/corner.aprefab|uid://9w1e3r5t7y9ua">
  <Transform Position="10 0 4"/>                         <!-- a modified field on the root: the other fields stay the prefab's -->
  <Health max="200" current="200" append="true"/>        <!-- a component the prefab does not have, written whole -->
  <Override entity="4" name="Old bench"><Transform remove="true"/></Override>   <!-- a renamed entity, and a removed component -->
  <Override entity="2"><Health max="50" current="50" append="true"/></Override> <!-- a component added to the nested lamp's root -->
  <Override entity="2/2"><Light Intensity="40"/><Flicker lamp="1"/></Override>  <!-- into the nested lamp's bulb; lamp="1" is entity 1 of THIS scene -->
  <Override entity="3" remove="true"/>                   <!-- a removed entity, and everything under it -->
  <Override entity="2/3">                                <!-- an entity added under a member, numbered in this document -->
    <Entity id="3" name="Sign"><Transform Position="0 0.5 0" Rotation="0 0 0 1" Scale="1 1 1"/></Entity>
  </Override>
  <Entity id="4" name="Trash can"/>                      <!-- an entity added directly under the root -->
</Entity>
```

- **`<Override entity="address">`** is required and is an address relative to the instance (the root of the instance is its id in the prefab, `1` for a prefab whose root is entity 1; the root's own patches are component elements directly
  inside the instance element). Its children are component patches and added `<Entity>` elements; `name` renames the
  entity and `remove="true"` removes it with everything under it (not the root of the instance: remove the instance instead).
- **A component element** changes a component the entity has: its attributes replace the prefab's, and the fields it does not name keep the prefab's value. `remove="true"` removes the component.
  `append="true"` adds the component, written whole; if the prefab has (or later gets) the component, the instance's wins. These three words, with `patch`, are the same ones [`DefDatabase.Merge`](defs.md) uses
  and are never kept in what an instance expands to.
- **Added entities** are ordinary entities of the document that writes them (their ids are numbered there, so a scene's own ids, or the prefab file's), and may themselves be instances. They come after the
  prefab's own children.
- There is no order to override: siblings are in creation order, the prefab's entities first and then the added ones.

**Strict matching.** An override applies only to what is there. The expander reads each element and, when it cannot apply it, keeps it aside instead of guessing:

- an address that names nothing (the prefab lost that entity, or an earlier override removed it), a malformed or missing address, or an `<Override>` with an attribute this version does not know: the whole element;
- a patch of a component the entity does not have (the prefab dropped it: a half component would be made), or a `remove` of one it does not have: that component element, under an `<Override>` with the same
  address (what could apply did);
- an entity added under a member that another override of the same instance removes: its element, under an `<Override>` with the address of the removed member.

Each is logged with its file and line (once per line however many instances there are, and at most 200 lines per load, then one line saying so), and kept as written so that saving the scene does not lose it;
if the prefab gets the entity or component back, the override applies again. A prefab that is not there is the same: the instance is a single entity with nothing in it, and every element of the instance
element is kept. What is kept belongs to the document the expansion was asked for: the leftovers of an instance element inside a *prefab file* are that file's, are logged, and are worked out again at every
expansion, so they do not appear on the scene's instance.

`DefDatabase.Merge` is used for the part it fits: the fields of one component (and the `remove`/`append` words). It is *not* used to find entities, which it would do by name and `id` among direct children only:
an entity that the author moved would stop matching, ids repeat across nested prefabs, and a patch that matches nothing would add a half-made entity. Overrides address entities by path instead.

## Limits

A prefab file is read by anyone who installs a mod, so expansion is bounded and never trusts a file:

- **Nesting depth: 16** (`World.MaxPrefabDepth`). A chain of prefabs (one inside the next) may be 16 long; the 17th is missing (an instance of it is one empty entity, with a warning). Entities may be nested at most 128 deep in what
  results (the limit a document already has), or the whole expansion fails.
- **Cycles.** A prefab that contains itself, directly or through others (`a → b → c → a`), is not followed: the instance that would repeat it is missing, and the warning shows the chain. The rest expands.
- **Entity budget: 262,144** (`World.MaxLoadEntities`, settable). The expansion counts each entity as it is about to make it and stops with a `FormatException` when a load (a scene with all its instances,
  its own entities are not counted: they are the size of the file and cannot multiply, and a scene with more than the limit entities loaded before there were prefabs) would go over, so a few small files that nest
  ten instances sixteen levels deep cost at most the budget, not memory without end. Nothing is made when a load fails. The budget is one number for a whole load, and a save gives each instance a budget of its
  own (the one an `Instantiate` of it had), so a world that was made of many instances can always be saved.
- **Work budget: 64 steps for each entity of the budget (16,777,216).** A file's content is repeated for every instance of it, so entities alone do not bound a load: ten thousand overrides in a prefab
  that a thousand prefabs hold are ten million merges for a handful of entities. Every component or override handled, attribute copied, component merged (by its size) and name tried while making a name
  unique counts, and the expansion stops with a `FormatException` over the limit, with the same effect as the entity budget. An entity may have any number of components within it (the lookup is by name,
  not a scan). A load, each `Instantiate`, and each save (which expands the instances again to see what they changed) takes a new budget; one that failed is spent.
- **Missing prefabs.** A reference that names no file, a file that cannot be read, is not a prefab (another root element, none or two root entities, a repeated id, a newer version), or whose root
  entity is itself an instance of another prefab (an entity would be the root of two instances: put it under an entity of its own) leaves the instance missing, as above.
- **Addresses.** An instance at an address of 17 segments (the longest there is) is missing, since what is in it would need 18. A field that holds text that is not an address means no entity, so a
  loader resolves only text that is a valid address; an entity that nothing can refer to has an address that starts with `~`, which no file can write.
- **Ids.** Ids of entities an `<Override>` adds count in the prefab's numbering like any other: they may not repeat one, and `nextId` stays above them. An entity with no usable id, or one that repeats an
  address, is made but cannot be referred to.

## When the prefab changes

A prefab file changes while worlds hold instances of it: it is edited by hand, an editor saves it, a game that runs with [`--watch`](hot-reload.md) sees it change on disk. The instances follow it **in place**,
and the entities they are made of keep their ids, so a selection, a reference from another entity and a variable in a script stay valid.

```csharp
PrefabReload result = world.Prefabs.Reload("res://prefabs/lamp.aprefab");   // reads the file again: Reloaded, Unchanged or Failed (with why)
PrefabUpdate? update = world.ApplyPrefabUpdates();                           // makes the instances follow it, at a point where changing entities is safe
```

`PrefabLibrary.Reload` reads the file again and, when it is a prefab that loads and does not contain itself (directly or through the prefabs it holds), makes it the library's document and raises `Changed`.
A world that follows the library (the world's `Prefabs`, which `world.Prefabs = library` shares between worlds, and a prefab written by `SavePrefabFile` announces itself the same way) writes down which prefab
changed and what it was made from; nothing else happens until **`ApplyPrefabUpdates`**, which a host calls at a frame boundary after it has reloaded every file that changed: all of them are one update. A
running game does this by itself when it is started with `--watch`; a host that must not have entities change under it (a drag, a dialog) sets `HoldPrefabUpdates` and applies when it is ready.

What an update does to an instance, which it finds through the [dependency record](#nested-prefabs) of its expansion (so an instance of a prefab that holds the changed one is updated too, once, and so is an
instance that was missing when its prefab appears):

- What the instance changed is **kept where its target still exists**: a field set to another value stays that value and the rest follows the prefab (the instance has Max 40, the prefab goes from 9 to 90
  and its Regen from 0.5 to 2: the instance has 40 and 2). An entity the instance removed stays removed, a name it changed stays.
- What the prefab **gained** is made in every instance; what it **lost** is destroyed, with what the instance had added below it.
- What an override said about something the prefab lost is **kept aside** in the instance, as when a scene is loaded: it applies again if the entity comes back, and a scene saved in between holds it.
- Components that scenes leave out (`IgnoreInScenes`, a physics body) are not touched, and what an entity outside the instance names inside it keeps naming it. A field outside the instance that named an
  entity the prefab dropped is empty now and remembers what it named, as after a load.
- Entities that stay are changed rather than made again; a field that has the value it should is not set again.

The result is what saving the scene before the change and loading it after would give: an update is a load of the instance's own document that changes the entities in place instead of making new ones, so
the limits (nesting, [`MaxLoadEntities`](#limits) and the budget) are the load's. Nothing the update does can lose what an instance holds, and an instance it cannot update is **left exactly as it was**.

- **A file that is not usable keeps the old prefab.** One that does not parse, is not a prefab, repeats an id, or holds an instance of itself (through a loop of any length, or nested too deep) is not adopted:
  `Reload` says `Failed` and why, nothing is announced, and the instances stay. A file that is **deleted** is the same (an editor that saves by replacing the file, a version control branch that is switched),
  and when it is back unchanged `Reload` says `Unchanged`. A file that was never there and appears makes the instances that were missing whole.
- **An instance that cannot be updated** (the new prefab expands to more than `MaxLoadEntities`, or an entity of the instance is not part of what the instance and its prefab say) is in
  `PrefabUpdate.Failed` with the reason and is not changed; the library has the new prefab, so saving it writes the difference as overrides (the old values pinned): nothing is lost.
- **Instances made after the change was announced** (and before the update) are made from the new file and are left alone.

`PrefabsChanged` (a C# event, and the `PrefabChanged` signal for scripts, with the prefab's path) says what an update did: the prefab files, the instances updated, the entities it made and destroyed, and the
instances it left. **A tool treats an update as a change that did not come from the user and cannot be undone.** An editor with an undo history keeps the history for what the update did not touch, and ends
it (or at least does not undo past the update) for the instances in `Updated`: a command that names an entity in `Destroyed` names nothing now, and one that would bring such an entity back brings it back as a plain
entity. Selections of destroyed entities are cleared, those of the others stay valid.

## How overrides are stored

An instance is saved as the element of [Overrides](#overrides) above, written for you: the world compares each instance with what its prefab expands to on its own, and writes what differs. Nothing is
recorded while you edit, so a change made by the editor, by a script or by code is saved the same way, and a field set back to the prefab's value is no longer an override.

- **What is compared** is the value the loader would make, not the text. `1.0` and `1` are one number, a field an element leaves out is zero, a `true` may be written `1`; an asset reference with a UID and one
  without (or after the file moved) is the same asset, and two references with different UIDs are two assets; an entity field is the entity it names, wherever it is written from. A component is compared
  field by field and only the fields that differ are written, a field that was set to nothing as `""`.
- **Which entities pair up.** The live instance and the expansion are paired from the root down by the id every member keeps (`PrefabMember`). A member the live instance no longer has is written as a removal
  (`<Override entity="…" remove="true"/>`, only the topmost of a removed branch). An entity of the instance that is not a member (made in the scene, or a copy of a member) is an *added entity*, written under the
  override of its parent (or directly in the element for the root's children), and it may itself be an instance. A member moved under another parent by code (the editor does not allow it) is written as both: the
  member removed from where it was, and a plain copy under its new parent. A member of the wrong prefab for its place, or a second entity with a member's id (a copy of its components), is an added entity.
- **The text is stable.** The element is written in a fixed order: the root's changes (components in the prefab's order, those added last), an `<Override>` for each changed entity in the prefab's order (name,
  then components, then entities added below it), the entities added to the root, and the elements kept aside last. A scene that is loaded and saved again is byte for byte the same text.
- **Kept aside is kept.** What an instance element held that could not be applied (see [Overrides](#overrides)) is not in the live entities, so it is stored with the instance root (it travels with
  [snapshots](../core/ecs.md), which is why Play and Stop keep it, and with a clone of the root) and written back **first**, before what the live entities say: an instance element is applied in the order it
  is written, so an old element that has become applicable (a patch of a component the entity did not have, and has now) cannot win over what was done since. It is written as it was read, except that XML
  comments and layout are not kept, and that the numbers in it follow the scene: an entity it adds gets a number of this save, and an entity field in it that named an entity of the scene (or the instance
  root, and what follows, of an instance whose prefab is missing) is written with that entity's address now, empty if the entity is gone. Fields of components the world does not know (the prefab that defines
  them is missing) cannot be told from numbers and keep the number they had.
- **References to what was not made.** A field of the scene that names an entity inside an instance whose prefab is missing (`2/2`), or one that a kept element would add, loads as no entity; the world
  remembers what it named and writes it back as the address that entity will have, so it names it again when the prefab is back. The same is kept for an asset field that did not load: it is 0, it is not an
  override of the prefab's model, and a plain entity writes the reference it had.
- **A reference that is not one.** A `prefab` attribute that is not an asset reference (a damaged UID, text) makes an empty instance with the text kept as it was written, and everything the element holds kept.
- **A prefab that moved on.** The comparison is with the prefab *as it is now*. New fields, components and entities of the prefab are not overrides when a scene is *loaded* with the new prefab, since the
  instance had nothing to say about them; a deleted entity that the scene had overrides for keeps them aside, and they apply again if the entity returns. The live entities of a world that was running when
  the file changed are [brought up to date](#when-the-prefab-changes) before anything is saved from them; if that is not done (a file changed behind the world's back, with no `Reload`), the next save of an
  instance of it writes every difference as an override: the new entity as a removal, the old value of a changed field pinned.
- **A prefab that cannot be read** while the world holds its instance (the file was deleted, or replaced by one that contains itself) is compared as the single root it expands to: the live entities that
  came from it are written as additions and nothing is dropped. A scene loaded without the prefab keeps its element intact and saves it as it was. An instance whose prefab is missing is only its root: what is
  edited in it (a component added to the root, an entity added below it) is saved, and goes aside again when the scene is loaded again before the prefab is back, so a tool should treat it as read-only.

**What is never an override:** the order of siblings (they are in creation order, the prefab's entities first), `WorldTransform` and the other components scenes leave out (`IgnoreInScenes`), components the world
cannot describe, a name that was cleared, `PrefabInstance` and `PrefabMember` (the engine makes them), and what the prefab's own nested instances could not apply (that belongs to the prefab's file).

A scene that holds an instance is [version 2](scenes.md#the-format); one without is written as version 1, as before. The schemas (`scene.xsd`, `prefab.xsd`) describe `prefab` and `<Override>`, but
leave what is inside an `<Override>` open: the rules above are what check it when a scene is read.

## Files

`.aprefab` is an [authored asset](assets.md#where-a-uid-lives) (`AuthoredAssets.Extensions`): `--import` gives a file without a `uid` one by adding the attribute to its root, the asset database finds a
prefab by its UID, `--pack` stores it (`--cooked` as [cooked XML](import.md#cooked-xml), at the same path), and renaming or moving it keeps its UID (and what refers to it by `path|uid` still finds it).
