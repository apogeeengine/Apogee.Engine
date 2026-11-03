# libktx: KTX 1 and 2 loading with the Basis Universal transcoder, and KTX 2 writing with the Basis Universal encoder (UASTC), both on our zstd library
# (Native/zstd). No ASTC encoder (UASTC is transcoded to ASTC when loaded), no OpenGL or Vulkan upload (we upload ourselves), and no software ETC unpacker
# (it is under Ericsson's license and only the OpenGL loader uses it). KHRONOS_STATIC makes ktx.h's KTX_API empty; every symbol is exported instead.
set(KTX "${APOGEE_THIRDPARTY}/ktx")
# The Basis Universal encoder (upstream's BASISU_ENCODER_CXX_SRC), without the OpenCL path and the JPEG and PNG readers (LIBKTX leaves those out).
set(KTX_ENCODER_SOURCES)
foreach(name backend basis_file bc7enc comp enc etc frontend gpu_texture kernels_sse opencl pvrtc1_4 resample_filters resampler ssim uastc_enc)
  list(APPEND KTX_ENCODER_SOURCES "${KTX}/external/basisu/encoder/basisu_${name}.cpp")
endforeach()
add_library(ktx SHARED
  "${KTX}/lib/checkheader.c" "${KTX}/lib/filestream.c" "${KTX}/lib/glformat_str.c" "${KTX}/lib/hashlist.c" "${KTX}/lib/info.c"
  "${KTX}/lib/memstream.c" "${KTX}/lib/strings.c" "${KTX}/lib/swap.c" "${KTX}/lib/texture.c" "${KTX}/lib/texture1.c"
  "${KTX}/lib/texture2.c" "${KTX}/lib/vkformat_check.c" "${KTX}/lib/vkformat_check_variant.c" "${KTX}/lib/vkformat_str.c"
  "${KTX}/lib/vkformat_typesize.c" "${KTX}/lib/basis_transcode.cpp" "${KTX}/lib/miniz_wrapper.cpp"
  "${KTX}/lib/basis_encode.cpp" "${KTX}/lib/writer1.c" "${KTX}/lib/writer2.c"
  "${KTX}/external/basisu/transcoder/basisu_transcoder.cpp"
  ${KTX_ENCODER_SOURCES}
  "${KTX}/external/dfdutils/colourspaces.c" "${KTX}/external/dfdutils/createdfd.c" "${KTX}/external/dfdutils/interpretdfd.c"
  "${KTX}/external/dfdutils/printdfd.c" "${KTX}/external/dfdutils/queries.c" "${KTX}/external/dfdutils/vk2dfd.c")
target_include_directories(ktx PRIVATE
  "${CMAKE_CURRENT_SOURCE_DIR}" # version.h
  "${KTX}/include" "${KTX}/lib" "${KTX}/utils" "${KTX}/external" "${KTX}/external/dfdutils" "${KTX}/external/basisu")
target_include_directories(ktx SYSTEM PRIVATE "${KTX}/other_include")
target_compile_definitions(ktx PRIVATE
  LIBKTX KHRONOS_STATIC KTX_FEATURE_KTX1 KTX_FEATURE_KTX2 KTX_FEATURE_WRITE=1 KTX_OMIT_VULKAN=1 SUPPORT_SOFTWARE_ETC_UNPACK=0
  BASISD_SUPPORT_FXT1=0 BASISD_SUPPORT_KTX2_ZSTD=1 BASISU_SUPPORT_OPENCL=0)
# The encoder's SSE 4.1 kernels exist on x86-64 only (upstream turns them on the same way).
if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|AMD64|amd64)$")
  target_compile_definitions(ktx PRIVATE BASISU_SUPPORT_SSE=1)
  if(NOT MSVC)
    target_compile_options(ktx PRIVATE -msse4.1)
  endif()
else()
  target_compile_definitions(ktx PRIVATE BASISU_SUPPORT_SSE=0)
endif()
if(NOT MSVC)
  set_source_files_properties(${KTX_ENCODER_SOURCES} "${KTX}/external/basisu/transcoder/basisu_transcoder.cpp" PROPERTIES COMPILE_OPTIONS "-fno-strict-aliasing")
endif()
set_target_properties(ktx PROPERTIES C_VISIBILITY_PRESET default CXX_VISIBILITY_PRESET default WINDOWS_EXPORT_ALL_SYMBOLS ON)
# zstd has to sit next to libktx at run time: ThirdParty/NativeLibraries.props copies both into every output folder, and the rpath is relative to this library.
target_link_libraries(ktx PRIVATE zstd)
if(NOT WIN32)
  set_target_properties(ktx PROPERTIES BUILD_WITH_INSTALL_RPATH ON INSTALL_RPATH "$<IF:$<BOOL:${APPLE}>,@loader_path,$ORIGIN>")
  find_package(Threads REQUIRED)
  target_link_libraries(ktx PRIVATE m ${CMAKE_DL_LIBS} Threads::Threads)
  target_compile_options(ktx PRIVATE -Wno-nested-anon-types -Wno-gnu-anonymous-struct)
endif()
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  target_link_options(ktx PRIVATE -Wl,--no-undefined)
endif()
apogee_library(ktx)
