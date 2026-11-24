# ECS

`Weft.Ecs` wraps a Flecs world (`Weft.Native.Flecs`). `World` is an `EngineObject`, so scripts get a handle to it, and its
`[Method]`s (`create_entity`, `lookup`, `progress`…) are in the registry. Use a world from one thread.

## Components

A component is an unmanaged struct marked `[Component]` (see [Reflection](reflection.md)); its fields are the memory layout:

```csharp
[Component] public struct Position { public Vector3 Value; }
[Component] public struct Health { public int Current; public int Max; }
[Component] public struct Frozen { }                       // no fields: a tag

using var world = new World();
Entity e = world.CreateEntity("hero");
world.Set(e, new Health { Current = 5, Max = 10 });
ref readonly Health health = ref world.Get<Health>(e);      // valid until the next structural change
world.Ensure<Health>(e).Current = 8;                       // a writable reference; the component is added when missing
world.Modified<Health>(e);                                 // tell observers about it
world.Add<Frozen>(e);                                      // tags: Add, Has, Remove
```

The first use of a component registers it in the world: an entity with the component's name, its size and alignment, and its
fields as Flecs reflection data (`world.ToJson(value)` prints `{"Current":5,"Max":10}`; Flecs' own tools can read it too). Entity
ids are `Entity` structs. Ids are never reused as such: deleting an entity and creating another gives a different id, and
`IsAlive` is false for the old one.

### Components a script defines

`world.DefineComponent("Spin", [("speed", FieldKind.Float32), ("axis", FieldKind.Vector3)])` lays the fields out in name order
(each aligned to its own size, a vector to four bytes), so a schema always gives the same layout. The same schema again returns the same
component; another schema under a known name is an error. `GetComponentInfo(id)` gives the layout, and
`FieldAccess.Read`/`Write` (in `Weft.Reflection`) read and write a field of the component's memory as a `Value`, with range checks.
The pointer API (`GetPointer`, `EnsurePointer`, `SetPointer`) works on any component id.

### Enum fields

A field declared with an enum type (`public LightKind Kind;`), or an integer field marked `[Property(Enum = typeof(LightKind))]`, is stored as the integer of the enum's underlying type, which is also its `FieldKind`
(`UInt8` for an enum of `byte`). `FieldDef.Enum` names its registered `EnumInfo`, which an inspector uses to list the members. Nothing else knows: Flecs describes it as that integer, scripts read and write a
number, and a scene writes the number (`Kind="1"`), so renaming or adding a member never breaks a saved file. A number with no member (a flags value, or a value from a newer version of the game) is kept as it is.
See [reflection](reflection.md#what-an-inspector-shows).

### Asset fields

A `uint` field marked `[Property(Asset = "model")]` has the kind `FieldKind.AssetRef` and `FieldDef.AssetType = "model"` (see [reflection](reflection.md#asset-fields)). In memory it is four bytes, a `uint` handle
into the host's library of that asset type; Flecs describes it as a 32-bit number, `FieldAccess` reads and writes it as a number, and scripts see a number. The kind is what tells a scene file
or an inspector that the number names an asset. `DefineComponent` takes the kind too (`("model", FieldKind.AssetRef)`) and, in its `assetTypes` argument, the asset type of each such field by name (`new Dictionary<string, string> { ["model"] = "model" }`); a scene carries it as `assetType="model"` in the component's schema, so a script-defined component saves its asset fields too. Luau's `world:component` cannot name an asset type yet.

A scene does not store the handle, which only means something in the run that made it. The host says what each asset type's handles name with
`RegisterAssetType(type, toRef, fromRef)`: `toRef` turns a handle into the `AssetRef` it names (null for a model made in code, which has no path), `fromRef` turns a reference into this run's handle,
loading the asset when needed. The host registers `model` (`ModelRenderer.Model`) with the model library and the [asset database](../content/assets.md), so a scene holds
`Model="res://models/car.glb|uid://4k2m9x7q1w3ze"`: the path is tried first, and when its file is gone the UID finds it where it moved. A scene written before UIDs holds the path alone, which loads as well. A reference that
leads to no asset leaves the field 0 with a warning, and the scene loads on. A field of a type nobody registered (and a script-defined field with no `assetType`) is left out of the scene, with one warning per field.

The same converters are public for a tool that edits a handle (the editor's asset picker): `world.AssetRefOf("model", handle)` gives the `AssetRef?` a handle names and `world.AssetHandleOf("model", reference)` gives
the handle of this run for a reference, loading the asset when the type needs to. `AssetRefOf` is null for handle 0, for a type nobody registered and for a handle that names nothing that can be saved; `AssetHandleOf` is 0
for an empty reference, for an unregistered type, and for an asset the converter does not find, all without a warning (asking is not saving, and an editor asks often); only a converter that throws because the asset cannot be loaded is logged.

A field that is 0 because its scene named an asset that did not load keeps the reference text, and a save writes it again, so that the reference works when the file is back. An editor shows it with
`world.UnresolvedAssetOf(entity, "ModelRenderer", "Model")` and, when the person replaces or clears the field, drops it with `world.SetUnresolvedAsset(entity, component, field, null)` (passing the old text back undoes that); without it, clearing a broken field would be undone by the next save.

`Entity` is not a component name: `RegisterComponent` and `DefineComponent` refuse it (`InvalidOperationException`), because a scene file writes a child entity as `<Entity>`.

### Editing: hierarchy, copies, snapshots and scenes

What an editor needs of a world, in `World.Editing.cs` and `World.Scene.cs`. "**User entities**" are the ones a game or an editor made (`IsUserEntity`): not Flecs' own entities (modules, systems, observers),
not the entities of registered components and the reflection data under them, not what is marked `EditorOnly` (the editor's own camera) or under such an entity.

- **Hierarchy:** `Roots()`, `Children(entity)` (in creation order) and `Subtree(entity)` (parents first). `ComponentsOf(entity)` lists the components and tags the registry describes (not relationship pairs or
  the name), with their `ComponentInfo`; `Components` is every component the world has registered. `SetParent` and `GetParent` were there before; `Trs.InLocalOf` gives the local transform that keeps an
  entity where it is under a new parent.
- **Names:** a name is unique among the children of one parent, and among the top-level entities. `SetName` gives an entity a free name when a sibling has the one asked for (`Lamp`, `Lamp 2`, `Lamp 3`…; read
  it back with `GetName`), `SetParent` does the same when the new parent has a child of that name, and an empty name takes the name away (no name never clashes). `UniqueName(name, parent?)` says what
  a name would become. The loader, `Clone`, `Restore` and the editor's paste all use it. Names are compared as written (`lamp` and `Lamp` differ), and only the siblings count: a child and a top-level
  entity may share a name, and so may the entity and the engine's own entities of the same name (Flecs has one called `Empty` in its core module). The one exception is a top-level name that a component or
  system of the world already has (`Health`, `Transform`): Flecs keeps one entity of a name in a scope, so that entity would take its place in the index, and the user's entity gets `Health 2`. A load of many
  entities of one name takes as long as a load of distinct ones.
- **Copies:** `Clone(entity, parent?)` copies the components and the whole branch under the same parent, naming the copies `name (copy)`, `name (copy 2)` so they stay unique. A copy of an
  [instance of a prefab](../content/prefabs.md) is another instance; a copy of a branch of one, without its root, is plain entities (the entities' links to their prefab are kept only when the instance they belong to is copied too).
- **Snapshots:** `Snapshot()` takes every user entity (ids with their generation, names, parents, component bytes), `SnapshotTree(entity)` one branch. `Restore(snapshot)` puts the world back: user entities
  that are not in it are destroyed, the ones that are come back *under the same ids* (made alive again with `ecs_make_alive` when they were destroyed) with their components, names and parents as they
  were; `RestoreEntities(snapshot)` only brings back and overwrites, destroying nothing (an undo of a deletion). The same ids are what keep a selection and an undo history valid across a play session.
  Asset fields travel by reference: for each field of an asset type (`ModelRenderer.Model`, anything `[Property(Asset = "…")]`) the snapshot also keeps the `AssetRef` (path and UID) its handle named, through the
  type registered with `RegisterAssetType`. Restored into the world it was taken from, the bytes come back as they are; restored into another world (the new session after a play run), each such field is set to
  *that* world's handle for the same asset, so a model is still the same model although the new session numbered its models afresh. An asset that world cannot find, or that has no path or UID, comes back as 0
  with a warning. Any other handle of the run (a physics body's id) is still bytes and comes back with the old value, so such components are left to the code that made them. What a prefab instance's element held that could not be applied
  (see [prefabs](../content/prefabs.md#overrides)) travels with the instance's root, so Play and Stop, undo and redo do not lose it.
- **Scenes** (`.ascene`): `SaveScene()` writes the user entities as XML text, `SaveScene(entity)` one branch, and `LoadScene(text, parent?)` creates the entities (under `parent` when given) and returns them in the
  order of the file. `SaveSceneDocument(roots)` and `LoadScene(node, parent?)` do the same with an `XmlNode` tree, for code that patches or inspects it. Each component is an element and each field an attribute
  (`<Transform Position="0 -0.5 0" Rotation="0 0 0 1" Scale="1 1 1"/>`), a child `<Entity>` is a child entity, and the format needs no reflection, so it works in NativeAOT. Flecs' own JSON was not used: it would
  carry the entities the engine registers for its types and systems. Components a script defined are written with their schema and defined again when the scene is loaded into a world that has not heard of them.
  On loading, a component or a field the world does not have is logged and skipped, a name that is taken gets a free one (`name 2`), and text that is not a scene is a `FormatException`.
  `WorldTransform` is derived and never saved; `IgnoreInScenes(name)` leaves others out (the host does it for physics `Body`), and `RegisterAssetType` (see [asset fields](#asset-fields)) is how a field that holds
  a handle is saved as the asset it names: `ModelRenderer.Model`, a number in this run's model library, is saved as the model's path and UID. The format, the schema and how to patch a scene are in [Scenes](../content/scenes.md).
- **From scripts:** `world:save_scene()` returns the text, `world:load_scene(text, parent?)` the list of entities it made, `world:roots()` and `world:children(entity)` the hierarchy. A script reads and writes the file
  itself with `fs.read_text` and `fs.write_text`, which keeps the confinement of a mod's files.

### Transforms

Every world has `Transform` (local to the parent) and `WorldTransform` (in the world, computed top down once a frame by a system that every world installs), with a
`Double3` position: see [Math and the double-precision world](math.md). A query term can read a component from the entity's parent with
`new QueryTerm(id, QueryOperator.Optional, QuerySource.ParentCascade)`: the field is a single element shared by the table (`view.FieldPointer(index, size)`), and
parents are visited before their children. `World.ComputeWorldTransform(entity)` computes the world placement now, from the entity's `Transform` and its parents', for code that changes
transforms and needs the result before the next `Progress` (physics placing a body it was just given).

## Queries

```csharp
using Query<Position, Velocity> moving = world.Query<Position, Velocity>();
moving.Each((Entity e, ref Position p, ref Velocity v) => p.Value += v.Value);
int count = moving.Count();
```

A query matches the entities that have all of the components (up to four typed ones; `CreateQuery` takes any number of
`QueryTerm`s, with `QueryOperator.Not` to exclude a component). It is cached by Flecs. Iterating **defers** structural changes
(adding or removing components, deleting entities): they are applied when the iteration is over, so a callback may do them without
breaking it. `ForEachTable` gives a `TableView` for each table: `Entities`, `Count`, and `Field<T>(index)` for the component of the term with that
index. Dispose a query when done with it; the world disposes the rest.

## Systems and observers

```csharp
world.AddSystem<Position, Velocity>("Move", Phase.OnUpdate, (Entity e, ref Position p, ref Velocity v) =>
    p.Value += v.Value * world.DeltaTime);
world.Progress(1f / 60);
```

`Progress` runs one frame: the phases in order (`OnLoad`, `PostLoad`, `PreUpdate`, `OnUpdate`, `OnValidate`, `PostUpdate`, `PreStore`, `OnStore`),
and within a phase the systems in the order they were added. `AddSystem` also takes terms and a `TableAction` for the raw form scripts use.
Destroy the system's entity to remove it.

An **observer** runs at once when its event happens to an entity that matches: `AddObserver<Health>("Watch", ObserverEvent.OnSet, …)` (also
`OnAdd` and `OnRemove`).

**Exceptions** never unwind through Flecs. A system or observer that throws is recorded, the others still run, and `Progress` throws
when the frame is done: the exception itself when there was one, an `AggregateException` when there were several.

## Notes

- Flecs copies the names it is given (entities, struct members), and a system's callback is kept alive by a `GCHandle` that Flecs
  frees together with the system; a world that is destroyed frees them all. This is checked under NativeAOT by the runtime's smoke test,
  which CI runs.
- Relationship pairs are `World.Pair(first, second)`; the child-of relation and the other builtin ids are `Ecs.ChildOf`… in the Flecs binding.

## Tests

`Tests/Weft.Ecs.Tests` (needs the `flecs` native library): the transform hierarchy (`TransformTests`), registration and reflection JSON, set/get/ensure, tags, runtime components, names
and parents, ids that stay dead, queries (with `Not`, with structural changes inside), systems in phases, failing systems, observers, and
disposal; and `EditingTests`: which entities are the user's, hierarchy order, the components of an entity, clones, snapshots and restoring (ids that come back, names that swapped, a deleted branch);
`SnapshotAssetTests` (asset fields restored into a world that numbers its assets differently); and scenes (a round trip with the hierarchy, references, doubles and script components, converters, unknown things, bad files). The scene round trip also runs in the NativeAOT smoke test
(`Weft.Runtime --smoke`) and from Luau (`Tests/Luau/ecs.test.luau`).
