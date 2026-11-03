# Flecs, from the amalgamated distribution in ThirdParty/flecs. The distribution's header starts with
# `#define flecs_STATIC` (a static build); a shared build exports through FLECS_API instead, so the library is compiled
# against a copy of the header without that line. ThirdParty/flecs/include/flecs.h itself stays as upstream shipped it.
set(FLECS_SHARED_INCLUDE "${CMAKE_CURRENT_BINARY_DIR}/include")
file(READ "${APOGEE_THIRDPARTY}/flecs/include/flecs.h" FLECS_HEADER)
string(REPLACE "#define flecs_STATIC\n" "" FLECS_HEADER "${FLECS_HEADER}")
file(WRITE "${FLECS_SHARED_INCLUDE}/flecs.h" "${FLECS_HEADER}")

add_library(flecs SHARED "${APOGEE_THIRDPARTY}/flecs/source/flecs.c")
target_include_directories(flecs PRIVATE "${FLECS_SHARED_INCLUDE}")
# FLECS_NDEBUG in every configuration: debug builds of Flecs have other struct layouts (ecs_ref_t, ecs_map_t…), and the
# bindings are generated against this one (Tools/Apogee.Build/Bindings/Configs/flecs.json).
target_compile_definitions(flecs PRIVATE flecs_EXPORTS FLECS_NDEBUG)
if(WIN32)
  target_link_libraries(flecs PRIVATE ws2_32 dbghelp)
else()
  target_link_libraries(flecs PRIVATE m pthread)
endif()
apogee_library(flecs)
