# Runtime and Luau tests

`Apogee.Runtime` is the game host: one executable, CoreCLR in dev builds and NativeAOT when shipped (`./build.sh aot` publishes it to
`Bin/publish/runtime`). Its command line is parsed by hand so that it stays AOT-safe.

```text
Apogee.Runtime                                    load the native libraries and run the smoke tests
Apogee.Runtime --luau-test <folder> [--luau-filter <text>]
Apogee.Runtime --run <file.luau> [--frames <n>]   a script with a `world` global, in a window (until it is closed, or n frames)
    --headless                                    no window and no GPU: n frames (default 1)
    --headless --screenshot <file.png>            no window, rendered on the GPU: the last frame as an image
    --size <w>x<h>  --overlay  --gpu <name>       the window or image size (1280x720), the dev overlay in images, which GPU
Apogee.Runtime --game <folder> [--user <folder>]  a game: its packages and mods (see Packages and mods), then the same loops as --run
Apogee.Runtime --workshop-upload <mod folder> ... publish a mod folder as a Steam Workshop item (only describes it without --confirm; see Steam)
Apogee.Runtime --luau-defs <folder>               apogee.d.luau and the engine's Luau modules, for luau-lsp
```

Exit codes: 0 fine, 1 bad arguments, missing files or failed tests, 2 a script or the engine failed (including Vulkan and window errors), 3 the Vulkan validation
layer reported errors.

## `Apogee.Host`: what runs a game

`Source/Apogee.Host` holds what the runtime and the editor share, so that both run a game the same way: `ScriptSession` (the world, the Luau VM, physics, audio, Steam, the UI, the model library and the hot
reloader, made from a script or a game folder), `WorldView` (the world drawn through its main camera), `FrameOverlay` (the game's UI and then the dev tools over the picture), `GameLibrary` (the script table
`game`) and, in `Apogee.Host.Editing`, the editor's undo and redo: `EditHistory` and the commands it runs (`SetComponentCommand`, `AddComponentCommand`, `RemoveComponentCommand`, `CreateEntityCommand`,
`DestroyEntityCommand`, `ReparentCommand`, `RenameCommand`, `CompositeCommand`). `Apogee.Runtime` is the command line and the three loops over them.

`EditHistory.Execute(command)` runs a command and records it; `Undo` and `Redo` take it back and do it again; the changes of one gesture (a drag of a slider, many small `SetComponentCommand`s on one
component) merge into one step until `Seal()` is called, which the editor does when the mouse is let go. Deleting and creating entities are undone through snapshots, so an entity comes back under the same
id and the selection and the other commands in the history still point at it. `ReparentCommand` keeps the entity where it is in the world (it rewrites its `Transform`) and refuses a parent that is the
entity itself or one of its descendants. A session registers the scene converter for `ModelRenderer.Model` (the model's path) and leaves physics bodies out of scenes.
`RenderTarget.Texture(image, bindlessIndex)` renders the world into an image that is left ready to be sampled, for the editor's viewport.

## The frame loop of `--run`

Each frame steps the world, runs an ImGui frame in which the scripts' `devtools.on_gui` functions draw, and draws that over a cleared background. There are
three loops, which differ only in where the frame goes (`RuntimeLoop`):

- **A window** (the default): an SDL window with a Vulkan swapchain, set up as the [viewer sample](../../getting-started/samples/viewer.md) does it. ImGui gets the window's input, so the
  panels are live, and the dev overlay is on (F1 toggles it). Esc, closing the window, or `--frames` ends it. The time step is the real one, capped at a quarter second.
- **`--headless --screenshot`**: no window, an offscreen image of `--size`, and a fixed 1/60 s step, so that frame n always looks the same. The last frame is
  written as a PNG. The overlay is left out unless `--overlay`, because its timings change from run to run. CI renders the fixture game (`Tests/Fixtures/ShippedGame`) this way on lavapipe.
- **`--headless`**: no window and no GPU. The frames run and nothing is shown, but everything a script does with `imgui` runs. This is what CI runs on machines
  without Vulkan.

The scripts' `imgui` calls do nothing outside an ImGui frame, so the same script works in all three.

## Luau tests

`--luau-test` runs every `*.test.luau` under a folder, sorted, skipping folders that start with a dot. Each file gets a VM of its own, so one file's
globals, modules and worlds cannot touch another's; the file may `require` anything under the folder, with the `@engine/test` library:

```lua
local test = require("@engine/test")

test.describe("health", function()
    test.it("drops when hit", function()
        local world = test.world()                      -- this case's own World, destroyed after it
        local Health = world:component("Health", { hp = "i32" })
        local hero = world:create_entity()
        world:set(hero, Health, { hp = 10 })
        world:system("Poison", { Health }, function(_, health) health.hp -= 1 end)

        test.frames(3)                                   -- three frames of that world

        test.expect(world:get(hero, Health).hp).toBe(7)
    end)
end)
```

A file's `describe` bodies run as it loads and collect its cases; the cases then run in order, one frame at a time, each in a coroutine. An error in a `describe` body
is reported as a failure named after it. Expectations are `toBe`, `toEqual` (deep), `toBeNear(n, epsilon)`, `toBeTruthy`, `toBeFalsy`, `toBeNil`, `toThrow(text)` and
`toContainText(text)`, and `.never` negates any of them. `print` goes to the output. `--luau-filter text` runs the cases whose full name (`describe > case`) contains it.
A case that waits for more than 100 000 frames, or one call that outlives the 10 s time budget, fails the file.

The output, and the same from code (`LuauTestRunner.Run`):

```text
ok   ecs.test.luau (8 passed)
FAIL bad.test.luau (1 passed, 1 failed)
  - group > fails
    bad.test.luau:5: expected: 1 to be 2

Luau tests: 9 passed, 1 failed, in 2 files (0.02 s).
```

The engine's own Luau tests are in `Tests/Luau`. They run in `dotnet test` (in the JIT build) and in CI against the published NativeAOT binary.

## Input

A game's scripts see the global `input`, an `InputActionRouter` whose map they fill with their actions (see [Input actions](../platform/input.md)). The windowed loop feeds it from SDL events; `ScriptSession.Progress`
fires what time made due before the systems run, and `EndFrame` resets the mouse's motion. A headless run has no devices, and scripts can press inputs by hand.

## Physics

A game's scripts see the global `physics`, the simulation of the session's world (see [Physics](../physics/physics.md)). `ScriptSession.Progress` steps it before the world each frame, so that the systems and scripts of the frame
see the poses it wrote. It is disposed before the ECS world, which holds its observer.

## Audio

The global `audio` is the session's `AudioSystem` (see [Audio](../audio/index.md)). It plays on the sound device only in a real run: `--headless` and tests mix into nowhere (OpenAL Soft's loopback device, in the game's time). `ScriptSession.Progress` updates it after the
world has progressed, and it is disposed before the physics world and the ECS world.

## Steam

The global `steam` is the session's `SteamSystem` (see [Steam](../network/steam.md)), the base game's only. A real (windowed) game connects to Steam; `--headless`, `--run` scripts and tests get an inactive one. `ScriptSession.Progress`
updates it first, so that Steam's callbacks are signals before the systems and scripts of the frame run. `StartGame` also mounts `cloud://` and, with Steam running, `workshop://`.

## Network

The global `net` is the session's `NetSystem` (see [Network](../network/network.md)), the base game's only. It is made for every session and opens no socket until a script listens or connects. `ScriptSession.Progress` updates it after Steam's callbacks
and before the input, and it is disposed before Steam.

## Editor definitions

`--luau-defs <folder>` writes `apogee.d.luau`, the API from the registry (see [Scripting](scripting.md#definitions-for-the-editor)), and `engine/<name>.luau`, copies of the engine's
modules, so that luau-lsp can resolve `require("@engine/test")`. A project's `.luaurc` points the `engine` alias at that folder; at run time the alias is built in.

## Tests

`Tests/Apogee.Runtime.Tests` (the command line and each command, with their exit codes, and the fixture game run from a folder and from a pack) and, in `Tests/Apogee.Scripting.Tests`, the runner itself: discovery order,
the report, a file that does not compile, errors outside a case, filters, files that share nothing, and a loop stopped by the time budget.

## What a game asks of the program

The script table `game` is for a game that runs inside `Apogee.Runtime`: `game.quit()` ends the run at the end of the frame (a menu's Quit; a headless run stops too), and `game.set_escape_quits(false)` stops the Escape key from ending a run in a window, which is the default so that a script or a sample can be closed: a game whose Escape opens its own menu turns it off. See the [UI sample](../../getting-started/samples/ui.md).
