# Hot reload

While a game runs in development (and in the editor, which uses the same pieces), changing a file takes effect without a restart: a script you saved, a definition, an image.

```sh
Apogee.Runtime --game Samples/Mods --watch
```

`--watch` goes with `--game`. It makes `res://` writable (so that a changed asset can be imported again into `res://imported`) and reloads, every frame it has something to do:

| What changed | What happens |
| --- | --- |
| a `.luau` file that a script has `require`d | the module is run again and **updated in place** (below) |
| a definition or a schema in a package's `Content` or `Schema` folder | the definitions are read again, `LuauVm.Defs` is replaced, the `defs.on_change` listeners are called |
| a `.glb`, `.gltf`, `.bin`, image or `.import.xml` | imported again when the project has been imported (`imported/index.xml`), then `HotReloader.AssetsChanged` says which, for the host to load them again |
| a `.slang` file | imported again when the project has been imported (so its `.ashader` stays current), then `HotReloader.ShadersChanged` says which. The runtime hands them to its `ShaderLibrary`, and the renderers rebuild the pipelines that use them, off the render thread and swapped in at a frame boundary ([Rendering](../graphics/rendering.md#rebuilding-pipelines-when-a-shader-changes)); a shader that does not compile keeps the old pipelines and shows its error in the dev overlay |
| a package's `mod.xml`, or an entry script (a package's `<Script>`, which runs once) | listed as *needs a restart*, with a warning in the log |

Files and folders that start with a dot, editors' temporary files (`~`, `.tmp`, `.swp`), and `imported/` itself are ignored. Zips and packs are not watched (they do not change under a running game).

## Modules are updated where they are

`LuauVm.ReloadModule` runs the changed file again and then makes the table that every script got from `require` *be* the new module: it gets the new fields and loses the ones the new code does not have (and the new metatable).
So every script that required the module sees the new functions, without being reloaded itself.

- **If the new code fails** (syntax error, runtime error, timeout) **nothing changes**: the old module stays, and so does everything it registered. Fixing the file reloads it. The error and its line are in the log and in `ReloadReport.Failures`.
- **What a module registers while it loads is taken back and registered again** by the new run: functions given to `devtools.on_gui`, signal connections, `world:system`s, `defs.on_change` listeners. (What a script registers *later*, from an event handler, is the script's to manage.)
  If the new run fails halfway, what it had registered is removed again and the old registrations stay.
- **A function copied into a local stays the old one**: `local f = module.f` keeps the old function after a reload. Read `module.f` when you call it (or call `module.f()` directly).
- **State**: the functions of the new module work on the *new* module table, while holders keep the *old* one, so a counter kept as a plain field (`M.count`) would split in two. Keep state in a table of its own and
  hand it over in `on_reload(new, old)`, which the module may define and which is called before the update:

```lua
local M = { state = { count = 0 } }
function M.on_reload(new, old) new.state = old.state end   -- the new code carries on with the old state
function M.inc() M.state.count += 1 end
return M
```

- A module that returns a frozen table (`table.freeze`) or something that is not a table is replaced in the cache only: scripts that already hold the old one keep it, new `require`s get the new one.
- A module that nothing has required yet is not loaded by a change (it would run code nobody asked for).

## From code

```csharp
using var reloader = new HotReloader(files, vm, packages);        // watches every folder behind the mounts
reloader.AssetsChanged += paths => { /* load the assets again */ };

// each frame, on the thread that owns the VM:
ReloadReport report = reloader.Poll();        // modules, DefsReloaded, assets, shaders, NeedsRestart, Failures
```

`VfsWatcher` is the part that notices changes: it reports **virtual paths** (a file in `res://packages/core` is also `mod://core/…`, and both are reported), waits until a path has been quiet
for 150 ms (saving a file is usually several events) and gives each path once. A new folder is scanned, because the operating system starts watching it a moment after it exists.

## Limits

- Only what `require` loaded is reloaded; an entry script needs a restart. Entities and components a script created stay as they are.
- Pipelines are rebuilt from shaders, not state: a pipeline that a shader edit made differently (a different push constant layout, say) still has to agree with the code that draws with it. Texture and model reloading is the host's, from `AssetsChanged`.
- While watching, base-game scripts can write `res://` (mods still cannot: their `fs` rules do not change).

## Tests

`Tests/Apogee.Scripting.Tests`: module updates (identity, removed fields, the old module kept when the new one fails to compile or run, `on_reload`, a failing `on_reload`, frozen and non-table modules, listeners and signal
connections moved to the new code, a module that failed its first load leaving nothing registered) and `HotReloader` over real files (a module edit, a broken edit then a fix, definitions and schemas, changes that need a restart,
modules nothing required, an image imported again, sources and shaders without an import). `Tests/Apogee.Content.Tests`: the watcher (debounce, once, new and deleted files, ignored names, package views, zips). `Tests/Apogee.Runtime.Tests`: `--watch`, and a game's shader edits reaching its `ShaderLibrary`. `Tests/Apogee.Rendering.Tests`: a shader edit changing the rendered image (with its before and after), only the pipelines that use the file rebuilt, a broken edit then a fix, rebuilds while frames are in flight, changes during a build. `Tests/Apogee.DevTools.Tests`: the same for the overlay's shader, and the error section.
