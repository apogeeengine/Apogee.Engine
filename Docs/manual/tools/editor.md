# The editor

!!! warning "Very bare-bones for now"
    The editor is very bare-bones for now: files are brought in by [dragging them from the system](#bringing-files-in) (there is no import dialog or import settings editor), and it has no materials, no animations and no Export dialog (a game is [exported from the command line](export.md)). See [What is not here](#what-is-not-here).

`Source/Weft.Editor` is an ImGui editor for a game folder. It is a CoreCLR program (a tool, never published with NativeAOT) built on [Dear ImGui with docking](devtools.md), [ImGuizmo, ImPlot and imnodes](devtools.md#implot-imguizmo-and-imnodes),
[`Weft.Host`](../scripting/runtime.md#wefthost-what-runs-a-game) and the [scene format](../core/ecs.md#editing-hierarchy-copies-snapshots-and-scenes).

```sh
dotnet run --project Source/Weft.Editor -- [game folder] [--user <folder>]
```

With no game folder the editor shows its [start screen](#the-start-screen-and-opening-a-project).

The layout (a dock space: hierarchy on the left, inspector on the right, the viewport in the middle, tabs of assets, console, profiler and render graph below) is saved to `imgui.ini` in
`~/.local/share/Weft/Editor` (the platform's local application data folder); the first run, with no file, builds the default one. Quitting with unsaved changes asks first.

## The panels

- **Viewport.** The world drawn into an image that ImGui shows as a texture (`RenderTarget.Texture`; one image per frame in flight, so the GPU never samples what the frame is writing). The editor's camera is not an
  entity: right mouse button looks around and, with W A S D Q E (Shift is faster), flies; middle button pans; the wheel zooms; F frames the active entity. A click that does not drag picks the entity under the pointer:
  a ray against the models' bounds (measured from the file, so no GPU is needed), and a small sphere for entities that have no model (lights, cameras, empty entities). Ctrl-click toggles the entity in the
  [selection](#selection), Shift-click adds it, and a click on nothing clears the selection (unless Ctrl or Shift is held). **Dragging from empty space** (not on an entity, not on the gizmo) draws a selection box;
  letting go selects every entity whose bounds touch it (its model's bounds, projected; an entity without a model counts by its origin) and that is in front of the camera: with Ctrl the box toggles them, with Shift
  it adds them, otherwise it replaces the selection. Escape clears the selection (while a box is being dragged it cancels the box instead). **ImGuizmo** moves, rotates and scales the whole selection
  (W, E and R choose; local or world axes; optional snapping; the *Pivot* box chooses where a group turns, see [Group transforms](#group-transforms)); a drag is one undo step. The view cube in the corner turns the camera. The helpers drawn as debug lines (a ground grid, the bounds of every selected entity,
  the active one brighter, a mark for each light and camera) can be turned off. Dropping a `.glb` or `.gltf` from the assets panel makes an entity at the point of the ground under the pointer; dropping model files **from the system** copies them into the game, imports them and does the same (see [Bringing files in](#bringing-files-in)).
- **Hierarchy.** The scene as a tree: click to select, **Ctrl-click to toggle, Shift-click to select a range** (in the order the tree shows, so entities inside a collapsed branch are skipped), **drag on the empty space to
  select with a box**, Ctrl+A for all and Escape for none. Double click or F2 renames the active entity, Ctrl+X, Ctrl+C, Ctrl+V and Ctrl+D cut, copy, paste and duplicate the selection (see [Copy and paste](#copy-and-paste)), **Delete removes every selected entity as one undo step** ("Delete 3 entities"), right click
  opens the menu, and **dragging a node drags the whole selection** (or only that node when it is not selected) **onto another to make them children** (onto the empty space below to make them roots), as one step.
  An entity keeps its place in the world when it changes parent, and a name a sibling already has is made unique, also between the entities that move together.
- **Inspector.** The components of the selected entities, drawn from the registry's descriptions: see [The inspector](#the-inspector). With several entities selected it shows the active entity's components
  that all of them have, and an edit changes every one.
- **Assets.** The virtual file system: `res://` and, when listed, the packages' `mod://id/` folders; each importable file shows whether it is imported, out of date, or not imported yet (`AssetImporter.StatusOf`),
  and *Re-import* imports one file again (`Reimport`). Double click a `.wscn` to open it; drag a model into the viewport, or any file onto an asset field of the inspector (see [Asset fields](#asset-fields)); drop files from the system on the panel to add them to the folder it shows (see [Bringing files in](#bringing-files-in)).
- **Console.** The log, filtered by level; while files are being imported its header shows the progress and a *Cancel import* button.
- **Profiler.** The dev overlay's ImPlot plots: frame times, and the GPU time of every pass stacked over the last 240 frames, with the table.
- **Engine objects** (*Debug > Engine objects*). A floating window that inspects the session's engine objects (the world, the UI, physics, audio, assets, models, input actions, network, Steam and the settings) through their registered properties: see [Engine objects](#engine-objects).
- **Render graph.** The last viewport frame's render graph as a read-only imnodes view: a node for each pass that ran, its inputs and outputs, and a link from the pass that last wrote a resource to the passes that read it.

## Selection

The selection is a list of entities in the order they were picked, and one of them is the **active** entity: the last one picked, the one the inspector shows and the gizmo sits on (when the active one is deleted,
the last of the others takes over). Several entities can be selected in the hierarchy and in the viewport (see the panels above). What a delete, a drag in the hierarchy, a group transform (and, later, a copy) acts
on (and a copy, a cut and a duplicate) is the selection's **top level**: the selected entities that have no selected ancestor, because a branch is carried by its top and its selected descendants would be handled twice.

The selection is not an undo step. It is kept as entity ids: an entity that was destroyed drops out of it, and comes back into it when an undo brings the entity back under the same id; creating an entity selects it,
deleting entities takes them out of the selection (undoing the delete does not select them again), and the selection survives Play and Stop like the history does. While the game plays, selecting works and every edit
stays disabled. The code is `Source/Weft.Editor/Editing/Selection.cs` (plain classes over a world, with no ImGui, so it is tested without a window); the hierarchy panel only translates the requests of ImGui's
multi-select (a click, a range, the box, Escape) into `SelectionRequest`s, which `SelectionRequests.Apply` applies against the list of visible nodes.

## The inspector

A field is drawn by a **drawer**, picked from a table in this order: a drawer registered for the whole component, one for the field, one for the field's asset type, then the defaults, which look at what the registry says about the field
(an enum, a colour) and then at its kind (an asset field with a type gets the picker whatever its type). The defaults are:

- **Numbers.** A checkbox, a drag field (a slider when `[Property]` gives a `Min` and a `Max`), three drag fields for a vector or a double position, three angles for a rotation. An asset handle with no asset type has no picker to name a file, so it is a plain number too.
- **Asset reference.** Every field that holds an asset (`[Property(Asset = "model")]`, as `ModelRenderer.Model` does) is edited by the file it names: see [Asset fields](#asset-fields).
- **Entity reference.** The entity's name on a button that takes a dropped entity, and *Clear*.
- **Enum.** A field the registry says holds an enum (`[Property(Enum = typeof(LightKind))]`, or a field of an enum type) is a combo of the enum's members; a flags enum is a node with a checkbox for each member that has a bit.
  A value that is no member shows as its number and stays until another is picked, and the bits of a flags value that no member names are kept when a checkbox changes.
- **Colour.** A `Vector3` marked `Color = true` is a colour picker (`ColorEdit3`, HDR and float): a light can be brighter than white, so values outside 0 to 1 are shown and kept.

Fields are grouped under their `Category`, `Hidden` ones are left out, `ReadOnly` ones greyed, and a field's `///` summary is its tooltip.

**Defaults.** *Add component* starts a component from `ComponentDefaults` (in `Weft.Host.Editing`): a table from the component's name to its bytes, seeded by `RegisterEngine()` with the identity `Transform`, `CameraComponent.Default` and a white
point `Light`. A component nobody registered, such as one a script defines, starts as zeros. Right click a component's heading and choose *Reset to default* to put the whole value back, or right click a field for *Reset to default* on that field alone.
Each is one undo step. A game or an editor extension adds its own with `ComponentDefaults.Register(name, bytes)`.

**Several entities.** With several selected, the inspector shows the active entity's components that **all** of them have, with the active entity's values. A field whose bytes differ among them shows ImGui's mixed value (`---`, a dash
in a checkbox, "---" in a combo). An edit writes the bytes of **that field only** on every selected entity, so each keeps its own values for the others, and one drag, pick or click is **one undo step** (a composite of one change for each
entity, which merges with the next change to the same entities until the widget is let go of). *Add component* adds a component to every selected entity that lacks it, *Remove component* removes it from all, and *Reset to default* resets all.
Changing the name still renames the active entity only.

### Asset fields

An asset field is a number in memory (a handle that only means something in this run) and a path with a UID in the scene (`res://models/car.glb|uid://…`, see [Asset identity](../content/assets.md)). The inspector shows the second:

- **The button** shows the file's name; hover it for the full path and the `uid://` (a file that has not been imported yet has none, and the tooltip says so: it gets one at its first import). An empty field says *None*, and several selected
  entities that differ say `---`. Click it to open the picker.
- **The picker** is a popup with a filter box (focused when it opens) and the game's files of that kind: a model field lists `.glb` and `.gltf`, a texture field images, a scene field `.wscn`. The filter matches the file name first and then the rest of the path, whatever the case.
  The list is drawn a screen at a time, so a project with fifty thousand files opens as fast as one with fifty. Files of `res://` come first, then those of each package (`mod://id/…`), each in path order. The list leaves out the import cache (`imported/`), names
  that start with a dot and `.wimp` files; a package that sits under `res://packages/` and is also mounted as `mod://id/` is listed once, as `mod://`. The listing is made on another thread (the popup says so for the very first one), is repeated at most every two
  seconds while a picker is in use and at once when the hot reloader reports changed files, and stops at 200,000 files, 100,000 folders or 24 levels (the popup says the list is cut short).
- **Drop a file** from the Assets panel on the button. A file of another kind (a texture on a model field) is refused with a message under the field.
- **Clear** (the small button) empties the field. **Show in assets** (right click the button) switches to the Assets panel, opens the file's folder and marks the file.

A choice is turned into a handle through the world's converter (`World.AssetHandleOf`), which loads the file when the type needs it, so the field holds what a scene would load; it is written like any edit, as **one undo step** for all the selected entities, and undo puts the old
handle back. A file that is picked and gives **no handle** (it vanished from the disk since the list was made, or does not load) is **not written** (0 would pass for *Clear*): the field stays as it was, and a red message under it says which file and why to look at the console.
A file that is **not imported yet** can be picked: models and images load from their sources until they are imported.

A **broken reference** is drawn in red. That is a field whose file is gone (the scene or a script named it, and nothing has that path or uid any more), a handle that names no file at all (made in code: a scene cannot save it), and a field that is 0 because the scene named a file that
did not load: the button then shows the text the scene held, which a save writes back as it was, so that the reference works again when the file is back. Choosing a file or *Clear* replaces that text (in the same undo step: undo brings it back), so clearing a broken field survives a save. A file that moved
is found through its UID and drawn normally; its tooltip says where it was and that saving writes the new path.

### Custom inspectors

An editor extension (C# only: the editor is the one place a drawer can live; games define components in scripts and get the defaults) registers drawers on `InspectorDrawers` before the inspector first draws:

```csharp
// One field: a drawer gets the field, its bytes (write the new value back into them and return true), whether the selected entities disagree, and the widgets.
InspectorDrawers.RegisterField("Health", "Current", (in FieldEdit edit) =>
{
    int current = MemoryMarshal.Read<int>(edit.Bytes);
    var value = new double[] { current };
    if (!edit.Ui.Drag(edit.Label, value, 1, 0, 100, "%.0f", edit.Mixed))
    {
        return false;
    }

    int next = (int)value[0];
    MemoryMarshal.Write(edit.Bytes, in next);
    return true;
});

InspectorDrawers.RegisterAssetType("texture", (in FieldEdit edit) => BuiltInDrawers.AssetReference(in edit));   // every AssetRef field of that asset type
InspectorDrawers.RegisterComponent("Health", (in ComponentEdit edit) => false);                          // the whole component, in place of its fields
```

`FieldEdit.Ui` is an `IInspectorUi`: the widgets (drag fields, checkbox, combo, colour, entity reference, asset reference) with the mixed flag, which `ImGuiInspectorUi` draws with ImGui and the tests answer with a fake, so a drawer is tested with no window. The built-in drawers
(`BuiltInDrawers.Numbers`, `EntityReference`, `AssetReference`, `Enum`, `Color`) are public, to call for part of a field. `FieldEdit.Entity` is the active entity, for a drawer that looks something up in the world. A package or an extension that adds a kind of asset registers it with `AssetKinds.Register("prefab", [".wprf"], "Prefab")` (in `Weft.Host.Editing`): its files are then listed
for every field of that asset type, which gets the picker unless a drawer is registered for it. A component drawer gets the whole value and a mixed flag for each field; only the fields whose bytes it changed are written to the selected entities.

A class of objects (see [Engine objects](#engine-objects)) has its own hook, `RegisterClass`. The drawer gets the object, its class, the widgets and the inspector that is drawing it; `edit.DrawProperties()` draws the default look, so a
drawer can add its own widgets before or after it, or draw something else altogether. A drawer registered for a class also draws its subclasses, unless they have one of their own:

```csharp
InspectorDrawers.RegisterClass("PhysicsWorld", (in ObjectEdit edit) =>
{
    edit.Ui.Text("Bodies in this world: see the Physics shapes in the Debug menu.");
    return edit.DrawProperties();                                              // the usual list of properties after it
});

InspectorDrawers.RegisterMultiline("Note", "text");                             // the string property `text` of the class `Note` gets a box of several lines
```

A write that a custom drawer makes goes through `edit.Inspector.SetProperty(target, property, before, after, path)`, which makes it one undo step of the window's history and reports a setter that refuses.

## Engine objects

*Debug > Engine objects* opens a window with a combo of the session's objects and, under it, the properties of the one that is chosen. It is built on the same registry as scripts: every `[Property]` of a `[WeftClass]` (the
base classes' first) is read and written through the property's own getter and setter, so nothing reflects, and what the class says about it is respected (`Min`, `Max`, `Step`, `Category`, `Hidden`, `ReadOnly`, `Color`).

- **What is drawn.** A bool is a check box; an integer or a number a drag field (a range gives it its limits); a string a text field (one line, or a box when the property was registered with `RegisterMultiline`, or the text already has
  several lines or is long; text beyond 16384 characters is shown cut and not edited here); a `Vector3` three numbers, or a colour picker with `Color`; a rotation three angles in degrees; an enum a combo of the registered members, or for a
  `[Flags]` enum a node of check boxes; an entity its name, a button that takes an entity dragged from the hierarchy, and *Clear*; an asset reference (`AssetRef`) is text in the form scripts use, `res://models/car.glb|uid://…`; a function or a table is only named.
- **Objects** are a node that opens to the properties of the object inside. It goes four levels down; below that the object is named and not opened. An object that is already open above it (a cycle) is named and
  not opened again, and one that has been destroyed says so.
- **Lists** show their count in the node and the items a page at a time (200 on a page, with *Previous page* and *Next page*). When the property can be set, each item has *Up*, *Down* and *Remove*, and *Add* appends the zero of
  the item type (an empty text, 0, the first member of an enum). A list of objects has no *Add*: objects are not made here. A list of more than 5000 items is shown and not edited.
- **Properties that cannot be edited** (no setter, or `ReadOnly`) are greyed out. A property the getter of which throws shows the message in its place and the rest is drawn. A **setter that refuses a value** (it throws, as a script would see)
  leaves the property as it was and the message appears under it, until the property is written successfully; nothing is added to the history. A value that is `nil` is said (`none`); a nullable number or text gets a *Set* button.
- **Live objects.** What the window shows is read every frame, so a value that the engine changes by itself keeps changing on screen without the window writing it back.

The edits are **live**: they go straight to the object, they are **not part of the scene** (nothing is saved, the scene is not marked modified, Ctrl+Z does not undo them), and they work while the game plays. The window has its own
*Undo* and *Redo* buttons for them: one step for each property (the frames of one drag are one step; so is one click on *Add*, *Remove* or *Up*). The window says all of this at its top. When the shown object is another one,
which is what Play and Stop do (they replace the session), its history is dropped: the objects it changed are gone. Which object is shown is remembered by name.

## Group transforms

The gizmo acts on the selection's top level (the selected entities that have a `Transform` and no selected ancestor: a selected child rides on its selected parent and is never moved twice). A drag is one undo step
("Move 3 entities", "Rotate Cube"), however many frames it takes. **Pivot** in the viewport's toolbar chooses what a rotation or a scale turns around:

- **Center**: the centre of the selection. It is the middle of the box that contains every member's model bounds when all of them have a model; when one does not (a light, a camera, an empty entity) it is the mean of
  the origins. Only a member's own model counts, not those of its descendants, so a group of empty parents is centred on the mean of their origins. One selected entity always uses its own origin.
- **Active**: the origin of the active entity (the last one picked); when the active entity is a child of a selected entity, the origin of that selected parent, which is the one that moves.
- **Individual**: the gizmo sits at the centre, but every entity turns and scales about its own origin, so a group of crates each spin in place (about the gizmo's axes: with *World* off those are the active entity's, not each crate's own). A move carries every entity by the same amount in all three modes.

The gizmo's axes are the world's, or, with *World* off (and always when scaling), those of the active entity. Snapping applies to the gizmo, that is to the pivot, so the group moves by whole steps and the
members keep their offsets from each other.

What happens to a member, in world space: its offset from the pivot is stretched by the scale ratio (along the gizmo's axes) and then swung by the rotation of the drag, the pivot's translation is added, and its
own rotation is turned by the same rotation. The result is taken into its parent's space, so a member under a rotated or scaled parent still moves the way the gizmo does. Positions are `double` and only
differences (a delta, an offset) are ever floats. A move adds the gizmo's float difference to the double position, so it is exact in double for entities at the root of the scene, however far out (ten million metres
from the origin still moves to the millimetre), and a move never turns or scales anything; a rotation or a scale reads only the rotation or the scale back from the gizmo. Offsets between members are floats, so a rotation
or scale about a pivot a kilometre away is good to about a tenth of a millimetre. An entity under a parent has a *local* position that the engine stores relative to the parent and handles as a float: far from its
parent's origin (everything under a root at the origin while the group is ten million metres out) it is only as exact as that float, which is how every transform in the engine works. A scale ratio below 0.001 is
raised to 0.001 (the gizmo never goes lower), so a group is flattened but never mirrored or collapsed to zero. A drag that is cut short (Play, a failed Play, Escape or a click that changes the selection) ends where it
is as its own undo step, and a new drag cannot begin until the mouse button has been let go.

**Scaling a group is exact only in some cases.** A transform here has a per-axis scale and no shear. Stretching a member along axes that are not its own would shear it, which cannot be stored. Each of a member's axes
takes the length ratio that the stretch gives its direction, so a member whose axes line up with the gizmo's (or are turned a quarter turn) gets exactly the ratio you dragged, but a member turned 45 degrees under a
non-uniform stretch is only approximated (its axes stay perpendicular and only their lengths follow the stretch). A uniform scale is always exact. Scaling about *Individual* or *Active* has the same rule. The maths is in
`Source/Weft.Editor/Editing/GroupTransform.cs`, `TransformGesture.cs` and `GizmoDrag.cs` (plain functions over placements, with no ImGui, tested without a window); the undo step is `SetTransformsCommand` in `Weft.Host`.

## Copy and paste

**Ctrl+C** copies the selection (its top level, each entity with everything below it) to the system clipboard as **scene text**: the same XML a scene file holds ([scenes](../content/scenes.md)), written by
`World.SaveSceneDocument`, with no `uid`, because a copy is not a file. Several selected entities are one document. **Ctrl+V** reads the text back with `World.LoadScene`, so it works between scenes, between projects
and between editor instances, and the text can be edited or written by hand. **Ctrl+X** is a copy followed by a delete (one step). **Ctrl+D** duplicates without touching the clipboard. The same commands are in
the *Edit* menu and the hierarchy's context menu, which also has *Paste as child* (Ctrl+Shift+V).

- **Where it goes.** A paste lands next to the active entity (under the same parent; at the root when nothing is selected); *Paste as child* puts it under the active entity. The new entities are selected, and the paste
  is one undo step ("Paste Cube", "Paste 3 entities"); undoing removes them, redoing brings them back under the same ids. A pasted entity keeps its local transform, so under another parent it moves with that parent.
- **Names.** The pasted top-level entities get names no sibling has ("Cube", then "Cube 2"), also among themselves when they come from different parents; the loader decides, among the real siblings, so a pasted "Empty" stays "Empty" when no sibling has that name. Names below them are kept.
- **References.** An `Entity` field that points to an entity inside the copied set is written as that entity's number in the document, and a paste points it at the new copy, so a copied branch stays wired to itself
  (and several selected entities to each other). A field that points outside the copied set is left empty: the scene format cannot name an entity that is not in the document. Asset fields travel as
  `path|uid` (the model's path, and its UID once it is imported) and are found again by the pasting project; one that cannot be found is left empty and logged. Components that a script defined travel in the
  document's `<Components>` header. A prefab instance is copied as one element that names its prefab and what it changes, and pasted as an instance (a [selected member](../content/prefabs.md#how-overrides-are-stored) of one, without its root, as plain entities).
- **Duplicate** is not a paste: it clones each top-level entity with `World.Clone` under the same parent ("Cube (copy)") as one step, and a clone keeps the references of its components as they are, so one that
  pointed at another entity still points at the original (including inside the duplicated branch).
- **Playing.** Copy works while the game plays (it copies the running state); Cut, Paste and Duplicate are off, like every edit.
- **Bad text.** Whatever is on the clipboard is untrusted. Text that is not a scene (not XML, another root element, a DTD, nesting that is too deep, more than 16 MB), a scene of a newer version, or one with no
  entities changes nothing: the console says why. *Paste* is greyed in the menus when the text cannot be a scene.

The code is `Source/Weft.Editor/Editing/ClipboardScene.cs` (the `IClipboard` interface, `SdlClipboard` over SDL's clipboard, `MemoryClipboard` for tests, and the checks of the text), the project's `Copy`, `Cut`,
`Paste` and `Duplicate`, and `CreateEntitiesCommand` in `Weft.Host` (the undo step that makes several branches).

## Shortcuts

Every shortcut is a row of one table, the keymap (`Source/Weft.Editor/Input/Keymap.cs`): an action id (`edit.undo`), the chord or chords that fire it, and the **context** it applies in. The menus show the
shortcut text from the same table, so a label never disagrees with the key. On macOS the primary modifier is Cmd, shown as `Cmd+Z`; everywhere else it is Ctrl.

<!-- keymap:begin (checked against Keymap.Defaults by KeymapTests) -->
| Action | Shortcut | Where |
| --- | --- | --- |
| Undo | Ctrl+Z | Anywhere |
| Redo | Ctrl+Y or Ctrl+Shift+Z | Anywhere |
| Save | Ctrl+S | Anywhere |
| Save as | Ctrl+Shift+S | Anywhere |
| Play or stop | F5 | Anywhere |
| Cut | Ctrl+X | Anywhere |
| Copy | Ctrl+C | Anywhere |
| Paste | Ctrl+V | Anywhere |
| Paste as child | Ctrl+Shift+V | Anywhere |
| Duplicate | Ctrl+D | Anywhere |
| Delete | Delete | Hierarchy focused |
| Rename | F2 | Hierarchy focused |
| Clear the selection | Escape | Viewport focused |
| Frame the selection | F | Viewport focused |
| Move tool | W | Viewport focused |
| Rotate tool | E | Viewport focused |
| Scale tool | R | Viewport focused |
| Fly forward | W | Viewport, right mouse button held |
| Fly back | S | Viewport, right mouse button held |
| Fly left | A | Viewport, right mouse button held |
| Fly right | D | Viewport, right mouse button held |
| Fly up | E | Viewport, right mouse button held |
| Fly down | Q | Viewport, right mouse button held |
<!-- keymap:end -->

- **Contexts.** *Anywhere* shortcuts work whichever panel has the keyboard; the others only while that panel is focused. While the right mouse button is held in the viewport the camera flies: the
  fly keys are held rather than pressed (extra modifiers, such as Shift to go faster, do not stop them), and the viewport's own keys (W, E, R, F, Escape) are off so that W flies instead of choosing the Move tool.
- **Exact modifiers.** A chord fires only with exactly its modifiers: Ctrl+Shift+V pastes as a child and does not also paste, Shift+Z is not Z.
- **Typing.** While a text field (or any widget that uses the keyboard) is active, no shortcut fires, so typing a name never deletes entities or switches the tool.
- **Conflicts.** `Keymap.Conflicts()` reports two actions that share a chord where both could be active: in the same context, or when one of them is *Anywhere*. The defaults have none (a test checks it); a
  rebinding that collides is reported, not refused. [Preferences](#preferences) rebinds, saves and resets the table.
- **ImGui's own keys.** Ctrl+A (select all), Shift-click ranges, Ctrl-click and Escape in the hierarchy are ImGui's multi-select, not keymap rows.

The matching is `Keymap.Pressed` and `Keymap.IsHeld` over `IKeyState` (ImGui's keyboard in the editor, a fake in the tests), so it is tested without a window. The manual's table above is checked against the real one by a test.

## Preferences

The editor remembers its preferences, the recent projects and the key bindings in **`preferences.xml` in the editor's own folder** (the platform's local application data folder: `~/.local/share/Weft/Editor` on Linux and macOS, `%LOCALAPPDATA%\Weft\Editor` on Windows; next to `imgui.ini` and the logs). It is never written into a game folder, and it is not the game's `user://settings.xml`: that file belongs to one game and its
mods, is replaced when Play starts, and every mod can read it. The file is the engine's [settings store](../platform/settings.md) over a file system of the editor's folder alone, so it has the same shape (`<settings version="1">` with
`<value key="...">` lines), only what differs from the default is written, writes are atomic (a temporary file moved over the old one), and a key this editor does not know is kept. The dock layout stays in `imgui.ini`.

```xml
<settings version="1">
  <value key="editor.camera.speed">12</value>
  <value key="keys.edit.undo">Ctrl+Alt+U</value>
  <value key="recent.0">/home/me/games/farm</value>
  <value key="project.5b0c7e1d2a9f4c38.wscn">res://scenes/barn.wscn</value>
</settings>
```

| Keys | Holds |
| --- | --- |
| `editor.camera.speed` | the fly speed of the camera in metres per second (6; Shift is three times as fast) |
| `editor.gizmo.mode`, `.world`, `.pivot`, `.snap` | the tool (`move`, `rotate`, `scale`), world or local space, the pivot of a group (`center`, `active`, `individual`) and whether it snaps |
| `editor.gizmo.snap_move`, `.snap_rotate`, `.snap_scale` | the snap steps (0.5 m, 15 degrees, 0.1) |
| `editor.helpers`, `editor.game_camera` | the viewport's helpers, and the game camera while playing |
| `editor.ui.scale` | the interface scale (1; 0.5 to 3) |
| `recent.0` to `recent.9` | the recent projects as full paths, the latest first |
| `project.<16 hex digits>.wscn` | the scene that was open last in a project (the first 16 hex digits of the SHA-256 of the project's full path), reopened the next time the project opens if it is still there |
| `keys.<action id>` | a key binding that differs from the default |

The gizmo's state is kept as you work (the toolbar and the window edit the same values), the scene is noted when it changes, and the file is written within a second of a change and when the editor quits (a slider that is
dragged writes once a second, not once a frame).

**Edit > Preferences...** (also on the start screen) opens a window with two tabs. *General* edits the values above (*Reset to defaults* puts them back; it does not touch the recent projects or the keys). *Keys* lists every
action with its shortcut and where it applies. *Rebind* waits for the next key you press and binds the action to it with the modifiers held (Ctrl+Shift+K); Escape alone gives up (to bind Escape itself, hold a modifier,
or use *Reset* on an action whose default it is); a modifier alone is not a chord. *Clear* unbinds the action, *Reset* puts its defaults back (it is greyed when it is on them), *Reset all* does it for every action. While a key is
awaited no shortcut fires, so the key you choose does not also do what it is bound to. A chord that another action already has (where both could be active) is **accepted and shown in red** with the name of the other
action: both fire, and the window says so. The menus and the toolbar show the new chords at once.

**Key bindings are a difference from the defaults.** An action on its default chords has no key in the file, so a later editor with another default reaches everyone who never changed it. `keys.edit.redo` holds the chords
joined with ` | ` (`Ctrl+Y | Ctrl+Shift+Z`), `none` for an unbound action. The primary modifier is always written `Ctrl` and shown as `Cmd` on macOS, so a file reads the same on every platform. Bindings for actions this editor does not have
stay in the file untouched. When the file is read, a chord that does not parse is reported and the action keeps its default, and collisions the saved bindings cause are reported (nothing is refused).

**Recent projects.** *File > Open recent* and the start screen list the last ten game folders, the latest first, without duplicates (the same folder spelled differently counts once). A folder that is gone from the disk is dropped
when the file is read, and when you choose one that turned out to be gone (or is no game folder any more) the editor says so and removes it; *Clear recent* empties the list. Opening a project adds it, the same way as the dialog.

**A file that does not read never crashes the editor and is never lost.** A file that is not XML (or has another root element) is kept as `preferences.bad.xml` next to it; one written by a newer editor (a `version` above 1) is
kept as `preferences.v<N>.xml`; in both cases the defaults are used, a message at the start says so, and the next change writes a new `preferences.xml`. A file that cannot be read at all (locked, no permission) is left
exactly as it is and nothing is saved in that run. A value of the wrong type or outside its range falls back to its default (numbers are clamped). The window shows where the file is and whether it is being saved.

The code is `Source/Weft.Editor/EditorPreferences.cs` (the typed keys, the recent list, the keymap diff), `Source/Weft.Editor/Input/KeyBindingEditor.cs` (the rows and the capture of a chord over `IKeyState`, with no ImGui) and
`Source/Weft.Editor/Panels/PreferencesWindow.cs` (only draws them). The folder is a parameter of `EditorPreferences.Open`, so tests use a temporary one.

## Scenes and undo

*File* creates, opens and saves scenes (`.wscn` XML, see [scenes](../content/scenes.md)); *Open scene...* and *Save as...* use the system's [file dialogs](#file-dialogs). Every edit is a command on an `EditHistory`
(Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z): component values (a drag is one step), adding and removing components, creating, duplicating and deleting entities (a deleted branch comes back under the same ids; deleting or
moving several at once is one step), renaming and reparenting. Opening or creating a scene clears the history.

The history remembers the state the scene was saved in (its **save point**): the scene is *modified* (the `*` after its name, and the question when quitting) only when it differs from the file. Undo back to the saved
state and the mark goes away; undo past it, or make a new edit after undoing from it, and it comes back. A drag that is still going (its steps merge) is not the saved state either. With more than 500 steps (the
history's `Limit`) the oldest are forgotten, and if the saved state was among them the scene stays modified until it is saved again.

## File dialogs

*Open scene...*, *Save as...* (Ctrl+Shift+S, and *Save* on a scene that has no file yet) and *Open project...* show the operating system's own dialog: the open dialog starts in the game's `scenes` folder (or the game folder) and filters
on `.wscn`; the save dialog adds `.wscn` when the name has none. The dialogs come from SDL ([`FileDialog`](../platform/platform.md#file-dialogs)); they are asynchronous, so the editor goes on running while one is open, and the
*New scene*, *Open scene*, *Save as* and *Open project* entries are greyed until it closes (a second dialog is never opened; a shortcut pressed meanwhile opens the editor's own entry instead). A dialog that is cancelled changes
nothing. A dialog that never answers (a desktop portal that crashed) is given up on after ten minutes: the editor is free again, and an answer that arrives later is ignored.

A scene belongs to the game, so only a file **inside the game folder** can be opened or saved: the dialog gives an operating system path, and the editor turns it into the game's virtual path (`<game folder>/scenes/level.wscn`
becomes `res://scenes/level.wscn`). A path outside the folder (a game folder that is a symbolic link, or sits at the root of a drive, is understood), a file that is not a `.wscn`, a name the game cannot use, a place in a read-only part of the game, or a file that
a pack of the game already answers for is refused with a message that says why, and nothing is written. If the project was switched while a dialog was open, its late result is dropped; a result that arrives while the game is running
is applied like the menu entry would: Open scene and Save as end the run first (the scene saved is the edited one, not the run's).

A scene that cannot be opened (it does not parse, it is from a newer engine) or saved (a read-only file, a full disk) is reported in a message and changes nothing: the open scene stays as it was, with its path, its undo history,
its selection and its modified mark, so the next *Save* cannot write an empty scene over it. *Save and quit* and *Save and open* do not go on when the save failed. Known limits: Windows 8.3 short names and macOS file names
written in another Unicode form than the folder's are reported as outside the game folder.

On Linux SDL asks the desktop's file chooser through `xdg-desktop-portal`, or runs `zenity`. A machine with neither has no system dialog: the editor then falls back to its own entries (a list of the game's scenes for *Open
scene*, a path field for *Save as* and for *Open project*), so everything stays reachable.

## The start screen and opening a project

Started without a game folder, the editor opens its window on a start screen with an *Open project...* button (and *File > Open project...*) and the list of [recent projects](#preferences): choose a game folder in the system's folder
dialog, or click a recent one. Only a folder that has a `game.wgam` or a `packages` folder is taken for a game (a mistaken pick, such as the home folder, is refused with a message and nothing closes); choosing the folder that is already open does nothing. The same entry in the *File* menu of an open project **switches projects inside the same process**: if the scene has unsaved changes the editor asks first (*Save and open*, *Open without saving*, *Cancel*); then the
old project is closed (the GPU is waited for, the viewport and the session are disposed, since one session exists at a time) and the new folder is opened at the start of the next frame, with a fresh camera, panels and undo history. The window title
and the crash reports' *Edited game* line follow the project. `--user <folder>` belongs to the game folder given on the command line: a project opened later from the dialog gets its own user folder (what its `game.wgam` says), so two games never share `user://`. If the folder cannot be opened (a `game.wgam` that is wrong, say) the editor says why and opens the previous project again. A folder that does not exist is refused
without closing anything.

## Bringing files in

Drag files or folders from the system's file manager onto the editor's window. The editor accepts the drop only when it can do something sensible with it, and says why when it cannot (a message, and the console lists every file it left out):
no project is open, the game is playing, a dialog is open, an import is already running, a question about names is waiting, or the files landed on a panel that takes none (the hierarchy, the inspector).

**Where they go.** The panel under the pointer decides, using what it showed when you let go:

- **On the Assets panel**: into the folder it shows (a dropped folder is recreated under it). The files are copied and imported; nothing is added to the scene.
- **On the Viewport**: model files (`.glb`, `.gltf`) go to the asset browser's folder when it is inside `res://` below the top, otherwise to `res://models`; once they are imported, one entity per model is made, as **one undo step**, at the point of the ground under the pointer where you let go
  (five metres along the view when the pointer was above the horizon; several models are put side by side, two metres apart). The files are not undone by the undo, only the entities. Other files in the drop are added too. A model that fails to import makes no entity.
- **No position**: some systems (Wayland compositors without the protocol) do not say where the drop happened. The editor never guesses a point: such a drop goes to the Assets panel's folder.

Only folders of the game itself take files: `res://`, which must be writable. A folder of a package (`mod://`) or of a pack is read-only, and the drop is refused.

**What is taken.** Files the game can use: what an importer takes (`.glb` and `.gltf`, `.png`, `.jpg` and `.jpeg`, `.slang` shaders), scenes and prefabs (`.wscn`, `.wprf`), and scripts, definitions, sounds, fonts and UI documents
(`.luau`, `.xml`, `.wav`, `.ogg`, `.flac`, `.mp3`, `.ttf`, `.otf`, `.rml`, `.rcss`). Anything else is refused by name with the list of what is taken. Also refused, each with its reason:
a name the game cannot use on every system (a colon, a trailing dot or space, a Windows device name such as `con`), a hidden file or folder (a name that starts with a dot; hidden entries inside a dropped folder are skipped without a word), the **game folder itself or a folder that holds it** (it would copy the game into itself), anything into **`res://imported`** (the importer's own folder: its index and UID list; a dropped folder of that name too), an empty file, a file over 512 MB or a drop over 2 GB in all, and
**links**: a symbolic link or a junction (dropped or inside a dropped folder) is never followed, because its target can be anywhere. A dropped folder is read to at most 10,000 files, 10,000 folders and 64 levels (this walk, and the reading of a `.gltf`, run on the main thread when the files are dropped: they are capped for that reason). The paths the files get are built only from names that passed these checks, so a name cannot climb out of the folder.

**Models with files of their own.** A `.gltf` brings the buffers and images its `uri`s name (beside it or below it, `%20` and other escapes read as the glTF standard has them, so `a.bin` and `a%2Ebin` are one file; a file or a folder on the way that is a link is refused; data URIs need nothing). A model that names a file that is missing, an absolute path, a drive, or a path that climbs
(`..`) is refused whole, with the reason. The companions are copied with it and imported *as part of the model*, not as assets of their own. A file's `.wimp` beside it comes along; a dropped `.wimp` without its asset is refused.
A sidecar that arrives with a UID that another asset of the project has (you copied an asset together with its settings) gets a new UID, so two assets never share one; the console says which.

**Names the project has.** A dropped file is never written over a file the project has unless you choose it. When some names clash, a question lists them: *Keep both* (the default: the new file gets a free name, `car 2.glb`; a model with files of its own goes into a new folder, since the names inside it cannot change),
*Skip them*, *Replace* (overwrites; **this cannot be undone**; the asset keeps its own `.wimp`, so its UID and settings stay; a link in the project is never written through, that file is kept and the new one gets a free name instead) or *Cancel*. A name that appears between the plan and the copy (another program wrote it) is left alone and reported.
Two dropped files with the same name both arrive (the second is numbered).

**The copy and the import run on a worker thread**, so a large model or a texture compression does not stop the editor. The menu bar and the console header show the progress (*Importing 2/5: res://models/car.glb*), and the console lists each step. *Cancel import* stops at the next file;
what was copied stays (nothing is rolled back), and the files not yet imported can be imported with *Re-import* in the Assets panel. A file that fails to import (a damaged model) stays copied, and the console says why. Files are written through the game's file system, whole or not at all (a crash leaves the old file).
A file is read whole before it is written, which is why a single file is limited to 512 MB, and a file that is being read cannot be cancelled until it has been read.
Files are read again just before they are copied and must be exactly what the plan saw (the same size, not a link, not swapped for a pipe), so a file that changed meanwhile fails on its own and cannot hold the worker. Play is disabled while a job runs (and until its report has been taken), and a drop or a *Re-import* is refused. Closing the project, or the editor, during a job stops it at the next file (or the next megabyte of a big one) and waits up to 20 seconds for the worker; a texture compression cannot be interrupted, so a longer one ends by itself in the background, while the **gate of that game folder stays held** (there is one gate per game folder for the whole process, not one per open project), so opening the same folder again does not start a second importer.

**The hot reloader and the importer.** The editor's session normally imports changed files itself, on the main thread, every frame (`HotReloader`). Two importers must never write `imported/index.xml` at the same time, so the editor holds one **import gate**:
a job takes it for its whole run, the reloader's poll and the *Re-import* menu entry take it for theirs, and whoever finds it taken does nothing that frame (the main thread never waits). While a job runs the reloader is therefore not polled; what it
missed is kept, and when the job ends it notices the new files, finds their artifacts current (the job imported them), and reloads the models that changed without importing anything a second time.
**A project that was never imported** has no `imported/index.xml`, and its models and textures are loaded from their sources. Importing one dropped file would make an index, and from the next frame the reloader would import the whole project on the main thread (every model, every texture compression). So a drop on such a project **only copies**: the console says "the project has not been imported", models dropped on the viewport still become entities (they load from their source), and nothing is imported until the project is: with `Weft.Runtime --import`, or *Re-import* on a file in the Assets panel (which starts the index; the reloader then imports the rest of the project's out-of-date assets as well, on the main thread).

Dropped files arrive as one event for the whole drag-and-drop (see [Platform](../platform/platform.md#files-dropped-on-a-window)). On **X11 and Windows** the position is reliable; on **macOS** it is in points like the rest of the window; on **Wayland** it depends on the compositor and SDL's version.
Dropping from a file manager into a sandboxed editor (Flatpak or Snap) may give the editor a path that is not the real one: add files by copying them into the game folder instead.

## Play

**Play** (F5) runs the game inside the editor. The editor keeps a snapshot of the world, replaces its session with the game's (the same `ScriptSession` the runtime makes: packages, mods and scripts, physics, audio,
the UI) over a copy of the scene, loaded before the scripts run, and shows the game in the viewport through the world's main camera (or the editor's camera when it has none, or when *Game camera* is off). The game gets
the pointer and the input actions while the viewport is hovered or focused; keys and typed text also go to the game's own UI (its text fields) while the viewport has the keyboard; the pointer reaches it through the panel, in the panel's coordinates. *Pause* and *Step* freeze and advance the world. **Stop** makes an editing
session again and puts the world back as it was *under the same entity ids*, so the selection and the undo history still point at the same entities. A game whose scripts fail does not start: the editor says why and
stays in editing. Only one session exists at a time (the UI library and others hold process-wide state), which is why Play and Stop replace it. *Debug* toggles the physics shapes and RmlUi's debugger while playing, and opens the [Engine objects](#engine-objects) window, which works in both modes.

Editing is off while playing: the panels show the game's world, read-only.

## What is not here

An import settings editor and an import dialog (files are [dropped in](#bringing-files-in); their settings are the `.wimp` next to them, see [Asset import](../content/import.md)), materials, animations, an Export dialog (a game is exported with [`./build.sh export`](export.md)), material and logic graph editors (imnodes is ready for them), multi-viewport windows, and a physics ray cast for picking (an editing session has no physics bodies).
Sound plays in play mode through OpenAL Soft ([Audio](../audio/index.md)), which ships beside the editor like any other library.

## Tests

`Tests/Weft.Editor.Tests`: the field codec for every `FieldKind` (a rotation as angles, integers clamped), the editor camera, picking (rays, boxes, spheres, the ground, and the selection box with what is behind the
camera left out), the selection model (toggle, ranges in the visible order, top level, dead entities, the active entity, the multi-select requests), copy, cut, paste and duplicate through a memory clipboard (the same tree under new ids as one step, next to the active entity or under it, unique names, references inside the copy followed and outside ones dropped, models by path, undo of a cut, text that is not a scene, duplicating several, playing), the group transforms (the pivots, moving at ten million metres, rotating and scaling about the centre or in place, local axes, a member under a turned, non-uniformly scaled or mirrored parent, a selected child not moved twice, exactness of a move with a turned pivot, ten million metres out, the scale caveat and its 45 degree value, and the life of a drag: dropped when cut short, sealed, blocked until the button is let go), the keymap (chords parsed and printed for each platform, the defaults and their menu text, conflicts in a context or against an *Anywhere* shortcut, exact modifiers, contexts, no shortcut while typing, held keys, and this page's table equal to the real one), the preferences in a temporary editor folder (values surviving a reopen and only differences written, the debounce with a fake clock, recent projects most recent first without duplicates and with missing ones dropped and a bound of ten, the last scene per project, key bindings saved as a difference and read back with unknown actions kept, collisions and bad chords reported on load, a corrupt file or a newer one set aside with a copy and never overwritten, in-memory preferences that never write), the rebinding logic with a fake keyboard (capturing the next chord with its modifiers, a modifier alone, Escape giving up, shortcuts off while a key is awaited, a colliding chord accepted and warned about, clear and reset), the file dialogs through a scripted fake (an OS path becoming a game path only inside the game folder; Save as writing the chosen file, adding the extension, refusing a path outside the game; Open scene opening it, refusing other paths, files that are not scenes and missing ones; a cancel doing nothing and an error asking for the built-in entry; a second dialog refused; a result that arrives after the project changed dropped; a scene that fails to load leaving the editor exactly as it was and the next Save writing the old scene; a failing save reported with the scene still modified; a name the game cannot use; a result while playing; a second dialog using the built-in entry; only game folders opened; the dialog gate with a fake clock: refusing a second dialog, free again after an exception or a lost dialog, a late answer dropped; paths of a symlinked and a root-level game folder), switching projects (the old project disposed first, the events in order, a missing folder keeping the open project, a game that fails to start reported and the previous project reopened), the project (creating, naming, reparenting,
duplicating and deleting, one or several, through the history; the modified mark clearing when undo returns to the saved scene; a scene saved to a file and opened again with the model's path; Play running the game's
scripts over the scene and Stop restoring the world under the same ids with the selection and history intact; a failing game), dropping files from the system (the plan: where files land, numbering, `Replace` and `Skip`, a glTF's buffers and images, uris that climb out and absolute ones, folders and their limits, read-only roots, refused kinds, names, links and sizes; the import job: copied and imported on another thread, the reloader catching up without importing again and not polling while the importer is held, cancelling between files, Play and a second import refused, nothing overwritten that appeared after the plan, `Replace` keeping the UID, a copied sidecar's UID made new, a model with its buffer, a failed import leaving the file, closing the project waiting for the worker; the flow: an entity at the dropped point as one undo step, the asset browser only importing, every refusal, the question about names, the routing by panel rectangle; the real SDL drop event needs a display and a person and is tested through its synthetic events in `Tests/Weft.Platform.Tests`), the inspector through a fake UI with no ImGui (the lookup order of the drawer table, numbers drawn as before, enum combos with an unknown value and flags checkboxes with unnamed bits kept, colours outside 0 to 1, entity references, hidden, read-only and playing fields, an edit of several entities changing only that field as one step per drag, mixed values, the shared components, add, remove and reset with defaults), the asset fields (the file name with its path and uid, none, mixed, a pick as one undo step and its undo, a drop of the wrong kind refused, a pick that gives no handle left alone with its message, a file not imported yet, clear, several entities, a reference that did not load shown in red and its text dropped by clear or a pick and brought back by undo, a deleted file, a handle made in code, show in assets, a pick saved with its path and uid), the asset catalog on temporary folders (kinds with their uids, the import cache, hidden names and sidecars left out, package roots after `res://` and listed once, extensions in any case, name matches before path matches, the limits, the listing made on a worker and repeated when invalidated or old with a fake clock, found, moved and missing references, the picker's filter and notices), and, on a GPU (skipped without one), the object inspector over test classes (every kind of property read and written through `Value` and undone, read-only and hidden properties, base classes first and categories grouped, cycles and the depth limit, text of one line or several and text too long to edit, enums and flags, lists added to, removed from, moved and typed in, a list of 1000 items drawn in pages and one too big to edit, lists of objects with one destroyed, a getter that throws, a setter that refuses a value and keeps the property and shows why, an object destroyed while inspected, a value that changes by itself every frame, a drag as one step, missing values, class drawers with their fall back) and the engine objects window's model (the session's objects listed, live edits that leave the scene's history and its modified mark alone, its own undo, an object held by another, edits while playing and the history ending with the session), and, on a GPU (skipped without one), a frame of the viewport with no validation errors whose render graph lays out as nodes. `Tests/Weft.Host.Tests` covers the history and
its commands (setting a registered property with undo, merges and an object that is gone, a drag over several entities is one step, composites of the same targets merging, the component defaults, the asset kinds and the command that forgets a broken reference's text); `Tests/Weft.Ecs.Tests` covers reading and forgetting that text.
