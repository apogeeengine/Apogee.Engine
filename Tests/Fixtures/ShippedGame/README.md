# Shipped game fixture

A tiny game for CI: the NativeAOT runtime runs it from its folder, packs it, runs it from the pack, and runs it again with the Slang compiler deleted (a shipped game never loads it). The `aot` jobs also export it (`--export --cooked`, encrypted with a public test key) and run the export headless from another folder, with the key out of the environment and the mod installed in the user folder ([Testing an export](../../../Docs/manual/tools/export.md#testing-an-export)). It uses what a shipped game uses: Luau,
a RmlUi document with a script and a data model, ImGui, the shaders, a mod. It is a *test* fixture, not a sample: samples (`Samples/`) are demos that people run by hand and CI never runs them.

`game.xml` is the game's manifest (name, version 1.0.0, the icon `icon.png`, the window): it sets the folder of the player's files and the game version that the mod's `gameVersion="^1.0"` is checked against.

The base package also has a scene (`scenes/start.ascene`: a camera with a child, an instance of a prefab, and a UID) and a definition file (`data/crates.xml`, listed in its `mod.xml` as a `Content` folder). `--pack --cooked` stores both as binary XML at
the same paths, and `main.luau` reads them with `world:load_scene_file`, `defs` and `xml.load`, which take the text and the cooked form alike.

The prefabs (`prefabs/crate.aprefab`, which holds `prefabs/lid.aprefab`, each with a UID of its own so that an import changes nothing) are cooked too. The scene's instance (`stack`) overrides the position of the lid's handle, and `main.luau` instantiates the crate once more with `world:instantiate("mod://core/prefabs/crate.aprefab")`.

Each run leaves `user://ran.txt` (the base package), `user://scene.txt` and `user://def.txt` (what it read from the scene and the definition), `user://prefab.txt` (what its prefab instances made), `user://document.txt` (its document's script) and `user://moddata/patcher/ran.txt` (the mod) behind, which CI checks. The
`ACookedGameLoadsItsSceneAndDefsFromThePack` test (`Tests/Weft.Runtime.Tests`) imports the fixture, packs it with `--cooked` and an encryption key, deletes the sources and runs it from the pack; `ACookedGameInstantiatesPrefabsFromThePack` does the same for the prefabs and checks that the packed prefab files start with the magic of cooked XML.
