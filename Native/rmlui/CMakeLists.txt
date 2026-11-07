# RmlUi (ThirdParty/rmlui: the core, the FreeType font engine and the debugger) with our C API over it, as one shared library, `rmlui`. RmlUi is built as a static library would be
# (RMLUI_STATIC_LIB: its own API is not exported) and the symbols are hidden, so what the library exports is apogee_rmlui.h and nothing else. The glyphs come from our freetype.
set(RMLUI "${APOGEE_THIRDPARTY}/rmlui")
file(GLOB RMLUI_SOURCES CONFIGURE_DEPENDS
  "${RMLUI}/Source/Core/*.cpp" "${RMLUI}/Source/Core/Elements/*.cpp" "${RMLUI}/Source/Core/FontEngineDefault/*.cpp" "${RMLUI}/Source/Core/Layout/*.cpp"
  "${RMLUI}/Source/Debugger/*.cpp")
add_library(rmlui SHARED ${RMLUI_SOURCES} apogee_rmlui.cpp)
target_include_directories(rmlui PRIVATE "${RMLUI}/Include" "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_definitions(rmlui PRIVATE RMLUI_STATIC_LIB RMLUI_FONT_ENGINE_FREETYPE "RMLUI_VERSION=\"6.3\"")
target_link_libraries(rmlui PRIVATE freetype)
# The API macro of apogee_rmlui.h: default visibility on Linux and macOS (everything else is hidden), dllexport on Windows.
if(WIN32)
  target_compile_definitions(rmlui PRIVATE "APOGEE_RMLUI_API=__declspec(dllexport)")
else()
  target_compile_definitions(rmlui PRIVATE "APOGEE_RMLUI_API=__attribute__((visibility(\"default\")))")
  set_target_properties(rmlui PROPERTIES BUILD_WITH_INSTALL_RPATH ON INSTALL_RPATH "$<IF:$<BOOL:${APPLE}>,@loader_path,$ORIGIN>")
endif()
apogee_library(rmlui)
