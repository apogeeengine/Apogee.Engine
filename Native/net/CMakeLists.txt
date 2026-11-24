# The C API over GameNetworkingSockets (its headers are C++). It links the GameNetworkingSockets shared library, which has to sit next to it at run
# time: ThirdParty/NativeLibraries.props copies both into every output folder, and the rpath is relative to this library. The library is vendored as a
# binary (ThirdParty/gns/lib/<rid>, built by .github/workflows/thirdparty_gns.yml); a developer can put a build of their own in Bin/native/<rid> and
# it is used first. Without either the shim is not built and the engine runs without networking (the tests skip).
set(GNS "${WEFT_THIRDPARTY}/gns")
set(GNS_LIBS "${GNS}/lib/${WEFT_OS}-${WEFT_ARCH}")
if(WIN32)
  set(GNS_NAMES GameNetworkingSockets.lib)
elseif(APPLE)
  set(GNS_NAMES libGameNetworkingSockets.dylib)
else()
  set(GNS_NAMES libGameNetworkingSockets.so libGameNetworkingSockets.so.1)
endif()
# macOS binaries are universal (arm64 and x86_64 in one file).
find_library(GNS_LIBRARY NAMES ${GNS_NAMES} PATHS "${WEFT_OUT}" "${GNS_LIBS}" "${GNS}/lib/osx-universal" NO_DEFAULT_PATH)

if(NOT GNS_LIBRARY)
  message(STATUS "weft_net: GameNetworkingSockets is not in ${WEFT_OUT} or ${GNS_LIBS}: not built (networking stays off)")
  return()
endif()

add_library(weft_net SHARED weft_net.cpp)
target_include_directories(weft_net PRIVATE "${GNS}/include")
target_compile_definitions(weft_net PRIVATE STEAMNETWORKINGSOCKETS_OPENSOURCE)
target_link_libraries(weft_net PRIVATE "${GNS_LIBRARY}")
if(NOT WIN32)
  set_target_properties(weft_net PROPERTIES BUILD_WITH_INSTALL_RPATH ON INSTALL_RPATH "$<IF:$<BOOL:${APPLE}>,@loader_path,$ORIGIN>")
endif()
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  target_link_options(weft_net PRIVATE -Wl,--no-undefined)
endif()
weft_library(weft_net)
