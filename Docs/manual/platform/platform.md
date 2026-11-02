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

## Windows for a graphics API

A window is made for the API it will be drawn with: `WindowOptions.Vulkan` (loads the Vulkan loader) or `WindowOptions.Metal`. Ask the graphics layer which: `GraphicsDevice.RequiredWindowOptions()`
(Vulkan for now; Metal on macOS when that backend exists). `Apogee.Platform` knows no graphics API beyond that flag: the instance extensions and the surface are the graphics backend's business (it uses `window.NativeHandle`,
the `SDL_Window*`, and `window.PixelSize`), see [Graphics](../graphics/graphics.md).

## Tests

The tests use SDL's `dummy` video driver, which has windows but no display, and push synthetic events into the queue, so they run on
a build server.
