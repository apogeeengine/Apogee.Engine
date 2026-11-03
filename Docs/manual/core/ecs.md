# ECS

`Apogee.Ecs` wraps a Flecs world (`Apogee.Native.Flecs`). `World` is an `EngineObject`, so scripts get a handle to it, and its
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
`FieldAccess.Read`/`Write` (in `Apogee.Reflection`) read and write a field of the component's memory as a `Value`, with range checks.
The pointer API (`GetPointer`, `EnsurePointer`, `SetPointer`) works on any component id.

### Editing: hierarchy, copies, snapshots and scenes

What an editor needs of a world, in `World.Editing.cs` and `World.Scene.cs`. "**User entities**" are the ones a game or an editor made (`IsUserEntity`): not Flecs' own entities (modules, systems, observers),
not the entities of registered components and the reflection data under them, not what is marked `EditorOnly` (the editor's own camera) or under such an entity.

- **Hierarchy:** `Roots()`, `Children(entity)` (in creation order) and `Subtree(entity)` (parents first). `ComponentsOf(entity)` lists the components and tags the registry describes (not relationship pairs or
  the name), with their `ComponentInfo`; `Components` is every component the world has registered. `SetParent` and `GetParent` were there before; `Trs.InLocalOf` gives the local transform that keeps an
  entity where it is under a new parent.
- **Copies:** `Clone(entity, parent?)` copies the components and the whole branch under the same parent, naming the copies `name (copy)`, `name (copy 2)` so they stay unique.
- **Snapshots:** `Snapshot()` takes every user entity (ids with their generation, names, parents, component bytes), `SnapshotTree(entity)` one branch. `Restore(snapshot)` puts the world back: user entities
  that are not in it are destroyed, the ones that are come back *under the same ids* (made alive again with `ecs_make_alive` when they were destroyed) with their components, names and parents as they
  were; `RestoreEntities(snapshot)` only brings back and overwrites, destroying nothing (an undo of a deletion). The same ids are what keep a selection and an undo history valid across a play session.
  A snapshot is bytes: a component that holds a handle of the run (a physics body's id) comes back with the old value, so such components are left to the code that made them.
- **Scenes** (`.ascene`): `SaveScene()` writes the user entities as JSON text, `SaveScene(entity)` one branch, and `LoadScene(text, parent?)` creates the entities (under `parent` when given) and returns them in the
  order of the file. The format is ours, written field by field from the registry's descriptions with `Utf8JsonWriter` and read with `JsonDocument`: no reflection, so it works in NativeAOT. Flecs' own
  JSON was not used: it would carry the entities the engine registers for its types and systems. An entity has a number in the file, its `parent` and entity-typed fields refer to those numbers (a reference to
  an entity that is not in the scene is saved as `null`), and 64-bit values that a JSON number cannot hold go as text. Components a script defined are written with their schema (`"components"`) and defined
  again when the scene is loaded into a world that has not heard of them. On loading, a component or a field the world does not have is logged and skipped, a name that is taken gets a free one (`name 2`), and
  text that is not a scene is a `FormatException`. `WorldTransform` is derived and never saved; `IgnoreInScenes(name)` leaves others out (the host does it for physics `Body`), and
  `RegisterSceneConverter(component, field, write, read)` saves a handle as what it names (the host saves `ModelRenderer.Model`, a number in this run's model library, as the model's path).
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

`Tests/Apogee.Ecs.Tests` (needs the `flecs` native library): the transform hierarchy (`TransformTests`), registration and reflection JSON, set/get/ensure, tags, runtime components, names
and parents, ids that stay dead, queries (with `Not`, with structural changes inside), systems in phases, failing systems, observers, and
disposal; and `EditingTests`: which entities are the user's, hierarchy order, the components of an entity, clones, snapshots and restoring (ids that come back, names that swapped, a deleted branch),
and scenes (a round trip with the hierarchy, references, doubles and script components, converters, unknown things, bad files). The scene round trip also runs in the NativeAOT smoke test
(`Apogee.Runtime` with no arguments) and from Luau (`Tests/Luau/ecs.test.luau`).
