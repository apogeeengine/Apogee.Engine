# Viewer sample

`Samples/Viewer` (`Apogee.Samples.Viewer`) is the engine's first program that shows something: a procedural colonnade, six small
glTF models on pedestals and 256 animated colored lights, drawn by the Forward+ renderer. It opens a window with the dev overlay, or
renders offscreen (`--headless`) for screenshots. Everything scene-specific lives in the sample, next to its assets;
the libraries it uses are `Apogee.Platform`, `Content`, `Graphics`, `Rendering` and `DevTools`.

```sh
dotnet run --project Samples/Viewer                 # the window
./build.sh aot && Bin/publish/viewer/Apogee.Samples.Viewer --help
```

## Controls

WASD move, Q/E down and up, Shift is faster, hold the right mouse button to look, the wheel changes the speed, F1 shows or hides the
dev overlay, Esc quits.

## Options

| Option | |
| --- | --- |
| `--scene <courtyard\|file.gltf>` | what to show; a `.gltf` or `.glb` is lit with the light show |
| `--assets <dir>` | where the sample models are (default: `Samples/Viewer/Assets`, found by walking up from the program) |
| `--no-models` | leave the sample models out of the courtyard |
| `--lights <n>` | animated point lights, 256 by default |
| `--camera x,y,z,yaw,pitch` | start position in meters, angles in degrees |
| `--headless` | no window: render offscreen |
| `--frames <n>` | stop after n frames (headless default: 1) |
| `--size <w>x<h>` | window or image size (default 1280x720) |
| `--overlay` | draw the dev overlay in headless images |
| `--screenshot <file.png>` | headless: write the last frame |
| `--gpu <name>` | prefer the GPU whose name contains this (sets `APOGEE_GPU`) |
| `--no-vsync` | do not wait for vertical blank |
| `--watch` | window: use the shaders' source files (found in the repository above the program) and rebuild the pipelines that use a shader when you save it, with no hitch; an edit that does not compile keeps the old image and shows the error in red in the overlay |

Exit codes: 0 fine, 1 bad arguments, 2 failure (no GPU, a file that does not load, a shader that does not compile), 3 the validation
layer reported errors.

## Headless runs

Headless frames advance by a fixed 1/60 s, and the light show is a function of time only, so a given `--frames` always renders the same
image on the same GPU:

```sh
Apogee.Samples.Viewer --gpu llvmpipe --headless --size 640x360 --frames 4 --screenshot viewer.png
```

The Viewer is a sample: people run it by hand, and neither CI nor any test runs it (there is no reference image to compare with). What the
renderer must get right is tested, with its own fixtures, in `Tests/Apogee.Rendering.Tests`. `./build.sh aot-viewer` publishes it with NativeAOT.

## Models

The six models in `Samples/Viewer/Assets` are CC0 glTF-Sample-Assets files kept as Git LFS objects (see their `README.md`); where LFS was not
pulled the courtyard still builds, without them. Larger scenes are not in the repository: Amazon's Lumberyard Bistro is CC BY 4.0 and
a conversion to glTF is a local step, and Sponza and DamagedHelmet were left out because their licenses do not fit a repository that is freely redistributable.
