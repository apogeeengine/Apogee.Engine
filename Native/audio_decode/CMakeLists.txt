# The audio decoders (dr_wav, dr_flac, dr_mp3 and stb_vorbis, all public domain or MIT-0/MIT) behind our small C API (apogee_audio.h), as one shared library.
# They decode from memory only: the files come through the engine's file system.
add_library(audio_decode SHARED apogee_audio.c)
target_include_directories(audio_decode PRIVATE "${APOGEE_THIRDPARTY}/dr_libs" "${APOGEE_THIRDPARTY}/stb")
set_target_properties(audio_decode PROPERTIES C_VISIBILITY_PRESET hidden)
if(NOT WIN32)
  target_link_libraries(audio_decode PRIVATE m)
endif()
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  target_link_options(audio_decode PRIVATE -Wl,--no-undefined)
endif()
apogee_library(audio_decode)
