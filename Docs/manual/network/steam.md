# Steam

`Apogee.Steam` connects a game to **Steam**: the player's name and language, the overlay, achievements, stats and leaderboards, rich presence, the timeline, cloud saves, and Workshop mods. In a game, scripts see
it as the global `steam` (the `SteamSystem` class). It is the base game's: **a mod cannot use it** (`ModsBlocked`), and a base-game function a mod calls still acts as the base game.

Steam's library (`steam_api`) is Valve's redistributable and is in the repository; the Steam *client* is the player's. **When Steam is not running, or the game was not started with an app id,
`steam.is_active` is false and every call does nothing and answers with something inert** (the log says why, once, and `steam.status` has the words), so a game works the same on a machine without Steam,
under `--headless`, and in a test. A real, windowed run connects; `--headless` and tests do not.

```lua
if steam.is_active then
	print(steam.user_name, steam.language)          -- "Ada", "english"
end

steam:unlock_achievement("ACH_WIN_ONE_GAME")        -- shows the pop-up, and sends it to Steam
steam:set_stat_int("NumGames", steam:stat_int("NumGames") + 1)
steam:store_stats()                                  -- stats are in memory until stored

steam:set_rich_presence("status", "Delivering hay")  -- the line friends see
steam:add_timeline_event("Crash", "Hit a tree", "steam_death")

steam.overlay_activated:connect(function(open: boolean) paused = open end)
```

## The frame and the signals

`ScriptSession.Progress` calls `steam:update()` first in every frame. It uses Steam's *manual dispatch*: Steam's callbacks and the results of asynchronous calls are collected there, on the calling thread, and turn
into signals. Nothing runs on a thread of Steam's. A script never waits: a call that needs Steam to answer returns at once, and a signal carries the answer.

| Signal | When |
| --- | --- |
| `overlay_activated(open)` | the overlay opened or closed: pause the game while it is open |
| `shutdown_requested()` | Steam is shutting down and wants the game to exit |
| `stats_received(ok)`, `stats_stored(ok)` | the player's stats arrived; a `store_stats` finished |
| `achievement_stored(name, current, max)` | an achievement unlocked or progressed |
| `leaderboard_found(name, found)`, `score_uploaded(name, ok, rank)`, `scores_downloaded(name, users, ranks, scores)` | the answers of the leaderboard calls (`ok` false and no entries when it failed or Steam is off) |
| `text_input_dismissed(submitted, text)` | Steam's on-screen keyboard closed |
| `workshop_item_installed(id)`, `workshop_item_downloaded(id, ok)`, `workshop_subscribed(id, ok)`, `workshop_unsubscribed(id, ok)` | Workshop |

Steam's 64-bit ids (the player, a Workshop item) are **decimal strings** in scripts, because a script number cannot hold them.

## What it does

- **The user and the app:** `user_id`, `user_name`, `app_id`, `build_id`, `language` (the language the player chose for the game, as Steam names it: `english`, `french`, `schinese`; a game can take it as the default language), `ui_language`, `country`, `is_subscribed`, `is_steam_deck`, `is_big_picture`, `owns_dlc(app_id)`, `is_dlc_installed(app_id)`.
- **Overlay:** `activate_overlay("friends" | "community" | "players" | "settings" | "stats" | "achievements" | "")`, `open_web_page(url)`, `open_store(app_id)`, `is_overlay_enabled`, and `show_text_input(description)`
  for the Deck's on-screen keyboard.
- **Achievements and stats:** `achievement(name)`, `unlock_achievement`, `clear_achievement`, `achievement_progress(name, current, max)`, `achievement_names()`, `stat_int`/`stat_float`,
  `set_stat_int`/`set_stat_float`, `store_stats`, `reset_all_stats(achievements_too)`. Steam loads the stats when the game starts: `stats_received` says when. Achievements and stats are *defined* in Steamworks
  (the partner site), by name; this only sets them.
- **Leaderboards:** `find_leaderboard(name)`, `upload_score(name, score, keep_best)`, `download_scores(name, from, to, "global" | "around_user" | "friends")`. A leaderboard that was not looked up yet is looked up by the
  call that needs it.
- **Rich presence:** `set_rich_presence(key, value)` (`status` is the line shown in the friends list), `clear_rich_presence()`.
- **Screenshots and the timeline:** `trigger_screenshot()`, `add_screenshot(path, width, height)`; `add_timeline_event(title, description, icon, priority, offset_seconds, featured)`, `set_timeline_state(text)` and
  `set_timeline_mode("playing" | "staging" | "menus" | "loading")` give the player's Steam timeline (the game recording) its markers.

## Cloud saves

`ScriptSession.StartGame` mounts **`cloud://`** in the game's file system. With Steam running and the cloud on (`steam.is_cloud_enabled`: the player has it on for the account and for the game) it is the player's Steam Cloud
files for the game; otherwise it is a plain folder (`<user folder>/cloud`). A game saves to `cloud://` in both cases, with the same code:

```lua
fs.write_text("cloud://slot1/save.json", json)       -- follows the player to their other machines
local text = fs.read_text("cloud://slot1/save.json")
```

Steam keeps cloud files flat, with `/` in their names, so a folder exists only as far as a file's name says; a write that Steam refuses (too big, or the space is used up) fails like a write to a full disk. `steam:cloud_quota()`
gives the total space and what is left. The game must also turn the cloud on for its files in Steamworks (the app's *Steam Cloud* settings: a byte and a file count quota). What is saved in the plain folder while Steam was
off is not copied into the cloud later.

## Workshop mods

A Workshop item is a mod: a folder with a `mod.xml` at its root, uploaded by its author, that Steam downloads and keeps up to date for subscribers. When Steam runs, the game mounts the installed items at
`workshop://` (Steam's own folders, read-only), each listed under its `mod.xml` id, and the package finder searches it (`PackageOptions.ExtraUserModsPaths`) next to `user://mods`: a Workshop mod is one of
the player's mods in every way (the same solver, the same "switch it off" list in `user://mods.xml`, the same limits for mods). If an own mod and a Workshop item share an id, the one found first (the player's own) wins.

Mods are read when the game starts. `workshop_item_installed(id)` says a subscription finished installing: tell the player to restart. `subscribe(id)`, `unsubscribe(id)`, `subscribed_items()`,
`workshop_item_folder(id)` and `download_workshop_item(id)` are there for a mod browser in the game's menu.

### Uploading a mod

```sh
cd path/with/steam_appid.txt          # the game's app id; the Steam client must be running and signed in as a partner of the game
Apogee.Runtime --workshop-upload Mods/my_mod --title "My mod" --description "What it does" --preview preview.png --tags Vehicles,Tools --visibility private
Apogee.Runtime --workshop-upload Mods/my_mod --title "My mod" ... --confirm      # this one really uploads
Apogee.Runtime --workshop-upload Mods/my_mod --item 1234567890 --note "Fixed the hitch" --confirm   # an update of an item
```

Uploading makes something visible under the author's Steam account, so **without `--confirm` the command only prints what it would send**. A new item starts `private`: change it to `public` on the Workshop page, or
upload with `--visibility public` once it is ready. The first time, Steam wants the Workshop legal agreement accepted (the command prints the link). The folder must have a `mod.xml`; everything in it is sent.

## Starting a game under Steam

A game that ships on Steam has the game folder's `steam_appid.txt` (the app id alone on one line) or Steam's own launch to tell the app id. `StartGame` reads the file and asks Steam to relaunch the game through itself when
it was started some other way (`SteamAPI_RestartAppIfNecessary`): the process then ends at once (`RestartRequested`). A **`steam_appid.txt` next to the program** turns that off, which is how you run it during development
(Steam reads the app id from the file; use 480, Valve's *Spacewar*, to try the API without an app of your own). A watched game (`--watch`) never asks for a relaunch.

Steam Input is not wrapped: SDL sees Steam's virtual gamepad, so a player's Steam controller configuration works with the [input actions](../platform/input.md) as it is. Steam's networking and matchmaking are bound
([Native libraries](../native/native-libraries.md#steamworks-apogeenativesteam)) but have no engine layer yet.

## Tests

- `Tests/Apogee.Steam.Tests`: the inactive system (every call), the Workshop source, `workshop://` through the package finder, and the cloud's fallback folder. They need no Steam.
- **Against a real client** (off by default): `APOGEE_STEAM_TESTS=1 ./build.sh test` with the Steam client running and signed in. They use *Spacewar* (480) and change that account's Spacewar stats and cloud files.
- `Tests/Luau/steam.test.luau`: the same inactive behavior as scripts see it, in the runtime's Luau tests.
