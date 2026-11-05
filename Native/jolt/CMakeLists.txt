# Jolt Physics and our C API over it (apogee_jolt.h) as one shared library.
#
# Jolt is built the way its own Build/CMakeLists.txt and Jolt/Jolt.cmake would build it, with these choices:
#  - JPH_DOUBLE_PRECISION: positions are doubles (the world is Double3, see Docs/manual/core/math.md); rotations, scales and velocities stay float.
#  - JPH_CROSS_PLATFORM_DETERMINISTIC: the same inputs give the same results on every OS and compiler (no fused multiply-add, strict float
#    contraction, no fast math), which lockstep multiplayer and replays need. It costs about 8% of the simulation.
#  - JPH_OBJECT_LAYER_BITS=32: an object layer holds a body's layers and its mask (see apogee_jolt.cpp).
#  - No C++ exceptions and no RTTI, as Jolt does not use them (they must not cross managed frames either).
#  - The instruction set is SSE 4.2 (x86-64-v2, the engine's baseline, see Directory.Build.props). -DAPOGEE_JOLT_AVX2=ON builds for AVX2, BMI, LZCNT and F16C
#    (x86-64-v3, any CPU since Haswell); Jolt picks its instruction set at build time. FMA stays off for determinism. arm64 needs no flags.
#  - No GPU compute (DirectX, Vulkan, Metal, the CPU fallback), no profiler, no assertions in Release.
#  - The debug renderer (JPH_DEBUG_RENDERER) is compiled in for the editor to use later.
option(APOGEE_JOLT_AVX2 "Build Jolt for AVX2 (x86-64-v3) instead of SSE 4.2 (x86-64-v2)" OFF)

set(JOLT "${APOGEE_THIRDPARTY}/jolt")
file(GLOB_RECURSE JOLT_SOURCES "${JOLT}/Jolt/*.cpp")
# Platform compute back ends (DirectX 12, Metal, Vulkan) are not used.
list(FILTER JOLT_SOURCES EXCLUDE REGEX "/Compute/(DX12|MTL|VK)/")

add_library(jolt SHARED ${JOLT_SOURCES} apogee_jolt.cpp)
target_include_directories(jolt PRIVATE "${JOLT}" "${CMAKE_CURRENT_LIST_DIR}")
target_compile_definitions(jolt PRIVATE
  JPH_DOUBLE_PRECISION JPH_CROSS_PLATFORM_DETERMINISTIC JPH_OBJECT_LAYER_BITS=32 JPH_OBJECT_STREAM JPH_DEBUG_RENDERER
  "$<$<NOT:$<CONFIG:Debug>>:NDEBUG>"
  "$<$<CONFIG:Debug>:JPH_ENABLE_ASSERTS>")
if(WIN32)
  target_compile_definitions(jolt PRIVATE "APOGEE_JOLT_API=__declspec(dllexport)")
else()
  target_compile_definitions(jolt PRIVATE "APOGEE_JOLT_API=__attribute__((visibility(\"default\")))")
endif()

if(MSVC)
  # Strict floating point for determinism; Jolt's own build sets the same.
  target_compile_options(jolt PRIVATE /Zc:__cplusplus /fp:precise /GR- /wd4577)
else()
  target_compile_options(jolt PRIVATE -fno-rtti -fno-exceptions -ffp-contract=off -pthread)
  if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    target_compile_options(jolt PRIVATE -ffp-model=precise)
  else()
    target_compile_options(jolt PRIVATE -Wno-stringop-overflow -Wno-psabi)
  endif()
  if(APPLE)
    target_compile_options(jolt PRIVATE -faligned-allocation)
  endif()
  target_link_options(jolt PRIVATE -pthread)
endif()

# The instruction set: the same flags and JPH_USE_* definitions Jolt.cmake derives from its USE_* options (a mismatch is a link error).
if(CMAKE_SYSTEM_PROCESSOR MATCHES "x86_64|AMD64")
  target_compile_definitions(jolt PRIVATE JPH_USE_SSE4_1 JPH_USE_SSE4_2)
  if(APOGEE_JOLT_AVX2)
    target_compile_definitions(jolt PRIVATE JPH_USE_AVX JPH_USE_AVX2 JPH_USE_LZCNT JPH_USE_TZCNT JPH_USE_F16C)
    if(MSVC)
      target_compile_options(jolt PRIVATE /arch:AVX2)
    else()
      target_compile_options(jolt PRIVATE -mavx2 -mbmi -mpopcnt -mlzcnt -mf16c)
    endif()
  elseif(NOT MSVC)
    target_compile_options(jolt PRIVATE -msse4.2 -mpopcnt)
  endif()
  if(NOT MSVC)
    target_compile_options(jolt PRIVATE -mfpmath=sse)
  endif()
endif()

# A shared library may keep undefined symbols on Linux; fail the link instead of crashing on the first call.
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  target_link_options(jolt PRIVATE -Wl,--no-undefined)
endif()
apogee_library(jolt)
