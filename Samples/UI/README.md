# UI sample

A small game made of RML and RCSS documents with Luau scripts and data models (a menu, a HUD and a scene), and a mod that adds a panel to the HUD. See `Docs/getting-started/samples/ui.md`.

```sh
Apogee.Runtime --game Samples/UI/game --user /tmp/ui-user                  # in a window: Start, play, Esc to pause
Apogee.Runtime --game Samples/UI/game --user /tmp/ui-user --watch           # edit a script while it runs
Apogee.Runtime --luau-test Samples/UI                                       # the Luau tests (tests/)
echo playing > /tmp/ui-user/autostart.txt                                   # start somewhere else than the main menu: menu, options, playing, paused
```

- `game/packages/base_ui`: the game. `ui/` holds the documents (`menu`, `options`, `hud`, `pause`: RML, a script each, and `theme.rcss`); `lib/state.luau` is the state and the flow between screens; `lib/screens.luau` loads and
  shows the documents; `lib/hud.luau` and `lib/options.luau` are the data models; `lib/scene.luau` the physics scene behind it (drawn as debug wireframes: the sample ships no models).
- `game/dev_mods/crate_panel`: a mod. It requires `@base_ui/lib/hud` and puts an RML fragment (with its RCSS) in the HUD's right-hand slot: Luau, RML and RCSS and nothing else.
