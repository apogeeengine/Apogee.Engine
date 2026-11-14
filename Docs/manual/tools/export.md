# Exporting a game

An export turns a game folder into a folder that runs on its own: the runtime built for a platform, renamed to the game's executable name, its native libraries and compiled shaders, the game's files in
`data/game.pack`, and the game's `game.xml`. It is made from the command line (`Apogee.Runtime --export`, wrapped by `./build.sh export`) or from code (`GameExporter` in `Source/Apogee.Export`). The editor
has no Export dialog yet.

```sh
./build.sh aot                                                          # publish the runtime for this machine: Bin/publish/runtime
./build.sh export Tests/Fixtures/ShippedGame Bin/export/linux --target linux-x64 --cooked
Bin/export/linux/FixtureGame                                           # runs from anywhere, with no environment variable
```

```text
Apogee.Runtime --export <game folder> <out folder> --target <rid> [--cooked] [--runtime <dir>] [--console]
```

| | |
| --- | --- |
| `<game folder>` | the game: a folder with a [`game.xml`](../content/game-manifest.md). It is never written to. |
| `<out folder>` | where the export goes: it does not exist, is empty, or is an earlier export (it holds a file `.apogee-export`), which is then replaced. Anything else is an error, so that nothing of yours is deleted. It cannot be inside the game folder, nor contain it. |
| `--target <rid>` | the platform: `linux-x64`, `win-x64`, `osx-arm64` or `osx-x64` (the platforms the engine has complete native libraries for). Anything else is an error that lists these. |
| `--cooked` | pack the [imported](../content/import.md) artifacts and the [cooked](../content/packs.md#making-one) documents instead of the sources they replace (the game is imported first, in a copy). |
| `--runtime <dir>` | the folder the runtime for the target was published to. Default: the folder of the running program when it is the NativeAOT runtime, else the `Bin/publish/runtime` above the program or the current folder. |
| `--console` | Windows only: keep the console window (the Windows export otherwise has none). The Windows packaging is still to come, see below. |

Exit codes: 0 fine, 1 bad arguments (an unknown target, a bad `APOGEE_PACK_KEY`), 2 the export failed (the message says why). Warnings are printed to stderr, for example that no key is set.

## Where the runtime for a platform comes from

The runtime is a NativeAOT program, so it can only be built on its own operating system (`./build.sh aot`), and the folder that command publishes already holds every native library of that platform. An
export copies from such a folder. It reads the header of the program in it (PE, ELF or Mach-O, with its processor) and refuses a folder of another platform with what to do:

```text
/path/runtime holds a linux-x64 runtime; --target win-x64 needs one published for win-x64 (./build.sh aot on that operating system, or the CI artifact apogee-win-x64), passed with --runtime <dir>.
```

So exporting for the machine you are on needs nothing but `./build.sh aot`; exporting for another platform needs a runtime published there (or downloaded from CI) and `--runtime`. A macOS runtime has to
be a thin binary for one processor, not a universal one. Left out of the copy: debug files (`*.pdb`, `*.dbg`, `*.dSYM`), import libraries (`*.lib`, `*.exp`), the Slang compiler's libraries and the shader
sources (`Shaders/*.slang`; the compiled `Shaders/imported` stays: a shipped game never loads the compiler), `steam_appid.txt`, and the runtime folder's own `game.xml` and `data/`.

## The layout

| | Linux and Windows | macOS |
| --- | --- | --- |
| executable and native libraries, `Shaders/` | `<out>/` | `<out>/<Name>.app/Contents/MacOS/` |
| `game.xml`, `data/game.pack` | `<out>/` | `<out>/<Name>.app/Contents/Resources/` |
| executable name | `<executable>` (`.exe` on Windows), from `game.xml` | the same; the bundle is `<name>.app` |

The folder that holds `game.xml` and `data/` is the game's **root**: the runtime finds it next to its executable, or in `../Resources` from a bundle's `MacOS` folder (see
[how the runtime finds it](../content/game-manifest.md#how-the-runtime-finds-it)), and mounts only `data/*.pack` as `res://`. A game that is exported does not read the files of the folder it
came from, only its pack.

`data/game.pack` holds the game folder as [`GamePacker`](../content/packs.md#from-code-gamepacker) packs it (a very big game continues in `game_001.pack`…), less what a shipped game does not have:
`data/` (the packs of the development folder), `dev_mods/` (a shipped game has no development mods), `game.xml` (it is written beside the pack), the icon file, `steam_appid.txt` (the Steam app id
comes from `game.xml`), and names that start with a dot. A cooked export imports a **copy** of the game folder (the existing `imported/` is reused, so only what changed is imported again), so the game
folder gets no `imported/` and no changed file.

The exported `game.xml` is the game's, with the icon changed: the icon is in the executable or the bundle on Windows and macOS, and a file `icon.png` beside `game.xml` on Linux, which the platform steps below write.

## The pack key

If `APOGEE_PACK_KEY` is set (64 hexadecimal digits, in the environment or in the `.env` file that the `./build.sh` wrapper loads), the export encrypts the pack with it, and writes the key into the
executable, so that the game opens its own pack without the variable. Without a key the packs are plain, and the export warns about it. See [Encryption and the key](../content/packs.md#encryption-and-the-key).

The key goes into a reserved **slot** of the runtime (`PackKeySlot`): the executable holds 68 bytes in its read-only data, which start with a 16-byte magic that appears nowhere else, and the export
patches the copy: it finds the magic (exactly once, else it is not this engine's runtime and the export fails), and writes the key masked with a random salt. The running game reads it back
before it looks at the environment. No recompiling is needed, which is why an export can be made for any platform.

!!! warning "Obfuscation, not protection"
    The key is in the file, one XOR away from anyone who reads this page and the source, and in the memory of the running game. The slot keeps it out of `strings`, out of a quick look in a hex
    editor, and away from the pack. It does not stop a determined person from reading your game's files. Do not put a key in an export that you cannot afford to have extracted, and use a key only for
    this game.

## Arguments a shipped game ignores

A game started with no command takes the runtime's [options](../scripting/runtime.md#running-a-shipped-game) (`--user`, `--headless`, `--frames`, `--size`...) and logs and ignores any other argument,
because Steam appends `+connect_lobby <id>` when a player accepts an invitation. Every runtime command stays in the shipped program (there are no feature toggles).

## Platform steps

The last step of an export depends on the operating system (`IPlatformPackaging`): the executable's icon and version information on Windows, the `.app` bundle on macOS, the icon file and desktop
entry on Linux. The Linux step is described below; the Windows and macOS steps are not written yet: those exports are complete and run, but have no icon of their own, and the Windows program is still a
console program. Code signing (Authenticode, Developer ID and notarization) is outside the export: sign the result.

### Linux

An export for `linux-x64` is a folder that runs from anywhere:

```text
FixtureGame                      the program (mode 755), the game's executable name
lib*.so, libSDL3.so.0 ...        the native libraries, beside it
Shaders/imported/                compiled shaders
game.xml, data/game.pack         the game
icon.png                         the icon, 256x256
org.apogee.fixturegame.desktop   the desktop entry
```

- **The executable bit.** The step sets the executable's mode to `rwxr-xr-x` (`File.SetUnixFileMode`). On a Windows computer there are no Unix modes, so the export warns that the bit is not set: set it
  (`chmod +x`) when the folder is unpacked on Linux, or export Linux from Linux or WSL, where the bit is kept (an archive made on Windows loses it again).
- **The icon.** The game's `Icon` is written as `icon.png`, scaled to 256x256 (a smaller icon is made bigger and the export warns, as it does for an icon below 256 pixels), and the exported `game.xml`
  names it (`<Icon path="icon.png"/>`), which is the file the runtime draws the window's icon from. A game without an icon gets no `icon.png` and no `Icon` line.
- **The desktop entry**, `<identifier>.desktop`, follows the [Desktop Entry Specification](https://specifications.freedesktop.org/desktop-entry-spec/latest/): `Type`, `Name` and `Comment` (from
  `game.xml`'s `name` and `description`), `Exec=<executable>`, `Icon=<identifier>`, `Categories=Game;` and `StartupWMClass=<identifier>`, which is the application id the runtime gives its window, so
  that a desktop matches the window to the entry. Values are escaped as the specification says (a backslash, a line break and a leading space). It is a **template for whoever packages the game**
  (a distribution package, a Flatpak, an AppImage, an installer): `Exec` is the program's name as found on the `PATH` and `Icon` is the name of an icon in a theme, which is where an installer puts
  `icon.png`. Nothing in the export reads it, and a game run through Steam does not use it.

**It does not depend on the working directory.** The runtime finds the game (`game.xml`, `data/`), the compiled shaders and the native libraries from the folder of the executable, not from where it was
started (`AppContext.BaseDirectory`), and the libraries that load other libraries (for example `libcimgui.so`, `librmlui.so`, `libktx.so`) have the run path `$ORIGIN`, so they find them beside
themselves. No `LD_LIBRARY_PATH` is needed: `cd /tmp && /path/to/export/FixtureGame` works. The player's files are written under `~/.local/share/<organization>/<user folder>` (or `--user <dir>`), never in the
game's folder.

**What the player's system provides.** The native libraries are shipped; these are not:

- **The C and C++ libraries**: glibc, and libstdc++ (`libstdc++.so.6`) for the libraries that use it. How new a glibc and libstdc++ the export needs is decided by where its parts were built. SDL, OpenAL
  Soft and GameNetworkingSockets are the prebuilt libraries in `ThirdParty`, built in Valve's Steam Runtime "sniper" and needing glibc 2.31 at most (`ThirdParty/README.md`). The NativeAOT program and the
  libraries built by `./build.sh native` (Flecs, Luau, Jolt, ...) need the glibc and libstdc++ of the computer that built them: a runtime published on a current rolling distribution needs a current
  glibc (one published on Arch Linux in November 2026 needed 2.44) and will not start on an older one. Publish the runtime (`./build.sh aot`) on the oldest distribution you want to support, or in a container of it, and
  export from that. Steam's "sniper" runtime is the baseline to aim for for a game on Steam.
- **Vulkan**: the Vulkan loader (`libvulkan.so.1`) and a driver for the graphics card. The engine does not ship them (`ThirdParty/README.md`, "Not vendored").
- **OpenSSL** (`libssl`): the packs are encrypted with AES-GCM, which .NET takes from the system's OpenSSL on Linux. A game with encrypted packs needs it; a plain one does not.
- **Audio and windowing**: SDL and OpenAL Soft load the desktop's backends at run time (X11 or Wayland; PipeWire, PulseAudio or ALSA), so whatever the player's desktop has is used.

The unit tests export from a synthesized runtime folder, so they check the step's files, not that a real export starts on a given distribution.

## From code

```csharp
ExportResult result = GameExporter.Export(
    new ExportOptions
    {
        GameFolder = "Game",
        OutputFolder = "Bin/export",
        Target = new ExportTarget("linux", "x64"),
        RuntimeDirectory = null,                 // look for a published runtime
        Cooked = true,
        Key = PackKey.FromEnvironment(),
        Importers = ScriptSession.Importers,     // the importers of the game (models, images, shaders); null: models and images
    },
    Console.WriteLine);
foreach (string warning in result.Warnings) { /* ... */ }
```

`ExportResult` has the executable, the game's root and the files written. `ExportException` is the one error an export throws. `IconImages.Load(png)` makes the sizes the platform steps need from the
game's icon (`Rgba(size)`, `Png(size)`: a box filter down, with a warning for an icon below 256 pixels or a size above the image).

## Tests

`Tests/Apogee.Export.Tests`: the targets, reading the platform from an executable header, what is left out of a runtime folder, the key slot (written into the only slot, read back, no slot or two is an
error, the key is not in the file in the clear), the icon sizes, the Linux step (the executable bit, `icon.png` and the manifest's icon, the escaping of the desktop entry), and exports for all four targets from any host with a synthesized runtime folder (layout, renamed executable, what is packed, the game
folder untouched, encrypted packs, the output folder rules). `Tests/Apogee.Runtime.Tests` runs an exported game from its folder, headless, on its encrypted pack.
