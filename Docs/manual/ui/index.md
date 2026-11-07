# User interface

`Apogee.UI` is the game's UI: [RmlUi](../native/native-libraries.md#rmlui-apogeenativermlui) (HTML and CSS as the game UI world knows them, in RML and RCSS) on the engine's renderer, file system and input, behind the script
global `ui`. A UI is a *document* (RML text, from a file or built in code) of *elements* that scripts make, move, style and listen to. Luau-only mods author UI the same way the base game does, with documents that carry their own [scripts](#scripts-in-documents)
and [data models](#data-models). The ImGui of the [dev tools](../tools/devtools.md) is a different thing: it is for tools and the editor, this is for players.

```lua
local doc = ui:load_document("res://ui/menu.rml")          -- or ui:load_document_text(rml), or ui:create_document() for an empty one
doc:show()
doc:by_id("start"):listen("click", function(event)
    print("clicked", event.target.id, event:get_number("mouse_x"))
end)

local note = doc:create_element("div")                     -- made in code, in no tree yet
note.class_name = "toast"
note:append(note:create_text("Saved"))
doc:append(note)                                           -- the document owns it now
```

## The pieces

- **`UiSystem`** (`Source/Apogee.UI/UiSystem.cs`) starts RmlUi with one context, the screen, and owns what RmlUi asks of its host: files (read through the virtual file system, so `res://`, `mod://<id>/` and `user://`
  paths work in documents' `<link>`s and `src`s, and relative ones resolve against the document), images (decoded by the content loader, premultiplied on loading), the clipboard, the cursor, the clock and the log.
  There is one at a time, because RmlUi has one state per process. `Update(dt)` lays out and runs animations; `SetSize(width, height, density)` follows the window.
- **`UiElement`** is an element as a script holds it: a *handle*. It names the element without keeping it alive, so one whose element was destroyed (its document was closed, an ancestor destroyed) answers `is_alive = false`
  and every call on it does nothing. An element made with `create_element` or `create_text` belongs to the object that holds it until `append` or `insert_before` puts it in a tree, which owns it from then on;
  `remove(child)` gives it back; letting go of an element that no tree ever held destroys it, and `destroy_element()` destroys one now.
- **`UiEvent`** is what a listener receives: `type`, `target`, `current`, `phase`, `stop_propagation()`, and the event's parameters (`get_number("mouse_x")`, `get_string("value")`, `has(key)`). It is good only inside the handler.
- **`UiRenderer`** draws the picture, as a pass of the render graph over the finished image (`IRenderOverlay`); `OverlayChain` puts it under the dev tools. **`UiInput`** feeds one window's events to the UI.

## Documents

RmlUi has **no built-in stylesheet**: a `div` is `display: inline` and takes no width or height, text has no font, `body` is only as big as its content. `ui_base.rcss` (`Source/Apogee.UI`, embedded in the assembly) is added **under**
every document's own rules, so a document, or a mod, overrides anything in it: block elements, `box-sizing: border-box` for everything, headings, lists, links, a look for
form controls and scrollbars, tables, and a `body` as big as the screen with the default font. The default font is Inter (regular, bold and italic, SIL OFL 1.1; `Source/Apogee.UI/Fonts`), registered as the family
`Inter`; `ui:load_font(path, family, bold, italic, fallback)` adds more from any mounted file. Documents loaded from text have `[document from memory]` as their source unless `load_document_text` is given a URL.

`doc:close()` unloads a document with everything in it. A module that is reloaded (`--watch`) closes the documents it loaded and the listeners it added, and loads them again.

## Events and input

`element:listen(type, handler, capture?)` returns a number for `ui:unlisten`. Events are the DOM's: `click`, `mousedown`, `mouseup`, `mouseover`, `mouseout`, `mousemove`, `keydown`, `keyup`, `textinput`, `focus`, `blur`,
`change`, `submit`, `scroll`, `resize`, `transitionend`, `animationend`... They go down to the target (capture) and back up (bubble); `event:stop_propagation()` ends the trip. A handler that throws is logged and the event
goes on: nothing may unwind through RmlUi's native frames. Listeners go when their element does (the system forgets their handlers within a couple of seconds).

The host gives the UI the player's input: `UiInput` turns SDL's events into the UI's (buttons: SDL's left, middle, right are RmlUi's 0, 2, 1; the wheel's y is flipped; keys by USB scancode; typed text; modifiers) and starts
SDL's text input when a text field has the focus. Every call says whether the UI used the input, and `ui.wants_mouse` (the pointer is over an element) and `ui.wants_keyboard` (a text field is focused) tell the game to leave
it alone: the runtime's [input actions](../platform/input.md) are not fed while they are true. A host that shows the UI inside something else (the [editor](../tools/editor.md) shows the game in a panel) sets `UiInput.PointerEnabled = false` when it feeds the pointer itself, and `UiInput.KeyboardEnabled` to a function that says whether keys and text may go to the UI now.

## Scripts in documents

A document can carry its own Luau, the way RmlUi's Lua plugin lets a document carry Lua. The engine implements the same model on Luau (there is no Lua in the engine): `<script>` tags, `on*` attributes and data models.

```html
<rml>
<head>
  <link type="text/rcss" href="menu.rcss"/>
  <script src="menu.luau"/>                       <!-- a file next to the document -->
  <script>
    clicks = 0
    function bump(element, event)                  -- a function in the document's globals
      clicks += 1
      element.inner_rml = "Clicked " .. clicks
    end
  </script>
</head>
<body>
  <button id="b" onclick="bump(element, event)">Click</button>
</body>
</rml>
```

- **Scripts run in the `head`**, when it has been read and before the body is: define functions and open data models there, and do the rest when the document says it has loaded (`document:listen("load", function() ... end)`).
- **Each document has globals of its own** over the ones every script sees. What a `<script>` defines is there for that document's `on*` attributes and for nothing else; use `require` for state that documents share. `document` is the document's `UiElement`.
- **An `on*` attribute is the body of a function** with `event` (the `UiEvent`) and `element` (the element the attribute is on, `event.current`) in scope. It is compiled when its first event comes, because the element is not in its document yet when its attributes are read. A syntax error is logged then.
- **An attribute runs as whoever wrote it.** One in the document's own RML runs as the script that loaded the document, with the document's globals. One that another script puts in with `inner_rml` or `set_attribute` (a mod
  adding to the game's HUD) runs as *that* script, in globals of its own: it has the mod's limits and file rules, and cannot call the game's functions through the document.
- **`<script src>` is read through the virtual file system** with the rules of the script that loaded the document: a mod can read its own files and the ones it depends on, not another mod's (`CanAccessFile`). `require("./helper")` in it is relative to the script's file. The path may be relative to the document or a `res://` or `mod://` one.
- **Scripts run as the script that called `ui:load_document`**: its memory category and its time budget apply, and a mod's UI scripts cannot do what the mod cannot.
- A script that fails is logged with the document's name and line, and the rest of the document loads.
- Without a scripting layer (a bare `UiSystem` in a C# test) scripts and attributes are ignored with a warning; set `UiSystem.ScriptEnvironments` to give it one.

## Data models

A data model ties a table of the script to a document: RmlUi's `{{value}}`, `data-if`, `data-for`, `data-class-*`, `data-style-*`, `data-value` and `data-event-*`. Open the model before the document that uses it loads (a `<script>` in the document's head can).

```lua
-- menu.luau, a <script src> of menu.rml
local game = { volume = 0.8, name = "Ada", items = { { name = "Sword", qty = 1 } }, player = { hp = 100 } }
function game.buy(event, amount) -- data-event-click="buy(3)"
    table.insert(game.items, { name = "Arrow", qty = amount })
    model:dirty("items")
end
model = ui:open_data_model("game", game)
```

```html
<body data-model="game">
  <p>{{name}} has {{player.hp}} hp</p>
  <div data-for="item : items">{{item.name}} x{{item.qty}}</div>
  <div data-if="player.hp < 20">Low health!</div>
  <input type="range" min="0" max="1" step="0.1" data-value="volume"/>
  <button data-event-click="buy(3)">Buy</button>
</body>
```

- **The table is read when RmlUi needs a value**, so it is the one source of truth. When you change it, say so: `model:dirty("items")` for one top-level field (a change inside a struct or a list dirties the field that holds it), `model:dirty()` for everything, or `model:set("volume", 0.5)` to write a top-level field and dirty it. The elements update at the next `ui:update`.
- **The top-level fields are bound when the model opens.** Each has to exist then (use `false`, `0`, `""` or `{}` for a value that comes later) and keeps its kind: a number stays a number, a list a list, a table with named fields a struct. A table with elements, or an empty one, is a list (counted from 1 in Luau, from 0 in `data-for` addresses).
- **Functions in the table are events** for `data-event-*`. They get the `UiEvent` and the arguments of the expression (`buy(3, 'sword')` calls `game.buy(event, 3, "sword")`).
- **`data-value` writes back**: typing in a field, or moving a slider, sets the field in the table (text is converted to the field's number or boolean).
- A model belongs to the script that opened it and is closed when its module reloads; `model:close()` closes it now (close its documents first). `ui:open_data_model` returns nil, with a message in the log, when the name is taken.
- Integers show without a decimal point (`5`, not `5.0`).

## The Lua manual, in the engine's names

RmlUi's [Lua manual](https://mikke89.github.io/RmlUiDoc/pages/lua_manual.html) describes the plugin; the engine keeps the model and uses its own snake_case names, like every other script API.

| RmlUi Lua plugin | Here |
| --- | --- |
| `<script>`, `<script src>`, `onclick="..."` | the same; `event`, `element` and `document` are in scope |
| `rmlui.contexts["main"]` | the `ui` global (one context, the screen) |
| `context:LoadDocument(path)` | `ui:load_document(path)`, `ui:load_document_text(rml)`, `ui:create_document()` |
| `document:Show()`, `:Hide()`, `:Close()` | `doc:show()`, `doc:hide()`, `doc:close()` |
| `document:GetElementById(id)` | `doc:by_id(id)` |
| `element:AppendChild(e)`, `:RemoveChild(e)` | `el:append(e)`, `el:remove(e)` |
| `element.inner_rml`, `.class_name`, `.id` | the same properties |
| `element:SetAttribute(n, v)`, `:GetAttribute(n)` | `el:set_attribute(n, v)`, `el:get_attribute(n)` |
| `element.style["width"] = "10px"` | `el:set_property("width", "10px")` |
| `element:AddEventListener(type, fn)` | `el:listen(type, fn)` (a number for `ui:unlisten`) |
| `event.parameters.mouse_x`, `event:StopPropagation()` | `event:get_number("mouse_x")`, `event:stop_propagation()` |
| `context:OpenDataModel(name, table)` | `ui:open_data_model(name, table)` |
| assigning to a model field | `model:set(name, value)` or change the table and `model:dirty(name)` |
| Lua 5.x | Luau only; its `require` is Luau's, relative to the file |

## In the runtime

`--game` and `--run` give every script `ui`. Each frame the runtime sizes it to the window (in pixels, with the window's pixel density as the `dp` unit), feeds it input, steps it after the world and the scripts have run,
and draws it over the picture (the 3D world if there is a camera, the plain background otherwise) with the dev overlay on top. The UI renderer is made the first frame after a document was loaded, so a script that only has
ImGui panels never compiles its shader. `ui.debugger = true` shows RmlUi's element inspector over the UI.

## Drawing

RmlUi compiles geometry (vertices with positions in pixels, premultiplied-alpha colors and texture coordinates) and asks to render it with a translation, a texture, a scissor rectangle and an optional transform.
`UiRenderer` keeps the compiled vertices and indices in memory and, when a frame's graph is built, packs the geometry that frame draws, once each however often it is drawn, into per-frame upload buffers (as the ImGui
backend does), so nothing on the GPU depends on when RmlUi releases a piece of geometry. `ui.slang` pulls the 20-byte vertices from a buffer, applies the element's CSS transform and the translation, and maps pixels to
clip space at the context's size; the pipeline blends premultiplied (`source + destination * (1 - source alpha)`), the textures are premultiplied when they are made, and colors go to the (UNORM) target as authored, like ImGui's.
Textures are bindless images whose index is the handle RmlUi holds (0 is the white texel that untextured geometry samples); a released texture is freed when the frame that last used it is done.

### Effects

RmlUi 6 asks the host for more than geometry, and `UiRenderer` records everything it asks for, in order, then turns the list into passes of the render graph.

| RCSS | What RmlUi asks | What we do |
| --- | --- | --- |
| `overflow: hidden` with a `border-radius`, or a transform | a **clip mask** (`EnableClipMask`, `RenderToClipMask`: set, set inverse, intersect) | a texture the size of the context whose red channel is the coverage. Every operation draws its shape into a new one (an intersection samples the one before), and `ui.slang` multiplies what it draws by the mask. This is a coverage texture, not a stencil buffer: it needs nothing from the graphics device's pipelines, and it is the same on every backend. A plain rectangle stays a scissor. |
| `filter: opacity() blur() drop-shadow() brightness() contrast() grayscale() hue-rotate() invert() saturate() sepia()` | **layers** (`PushLayer`, `PopLayer`, `CompositeLayers`) and **filters** (`CompileFilter`) | what is inside a filtered element is drawn into a transient RGBA8 layer (an image of the render graph, as large as the context), and the filter chain runs on it as full-screen passes: a 5x4 color matrix for the color filters, a separable Gaussian for `blur`, shifted and tinted blur under the source for `drop-shadow`. The result is composited onto the layer below, inside the scissor and the clip mask. |
| `box-shadow`, `mask-image` | **`SaveLayerAsTexture`**, **`SaveLayerAsMaskImage`** | the box shadow is drawn once into a layer, blurred, and kept as a texture RmlUi gives back to draw; a mask image is drawn into a layer whose alpha multiplies the element when it is composited. |
| `decorator: linear-gradient()`, `radial-gradient()`, `conic-gradient()` (and `repeating-` forms) | **shaders** (`CompileShader`, `RenderShader`) | a branch of `uiFs`: the gradient's stops are in a per-frame storage buffer and the fragment computes its color from its position in the element. |

The `opacity` property is not a layer: RmlUi folds it into the colors of each element, so overlapping children of a half-transparent element show through each other. `filter: opacity()` is the one that makes a group.
Not supported: `backdrop-filter` (it would sample the frame under the UI, which is not an image the pass can read) and a filter on the root of the context. `UiRenderer.LastEffects` says how many masks, layers and composites the last
frame had; the tests in `UiRenderingTests` read pixels back for each of the rows above.

## Animations

Animations and transitions run on the sum of the `Update` steps, so a paused game pauses its UI and a test is deterministic. RmlUi limits one step to a tenth of a second per animation (a long frame does not skip
one). Tween names are RmlUi's (`linear-in`, `quadratic-out`, `cubic-in-out`); a transition starts counting at the first update after the change; `transition: width 1s linear-in` in a rule and a class change is the usual shape.

## Tests

`Tests/Apogee.UI.Tests` runs RmlUi through the whole engine: the base stylesheet under a document's own rules, the default font and its bold face, making and moving and destroying elements from the script surface (and what a
handle to a destroyed one does), attributes, inline styles and pseudo-classes, listeners (bubbling, capture, stopping, a handler that throws, pruning), typing into a text field, documents and their stylesheets read through
the file system, resizing and density, transitions and the step limit, the debugger, the SDL key and button tables; and on a GPU, pixels read back from the render graph: a box at its place in its color, premultiplied blending
over black and white, `overflow: hidden` clipping, CSS transforms (a translation and a rotation), text from the default font, an image with straight alpha from the file system, the geometry cache across frames, edits
showing on the next frame, resizing. `RuntimeTests` runs a game folder with a document through `--headless --screenshot` and clicks an element from a Luau system, whose handler writes a file.
