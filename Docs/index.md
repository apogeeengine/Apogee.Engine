# Apogee Engine

A free, open-source C# 3D engine made for crunching numbers: big worlds in double precision, a data-oriented ECS, SIMD, and the best libraries that exist for everything else.

Apogee is a set of small `Apogee.*` libraries, each with its own interop, on top of SDL3, Vulkan, Flecs, Luau, Jolt, OpenAL Soft, GameNetworkingSockets, Steamworks, Dear ImGui and RmlUi. The editor and dev builds run on CoreCLR;
shipped games are NativeAOT, so engine code never uses runtime reflection. The engine is C#; games and mods are written in Luau and data. It is licensed under the [Apache License 2.0](about/license.md).

!!! warning "Pre-alpha"
    APIs change without notice. The engine is developed on Linux with Vulkan; Windows is built and tested by CI, and the Metal backend for macOS has never run on a GPU. See [Help wanted](contributing/help-wanted.md).

## Where to go

- **New here?** Read the [Introduction](about/introduction.md), then [Building](getting-started/building.md) and run a [sample](getting-started/samples/viewer.md).
- **Looking for a feature?** The [Manual](manual/core/core.md) has a page per library and per feature.
- **Writing scripts or mods?** [Scripting](manual/scripting/scripting.md), [Packages and mods](manual/content/packages.md) and the [Luau API](api/luau/index.md).
- **Looking up a type?** The [C# API](api/csharp/index.md).
- **Want to help?** [Contributing](contributing/index.md) and [Help wanted](contributing/help-wanted.md).

The source is at [github.com/apogeeengine/Apogee.Engine](https://github.com/apogeeengine/Apogee.Engine).
