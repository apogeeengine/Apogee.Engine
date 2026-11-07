# Shipped game fixture

A tiny game for CI: the NativeAOT runtime runs it from its folder, packs it, runs it from the pack, and runs it again with the Slang compiler deleted (a shipped game never loads it). It uses what a shipped game uses: Luau,
a RmlUi document with a script and a data model, ImGui, the shaders, a mod. It is a *test* fixture, not a sample: samples (`Samples/`) are demos that people run by hand and CI never runs them.

Each run leaves `user://ran.txt` (the base package), `user://document.txt` (its document's script) and `user://moddata/patcher/ran.txt` (the mod) behind, which CI checks.
