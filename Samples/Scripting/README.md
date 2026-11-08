# Scripting sample

Luau only, no C#: boxes that move and bounce in a cube, a panel to watch and steer them, and tests.

| File | What it is |
| --- | --- |
| `main.luau` | The program: installs the simulation into the host's `world`, spawns 50 boxes, and draws the panel with `devtools.on_gui` |
| `sim.luau` | Components (`Position`, `Velocity`, the `Box` tag), the `Move` and `Bounce` systems, and spawning; a module, so the tests run it too |
| `tests/sim.test.luau` | The simulation for as many frames as each case needs |
| `.luaurc` | `languageMode` and the alias for luau-lsp |

```sh
./build.sh aot                                                       # or run the runtime from the build output
Bin/publish/runtime/Apogee.Runtime --luau-test Samples/Scripting    # the tests
Bin/publish/runtime/Apogee.Runtime --run Samples/Scripting/main.luau                      # in a window
Bin/publish/runtime/Apogee.Runtime --run Samples/Scripting/main.luau --frames 120 --headless
Bin/publish/runtime/Apogee.Runtime --luau-defs Bin/defs             # then point luau-lsp at Bin/defs/apogee.d.luau
```

A sample: run it by hand; CI does not run it. See the Manual for the scripting API, the test library and the runtime.
