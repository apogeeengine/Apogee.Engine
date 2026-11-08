# Scripting sample

`Samples/Scripting` is a program in Luau only: boxes that move and bounce in a cube, a panel with their count and a speed slider, and tests. It uses the scripting layer, and shows
how a game's scripts are laid out.

- **`sim.luau`** is a module. `sim.install(world, settings)` defines the components (`Position`, `Velocity`, the `Box` tag) and two systems: `Move` carries every box by its velocity
  times `settings.speed` and the frame's time step, and `Bounce` runs after it (`"post_update"`) and puts a box that left the cube back on the wall, turned around. `sim.spawn` makes
  boxes from a small deterministic generator, so a run is the same every time. The settings table is read by the systems each frame: the panel's slider changes it.
- **`main.luau`** is the program. The host gives it `world`; it installs the simulation, spawns 50 boxes, and registers a gui function with `devtools.on_gui` that draws the panel
  (`imgui.begin_window`, `imgui.slider_float`, `imgui.button`) and `devtools.watch`es the speed and the frame in the debug window.
- **`tests/sim.test.luau`** runs `sim` in a world of its own for as many frames as each case needs: spawning, the same places every run, motion by velocity, the speed setting, a bounce at the wall,
  and sixty boxes staying inside the cube for ten seconds.

```sh
Apogee.Runtime --luau-test Samples/Scripting
Apogee.Runtime --run Samples/Scripting/main.luau                    # in a window, with the panel and the dev overlay
Apogee.Runtime --run Samples/Scripting/main.luau --frames 120 --headless
```

This is a sample: you run it by hand, and nothing in CI runs it (the Luau library it uses is tested by `Tests/Luau` and the engine's own test projects). For luau-lsp, `Apogee.Runtime --luau-defs Bin/defs` writes the definitions and the sample's `.luaurc` points
the `engine` alias at them. See [Scripting](../../manual/scripting/scripting.md), [Runtime and Luau tests](../../manual/scripting/runtime.md) and [Dev tools](../../manual/tools/devtools.md#dear-imgui-for-luau-scripts).
