# Third-party code

Vendored libraries: headers (for the bindings generator) and, where noted, prebuilt binaries. Each entry says where it
comes from and how it was built. Update one with its recipe, not by hand.

Everything under `<lib>/lib/` (the prebuilt binaries, except `SHA256SUMS`) is a Git LFS object; run `git lfs install` once
and clone as usual. CI pulls only the files for its own platform (`git lfs pull --include=…`). Only the per-platform directories
`lib/<os>-<arch>/` are LFS objects; a library's own `lib/` source directory is not.

A native build must not depend on headers that happen to be installed on the developer's machine (that is how `vk_video/*` and
`GL/*` went missing on CI). After adding or updating a library, build clean and check what resolved outside the repository:
`rm -rf Bin/native-build && ./build.sh native && ninja -C Bin/native-build -t deps | grep -v ThirdParty` should list only libc,
libstdc++ and compiler headers.

## Proprietary SDK (Steamworks)

Steamworks is not ours to publish, and the repository is planned to become open source, so it is handled the way its license allows. `./build.sh sdk steam <zip>` (`Tools/Apogee.Build/SdkCommand.cs`) unpacks exactly
the files the engine uses. Download the archive from https://partner.steamgames.com/downloads/list (needs a Steamworks partner account). Everything else here may be redistributed, so a clone builds and runs with
nothing to download. (FMOD was handled the same way until it was replaced by OpenAL Soft.)

## steamworks (redistributables committed, headers not)

- Upstream: https://partner.steamgames.com, Steamworks SDK 1.65 (`steamworks_sdk_165.zip`, 2026-07-23)
- License: the Steamworks SDK Access Agreement (https://partner.steamgames.com/documentation/sdk_access_agreement). Section 1.1 lets us reproduce the SDK "in source code form, solely to develop" a game and **distribute only the
  `redistributable_bin` folder** in object code. So the three libraries are committed (Git LFS, checksums in `lib/SHA256SUMS`) and the SDK headers are not: `ThirdParty/steamworks/include/` is git-ignored, filled by `./build.sh sdk steam`,
  and needed only to regenerate the bindings.
- Vendored: `lib/linux-x64/libsteam_api.so`, `lib/win-x64/steam_api64.dll` and `lib/osx-universal/libsteam_api.dylib` (arm64 + x86_64), unchanged, from `sdk/redistributable_bin/{linux64,win64,osx}`. Not vendored: the 32-bit and
  Linux/Android arm64 builds, the import libraries, `sdkencryptedappticket`, and the tools.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/steam.json` into `Source/Apogee.Native.Steam`, generated from the flat API (`steam_api_flat.h`) and committed.
- A game still needs its own Steamworks app (AppID) and Valve's agreement; `steam_appid.txt` next to the program is for development only.

## sdl

- Upstream: https://github.com/libsdl-org/SDL
- Version: 3.4.18 (`release-3.4.18`, 829a65d769d935c4852f8159e964312c0957260a, 2026-10-02)
- License: Zlib (`sdl/LICENSE.txt`)
- Vendored: `include/` (SDL3 headers, from the tag's tarball) and `lib/<rid>/` (checksums in `lib/SHA256SUMS`):
  - `win-x64`, `win-arm64`: `SDL3.dll` from the official `SDL3-3.4.18-win32-*.zip` release assets.
  - `osx-universal`: the arm64 + x86_64 binary of `SDL3.xcframework/macos-arm64_x86_64` from the official `SDL3-3.4.18.dmg`,
    renamed `libSDL3.dylib` (loaded by path, so its framework install name does not matter).
  - `linux-x64`: built from the release tarball (sha256 9c75cf16…34a3) with `cmake -G Ninja -DCMAKE_BUILD_TYPE=Release -DSDL_SHARED=ON
    -DSDL_STATIC=OFF -DSDL_TESTS=OFF -DSDL_EXAMPLES=OFF -DSDL_INSTALL=OFF` in Valve's Steam Runtime "sniper" SDK (glibc 2.31; the result
    needs glibc 2.29 or newer and links only libc, libm, libdl and libpthread, the video and audio backends are loaded at runtime), then
    stripped. Run the manual workflow `.github/workflows/thirdparty_sdl_linux.yml` (Actions > SDL for Linux), download its artifact and
    commit it as `libSDL3.so.0`. An earlier build on Arch needed glibc 2.43 and did not load on Ubuntu or in Steam's runtime.
  - Linux arm64 and macOS x64-only are not vendored.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/sdl.json` into `Source/Apogee.Native.Sdl`.

## flecs

- Upstream: https://github.com/SanderMertens/flecs
- Version: 4.1.6 (`v4.1.6`, fb55f3c25660425cfe1bc4cf5e6bff8b3f18a9b8, 2026-06-29)
- License: MIT (`flecs/LICENSE.txt`)
- Vendored: the amalgamated distribution, `distr/flecs.h` → `include/flecs.h` and `distr/flecs.c` → `source/flecs.c`, unchanged.
- Built: `./build.sh native` (`Native/flecs/CMakeLists.txt`) as a shared library into `Bin/native/<rid>`. The distribution's
  header starts with `#define flecs_STATIC`; the build compiles against a copy without that line so that `FLECS_API` exports.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/flecs.json` into `Source/Apogee.Native.Flecs`.

## luau

- Upstream: https://github.com/luau-lang/luau
- Version: 0.745 (`0.745`, 2a3d8937c19c8fd9f7b72b6ec9068c62760e6588, 2026-10-30)
- License: MIT (`luau/LICENSE.txt`, and Lua's own in `luau/lua_LICENSE.txt`)
- Vendored: the sources of `Common`, `Ast`, `Bytecode`, `Compiler`, `VM` and `CodeGen` (`include/` and `src/` of each, copied from the tag).
  No Analysis, CLI or tests.
- Built: `./build.sh native` (`Native/luau/CMakeLists.txt`) as one shared library with the C API under C linkage (`LUA_API`,
  `LUACODE_API` and `LUACODEGEN_API` are Luau's hooks for that) and `LUA_USE_LONGJMP=1`, because C++ exceptions cannot cross managed
  frames. Our layer, `Native/luau/apogee_luau.{h,cpp}`, is part of it: the allocator with its limits, the interrupt for the time
  budget, the wrapper for managed functions, protected calls and loading (see `Docs/manual/native/native-libraries.md`). Errors raised from
  managed callbacks must not unwind through managed code: callbacks return a code and the wrapper calls `lua_error`. The bindings
  leave out what can raise an error on input a script controls. CodeGen is built for both x64 and arm64 and picks at run time; a
  macOS app signed with the hardened runtime needs the `allow-jit` entitlement.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/luau.json` into `Source/Apogee.Native.Luau`, with the shim's header.

## cimgui (Dear ImGui)

- Upstream: https://github.com/cimgui/cimgui, branch `docking_inter` (2a6a93fccd10f275ee9206167dc9698f6f00fac1, 2026-10-18) for its generator, with Dear ImGui
  (https://github.com/ocornut/imgui) **1.93.0 from the `docking` branch**: tag `v1.93.0-docking` (b916186e66aa3c02b72c6ca17455516512ed8049; tarball sha256 746c2655…f1a5).
  The `imgui` submodule of cimgui's `docking_inter` is pinned to a `master` commit (6d32174, no docking API), so its own generated `cimgui.h` has no docking either: do not take
  the generated files from the branch, generate them against the docking tag. (Until 2026-11-05 this entry claimed docking but vendored that `master` commit.)
- License: MIT (`cimgui/LICENSE.txt`, `cimgui/imgui/LICENSE.txt`)
- Vendored: `cimgui.h`, `cimgui.cpp`, `cimconfig.h` (generated by cimgui for this ImGui, with the internal API), and ImGui's
  core sources and headers in `imgui/` (no backends, no misc, no examples), unchanged from the tag. Backends (SDL3, Vulkan) are written in C# on the
  generated bindings or added when the renderer needs them.
- Updating: unpack the ImGui tag over a clone of cimgui's `imgui/` folder and, in its `generator/`, run
  `luajit ./generator.lua gcc "internal noimstrv" -U__GNUC__` (needs luajit and gcc). `-U__GNUC__` makes ImGui's `IM_NODEBUGSTEP` (`[[gnu::artificial]]`, which the generator
  cannot parse) expand to nothing. Copy `cimgui.h` and `cimgui.cpp` from the clone's top folder and ImGui's `imgui*.{h,cpp}` core files, then `./build.sh bindings cimgui`.
  Docking is on per host: `ImGuiHost.EnableDocking()`; multi-viewport is not used.
- Also vendored, unchanged: `imgui/misc/freetype/imgui_freetype.{cpp,h}` (git blobs 375863fd… and 85313699…, checked against GitHub at the same ImGui commit), ImGui's FreeType font loader.
- Built: `./build.sh native` (`Native/imgui/CMakeLists.txt`) as one shared library, `cimgui`, with `IMGUI_ENABLE_FREETYPE` and `IMGUI_ENABLE_STB_TRUETYPE`
  (ImGui supports both together, and cimgui.cpp calls the stb loader, so the second has to be on; FreeType is the default) and linked to our [freetype](#freetype)
  (it sits next to it: the rpath is `$ORIGIN` / `@loader_path`). [ImGuizmo](#imguizmo), [ImPlot](#implot) and [imnodes](#imnodes) are compiled into the same library, with our own C APIs over them.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/cimgui.json` into `Source/Apogee.Native.CImGui`. Variadic functions
  (`igText`, `igSetTooltip`…) are not bound; use their non-variadic forms (`igTextUnformatted`).

## ImGuizmo

- Upstream: https://github.com/CedricGuillemet/ImGuizmo, tag `1.10` (b796ac3b861afc6e91ca74e4611effd9c9527367, 2026-05-11; tarball sha256 a467155e…5094)
- License: MIT (`imguizmo/LICENSE`)
- Vendored: `ImGuizmo.cpp` and `ImGuizmo.h` from the tag's `src/`, unchanged (not its sequencer, curve editor and gradient widgets). It compiles against the vendored ImGui 1.93.0 docking as is.
- Updating: replace the two files from a newer tag, rebuild, run the tests of `Tests/Apogee.DevTools.Tests` (`ImGuiExtensionTests`).
- Built: into `cimgui` (see above), with our shim `Native/imgui/apogee_imguizmo.{h,cpp}`: begin frame, rect, draw list, `manipulate` (operation, mode, snap, the change since the drag began), `view_manipulate`, the
  is-over and is-using questions, grid and cubes, matrix decomposition. There is no cimguizmo: the C API is ours, on plain floats (matrices are 16 floats).
- Bound by: `Tools/Apogee.Build/Bindings/Configs/imguizmo.json` into `Source/Apogee.Native.ImGuizmo` (class `Gizmo`), on the library `cimgui`.

## ImPlot

- Upstream: https://github.com/epezent/implot, tag `v1.1` (819f200639455a58b6b3d4156b70e4271aeb2d00, 2026-10-25; tarball sha256 f13e1e11…2ae8)
- License: MIT (`implot/LICENSE`)
- Vendored: `implot.h`, `implot_internal.h`, `implot.cpp`, `implot_items.cpp` (not the demo), unchanged. It compiles against the vendored ImGui 1.93.0 docking as is.
- Updating: replace the four files from a newer tag, rebuild, run `ImGuiExtensionTests`.
- Built: into `cimgui`, with our shim `Native/imgui/apogee_implot.{h,cpp}`: contexts, begin and end plot, axes and limits and formats, the legend, and line, shaded, bars and scatter items in float and double
  versions (ImPlot's templates are instantiated for those two). An item's look is a small `AplotSpec` struct (ImPlot's `ImPlotSpec` without its arrays). There is no cimplot.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/implot.json` into `Source/Apogee.Native.ImPlot` (class `Plot`), on the library `cimgui`.

## imnodes

- Upstream: https://github.com/Nelarius/imnodes, `master` at 2d0c280b07b2f62b120034811cc6e41251673961 (2026-10-17; the last release, v0.5, is from 2022); tarball sha256 f885736a…399f
- License: MIT (`imnodes/LICENSE.md`)
- Vendored: `imnodes.h`, `imnodes_internal.h`, `imnodes.cpp`, unchanged. It compiles against the vendored ImGui 1.93.0 docking as is (it does not touch ImGui's font or texture API).
- Updating: replace the three files from a newer commit, rebuild, run `ImGuiExtensionTests`.
- Built: into `cimgui`, with our shim `Native/imgui/apogee_imnodes.{h,cpp}`: contexts, editors, begin and end editor, nodes with title bars, input, output and static attributes, links, the minimap, positions in
  the three spaces, hover, selection, and the link-created and link-destroyed questions. There is no cimnodes.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/imnodes.json` into `Source/Apogee.Native.ImNodes` (class `Nodes`), on the library `cimgui`.

## freetype

- Upstream: https://gitlab.freedesktop.org/freetype/freetype
- Version: 2.15.0 (`VER-2-15-0`, 37515cf56ba3349656049edbed89ffc6f6466f2d, 2026-10-11)
- License: the FreeType License, FTL (`freetype/docs/FTL.TXT`; FreeType is dual-licensed with GPLv2, and we take the FTL, which is BSD-style with a credit clause: a game that
  ships the library says so in its credits, with the text in `Legal/THIRD_PARTY_NOTICES.md`)
- Vendored, unchanged: `include/` (without `dlg`), `LICENSE.TXT`, `docs/FTL.TXT`, and from `src/` the modules we build, each as its `.c` and `.h` files: `autofit`, `cff`, `cid`, `gzip`
  (FreeType's own zlib), `psaux`, `pshinter`, `psnames`, `raster`, `sdf`, `sfnt`, `smooth`, `truetype`, `type1`, `type42`, and of `base` the headers and the sources we compile (`ftsystem`,
  `ftinit`, `ftdebug`, `ftbase` with the files it includes, `ftbbox`, `ftbitmap`, `ftcid`, `ftfstype`, `ftgasp`, `ftglyph`, `ftmm`, `ftstroke`, `ftsynth`, `fttype1`). They come from the release tarball
  `freetype-2.15.0.tar.xz` (sha256 93cd47eb719c325107f2d3cf087d06299e944c3b48a40b55537302304dea1dd5, from download.savannah.gnu.org), whose `src/` and `include/` are identical to the git tag's apart from the
  `dlg` debug-logging library, which is not vendored.
- Left out, on purpose: the modules `bdf`, `pcf`, `winfonts` (bitmap and Windows fonts), `pfr`, `hvf` (Apple's experimental driver), `svg` (needs a renderer to plug in), `lzw`, `bzip2`, `cache`, the validators `gxvalid`
  and `otvalid`, and `ftpatent.c` (two deprecated no-ops; their bindings are excluded). Not linked: libpng (colour PNG glyphs), brotli (WOFF 2) and HarfBuzz, which upstream's defaults leave off.
- Built: `./build.sh native` (`Native/freetype/CMakeLists.txt`) as one shared library, the way `docs/INSTALL.ANY` describes a build without FreeType's make files (the amalgamated module sources, `-Iinclude`,
  `FT2_BUILD_LIBRARY`, `DLL_EXPORT` on Windows), with our configuration instead of patches: `FT_CONFIG_OPTIONS_H` is `Native/freetype/apogee_ftoption.h` (upstream's `ftoption.h`, then it undefines LZW and
  SVG, which need modules we do not build) and `FT_CONFIG_MODULES_H` is `apogee_ftmodule.h` (the list of built modules). The options header is also what `cimgui` and the bindings generator compile against, so
  that they agree with the library. `ftsystem.c` is the portable one (C `stdio`), because fonts are loaded from memory.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/freetype.json` into `Source/Apogee.Native.FreeType` (class `FT`).
- Test font: `Tests/Apogee.Native.Tests/Fixtures/fonts/ProggyClean.ttf` (MIT, see its README).

## jolt (Jolt Physics)

- Upstream: https://github.com/jrouwe/JoltPhysics
- Version: 5.6.0 (`v5.6.0`, e77f175595e64cb44218cc9d9d56fc365ad0e36a, 2026-07-11)
- License: MIT (`jolt/LICENSE`, "Copyright 2021 Jorrit Rouwe"; the notice has to travel with games)
- Vendored, unchanged: the `Jolt/` directory of the release tarball `v5.6.0.tar.gz` (sha256 6e069ee0172478cc78182047aac87e5310ba14a67a53348ae14cc37801fd3f8e) and its `LICENSE`. Not vendored: the samples, the unit tests, the viewer, the build files
  of upstream (`Build/`), the assets.
- Built: `./build.sh native` (`Native/jolt/CMakeLists.txt`) as one shared library, `jolt`, together with our C API over it, `Native/jolt/apogee_jolt.{h,cpp}` (there is no third-party wrapper: JoltC was used first and replaced). The sources are
  `Jolt/**/*.cpp` without the DirectX 12, Metal and Vulkan compute back ends. The flags and definitions are what upstream's `Build/CMakeLists.txt` and `Jolt/Jolt.cmake` would pass for our options, written out in our CMake file (a Jolt update
  re-reads both): double precision, cross-platform determinism, 32-bit object layers, SSE 4.2 (`-DAPOGEE_JOLT_AVX2=ON` for AVX2), no exceptions, no RTTI, strict floating point. See `Docs/manual/native/native-libraries.md`.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/jolt.json` into `Source/Apogee.Native.Jolt` (class `Jolt`), generated from `apogee_jolt.h`.

## rmlui

- Upstream: https://github.com/mikke89/RmlUi
- Version: 6.3 (`6.3`, ba95ffe8bfb6370efb2cdcca927eaad4710c5413, 2026-08-22; tarball `6.3.tar.gz` sha256 d977298bb6147610e5984d5db85ddf284020d655a8713913f6982074f1dbdede)
- License: MIT (`rmlui/LICENSE.txt`). Parts of it carry licenses of their own, listed in `Legal/THIRD_PARTY_NOTICES.md`: robin_hood and itlib (MIT, `Include/RmlUi/Core/Containers/LICENSE.txt`) and the debugger's Courier Prime fonts
  (SIL OFL 1.1, `Source/Debugger/LICENSE.txt`).
- Vendored, unchanged, at upstream's paths: `Include/RmlUi/` (`Config`, `Core`, `Debugger`, `Core.h`, `Debugger.h`) and `Source/Core` (with `Elements`, `FontEngineDefault`, `Layout`) and `Source/Debugger`, without their CMake files.
  Not vendored: the Lua, SVG and Lottie plugins (scripting is Luau, through our own bridge), the backends (we write the render, system and file interfaces), the samples, the tests, `Dependencies`.
  No patches. (The old engine had patched RmlUi's Lua plugin; the plugin is not used.)
- Built: `./build.sh native` (`Native/rmlui/CMakeLists.txt`) as one shared library, `rmlui`, with `RMLUI_STATIC_LIB` (RmlUi's own API is not exported), `RMLUI_FONT_ENGINE_FREETYPE` and `RMLUI_VERSION="6.3"`, linked to our
  [freetype](#freetype) (it sits next to it: the rpath is `$ORIGIN` / `@loader_path`). Our C API over it, `Native/rmlui/apogee_rmlui.{h,cpp}`, is part of the library and its only export.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/rmlui.json` into `Source/Apogee.Native.RmlUi` (class `Rml`), generated from `apogee_rmlui.h`.
- Updating: replace the three folders from the new tag's tarball, `./build.sh native`, and read the new `RenderInterface.h`, `SystemInterface.h` and `FileInterface.h` for functions the C API does not forward (`ArHost`).

## vulkan

- Upstream: https://github.com/KhronosGroup/Vulkan-Headers
- Version: `vulkan-sdk-1.4.363.0` (6802bb4733b63ed5efd3adb308a6c885ef180ea1)
- License: Apache-2.0 OR MIT (`vulkan/LICENSE.md`, `vulkan/LICENSES/`)
- Vendored: `registry/vk.xml` (the source of the bindings), and `include/vulkan/vulkan_core.h`, `vk_platform.h` and `vulkan.h`
  (to read the C struct sizes for the layout test, and as the include of libraries such as VMA that are written against Vulkan's C
  headers), and `include/vk_video/*` (which `vulkan_core.h` includes). No window-system headers: `vulkan.h` includes none
  without a `VK_USE_PLATFORM_*` define. Build with only these include paths: a system Vulkan-Headers install hides a missing file.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/vulkan.json` into `Source/Apogee.Native.Vulkan`, from the registry (`kind:
  vulkan`): Vulkan 1.0 to 1.4 and every extension that is not platform specific, provisional or video. Window-system
  surfaces come from SDL (`SDL_Vulkan_CreateSurface`). Only `vkGetInstanceProcAddr` is imported from the loader; everything
  else is loaded through it into `VkGlobalCommands`, `VkInstanceCommands` and `VkDeviceCommands`.
- Not vendored: the Vulkan loader (`libvulkan.so.1`, `vulkan-1.dll` come with the system and the GPU drivers). macOS needs
  MoltenVK if Vulkan is wanted there; the default on macOS is the Metal backend (below).

## metal (Apple's Metal, through `Native/metal_c`)

- Nothing is vendored: Metal, QuartzCore and Foundation are system frameworks of macOS (15 or later is required, for `MTLResidencySet`).
- Wrapped by: `Native/metal_c/` (`apogee_metal.h`, `apogee_metal.m`), our own C API over the Objective-C classes, built by `./build.sh native` on macOS only
  (Objective-C with ARC, linked against the frameworks; the same library as every other, `metal_c`). Objects are opaque handles, the header has no Objective-C in it, and no block crosses it
  (completion is an `MTLSharedEvent` value), so any language can call it.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/metal.json` into `Source/Apogee.Native.Metal` (class `Mtl`). The header is plain C, so the bindings are generated on any OS; the library itself exists only on macOS.
- Not verified on a GPU: the shim is compiled and its entry points are checked on the macOS CI job, but no Metal 3 GPU has run it (`Docs/contributing/help-wanted.md`).

## vma (Vulkan Memory Allocator)

- Upstream: https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator
- Version: 3.5.0 (`v3.5.0`, 0f1a757da579831283413295cc7f3b47eb521c77, 2026-10-26)
- License: MIT (`vma/LICENSE.txt`)
- Vendored: `include/vk_mem_alloc.h`, unchanged.
- Built: `./build.sh native` (`Native/vma/`) as a shared library from `vma.cpp`, which only defines `VMA_IMPLEMENTATION`.
  Vulkan is not linked: `VMA_STATIC_VULKAN_FUNCTIONS=0`, `VMA_DYNAMIC_VULKAN_FUNCTIONS=1`, so the caller passes
  `vkGetInstanceProcAddr` and `vkGetDeviceProcAddr` in `VmaVulkanFunctions`. `VMA_CALL_PRE` exports the C functions.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/vma.json` into `Source/Apogee.Native.Vma`. Vulkan's types come from
  `Apogee.Native.Vulkan` (`foreignTypes`); the handles are the opaque structs behind VMA's typedefs (`Allocator*`, `Allocation*`).

## meshoptimizer

- Upstream: https://github.com/zeux/meshoptimizer
- Version: 1.3 (`v1.3`, 9e1f07b159d3cb777f1c67ed31fc11fd117986f4, 2026-09-25)
- License: MIT (`meshoptimizer/LICENSE.md`)
- Vendored: `src/` (the library's sources and `meshoptimizer.h`), unchanged.
- Built: `./build.sh native` (`Native/meshoptimizer/`) as one shared library; `MESHOPTIMIZER_API` exports the C functions.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/meshopt.json` into `Source/Apogee.Native.Meshopt`.

## cgltf

- Upstream: https://github.com/jkuhlmann/cgltf
- Version: 1.15 (`v1.15`, bbeb5b0b070ddacddac6852fb72143eb68454937, 2025-02-09)
- License: MIT (`cgltf/LICENSE.txt`)
- Vendored: `cgltf.h`, unchanged. `cgltf_write.h` (the glTF writer) is not vendored; add it with the first tool that exports glTF.
- Built: `./build.sh native` (`Native/cgltf/`) as a shared library from `cgltf.c`, which only defines `CGLTF_IMPLEMENTATION`. The
  header has no export macro, so the target exports every symbol (default visibility; `WINDOWS_EXPORT_ALL_SYMBOLS`).
- Bound by: `Tools/Apogee.Build/Bindings/Configs/cgltf.json` into `Source/Apogee.Native.Cgltf`.

## stb_image

- Upstream: https://github.com/nothings/stb (`stb_image.h` v2.30; the repository's master at the time was 003501f6d7a8668ce8b6d875e34dd97ad0d5d5b5, 2026-10-23)
- License: MIT or public domain, your choice (`stb/LICENSE.txt` is the notice at the end of the header)
- Vendored: `stb_image.h`, unchanged. Not `stb_image_write.h`: screenshots use our own PNG writer.
- Built: `./build.sh native` (`Native/stb/`) as a shared library from `stb_image.c` (`STB_IMAGE_IMPLEMENTATION`, `STBI_NO_STDIO`: decoding
  from memory only, files come through the engine's file system). The header has no export macro, so the target exports every symbol.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/stb.json` into `Source/Apogee.Native.Stb` (the class is `Stbi`).

## stb_vorbis

- Upstream: https://github.com/nothings/stb (`stb_vorbis.c` v1.22; the repository's master at the time was 4e9b368bfc071e97206ea5c6adba68dd0cbf8897, 2026-11-07)
- License: MIT or public domain, your choice (`stb/LICENSE.txt` has the same two alternatives as the end of the file)
- Vendored: `stb/stb_vorbis.c`, unchanged (next to `stb_image.h`).
- Built: into `audio_decode` (see below), with `STB_VORBIS_NO_STDIO` and `STB_VORBIS_NO_PUSHDATA_API`: decoding from memory only.

## dr_libs

- Upstream: https://github.com/mackron/dr_libs (master at the time: 35b5c392cc15456736c440a68dbe073178bbffe0, 2026-10-30)
- Version: `dr_wav.h` 0.14.6, `dr_flac.h` 0.13.4, `dr_mp3.h` 0.7.4 (all 2026-10-31)
- License: public domain or MIT-0, your choice (`dr_libs/LICENSE.txt`)
- Vendored: the three headers, unchanged.
- Built: into `audio_decode` (see below), with `DR_WAV_NO_STDIO`, `DR_FLAC_NO_STDIO` and `DR_MP3_NO_STDIO`.

## audio_decode (our own shim over dr_libs and stb_vorbis)

- Source: `Native/audio_decode/apogee_audio.{h,c}`, ours: four functions that open a WAV, FLAC, Ogg Vorbis or MP3 file held in memory (the format is told from its first bytes) and read it as 32-bit float samples.
- Built: `./build.sh native` as one shared library, `audio_decode`, from the decoders above.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/audiodecode.json` into `Source/Apogee.Native.AudioDecode` (the class is `AudioDecode`). Tests: `Tests/Apogee.Native.Tests/AudioDecodeSmokeTests.cs` with the tones in `Fixtures/audio/`.

## openal-soft

- Upstream: https://github.com/kcat/openal-soft
- Version: 1.25.2 (`1.25.2`, b2c48f7718ef3fcf67921a8b6534c4914e328970, 2026-05-12)
- License: LGPL-2.0-or-later (`openal-soft/COPYING`; the modified PFFFT inside it has its own BSD-style license, `openal-soft/LICENSE-pffft`). It is used as a **separate shared library** that a game ships beside itself, where its
  user can replace it (see `Legal/THIRD_PARTY_NOTICES.md`); the engine links nothing of it statically.
- Vendored: `include/AL/{al,alc,alext,efx,efx-presets}.h` from the tag, unchanged, and `lib/<rid>/` (checksums in `lib/SHA256SUMS`):
  - `win-x64`: `soft_oal.dll` from the official `openal-soft-1.25.2-bin.zip` release asset, renamed `OpenAL32.dll` (its readme says to, to use it without the router).
  - `linux-x64` (`libopenal.so.1`) and `osx-universal` (`libopenal.1.dylib`, arm64 + x86_64, macOS 11): built by the manual workflow `.github/workflows/thirdparty_openal.yml` (Actions > OpenAL Soft) from the tag with
    `-DLIBTYPE=SHARED -DALSOFT_UTILS=OFF -DALSOFT_EXAMPLES=OFF -DALSOFT_TESTS=OFF -DALSOFT_INSTALL=OFF -DALSOFT_EMBED_HRTF_DATA=ON`. On Linux it runs in Valve's Steam Runtime "sniper" SDK with its g++ 14 (OpenAL Soft is C++20; the SDK's default g++ 10 is too old),
    loads its audio backends (PipeWire, PulseAudio, ALSA, JACK) at run time, links libstdc++ statically, and the job fails if the result needs a newer glibc than 2.31 or has a `GLIBCXX` symbol (the committed build needs glibc 2.28 and links only libc, libm, libdl and libpthread). On macOS
    it passes `-DHAVE_WFUNCTION_EFFECTS=0`, because OpenAL Soft's `-Werror=function-effects` rejects its own CoreAudio callbacks under Xcode 26. The committed files are the artifacts of run 42389726417.
  - Not vendored: Windows on Arm and a macOS x64-only build.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/openal.json` into `Source/Apogee.Native.OpenAL` (AL, ALC, EFX and the extensions OpenAL Soft exports, with `AL_ALEXT_PROTOTYPES`).

## gns (GameNetworkingSockets)

- Upstream: https://github.com/ValveSoftware/GameNetworkingSockets
- Version: 1.6.1 (`v1.6.1`, 61d5da24fc933ab104cc8e5ab2beb133550f9570, 2026-10-03)
- License: BSD-3-Clause, Valve (`gns/LICENSE`). Its build also carries protobuf (BSD-3-Clause, Google), Abseil (Apache-2.0) and, on Linux and macOS, OpenSSL (Apache-2.0); see `Legal/THIRD_PARTY_NOTICES.md`.
- Vendored: `include/steam/*.h` from the tag, unchanged (the public headers: `steamnetworkingsockets.h`, `steamnetworkingtypes.h`, `isteamnetworking*.h`, …) and `lib/<rid>/` (checksums in `lib/SHA256SUMS`): one shared
  `GameNetworkingSockets` per platform, built by the manual workflow `.github/workflows/thirdparty_gns.yml` (Actions > GameNetworkingSockets) from the tag with `-DBUILD_SHARED_LIB=ON -DBUILD_STATIC_LIB=OFF -DUSE_STEAMWEBRTC=OFF`
  (IP sockets only: no P2P and no WebRTC). protobuf, Abseil and the crypto library are linked statically from vcpkg, so nothing else has to be shipped: `libGameNetworkingSockets.so` (Linux, in Valve's Steam Runtime "sniper" SDK:
  glibc 2.31; libstdc++ static; the job fails on a newer glibc, a `GLIBCXX` symbol, or a dependency on protobuf, Abseil or OpenSSL), `GameNetworkingSockets.dll` and its import library `.lib` (Windows: BCrypt for the crypto) and
  `libGameNetworkingSockets.dylib` (macOS, arm64 + x86_64). The committed files are the artifacts of runs 42390932561 (Linux: needs glibc 2.30 and links only libc, libm, libdl and libpthread; macOS) and 42392721424 (Windows). A developer can put a build of their own (`cmake -S <tag> -B build -G Ninja
  -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIB=ON -DBUILD_STATIC_LIB=OFF -DBUILD_EXAMPLES=OFF -DBUILD_TESTS=OFF -DUSE_STEAMWEBRTC=OFF`, which needs protobuf, Abseil and OpenSSL installed) in `Bin/native/<rid>/`, which
  `./build.sh native` and `NativeLibraries.props` use first. Without any, the shim below is not built and networking stays off.
- Not vendored: Windows on Arm and a macOS x64-only build.
- The workflow's vcpkg steps use the release-only triplets (`x64-linux-release`, `arm64-osx-release`, `x64-osx-release`) and, on Windows, `x64-windows-static-md` with `-DProtobuf_USE_STATIC_LIBS=ON`.

## net (our own shim over GameNetworkingSockets)

- Source: `Native/net/apogee_net.{h,cpp}`, ours: connections as integers and everything the library says (a connection asked for, made, closed; a message arrived) as events from a poll. GNS's flat C API is not used: it takes C++
  references and structs that are packed differently by platform, which the compiler lays out here instead.
- Built: `./build.sh native` as the shared library `apogee_net`, linked against `GameNetworkingSockets` (found in `Bin/native/<rid>` first, then `ThirdParty/gns/lib/<rid>`), with an rpath next to itself.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/net.json` into `Source/Apogee.Native.Net` (the class is `Net`). Tests: `Tests/Apogee.Network.Tests` (two systems talk over the loopback interface).

## ktx (KTX-Software, libktx)

- Upstream: https://github.com/KhronosGroup/KTX-Software
- Version: 4.4.2 (`v4.4.2`, 936b655d10fe75f900967f524ba31005bebcbb47, 2025-10-04)
- License: Apache-2.0 (`ktx/LICENSE.md`, texts in `ktx/LICENSES/`). It bundles the Basis Universal transcoder and encoder (Apache-2.0; the encoder has a public-domain/MIT resampler and bc7enc inside), dfdutils
  (Apache-2.0) and miniz (MIT, in `basisu_miniz.h`). Its KTX 2 zstd supercompression uses our own [zstd](#zstd) (below), which is linked,
  not bundled.
- Vendored, at upstream's paths: `include/` (`ktx.h`, `KHR/khr_df.h`), `lib/` (the reading half, and the writing half: `basis_encode.cpp`, `writer1.c`, `writer2.c`), `utils/unused.h`, `other_include/` (the GL, GLES, EGL and KHR headers the sources include),
  `external/basisu/transcoder/`, `external/basisu/encoder/` (the Basis Universal encoder: every file of the directory except `jpgd.*` and `pvpngreader.*`, which only the standalone tool's image readers use, and which `LIBKTX` compiles out) and `external/dfdutils/`.
  Upstream's decoder-only zstd (`external/basisu/zstd/`) and `zstd_errors.h` are not vendored here: libktx uses the zstd below.
  Two lines of upstream are changed, both the include of the bundled zstd: `basisu_transcoder.cpp` and `basisu_comp.cpp` include `<zstd.h>` where they had `"../zstd/zstd.h"`.
  Upstream generates `lib/version.h` from `git describe`; a vendored copy has no history, so `Native/ktx/version.h` is written by hand (`LIBKTX_VERSION` v4.4.2): change it with the version.
  The files came from the tag's tarball (sha256 9412cb45…d323).
- Left out, on purpose: the ASTC encoder (`lib/astc_codec.cpp` and `external/astc-encoder`: UASTC is transcoded to ASTC when loaded, so there is nothing to encode), the OpenCL path of the Basis encoder (`BASISU_SUPPORT_OPENCL=0`), the OpenGL and Vulkan loaders
  (we upload ourselves), and `external/etcdec` + `lib/etcunpack.cxx`, which are under Ericsson's own license (`LicenseRef-ETCSLA`) and only serve the OpenGL loader.
- Built: `./build.sh native` (`Native/ktx/`) as one shared library, with `KHRONOS_STATIC` (ktx.h's `KTX_API` then empty), `KTX_FEATURE_WRITE=1` (the writer instead of upstream's stubs) and every symbol exported. The encoder's SSE 4.1 kernels are
  built on x86-64 only, as upstream does (`BASISU_SUPPORT_SSE`; `-msse4.1` on GCC and Clang). It links `zstd` (next to it at run time; the rpath is `$ORIGIN` / `@loader_path`) and threads (the encoder is multithreaded).
- Bound by: `Tools/Apogee.Build/Bindings/Configs/ktx.json` into `Source/Apogee.Native.Ktx`, without the functions of the left-out parts (ASTC, OpenGL).
- Test files: `Tests/Apogee.Native.Tests/Fixtures/ktx2/` (from `tests/testimages/`, Apache-2.0, see its README).

## zstd

- Upstream: https://github.com/facebook/zstd
- Version: 1.5.7 (`v1.5.7`, f8745da6ff1ad1e7bab384bd1f9d742439278e99, 2025-02-19)
- License: BSD-3-Clause (`zstd/LICENSE`; zstd is dual-licensed with GPLv2, and we use it under the BSD terms. Its notice has to travel with games)
- Vendored: `lib/{common,compress,decompress}/`, `lib/zstd.h` and `lib/zstd_errors.h` from the tag, unchanged (every file's git blob hash was checked against
  the tag's tree). Not vendored: the legacy decoders, the dictionary builder, the deprecated and DLL-only files, the programs, the tests.
- Built: `./build.sh native` (`Native/zstd/`) as one shared library, `zstd`, with multithreaded compression (`ZSTD_MULTITHREAD`) and without the x86-64 assembly of the
  Huffman decoder (`ZSTD_DISABLE_ASM`: the C decoder is a little slower and builds the same everywhere). The library exports its own API (`ZSTDLIB_VISIBLE`;
  `ZSTD_DLL_EXPORT` on Windows). libktx links it.
- Bound by: `Tools/Apogee.Build/Bindings/Configs/zstd.json` into `Source/Apogee.Native.Zstd`: the stable API of `zstd.h` and `zstd_errors.h` (no
  `ZSTD_STATIC_LINKING_ONLY`). `ZSTD_CONTENTSIZE_UNKNOWN` and `_ERROR` are `(0ULL - 1)` and `(0ULL - 2)`, which the generator's macro evaluator cannot
  express, so `Source/Apogee.Native.Zstd/Zstd.ContentSize.cs` defines them.

## slang

- Upstream: https://github.com/shader-slang/slang
- Version: 2026.19 (`v2026.19`, 0f136005d8a75b5f44ca914272810799bb4e7d7a, 2026-09-29)
- License: Apache-2.0 with LLVM exception (`slang/LICENSE`, `slang/LICENSES/`). The binaries contain glslang, SPIRV-Tools, miniz, lz4,
  cmark and unordered_dense: their notices are in `slang/third-party-notices/` and have to travel with games.
- Vendored: `include/` (`slang.h` and the headers it includes), and the official release binaries in `lib/<rid>/`, unchanged
  (checksums in `lib/SHA256SUMS`): the compiler (`libslang-compiler.so.0.2026.19`, `slang-compiler.dll` + `slang-compiler.lib`,
  `libslang-compiler.0.2026.19.dylib`) and `slang-glslang` (SPIR-V validation and linking). From these release archives
  (sha256): `slang-2026.19-linux-x86_64-glibc-2.27.tar.gz` 5899bd40…fe75 (glibc 2.27 or newer), `slang-2026.19-windows-x86_64.tar.gz`
  fa7629c9…eeee, `slang-2026.19-windows-aarch64.tar.gz` e47c3272…6751, `slang-2026.19-macos-aarch64.tar.gz` fa46822c…f2f0 and
  `slang-2026.19-macos-x86_64.tar.gz` 490b6447…ee11. Not vendored: the tools, `slang-rt`, `gfx`, `slang-llvm` (84 MB, only for the CPU
  targets) and `slang-glsl-module` (only for GLSL input).
- Slang's API is C++ and COM, so it is bound through our C API over it: `Native/slang_c/` (`apogee_slang.h`, `apogee_slang.cpp`),
  built by `./build.sh native` into `slang_c` and linked against the vendored compiler library (rpath `$ORIGIN` / `@loader_path`:
  the compiler has to sit next to it, which `ThirdParty/NativeLibraries.props` arranges).
- Bound by: `Tools/Apogee.Build/Bindings/Configs/slang.json` into `Source/Apogee.Native.Slang`.
