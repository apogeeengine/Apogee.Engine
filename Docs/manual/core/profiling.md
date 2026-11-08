# Profiling, SIMD and the shipped instruction set

The rule for speed is *measure, then write SIMD where a measurement says so*. `System.Numerics` is already intrinsic (see [Math](math.md)), so most code needs nothing. This chapter says how to
look, what the shipped programs are compiled for, and where hand-written vector code exists.

## Where to look

- **The dev overlay** ([Dev tools](../tools/devtools.md)): the CPU frame time history, the **GPU time of each pass** (`FrameLoop.Timings`, from timestamp queries, per frame in flight), the render graph and the
  draw, culled and light counts. Start here: it says whether a frame is CPU- or GPU-bound.
- **RenderDoc** shows the same pass names (`frame.Scope(name)` and `recorder.Label(name)` are debug labels).
- **CoreCLR (the editor and `dotnet run`)**: `dotnet-trace collect -- <program>` and `dotnet-counters monitor` (install them with `dotnet tool install -g dotnet-trace dotnet-counters`).
- **NativeAOT (a shipped build)**: a native program, so the platform's profiler reads it: `perf record -g` on Linux, Instruments on macOS, Windows Performance Analyzer or Visual Studio on Windows.
  `./build.sh aot` keeps the symbols (`Apogee.Runtime.dbg` next to the program on Linux, `.pdb` files on Windows).
- **Micro-benchmarks**: `./build.sh bench [filter]` runs BenchmarkDotNet over `Samples/Benchmarks` (a sample: benchmarks are run by hand) in Release (the raw results go to `Bin/benchmarks`, which is not committed) (for example `./build.sh bench '*Culling*' --job short`). BenchmarkDotNet is used there
  only (MIT, a test dependency, never shipped), and CI builds the project but does not run it: benchmarks are noisy on shared machines. Remember that BenchmarkDotNet's NativeAOT mode compiles for the
  CPU it runs on, which is not what a game ships with; to measure the shipped baseline publish a small program with `-p:PublishAot=true` instead.

## The instruction set of a shipped game

The JIT asks the CPU which instructions exist when the program starts. NativeAOT cannot: it compiles for a fixed set, and a CPU without it does not start the program. Measured with a
small program on an AVX2 machine:

| `IlcInstructionSet` | `Vector128.IsHardwareAccelerated` | `Vector256.IsHardwareAccelerated`, `Avx2.IsSupported`, `Fma.IsSupported` |
| --- | --- | --- |
| *(not set)* | true | **false** |
| `x86-64-v2` | true | **false** |
| `x86-64-v3` | true | true |

So the default of x64 *is* `x86-64-v2` (SSE4.2 and POPCNT, practically every PC since 2009), and AVX2 code is simply compiled out of a default NativeAOT build; it is not a run-time check. Our choice, in
`Directory.Build.props` for `PublishAot` builds:

- **x64: `x86-64-v2`**, written down so that it is a decision. `-p:ApogeeAotInstructionSet=x86-64-v3` builds for AVX2, BMI and FMA (Intel since Haswell in 2013, AMD since Excavator in 2015 and Zen) at the price of the older CPUs, which then do not start the game; the next
  section says what that buys.
- **osx-arm64: `apple-m1`** (atomics, CRC, rounding multiply, dot products): every Apple-silicon Mac has at least that.
- Other arm64 and anything else keep the compiler's default (ARMv8.0 with NEON).

The CoreCLR editor and development runs use the JIT and so *do* get AVX2 on a CPU that has it. That is why the code that can use 256-bit vectors checks `Vector256.IsHardwareAccelerated` and falls back.

## Where there is SIMD

Only where it was measured to pay, and with a scalar twin that the tests compare it with.

**Frustum culling** (`Apogee.Core.Numerics.FrustumCulling`, used by `SceneRenderer`): the bounds of every draw are kept as six arrays (centers and half extents, structure of arrays, padded to a multiple of eight),
and each frame the five planes of the camera's view volume are tested against all of them, four or eight boxes per instruction, producing a bit per draw. The three kernels (`CullScalar`, `CullVector128`,
`CullVector256`) do the same arithmetic in the same order, so they set the same bits, which `FrustumCullingTests` checks on random boxes and frusta; `Cull` picks the widest that is hardware accelerated.
Because the scene's boxes are relative to its origin, the frustum is moved to them with `Frustum.Offset` (the plane constants absorb the offset) and the arrays never change.

Measured for 100 000 boxes (Core i7-14700K, .NET 10):

| | scalar | `Vector128` (NativeAOT `x86-64-v2`, the shipped default) | `Vector256` (NativeAOT `x86-64-v3`; CoreCLR) |
| --- | --- | --- | --- |
| time | 1.66 ms | 0.18 ms (9x) | 0.064 ms (26x) |

So a shipped game culls 100 000 draws in under a fifth of a millisecond without AVX2, and AVX2 would save about 0.1 ms more per 100 000: not worth turning players away. For 1 000 boxes the whole cull is about
1.5 µs (128-bit). `./build.sh bench '*Culling*'` reproduces the table; `*RelativePosition*` measures `Double3.RelativeTo` (100 000 positions: about 0.11 ms, scalar double code, which is fast enough that it
has no SIMD twin).

**Not done, on purpose** (nothing measured yet, so nothing written): density-map sweeps and the instancer (terrain), skinning, particles. They get a benchmark first.
