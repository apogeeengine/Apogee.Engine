# UI sample

`Samples/UI` is a small game whose UI is plain RmlUi: a main menu, an options screen, a pause menu, and a HUD over a physics scene, written as **RML and RCSS documents with Luau scripts and data models**, plus a **mod** that adds
a panel to the HUD. It is the model for how a game and its mods build UI with [the `ui` surface](../../manual/ui/index.md): documents that carry their own [scripts](../../manual/ui/index.md#scripts-in-documents) and [data models](../../manual/ui/index.md#data-models).

```sh
Apogee.Runtime --game Samples/UI/game --user /tmp/ui-user          # Start, play; Esc pauses and resumes
echo playing > /tmp/ui-user/autostart.txt                          # menu, options, playing or paused: where the game starts
Apogee.Runtime --luau-test Samples/UI                              # the sample's own tests
```

A window, a keyboard and a mouse are all it needs. Like the other samples it is run by hand (and with `--watch` to edit while it runs), and nothing in CI runs it: the engine's behavior it relies on is tested in the engine's own tests.

## The game

`game/packages/base_ui` is the base package. Its files are small, and each has one job:

| File | What it holds |
| --- | --- |
| `main.luau` | Ties the scene to the screen (the world is paused except while playing; Start builds the scene and launches the ball), and starts the screens. |
| `lib/state.luau` | The game's state as plain values (`screen`, `volume`, `theme`, the speeds), the flow between screens (`start`, `open_options`, `close_options`, `to_menu`, `escape`) and the formatting helpers. It tells whoever registered (`on_screen`, `on_theme`) when something changed. Nothing else knows what a screen is called, and nothing here knows what a document is. |
| `lib/screens.luau` | Loads the four documents once, shows and hides them as the screen changes (the pause menu is shown after the HUD, so it is drawn over it), puts the theme class on their bodies, and sends Escape to `state.escape`. |
| `lib/hud.luau` | The `hud` data model (`{{speed}}`, `{{top_speed}}`, `{{fps}}`, the minimap dot) and the **slots**: `hud.add_panel("hud_right", rml, rcss)` puts a panel in a place the HUD document reserves. |
| `lib/options.luau` | The `options` data model (the theme's name, the volume as text and as a bar width) and what the buttons do to it. |
| `lib/scene.luau` | The scene behind the UI: a ground, a pyramid of crates and a ball (physics bodies, drawn as debug wireframes: the sample ships no models), a camera entity, and a system that reads the ball into the HUD's model every frame. |
| `ui/theme.rcss` | The look: colors are custom properties on `body` (`--surface`, `--fg`, `--primary`...), and `body.light` redefines them, so putting the class `light` on a document's body switches the theme. Cards, buttons, rows and the volume bar are rules here. |
| `ui/*.rml`, `ui/*.luau` | A document and its script for the menu, the options, the pause menu; the HUD has no script, only its data model. A script is a `<script src>` of the document, and its functions are what the `onclick="on_start()"` attributes call. |

Things the sample shows on purpose: the **state is separate from the documents** (the scripts of a document only call into `state`, and the tests exercise `state` without any UI); **a theme is a set of custom
properties** and the Options button restyles every document by changing a class; **values that change continuously are a data model** (`data-style-left` on the minimap dot, `{{speed}}`), updated once a frame with
`model:dirty()`; and **the data models are opened before the documents that use them load**.

The game says `game.set_escape_quits(false)`: in a window the runtime ends a run on Escape, which a game with a pause menu cannot have. `game.quit()` is what its Quit button calls. `physics.paused` is what the pause menu
sets: no time is added to the simulation while it is true, so the world stays where it was and resumes from there.

## The mod

`game/dev_mods/crate_panel` is a mod: a `mod.xml` that depends on `base_ui`, a script, an RML fragment and its RCSS.

```lua
local hud = require("@base_ui/lib/hud")
local scene = require("@base_ui/lib/scene")

local panel = hud.add_panel("hud_right", fs.read_text("mod://crate_panel/panel.rml"), fs.read_text("mod://crate_panel/panel.rcss"))
panel:by_id("crate_boost"):listen("click", function() scene.boost() end)
```

It uses the game's modules (`@base_ui/...`, a dependency's, which a mod may require) and nothing in the base game mentions it: delete the folder and the panel is gone. It is Luau, RML and RCSS only, as every mod
is. The mod listens on its own elements instead of writing `onclick` attributes in its fragment: an attribute a script writes runs as that script (as the mod, with the mod's limits, and without the base document's
functions), so it could not call the game's functions anyway. What is not confined yet is what the UI may *load*: `ui:load_document` and `ui:load_font` read any mounted path, like the `audio` and `models` libraries, because
the engine's classes do not know which mod called them; a document's `<script src>` is confined to what the document's owner may read.

## Tests

`Samples/UI/tests` holds Luau tests of the pieces that need no UI (the formatting, the volume and the minimap arithmetic, the screen flow); run them by hand with `Apogee.Runtime --luau-test Samples/UI`.
The things the sample uses are tested where they live: scripts and data models in documents (`Tests/Apogee.UI.Tests`, `Tests/Apogee.Runtime.Tests`), `physics.paused` ([physics](../../manual/physics/physics.md)), `game.quit` ([runtime](../../manual/scripting/runtime.md)).
