# Scripting

`Apogee.Scripting` runs Luau, the only scripting language: the engine is C#, and games and mods are written in Luau and data. It sits on the
Luau library and the layer over its C API described in [Native libraries](../native/native-libraries.md#luau-apogeenativeluau).

## The VM and its owners

```csharp
using var vm = new LuauVm(new LuauVmOptions { MemoryLimit = 256 << 20, TimeBudgetMilliseconds = 100 });
ScriptOwner mod = vm.CreateOwner("coolmod", isMod: true, memoryLimit: 32 << 20, timeBudgetMilliseconds: 20);

IReadOnlyList<Value> results = vm.Execute(mod, "@/mods/coolmod/main.luau", source);   // what the chunk returns
```

There is **one VM for the game and all its mods**. What separates them is the `ScriptOwner`: the base game's (`vm.BaseOwner`) or one mod's.

- **Memory:** everything an owner's scripts allocate is counted in the owner's memory category (up to 255 owners in a VM). Going over the owner's
  `MemoryLimit` is a memory error in that owner's code at its next call or loop iteration; the other owners are not affected. `MemoryLimit` of the VM
  is the hard limit for everyone. An owner that is over its limit has its garbage collected before its next call, so one failure does not
  make the next call fail too.
- **Time:** each call gets the owner's `TimeBudgetMilliseconds` (the VM's default when it has none). Once a call's time is up, every function
  call and loop iteration raises, so a script's own `pcall` cannot swallow it. A long native function (`string.rep` of a gigabyte) is not interrupted.
- **Coroutines** belong to the owner of the script that made them, and so do the objects they allocate.
- A module that a script requires runs as that script's owner, unless `LuauVmOptions.OwnerOfModule` (the packages) says whose file it is.

A failure is a `ScriptException` with a `Kind` (`Compile`, `Runtime`, `Timeout`, `Memory`) and the script's message; a runtime error has a traceback.
An error that crosses `coroutine.wrap` arrives as a plain runtime error whose message is "not enough memory".

## The sandbox

Every script runs in a thread of its own with its own globals over the VM's read-only ones (`luaL_sandbox`, `luaL_sandboxthread`): a script
cannot change `string` or `math`, and what it assigns to a global is not seen by the next script. The base library is Luau's, without
`getfenv`, `setfenv` and `loadstring`: code comes from `require`, never from a string. `print` and `warn` go to `LuauVmOptions.Output` (the log by default,
with the owner's id), and `Vector3(x, y, z)` makes a `vector` (`Quat` makes rotations: see [Math](../core/math.md)); the compiler turns calls with constant arguments into constants.

Scripts with `--!native` on their first line are compiled to machine code where the machine supports it (`NativeCodeEnabled`).

## require

Require-by-string, as Luau's RFC describes it:

| Request | Means |
| --- | --- |
| `./x`, `../x` | relative to the folder of the requiring file |
| `@alias/x` | the alias from the nearest `.luaurc` (`{ "aliases": { "lib": "./libs" } }`, comments allowed) in that folder or above it |
| `@self/x` | relative to the folder of an `init.luau` (an error from any other file) |
| `@engine/name` | a module of the engine, added with `AddEmbeddedModule` |

A module is `x.luau` or `x/init.luau`; having both is an error. Paths cannot leave the roots given to `AddRoot`. A module runs once in a thread of its
own and its single result is cached, so a module cannot yield into its requirer, one that returns zero or two values is an error, and a cycle is
reported (`cyclic require`). The requiring file is the nearest *Luau* function on the stack, so `pcall(require, "./x")` works from where it was written.
A chunk is a file when its name is `@path`; a `=name` chunk is not, so it can only require `@engine` modules.

Scripts are read through `IScriptFileSystem`: the real file system by default, or `VfsScriptFiles` for the content [file system](../content/content.md#file-system) (`res://`, `mod://<id>/`, zips, packs).
Add what scripts may load from with `AddRoot` (a folder, or a prefix such as `res://`); `./` and `../` never climb out of a scheme, and `@alias` comes from the nearest `.luaurc` as usual.

## Engine objects in scripts

The classes of the [registry](../core/reflection.md) are what scripts see. A class marked `Scriptable = false` is not there at all (an object of it is `nil`).

```lua
local actor = Actor.new()                       -- only for Creatable classes
actor.name = "rex"                              -- properties: strings, numbers, vectors, objects, lists, enums
actor.mood = "very_happy"                       -- an enum is the snake_case name of its member
actor.traits = { "fast", "tough_skin" }         -- a [Flags] enum is a list of names
local moved, speed = actor:try_move(Vector3(1, 0, 0), 2)   -- methods take ':'; a tuple gives several results
actor.hurt:connect(function(amount, who) print(amount) end)   -- signals
```

- **Objects are handles.** A script holds an object by its slot and generation (`SlotHandle`), never by a pointer. `==` and table keys work, because
  there is one userdata per live object. A destroyed object, or one whose slot was reused, raises `attempt to use a destroyed object`.
- **Types are checked.** Arguments and values are converted by the declared type, strictly (a number is not a string, a float is not an integer),
  and a mistake names the member: `bad argument #1 (word) to 'speak': expected string, got number`.
- **An object a script creates with `Class.new()` belongs to the script**: it is destroyed when the script no longer holds it, unless the engine called
  `Adopt()` on it. (The collector only notes it; `LuauVm.ProcessPending()` destroys, after every `Execute` and whenever a game loop calls it.) Every other
  object lives until the engine calls `Destroy()`.
- **Signals:** `obj.signal:connect(fn)` returns a connection with `:disconnect()` and `.connected`. A handler that fails is logged with its traceback
  and the emitter never sees it. A connection stays until it is disconnected or its object is destroyed (`ProcessPending` lets go of those).
- **Mods:** an object of a class with `ModsBlocked` raises `mod "x" can't use Steam: it is the base game's` when a mod's script uses it.
- **Static methods** are on the class (`Actor.make("rex")`); a `[ScriptLibrary("imgui")]` class is a read-only global table.
- **Functions** a script passes to the engine come back as `ScriptFunction`s. Calling one runs it as its owner, on a thread of the owner's, with the
  owner's time budget.
- **Entities** are numbers. A pair (a relationship) does not fit in the 53 bits of a number, so it is a table of two ids: `world:add(e, { likes, apple })`.
- `vector`s have `v:length()`, `v:normalized()`, `v:dot(w)`, `v:cross(w)` and the rest of Luau's `vector` library as methods.

A host gives scripts its objects with `vm.SetGlobal("world", world)`. The scripts compiled after that read `world.x` every time (Luau would otherwise
resolve `global.field` once, when a function loads, which is wrong for an object whose members change).
Types are registered when their assembly's module initializer runs; make sure the assemblies with described classes are loaded before the VM is made.

## ECS from scripts

`World` is a class like the others (`world:create_entity("hero")`, `world:lookup`, `world:progress(dt)`, `world.frame`, `world.delta_time`) and has the calls that take tables and
functions, written by hand and listed in the definitions:

```lua
local Position = world:component("Position", { value = "vec3", on = "bool" })    -- or world:component("Health") for one the engine registered
local e = world:create_entity()
world:set(e, Position, { value = Vector3(1, 2, 3) })
local p = world:get(e, Position)                    -- a copy: { value = ..., on = false }

world:each({ Position, { Frozen, "not" } }, function(entity, position)    -- views that write in place
    position.value += Vector3(0, 1, 0)
end)
world:system("Move", { Position, Velocity }, function(entity, pos, vel)
    pos.value += vel.value * world.delta_time
end, "on_update")                                   -- a real Flecs system, in a phase
```

Field types are `bool`, `i8` … `u64`, `f32`, `f64`, `vec3`, `dvec3` (a world position, narrowed to a `vector` in scripts), `quat` and `entity`. The view a callback gets is the component's memory, not a copy; it is only valid during the call
(`a component view is only valid during the call it was given to`). Writing through a view does not notify observers; `world:set` does. Structural changes inside
`each` are applied when it ends. An error in `each` reaches its caller. A system that fails is logged with its traceback and skipped for the rest of that frame, and
the other systems still run.

## Definitions for the editor

`DefinitionWriter.Write()` writes the API as Luau type definitions (`apogee.d.luau`) from the registry: classes as `declare extern type`, enums as unions of strings, signals as
`Signal<…>`, libraries and statics as `declare` tables, and the hand-written methods with the declarations they were registered with. A host adds the globals it sets
(`DefinitionOptions.Globals`: the runtime declares `world`) and can leave classes out (`HiddenClasses`: its smoke-test class). The runtime's `--luau-defs <folder>` writes the file
and the engine's Luau modules (`engine/test.luau`) to a folder (see [Runtime](runtime.md)).

To use it in VS Code with the luau-lsp extension: run `Apogee.Runtime --luau-defs Bin/defs`, add the file to `"luau-lsp.types.definitionFiles"`
(`{ "@apogee": "Bin/defs/apogee.d.luau" }`), and give each script folder a `.luaurc` whose `engine` alias points at `Bin/defs/engine`, so that `require("@engine/test")` resolves (at
run time the alias is built in; the `.luaurc` is only for tools). See `Samples/Scripting/.luaurc`. From a terminal:

```sh
luau-lsp analyze --platform=standard --definitions=@apogee=Bin/defs/apogee.d.luau Samples/Scripting Samples/Mods Bin/defs/engine
```

CI runs exactly that, on Linux, with a pinned luau-lsp (1.70.1), and fails on any error: the engine's own modules type-check against the generated file (the samples are run by hand, not in CI; they are written to type-check too).

Things the definitions cannot say, found by running luau-lsp:

- **Flag lists and field tables that may be omitted** are declared `({ Flags } | {})?` (and `({ [string]: FieldType } | {})?` for `world:component`). luau-lsp's default solver infers a
  literal such as `{ "no_move" }` as `{ string }` and refuses it against `{ Flags }?`; the union with an empty table takes the literal, nothing and nil in both solvers, and
  hover still lists the names. The cost is that a misspelled flag in an optional list is not reported. A required list (`{ Flags }`) is checked.
- **Vector methods** (`v:length()`, `v:dot(w)`, `v:cross(w)`) are not in the file: `vector` is a primitive of Luau, which a definitions file cannot extend, so luau-lsp reports them as
  missing keys. Use them from nonstrict scripts, or `--!nolint`; the arithmetic operators are fine.
- **`type` aliases** in the file (`Entity`, the enums) are not visible to scripts, only to the declarations. Scripts name the class types (`World`, `Animal`).
- **`world:lookup`** returns `0` for a missing entity, not nil, and `0` is truthy in Luau: compare with `== 0`.

## Tests

`Tests/Apogee.Scripting.Tests` (needs the `luau` native library): values, the sandbox, errors with tracebacks, time and memory limits per owner including
coroutines, native code, `require` with real files, the bridge (properties, methods, enums, lists, identity, lifetimes, signals, libraries, mods), the ECS from scripts, and the
definitions (an exact expected block). The runtime's smoke test runs the bridge and the ECS from a script under NativeAOT, which CI runs.
