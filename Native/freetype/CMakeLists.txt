# FreeType 2 as one shared library, built the way docs/INSTALL.ANY describes for a build without FreeType's own make files: the amalgamated source of every module we
# use (ThirdParty/freetype/src), the base layer, and our configuration (apogee_ftoption.h, apogee_ftmodule.h) instead of patches to the upstream headers. FreeType
# marks its API for export itself (FT_EXPORT, with DLL_EXPORT on Windows), so the hidden default visibility of this project does not matter.
set(FT "${APOGEE_THIRDPARTY}/freetype")
set(FT_SOURCES
  ${FT}/src/base/ftsystem.c ${FT}/src/base/ftinit.c ${FT}/src/base/ftdebug.c ${FT}/src/base/ftbase.c
  ${FT}/src/base/ftbbox.c ${FT}/src/base/ftbitmap.c ${FT}/src/base/ftcid.c ${FT}/src/base/ftfstype.c ${FT}/src/base/ftgasp.c ${FT}/src/base/ftglyph.c
  ${FT}/src/base/ftmm.c ${FT}/src/base/ftstroke.c ${FT}/src/base/ftsynth.c ${FT}/src/base/fttype1.c
  ${FT}/src/autofit/autofit.c ${FT}/src/cff/cff.c ${FT}/src/cid/type1cid.c ${FT}/src/gzip/ftgzip.c ${FT}/src/psaux/psaux.c ${FT}/src/pshinter/pshinter.c
  ${FT}/src/psnames/psnames.c ${FT}/src/raster/raster.c ${FT}/src/sdf/sdf.c ${FT}/src/sfnt/sfnt.c ${FT}/src/smooth/smooth.c ${FT}/src/truetype/truetype.c
  ${FT}/src/type1/type1.c ${FT}/src/type42/type42.c)
add_library(freetype SHARED ${FT_SOURCES})
# The headers of whoever links this (cimgui) must see the same configuration as the library.
target_include_directories(freetype PUBLIC "${FT}/include" "${CMAKE_CURRENT_LIST_DIR}")
target_compile_definitions(freetype PUBLIC "FT_CONFIG_OPTIONS_H=<apogee_ftoption.h>")
target_compile_definitions(freetype PRIVATE FT2_BUILD_LIBRARY "FT_CONFIG_MODULES_H=<apogee_ftmodule.h>")
if(WIN32)
  target_compile_definitions(freetype PRIVATE DLL_EXPORT _CRT_SECURE_NO_WARNINGS)
endif()
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  target_link_options(freetype PRIVATE -Wl,--no-undefined)
endif()
apogee_library(freetype)
