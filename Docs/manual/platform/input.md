# Input actions

`Apogee.Input` turns keys, mouse and gamepads into **actions** a game defines by name (`jump`, `move_forward`, `lights`). The game's code and mods ask for actions, never for keys, so
a player can rebind everything and one gamepad works where another has different buttons. It is built for games with large action catalogs (about 45 actions).

- `InputActionMap`: every action, its contexts, its defaults and what the player bound it to. Saves and loads the player's bindings.
- `InputActionRouter`: routes input to the actions of the active contexts and reads their strength.
- `InputDevices`: feeds a router from SDL's events (keyboard, mouse, gamepads).

In a game (`--game`, `--run`) scripts see one router as the global `input`, with its own map in `input.map`.

```lua
input.map:add_situation({ "world", "on_foot" })               -- contexts that are active together
input.map:add_situation({ "world", "vehicle" })
input.map:add_situation({ "menu" })
input.map:add_action("move_forward", { "on_foot", "vehicle" }, { "W", "Joy LY-" }, true)   -- analog: read by strength
input.map:add_action("lights", { "vehicle" }, { "L" }, false, "Toggle the headlights")
input.map:add_action("lights_back", { "vehicle" }, { "Alt+L" })

-- every frame, from what the player is doing:
input.contexts = if menu_open then { "menu" } else { "world", "vehicle" }

local throttle = input:get_strength("move_forward")           -- 0..1 (the mouse's motion: pixels of the frame)
input.action_fired:connect(function(action) print(action) end)   -- "lights" when L is pressed
```

## Contexts and situations

A **context** is a name the game chooses. An action works in the contexts it lists, and the router fires only the actions of `input.contexts` (none: no input at all). The game sets
them every frame (`{ "menu" }` while a menu is open, `{ "world", "on_foot" }` walking).

**Situations** say which contexts are active together, and are used only to find clashes: two actions clash when some situation has a context of each and they are bound to the same input.
Without any situation, two actions clash when they share a context. `Q` for the camera in `world` and for a tab in `menu` is no clash when no situation holds both. `get_conflicts(action, binding)`
tells what would clash if an action were bound to a text, and `get_all_conflicts()` lists the clashes among the current bindings, each as `action<TAB>other<TAB>binding`.

An **analog** action is read with `get_strength` and never fires; it clashes with any other action on its input whatever the modifiers. The others fire `action_fired` and also answer
`is_held` (strength at least half) and `get_strength` (0 or 1).

## Bindings

A binding is text, the form players see in a settings file: `[Hold |Double ][Ctrl+][Shift+][Alt+]input`.

| Input | Written |
| --- | --- |
| Keys | by position on a **US QWERTY** keyboard, so AZERTY players get ZQSD for WASD: `A`..`Z`, `0`..`9`, `F1`..`F24`, `Enter`, `Escape`, `Backspace`, `Tab`, `Space`, `Minus`, `Equal`, `BracketLeft`, `BracketRight`, `Backslash`, `Semicolon`, `Apostrophe`, `Grave`, `Comma`, `Period`, `Slash`, `IntlBackslash`, `CapsLock`, `PrintScreen`, `ScrollLock`, `Pause`, `Insert`, `Home`, `PageUp`, `Delete`, `End`, `PageDown`, `Left`, `Right`, `Up`, `Down`, `NumLock`, `Kp0`..`Kp9`, `KpDivide`, `KpMultiply`, `KpMinus`, `KpPlus`, `KpEnter`, `KpPeriod`, `Menu`, and `Ctrl`, `Shift`, `Alt`, `Meta` (left and right are one input) |
| Mouse | `Mouse Left`, `Mouse Right`, `Mouse Middle`, `Mouse Wheel Up`, `Mouse Wheel Down`, `Mouse Back`, `Mouse Forward`, and its motion `Mouse X-`, `Mouse X+`, `Mouse Y-`, `Mouse Y+` |
| Gamepad (Xbox names) | `Joy A`, `Joy B`, `Joy X`, `Joy Y`, `Joy LB`, `Joy RB`, `Joy Back`, `Joy Start`, `Joy Guide`, `Joy L3`, `Joy R3`, `Joy Up`, `Joy Down`, `Joy Left`, `Joy Right`, sticks `Joy LX-` .. `Joy RY+` (`Y+` is down), triggers `Joy LT`, `Joy RT` |

Names are case-insensitive when read and written as listed. `Ctrl+`, `Shift+` and `Alt+` go with keys and mouse buttons, not gamepad inputs. `normalize_binding(text)` writes a text the canonical way and
raises an error (with what is wrong) for one that does not read; `binding_error(text)` returns that message or `""`; `parse_binding(text)` returns the input, the modifiers (a list: `{ "ctrl", "shift" }`)
and the trigger (`"press"`, `"hold"`, `"double_tap"`).

SDL's gamepad mapping decides which physical button is `Joy A` (the south one, so a PlayStation controller's cross). Steam Input's virtual gamepad arrives the same way, so a player's Steam controller
configuration keeps working with no engine support.

## What the router does

- **The most specific modifiers win.** Of the bindings on an input whose modifiers are all held, only those with the most modifiers fire: Shift+Tab does not also fire Tab, and `W` still fires while Shift is held for running.
- **Hold.** A key that also has a `Hold` binding fires its press on a quick release instead; held for 0.4 s it fires the hold, and its release then fires nothing.
- **Double tap.** A key with a `Double` binding waits 0.3 s for a second tap before it fires its press.
- **Axes.** Sticks and triggers have a dead zone of 0.2 (the rest is scaled to 0..1). A stick direction pushed past 0.5 also acts as a button and is released below 0.4, so a stick can fire actions and move a menu.
  The mouse's motion adds up in pixels until the end of the frame (`end_frame`, which the runtime calls).
- **Panels and text boxes.** `InputDevices` takes `KeyboardCaptured` and `MouseCaptured` functions (the runtime gives it the dev overlay's `WantsKeyboard` and `WantsMouse`): while true, new presses and the mouse's
  motion are not routed. Releases always are, so a key that was pressed before a text box got focus cannot stay down. Losing the window's focus releases everything.
- **Changing contexts** drops what was waiting (a hold that had not come due, a first tap).

## Rebinding

```lua
input:listen()                                               -- the next input is not routed: it is reported
input.captured:connect(function(binding) print(binding) end)  -- "Shift+F", "Joy LY-", "Mouse Wheel Up"
input.map:set_bindings("lights", { binding, "Joy RB" })
```

A key goes by the modifiers held with it; a modifier key alone does not count (the router waits for the key it goes with); a stick or trigger counts when pushed past half way. `get_label(action)` is what to
show the player: the binding as their keyboard names it (`Z` on AZERTY for a `W` binding, `&` for a key that types it), its gamepad button (`Pad A`) when a gamepad was used last, or `–` when unbound;
`InputActionRouter.label_binding(text)` does the same for any text (`Hold Ctrl+Left mouse`).

The player's bindings are saved as what differs from the defaults, as text (`map:save_text()`), one line per action, which a game keeps in its user settings and applies at startup with `map:load_text(text)`:

```text
lights = Hold L | Joy RB
road_lights =
```

`load_text` returns the lines it could not apply (an action that no longer exists, a binding that does not read) and applies the rest. From C#, `Save()` and `Load()` use a dictionary of string arrays.

## From C#

```csharp
var map = new InputActionMap();
map.AddAction("lights", ["vehicle"], ["L"]);
var router = new InputActionRouter { Map = map, Contexts = ["vehicle"] };
using var devices = new InputDevices(router) { KeyboardCaptured = () => imgui.WantsKeyboard };   // after PlatformHost.Initialize

// each frame, after PlatformHost.PumpEvents():
router.Update(InputDevices.Now);           // fires ActionFired for holds and taps that came due
// …logic reads router.GetStrength("lights")…
router.EndFrame();
```

`Press`, `Release`, `Axis` and `Poll` take explicit times in seconds, for tests and replays. `ScriptSession.Progress` calls `Update` itself and `EndFrame` ends the frame.

## Not done

Gamepad rumble, touch, gamepad hot-plug events for scripts, per-device bindings and chords of several keys (modifiers are the only combination). Steam Input's own action sets are not used (a Steam controller configuration arrives as an SDL gamepad).

## Tests

`Tests/Apogee.Input.Tests` covers parsing, key names, the map (clashes, saving), the router (modifiers, hold, double tap, axes, listening) and the SDL devices on the dummy video driver with pushed events.
`Tests/Luau/input.test.luau` runs the same checks, with a catalog of about 45 actions, from a script, in the NativeAOT binary too.
