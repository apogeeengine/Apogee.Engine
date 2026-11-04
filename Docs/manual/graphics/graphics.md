# Graphics

`Apogee.Graphics` is the GPU layer. The renderer, the dev tools and the content pipeline talk to a small **portable API**: `GraphicsDevice` and the objects it makes (buffers, images,
samplers, pipelines, a command recorder, a swapchain and a frame loop). Each graphics API implements it in its own namespace: `Apogee.Graphics.Vulkan` and `Apogee.Graphics.Metal`. The renderer on top
of it is in [Rendering](rendering.md).

## Backends: the seam

Everything above the backends is abstract classes with the portable surface, and each backend has one sealed class for each:

| Portable (`Apogee.Graphics`) | Vulkan (`Apogee.Graphics.Vulkan`) | What it hides |
| --- | --- | --- |
| `GraphicsDevice` | `VulkanDevice` | instance, physical device choice, queue, memory allocator, the window's surface |
| `GpuBuffer`, `GpuImage`, `ImageView`, `GpuSampler` | `VulkanBuffer`, `VulkanImage`, `VulkanImageView`, `VulkanSampler` | `VkBuffer`, `VkImage` and views (VMA allocations) |
| `Pipeline` | `VulkanPipeline` | pipeline objects and the shared pipeline layout |
| `CommandRecorder` | `VulkanCommandRecorder` | command buffers, barriers (`VulkanStates` maps an `ImageState` to layouts, stages and accesses), dynamic rendering |
| `BindlessHeap` | `VulkanBindlessHeap` | the descriptor set of texture, sampler and storage-image arrays |
| `ImmediateContext` | `VulkanImmediateContext` | a command buffer and a fence for loading and tools |
| `Swapchain`, `FrameLoop` | `VulkanSwapchain`, `VulkanFrameLoop` | acquiring and presenting, the timeline semaphore of the frames in flight, timestamp queries |

The portable names of things are the engine's own: `PixelFormat` (in `Apogee.Core.Graphics`; its numbers are Vulkan's `VkFormat` numbers, which are also the `vkFormat` of a KTX 2 file, so a texture's
format is read by a cast, and another backend maps the ones it supports with a table), `BufferUsage`, `ImageUsage`, `SampleCount`, `CullMode`, `FrontFace`, `CompareOp`, `PrimitiveTopology`, `Filter`,
`MipmapMode`, `AddressMode` and `IndexFormat` (all with Vulkan's numbers too), and `ShaderCode` (the compiled code of one entry point and which `ShaderBackend` it is for). `Apogee.Graphics.Vulkan.VulkanConvert`
turns them into Vulkan's enums; the barrier states (`ImageState`, `BufferState`) were already abstract. A device refuses `ShaderCode` of another backend.

**A test keeps the seam closed.** `BackendSeamTests` reads the public surface of `Apogee.Graphics`, `Apogee.Rendering`, `Apogee.DevTools`, `Apogee.Content` and `Apogee.Platform` by reflection and fails on any
signature that names a type of `Apogee.Native.Vulkan`, outside `Apogee.Graphics.Vulkan`. Code that does need Vulkan (the backend's own tests, tools) casts: `(VulkanDevice)device`.

**Creating a device.**

```csharp
using var window = new Window("My game", 1280, 720, WindowOptions.Resizable | GraphicsDevice.RequiredWindowOptions());   // a window is made for one API
using var device = GraphicsDevice.Create(new GraphicsDeviceOptions { Window = window });                              // no window: headless
using var swapchain = device.CreateSwapchain(vsync: true);
using var loop = device.CreateFrameLoop(swapchain);
```

`GraphicsDeviceOptions.Backend` chooses the API; when it is null the platform's is used (`GraphicsDevice.DefaultBackend`: Metal on macOS, Vulkan elsewhere; `APOGEE_GRAPHICS=vulkan|metal` overrides it for development).
`TryCreate` returns the reason instead of throwing, for a machine without a usable GPU or driver (`GraphicsException` is what the creation functions throw). `GraphicsDevice.EnumerateDevices` lists every GPU with the reasons
it cannot be used. `device.Name`, `Kind`, `Backend`, `ShaderBackend` and the capabilities (`SupportsBc`, `SupportsAstc`, `SupportsAnisotropy`, `MaxSamplerAnisotropy`) are portable;
`device.ValidationErrorCount` / `TakeValidationErrors()` count what the API's validation layer reported, which is how the tests fail on any validation error.

### Other backends

Direct3D 12 is not planned: Vulkan already covers Windows (AMD, NVIDIA and Intel ship Vulkan 1.3 drivers), Linux and the Steam Deck. The seam does not rule one out (push constants of 128 bytes fit root constants, the bindless
heap maps to a descriptor heap, the render graph's states are abstract, and the shader bundle reserves a slot for DXIL), but the engine's shaders read buffers through pointers (device addresses), which HLSL does not have.

## The Metal backend

**Status: written, never run on a GPU ([help wanted](../../contributing/help-wanted.md)).** It compiles, the shim builds on macOS CI, Apple's compiler accepts every shader, and the native bindings are tested; GitHub's macOS runners have no Metal 3 GPU, so the device, rendering and swapchain code has not run.

`Apogee.Graphics.Metal` is the default on macOS. It needs **macOS 15**, **Metal 3** and **tier 2 argument buffers**, which is Apple silicon and the Intel Macs of 2017 and later; `EnumerateDevices` says why a GPU
does not qualify. Metal is Objective-C, so it is reached through a small C API, `Native/metal_c` (see `ThirdParty/README.md`), bound by `Apogee.Native.Metal`. Nothing Objective-C crosses it: objects are opaque
handles, and completion is a shared event's value rather than a block.

| Portable | Metal |
| --- | --- |
| `GpuBuffer` | `MTLBuffer`: shared (mapped for life) for upload and readback; device-local is private, filled through a staging buffer. Its `Address` is `gpuAddress`, which is what the push constants carry |
| `GpuImage`, `ImageView`, `GpuSampler` | `MTLTexture` and `MTLSamplerState`; `PixelFormat` is mapped by a table in the shim |
| `BindlessHeap` | one **argument buffer**: 16 384 texture `gpuResourceID`s, then 256 sampler ids, 8 bytes each (slot 0 is a white texel and a linear-repeat sampler) |
| `CommandRecorder` | an `MTLCommandBuffer`; the render, compute or blit encoder a command needs is opened on demand and ended by the next kind of command or by a barrier |
| `Pipeline` | `MTLRenderPipelineState` / `MTLComputePipelineState` from the bundle's MSL (compiled with `newLibraryWithSource` when loaded); a compute pipeline's thread-group size comes from Slang's reflection |
| `Swapchain`, `FrameLoop` | a `CAMetalLayer` (SDL creates it for a `WindowOptions.Metal` window) and an `MTLSharedEvent` as the timeline, with the same frames in flight as Vulkan |

Two things differ from Vulkan and shape the code:

- **Residency.** Buffers are reached by address and textures by the argument buffer, so Metal does not know the GPU uses them. Every buffer and texture is added to one `MTLResidencySet` when it is made (this is why
  macOS 15 is the minimum), and the set is committed before a submission when something was added.
- **Hazards.** Metal does not order what shaders reach through an address. Encoders are chained with an `MTLFence` (each waits for the one before), which is what the render graph's barriers mean on Metal.
  Image layouts do not exist, so `Barrier` only ends the current encoder.

Not done yet: GPU timings (`FrameLoop.Timings` is empty on Metal), Metal's binary archive cache for compiled shader libraries. The Metal tests (`MetalBackendTests`, and every shared GPU test when the device is Metal)
skip without a qualifying GPU, unless `APOGEE_REQUIRE_METAL=1`. How to check a build on a Mac: [Metal on a Mac](metal.md); the open tasks: [Help wanted](../../contributing/help-wanted.md).

## The Vulkan backend: instance and device

`GraphicsDevice.Create` makes a `VulkanInstance` and, for a window, its `VkSurfaceKHR` (through SDL), and the `VulkanDevice` owns both; a caller that keeps its own instance uses `VulkanDevice.Create(instance, options)`.

`VulkanInstance` asks for Vulkan 1.3, turns on the Khronos validation layer when it is installed (`APOGEE_VULKAN_VALIDATION=0` turns it
off, `=1` makes a missing layer an error), and installs a debug messenger: warnings and errors from the layers and the driver go to the
log, and `ErrorCount` / `TakeErrors()` count them. On macOS the loader's portability enumeration is enabled so MoltenVK is listed.

The device picks the best GPU that meets the engine's baseline: Vulkan 1.3 with dynamic rendering, synchronization2,
maintenance4, buffer device addresses, timeline semaphores and descriptor indexing (runtime arrays, partially bound, sampled images update
after bind, non-uniform indexing), plus a graphics and compute queue that can present to the window when one is given. Discrete GPUs win
over integrated ones, then virtual, then software. `PreferredDeviceName` or `APOGEE_GPU=llvmpipe` picks by name (a substring), and a GPU
that fails the baseline is skipped with its reason.

The device owns the VMA allocator (created with `bufferDeviceAddress`; VMA loads Vulkan through the device's own entry points), and `VulkanDevice.SetDebugName` names objects for the validation layer and RenderDoc.

## Buffers, images, samplers

All GPU memory comes from VMA through three small classes. `GpuBuffer` always gets a device address (`buffer.Address`), which is how
shaders reach buffers: a pointer in push constants, no descriptors. `MemoryKind` says where it lives: `DeviceLocal` (fastest, filled by
copying), `Upload` (the CPU writes it, the GPU reads it; mapped for its whole life) and `Readback`.

```csharp
using GpuBuffer vertices = device.CreateBuffer(vertexBytes, BufferUsage.Storage, "mesh vertices");   // uploads through staging
using GpuBuffer perFrame = device.CreateBuffer(256, BufferUsage.Uniform, MemoryKind.Upload);
perFrame.Write(frameConstants);

using GpuImage albedo = device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Srgb, 1024, 1024, ImageUsage.Sampled, MipLevels: 11));
device.UploadImage(albedo, texels, regions, ImageState.ShaderRead);
```

`GpuImage` knows its description, has a view of everything (the depth aspect only, for depth-stencil formats) and makes extra views for one
mip or layer. Memory from VMA is not zeroed. `UploadBuffer`, `UploadImage`, `ReadBuffer` and `ReadImage` go through staging and wait for
the GPU: they are for loading, tools and tests, not for every frame (per-frame data goes in `Upload` buffers). `ImmediateContext`
(`device.Immediate.Submit(recorder => …)`) is the same machinery for your own one-off GPU work, and is safe from several threads.

## Resource states and barriers

An image or buffer is always in one `ImageState` / `BufferState` (`ShaderRead`, `ColorAttachment`, `StorageReadWrite`, …), and
`ResourceStates.Describe` maps each to the Vulkan layout, pipeline stages and access mask. `CommandRecorder.Barriers` records any number of
transitions as one synchronization2 `vkCmdPipelineBarrier2`; the render graph computes them from what each pass declares.
`recorder.Label("Shadow pass")` opens a named group that shows up in RenderDoc and in validation messages.

## The bindless heap

Every pipeline has the same layout: descriptor set 0 is `device.Bindless`, three big arrays (sampled images, samplers, storage images,
partially bound and update-after-bind) that shaders index with integers, plus 128 bytes of push constants. `AddSampledImage(image)` returns
the index to read it with, `RemoveSampledImage` frees it for reuse. Slot 0 of the images and samplers is a white texel and a linear repeat
sampler, so an unset index still reads something sane.

## Shaders

Shaders are Slang. In development they are compiled at run time by `ShaderCompiler`; a **shipped game never loads the compiler**: its shaders were compiled
ahead of time by the importer (below) and are loaded from their artifacts. `ShaderCompiler` targets Vulkan 1.3 (SPIR-V 1.6) or Metal ([below](#metal-the-same-shaders)) with row-major matrices, and
entry points keep their names.

```csharp
var compiler = new ShaderCompiler(new ShaderCompilerOptions { SearchPaths = [shaderFolder], CacheDirectory = cacheFolder });
CompiledShader shader = compiler.CompileFile(Path.Combine(shaderFolder, "tonemap.slang"));   // throws ShaderCompileException with file:line
using Pipeline pipeline = device.CreateComputePipeline(shader.Code("main"), "tonemap");     // the code of the compiler's backend, as the device runs it
```

A `CompiledShader` has the code of each entry point for one backend (`Spirv(entry)`, `Msl(entry)` or `Code(entry)` for a pipeline), Slang's reflection of the program as JSON, warnings, and the list of files it read.
`CompileSource(name, text)` compiles text. **The cache** (`CacheDirectory`) keeps SPIR-V between runs (the Vulkan build only): an entry is named by a hash of the
source, the options and the Slang version, and remembers the hash of every file the compile read, so editing an imported module
invalidates it. A corrupt or unwritable cache only costs a recompile. Compiles are serialized process-wide: Slang's global session is not
safe to use from two threads at once.

### Compiled ahead of time: `ShaderBundle`

`Apogee.Runtime --import <folder>` compiles every `.slang` module that has a `[shader(…)]` entry point into `imported/<path>.ashader` (the importer is `ShaderImporter`
in Apogee.Rendering, see [Asset import](../content/import.md)). A **bundle** holds one or more *variants* of the module, each the module compiled with one set of preprocessor
defines (the variant without defines is always there; more are listed in the shader's `.import.xml`), and for each variant its reflection and the code of every entry
point **per backend**. The importer writes SPIR-V and Metal shading language into the same file; a reader takes the code of the backend of its device (`ShaderBundle.Read(…, backend)`, `ShaderLibrary.Load(path, backend: device.ShaderBackend)`) and skips the others, and the format reserves DXIL (Direct3D 12). A shader that does not compile for every backend fails the import, so it is found on the author's machine and not on a Mac. Specialization constants need no variant: they stay in the SPIR-V and are in the reflection (pipelines do not set them yet).

A bundle records `ShaderAbi.Version` (2: it added the Metal code and the bindless parameter block), which is bumped when the shared pipeline layout below changes, on any backend, and an engine refuses a bundle of another version with "import it again"
instead of crashing in the driver. That matters for mods, which may ship their own compiled shaders (a mod's folder imported with `--import`; `mod://id/imported`).

`ShaderCache` stores its entries as bundles, so there is one format on disk.

### Metal: the same shaders

`ShaderCompilerOptions.Backend = ShaderBackend.Msl` makes the compiler produce **Metal shading language source** instead of SPIR-V (text, so any machine can make it; the Metal driver compiles it when the
shader is loaded). It is the same Slang source for both backends, which is why the engine's shaders follow a few rules, checked by `MetalShaderTests`:

- **Buffers are pointers** (`Foo*` in the push constants), which Slang writes as `device Foo*` in MSL: nothing to change.
- **The push constants are Metal buffer 0** (`setBytes`), and **the bindless textures and samplers are one argument buffer, buffer 1**. In Slang they are `ParameterBlock<BindlessResources> gBindless` (arrays of
  16 384 textures and 256 samplers, which on Vulkan come out as exactly the engine's bindless descriptor set 0, bindings 0 and 1), with `gTextures` and `gSamplers` as macros for its members. Metal has no
  descriptor sets and Slang numbers buffers in declaration order, so the compiler defines `APOGEE_METAL` for the Metal build only, and the shaders pin the indices with `: register(b0)` / `: register(b1)`
  (`PUSH_SLOT`, `BINDLESS_SLOT` in `common.slang`). The Vulkan build never sees those annotations: the same `register` annotations changed the SPIR-V's bindings and crashed lavapipe.
- No other buffer, texture or sampler is bound directly: everything else is reached through an address or an index.

`AppleCompilesEveryEngineShaderToMetal` hands every entry point to Apple's compiler (`xcrun metal -c`) when it runs on a Mac with Xcode; the macOS CI job requires it.
Slang's reflection is that of the SPIR-V build; the bundle keeps the first backend's.

All pipelines share one layout (the bindless set and 128 bytes of push constants), so a pipeline is just shaders plus state.
`CreateGraphicsPipeline` takes a `GraphicsPipelineDescription`: geometry is pulled from buffers by the vertex shader (no vertex input
state), depth is reverse-Z (near plane = 1, so the compare is greater-or-equal, and the clear value is 0), blending is one of
`Opaque`, `Alpha`, `Premultiplied`, `Additive`, and rendering is dynamic rendering into attachments of the given formats.

Buffers are reached through pointers: put a buffer's `Address` in the push constants and read it in Slang as a pointer.

```slang
struct Push { float* output; uint count; float scale; };
[[vk::push_constant]] ConstantBuffer<Push> push;

[shader("compute")] [numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID) { if (id.x < push.count) push.output[id.x] = id.x * push.scale; }
```

```csharp
recorder.BindPipeline(pipeline);                  // also binds the bindless set
recorder.PushConstants(new Push { Output = buffer.Address, Count = n, Scale = 0.5f });
recorder.Dispatch((n + 63) / 64);
```

For drawing, `BeginRendering(width, height, colorTargets, depthTarget)` starts a pass and sets viewport and scissor, `Draw`/`DrawIndexed`
draw, `EndRendering` ends it; images are moved to `ColorAttachment`/`DepthAttachment` with `Barriers` first.

## Swapchain and the frame loop

```csharp
using var swapchain = device.CreateSwapchain(vsync: true);
using var loop = device.CreateFrameLoop(swapchain);

while (!window.CloseRequested)
{
    PlatformHost.PumpEvents();
    Frame? frame = loop.BeginFrame();           // null: minimized, or the swapchain was just recreated
    if (frame is null) continue;

    Backbuffer back = frame.Backbuffer!.Value;   // starts in ImageState.Undefined
    using (frame.Scope("Clear"))
    {
        frame.Recorder.Barrier(new ImageBarrier(back.Image, ImageState.Undefined, ImageState.ColorAttachment));
        frame.Recorder.BeginRendering(back.Width, back.Height, [new ColorTarget(back.View, Clear: true)]);
        frame.Recorder.EndRendering();
        frame.Recorder.Barrier(new ImageBarrier(back.Image, ImageState.ColorAttachment, ImageState.Present));
    }
    loop.EndFrame(frame);                        // submits, then presents
}
loop.WaitIdle();
```

`Swapchain` picks a UNORM format (B8G8R8A8 or R8G8B8A8; shaders write sRGB-encoded values themselves, which lets the dev overlay draw onto it
directly), FIFO presentation (mailbox or immediate without vsync), and the window's size. A resized window,
an out-of-date or suboptimal result all recreate it; a minimized window skips frames. Present semaphores are per image, so one is never
signalled while an earlier present still waits on it.

`FrameLoop` keeps two frames in flight. Each has its own command pool and buffer, upload arena, timer queries and acquire semaphore, and
one **timeline semaphore** counts finished submissions, so `BeginFrame` waits only for the frame that used its slot two frames ago.
Without a swapchain it runs **headless**: the same frames with no window image, for screenshots, tests and servers.

- `frame.Allocate(size)` gives per-frame host-visible memory with a GPU address (a 4 MB arena per frame, reset when the slot is reused):
  per-draw constants, instance data. It is valid until the GPU finishes the frame.
- `frame.Defer(resource)` disposes something once the GPU has finished the frame, which is how a transient resource a frame's commands use is freed.
- `frame.Scope("Shadow pass")` is a RenderDoc debug label plus a GPU timer; `loop.Timings` lists the sections of the most recent finished
  frame in order, with their GPU milliseconds.

The window tests open a real window, so they only run with `APOGEE_WINDOW_TESTS=1 ./build.sh test`.

## Tests

The GPU tests share one device and fail on any validation error. They skip without a GPU, unless `APOGEE_REQUIRE_VULKAN=1` (CI, on
lavapipe) or, with the Metal backend, `APOGEE_REQUIRE_METAL=1`. Run them on a specific GPU with `APOGEE_GPU=nvidia ./build.sh test` or `APOGEE_GPU=llvmpipe ./build.sh test`.
