# Versions

The version of the engine is written once, in `Engine.props` at the root of the repository. Everything else is derived from it when the engine is built: the versions of the assemblies, the `EngineVersion` constants, the C
header of the native libraries, the footer of this site, and what an exported game says about the engine it is made of.

```xml
<PropertyGroup>
  <WeftVersionMajor>0</WeftVersionMajor>
  <WeftVersionMinor>2</WeftVersionMinor>
  <WeftVersionPatch>0</WeftVersionPatch>
  <WeftVersionStatus>alpha</WeftVersionStatus>
  <WeftVersionCodename></WeftVersionCodename>
  <WeftCopyrightYear>2026</WeftCopyrightYear>
  <WeftApiVersion>1</WeftApiVersion>
</PropertyGroup>
```

| Value | Meaning |
| --- | --- |
| `WeftVersionMajor`, `Minor`, `Patch` | the numbers, each from 0 to 65535 (a Windows version has 16 bits per part) |
| `WeftVersionStatus` | `alpha`, `beta`, `rc<N>` (`rc1`, `rc2`, ...) or `stable` |
| `WeftVersionCodename` | a name for a release, or empty; letters, digits, spaces, dots and dashes. It is shown next to the version and is never part of it |
| `WeftCopyrightYear` | the year of the copyright notice of every assembly |
| `WeftApiVersion` | the level of the mod API: a package's `apiVersion` is checked against it ([Packages and mods](../content/packages.md)). Raise it when a change breaks the mods written for the level before |

The version as text is `0.2.0-alpha`: the three numbers, a dash and the status; a `stable` release has no suffix (`1.0.0`). It is what `EngineVersion.Text` holds and what the git tag of a release is made from
(`v0.2.0-alpha`). It follows [semantic versioning](https://semver.org): before 1.0 a minor version may break anything.

## What is derived

- **Assemblies.** `Directory.Build.props` imports `Engine.props` first and sets `Version` (`0.2.0-alpha`), `AssemblyVersion` (`0.2.0.0`), `FileVersion` (`0.2.0.<build>`), the informational version
  (`0.2.0-alpha+abc1234`, the SDK adds the commit) and the copyright. No project sets a version itself.
- **The build number** is `WEFT_BUILD_NUMBER` (CI sets it to the number of the run) and 0 on a developer's machine. It is the last part of `FileVersion`, which holds 16 bits, so a number above 65535 starts
  again from 0 there; `EngineVersion.Build` keeps the whole number.
- **`EngineVersion`** (`Weft.Core`) is a class of constants that the `WeftEngineVersion` target of `Weft.Core.csproj` writes into `obj` before the compiler runs: `Major`, `Minor`, `Patch`, `Build`, `Api`,
  `Status`, `Codename`, `Text`, and `Commit` and `Full` (`0.2.0-alpha+abc1234`; just `Text` outside a git checkout). There is no reflection, so a NativeAOT program says the same as a JIT one. The file is only
  rewritten when its text changes, so a build that changes nothing compiles nothing. `Commit` is a property, not a constant: a new commit does not change what the other projects are compiled against.
- **Native libraries.** `Native/CMakeLists.txt` reads `Engine.props` (`file(READ)` and a regular expression per value), gives `project(WeftNative VERSION ...)` the numbers and writes `weft_version.h`
  (`WEFT_VERSION_MAJOR`, `WEFT_VERSION_STRING`, ...) into the build folder, on the include path of every library.
- **Documentation.** `./build.sh docs` passes the version on: the footer of the site and the namespaces page of the C# reference say `Weft Engine 0.2.0-alpha`.

`./build.sh version` prints the version text; `./build.sh version check` is what the static checks of CI run (see [Releases](../../contributing/releases.md)).

## Where the engine says its version

| Where | What |
| --- | --- |
| `Weft.Runtime --version` | `Weft 0.2.0-alpha (abc1234) linux-x64 NativeAOT`: the version, the commit, the platform, and `CoreCLR` or `NativeAOT` |
| `Weft.Editor --version`, its title bar and start screen | `Weft Editor 0.2.0-alpha` |
| The first line of every log file, and crash reports | `Weft 0.2.0-alpha+abc1234`, see [Logs and crash reports](crashes.md) |
| Vulkan | `engineVersion` of the application info is `VK_MAKE_API_VERSION(0, 0, 2, 0)`, so a GPU driver's logs and profiles know which engine made the calls |
| An exported Windows program | the `EngineVersion` string of the version information, next to the game's own versions |
| An exported macOS bundle | the `WeftEngineVersion` key of `Info.plist` |

## A game names the engines it runs on

`game.wgam` has an optional `engine` attribute, a range in the syntax of a package's `gameVersion`: `>=0.2`, `^0.2.1`, `~0.2.0 <0.4`, `*`.

```xml
<Game name="Fixture Game" organization="Weft" version="1.0.0" engine="&gt;=0.2 &lt;0.4"/>
```

`--game`, the launch of an exported game and an export check it against the engine that runs (the numbers only: `>=0.2` holds `0.2.0-alpha`) and refuse a game it does not hold, with a message such as
`Fixture Game 1.0.0 needs the engine >=0.3 (game.wgam: engine=">=0.3"), and this is Weft 0.2.0-alpha.` and exit code 2. A game without `engine` runs on every engine. See
[Game manifest](../content/game-manifest.md).

## Format versions are another thing

The engine's version says nothing about its file formats: each has its own number (the model, the pack, the cooked XML, the shader bundle, the importer, the scene and the prefab), which changes when that
format does. See [File formats](../content/formats.md).
