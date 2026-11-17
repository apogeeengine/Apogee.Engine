# The editor

!!! warning "Very bare-bones for now"
    The editor is very bare-bones for now: it cannot import 3D models or other assets (it only places and re-imports files already in the game folder), and it has no materials, no animations and no Export dialog (a game is [exported from the command line](export.md)). See [What is not here](#what-is-not-here).

`Source/Apogee.Editor` is an ImGui editor for a game folder. It is a CoreCLR program (a tool, never published with NativeAOT) built on [Dear ImGui with docking](devtools.md), [ImGuizmo, ImPlot and imnodes](devtools.md#implot-imguizmo-and-imnodes),
[`Apogee.Host`](../scripting/runtime.md#apogeehost-what-runs-a-game) and the [scene format](../core/ecs.md#editing-hierarchy-copies-snapshots-and-scenes).

```sh
dotnet run --project Source/Apogee.Editor -- <game folder> [--user <folder>]
```

The layout (a dock space: hierarchy on the left, inspector on the right, the viewport in the middle, tabs of assets, console, profiler and render graph below) is saved to `imgui.ini` in
`~/.local/share/Apogee/Editor` (the platform's local application data folder); the first run, with no file, builds the default one. Quitting with unsaved changes asks first.

## The panels

- **Viewport.** The world drawn into an image that ImGui shows as a texture (`RenderTarget.Texture`; one image per frame in flight, so the GPU never samples what the frame is writing). The editor's camera is not an
  entity: right mouse button looks around and, with W A S D Q E (Shift is faster), flies; middle button pans; the wheel zooms; F frames the active entity. A click that does not drag picks the entity under the pointer:
  a ray against the models' bounds (measured from the file, so no GPU is needed), and a small sphere for entities that have no model (lights, cameras, empty entities). Ctrl-click toggles the entity in the
  [selection](#selection), Shift-click adds it, and a click on nothing clears the selection (unless Ctrl or Shift is held). **Dragging from empty space** (not on an entity, not on the gizmo) draws a selection box;
  letting go selects every entity whose bounds touch it (its model's bounds, projected; an entity without a model counts by its origin) and that is in front of the camera: with Ctrl the box toggles them, with Shift
  it adds them, otherwise it replaces the selection. Escape clears the selection (while a box is being dragged it cancels the box instead). **ImGuizmo** moves, rotates and scales the whole selection
  (W, E and R choose; local or world axes; optional snapping; the *Pivot* box chooses where a group turns, see [Group transforms](#group-transforms)); a drag is one undo step. The view cube in the corner turns the camera. The helpers drawn as debug lines (a ground grid, the bounds of every selected entity,
  the active one brighter, a mark for each light and camera) can be turned off. Dropping a `.glb` or `.gltf` from the assets panel makes an entity at the point of the ground under the pointer.
- **Hierarchy.** The scene as a tree: click to select, **Ctrl-click to toggle, Shift-click to select a range** (in the order the tree shows, so entities inside a collapsed branch are skipped), **drag on the empty space to
  select with a box**, Ctrl+A for all and Escape for none. Double click or F2 renames the active entity, Ctrl+D duplicates it, **Delete removes every selected entity as one undo step** ("Delete 3 entities"), right click
  opens the menu, and **dragging a node drags the whole selection** (or only that node when it is not selected) **onto another to make them children** (onto the empty space below to make them roots), as one step.
  An entity keeps its place in the world when it changes parent, and a name a sibling already has is made unique, also between the entities that move together.
- **Inspector.** The components of the active entity (the last one picked; editing several at once comes later), drawn from the registry's descriptions: a checkbox, drag field, slider (when `[Property]` gives a `Min` and a `Max`), three drag fields for a vector or a double
  position, three angles for a rotation, a drop target for an entity reference, and for `ModelRenderer.Model` the model's path (type one, or drop a model file on it). Fields are grouped under their `Category`, `Hidden`
  ones are left out and `ReadOnly` ones greyed; right click a component to remove it; *Add component* lists every registered component. See [what an inspector shows](../core/reflection.md#what-an-inspector-shows).
- **Assets.** The virtual file system: `res://` and, when listed, the packages' `mod://id/` folders; each importable file shows whether it is imported, out of date, or not imported yet (`AssetImporter.StatusOf`),
  and *Re-import* imports one file again (`Reimport`). Double click a `.ascene` to open it; drag a model into the viewport or the inspector.
- **Console.** The log, filtered by level.
- **Profiler.** The dev overlay's ImPlot plots: frame times, and the GPU time of every pass stacked over the last 240 frames, with the table.
- **Render graph.** The last viewport frame's render graph as a read-only imnodes view: a node for each pass that ran, its inputs and outputs, and a link from the pass that last wrote a resource to the passes that read it.

## Selection

The selection is a list of entities in the order they were picked, and one of them is the **active** entity: the last one picked, the one the inspector shows and the gizmo sits on (when the active one is deleted,
the last of the others takes over). Several entities can be selected in the hierarchy and in the viewport (see the panels above). What a delete, a drag in the hierarchy, a group transform (and, later, a copy) acts
on is the selection's **top level**: the selected entities that have no selected ancestor, because a branch is carried by its top and its selected descendants would be handled twice.

The selection is not an undo step. It is kept as entity ids: an entity that was destroyed drops out of it, and comes back into it when an undo brings the entity back under the same id; creating an entity selects it,
deleting entities takes them out of the selection (undoing the delete does not select them again), and the selection survives Play and Stop like the history does. While the game plays, selecting works and every edit
stays disabled. The code is `Source/Apogee.Editor/Editing/Selection.cs` (plain classes over a world, with no ImGui, so it is tested without a window); the hierarchy panel only translates the requests of ImGui's
multi-select (a click, a range, the box, Escape) into `SelectionRequest`s, which `SelectionRequests.Apply` applies against the list of visible nodes.

## Group transforms

The gizmo acts on the selection's top level (the selected entities that have a `Transform` and no selected ancestor: a selected child rides on its selected parent and is never moved twice). A drag is one undo step
("Move 3 entities", "Rotate Cube"), however many frames it takes. **Pivot** in the viewport's toolbar chooses what a rotation or a scale turns around:

- **Center**: the centre of the selection. It is the middle of the box that contains every member's model bounds when all of them have a model; when one does not (a light, a camera, an empty entity) it is the mean of
  the origins. One selected entity always uses its own origin.
- **Active**: the origin of the active entity (the last one picked).
- **Individual**: the gizmo sits at the centre, but every entity turns and scales about its own origin, so a group of crates each spin in place. A move carries every entity by the same amount in all three modes.

The gizmo's axes are the world's, or, with *World* off (and always when scaling), those of the active entity. Snapping applies to the gizmo, that is to the pivot, so the group moves by whole steps and the
members keep their offsets from each other.

What happens to a member, in world space: its offset from the pivot is stretched by the scale ratio (along the gizmo's axes) and then swung by the rotation of the drag, the pivot's translation is added, and its
own rotation is turned by the same rotation. The result is taken into its parent's space, so a member under a rotated or scaled parent still moves the way the gizmo does. Positions are `double` and only
differences (a delta, an offset) are ever floats, so a group ten million metres from the origin still moves to the millimetre; the gizmo itself works in floats relative to the camera, as the rest of the viewport does.

**Scaling a group is exact only in some cases.** A transform here has a per-axis scale and no shear. Stretching a member along axes that are not its own would shear it, which cannot be stored. Each of a member's axes
takes the length ratio that the stretch gives its direction, so a member whose axes line up with the gizmo's (or are turned a quarter turn) gets exactly the ratio you dragged, but a member turned 45 degrees under a
non-uniform stretch is only approximated (its axes stay perpendicular and only their lengths follow the stretch). A uniform scale is always exact. Scaling about *Individual* or *Active* has the same rule. The maths is in
`Source/Apogee.Editor/Editing/GroupTransform.cs` and `TransformGesture.cs` (plain functions over placements, with no ImGui, tested without a window); the undo step is `SetTransformsCommand` in `Apogee.Host`.

## Scenes and undo

*File* creates, opens and saves scenes (`.ascene` XML, see [scenes](../content/scenes.md)); *Save as* takes a virtual path (`res://scenes/level`), and the scenes of the game are listed by *Open*. Every edit is a command on an `EditHistory`
(Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z): component values (a drag is one step), adding and removing components, creating, duplicating and deleting entities (a deleted branch comes back under the same ids; deleting or
moving several at once is one step), renaming and reparenting. Opening or creating a scene clears the history.

The history remembers the state the scene was saved in (its **save point**): the scene is *modified* (the `*` after its name, and the question when quitting) only when it differs from the file. Undo back to the saved
state and the mark goes away; undo past it, or make a new edit after undoing from it, and it comes back. A drag that is still going (its steps merge) is not the saved state either. With more than 500 steps (the
history's `Limit`) the oldest are forgotten, and if the saved state was among them the scene stays modified until it is saved again.

## Play

**Play** (F5) runs the game inside the editor. The editor keeps a snapshot of the world, replaces its session with the game's (the same `ScriptSession` the runtime makes: packages, mods and scripts, physics, audio,
the UI) over a copy of the scene, loaded before the scripts run, and shows the game in the viewport through the world's main camera (or the editor's camera when it has none, or when *Game camera* is off). The game gets
the pointer and the input actions while the viewport is hovered or focused; keys and typed text also go to the game's own UI (its text fields) while the viewport has the keyboard; the pointer reaches it through the panel, in the panel's coordinates. *Pause* and *Step* freeze and advance the world. **Stop** makes an editing
session again and puts the world back as it was *under the same entity ids*, so the selection and the undo history still point at the same entities. A game whose scripts fail does not start: the editor says why and
stays in editing. Only one session exists at a time (the UI library and others hold process-wide state), which is why Play and Stop replace it. *Debug* toggles the physics shapes and RmlUi's debugger while playing.

Editing is off while playing: the panels show the game's world, read-only.

## What is not here

Importing 3D models and other assets from inside the editor (new files go through `Apogee.Runtime --import`, see [Asset import](../content/import.md)), materials, animations, an Export dialog (a game is exported with [`./build.sh export`](export.md)), editing several selected entities at once in the inspector (it shows the active one), a scene file dialog beyond a list, material and logic graph editors (imnodes is ready for them), multi-viewport windows, and a physics ray cast for picking (an editing session has no physics bodies).
Sound plays in play mode through OpenAL Soft ([Audio](../audio/index.md)), which ships beside the editor like any other library.

## Tests

`Tests/Apogee.Editor.Tests`: the field codec for every `FieldKind` (a rotation as angles, integers clamped), the editor camera, picking (rays, boxes, spheres, the ground, and the selection box with what is behind the
camera left out), the selection model (toggle, ranges in the visible order, top level, dead entities, the active entity, the multi-select requests), the group transforms (the pivots, moving at ten million metres, rotating and scaling about the centre or in place, local axes, a member under a turned parent, a selected child not moved twice, the scale caveat), the project (creating, naming, reparenting,
duplicating and deleting, one or several, through the history; the modified mark clearing when undo returns to the saved scene; a scene saved to a file and opened again with the model's path; Play running the game's
scripts over the scene and Stop restoring the world under the same ids with the selection and history intact; a failing game), and, on a GPU (skipped without one), a frame of the viewport with no validation errors whose render graph lays out as nodes. `Tests/Apogee.Host.Tests` covers the history and
its commands (a drag over several entities is one step).
