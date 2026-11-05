# Rendering

The renderer is built on [Graphics](graphics.md): a render graph, the Forward+ lighting pipeline and the dev overlay.

## The render graph

A frame's rendering is a graph of **passes** over **images and buffers**. A pass says how it uses each resource; the graph works out what
runs, where the memory comes from, and every barrier. You build a new graph every frame (it is cheap); the memory persists in a pool.

```csharp
using var pool = new TransientPool(device);                       // lives as long as the renderer

void RenderFrame(Frame frame, Backbuffer back)
{
    var graph = new RenderGraph(device, pool);
    ImageHandle backbuffer = graph.ImportBackbuffer(back);        // starts undefined, ends ready to present
    ImageHandle hdr = graph.CreateImage("HDR color", new ImageDescription(PixelFormat.R16G16B16A16Sfloat, back.Width, back.Height,
        ImageUsage.ColorAttachment | ImageUsage.Sampled));

    graph.AddPass("Scene", PassType.Graphics, pass =>
    {
        pass.WriteColor(hdr, clear: new Vector4(0, 0, 0, 1));     // the graph begins and ends the rendering
        pass.Execute(ctx => DrawScene(ctx.Recorder));
    });
    graph.AddPass("Tonemap", PassType.Graphics, pass =>
    {
        pass.ReadSampled(hdr);
        pass.WriteColor(backbuffer, clear: Vector4.Zero);
        pass.Execute(ctx => Tonemap(ctx.Recorder, ctx.Image(hdr).SampledIndex));    // the bindless index to read it by
    });
    graph.Execute(frame);
}
```

**Declaring.** `AddPass(name, type, setup)` runs `setup` immediately; it declares uses on the `PassBuilder` and sets the callback that records
the commands (`pass.Execute`). Images: `ReadSampled`, `WriteColor`, `WriteDepth`, `ReadDepth`, `WriteStorage`, `ReadWriteStorage`, `CopyFrom`,
`CopyTo`. Buffers: `Read`, `Write`, `ReadWrite`, `CopyFrom`, `CopyTo`. The difference between *write* and *read-write* matters: a write
replaces everything (so whatever wrote it earlier is no longer needed, and a fresh transient can be written first), a read-write needs the
previous contents. Attachments are begun and ended by the graph for graphics passes, with clear (give a value) or load (give none); depth is
reverse-Z, so its default clear is 0. A pass the graph cannot see the effect of calls `NoCull()`.

**Compiling** (`graph.Compile()`, done by `Execute` if you did not) is pure and needs no GPU:

1. *Culling.* Walking back from the outputs (the backbuffer, and imports marked `output: true`), a pass runs only if it writes something that
   is needed; what it reads becomes needed. A pass that overwrites a resource completely makes earlier writers of it unneeded.
2. *Validation.* A read of something no earlier pass wrote is a `RenderGraphException`, as are one resource used two ways in a pass,
   attachments of different sizes, and attachments in a compute pass.
3. *Memory.* Transients whose lifetimes do not overlap and whose descriptions are equal share one image (buffers: equal size and usage).
4. *Barriers.* In front of each pass, one batched barrier brings each resource from its last state to the one the pass declared. Reading a
   resource again in the same state needs none; anything after a write does, even in the same state. Imported resources leave in the state
   you gave (`Present` for the backbuffer). `graph.Plan` describes the result (`ToString()` prints it for the dev overlay).

**Executing.** Each live pass runs inside `frame.Scope(name)`, so it is a RenderDoc label and a GPU timer. `PassContext` resolves handles:
`ctx.Image(h)` gives the `VkImage`, view, size and, for transients created with `Sampled` / `Storage` usage, the **bindless index** to read or
write it by; `ctx.Buffer(h)` gives the `GpuBuffer` (use `.Address` in push constants).

**Transients** come from `TransientPool`, one set per frame in flight, so a steady-state frame allocates nothing; resources unused for eight
frames are freed. A transient image with `Sampled` or `Storage` usage keeps its bindless slot for as long as it lives. Limits: images are tracked
as a whole (every mip and layer in one state), transients have one mip and no multisampling, and everything runs on the one queue.

`new RenderGraph()` without a device can only be compiled, which is how the planner is unit tested.

## Where shaders come from: `ShaderLibrary`

Renderers get their shaders from a `ShaderLibrary` (`SceneRenderer(device, shaders)`; the default is `ShaderLibrary.Standard(cacheDirectory)`, the `Shaders` folder next to the
program mounted as `shaders://`). `Load("shaders://forward.slang")` takes the compiled artifact when the folder was imported (`imported/forward.slang.ashader`, made by
`Apogee.Runtime --import`, which `./build.sh aot` runs on the published programs) and never touches the Slang compiler. A module without an artifact is compiled (development), if the
compiler library is there; if it is not, the error says to import the folder. A mod's shaders are loaded the same way from `mod://id/…` and its own `imported/`.

### Rebuilding pipelines when a shader changes

`ShaderLibrary.NotifyChanged(paths)` takes the virtual paths of changed files (`HotReloader.ShadersChanged` reports them, or a `VfsWatcher` over the shaders' folders) and raises `ModulesChanged` with the modules it
had loaded that are one of those files or were made from one (the files a compile read are in `CompiledShader.Dependencies`, the artifact's in the index), so an edit to `common.slang` reaches every shader that imports it.
`SceneRenderer` and `ImGuiRenderer` keep their pipelines in `ReloadablePipelines<T>` groups, one per shader module (the two cluster shaders are one group), and only the groups that use a changed module are rebuilt:

1. **Off the render thread.** The change starts a build on another thread: the module is compiled through the library and the pipelines are made (pipeline creation needs no external synchronization). The frame is not
   held up; the renderer keeps drawing with the pipelines it has. Changes that arrive during a build start one more right after it.
2. **Swapped at a frame boundary.** `Render` (or `AddPasses`) calls `Apply(frame)` first: if a build has finished, its pipelines become the current ones there, so a frame never mixes old and new.
3. **Destroyed when the GPU is done.** The old pipelines go through `frame.Defer`, which frees them when that frame's slot comes round again, after the frames that used them have completed. The tests record several frames
   without waiting, rebuild in the middle, and the validation layer must stay silent (it does not when the old pipelines are destroyed at once).
4. **A shader that does not compile changes nothing.** The old pipelines stay, the error (file, line, message) is logged and held in `ShaderLibrary.Errors` for the dev overlay, and the next edit that compiles clears it.
   Pipelines that are made lazily per target format are made again for the formats already seen, in the same build.

The runtime's `--watch` mounts the shaders' source folders when it runs from the repository, and the viewer's `--watch` does the same, so saving a `.slang` file changes the picture of the running program.

## Forward+

`SceneRenderer` draws a `RenderScene` (a `ModelData` on the GPU) with a `Camera`, using a render graph it builds every frame.

```csharp
using var renderer = new SceneRenderer(device, cacheDirectory: shaderCache);     // loads the shaders in <program>/Shaders (compiled ahead of time, else compiled now)
using RenderScene scene = renderer.CreateScene(GltfLoader.Load("Sponza.gltf", options));
var camera = new Camera { Position = new Vector3(0, 2, 8) };
camera.LookAt(Vector3.Zero);

Frame frame = loop.BeginFrame()!;
renderer.Render(frame, scene, camera, RenderTarget.Window(frame.Backbuffer!.Value), new RenderSettings { Exposure = 1f });
loop.EndFrame(frame);
```

**The frame.** *Depth prepass* (opaque and alpha-masked geometry into a reverse-Z D32 image) → *Build clusters* (only when the projection changed)
→ *Cull lights* → *Forward* (PBR into an RGBA16F image, opaque and masked, then blended surfaces far to near, depth-tested against the
prepass) → *Tonemap* (exposure, the Khronos PBR Neutral tone mapper, and the sRGB transfer function, into the target). `renderer.LastPlan` is
the graph and `LastStats` the draw, light and cluster counts.

**Clusters.** The view is cut into 16 x 9 cells over the screen and 24 slices in depth, spaced exponentially between the camera's `Near` and
`ClusterFar` (a slice is thin close to the camera and long far away, so every cell holds a similar amount of space as seen from the eye).
A compute shader builds each cluster's view-space box; another tests every light's sphere against every box and keeps at most 64 lights
per cluster in a fixed-size list, so there are no atomics and nothing to clear. The forward shader finds the pixel's cluster from its screen
position and depth and loops over only those lights. `ClusterGrid` has the same arithmetic on the CPU. A cluster's bounds are per
frame-in-flight and are rebuilt only when the projection changes. Spot lights are culled by their bounding sphere (the cone is applied when shading).

**Lighting.** glTF metallic-roughness materials with Cook-Torrance GGX: Smith visibility, Schlick Fresnel, Lambert diffuse, base color,
metallic-roughness, normal, occlusion and emissive textures with `KHR_texture_transform`, alpha mask and blend, double-sided, and unlit.
Lights are point and spot (`RenderScene.Lights`, editable every frame; intensity is candela, falling off as the inverse square and reaching
zero exactly at the light's radius, which is its glTF `range` or where it fades below a faint threshold) plus one directional sun
(`SunDirection`, `SunColor`) and a two-colour ambient. No shadows and no image-based lighting yet, so a smooth metal reflects only the lights.
`RenderSettings.Debug = DebugView.ClusterLights` tints each pixel by its cluster's light count (dark: none; blue to red: one to eight or more).

**Conventions.** Right-handed, +Y up, a camera looking down -Z at yaw 0. Reverse-Z with an infinite far plane: depth 1 at the near plane, 0
at infinity, compare greater-or-equal. The projection flips y (Vulkan's normalized y points down), which keeps the image and so the winding:
front faces are counter-clockwise, as in glTF. Matrices are row-major and used with row vectors, as in `System.Numerics`.

**Culling.** Before the passes are recorded, every draw's bounds (the mesh's box carried to the camera's axes, written each frame into six float arrays together with the instance) are tested against the camera's view volume
(`Frustum`: left, right, bottom, top and the near plane; the far plane is at infinity) with SIMD, and only the draws inside go into the depth and forward passes. It is exact for the planes, so
it never drops a draw that touches the view and may keep one near a corner that is not visible. `LastStats.Culled` counts what was left out, `RenderSettings.CullDraws = false` turns it off to compare
(`CullingTests` requires the same picture either way, and that a camera turned away culls everything). The measurements and the instruction set shipped games are built for are in [Profiling and SIMD](../core/profiling.md).
The per-pass lists of draws are reused from frame to frame instead of allocated.

**A double-precision world, rendered camera-relative.** `Camera.Position` and `RenderScene.Origin` are `Double3` (see [Math](../core/math.md)): `renderer.CreateScene(model, at: new Double3(5_000_000, 0, 2_000_000))`
puts a model 5 000 km out, and the model's own coordinates (instances, lights, bounds) stay relative to that origin. Nothing on the GPU is in world coordinates. Each frame:

- `Camera.View` has the camera at the origin (rotation only; `camera.WorldToView(point)` takes a world point there, subtracting in double first);
- the renderer writes the frame slot's instance records with their translation `(placement position - camera.Position) + offset`, subtracted in `double` and then narrowed, so a scene
  10 000 km away is as precise as one at the origin (the instance buffer is per frame in flight, like the cluster buffers, and grows when a frame needs more; the matrices' linear part is stored once at load);
- lights and the blend sort are moved the same way, and `GpuFrameData.CameraPosition` is zero, which the shaders (that subtract it from a surface position) need no change for.

`ForwardPlusTests.TheSceneLooksTheSameFarFromTheWorldsOrigin` renders the golden scene at the origin, at 100 km and at 10 000 km and requires the same picture.

**Data for the shaders.** One `GpuFrameData` per frame (in the frame's upload arena) holds the matrices, the screen, the sun, and the
addresses of the instance (rewritten every frame), light and cluster buffers; a draw pushes the frame's address, the instance number and the addresses of the vertices and materials of its model (a model has buffers of its own). `Shaders/common.slang`
is the Slang side of `GpuStructs.cs`; the structs use only 16-byte aligned members (and the vertex is twelve explicit floats) so that every
layout rule agrees, and tests pin the sizes and offsets.

**Tests.** The planner and the math run without a GPU. On a GPU: a GPU-vs-CPU comparison of the light lists of all 3456 clusters (every
light that is inside by a margin must be listed, every listed light inside by a margin), the sky tone-mapped exactly like the CPU twin,
lights brighten what they should and nothing beyond their radius, spot cones, the sun, alpha mask and blend, double-sided surfaces, the
heatmap, the 64-light cap, 600 lights, and a golden image rendered by the software rasterizer (`Golden/forward_plus.png`;
`APOGEE_UPDATE_GOLDEN=1` rewrites it). The discrete GPU matches it to a mean of 0.02 out of 255.

## Drawing a world of entities

A `RenderScene` is one model with its own lights. A game draws *entities*: any number of models, each placed by an entity, lit by light entities, seen through a camera entity. The pieces:

- **`GpuModel`** is a `ModelData` on the GPU: its vertex, index and material buffers, its textures in the bindless heap, and a record per mesh instance (`ModelNode`: its matrices without the translation, its box,
  its first index). It is placed any number of times at no cost but one instance record per mesh. `renderer.CreateModel(data)` makes one; a `RenderScene` is a `GpuModel` plus an origin, a sun and lights.
- **`ModelLibrary`** gives models ids: `Load(path)` returns the same id for the same path, however many entities show it, and reads the model through the virtual file system (imported `.amodel` when there is one).
  Ids exist before there is a GPU, because a game's scripts run before the renderer is made: the model is read and uploaded the first time a frame needs it (`Attach(renderer, caps)` says where), a file that does not
  load is logged once and draws nothing, and `Reload(path)` swaps a changed file in under the same id (the runtime does it for the `--watch` run, after the importer). `Detach()` frees the GPU side before the device goes.
  Scripts see the library as the global `models`: `models:load("res://packages/car/car.glb")`. It reads any mounted path, as `audio` does: the engine's classes do not know which mod called them.
- **Components** (`Components.cs`, all visible to scripts and, later, the editor): `ModelRenderer { Model }` draws the model at the entity's `WorldTransform` (position, rotation and scale place every mesh);
  `Light { Color, Intensity, Range, InnerCone, OuterCone, Kind }` is a point, spot or (kind 0) directional light shining along the entity's -Z, and the first directional one is the sun;
  `CameraComponent { FovDegrees, Near, ClusterFar }` is a lens, and the entity that also has the `MainCamera` tag is the eye. The lights a model came with move with its entity.
- **`RenderWorld(world, models)`** reads those entities when a frame is rendered (queries over `WorldTransform`, so what the systems moved this frame is what is drawn; nothing is copied or kept in sync) and
  `SceneRenderer.Render(frame, renderWorld, camera, target)` draws it. `renderWorld.TryGetMainCamera(camera)` fills a `Camera` from the main camera entity (roll is ignored: yaw, then pitch).
  `DefaultSunDirection`, `DefaultSunColor` and the ambient colours are what a world without a directional light uses.

Both kinds of scene go through one renderer core. It gathers *placements* (a model, a double-precision position, a rotation, a scale) and lights, writes one `GpuInstance` per mesh instance with the placement's rotation and scale
composed in (normals take `S^-1 * R`, so non-uniform scale lights correctly) and its translation relative to the camera, builds the boxes from the same numbers, culls, and draws by pass. A draw binds its model's index buffer when
the model changes and pushes the model's vertex and material addresses, so many models share one frame without sharing buffers.

**In the runtime.** `--game` and `--run` draw the world through its main camera when it has one, with the ImGui frame over it; the scene renderer is not even made (and its shaders not compiled) until then, so a script that
only has panels keeps drawing them over the plain background, exactly as before. Without a GPU (`--headless` without `--screenshot`) ids and entities exist and nothing is drawn.

```lua
local Transform, ModelRenderer = world:component("Transform"), world:component("ModelRenderer")
local CameraComponent, MainCamera = world:component("CameraComponent"), world:component("MainCamera")

local car = models:load("res://packages/garage/car.glb")
local e = world:create_entity("car")
world:set(e, Transform, { Position = Vector3(0, 0, 0), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
world:set(e, ModelRenderer, { Model = car })

local eye = world:create_entity("eye")
world:set(eye, Transform, { Position = Vector3(0, 1.5, 6), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
world:set(eye, CameraComponent, { FovDegrees = 60, Near = 0.05, ClusterFar = 100 })
world:add(eye, MainCamera)
```

`RenderWorldTests` check, on a GPU, that an entity draws what a scene of the same model draws, that five entities share one model (draw counts), that scale and rotation place the model, that lights and the sun
light the world, that an unknown or removed id draws nothing, that a model 3 000 km out looks the same as at the origin, that the main camera entity drives the camera, and that models load once per path and reload in place;
`WorldRenderingTests` run a game folder with a model, a light and a camera through `--headless --screenshot`.

## Debug drawing

`DebugDraw` (in `Apogee.Core`, so anything can add to it without knowing about rendering) is immediate mode: lines and triangles in double-precision world positions that live for one frame.
`Line`, `Triangle`, `Box` (turned, or from two corners), `Circle`, `Sphere`, `Arrow` and `Axes` add them; colors are bytes `r, g, b, a` as a `uint` (`DebugColor.Rgba`, `DebugColor.Red`, ...). It keeps at most `Capacity`
(a million) lines and as many triangles and counts the rest in `Dropped`, so a loop that draws too much costs detail, not memory. Physics adds to it with `PhysicsWorld.DrawDebug` ([physics](../physics/physics.md#debug-drawing));
scripts use the `debug_draw` library (`debug_draw.line(from, to, color)`, `box`, `sphere`, `arrow`, `axes`; a color is a vector of red, green and blue from 0 to 1, white without one), called every frame for as long as the picture should stay.

`RenderSettings.DebugDraw` is the instance to draw; the renderer reads it and does not clear it (whoever fills it clears it: the runtime does after every frame). When it is not empty a pass, *Debug draw*,
is added between the forward pass and tone mapping: it loads the HDR image, depth-tests against the scene's depth without writing it, and so draws over the lit picture before exposure and the tone mapper, which is why a color means
what it says (`debug_draw.slang` takes the colors as sRGB and the tone mapper encodes them back).

- **Lines** are quads of two triangles, widened to `RenderSettings.DebugLineWidth` pixels (1.5 by default) in the vertex shader: hardware line width is not portable (Metal has none). A quad cannot be left to the clipper, so the
  shader cuts a line at the near plane itself, and a line wholly behind the camera is not drawn. The part of a line that is behind something is drawn again without the depth test, at `DebugHiddenAlpha` (0.25; 0 hides it).
- **Triangles** are drawn solid with flat shading from a light at the eye, so a face looking at the camera is bright and one seen edge-on is dim.
- **Camera-relative**, like the scene: `DebugDrawRenderer` writes the positions as offsets from the camera, subtracting in double first, so a line 3 000 km out lands on the same pixels as one at the origin
  (`DebugDrawRenderingTests` render both). The buffers are per frame in flight and grow when a frame needs more.

`DebugDrawRenderingTests` read pixels back: a line is a stripe of its color and a wider one covers more, hidden parts follow `DebugHiddenAlpha`, a line behind the camera changes nothing and one crossing the near plane is cut there,
triangles are shaded, and an empty `DebugDraw` adds no pass.

