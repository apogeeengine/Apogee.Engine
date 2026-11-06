# Dev tools

`Apogee.DevTools` is Dear ImGui (the docking branch, through cimgui) wired into the engine: an SDL3 input backend and a Vulkan renderer backend
written in C#, a thin C# layer over ImGui's C API, and the engine's debug window. The editor and the game's own dev windows use the same pieces.

## Using it

```csharp
using var imgui = new ImGuiHost();
imgui.Attach(window);                                              // input from this window
using var imguiRenderer = new ImGuiRenderer(device);               // draws ImGui's output
var overlay = new DevOverlay();

// each frame
imgui.BeginFrame(deltaSeconds);                                    // display size and scale come from the window
overlay.Draw(new DevOverlayData { GpuName = gpuName, CpuFrameMilliseconds = cpuMs, GpuTimings = loop.Timings,
                                  Plan = renderer.LastPlan, Stats = renderer.LastStats, Settings = settings, Camera = camera });
Ui.Begin("My window"); Ui.Text("anything"); Ui.End();             // any ImGui widgets
imgui.EndFrame();

renderer.Render(frame, scene, camera, target, settings, overlay: imguiRenderer);   // the overlay is the last pass of the frame's graph
```

`imgui.WantsMouse` / `WantsKeyboard` say when ImGui has the pointer over a widget or a text field active: the game should not also react then.

## Docking

The vendored Dear ImGui is the `docking` branch (the tag `v1.93.0-docking`, see `ThirdParty/README.md`; before 2026-11-05 it was by mistake a `master` commit without the docking API).
Docking is off until a host asks for it, because the dev overlay's windows are meant to float:

```csharp
imgui.EnableDocking();
// each frame, before the windows that should dock:
uint dockspace = ImGui.DockSpaceOverViewport(0, ImGui.GetMainViewport(), ImGuiDockNodeFlags.PassthruCentralNode, null);
```

Windows dock inside the one SDL window. ImGui's multi-viewport (windows that leave the main window as OS windows) is not enabled, so there is one swapchain. The editor
builds its layout on this.

## The SDL input backend

`ImGuiHost.Attach(window)` listens to `PlatformHost.RawEvent`, so ImGui gets its input as the platform pumps events: the pointer and buttons (SDL's
left, middle, right are reordered to ImGui's), the wheel, every key (an SDL scancode to an `ImGuiKey` table: letters, digits, function keys,
arrows, the keypad, modifiers), typed text (UTF-8), window focus, and the pointer leaving the window. Each frame it also sets the OS mouse cursor to
the one ImGui asks for (arrow, text beam, resize arrows) and starts or stops SDL text input as text fields appear. The clipboard goes through
SDL. Events of other windows are ignored; `AttachToAllWindows()` is for headless use.

## Text

Dear ImGui rasterizes its fonts with FreeType (`imgui_freetype.cpp` is part of the `cimgui` library, see [Native libraries](../native/native-libraries.md#freetype-apogeenativefreetype)): text is
hinted and anti-aliased by FreeType instead of ImGui's built-in stb_truetype (which is still compiled in, because cimgui calls it, but is not the default). The loader's name
(`FreeType (2.15.0; 2.15.0)`, compiled against and linked) is `atlas->FontLoader->Name`, which `TheGlyphsAreRasterizedByFreeType` checks. Colour glyphs are off
(`ImGuiFreeTypeLoaderFlags.LoadColor`), so the atlas stays a single-channel coverage image that `ImGuiRenderer` expands to RGBA8; the flags can be set per font through
`ImFontConfig.FontLoaderFlags`.

## The Vulkan renderer backend

`ImGuiRenderer` is an `IRenderOverlay`: it adds one pass to the frame's render graph that draws on the output after tone mapping. It follows
Dear ImGui 1.92's texture protocol: ImGui says which textures to create, update and destroy, and the backend answers with an id per texture. The font atlas
becomes an RGBA8 image with a bindless slot; an update creates a new image and defers freeing the old one until the GPU has finished the frame that
may still sample it. Vertices and indices of every draw list are copied into per-frame-in-flight buffers that grow when needed, and the shader
pulls the vertices (`imgui.slang`, ImGui's 20-byte vertex) with push constants for the projection and the texture. Clip rectangles become scissors,
alpha blending is straight alpha, and the output image is the (UNORM) swapchain so the colours ImGui chose reach the screen unchanged.

## The overlay window

`DevOverlay.Draw` shows one window with four sections, and above them, in red, the **shader errors** (`DevOverlayData.ShaderErrors`, fill it with `ShaderLibrary.Errors`) while a shader that was edited does not
compile: the renderers keep using the shader as it was, and the message, with the file and line, stays until an edit that compiles:

- **Frame**: frames per second, average, minimum and maximum frame time, and an ImPlot plot of the last 240 frames, newest on the right (the history keeps filling while the
  window is hidden, `Visible = false`).
- **GPU passes**: a stacked ImPlot plot of the GPU milliseconds of each top-level render graph pass over the last 240 frames (`DevOverlay.GpuHistory`; a pass that did not run in a frame has 0 there,
  and a section inside a pass, `Forward/lights`, is part of it), and under it the table of every section of the last finished frame, with bars.
- **Render graph**: the passes that ran and the ones culled, their type, barriers and what they write, and how many images the graph shares.
- **Lighting**: draws, lights, whether the clusters were rebuilt; exposure, the view (final image or the cluster-light heatmap), the camera's field of
  view and the cluster range, all edited live.

`Ui` has the ImGui calls the dev tools need with C# strings and `ref` values (windows, text, sliders, checkboxes, combos, plots, tables, headers); for
anything else call `ImGui` from `Apogee.Native.CImGui` directly.

## ImPlot, ImGuizmo and imnodes

Three more libraries sit in the same shared library as Dear ImGui (they use its C++ API and its context, and ImGui's symbols are hidden), each with a small C API of ours over it (see `ThirdParty/README.md`, and
[the native libraries](../native/native-libraries.md#imguizmo-implot-and-imnodes)):

- **ImPlot** (`Apogee.Native.ImPlot`, class `Plot`) draws plots. `Plots` in `Apogee.DevTools` is the surface the overlay uses, with strings and spans: `if (Plots.Begin("id", size)) { Plots.Axes(...); Plots.Limits(...);
  Plots.Line("label", values, spec); Plots.Shaded(...); Plots.Band(...); Plots.Bars(...); Plots.End(); }`.
- **ImGuizmo** (`Apogee.Native.ImGuizmo`, class `Gizmo`) draws move, rotate and scale gizmos and a view cube over a viewport: `Gizmo.BeginFrame()`, `Gizmo.SetRect(...)`, then `Gizmo.Manipulate(view, projection, operation, mode,
  matrix, ...)` changes the matrix in place while the player drags (the matrices are 16 floats, column-major).
- **imnodes** (`Apogee.Native.ImNodes`, class `Nodes`) draws node editors: nodes with title bars and pins, links, a minimap, selection and positions.

`ImGuiHost` makes an ImPlot context and an imnodes context with the ImGui one and makes them current at the start of every frame; they are destroyed with it.

## Dear ImGui for Luau scripts

`ImGuiLibrary` (`[ScriptLibrary("imgui")]`) is the global table `imgui`, written with the same source generator as everything else, so its declarations in the
`.d.luau` definitions cannot drift from the code. Functions are snake_case; a widget returns `changed, value`; a window, tree, table, menu, tab or popup that can be open
returns whether it is, and every `begin_…` has its `end_…` (a script that forgets one, or fails between them, is put right at the end of the frame by ImGui's error
recovery). Colors are tables of 3 or 4 numbers from 0 to 1, a combo's index counts from 1, flags and conditions are the names of ImGui's enums
(`{ "no_title_bar", "no_resize" }`, `"always"`), and ids are the labels, with ImGui's `##` and `###`.

```lua
devtools.on_gui(function()                                 -- called every frame, inside the ImGui frame
    if imgui.begin_window("Spawner", nil, { "always_auto_resize" }) then
        imgui.text("entities: " .. world:count({ Position }))
        local changed, rate = imgui.slider_float("Rate", rate, 0, 10)
        if imgui.button("Spawn") then spawn() end
        devtools.watch("rate", rate)                        -- shown in the engine's debug window
    end
    imgui.end_window()
end)
```

With **no frame open** (a headless run, a shipped game that has no overlay) every `imgui` call does nothing and returns what it was given, so tool code can stay in the game.
`devtools.frame_open()` says which it is. `devtools.on_gui(fn)` registers a drawing function and returns an id for `devtools.remove_gui(id)`; the host calls `ScriptGui.RunGui()` once a
frame between `ImGuiHost.BeginFrame` and `EndFrame`, and a function that raises is logged and removed. `devtools.watch(name, value)` adds a row to the debug window.
The runtime's `--run` does this every frame, headless. The surface is: windows, text, widgets, layout, ids, trees, tables, menus, tabs, popups, tooltips, style,
`show_demo_window`); ImPlot, ImGuizmo and imnodes are not in the Luau library yet: they are for the dev overlay and the editor, in C#.

## Tests

The Luau library runs against real ImGui frames with no window and no GPU: widgets return what they were given, draw data is produced, a mouse click on a button reaches the script, a
script that forgets an end or fails in the middle does not break later frames, gui functions run and are removed when they fail, and the idle behavior.

`ImGuiExtensionTests` calls each of the three libraries in a headless frame (a quiet gizmo changes nothing and draws, a plot draws its four kinds of item in float and double, a node editor keeps the
positions it was given and draws two nodes and a link) and reads how many vertices the frame produced. The overlay's GPU history (a line per top-level pass, zero where it did not run, the last 240 frames) is tested
without a window.

The input translation runs on SDL's dummy video driver with synthetic events: the key table, buttons, the wheel, text, window filtering, display
size and who wants the mouse. The renderer tests draw to an offscreen target and check where pixels land (a window and nothing outside it, glyphs
from the atlas, clipping at the corners), that the atlas's bindless slot is released with the renderer, that the buffers grow, and that the
overlay is the last pass of a real `SceneRenderer` frame. ImGui keeps a process-wide current context, so these tests run one at a time.
