# Metal

The Metal backend ([Graphics](graphics.md)) was written without a Mac in front of the developer, and has never run on a GPU. The macOS jobs of the CI build it on every push and run the tests that can run there; a Mac
that meets the baseline can check the rest. This page is for whoever has one, and the open tasks are in [Help wanted](../../contributing/help-wanted.md).

**Needs:** macOS 15 or later, and a Mac with Metal 3 and tier 2 argument buffers: an Apple silicon Mac, or an Intel Mac from 2017 on with a recent GPU. The same checks apply to both.

## What CI does

- Builds `metal_c` (the Objective-C shim), the bindings and the backend, and runs the native binding tests (`Tests/Apogee.Native.Tests`) against the real library.
- Compiles every engine shader's Metal shading language with Apple's own compiler (`xcrun metal -c`; `AppleCompilesEveryEngineShaderToMetal`).
- Runs `Apogee.Runtime --gpu-info`. If the runner's GPU meets the baseline (the line `metal: … ok`), the GPU tests are required to run on it (`APOGEE_REQUIRE_METAL=1`); if not, they skip and the log says why.
- Does all of this on an Apple-silicon runner and an Intel one, and uploads each NativeAOT build as the artifact `apogee-osx-arm64` / `apogee-osx-x64`. When the probe finds a qualifying GPU it also renders the fixture game on Metal (uploaded as `metal-screenshots-*`).

## Checking on a Mac

Clone the repository, download the artifact for your Mac (`apogee-osx-arm64` or `apogee-osx-x64`) from the CI run and unzip it into `Bin/publish` (so that `Bin/publish/runtime` exists). A downloaded binary needs
`chmod +x Bin/publish/runtime/Apogee.Runtime` and `xattr -dr com.apple.quarantine Bin/publish` first. Then, from the repository's root:

```sh
Bin/publish/runtime/Apogee.Runtime --gpu-info                       # which GPUs, and whether they qualify; send this line first
Bin/publish/runtime/Apogee.Runtime --game Tests/Fixtures/ShippedGame --user /tmp/game-user --headless --size 640x360 --frames 10 --screenshot game.png
Bin/publish/runtime/Apogee.Runtime --run Samples/Scripting/main.luau --headless --size 640x360 --frames 30 --screenshot scripting.png   # a sample, run by hand
Bin/publish/runtime/Apogee.Runtime --luau-test Tests/Luau
```

Then a window, which tests the swapchain: run the lighting sample (`./build.sh aot-viewer`, then `Bin/publish/viewer/Apogee.Samples.Viewer`) and look at it for a few seconds (fly with the mouse and WASD; resize the window).

Report (an issue, or a pull request with the fix) the output of `--gpu-info`, the PNGs, and anything printed in the terminal. There are no reference images: a few pixels of difference at edges between GPUs is the rasterizer, a black or garbled image is a bug.

To run the engine's tests from a checkout instead: `APOGEE_REQUIRE_METAL=1 ./build.sh test` (a missing or unqualified GPU is then a failure, not a skip).

## What is not covered

GPU timings (`Timings` is empty on Metal: Metal samples counters only at encoder boundaries, and not on every GPU) and a binary archive cache for shader libraries.

The images are compared with the references that Mesa's software rasterizer made, so the Metal tests allow a mean difference of 2 (worst channel 64) instead of Vulkan's 0.5 (24); `APOGEE_IMAGE_TOLERANCE=<mean>` sets it.
These numbers are a guess until a real Mac has measured the difference: report the "mean difference" lines, and the bound can be tightened (or the Metal reference images split off).
