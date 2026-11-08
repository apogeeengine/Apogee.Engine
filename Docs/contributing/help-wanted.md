# Help wanted

The engine is developed on Linux with Vulkan, and checked on Windows and macOS by CI. That leaves work that needs hardware, platforms or a second pair of eyes the maintainer does not have. Each task below says what
exists, what is missing and how to check your work. Open an issue or a discussion before starting a big one, and read [CONTRIBUTING.md](https://github.com/apogeeengine/Apogee.Engine/blob/main/CONTRIBUTING.md).

## Metal backend

**What exists.** Everything compiles, and CI checks what a GPU-less runner can: the Objective-C shim `Native/metal_c` builds, its bindings (`Apogee.Native.Metal`) resolve, the engine's Metal shading language is
accepted by Apple's own compiler, and the backend `Apogee.Graphics.Metal` (device, buffers, textures, argument-buffer bindless heap, pipelines, command encoders, `CAMetalLayer` swapchain, frame loop) is complete
on paper. GitHub's macOS runners have a paravirtual GPU that is not Metal 3, so **none of the Metal GPU tests has ever run**: expect bugs.

**Needs:** a Mac with macOS 15 or later and a Metal 3 GPU with tier 2 argument buffers (Apple silicon, or an Intel Mac from 2017 on). Either architecture helps, because a bug can be in one only.

| Task | Where to look | Done when |
| --- | --- | --- |
| **Run it and fix what breaks.** Start with `--gpu-info`, then `APOGEE_REQUIRE_METAL=1 ./build.sh test`, then the viewer and the samples. | [Metal on a Mac](../manual/graphics/metal.md), `Source/Apogee.Graphics/Metal/`, `Native/metal_c/` | the GPU tests pass with no skips, and the viewer draws the courtyard in a window |
| **Measure the image tolerance.** Metal's rasterizer is not Mesa's, which made the reference images; the Metal bound (mean 2, worst channel 64) is a guess. | `GpuTest.ReferenceTolerance` in `Tests/Apogee.Graphics.Tests/GpuFixture.cs` | the bound is tightened to what Macs measure, or Metal gets reference images of its own |
| **GPU timings.** `FrameLoop.Timings` is empty on Metal. Counter sample buffers (`MTLCounterSampleBuffer`) sample at encoder boundaries, and not on every GPU. | `MetalFrameLoop`, `MetalDevice.SupportsTimestamps`, `apogee_metal.h` | `GpuScopesAreTimedAndLabelled` runs (is not skipped) and passes on Metal |
| **Shader library cache.** Metal shading language is compiled with `newLibraryWithSource` every start; an `MTLBinaryArchive` would keep the result. | `MetalDevice.CreateComputePipeline` and `CreateGraphicsPipeline` | the second start does not compile |
| **A CI runner with a real Metal GPU.** A self-hosted Mac makes `APOGEE_REQUIRE_METAL=1` possible: point `macos_builds.yml` at it (the probe step already turns the requirement on when a GPU qualifies). | `.github/workflows/macos_builds.yml`, "GPUs (Metal probe)" | the Metal tests are required in CI |
| **Check the Intel (`osx-x64`) NativeAOT build.** CI publishes it as an artifact, but it has only been built. | the `apogee-osx-x64` artifact | the fixture game, a sample and the Luau tests run from it |

## Windows testing

**What exists.** CI builds and tests Release on `windows-latest` (a software GPU at best), and the NativeAOT runtime runs the fixture game headless. Nobody has run the editor, the samples or the GPU tests on real Windows
hardware.

**Needs:** a Windows 10 or 11 machine with a Vulkan 1.3 GPU (AMD, NVIDIA or Intel; Windows on Arm too).

| Task | Done when |
| --- | --- |
| **Run it.** `./build.sh test`, then the viewer sample and the editor (`dotnet run --project Source/Apogee.Editor -- Samples/Mods/game`). Report what fails, with `--gpu-info`'s output. | an issue per problem, or a "works" note in the Windows discussion with your GPU and driver |
| **Controllers, high-DPI, multi-monitor, fullscreen.** The input and window code is checked on Linux only. | what breaks is reported or fixed |
| **NativeAOT on Windows.** `./build.sh aot` and the games' commands in [Building](../getting-started/building.md). | the fixture game and the Luau tests run from the published binary |

## Reviewing

The maintainer works alone, so every kind of review helps: the `unsafe` and interop code (`Apogee.Interop`, the generated bindings' layout checks, the native shims in `Native/`), the public APIs (naming, ownership, what throws),
the shaders, and the Manual (a page that does not match the code is a bug). Open an issue with what you found, or a pull request.

## Expanding the engine

Features that fit the scope in [CONTRIBUTING.md](https://github.com/apogeeengine/Apogee.Engine/blob/main/CONTRIBUTING.md) are welcome: rendering features (shadows, post-processing, terrain), editor panels, tools, samples,
the editor (importing models and assets, materials, animations, exporting a game: it is very bare-bones), more Luau API, performance work with a benchmark in `Samples/Benchmarks`. Every feature comes with tests and a Manual page. Ask first if it is big.

## Tell us if you hit this

The `Apogee.Ecs.Tests` test program crashed twice on the Intel macOS CI runner (that job is experimental, so a failure does not fail the run) with a floating-point exception (SIGFPE) in a non-main thread, and passed on other runs. It has not been explained. The CI
prints the crash report's faulting thread when it happens again.
