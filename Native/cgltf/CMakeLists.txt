# cgltf as a shared library. The header has no export macro, so every function is exported.
add_library(cgltf SHARED cgltf.c)
target_include_directories(cgltf PRIVATE "${APOGEE_THIRDPARTY}/cgltf")
set_target_properties(cgltf PROPERTIES C_VISIBILITY_PRESET default WINDOWS_EXPORT_ALL_SYMBOLS ON)
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  target_link_options(cgltf PRIVATE -Wl,--no-undefined)
endif()
apogee_library(cgltf)
