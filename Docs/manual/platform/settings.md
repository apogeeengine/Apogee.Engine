# Settings

The player's settings (window size, fullscreen, volumes, input bindings, and whatever the game and its mods add) live in one store, the `settings` global, and in one file, `user://settings.xml`. Scripts
define the keys they care about, read and change them, and are told when one changes. The engine's own settings are wired to what they control: change `audio.volume` and the master volume follows, change
`window.fullscreen` and the window goes fullscreen.

```lua
settings:define("game.show_hints", { type = "bool", default = true, help = "Show hints on the first day." })
settings:define("game.fov", { type = "int", default = 90, min = 60, max = 120 })

if settings:get("game.show_hints") then
    print("Press E to use things.")
end

settings:set("game.fov", 100)                    -- saved within a second; the next run starts with 100
settings.changed:connect(function(key)
    if key == "game.fov" then print("fov is now", settings:get(key)) end
end)
```

## The file

```xml
<settings version="1">
  <value key="audio.volume">0.8</value>
  <value key="window.fullscreen">true</value>
  <value key="mod.coolmod.show_hints">false</value>
  <bindings>
    <action id="jump">Space | Joy A</action>
  </bindings>
</settings>
```

- **Only what differs from the default is written.** A setting the player never touched is not in the file, so when a game update changes a default, those players get the new one. A game that has changed
  nothing has no file at all.
- **Keys the game does not define are kept.** A mod that is switched off for a while keeps its settings (and its players' bindings) in the file, and finds them again when it comes back. A value is read
  when its key is defined, which scripts do while they load.
- **A value that does not read** (text where a number should be, a choice that does not exist any more) is logged and the default is used; a number out of its range is clamped. A file that is not XML at all
  (or whose root is not `<settings>`) is logged and kept as `settings.bad.xml` next to it, the defaults are used, and the next save replaces `settings.xml`. A `version` above 1 is read as far as the engine understands it.
- **Writing is atomic.** The file is written as `settings.xml.tmp` and moved over `settings.xml`, so a crash or a full disk leaves the old file whole. If it cannot be written at all (a read-only folder) the
  game logs it once and goes on; the changes stay in memory and are tried again.
- **Writing is debounced.** A change is written at the end of the frame if the file was not written in the last second, and otherwise a little later: a slider that is dragged makes one write a second, not one a
  frame. The session writes what is left when it ends.

String values lose spaces at their ends (the file format trims text). A `Settings.new()` store (below) is in memory and never written.

## Keys and types

A key is two or more words of `a-z`, `0-9` and `_` joined by dots: `audio.volume`, `game.fov`, `mod.coolmod.show_hints`. Define it before you use it:

| `type` | holds | notes |
| --- | --- | --- |
| `"bool"` | `true` or `false` | |
| `"int"` | a whole number | `min` and `max` clamp it |
| `"number"` | a number | `min` and `max` clamp it |
| `"string"` | text | |
| `"choice"` | one of `choices` | a list of different strings; the default is one of them |

`default` is required. `restart = true` marks a setting that takes effect at the next start (below), and `help` is a line for the options menu. Anything else in the table is an error, so a typo does not go unnoticed.

| Call | |
| --- | --- |
| `settings:define(key, spec)` | Defines a key. Defining it again with the same type (a script that hot-reloaded) replaces the spec and keeps the player's value; another type, or a key that another script owns, is an error. |
| `settings:get(key)` | The value. An undefined key is an error: a typo is not silently `nil`. |
| `settings:set(key, value)` | Changes it. A number out of range is clamped; a value of the wrong type, an integer setting given 90.5, or a choice that is not one of the choices is an error. |
| `settings:reset(key)`, `settings:reset_all(prefix?)` | Back to the defaults: one key, the keys that start with `prefix`, or all of them. |
| `settings:keys(prefix?)` | The defined keys that start with `prefix` (all without it), sorted. |
| `settings:info(key)` | `{ type, default, min, max, choices, restart, help }`, leaving out what does not apply, or `nil` for a key that is not defined: what an options menu is built from. |
| `settings:save()` | Writes now. Returns `false` (and logs) when it could not. Scripts do not need it: saving is automatic. |
| `settings.changed` | Signal with the key, fired at once, after the store holds the new value, and **only when the value really is different**. |
| `settings.needs_restart` | `true` when a `restart` setting holds another value than this run started with. |

## The engine's settings

| Key | Type | Default | |
| --- | --- | --- | --- |
| `window.width`, `window.height` | int, at least 320 and 240 | the `<Window>` of [game.xml](../content/game-manifest.md), else 1280 x 720 | The size the window opens with. While the player resizes the window it is stored once the size has been left alone for 20 frames, and never while fullscreen. |
| `window.fullscreen` | bool | the `<Window>` of `game.xml`, else `false` | Takes effect at once. |
| `graphics.vsync` | bool, `restart` | `true` | Frames wait for the display. Read when the window is made: the change shows at the next start, and `settings.needs_restart` says so. |
| `audio.volume` | number, 0 to 1 | `1` | The master volume (`audio.volume` of the audio system). |
| `audio.bus.<name>` | number, defined by the game | | The volume of the bus `<name>`: define `audio.bus.music` as a number and `audio:get_bus("music").volume` follows it, from the start (with the saved value) and on every change. |

A script cannot define these keys again. The command line wins over the settings for the window: `--size 800x600` opens the window that size and the player's saved size is left alone. `--headless` and
`--screenshot` ignore the window keys (there is no window), but the volume and everything else apply.

## Mods

A mod can read every key. It can define and change only `mod.<its id>.…` and the `audio.bus.<name>` keys it defined itself; anything else is the error
`a mod can only change its own settings (mod.<id>.…)`. `reset_all` in a mod needs the prefix of its own keys (`settings:reset_all("mod.coolmod.")`). A mod that fails to load keeps what it defined, and its keys stay in the file.

## Input bindings

The input map's rebindings are part of the file, under `<bindings>`, in the form [`save_text`](input.md#rebinding) writes them: only the actions that differ from their defaults. A game has nothing to do for them:
`input.map:set_bindings`, `reset` and `reset_all` mark the settings to be saved.

An action can be added after the settings are read (a mod's scripts run after them). The saved bindings of an action that does not exist yet wait, are applied when `add_action` creates it, and are written back
meanwhile, so switching a mod off does not lose its players' bindings. (`reset_all` puts every action that exists back to its defaults and leaves the waiting ones alone.) From C#, `InputActionMap.BindingsChanged`
is the event and `LoadSaved` the call.

## An options menu

```lua
local keys = settings:keys("game.")
for _, key in keys do
    local info = settings:info(key)
    if info.type == "bool" then
        add_checkbox(key, info.help, settings:get(key), function(on) settings:set(key, on) end)
    elseif info.type == "int" or info.type == "number" then
        add_slider(key, info.help, info.min, info.max, settings:get(key), function(v) settings:set(key, v) end)
    elseif info.type == "choice" then
        add_dropdown(key, info.help, info.choices, settings:get(key), function(v) settings:set(key, v) end)
    end
end

local function show_restart_note(visible: boolean) end -- the game's own UI
settings.changed:connect(function() show_restart_note(settings.needs_restart) end)
```

## From C#

```csharp
var files = new VirtualFileSystem();
files.Mount("user://", new DirectorySource(userFolder, writable: true));

SettingsStore settings = SettingsStore.Open(files);                       // user://settings.xml; a missing file means the defaults
EngineSettings.Register(settings, new EngineSettingDefaults(Width: 1920, Height: 1080));
settings.Define(SettingDef.OfInt("game.fov", 90, min: 60, max: 120));
settings.Bindings = inputMap;                                              // rebindings are kept in the file
EngineSettings.ConnectAudio(settings, audio);                              // audio.volume and audio.bus.* drive the mix

int fov = settings.GetInt("game.fov");
settings.Set("game.fov", Value.Int(100));                                  // Changed fires; IsDirty is true

// each frame:
settings.SaveIfDirty(TimeSpan.FromSeconds(1));
// at the end:
settings.Flush();
```

`ScriptSession` does all of this: `session.Settings` is the store, saved at the end of every frame it is due and when the session is disposed. `EngineSettingDefaults` takes the window from `game.xml`. The
store belongs to the main thread. `new SettingsStore()` (and `Settings.new()` in a script) is the same store in memory, which is how the tests use it; a single script run with `--run` gets one too.

## Tests

`Tests/Apogee.Host.Tests/SettingsStoreTests.cs` covers the file (round trip, only non-defaults, unknown keys kept, a corrupt file set aside, the atomic write on a read-only folder), the types and ranges, the signal, the
debounce with a fake clock, the mods' namespace, redefinition, the audio keys with a real `AudioSystem`, the bindings, and the window size tracking. `Tests/Luau/settings.test.luau` covers the script side and
`AGameKeepsItsSettingsInItsUserFolder` in `Tests/Apogee.Runtime.Tests` starts a game, changes a setting and a binding, ends the session, and starts it again.
