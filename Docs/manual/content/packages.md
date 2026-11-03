# Packages and mods

The game's content and the mods that change it are all *packages*: a folder (or a `.zip`) with a `mod.xml` at its root. The game's own packages are *base* packages; mods are
packages too, with the same layout and the same tools, so the base game is just the first thing that loads. Mods are Luau and data, never native code.

```text
my_mod/
  mod.xml          what it is, what it needs, what it brings
  main.luau        scripts listed in mod.xml
  data/*.xml       definitions (see Definitions and schemas)
  schemas/*.xsd    schemas for the definitions it adds
```

## mod.xml

```xml
<Mod id="bigger_crates" name="Bigger Crates" version="1.2.0" author="Ada" description="Crates, but bigger."
     apiVersion="1" gameVersion="&gt;=1.0 &lt;3">
  <Dependency id="base_game" version="^1.0"/>
  <Script path="main.luau"/>
  <Content path="data"/>
  <Schema path="schemas"/>
</Mod>
```

| | |
| --- | --- |
| `id` | lowercase letters, digits and underscores, up to 64; **the name of the package's folder** (or of its `.zip` without the extension). Not `engine`, `self`, `res`, `user` or `mod`. |
| `version` | `major.minor.patch`; missing parts are 0, a leading `v` and a `-suffix` are ignored |
| `apiVersion` | the package API it was written for (default 1); a package that needs a newer one than the engine has does not load |
| `gameVersion` | which versions of the game it works with (a version range, default any) |
| `Dependency` | a package that must be there, and which versions of it (a range, default any) |
| `Script` | a `.luau` file inside the package, run when it loads, in the order written |
| `Content`, `Schema` | folders of definitions and of schemas inside the package |

Paths are plain relative paths inside the package: no `..`, no drive or scheme. The manifest is checked against the engine's schema (`mod.xsd`), then for what a schema cannot say; every problem
comes with the file and line.

**Version ranges** are comparators separated by spaces or commas, all of which must hold: `>=1.2`, `<=`, `>`, `<`, `=1.2` (or `1.2`), `^1.2.3` (at least that, below the next version of its
first non-zero part: `<2.0.0`; `^0.2.3` is `<0.3.0`), `~1.2.3` (below the next minor: `<1.3.0`), `*` (any).

## Finding, solving and mounting

`PackageSet.Load(files)` looks in:

| Where | Kind | Can the player switch it off? |
| --- | --- | --- |
| `res://packages/<id>/` | base | no |
| `res://dev_mods/<id>/` | dev (a development build's mods) | yes |
| `user://mods/<id>/` and `user://mods/<id>.zip` | the player's mods | yes |
| `workshop://<id>/` | the player's Steam Workshop mods (when Steam runs; see [Steam](../network/steam.md)) | yes |

The player's switched-off mods are in `user://mods.xml` (`<Mods><Disabled id="cool"/></Mods>`), which only the game writes (`UserModSettings`), never a mod. There is **one package per
id**: the first one found wins, and a later one with the same id is ignored with a problem.

A package **does not load**, with a reason you can show the player, when: it needs a newer package API or another game version, the player switched it off, a dependency is missing,
does not load, or is out of its version range (this cascades through whatever depends on it), it is part of a dependency cycle, or it is a base package that needs a mod. What loads is
ordered **dependencies first, base packages before mods, and otherwise by id**, so the order is the same on every machine. Everything that loads is mounted at **`mod://<id>/`** in the
[file system](content.md#file-system), whatever it is made of, so a package's files have one address everywhere (`mod://bigger_crates/data/x.xml`).

```csharp
using PackageSet packages = PackageSet.Load(files, new PackageOptions { GameVersion = new PackageVersion(1, 4, 0) });

foreach (SolvedPackage p in packages.Packages)
    Console.WriteLine($"{p.Id} {p.Manifest.Version}: {(p.Enabled ? "loads" : p.Reason)}");   // "needs 'base_game', which is not installed"

DefDatabase defs = packages.LoadDefs();    // the packages' schemas and content folders, in load order
foreach (var (package, path) in packages.Scripts) { /* run the scripts */ }
```

Trouble with the search itself (a `mod.xml` that does not parse, an id that is not its folder's name, a zip that is not a zip, a schema that is wrong) is in `packages.Problems`, not an
exception: one broken mod does not stop the game. `PackageSolver.Solve` is the same decision on a plain list of candidates, which is what the tests drive.

## Running packages in Luau (`PackageLoader`)

`PackageLoader` (in `Apogee.Scripting`) runs the packages of a `PackageSet` in a VM. A game wires its hooks into the VM's options and then loads:

```csharp
var loader = new PackageLoader(files, packages);
var vm = new LuauVm(new LuauVmOptions
{
    Files = new VfsScriptFiles(files),          // scripts are read through the file system
    FileSystem = files,                         // the fs library
    OwnerOfModule = loader.OwnerOfModule,       // a module runs as the owner of the package it is in
    CanLoadModule = loader.CanLoadModule,       // require confinement
    CanAccessFile = loader.CanAccessFile,       // fs confinement
});
vm.Defs = packages.LoadDefs();
IReadOnlyList<PackageRunResult> results = loader.Load(vm);
```

- **Order and owners.** Scripts run packages in load order, each package's `<Script>`s in the order written. A base package's scripts run as the VM's base owner; a mod gets an owner of its
  own (`PackageLoaderOptions`: a soft memory limit, 256 MiB by default, and a time budget per call, 2 s), so one mod cannot use up the memory or the frame of the others, and it cannot use
  classes marked `ModsBlocked`. A module runs as the owner of the package it is in, whoever required it: a mod that requires a base-game module does not give it the mod's limits.
- **Aliases.** `@<id>/…` is an alias for each package (`require("@base_game/lib/util")`), in every script. A `.luaurc` alias of the same name wins.
- **Confinement.** A mod may `require` files of its own package, of the packages it depends on (however far), and `res://`; `../other_mod/x` resolves to another mod's folder and is refused,
  and so is `@other_mod/x`. The base game may require anything.
- **Failure.** A script that raises an error stops its package: the error is logged and in `PackageRunResult.Error`, the package's remaining scripts and the packages that need it are not run,
  and everything else goes on. A mod that never returns is stopped by its time budget. Content a package brought stays loaded.

### The `fs` library

Scripts read and write through the virtual file system with **virtual paths only** (`res://…`, `user://…`, `mod://<id>/…`):

```lua
local text, err = fs.read_text("mod://my_mod/config.xml")      -- text, or nil and a message
local ok, err = fs.write_text("user://moddata/my_mod/save.xml", xml.encode(node))
fs.exists(path)  fs.read_bytes(path)  fs.write_bytes(path, data)  fs.list(dir, "*.xml", true)  fs.list_dirs(dir)  fs.make_dir(path)  fs.remove(path)
```

Functions return `nil` and a message instead of raising an error for what can go wrong. A **mod** may read `res://`, its own package and its dependencies', and `user://moddata/<its id>/`, and
may write only `user://moddata/<its id>/` (folders are made as needed): the game's content is read-only to it, and so are the player's saves and other mods' data. The base game may write
wherever the file system allows (`user://`). Messages name the virtual path (`user://x: access denied`, `res://y: not found`), never a path on the disk, and a path that is not valid (`..`, a
backslash, a drive) is refused before anything is looked up. `fs.exists` says `false` for what a script may not see.

## Shaders in a mod

A mod may bring Slang shaders. The shipped game has no shader compiler, so a mod's shaders must be **compiled by the modder**: run the engine's dev runtime on the mod's folder
(`Apogee.Runtime --import <mod folder>`) and ship the result with the mod. The compiled files are in the mod's `imported/` folder (`imported/shaders/x.slang.ashader` and an `index.xml`), which
`ShaderLibrary` and `ContentLoader` find at `mod://id/imported`. A bundle records the engine's shader ABI; after an engine update that changes it, the game says to import the mod again. In development, where
the compiler is present, a mod's `.slang` source loads without importing. See [Asset import](import.md).

## `Apogee.Runtime --game <folder>`

```sh
Apogee.Runtime --game Samples/Mods [--user <folder>] [--headless --frames 60 | --headless --screenshot out.png | (a window)]
```

The folder is `res://` (its `packages/` hold the game's packages), `user://` is `--user` or the platform's folder for the game's name (the player's mods are in `user://mods`). The runtime
logs every package and why one does not load, loads the definitions and their problems, runs the scripts, and then runs the frame loop of `--run`. A mod that fails is logged and left out; a
base package that fails is an error of the game (exit code 2).

## Tests


`Tests/Apogee.Content.Tests`: versions and ranges, the manifest (every field, every kind of problem), the solver (each reason to disable, cascade, cycles, order independent of discovery
order, duplicate ids) and `PackageSet` over real folders and a zip (places, mounting, switched-off mods, problems, definitions built from the packages, disposal). `Tests/Apogee.Scripting.Tests` runs
`PackageLoader` over real folders (order, owners and limits, aliases, `require` and `fs` confinement including that no real path leaks, failures that stay inside their package, a runaway mod
stopped by its budget, definitions seen by scripts), and `Tests/Apogee.Runtime.Tests` runs `--game` end to end.
