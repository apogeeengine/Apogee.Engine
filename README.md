# Apogee Engine

[![CI](https://github.com/apogeeengine/Apogee.Engine/actions/workflows/runner.yml/badge.svg)](https://github.com/apogeeengine/Apogee.Engine/actions/workflows/runner.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

**A free, open-source C# 3D engine made for crunching numbers.**

Apogee is a set of small `Apogee.*` libraries on top of the best native libraries that exist: SDL3, Vulkan, Flecs, Luau, Jolt, OpenAL Soft, GameNetworkingSockets, Dear ImGui and RmlUi. It is built for games that spend their
frame on arithmetic: simulations with big worlds, many entities and a lot of physics. The editor and dev builds run on CoreCLR; shipped games are NativeAOT. Scripts and mods are Luau.

> **Pre-alpha.** APIs change without notice. Developed on Linux with Vulkan; Windows is built and tested by CI; the Metal (macOS) backend is written but has never run on a GPU. [Help wanted](#help-wanted).

## Why

- **Numbers first.** Positions are `double`, so a world can be thousands of kilometers wide without jitter; the GPU sees `float`. A data-oriented ECS (Flecs), SIMD, unmanaged memory, and hot paths that do not allocate.
- **The best library for each job.** We do the thin, fast layer between them and your game: our own interop and our own bindings generator, so every binding is generated, tested against clang's struct layouts, and committed.
- **Small libraries.** One `Apogee.*` project per concern. Take what you need.
- **AOT by default.** No runtime reflection, no `Reflection.Emit`, no loading managed code: a source generator writes the registry behind scripting, serialization and the editor's inspectors.
- **Luau for scripts and mods**, never C#, so a mod can be sandboxed, limited and hot-reloaded.
- **You own your stack.** Apache-2.0, no royalties, no logo. Every dependency is redistributable, so a clone builds and runs with nothing to download. If the project goes somewhere you do not want to follow, fork it.

### What Apogee is not

It is not a general-purpose engine, and not a drop-in replacement for Unity, Unreal or Godot. It targets Windows, Linux and macOS: no consoles, mobile or web. It has no C# modding, no visual scripting and no feature toggles.
Pull requests that drift from this purpose are declined; adding everything is how engines end up doing nothing well.

## What's in the box

| Library | What it is for |
| --- | --- |
| `Apogee.Core` | logging, arenas, native collections, the job system, double-precision numerics, debug drawing |
| `Apogee.Interop` | loading native libraries, UTF-8, function pointers: what the bindings stand on |
| `Apogee.Native.*` | raw bindings, generated from the libraries' headers by `Tools/Apogee.Build` (never edited by hand) |
| `Apogee.Reflection` | the registry of classes, properties, methods, signals, enums and components, written by a source generator |
| `Apogee.Ecs` | a Flecs world with reflected components, systems, hierarchy and scenes |
| `Apogee.Scripting` | Luau: the VM, modules, sandboxing and limits, generated type definitions and API docs |
| `Apogee.Platform`, `Apogee.Input` | SDL3 windows and events; input actions with rebinding and gamepads |
| `Apogee.Graphics` | the portable GPU API with Vulkan (complete) and Metal (untested), Slang shaders, bindless resources |
| `Apogee.Rendering` | a render graph, Forward+ lighting, glTF models, culling |
| `Apogee.Content` | the virtual file system, XML definitions with schemas, packages and mods, packs, asset import, hot reload |
| `Apogee.Physics` | Jolt in double precision, vehicles, hitches, a character controller |
| `Apogee.Audio`, `Apogee.Network`, `Apogee.Steam` | OpenAL Soft audio, GameNetworkingSockets transport, Steamworks |
| `Apogee.UI`, `Apogee.DevTools` | RmlUi game UI with Luau in its documents; Dear ImGui dev tools |
| `Apogee.Host`, `Apogee.Runtime`, `Apogee.Editor` | what runs a game (session, world view, undo), the game executable, the ImGui editor with play-in-editor |

### Built on

| | | |
| --- | --- | --- |
| [SDL](https://libsdl.org) 3.4 (Zlib) | [Vulkan](https://www.vulkan.org) 1.3, [VMA](https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator) 3.5 | [Slang](https://shader-slang.org) 2026.19 |
| [Flecs](https://github.com/SanderMertens/flecs) 4.1 | [Luau](https://luau.org) 0.745 | [Jolt Physics](https://github.com/jrouwe/JoltPhysics) 5.6 |
| [OpenAL Soft](https://github.com/kcat/openal-soft) 1.25 (LGPL) | [GameNetworkingSockets](https://github.com/ValveSoftware/GameNetworkingSockets) 1.6 | [Steamworks](https://partner.steamgames.com) 1.65 |
| [Dear ImGui](https://github.com/ocornut/imgui), ImGuizmo, ImPlot, imnodes | [RmlUi](https://github.com/mikke89/RmlUi) 6.3 | [FreeType](https://freetype.org) 2.15 |
| [meshoptimizer](https://github.com/zeux/meshoptimizer) 1.3, [cgltf](https://github.com/jkuhlmann/cgltf) 1.15 | [KTX](https://github.com/KhronosGroup/KTX-Software) 4.4, [zstd](https://github.com/facebook/zstd) 1.5 | stb, dr_libs |

`ThirdParty/README.md` says where each came from and how its binaries were built; [`Legal/THIRD_PARTY_NOTICES.md`](Legal/THIRD_PARTY_NOTICES.md) says what a game that ships them must include.

## Getting started

You need the .NET 10 SDK, Git LFS (the prebuilt binaries are LFS objects), CMake and Ninja (the native libraries built from source), a Vulkan 1.3 GPU to render anything, and clang only if you regenerate bindings.

```sh
git lfs install
git clone https://github.com/apogeeengine/Apogee.Engine
cd Apogee.Engine
./build.sh native              # Flecs, Luau, Jolt, ... into Bin/native
./build.sh test                # build and run the tests
dotnet run --project Samples/Viewer     # a lit colonnade, glTF models and the dev overlay
```

More: the editor (`dotnet run --project Source/Apogee.Editor -- Samples/Mods/game`), the NativeAOT runtime (`./build.sh aot`), and a game made of packages and mods
(`Bin/publish/runtime/Apogee.Runtime --game Samples/Mods/game`). `build.ps1` is the same on Windows. Read [Building](Docs/getting-started/building.md) next.

## Documentation

The Manual, with a page per library and feature, the generated C# and Luau API references, and the contributor guides, is at **[apogeeengine.github.io/docs](https://apogeeengine.github.io/docs/)**. It lives in [`Docs/`](Docs), next to the code.

## Help wanted

This is a one-person project and these are the things it needs most. Details, with where to look and how to check your work, are in [`Docs/contributing/help-wanted.md`](Docs/contributing/help-wanted.md).

- **A macOS graphics programmer**, to run the Metal backend on a real Mac (Apple silicon or Intel) and fix what breaks. It compiles and its shaders pass Apple's compiler, but it has never run on a GPU. Then GPU timings and a shader cache.
- **Windows testers**, to run the tests, the editor and the samples on real hardware (AMD, NVIDIA, Intel, Windows on Arm) and report what fails.
- **Reviewers** of the `unsafe` and interop code, the native shims, the public APIs and the Manual. A second pair of eyes finds what one pair cannot.
- **Contributors to expand the engine**, within its scope: rendering features, editor panels, tools, samples, Luau API, benchmarks, docs.

If you really do want to help, open a [discussion](https://github.com/apogeeengine/Apogee.Engine/discussions) and say which one.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) first: scope, conventions, the DCO sign-off, and what we expect when an AI assistant helped. Be patient; reviews take time. Please follow the [Code of Conduct](CODE_OF_CONDUCT.md), and report vulnerabilities privately ([SECURITY.md](SECURITY.md)).

## License

Apogee's own code is under the [Apache License 2.0](LICENSE). Third-party libraries keep their own licenses; see [`Legal/THIRD_PARTY_NOTICES.md`](Legal/THIRD_PARTY_NOTICES.md).
