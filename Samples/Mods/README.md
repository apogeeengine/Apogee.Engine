# Mods sample

A game made of packages, and mods that change it. See `Docs/getting-started/samples/mods.md`.

```sh
Apogee.Runtime --game Samples/Mods/game --user /tmp/mods-user                 # in a window
Apogee.Runtime --game Samples/Mods/game --user /tmp/mods-user --watch          # edit a script, a definition or the schema while it runs
Apogee.Runtime --luau-test Samples/Mods                                        # the Luau tests (tests/)
Apogee.Runtime --pack Samples/Mods/game out/data/game.pack && Apogee.Runtime --game out --user /tmp/mods-user   # shipped as a pack
```

- `game/packages/base_game`: the game's own package: a schema (`Crate`), definitions, a script with two panels.
- `game/dev_mods/bigger_crates`: patches a definition (`patch="true"`, `append="true"`), adds one, uses `@base_game/…` and writes its own data folder.
- `game/dev_mods/broken_mod`: its script has a bug: logged, the game goes on without it.
- `game/dev_mods/needs_ghost`: needs a mod that is not installed: does not load, with the reason.

A sample: run it by hand; CI does not run it.
