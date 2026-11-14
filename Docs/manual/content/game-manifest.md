# The game manifest (game.xml)

A game folder describes itself in a `game.xml` at its root, next to `packages/` and `data/`. It says what the game is called, who made it, which version it is, where the player's files go, its Steam app id,
and what its window looks like. The runtime reads it when it starts a game (`Apogee.Runtime --game <folder>`, the editor's Play), and a game that is exported needs it.

A folder **without** a `game.xml` still runs, as it always did: it has no version (so the `gameVersion` of its mods is not checked), its player's files go in a folder named after the game folder,
and its Steam app id is the one in a `steam_appid.txt`, if there is one. Add a `game.xml` to give it all of that.

```xml
<Game name="Fixture Game" organization="Apogee" version="1.0.0" executable="FixtureGame" identifier="org.apogee.fixturegame"
      description="The shipped-game fixture of the CI." copyright="Copyright 2026 The Apogee Engine Authors">
  <EntryScene path="scenes/main.ascene"/>
  <Icon path="icon.png"/>
  <Window width="1280" height="720" fullscreen="false" resizable="true"/>
</Game>
```

## What it holds

Attributes of `<Game>`:

| | | Default |
| --- | --- | --- |
| `name` | the game's name, required: the window title, the bundle name, the product name of an executable | none |
| `organization` | who made it, required: the folder that holds the player's files of all your games, the company of an executable | none |
| `version` | `major.minor.patch`, required (all three parts): the version of the game that mods are checked against, the file and bundle version | none |
| `executable` | the name of the executable of an export, without extension: letters, digits, `_`, `.`, `-`, up to 64, starting with a letter or digit | the letters and digits of `name` (`Game` if there are none) |
| `identifier` | the reverse-DNS name of the game, lowercase letters, digits and dashes in at least two parts (`org.apogee.fixturegame`): the macOS bundle identifier, the Linux application id | `com.<organization>.<executable>`, lowercase, letters, digits and dashes only |
| `userFolder` | the name of the folder of the player's files, under the organization's | `name` |
| `steamAppId` | the game's Steam app id (a number) | 0, none |
| `description`, `copyright` | text for the executable's properties and for installers | empty |

Children, each at most once:

| | |
| --- | --- |
| `<EntryScene path="…"/>` | a scene that is loaded before the packages' scripts run, as a path inside the game folder (`scenes/main.ascene`). The scripts then find its entities. A file that is not there is an error of the game. |
| `<Icon path="…"/>` | a square PNG inside the game folder, 256 pixels or more (1024 for macOS). An export makes the icons of each platform from it. |
| `<Window width height fullscreen resizable/>` | the window the game opens with: 16 to 16384 pixels wide and high, defaults 1280 x 720, windowed, resizable. |

Paths are plain relative paths inside the game folder: no `..`, no drive or scheme, and the icon is a `.png`. The file is checked against the engine's schema (`game.xsd`, embedded in the
`Apogee.Content` library) and then for what a schema cannot say; every problem comes with the file and line, and **a `game.xml` with problems is an error of the game**: it does not start (exit code 2).

## What it drives

- **The player's folder** (`user://`). `--user <dir>` wins; otherwise it is `<local application data>/<organization>/<userFolder>` with `<>:"/\|?*`, control characters and trailing dots and
  spaces left out of both names (`~/.local/share` on Linux, `AppData/Local` on Windows, `~/Library/Application Support` on macOS). Without a `game.xml` it is
  `<local application data>/Apogee/<game folder name>`.
- **The game version.** `version` is what each package's `gameVersion` range is checked against ([Packages and mods](packages.md)): a mod that needs `^2.0` does not load in game 1.4.0, with a
  reason the game can show the player.
- **The Steam app id.** `steamAppId` is the app id that `StartGame` hands to Steam (see [Steam](../network/steam.md#starting-a-game-under-steam)). A game that has none in its `game.xml` but a
  `steam_appid.txt` in its folder still works, with a warning in the log that the file is deprecated.
- **The entry scene**, when the game starts without a scene of its own (the editor's Play starts from the scene being edited).

## How the runtime finds it

A runtime started with no command looks for `game.xml` in the folder of its executable, then in `../Resources` (the `Contents/Resources` of a macOS app whose executable is in `Contents/MacOS`), and
the folder it finds is the game's root: `data/*.pack` is there too. That game is run with its packs only (nothing else in the folder is `res://`), with the window of `<Window>` and the name of `name`
as its title (`--size <w>x<h>` overrides the size). Both places are tried on every system. Arguments the runtime does not know are logged and ignored when a game is found that way, because Steam
adds `+connect_lobby <id>` to the command line when a player accepts an invitation, and an older macOS adds `-psn_…`; after any command (`--game`, `--pack`, …) they are still mistakes. A development run
(`--game <folder>`) takes the title and the window from `game.xml` too, but the whole folder stays `res://`. With no `game.xml` anywhere the runtime exits with code 1 (use `--game <folder>`, `--smoke` or `--help`).

The window gets the application's name, version and identifier from the manifest before it is created (SDL's application metadata: the macOS menu, Wayland's application id, the audio stream's name).
On Linux the icon of the window is `<Icon>`'s file, read from the game's root when it is there; on Windows and macOS it is the executable's icon and the bundle's.

## From code

```csharp
GameManifest? game = GameManifest.Load(folder, out IReadOnlyList<string> problems);   // null and no problems when there is no file
GameManifest.TryParse(text, "game.xml", out GameManifest? parsed, out problems);       // problems are "file:line: ..." and the manifest is null when there are any
string folder = game!.UserDirectory();                                                  // the player's folder
string xml = game.ToXml();                                                              // every value written out, so that reading it again gives the same manifest
ScriptSession.StartGame(new GameStart { Folder = folder, RealAudio = true });           // reads game.xml itself
```

`GameManifest.SanitizeFileName` is the rule that makes a name a folder name on every platform.
