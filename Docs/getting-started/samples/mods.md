# Mods sample

`Samples/Mods` is a small game made only of [packages](../../manual/content/packages.md), with mods that [change its data](../../manual/content/defs.md) and run [scripts](../../manual/scripting/scripting.md), shown by two panels. It is also what CI
runs end to end: the package loader, definitions with schemas and patches, the sandbox, packs, and a rendered image.

```sh
Apogee.Runtime --game Samples/Mods/game --user /tmp/mods-user              # a window with the panels
Apogee.Runtime --game Samples/Mods/game --user /tmp/mods-user --watch      # and edit while it runs (see Hot reload)
```

`game/` is the game's folder (`res://`): `packages/base_game` is its one base package, and `dev_mods/` holds the mods of a development build (a player's mods go in `user://mods`, the same
layout). `--user` is where `user://` is; mods may write only `user://moddata/<their id>/`.

| Package | Shows |
| --- | --- |
| `base_game` | a schema for `Crate` (an enumeration, a default, a repeated `Tag`, a `map` of prices), two crates, a module (`lib/util.luau`) and a script with the two panels |
| `bigger_crates` | a dependency on `base_game` (`^1.0`) and `gameVersion`; a **patch** of the wooden crate (`weight`, `size`, and `<Tag append="true">`) and a **new** crate (`gold`); `require("@base_game/lib/util")`; writing its own data folder |
| `broken_mod` | a script with a bug: the error is logged with its file and line, the package is marked failed, and the game goes on |
| `needs_ghost` | a dependency on a mod that is not there: it does not load, and `packages.list()` says why |

The **Packages** panel is `packages.list()`: which packages loaded, in load order, and for those that did not, why. The **Crates** panel is `defs.all("Crate")` after every package's content was
merged: the wooden crate is 20 kg and large with three tags (the base game's two and the mod's), and the gold crate is the mod's. Delete `bigger_crates` and run again, and the panel shows the base game's
numbers: nothing else changes.

## Things to try

- `--watch`, then change `weight` in `dev_mods/bigger_crates/data/crates.xml`, or a line in `lib/util.luau`: the panel changes without a restart.
- Misspell an attribute in `crates.xml`: the schema reports it (`defs.problems()`, and the log) with the file and line, and the crate is still there.
- Make `needs_ghost` load by adding a `ghost_mod` package next to it; make `bigger_crates` stop loading with `<Dependency id="base_game" version=">=2.0"/>`.

## Tests

- `Samples/Mods/tests/util.test.luau`: the module's functions, with `--luau-test` (run by hand).
- Nothing in CI runs this sample. What it shows is tested on a fixture game of its own: `Tests/Fixtures/ShippedGame` (a package, a mod, a UI document with a script and a data model, and an ImGui panel) runs headless, from a pack and rendered on lavapipe in CI, and `Tests/Apogee.Runtime.Tests` and `Tests/Apogee.Mods.Tests` cover the loading, patching, dependency and failure rules.
