# Shipped game fixture

A tiny game for CI: the NativeAOT runtime runs it from its folder, packs it, runs it from the pack, and runs it again with the Slang compiler deleted (a shipped game never loads it). It uses what a shipped game uses: Luau,
a RmlUi document with a script and a data model, ImGui, the shaders, a mod. It is a *test* fixture, not a sample: samples (`Samples/`) are demos that people run by hand and CI never runs them.

`game.xml` is the game's manifest (name, version 1.0.0, the icon `icon.png`, the window): it sets the folder of the player's files and the game version that the mod's `gameVersion="^1.0"` is checked against.

Each run leaves `user://ran.txt` (the base package), `user://document.txt` (its document's script) and `user://moddata/patcher/ran.txt` (the mod) behind, which CI checks.
