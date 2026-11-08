# Building

Requires the .NET 10 SDK, CMake and Ninja (native libraries), Git LFS (prebuilt binaries are LFS objects: run
`git lfs install` once before cloning) and clang (only to regenerate bindings).

```sh
./build.sh                       # dotnet build Apogee.slnx
./build.sh test                  # dotnet test
./build.sh native                # the shared libraries in Native/ (Flecs, Luau, cimgui) into Bin/native/<rid>
./build.sh aot                   # NativeAOT publish of Apogee.Runtime and Samples/Viewer into Bin/publish, then their shaders compiled ahead of time
./build.sh docs                  # this Manual and the API reference, built with mkdocs --strict into Bin/site (see Writing docs)
./build.sh bindings [lib…]       # regenerate Source/Apogee.Native.*/Generated
```

`build.ps1` is the same on Windows. A `.env` file (gitignored) is loaded by both wrappers.

## Native libraries

Libraries that are vendored as binaries live in `ThirdParty/<lib>/lib/<rid>`; libraries built from source are built by
`./build.sh native` into `Bin/native/<rid>`. Either way `ThirdParty/NativeLibraries.props` copies the ones for the current
platform next to a project's output, where `NativeLibraries` finds them (see [Interop](../manual/core/interop.md)). `ThirdParty/README.md`
says where each library comes from and how it was built.

**Steamworks** is proprietary. Valve lets us publish the SDK's `redistributable_bin` libraries, which are in the repository; `./build.sh sdk steam <zip>`
refreshes them from a newer SDK and unpacks the headers (git-ignored) that regenerating the bindings needs. Which files are unpacked, and why, is in `ThirdParty/README.md`.

Shared libraries on Linux are linked with `--no-undefined`: a missing source file fails the build instead of the first call.

## Warnings

Warnings are errors, including the trimming and NativeAOT analyzers, for everything under `Source/`.

## Continuous integration

Every push to `main` and every pull request builds, tests and publishes on Linux, Windows and macOS: see [Continuous integration](../contributing/continuous-integration.md).
