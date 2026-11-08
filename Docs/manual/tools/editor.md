# The editor

!!! warning "Very bare-bones for now"
    The editor is very bare-bones for now: it cannot import 3D models or other assets (it only places and re-imports files already in the game folder), and it has no materials, no animations and no game export. See [What is not here](#what-is-not-here).

`Source/Apogee.Editor` is an ImGui editor for a game folder. It is a CoreCLR program (a tool, never published with NativeAOT) built on [Dear ImGui with docking](devtools.md), [ImGuizmo, ImPlot and imnodes](devtools.md#implot-imguizmo-and-imnodes),
[`Apogee.Host`](../scripting/runtime.md#apogeehost-what-runs-a-game) and the [scene format](../core/ecs.md#editing-hierarchy-copies-snapshots-and-scenes).

```sh
dotnet run --project Source/Apogee.Editor -- <game folder> [--user <folder>]
```

The layout (a dock space: hierarchy on the left, inspector on the right, the viewport in the middle, tabs of assets, console, profiler and render graph below) is saved to `imgui.ini` in
`~/.local/share/Apogee/Editor` (the platform's local application data folder); the first run, with no file, builds the default one. Quitting with unsaved changes asks first.

## The panels

- **Viewport.** The world drawn into an image that ImGui shows as a texture (`RenderTarget.Texture`; one image per frame in flight, so the GPU never samples what the frame is writing). The editor's camera is not an
  entity: right mouse button looks around and, with W A S D Q E (Shift is faster), flies; middle button pans; the wheel zooms; F frames the selection. A click that does not drag picks the entity under the pointer:
  a ray against the models' bounds (measured from the file, so no GPU is needed), and a small sphere for entities that have no model (lights, cameras, empty entities). **ImGuizmo** moves, rotates and scales the selection
  (W, E and R choose; local or world axes; optional snapping); a drag is one undo step. The view cube in the corner turns the camera. The helpers drawn as debug lines (a ground grid, the selection's bounds, a mark for
  each light and camera) can be turned off. Dropping a `.glb` or `.gltf` from the assets panel makes an entity at the point of the ground under the pointer.
- **Hierarchy.** The scene as a tree: click to select, double click or F2 to rename, Delete, Ctrl+D to duplicate, right click for the menu, and **drag an entity onto another to make it a child** (onto the empty space
  below to make it a root). The entity keeps its place in the world when it changes parent, and a name a sibling already has is made unique.
- **Inspector.** The components of the selected entity, drawn from the registry's descriptions: a checkbox, drag field, slider (when `[Property]` gives a `Min` and a `Max`), three drag fields for a vector or a double
  position, three angles for a rotation, a drop target for an entity reference, and for `ModelRenderer.Model` the model's path (type one, or drop a model file on it). Fields are grouped under their `Category`, `Hidden`
  ones are left out and `ReadOnly` ones greyed; right click a component to remove it; *Add component* lists every registered component. See [what an inspector shows](../core/reflection.md#what-an-inspector-shows).
- **Assets.** The virtual file system: `res://` and, when listed, the packages' `mod://id/` folders; each importable file shows whether it is imported, out of date, or not imported yet (`AssetImporter.StatusOf`),
  and *Re-import* imports one file again (`Reimport`). Double click a `.ascene` to open it; drag a model into the viewport or the inspector.
- **Console.** The log, filtered by level.
- **Profiler.** The dev overlay's ImPlot plots: frame times, and the GPU time of every pass stacked over the last 240 frames, with the table.
- **Render graph.** The last viewport frame's render graph as a read-only imnodes view: a node for each pass that ran, its inputs and outputs, and a link from the pass that last wrote a resource to the passes that read it.

## Scenes and undo

*File* creates, opens and saves scenes (`.ascene` text, see [ecs](../core/ecs.md)); *Save as* takes a virtual path (`res://scenes/level`), and the scenes of the game are listed by *Open*. Every edit is a command on an `EditHistory`
(Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z): component values (a drag is one step), adding and removing components, creating, duplicating and deleting entities (a deleted branch comes back under the same ids), renaming and
reparenting. Opening or creating a scene clears the history.

## Play

**Play** (F5) runs the game inside the editor. The editor keeps a snapshot of the world, replaces its session with the game's (the same `ScriptSession` the runtime makes: packages, mods and scripts, physics, audio,
the UI) over a copy of the scene, loaded before the scripts run, and shows the game in the viewport through the world's main camera (or the editor's camera when it has none, or when *Game camera* is off). The game gets
the pointer and the input actions while the viewport is hovered or focused; keys and typed text also go to the game's own UI (its text fields) while the viewport has the keyboard; the pointer reaches it through the panel, in the panel's coordinates. *Pause* and *Step* freeze and advance the world. **Stop** makes an editing
session again and puts the world back as it was *under the same entity ids*, so the selection and the undo history still point at the same entities. A game whose scripts fail does not start: the editor says why and
stays in editing. Only one session exists at a time (the UI library and others hold process-wide state), which is why Play and Stop replace it. *Debug* toggles the physics shapes and RmlUi's debugger while playing.

Editing is off while playing: the panels show the game's world, read-only.

## What is not here

Importing 3D models and other assets from inside the editor (new files go through `Apogee.Runtime --import`, see [Asset import](../content/import.md)), materials, animations, exporting a game, multiple selection, a scene file dialog beyond a list, material and logic graph editors (imnodes is ready for them), multi-viewport windows, and a physics ray cast for picking (an editing session has no physics bodies).
Sound plays in play mode through OpenAL Soft ([Audio](../audio/index.md)), which ships beside the editor like any other library.

## Tests

`Tests/Apogee.Editor.Tests`: the field codec for every `FieldKind` (a rotation as angles, integers clamped), the editor camera, picking (rays, boxes, spheres, the ground), the project (creating, naming, reparenting,
duplicating and deleting through the history; a scene saved to a file and opened again with the model's path; Play running the game's scripts over the scene and Stop restoring the world under the same ids with the
selection and history intact; a failing game), and, on a GPU (skipped without one), a frame of the viewport with no validation errors whose render graph lays out as nodes. `Tests/Apogee.Host.Tests` covers the history and
its commands.
