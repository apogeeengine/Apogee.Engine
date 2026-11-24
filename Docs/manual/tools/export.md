# Exporting a game

An export turns a game folder into a folder that runs on its own: the runtime built for a platform, renamed to the game's executable name, its native libraries and compiled shaders, the game's files in
`data/game.pack`, and the game's `game.xml`. It is made from the command line (`Weft.Runtime --export`, wrapped by `./build.sh export`) or from code (`GameExporter` in `Source/Weft.Export`). The editor
has no Export dialog yet.

```sh
./build.sh aot                                                          # publish the runtime for this machine: Bin/publish/runtime
./build.sh export Tests/Fixtures/ShippedGame Bin/export/linux --target linux-x64 --cooked
Bin/export/linux/FixtureGame                                           # runs from anywhere, with no environment variable
```

```text
Weft.Runtime --export <game folder> <out folder> --target <rid> [--cooked] [--runtime <dir>] [--console]
```

| | |
| --- | --- |
| `<game folder>` | the game: a folder with a [`game.xml`](../content/game-manifest.md). It is never written to. |
| `<out folder>` | where the export goes: it does not exist, is empty, or is an earlier export (it holds a file `.weft-export`), which is then replaced. Anything else is an error, so that nothing of yours is deleted. It cannot be inside the game folder, nor contain it. |
| `--target <rid>` | the platform: `linux-x64`, `win-x64`, `osx-arm64` or `osx-x64` (the platforms the engine has complete native libraries for). Anything else is an error that lists these. |
| `--cooked` | pack the [imported](../content/import.md) artifacts and the [cooked](../content/packs.md#making-one) documents instead of the sources they replace (the game is imported first, in a copy). |
| `--runtime <dir>` | the folder the runtime for the target was published to. Default: the folder of the running program when it is the NativeAOT runtime, else the `Bin/publish/runtime` above the program or the current folder. |
| `--console` | Windows only: keep the console subsystem, so that the game opens a console window (to read its log). Without it the program is a windowed one, see [Windows](#windows). |

Exit codes: 0 fine, 1 bad arguments (an unknown target, a bad `WEFT_PACK_KEY`), 2 the export failed (the message says why). Warnings are printed to stderr, for example that no key is set.

## Where the runtime for a platform comes from

The runtime is a NativeAOT program, so it can only be built on its own operating system (`./build.sh aot`), and the folder that command publishes already holds every native library of that platform. An
export copies from such a folder. It reads the header of the program in it (PE, ELF or Mach-O, with its processor) and refuses a folder of another platform with what to do:

```text
/path/runtime holds a linux-x64 runtime; --target win-x64 needs one published for win-x64 (./build.sh aot on that operating system, or the CI artifact weft-win-x64), passed with --runtime <dir>.
```

So exporting for the machine you are on needs nothing but `./build.sh aot`; exporting for another platform needs a runtime published there (or downloaded from CI) and `--runtime`. A macOS runtime has to
be a thin binary for one processor, not a universal one. Left out of the copy: debug files (`*.pdb`, `*.dbg`, `*.dSYM`), import libraries (`*.lib`, `*.exp`), the Slang compiler's libraries and the shader
sources and their import settings (`Shaders/*.slang` and `Shaders/*.slang.import.xml`; the compiled `Shaders/imported` stays: a shipped game never loads the compiler), `steam_appid.txt`, and the runtime folder's own `game.xml` and `data/`.

The continuous integration publishes the runtime of each platform it builds (`weft-linux-x64`, `weft-win-x64`, `weft-osx-arm64`, `weft-osx-x64`) as a workflow artifact for 14 days: download one,
unzip it (an archive does not keep the executable bit; the export sets it on the program it writes), and pass the folder with `--runtime`. This is how to export for Windows or macOS without a computer of that kind.

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

If `WEFT_PACK_KEY` is set (64 hexadecimal digits, in the environment or in the `.env` file that the `./build.sh` wrapper loads), the export encrypts the pack with it, and writes the key into the
executable, so that the game opens its own pack without the variable. Without a key the packs are plain, and the export warns about it. See [Encryption and the key](../content/packs.md#encryption-and-the-key).

The key goes into a reserved **slot** of the runtime (`PackKeySlot`): the executable holds 68 bytes in its read-only data, which start with a 16-byte magic that appears nowhere else, and the export
patches the copy: it finds every copy of the whole slot (at least one, else it is not this engine's runtime and the export fails; a magic that is not followed by the rest of a slot is other data and is left
alone, and a file that ends inside a slot is refused), and writes the same key, masked with one random salt, into each. A compiler may emit the slot's data more than once (the NativeAOT executable for
macOS on Apple silicon has two copies), and the game reads whichever copy its code refers to, so all of them are patched; the export log says how many places it wrote when there is more than one. The
running game reads the key back before it looks at the environment. No recompiling is needed, which is why an export can be made for any platform.

!!! warning "Obfuscation, not protection"
    The key is in the file, one XOR away from anyone who reads this page and the source, and in the memory of the running game. The slot keeps it out of `strings`, out of a quick look in a hex
    editor, and away from the pack. It does not stop a determined person from reading your game's files. Do not put a key in an export that you cannot afford to have extracted, and use a key only for
    this game.

## Arguments a shipped game ignores

A game started with no command takes the runtime's [options](../scripting/runtime.md#running-a-shipped-game) (`--user`, `--headless`, `--frames`, `--size`...) and logs and ignores any other argument,
because Steam appends `+connect_lobby <id>` when a player accepts an invitation. Every runtime command stays in the shipped program (there are no feature toggles).

## Platform steps

The last step of an export depends on the operating system (`IPlatformPackaging`): the executable's icon and version information on Windows, the `.app` bundle on macOS, the icon file and desktop
entry on Linux. The Windows, Linux and macOS steps are described below. Code signing with a certificate (Authenticode, Developer ID) and notarization are outside the export: sign the result.

### Windows

The export copies the runtime built for `win-x64`, renames it `<executable>.exe`, and then changes the file in place with the export's own PE writer (`Source/Weft.Export/Windows`). It reads and
writes the executable's headers and resource section itself, with no Windows API, `rc.exe` or `rcedit`, so a Windows export can be made on Linux or macOS from a runtime published on Windows
(`--runtime`). Three things change:

- **The icon.** The game's `Icon` (a square PNG, 256x256 or more advised) is written as an icon group with seven images: 16, 24, 32, 48, 64 and 128 pixels as 32-bit images with an alpha channel
  (what every Windows version reads), and 256 pixels as a PNG (Windows Vista and later), each scaled from your image with a box filter. Explorer picks the size it needs; the window and the task bar take
  the first group, which is number 1. A smaller image is scaled up for the sizes above it and the export warns. A game without an icon keeps the runtime's.
- **The version information** (the Details tab of the file's properties, the name task managers show) is made from `game.xml`: `CompanyName` is the organization, `FileDescription` the description (the
  name when there is none), `FileVersion` and `ProductVersion` the version as `major.minor.patch.0`, `InternalName` the executable's name, `OriginalFilename` the executable's file name,
  `LegalCopyright` the copyright, `ProductName` the name; language English (United States), code page Unicode. Each part of the version must be at most 65535. Any version or icon resource the runtime
  had is replaced; its other resources (the application manifest above all) are kept as they are.
- **The subsystem.** The runtime is a console program; a game started by a double-click would open a console window beside its own. The export makes it a windowed program (`IMAGE_SUBSYSTEM_WINDOWS_GUI`).
  A windowed program has no standard output: run the game from a terminal and nothing is printed. `--console` keeps the console subsystem for when you want to read the log.

How the file is changed: the resource section is rewritten where it is when it is the last section of the file and nothing else points into it; otherwise (linkers put the relocations last) a new `.rsrc` section is added after the
last one, and the old one is renamed `.rsrc0` and left in the file as unused data. No existing section moves, so the code, the pack key and everything that points into the program stay where they
are, and the key is written before this step so that the checksum in the header, which is written last, covers the final file. Adding a section header needs 40 free bytes between the section table and
the first section's data; a linker leaves hundreds, but an executable made some other way may not, and the export then stops with an error saying so. It also stops if the declared image size is larger than where the last section ends. The export refuses an executable that has other data after
its sections (an installer, an attached archive), and one that is damaged: the message says what is wrong.

- **Signing.** Signing the game (Authenticode, `signtool sign`) is outside the export: sign `<executable>.exe`, and
  the DLLs if you like, after the export. An unsigned program from the internet is held by Windows SmartScreen until enough people have run it, or until it is signed with a certificate that has a reputation;
  a game on Steam is not affected by that.
- **Version suffixes.** The version in `game.xml` is `major.minor.patch` and nothing else: a suffix such as `-beta` or `+build` is an error when the manifest is read. The file version, the product version and the
  strings `FileVersion` and `ProductVersion` all say `1.2.3.0`. Put a label such as "beta" in the `description` if players should see it.
- **Signed runtimes.** A signed runtime loses its signature (the changes invalidate it) and the export warns. A runtime with `IMAGE_DLLCHARACTERISTICS_FORCE_INTEGRITY`, which Windows loads only when signed,
  is refused instead, because the unsigned result could not start.

What the tests prove, and what they do not. The unit tests write executables with the PE writer and read them back with `System.Reflection.PortableExecutable`, with their own parser of the resource
tree and version block, and with an independent sum for the checksum: the sections are contiguous and aligned, `SizeOfImage` is where the last one ends, every resource directory is ordered (names first,
then numbers) with the entry counts it declares, the checksum is right. They do this on small executables made by the tests, on the engine's vendored MSVC-linked `SDL3.dll` and on the signed
`steam_api64.dll` (a signature to strip, a resource type with a name), where the original sections, the import, exception, relocation, debug and load-configuration tables and the other resources must come out
byte for byte. They also damage the headers (truncations, every field set to the edge values, random bytes) and expect an `ExportException` every time. They cannot show that the Windows loader accepts the
file: **nothing in the project's tests runs an exported program on Windows**, and `PEReader` is more lenient than the loader. On Windows one test asks `FileVersionInfo` for the version information.

To look at an export with a second, independent implementation of the format, on Linux or macOS with LLVM installed (one test does this when `llvm-readobj` is on the `PATH`):

```sh
llvm-readobj --file-headers --sections --coff-resources Bin/export/win/FixtureGame.exe
```

Expected: `Subsystem: IMAGE_SUBSYSTEM_WINDOWS_GUI (0x2)` (without `--console`), a non-zero `CheckSum`, a section `.rsrc` that is the last one and, usually, `.rsrc0` before it (the old resources, now unused), and
under `.rsrc` the types `ICON (ID 3)` with 7 entries, `GROUP_ICON (ID 14)`, `VERSIONINFO (ID 16)` and `MANIFEST (ID 24)`. That Explorer shows the icon and the Details tab, and that a double-click opens no
console, is checked by hand on a Windows computer.

### Linux

An export for `linux-x64` is a folder that runs from anywhere:

```text
FixtureGame                      the program (mode 755), the game's executable name
lib*.so, libSDL3.so.0 ...        the native libraries, beside it
Shaders/imported/                compiled shaders
game.xml, data/game.pack         the game
icon.png                         the icon, 256x256
org.weft.fixturegame.desktop     the desktop entry
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

### macOS

An export for `osx-arm64` or `osx-x64` is an application bundle. The runtime for the target must be a thin (single processor) one, see above.

```text
Fixture Game.app/                      the bundle, named after the game's name
  Contents/
    Info.plist                         generated from game.xml
    PkgInfo                            APPL????
    MacOS/
      FixtureGame                      the program (mode 755), the game's executable name
      lib*.dylib                       the native libraries, beside it
      Shaders/imported/                compiled shaders
    Resources/
      game.xml, data/game.pack         the game: its root, see "The layout"
      game.icns                        the icon
```

- **`Info.plist`** is written as an XML property list (the text of `game.xml` is escaped, so a name with `&` or `<` is fine). It holds `CFBundleName` and `CFBundleDisplayName` (the game's name),
  `CFBundleIdentifier` (its `identifier`), `CFBundleExecutable`, `CFBundleVersion` and `CFBundleShortVersionString` (its version, `major.minor.patch`: `game.xml` accepts no suffix such as `-beta`, which `CFBundleVersion` could not hold, since it takes only integers separated by dots), `CFBundlePackageType` `APPL`, `CFBundleIconFile` `game.icns` (when the
  game has an icon), `CFBundleInfoDictionaryVersion` `6.0`, `LSMinimumSystemVersion`, `NSHighResolutionCapable`, `LSApplicationCategoryType` `public.app-category.games` and
  `NSHumanReadableCopyright` (its copyright, when it has one). `LSMinimumSystemVersion` is the newest minimum system version (`LC_BUILD_VERSION`) among the program and the `.dylib` files beside it, for
  the target's processor, and never below 11.0, the first macOS for Apple silicon (also what it is when none of them says).
- **The icon** is made into `game.icns`, with PNG images at 16, 32, 64, 128, 256, 512 and 1024 pixels and the `@2x` images of 16 to 256 (the sizes Finder and the Dock use). A size above the game's icon is left
  out, not enlarged: macOS then scales the biggest image there is. **1024x1024 is advised**; below that the export warns that the 512 and 1024 pixel images are missing. The window's icon is the application's
  icon, so the exported `game.xml` names no `Icon`.
- **The executable bit** is set as on Linux (`rwxr-xr-x`); a Windows computer has no Unix modes, so the export warns that it must be set where the bundle is unpacked, and an archive made on Windows loses
  it again. Archive a bundle on a Mac with `ditto -c -k --keepParent "Fixture Game.app" Game.zip`.

#### The code signature

An Apple-silicon Mac only runs a program that is signed, and the NativeAOT runtime comes with an *ad-hoc* signature: the linker signs it with no certificate, only hashes (`codesign -s -` makes the same kind, with an empty signature slot, and is accepted too). The signature is a table at the end of the
file (the CodeDirectory) with the hash of every 16 KiB page of the program; the kernel checks a page's hash when it is loaded and kills the program if it does not match (`Killed: 9`). The export
changes a page of the program, for it writes the [pack key](#the-pack-key) into it (into each copy of the slot), so it has to make the hashes right again. `MachOSignature.Rehash` does that once, after the key is
written everywhere and as the last change to the program:

1. It reads the Mach-O header and finds the `LC_CODE_SIGNATURE` load command, which says where the signature is.
2. It reads the signature's SuperBlob and finds every CodeDirectory in it (a program can have an alternate one with another hash function).
3. For each, it hashes every page of the program, from the start of the file up to where the signature begins, with that CodeDirectory's own hash function (SHA-1, SHA-256, SHA-256 cut to 20 bytes, or
   SHA-384) and page size, and writes the hashes over the old ones in place.

Nothing else is touched: not the length of the file, the header, the position of the signature, the identifier, or the other blobs. All of it is checked first: a file that is not a thin 64-bit Mach-O, or whose lengths
and offsets do not fit it (or whose CodeDirectory is shorter than its version needs, or uses a scatter vector, or does not have every copy of the pack key slot inside the hashed code), fails the export and is not written (the program is untrusted input to this code, so every length is checked against the file with 64-bit arithmetic, and nothing is allocated or looped over by what the file
claims).

What it does **not** do:

- **It does not sign.** A program without a signature (an x86-64 build the linker did not sign) is left as it is, which macOS runs; for an arm64 one the export warns that it will not run until it is signed
  (`codesign --force --sign - <executable>`, on a Mac).
- **It does not repair a signature made with a certificate.** Such a signature (a CodeDirectory without the ad-hoc flag, or a signature slot with a CMS message in it; an *empty* slot is what `codesign -s -`
  writes) also signs the CodeDirectory, so changing the CodeDirectory breaks it, and only the private key can make it again. The export stops with that message: publish the runtime unsigned, and sign the
  bundle after the export.
- **There is no Developer ID signing and no notarization.** The bundle is not *sealed* either: no signature covers `Info.plist` or the files in `Resources`; only the program (and each library, by the signature it
  came with) is signed.

#### Gatekeeper, signing and notarization

- **Downloaded builds** (a browser, a message, a mail attachment) get the quarantine attribute, and Gatekeeper refuses to open an application that is not notarized: the player has to open it with Control-click
  and *Open*, or allow it in System Settings, Privacy & Security, or remove the attribute (`xattr -dr com.apple.quarantine "Fixture Game.app"`). Files that Steam installs are not quarantined, so a game
  distributed through Steam opens without any of this.
- **Developer ID signing and notarization** are outside the export, and an exported bundle is **not ready for them**: the compiled shaders are data files in `Contents/MacOS`, which is for code only, and
  `codesign --strict` and notarization reject data there. The engine looks for them next to the program (`AppContext.BaseDirectory`), so moving them to `Contents/Resources` is a change to the engine's shader
  loading that has not been made. When it has, signing is done after the export, with Apple's tools and your own certificate, from the inside out as Apple advises (`--deep` is discouraged): sign each
  `.dylib`, then the executable with `--options runtime`, then the bundle, and then submit it (`xcrun notarytool submit`) and staple the ticket (`xcrun stapler staple`). Signing again replaces the ad-hoc
  signature this step repairs, so it comes after the export, never before.
- **Checking an export on a Mac.** `codesign` judges an executable inside a bundle as part of the bundle, which is not sealed and may fail `--strict` for reasons that have nothing to do with the program. To
  check only the program's own signature, copy it out of the bundle: `cp "Fixture Game.app/Contents/MacOS/FixtureGame" /tmp/exe && codesign --verify --strict --verbose=4 /tmp/exe` (exit status 0), and
  `codesign -dvvv /tmp/exe` shows its flags (`adhoc`) and CDHash. `codesign --verify --strict --verbose=4 "Fixture Game.app"` is informational. `plutil -lint "Fixture Game.app/Contents/Info.plist"` checks the
  plist, and starting the game with its encrypted pack and no key in the environment is a check of the page that holds the key slot, since the game reads it. The unit tests cannot run these tools; the macOS job
  of the continuous integration is to.
- **Metal.** Whether an exported game draws on a Mac depends on the engine's Metal backend, which is written but not tested yet (see [help wanted](../../contributing/help-wanted.md)), not on the export.

!!! note "Not run on a Mac"
    The bundle step was written and tested without a Mac. The tests build Mach-O programs and signatures by hand and check the hashes against an independent computation (the SHA-1 and SHA-256 of each
    page), and they rehash the real signatures of the libraries in `ThirdParty` to see that the result is byte for byte what the linker and `codesign -s -` made; `codesign --verify` and `plutil -lint` have not been run on an
    export. The macOS job of the continuous integration is where they will be.

## Testing an export

The three `aot` jobs of the continuous integration (`.github/workflows/*_builds.yml`) export `Tests/Fixtures/ShippedGame` for their own platform with the runtime they just published, `--cooked`, and then
check and run the result:

1. The export step sets `WEFT_PACK_KEY` to a fixed test key, `000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f`, on that step only. **The key is public and worthless**: it is made
   up, nothing is protected by it, and an exported game carries its key anyway. Never ship a game with it.
2. The result is checked: the pack does not start with the plain magic (it is encrypted), no debug file, Slang library, shader source or `steam_appid.txt` was copied, and the game folder in the
   repository has no changed or new file. On Windows the version information says `Fixture Game`; on macOS the program's signature is verified (`codesign --verify --strict --verbose=4` on a copy
   outside the bundle, then `codesign -dvvv`) and `Info.plist` and `game.icns` are read by `plutil -lint` and `iconutil`.
3. The exported game is run headless **from another folder**, with `WEFT_PACK_KEY` not in the environment (so the key can only come out of the executable's slot), a player's mod copied to the user
   folder, and `+connect_lobby 1` on the command line, which a shipped game ignores. The files that the fixture leaves in the user folder (`ran.txt`, `scene.txt`, `def.txt`, `prefab.txt`, `document.txt` and the mod's
   `moddata/patcher/ran.txt`) must exist. On Linux it is run once more, rendered, on the software rasterizer.
4. The export is uploaded as the artifact `export-<rid>`.

This is the test that runs an export on each operating system: the unit tests build the files for all platforms from any computer, but only these jobs start the result.

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

`Tests/Weft.Export.Tests`: the targets, reading the platform from an executable header, what is left out of a runtime folder, the key slot (written into every copy of the slot and read back, a magic without the rest of the template left alone, no slot, a slot cut by the end of the file and too many copies an
error, the key is not in the file in the clear), the icon sizes, the PE writer (the resource tree, the section that is rewritten or added, header room, signatures and overlays, the subsystem, the checksum, files that are damaged or hostile, the engine's vendored Windows libraries, damaged and hostile files), the Windows step (the version information and the icon images read back, every copy of the key kept), the Linux step (the executable bit, `icon.png` and the manifest's icon, the escaping of the desktop entry), the macOS step (the plist and its escaping, the `icns` structure and sizes, the bundle layout, the minimum system version, and the Mach-O signature: page hashes of every hash type and page size checked against an independent computation, real signatures from `ThirdParty` reproduced, and hostile lengths and offsets, truncation and random damage all ending in an `ExportException`), and exports for all four targets from any host with a synthesized runtime folder (layout, renamed executable, what is packed, the game
folder untouched, encrypted packs, the output folder rules). `Tests/Weft.Runtime.Tests` runs an exported game from its folder, headless, on its encrypted pack.
