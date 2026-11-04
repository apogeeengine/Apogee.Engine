# Luau's VM, compiler and native code generator, plus our layer over them (apogee_luau.h), as one shared library with the C API
# under C linkage (LUA_API, LUACODE_API and LUACODEGEN_API are Luau's own hooks for this). LUA_USE_LONGJMP=1: errors must not
# be C++ exceptions, which cannot cross managed frames.
set(LUAU "${APOGEE_THIRDPARTY}/luau")
file(GLOB LUAU_SOURCES "${LUAU}/Common/src/*.cpp" "${LUAU}/Ast/src/*.cpp" "${LUAU}/Bytecode/src/*.cpp" "${LUAU}/Compiler/src/*.cpp"
  "${LUAU}/VM/src/*.cpp" "${LUAU}/CodeGen/src/*.cpp")
add_library(luau SHARED ${LUAU_SOURCES} apogee_luau.cpp)
# A shared library may keep undefined symbols on Linux; fail the link instead of crashing on the first call.
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  target_link_options(luau PRIVATE -Wl,--no-undefined)
endif()
target_include_directories(luau PRIVATE
  "${LUAU}/Common/include" "${LUAU}/Ast/include" "${LUAU}/Bytecode/include" "${LUAU}/Compiler/include" "${LUAU}/VM/include"
  "${LUAU}/VM/src" "${LUAU}/CodeGen/include" "${CMAKE_CURRENT_LIST_DIR}")
if(WIN32)
  set(LUAU_EXPORT "extern \"C\" __declspec(dllexport)")
  set(APOGEE_EXPORT "__declspec(dllexport)")
else()
  set(LUAU_EXPORT "extern \"C\" __attribute__((visibility(\"default\")))")
  set(APOGEE_EXPORT "__attribute__((visibility(\"default\")))")
endif()
target_compile_definitions(luau PRIVATE "LUA_API=${LUAU_EXPORT}" "LUACODE_API=${LUAU_EXPORT}" "LUACODEGEN_API=${LUAU_EXPORT}"
  "APOGEE_LUAU_API=${APOGEE_EXPORT}" LUA_USE_LONGJMP=1)
apogee_library(luau)
