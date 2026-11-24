# Dear ImGui (docking) and its C API, cimgui, as one shared library, with ImGuizmo, ImPlot and imnodes and our own C APIs over them (weft_imguizmo, weft_implot, weft_imnodes). They
# have to be in this library: they use ImGui's C++ API and its context, and ImGui's symbols are hidden. cimgui.h exports its functions itself (the API macro); the shims have theirs below.
# Glyphs come from FreeType (misc/freetype/imgui_freetype.cpp, with IMGUI_ENABLE_FREETYPE ImGui makes it the default font loader), linked as our own freetype library
# that sits next to this one.
set(CIMGUI "${WEFT_THIRDPARTY}/cimgui")
set(IMGUIZMO "${WEFT_THIRDPARTY}/imguizmo")
set(IMPLOT "${WEFT_THIRDPARTY}/implot")
set(IMNODES "${WEFT_THIRDPARTY}/imnodes")
add_library(cimgui SHARED
  "${CIMGUI}/cimgui.cpp"
  "${CIMGUI}/imgui/imgui.cpp" "${CIMGUI}/imgui/imgui_demo.cpp" "${CIMGUI}/imgui/imgui_draw.cpp"
  "${CIMGUI}/imgui/imgui_tables.cpp" "${CIMGUI}/imgui/imgui_widgets.cpp"
  "${CIMGUI}/imgui/misc/freetype/imgui_freetype.cpp"
  "${IMGUIZMO}/ImGuizmo.cpp" "${IMPLOT}/implot.cpp" "${IMPLOT}/implot_items.cpp" "${IMNODES}/imnodes.cpp"
  weft_imguizmo.cpp weft_implot.cpp weft_imnodes.cpp)
target_include_directories(cimgui PRIVATE "${CIMGUI}" "${CIMGUI}/imgui" "${IMGUIZMO}" "${IMPLOT}" "${IMNODES}" "${CMAKE_CURRENT_SOURCE_DIR}")
# cimgui.cpp calls the stb_truetype loader too, so both are enabled (ImGui supports that); FreeType is the default.
target_compile_definitions(cimgui PRIVATE IMGUI_ENABLE_FREETYPE IMGUI_ENABLE_STB_TRUETYPE)
# The API macros of the shims' headers: default visibility on Linux and macOS (everything else is hidden), dllexport on Windows.
if(WIN32)
  target_compile_definitions(cimgui PRIVATE "WEFT_IMGUIZMO_API=__declspec(dllexport)" "WEFT_IMPLOT_API=__declspec(dllexport)" "WEFT_IMNODES_API=__declspec(dllexport)")
else()
  target_compile_definitions(cimgui PRIVATE "WEFT_IMGUIZMO_API=__attribute__((visibility(\"default\")))" "WEFT_IMPLOT_API=__attribute__((visibility(\"default\")))" "WEFT_IMNODES_API=__attribute__((visibility(\"default\")))")
endif()
target_link_libraries(cimgui PRIVATE freetype)
if(NOT WIN32)
  set_target_properties(cimgui PROPERTIES BUILD_WITH_INSTALL_RPATH ON INSTALL_RPATH "$<IF:$<BOOL:${APPLE}>,@loader_path,$ORIGIN>")
endif()
weft_library(cimgui)
