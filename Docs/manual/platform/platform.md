# Platform

`Apogee.Platform` is the engine's window and event layer over SDL3. It runs on the main thread, as SDL requires.

## Lifetime and events

```csharp
PlatformHost.Initialize();                       // SDL video + events + gamepads; PlatformHost.Initialize("dummy") for headless tests
using var window = new Window("My game", 1280, 720, WindowOptions.Resizable | WindowOptions.HighPixelDensity | GraphicsDevice.RequiredWindowOptions());

while (!PlatformHost.QuitRequested && !window.CloseRequested)
{
    PlatformHost.PumpEvents();
    // …update and render…
}
window.Dispose();
PlatformHost.Shutdown();
```

`PumpEvents` drains SDL's queue once per frame. A window learns about its own close request, minimize/restore and focus changes;
`PlatformHost.QuitRequested` is set by the platform's quit event. The application decides what a close request means, so
`CloseRequested` stays set until `ClearCloseRequest()`. `PlatformHost.RawEvent` is called with every SDL event before the platform
handles it: the dev tools' input backend and `InputDevices` (see [Input actions](input.md)) hook in there.

## Windows

`PixelSize` is the drawable size in pixels, which is what a swapchain is created with; `LogicalSize` is in points, which is what the
window manager and mouse coordinates use. They differ on high-density displays (`PixelDensity` is the ratio) when the window was created
with `HighPixelDensity`. `ConsumeResize()` returns true once after the pixel size changed, which is the cue to recreate the swapchain.

## Application metadata and the window icon

`PlatformHost.SetAppMetadata(name, version, identifier)` tells SDL what the application is, and goes before `Initialize`: the name the platform shows (the macOS menu, the audio mixer), the version, and the
reverse-DNS identifier (`org.apogee.fixturegame`) that Wayland uses as the window's application id and that the sound server uses for the stream. The runtime passes the ones in `game.xml`.

`window.SetIcon(width, height, rgba)` gives a window an icon from RGBA8 pixels (it returns false when the driver cannot show one; a buffer smaller than the image is an `ArgumentException`).
The runtime uses it on Linux, with the `<Icon>` file of `game.xml`; Windows and macOS take the icon from the executable and the bundle instead.

## File dialogs

`FileDialog` shows SDL's native dialogs: `OpenFile(window, filters, defaultLocation, many, done)`, `SaveFile(window, filters, defaultLocation, done)` and `OpenFolder(window, defaultLocation, many, done)`. A filter is a name and a
pattern (`("Scenes", "ascene")`, `("Models", "glb;gltf")`, `("All files", "*")`); the platform may let the user ignore it. Each call returns at once with a request id, and `done` receives a `FileDialogResult` later:
`Paths` (operating system paths) when the user chose, `Cancelled` when the dialog was closed, or an `Error` (SDL's own text) when it could not be shown, which is what a Linux machine with neither `xdg-desktop-portal` nor `zenity` reports.

SDL reports the result on a callback that may run on **another thread** (Windows, the Linux portal and zenity) or before the call has returned, so `done` is never called from it. The callback only copies the paths into managed strings
(SDL frees its list when the callback returns) and puts the result into a thread-safe queue; `PlatformHost.PumpEvents` then calls `FileDialog.Dispatch()`, which runs the continuations **on the main thread**, once each. That queue is the only
state the two threads share; the request's id travels as the callback's userdata, not a pointer to anything managed. The filter table, its strings and the default location are one unmanaged block per request that SDL requires to
stay valid until the result: it is freed when the result is dispatched (a dialog still open at `PlatformHost.Shutdown` keeps its few bytes, since SDL may not have read them yet; a callback arriving after that finds no request and is dropped). A continuation that throws is logged and does not stop the others.
Call the dialogs from the main thread only (macOS requires it).

The dialogs need a display and a person: the tests call the callback themselves, from another thread, with synthesized result lists, and check the copy, the null (error) and empty (cancel) lists, that nothing runs before `Dispatch`, the memory layout
and the shutdown. They cannot show a dialog.

The drop events cannot be produced without a display and a person either, and SDL frees the strings of an event it did not make, so `FileDropTests` feeds the collector synthetic events (strings in memory the test frees right after, as SDL does): the batch, the copied paths, the
position (the hover position, or the file's, or none), a lost complete closed by the next begin or by the clock, text and empty drags delivering nothing, and a handler that throws.

## Files dropped on a window

`PlatformHost.FilesDropped` is raised, from `PumpEvents` on the main thread, when the user drops files or folders from the system onto a window, with a `FileDrop`: the `WindowId`, the `Paths` (operating system paths, copied: SDL frees its strings after
each event), and the position of the drop in window points (`X`, `Y`; `HasPosition` is false when the system gave none, and the position is then not to be guessed). SDL reports a drop as several events (`DropBegin`, `DropPosition` while dragging over
the window, one `DropFile` per file, `DropComplete`); the platform groups them into **one** `FileDrop` per drag-and-drop, delivered at `DropComplete`. A `DropComplete` that never comes is closed by the next `DropBegin`, or five seconds after the batch's first file (SDL3 begins a drop when the drag enters the window, so the time spent hovering does not count); a drag that leaves the window or drops
text delivers nothing (`DropText` is ignored). `PlatformHost.DragHover` is the pointer's position while something is dragged over a window, or null. A handler that throws is logged, and the other handlers still get the drop.

## Windows for a graphics API

A window is made for the API it will be drawn with: `WindowOptions.Vulkan` (loads the Vulkan loader) or `WindowOptions.Metal`. Ask the graphics layer which: `GraphicsDevice.RequiredWindowOptions()`
(Vulkan for now; Metal on macOS when that backend exists). `Apogee.Platform` knows no graphics API beyond that flag: the instance extensions and the surface are the graphics backend's business (it uses `window.NativeHandle`,
the `SDL_Window*`, and `window.PixelSize`), see [Graphics](../graphics/graphics.md).

## Tests

The tests use SDL's `dummy` video driver, which has windows but no display, and push synthetic events into the queue, so they run on
a build server.
