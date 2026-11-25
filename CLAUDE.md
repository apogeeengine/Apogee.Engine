# Weft Engine

A C# 3D game engine and framework: CoreCLR (JIT) in the editor and dev builds, NativeAOT in shipped games. Libraries are
small: one `Weft.*` project each, own interop, unsafe/unmanaged where it counts. Luau is the only
scripting language (the engine is C#; games and mods are Luau and data). Apache-2.0.

## Stack

SDL3 (window, input) · Vulkan 1.3 + VMA + Slang (the only backend that has run; Metal 3 is written but untested: a contributor task, see `Docs/contributing/help-wanted.md`) · Flecs ECS · Luau · Jolt · OpenAL Soft · GameNetworkingSockets ·
Steamworks · Dear ImGui (dev tools and the editor) · RmlUi with Luau in its documents (game UI).

## Layout

- `Source/Weft.<Name>/`: engine libraries. `Weft.Native.<Lib>/` hold generated raw bindings (`Generated/`, never
  edited by hand); `Native/` holds C shims over C++-only libraries; `ThirdParty/` holds vendored prebuilt binaries and
  headers, with how each was built in its README.
- `Source/Weft.Host/`: what runs a game (session, world view, undo history), shared by `Weft.Runtime` and `Weft.Editor` (the ImGui editor).
- `Source/Weft.Export/`: game export (the runtime of a platform, native libraries, packs, the embedded pack key).
- `Tools/Weft.Build/`: build orchestration and the bindings generator (ClangSharp, written from scratch).
- `Tests/`: one xUnit project per library. `Samples/`, `Docs/` (MkDocs: Manual, API reference, contributor guides), `Legal/`, `Bin/` (build output).

## Commands

```sh
./build.sh          # dotnet build Weft.slnx
./build.sh test     # dotnet test
./build.sh bench [filter]   # BenchmarkDotNet over Samples/Benchmarks (a sample: run by hand; Release; see Docs/manual/core/profiling.md)
./build.sh version [check]   # print the engine's version (Engine.props, the only place it is written); `check` is the CI check (see Docs/contributing/releases.md)
./build.sh docs     # the C# (DocFX) and Luau (--luau-docs) API pages, then the site with mkdocs --strict into Bin/site; `docs serve` to preview
./build.sh native   # CMake: the shared libraries in Native/ (Flecs, Luau) into Bin/native/<rid>
./build.sh aot        # NativeAOT publish of Weft.Runtime into Bin/publish, then its shaders compiled ahead of time (Shaders/imported); ./build.sh aot-viewer does the Viewer sample
dotnet run --project Source/Weft.Editor -- <game folder> [--user <dir>]     # the ImGui editor (CoreCLR only): viewport, hierarchy, inspector, assets, play-in-editor
./build.sh bindings [lib…]       # regenerate Source/Weft.Native.*/Generated from the headers (needs clang installed)
Bin/publish/runtime/Weft.Runtime --luau-test Tests/Luau     # Luau tests (also Samples/Scripting, Samples/Mods); --run <file.luau>, --luau-defs <dir>, --luau-docs <dir>
Bin/publish/runtime/Weft.Runtime --game <folder> [--user <dir>] [--watch]     # run a game: its packages and mods (Samples/Mods/game); --watch reloads what changes
Bin/publish/runtime/Weft.Runtime --workshop-upload <mod folder> [--confirm]     # publish a mod to the Steam Workshop (only describes it without --confirm: it is outward-facing)
Bin/publish/runtime/Weft.Runtime --import <folder>           # import models and images into <folder>/imported (only what changed)
./build.sh pack <folder> <out.wpak> [--cooked] [--base <pack>]   # Weft.Runtime --pack, with .env loaded: WEFT_PACK_KEY encrypts (no key = plain)
./build.sh export <game folder> <out dir> --target <rid> [--cooked]   # Weft.Runtime --export, with .env loaded (WEFT_PACK_KEY encrypts the packs and is embedded in the executable); needs ./build.sh aot first
Bin/publish/runtime/Weft.Runtime --export <game folder> <out dir> --target <linux-x64|win-x64|osx-arm64|osx-x64> [--cooked] [--runtime <dir>]     # a folder that runs on its own
```

`.env` (gitignored) holds secrets such as the pack-encryption key; the wrappers load it.

## Conventions

- **Conventional Commits** (`feat(rendering): …`, `fix(luau): …`): git-cliff builds the changelog from them. Sign off every
  commit (`git commit -s`, the DCO); a commit made with an AI assistant also carries its attribution trailer.
- **AOT-safe by default:** no runtime reflection, no `Reflection.Emit`, no dynamic loading of managed code in
  `Source/`; use source generators. Warnings are errors, including the trim and AOT analyzers.
- Engine classes are described with `[WeftClass]`/`[Property]`/`[Method]`/`[Signal]`; a Roslyn source generator
  writes the registry (Luau glue, serialization, inspectors, `.d.luau`).
- **Bindings** come only from our own generator (`Tools/Weft.Build/Bindings`, one JSON config per library in `Configs/`);
  `Generated/` is committed. `Tests/Weft.Native.Tests` checks that every entry point resolves in the native library, that
  struct sizes match clang's, and calls into each library. Never copy code from a proprietary or non-free source.
- Everything is always compiled: no feature toggles. Options are build modes and dev tools only.
- Plain and direct: link libraries, vendor prebuilt binaries (README says how they were built), no abstraction layers or
  one-off tooling nobody asked for. Removing a feature games might use needs the maintainer's OK.
- Every feature has tests and a page in `Docs/manual`. Public members get `///` comments: they are the C# API reference, and (on `[WeftClass]` members) the Luau one.
- **Tests are not samples.** `Tests/` runs in CI (and by hand); a fixture a test needs lives in `Tests/` (`Tests/Fixtures`, a project's own `Fixtures/`). `Samples/` (demo scenes: lighting, UI, physics; benchmarks) is run by hand by people: no CI step runs a sample, no test depends on one, no reference screenshots.
- Libraries must allow closed-source distribution; LGPL ones are linked dynamically.
- **Redistributable by default:** the engine ships only libraries whose headers and binaries we may redistribute, so a clone builds and runs with nothing to download.
  From Steamworks (Valve) only `redistributable_bin` may be
  distributed: those libraries are committed, the SDK headers are git-ignored (`./build.sh sdk steam <zip>`) and the generated bindings are committed. Never commit anything else from the Steamworks SDK.
