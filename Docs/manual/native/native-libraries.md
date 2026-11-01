# Native libraries

What is bound and how to use it. Where each library comes from and how it was built is in `ThirdParty/README.md`.

## Luau (`Apogee.Native.Luau`)

The VM, the compiler and the native code generator in one library, plus our layer over them, `Native/luau/apogee_luau.h`.
Luau is built with `LUA_USE_LONGJMP`: **an error is a longjmp to the nearest protected call, and a longjmp must never cross a
managed frame.** So the binding leaves out what raises an error on input a script controls: `lua_error`, `lua_call`, the
`luaL_check*`/`luaL_opt*` family, `lua_gettable`/`lua_getfield` and the other calls that run metamethods, `lua_yield`, and
`lua_pushcclosurek`. What is bound can raise only on a programming error (writing to a read-only table, pushing without
`Checkstack`) or when memory runs out, and the layer deals with that one.

```csharp
State* state = Lua.NewVm(memoryLimit: 64 * 1024 * 1024);          // allocator, interrupt and accounting installed
Lua.LuaLOpenlibs(state);
Lua.PushManagedFunction(state, &Add, name, 0);                       // [UnmanagedCallersOnly] static int Add(State*)
Lua.Rawsetfield(state, Lua.Globalsindex, name);
if (Lua.LoadSource(state, chunk, source, size, 0) == CallResult.Ok)  // compiles, loads, native code for --!native chunks
    result = Lua.ProtectedCall(state, 0, 1, budgetMilliseconds);     // traceback on errors, time budget, memory errors
Lua.CloseVm(state);
```

- **A managed function** returns its number of results, or `Lua.Raise` with the error value on top of the stack; the wrapper
  turns that into `lua_error` once managed code has returned. Its own upvalues start at index 2 (`Globalsindex - 2`).
- **Memory:** `NewVm` takes a hard limit (`SetMemoryLimit` changes it: set it after the libraries are open). An allocation over it
  fails, and Luau raises the memory error, only while Luau code is the topmost thing running. One made by a managed function, or by
  the host outside a call, is never refused (nothing could catch the error, or it would unwind managed code); the error is raised
  when the managed function returns.
  `SetCategoryLimit` is a soft limit for a memory category (`Setmemcat`), raised at the next call or loop iteration, which is
  how each script owner gets its own cap in one VM.
- **Time:** the budget of `ProtectedCall`/`ProtectedResume` (or `SetTimeBudget`) is armed by the outermost call. Once it has
  passed every call and loop iteration raises, so `pcall` in a script cannot swallow it. A long native function (`string.rep`)
  is not interrupted.
- **Bytecode** from `LuauCompile` is freed with `Lua.Free`: it comes from the library's C runtime, which is not
  necessarily the caller's. `LoadSource` keeps it inside the library.
- **Native code** (`--!native`, `Luau::CodeGen`) is on where the machine supports it (x64 and arm64); `NativeEnabled` says. It
  needs executable memory: a macOS app that is signed with the hardened runtime needs the `allow-jit` entitlement.
- One state belongs to one thread at a time.

## Vulkan Memory Allocator (`Apogee.Native.Vma`)

VMA suballocates GPU memory. Its binding is `Vma` (functions) plus `Allocator`, `Allocation`, `Pool`… (the opaque structs
behind VMA's handles; you only hold pointers to them).

```csharp
var functions = new VulkanFunctions { VkGetInstanceProcAddr = …, VkGetDeviceProcAddr = … };
var info = new AllocatorCreateInfo
{
    PhysicalDevice = physicalDevice, Device = device, Instance = instance,
    VulkanApiVersion = Vk.ApiVersion13, PVulkanFunctions = &functions,
};
Allocator* allocator;
Vma.CreateAllocator(&info, &allocator);
```

The library is built without static Vulkan functions: it loads the rest through the two pointers you pass, so it works with
whatever Vulkan loader the process uses. Flag typedefs are the `*FlagBits` enums (`AllocationCreateFlagBits`), already
`[Flags]`.

`VirtualBlock` needs no GPU: it hands out offsets from a range (used for sub-allocating our own buffers, and in tests).

## meshoptimizer (`Apogee.Native.Meshopt`)

Mesh processing: vertex deduplication, vertex cache / overdraw / vertex fetch optimization, simplification, meshlets and
quantization. The binding is `Meshopt` and keeps the C spelling of the parameters. The content loader runs every mesh
through it at import: `GenerateVertexRemap` → `RemapIndexBuffer`/`RemapVertexBuffer` → `OptimizeVertexCache` →
`OptimizeVertexFetch`.

```csharp
nuint unique = Meshopt.GenerateVertexRemap(remap, indices, indexCount, vertices, vertexCount, vertexSize);
Meshopt.RemapIndexBuffer(indices, indices, indexCount, remap);
Meshopt.OptimizeVertexCache(indices, indices, indexCount, unique);
```

## zstd (`Apogee.Native.Zstd`)

Compression, for packs and for libktx's KTX 2 supercompression. The binding is `Zstd` and covers the stable API of `zstd.h` and `zstd_errors.h`: one-shot
`Compress`/`Decompress` (with `CompressBound` and `GetFrameContentSize` to size the buffers), streaming through `CompressStream2`/`DecompressStream` with
`InBuffer`/`OutBuffer`, contexts (`CreateCCtx`, `CCtxSetParameter`), and dictionaries (`CreateCDict`/`CreateDDict`). Functions that fail return a size that
`IsError` recognizes; `GetErrorName` and `GetErrorCode` say what happened. The library is built with worker threads, so `CParameter.NbWorkers` compresses on several
threads.

```csharp
byte[] frame = new byte[(int)Zstd.CompressBound((nuint)data.Length)];
nuint size = Zstd.Compress(target, (nuint)frame.Length, source, (nuint)data.Length, 3);
if (Zstd.IsError(size) != 0) throw new InvalidDataException(Marshal.PtrToStringUTF8((IntPtr)Zstd.GetErrorName(size)));
```

`Zstd.ContentSizeUnknown` and `ContentSizeError` are what `GetFrameContentSize` returns for a frame that does not say its size and for non-frames.
libktx links this library (they have to sit next to each other, which the build arranges), so a zstd-compressed KTX 2 file is inflated by the same code.

## FreeType (`Apogee.Native.FreeType`)

Font rasterization, for the dev tools' text now (Dear ImGui's FreeType loader) and for RmlUi's. The binding is class `FT` over the public headers of the
modules we build: loading a face (`NewMemoryFace`, `NewFace`), sizes, glyph loading and rendering (`LoadChar`, `RenderGlyph`, with the `Load…` flags and the `RenderMode`
enum: anti-aliased, light, monochrome, LCD and signed distance fields), outlines, strokes, glyph and bitmap helpers, variable fonts (`ftmm`) and colour tables. The names
drop the `FT_` prefix (`FT_Init_FreeType` is `FT.InitFreeType`); `FT_Library` is `LibraryRec*` and `FT_Face` is `FtFaceRec*`; a C `long` is `CLong`.

```csharp
LibraryRec* library;
FT.InitFreeType(&library);
FtFaceRec* face;
fixed (byte* font = fontBytes)   // the face reads this memory for as long as it lives: keep it pinned (or in native memory)
{
    FT.NewMemoryFace(library, font, new CLong(fontBytes.Length), new CLong(0), &face);
    FT.SetPixelSizes(face, 0, 16);
    FT.LoadChar(face, new CULong('A'), FT.LoadRender);
    Bitmap bitmap = face->Glyph->Bitmap;               // Rows, Width, Pitch, Buffer: 8-bit coverage
}
```

Every function returns an `FT_Error`: zero is success. The modules are TrueType and OpenType (`tt`, `cff`, `sfnt`), Type 1, CID and Type 42, the PostScript helpers, the auto-hinter, and the
smooth, monochrome and SDF renderers; zlib is FreeType's own copy (WOFF 1 fonts and `.gz` files). Left out: the bitmap and Windows fonts, PFR, the SVG and colour-PNG
glyphs, WOFF 2 (brotli) and HarfBuzz; `ThirdParty/README.md` lists them and how the library is configured (`Native/freetype/apogee_ftoption.h`, `apogee_ftmodule.h`).
FreeType is used under the **FreeType License** (BSD-style, with a credit clause: a game that ships the library says so in its credits, see `Legal/THIRD_PARTY_NOTICES.md`).
Dear ImGui's font atlas is rasterized by it: `cimgui` links this library (they sit next to each other) and ImGui makes it the default font loader.
`FreeTypeSmokeTests` renders a glyph of a font loaded from memory through every renderer, and the common binding tests check the entry points and struct sizes.

## cgltf (`Apogee.Native.Cgltf`)

A glTF 2.0 parser (`.gltf` and `.glb`). `Cgltf.Parse` reads a file from memory and `Cgltf.ParseFile` from a path; both give a
`Data*` that owns the parsed document and that you release with `Cgltf.Free`. `Cgltf.LoadBuffers` reads the binary buffers
(external files and data URIs), `Cgltf.Validate` checks the document, and `Cgltf.AccessorUnpackFloats` /
`AccessorReadIndex` read accessor data whatever its component type.

```csharp
var options = new Options();
Data* data;
if (Cgltf.Parse(&options, bytes, size, &data) == Result.Success && Cgltf.LoadBuffers(&options, data, path) == Result.Success)
{
    Accessor* positions = data->Meshes[0].Primitives[0].Attributes[0].Data;
    Cgltf.AccessorUnpackFloats(positions, destination, floatCount);
}
Cgltf.Free(data);
```

The names are the C names without `cgltf_`; its booleans are plain `int`s.

!!! warning "Keep the parsed memory alive"
    For a `.glb` cgltf's buffers, and for any file its strings, point into the memory you passed to `Parse`, so that memory has to stay
    valid until `Free`. Pinning a managed array only for the call works under CoreCLR by luck (the array does not move) and breaks under
    NativeAOT. `GltfLoader` copies the file into native memory and frees it after `Free`.

## stb_image (`Apogee.Native.Stb`)

Decodes the common image formats (PNG, JPEG, BMP, TGA, GIF, PSD, HDR, PIC, PNM) from memory. The class is `Stbi`.

```csharp
int width, height, channelsInFile;
byte* pixels = Stbi.LoadFromMemory(data, length, &width, &height, &channelsInFile, Stbi.RgbAlpha);
if (pixels == null) throw new InvalidDataException(Marshal.PtrToStringUTF8((IntPtr)Stbi.FailureReason()));
// …upload width * height * 4 bytes…
Stbi.ImageFree(pixels);
```

Asking for `Stbi.RgbAlpha` always gives four 8-bit channels, whatever the file has. `Stbi.InfoFromMemory` reads the size without
decoding. There is no file API: read the bytes through the engine's file system first.

## libktx (`Apogee.Native.Ktx`)

Reads KTX 1 and KTX 2 textures, and writes KTX 2 with the Basis Universal encoder. Basis Universal data (ETC1S/BasisLZ and UASTC) is transcoded to whatever the GPU supports. The asset importer
encodes UASTC with `ktxTexture2_CompressBasisEx` and supercompresses it with `ktxTexture2_DeflateZstd` (`BasisEncoder`, [Asset import](../content/import.md#gpu-texture-compression)); the game only loads, but the encoder is
in the same library (libktx is 2.1 MB on Linux with it; a shipped game never calls the encoder). Not built: the ASTC encoder (UASTC is transcoded to ASTC instead), and the OpenGL/Vulkan upload (the renderer uploads the bytes itself).

```csharp
Texture2* texture;
Ktx.Texture2CreateFromMemory(bytes, size, TextureCreateFlagBits.LoadImageData, &texture);
if (Ktx.Texture2NeedsTranscoding(texture))
{
    // BC7 on desktop GPUs, ASTC 4x4 on mobile and Apple, RGBA8 as the fallback that always works.
    Ktx.Texture2TranscodeBasis(texture, TranscodeFmt.TtfBc7Rgba, 0);
}
nuint offset;
Ktx.Texture2GetImageOffset(texture, level, layer, face, &offset);   // texture->PData + offset: that level's bytes, texture->VkFormat: their format
Ktx.Texture2Destroy(texture);
```

Names are the C names without `ktx`, with the `_` after `Texture2` dropped (`Texture2CreateFromMemory`); enums lose `_e`.

## Slang (`Apogee.Native.Slang`)

The shader compiler. Slang's own API is C++ and COM, so the binding is over a small C API of ours (`Native/slang_c`) that compiles Slang
source to SPIR-V for Vulkan 1.3 (SPIR-V 1.6, row-major matrices, entry points keep their names). The compiler library ships with the
game: shaders compile at run time, and a mod can bring its own `.slang` files.

```csharp
Session* session;
var desc = new SessionDesc { SearchPaths = paths, SearchPathCount = pathCount, Optimization = 1 };
Slang.SessionCreate(&desc, &session);

Result* result;
Slang.Compile(session, moduleName, path, source, null, 0, &result);   // entry points: all the module defines
if (Slang.ResultStatus(result) < 0) Log.Error("Shaders", Marshal.PtrToStringUTF8((IntPtr)Slang.ResultDiagnostics(result)));
byte* spirv = (byte*)Slang.ResultSpirv(result, 0, &size);              // entry point 0
Slang.ResultDestroy(result);
```

Passing `source = null` loads `<moduleName>.slang` from the search paths. Besides the code, a result carries the diagnostics (warnings too), the
program's reflection as Slang's JSON, and the files the compile read, which is what a shader cache keys on.

## Jolt Physics (`Apogee.Native.Jolt`)

Jolt Physics, behind **our own C API**, `Native/jolt/apogee_jolt.h` (about 80 functions, written for what the engine needs; no third-party wrapper), in one shared library, `jolt`. The binding is a class `Jolt`
(`Jolt.WorldCreate`, `Jolt.BodyCreate`) with structs that keep their `Aj` prefix (`AjBodyDesc`, `AjVec3`, `AjWorld*`) because `BodyDesc`, `VehicleDesc` and `World` are the engine's own names for other things.
[Physics](../physics/physics.md) is the engine's use of it; this is the raw binding.

The API is handles and plain structs: a **world** owns its job system, temp allocator, collision layer filters and event queue; **shapes** are reference counted (`ShapeBox`, `ShapeMesh`, `ShapeStaticCompound`,
`MutableCompound`… each returns one reference that `ShapeRelease` gives back); **bodies** are made from an `AjBodyDesc` (layers, mask, motion, mass, damping, sensor, events, bullet) and are 32-bit ids; there are
queries (`Raycast`, `CastSphere`, `OverlapSphere`, `OverlapBox`, with a layer mask and an ignored body, filtered natively), a **character** (`CharacterCreate`, `CharacterStep`), **vehicles** (`VehicleCreate` from a flat
`AjVehicleDesc`, then `VehicleSetGearbox`, `VehicleDriveWheeled`, `VehicleGetWheelState`…) and a **drawbar** joint. Nothing throws; functions that can fail return false or null.

How it is built decides what the binding means, so these are fixed in `Native/jolt/CMakeLists.txt`:

- **Double precision** (`JPH_DOUBLE_PRECISION`): `AjRVec3` is three doubles, and so are the positions in every call (`BodyGetPose`, `Raycast`, `AjEvent.Position`). Rotations, scales, velocities and everything else
  are floats. It is the physics side of the double world (see [Math](../core/math.md)).
- **Cross-platform determinism** (`JPH_CROSS_PLATFORM_DETERMINISTIC`): the same inputs give the same results on Windows, Linux and macOS, on any CPU and with any number of threads (no fused multiply-add, strict float
  contraction). It costs about 8% of the simulation. `Tests/Apogee.Native.Tests/JoltSmokeTests` simulates a pile of bodies and compares the bits of their final positions with a recorded hash; it was the same on SSE 4.2 and
  AVX2 builds, with 1, 2 and 7 threads, and before and after the move from JoltC to our own API.
- **32-bit object layers** (`JPH_OBJECT_LAYER_BITS=32`): the low 16 bits are a body's group (its 14 layers, plus a marker for static and one for sensor bodies, which only choose the broad phase tree), the high 16 its
  mask. Two bodies collide when each is in a layer the other's mask holds; static bodies never collide with each other.
- **Instruction set:** SSE 4.2 (the engine's x86-64-v2 baseline). `cmake -DAPOGEE_JOLT_AVX2=ON` builds for AVX2, BMI, LZCNT and F16C, any CPU since Haswell, and Jolt's code is then faster; the library picks its
  instruction set when it is built, there is no run-time dispatch. FMA is never used.
- No C++ exceptions and no RTTI, as Jolt does not use them, and none may cross managed frames. No GPU compute, no profiler, no assertions in Release. The debug renderer is compiled in: `aj_world_draw_debug` collects what it draws (see [Physics](../physics/physics.md#debug-drawing)).

```csharp
Jolt.Init();                                                     // registers Jolt's types; Jolt.Shutdown() at the end
var desc = new AjWorldDesc { MaxBodies = 10240, MaxBodyPairs = 65536, MaxContactConstraints = 20480, TempBufferBytes = 32 << 20, Threads = 4, EventCapacity = 4096,
                             VelocitySteps = 10, PositionSteps = 2, Gravity = new AjVec3 { Y = -9.81f } };
AjWorld* world = Jolt.WorldCreate(&desc);
AjShape* ball = Jolt.ShapeSphere(0.5f);
uint body = Jolt.BodyCreate(world, &bodyDesc);                   // bodyDesc.Shape = ball
Jolt.WorldUpdate(world, 1f / 60);                                // one collision step
```

**Events.** Jolt calls its contact and activation listeners on its worker threads, inside `WorldUpdate`, where managed code must not run. The world's listeners only append to a queue in native memory (a mutex, and a
fixed capacity: what does not fit is counted, `EventsDropped`), and `EventsTake` copies them out after the update:

- `ContactAdded`: two bodies started touching; the point (a double position), normal and depth are the first contact point's. `ContactRemoved`: they stopped (also when one of them goes to sleep: Jolt ends the contacts of a sleeping body).
- `BodyActivated` and `BodyDeactivated`.
- Only bodies that ask are reported (`AjBodyDesc.Events`); a **sensor** always reports its contacts, with `Jolt.EventSensor` in the flags. The `UserData` of a body is yours, whole, and comes back in the event.
- `IgnorePair(a, b)` makes two bodies pass through each other (a truck and its trailer) until `AllowPair`; the world's contact validation looks the pair up only while some pair is ignored.

A step that needs more working memory than `TempBufferBytes` allocates from the heap instead of failing.

## RmlUi (`Apogee.Native.RmlUi`)

RmlUi 6.3 (HTML and CSS style game UI), behind **our own C API**, `Native/rmlui/apogee_rmlui.h` (85 functions), in one shared library, `rmlui`. RmlUi's own API is C++ with inheritance, templates and ownership by `unique_ptr`,
which a C# program cannot call, so everything goes through handles and plain structs. RmlUi is compiled into the library as a static one would be (`RMLUI_STATIC_LIB`, hidden symbols): the library exports what the header declares and nothing
else. Its font engine is FreeType, our own `freetype` library (it has to sit next to this one). The binding is a class `Rml` (`Rml.ContextCreate`, `Rml.ElementAppendChild`) with `Ar`-prefixed structs (`ArHost`, `ArVertex`, `ArRect`).
[UI](../ui/index.md) is the engine's use of it; this is the raw binding.

- **The host.** RmlUi needs a few things from its host, and `ArHost` is how it gets them: a table of function pointers (a C# program fills it with `[UnmanagedCallersOnly]` methods). *Drawing*: RmlUi compiles geometry
  (`ArVertex` lists, 20 bytes each, with the alpha already multiplied into the color) and asks for it to be drawn with a translation and a texture, under a scissor rectangle and a transform; textures come from `load_texture` (an
  image path) or `generate_texture` (RGBA pixels, for font atlases). *System*: time, a log, the mouse cursor, the clipboard. *Files*: `read_file` hands over a whole file to the library (`ar_sink_write`), which serves RmlUi's
  reads and seeks from memory, so every document, stylesheet and font is read through whatever file system the host has. Any function may be NULL; its work is then skipped (no drawing, no files).
- **Elements are handles**, 64 bits with a generation, not pointers. A slot watches its element with RmlUi's observer pointer, so a handle to an element that has been destroyed (it was in a document that was unloaded,
  or in a branch someone destroyed) answers "not alive" and every call refuses it; a released slot that is used again does not answer to the old handle. An element made with `DocumentCreateElement` or `DocumentCreateText` is
  owned by its handle until `ElementAppendChild` or `ElementInsertBefore` puts it in a tree (RmlUi's `ElementPtr` rule), `ElementRemoveChild` gives it back, `ElementDestroy` ends it. Handles that calls return are the caller's
  to `ElementRelease`; so are the ones an event gives (`EventTarget`, `EventCurrent`).
- **Events.** `ElementAddListener(element, "click", capture)` returns a token, and every event reaches the host's one `on_event(user, token, event)` synchronously, with the type, target, current element, phase and the event's
  parameters (`EventGetString`, `EventGetNumber`: `mouse_x`, `key_identifier`, `value`...), and `EventStopPropagation`. A listener dies with its element or at `ElementRemoveListener`; the only place one is freed is RmlUi's `OnDetach`.
- **Text results** (`ElementGetId`, `TextGet`...) are written into a caller's buffer with the length the whole text needs, like `snprintf`; `-1` is "no such thing".
- **Input** is `ContextMouseMove`, `ContextMouseButton`, `ContextMouseWheel`, `ContextKey` (`ArKey` is RmlUi's key list, its values checked at compile time) and `ContextText`; each returns false when a document consumed the input.
- **Scripts in documents.** Every document is an `ArDocument`, so that `<script>` and `<script src>` reach the host (`load_inline_script`, `load_external_script`, with a document id that is the same for every call about the document
  until `document_closed`) and `on*` attributes become listeners whose code the host compiles (`compile_inline_handler`, when the first event comes: the attribute is read before the element is in its document). RmlUi runs the
  scripts of a document when its `<head>` is read, before the body.
- **Data models.** `DataModelCreate`, `DataModelBind` (a top-level variable, by `ref` and `ArDataKind`), `DataModelBindEvent` (for `data-event-*`), `DataModelDirty`, `DataModelIsDirty` and `DataModelRemove`. The values stay the
  host's: RmlUi asks `data_get`, `data_set`, `data_size` and `data_child` with the refs the host gave out, so one `VariableDefinition` per kind serves every variable. `ArVariant` carries the value (nil, bool, integer, double, string).
- **A base stylesheet.** RmlUi has no user-agent stylesheet: a `div` is `display: inline` and ignores its width and height until a stylesheet says `div { display: block }`. `SetBaseStyle` gives it one, put under the rules of every
  document loaded afterwards (in its head, so the text the body makes already has a font); the tests of this binding that do not set one write the rule themselves.
- **Fonts** are copied (`LoadFont` keeps its own copy until after shutdown, which RmlUi requires of the memory).
- **Effects.** The host gets what RmlUi 6 asks of a render interface beyond geometry: `enable_clip_mask` and `render_to_clip_mask` (an `ArClipOperation`, geometry and translation), layers (`push_layer`, `composite_layers` with the
  filter handles and a blend mode, `pop_layer`), `save_layer_as_texture` and `save_layer_as_mask_image`, filters (`compile_filter` gets an `ArFilterDesc`: a blur, a drop shadow, or a 4x5 color matrix that opacity, brightness,
  contrast, invert, grayscale, sepia, saturate and hue-rotate are turned into) and shaders (`compile_shader` gets an `ArShaderDesc`: a linear, radial or conic gradient with its stops), with `render_shader` and the matching releases.
  Every callback is optional: one left null is a feature RmlUi then draws without. `backdrop-filter` is not forwarded.

```csharp
var host = new ArHost { Size = (uint)sizeof(ArHost), User = state, CompileGeometry = &CompileGeometry, RenderGeometry = &RenderGeometry, /* ... */ ReadFile = &ReadFile, OnEvent = &OnEvent };
Rml.Initialize(&host);                                           // once; Rml.Shutdown() at the end
ArContext* context = Rml.ContextCreate(name, 1280, 720);
ulong document = Rml.ContextLoadDocumentText(context, rml, url);
Rml.DocumentShow(document);
Rml.ContextUpdate(context);                                      // layout and animations
Rml.ContextRender(context);                                      // calls host.RenderGeometry for what is visible
```

`Tests/Apogee.Native.Tests/RmlUiSmokeTests` runs all of it against a recording host: layout in pixels, the geometry and its colors, text with a real font, building and editing a tree (and what a stale handle does), listeners
(bubbling, capture, stopping, removal, parameters), clicking by pointer position, files and stylesheets through the host, images, added stylesheets, typing into a text field, the debugger, resizing.

## ImGuizmo, ImPlot and imnodes

Dear ImGui add-ons for the dev tools and the editor: ImGuizmo (3D manipulation gizmos and a view cube), ImPlot (plots) and imnodes (node editors). All three use ImGui's C++ API and its context, and the symbols of ImGui are
hidden in `cimgui`, so they are compiled **into the `cimgui` library** (`Native/imgui/CMakeLists.txt`), each with **a C API of ours** over it (`apogee_imguizmo.h`, `apogee_implot.h`, `apogee_imnodes.h`: plain
functions over floats, ints and opaque handles, no C++ types, no `cimguizmo` / `cimplot` / `cimnodes`). Three binding projects are generated from those headers, all on the library name `cimgui`:
`Apogee.Native.ImGuizmo` (class `Gizmo`, functions `Gizmo.Manipulate`, `Gizmo.ViewManipulate`...), `Apogee.Native.ImPlot` (class `Plot`: `Plot.BeginPlot`, `Plot.PlotLineF`...) and `Apogee.Native.ImNodes`
(class `Nodes`: `Nodes.BeginNode`, `Nodes.Link`...). Their enums are checked against the library's own at compile time (`static_assert`s in the shims).

- **They act on ImGui's current context**, which is in the same library: make ImGui's context current, create an ImPlot and an imnodes context (`Plot.CreateContext`, `Nodes.CreateContext`), and make those current too.
  `ImGuiHost` does all of that.
- **ImPlot's templates** are exported as `_f` (float) and `_d` (double) functions: `Plot.PlotLineF(label, values, count, xScale, xStart, spec)` and `Plot.PlotLineXyD(label, xs, ys, count, spec)`, and the same for shaded areas
  (`PlotShadedBetweenF` is an area between two lines), bars and scatter. `AplotSpec` is the look of an item; `Plot.DefaultSpec` fills it with automatic colors.
- **ImGuizmo's** matrices are 16 floats, column-major; `Gizmo.Manipulate` changes the object's matrix in place and can give the change since the drag began (`deltaMatrix`).
- **imnodes'** ids are the caller's integers: nodes, attributes (pins) and links each have their own.
- The library exports exactly the three headers and ImGui's `cimgui.h`: `nm -D libcimgui.so | grep -c _Z` is 0, no C++ symbol of ImGui, ImPlot or imnodes leaks.
- Tests: the three bindings are in `NativeBindingTests` (every entry point resolves; they have no struct of a known size but ImPlot's `AplotSpec`), and `Tests/Apogee.DevTools.Tests/ImGuiExtensionTests` runs a headless frame through each.

## OpenAL Soft (`Apogee.Native.OpenAL`)

OpenAL 1.1 with OpenAL Soft's extensions, through its C API with no shim: class `OpenAL`. OpenAL Soft is LGPL, so it is its own shared library beside the program (see `Legal/THIRD_PARTY_NOTICES.md`); [Audio](../audio/index.md) is built on it.

- Names keep the C names with the first letter in capitals: `alGenBuffers` is `OpenAL.AlGenBuffers`, `alcOpenDevice` is `OpenAL.AlcOpenDevice`, `AL_SOURCE_RELATIVE` is `OpenAL.AlSourceRelative`. The extensions OpenAL Soft exports as
  plain functions are bound too (`AL_ALEXT_PROTOTYPES`): EFX effects, filters and auxiliary slots (`AlGenEffects`, `AlFilterf`), and `ALC_SOFT_loopback` (`AlcLoopbackOpenDeviceSOFT`, `AlcRenderSamplesSOFT`), which renders to memory
  with no sound card.
- Left out: the direct-context variants (`...Direct`), EAX, the debug extension, the functions the header declares that the library does not export, and the effect parameters' default, minimum and maximum (float macros).
- Every call can leave an error that is read with `AlGetError` / `AlcGetError`; the audio system checks them where a failure matters.

```csharp
ALCdevice* device = OpenAL.AlcLoopbackOpenDeviceSOFT(null);          // or AlcOpenDevice(null) for the speakers
int[] attributes = [OpenAL.AlcFormatChannelsSoft, OpenAL.AlcStereoSoft, OpenAL.AlcFormatTypeSoft, OpenAL.AlcFloatSoft, OpenAL.AlcFrequency, 48000, 0];
fixed (int* a = attributes) { context = OpenAL.AlcCreateContext(device, a); }
OpenAL.AlcMakeContextCurrent(context);
// ... buffers, sources, then, once per update on a loopback device:
OpenAL.AlcRenderSamplesSOFT(device, mix, 1024);
```

## Audio decoders (`Apogee.Native.AudioDecode`)

WAV, FLAC, Ogg Vorbis and MP3 behind four functions of ours (`Native/audio_decode/apogee_audio.h`, built from dr_libs and stb_vorbis): `Open` takes a whole file in memory and tells the format from its first bytes, `Read` gives interleaved 32-bit float
samples at the file's channel count and sample rate, `Rewind` starts over, `Close` ends it. The memory must stay valid until `Close`. The files come from the engine's file system, so the decoders never touch the disk.

```csharp
AaInfo info;
AaDecoder* decoder = AudioDecode.Open(bytes, (nuint)length, &info);   // null if it is not audio we can read
ulong frames = AudioDecode.Read(decoder, samples, 4096);              // 0 at the end
AudioDecode.Close(decoder);
```

## GameNetworkingSockets (`Apogee.Native.Net`)

Valve's GameNetworkingSockets behind our own small C API (`Native/net/apogee_net.h`, shim `apogee_net`), class `Net`: GNS's flat C API takes C++ references and structs packed differently by platform, which the shim's compiler lays out
instead. A host owns a listen socket and connections (GNS's handles, as `uint`); `Poll` returns what happened as `AnEvent`s (`Incoming`, `Connected`, `Message`, `Disconnected`), whose message bytes and strings stay valid until the
next `Poll`, so managed code never runs inside a GNS callback or on one of its threads. [Network](../network/network.md) is built on it; `Tests/Apogee.Network.Tests` is its test. Without `libGameNetworkingSockets` next to it the shim does not
load (`NativeLibraries.TryLoad` fails) and networking is off.

```csharp
Net.Init(error, size);                       // counted: every Init needs a Shutdown
AnHost* host = Net.HostCreate();
ushort port; Net.Listen(host, 27020, &port);
uint connection = Net.Connect(host, "127.0.0.1:27020"u8);
AnEvent* events = ...; int n = Net.Poll(host, events, 256);   // once a frame
Net.Send(host, connection, bytes, length, reliable: true, lane: 0);
```

## Steamworks (`Apogee.Native.Steam`)

Valve's flat C API (`steam_api.dll` / `libsteam_api.so` / `libsteam_api.dylib`), class `Steam`. The three libraries are Valve's `redistributable_bin`, committed unchanged (Git LFS, `ThirdParty/steamworks/lib`); the SDK
headers are not (Valve's agreement lets us distribute only the redistributables), so the *bindings* are generated code and committed, and the headers are needed only to regenerate them (`./build.sh sdk steam <zip>`, then
`./build.sh bindings steam`). Without the library `NativeLibraries.TryLoad` fails and [Steam](../network/steam.md) is off.

The headers are C++, and the bindings are generated from what Valve publishes for other languages instead: `steam_api.json` (interfaces, structs, enums, constants) plus the `S_API` function declarations in
`steam_api_flat.h`, `steam_api.h` and `steam_api_common.h`, with libclang measuring the structs (`"kind": "steam"`, `SteamApiParser`).

- **Interfaces are opaque pointers.** `Steam.SteamUserV023()` returns an `ISteamUser*` that Steam owns; its methods take it first (`Steam.UserGetSteamID(user)`, the flat API's `SteamAPI_ISteamUser_GetSteamID`).
  Only the interfaces in `steam.json` are bound (user, friends, utils, apps, user stats, remote storage, UGC, screenshots, timeline); networking, matchmaking, Steam Input and the game server API are left out.
- **Callbacks use manual dispatch** (`ManualDispatchInit`, `ManualDispatchRunFrame`, `ManualDispatchGetNextCallback` into a `CallbackMsg`, `ManualDispatchGetAPICallResult`), so nothing runs on Steam's threads.
  Every callback struct has its id as a constant (`Steam.GameOverlayActivatedId`).
- **Packing.** Valve packs its callback structs to 4 bytes on Linux and macOS and to 8 on Windows, and a few structs are `#pragma pack(4)` or `(2)` on every system. The structs that depend on the system say
  `Pack = SteamPack.Callback`, a constant chosen when the project is built (`APOGEE_STEAM_PACK8` for a Windows build), and the layout test checks the size clang computes for both packings
  (`SteamLayout.CallbackPackSizes`). Build the binding for the operating system it runs on.
- Names lose Valve's Hungarian prefixes (`m_unAppID` is `AppID`, `m_eResult` is `Result`); `_t` and the `E`/`I` prefixes of enums and interfaces stay as Valve writes them.
- Constants that are plain numbers (or `a * b`, `a + b`) are bound; strings and macros are skipped (`Bin/bindings/steam.skipped.txt`).

```csharp
byte* message = stackalloc byte[1024];
if (Steam.InitFlat(message) == ESteamAPIInitResult.OK)       // Steam client running, steam_appid.txt next to the program in development
{
    Steam.ManualDispatchInit();
    ISteamUser* user = Steam.SteamUserV023();
    ulong id = Steam.UserGetSteamID(user);
}
```
