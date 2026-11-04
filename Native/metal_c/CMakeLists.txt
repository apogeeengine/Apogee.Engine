# A C API over Metal (Objective-C only), see apogee_metal.h. macOS only: Native/CMakeLists.txt adds this directory on Apple systems. Compiled with ARC and linked against the system frameworks.
add_library(metal_c SHARED apogee_metal.m)
target_compile_options(metal_c PRIVATE -fobjc-arc)
target_include_directories(metal_c PUBLIC "${CMAKE_CURRENT_LIST_DIR}")
target_link_libraries(metal_c PRIVATE "-framework Metal" "-framework QuartzCore" "-framework Foundation")
set_target_properties(metal_c PROPERTIES C_VISIBILITY_PRESET default OBJC_VISIBILITY_PRESET default)
apogee_library(metal_c)
