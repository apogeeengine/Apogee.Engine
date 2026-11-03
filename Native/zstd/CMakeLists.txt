# zstd's compressor and decompressor (lib/common, lib/compress, lib/decompress) as one shared library, with multithreaded compression. The
# library marks its C API for export itself (ZSTDLIB_VISIBLE on ELF and Mach-O, ZSTD_DLL_EXPORT on Windows). Left out: the legacy formats, the
# dictionary builder, and the hand-written x86-64 assembly of the Huffman decoder (ZSTD_DISABLE_ASM): the C decoder is a little slower and
# builds the same on every platform. libktx links this library for its KTX 2 supercompression instead of bundling a decoder of its own.
set(ZSTD "${APOGEE_THIRDPARTY}/zstd/lib")
file(GLOB ZSTD_SOURCES "${ZSTD}/common/*.c" "${ZSTD}/compress/*.c" "${ZSTD}/decompress/*.c")
add_library(zstd SHARED ${ZSTD_SOURCES})
target_include_directories(zstd PUBLIC "${ZSTD}")
target_include_directories(zstd PRIVATE "${ZSTD}/common" "${ZSTD}/compress" "${ZSTD}/decompress")
target_compile_definitions(zstd PRIVATE ZSTD_MULTITHREAD ZSTD_DISABLE_ASM)
if(WIN32)
  target_compile_definitions(zstd PRIVATE ZSTD_DLL_EXPORT=1)
endif()
find_package(Threads REQUIRED)
target_link_libraries(zstd PRIVATE Threads::Threads)
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  target_link_options(zstd PRIVATE -Wl,--no-undefined)
endif()
apogee_library(zstd)
