# Introduction

## What Weft is

Weft is an engine for games that crunch numbers: simulations with big worlds, many entities and a lot of arithmetic per frame. Its choices follow from that.

- **Numbers first.** Positions are `double` so a world can be thousands of kilometers wide without jitter; everything the GPU sees is `float`. The ECS is [Flecs](https://github.com/SanderMertens/flecs). Hot paths use unmanaged memory, `unsafe` code and SIMD, and do not allocate.
- **The best library for each job.** SDL3 for windows and input, Vulkan 1.3 with VMA and the Slang shader compiler for graphics, Jolt for physics, Luau for scripting, OpenAL Soft for audio, GameNetworkingSockets for networking, Dear ImGui for tools, RmlUi for game UI.
  Our work is the thin, fast layer between them and the game.
- **Small libraries.** One `Weft.*` project per concern, each with its own interop. Take what you need; nothing forces the rest on you.
- **Our own bindings.** The raw bindings (`Weft.Native.*`) come from our own generator, checked by tests that every entry point resolves and every struct has clang's size. They are committed; nothing is hand-edited.
- **AOT by default.** Shipped games are NativeAOT. No runtime reflection, no `Reflection.Emit`, no loading managed code: a source generator writes the registry that gives scripts, serialization and the editor's inspectors their view of the engine's classes.
- **Luau for games and mods.** The engine core is C#. Game developers and modders both write Luau and data, so scripts can be sandboxed, limited per owner and hot-reloaded.
- **Everything redistributable.** The repository ships only libraries whose headers and binaries we may redistribute, so a clone builds and runs with nothing to download.
- **You own your stack.** Apache-2.0, no royalties, no logo screen. If the project goes somewhere you do not want to follow, fork it.

## What Weft is not

- It is not a general-purpose engine for every kind of game, and it is not a drop-in replacement for Unity, Unreal or Godot. Pull requests that drift from the purpose above are declined, because adding everything is how engines end up doing nothing well.
- It targets desktop platforms: Windows, Linux and macOS. No consoles, no mobile, no web.
- Games and mods are written in Luau, not C#. There is no visual scripting.
- It has no feature toggles: everything is always compiled. Options are build modes and dev tools only.

## Status

Pre-alpha. The libraries below are all there and tested, but APIs will change and some corners are thin. The maintainers develop on Linux with Vulkan. Windows is built and tested by CI; Metal (macOS) is written, its shaders pass Apple's
compiler, but it has never run on a GPU. Direct3D 12 is not planned. The editor is very bare-bones for now: it cannot import 3D models or other assets (it only places and re-imports files already in the game folder), and it has no materials, no animations and no game export. [Help wanted](../contributing/help-wanted.md) lists what we need.

## What is in the box

| Library | What it is for |
| --- | --- |
| `Weft.Core` | logging, arenas, native collections, the job system, double-precision numerics, debug drawing |
| `Weft.Interop` | loading native libraries, UTF-8, function pointers: what the bindings stand on |
| `Weft.Reflection` | the registry of classes, properties, methods, signals, enums and components, written by a source generator |
| `Weft.Ecs` | a Flecs world with reflected components, systems, hierarchy, scenes |
| `Weft.Scripting` | Luau: the VM, modules, sandboxing and limits, generated type definitions |
| `Weft.Platform`, `Weft.Input` | SDL3 windows and events; input actions with rebinding and gamepads |
| `Weft.Graphics` | the portable GPU API, with Vulkan (complete) and Metal (untested), Slang shaders, bindless resources |
| `Weft.Rendering` | a render graph, Forward+ lighting, glTF models, culling |
| `Weft.Content` | the virtual file system, XML definitions with schemas, packages and mods, packs, asset import, hot reload |
| `Weft.Export` | exporting a game: the runtime of a platform, the game's packs and manifest, the pack key in the executable |
| `Weft.Physics` | Jolt in double precision, vehicles, hitches, character controller |
| `Weft.Audio`, `Weft.Network`, `Weft.Steam` | OpenAL Soft audio, GameNetworkingSockets transport, Steamworks |
| `Weft.UI`, `Weft.DevTools` | RmlUi game UI with Luau in its documents; Dear ImGui dev tools |
| `Weft.Host`, `Weft.Runtime`, `Weft.Editor` | what runs a game (session, world view, undo), the game executable, the ImGui editor |
