/* Our layer over Luau's C API, built into the luau library (Native/luau/CMakeLists.txt).
 *
 * Luau is built with LUA_USE_LONGJMP: an error is a longjmp to the nearest protected call. A longjmp must never cross a managed
 * frame, so managed code never calls anything that can raise an error on input a script controls (lua_error, lua_call, the
 * luaL_check* family, calls that run metamethods…): the bindings leave those out (Configs/luau.json). What remains can only raise
 * on a programming error (writing a read-only table, no stack space after lua_checkstack) or when memory runs out, and memory
 * is handled here: only an allocation made by Luau code can be refused; one made by a managed function, or by the host outside a
 * call, never is, and going over the limit raises the error when the managed function returns.
 *
 * Managed functions are not pushed with lua_pushcclosurek; weft_luau_pushfunction wraps them in a C function that counts
 * the managed depth, turns a negative result into lua_error and raises the deferred memory error.
 *
 * One state belongs to one thread at a time. */
#pragma once

#include <stddef.h>
#include <stdint.h>

#include "lua.h"

#ifndef WEFT_LUAU_API
#define WEFT_LUAU_API extern
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* What a protected call or a resume ended with. */
typedef enum weft_luau_result
{
    WEFT_LUAU_OK = 0,
    /* weft_luau_resume: the coroutine yielded. */
    WEFT_LUAU_YIELDED = 1,
    /* A script error; the message (with a traceback, for weft_luau_pcall) is on top of the stack. */
    WEFT_LUAU_RUNTIME_ERROR = 2,
    /* The call outlived its time budget; the message is on top of the stack. */
    WEFT_LUAU_TIMEOUT = 3,
    /* A memory limit was exceeded. */
    WEFT_LUAU_MEMORY_ERROR = 4,
    /* weft_luau_load: the source did not compile; the message is on top of the stack. */
    WEFT_LUAU_COMPILE_ERROR = 5,
} weft_luau_result;

/* A function implemented in managed code: returns the number of results it left on top of the stack, or
 * WEFT_LUAU_RAISE with the error value on top of the stack. Upvalue 1 is ours: the function's own upvalues start at 2
 * (lua_upvalueindex(i + 1) for its i-th). */
typedef int (*weft_luau_function)(lua_State* L);

#define WEFT_LUAU_RAISE (-1)

/* What a managed function returns to yield the thread it runs in, with no results: whatever the next weft_luau_resume passes
 * becomes the results of the call, as for any C function that yields without a continuation. A thread that cannot yield (inside a
 * metamethod, a protected call of the host, a module's top level) gets Luau's own "attempt to yield across metamethod/C-call
 * boundary" error instead. */
#define WEFT_LUAU_YIELD (-2)

/* A new state with the allocator, interrupt and memory accounting installed. memory_limit is a hard limit on the bytes the
 * state may allocate while Luau code runs (0: none). Native code generation is created when the machine supports it. */
WEFT_LUAU_API lua_State* weft_luau_newstate(size_t memory_limit);

/* Closes a state made by weft_luau_newstate. */
WEFT_LUAU_API void weft_luau_close(lua_State* L);

/* Changes the hard limit (0: none). Set it after the libraries are open: an allocation that fails outside a protected call cannot be caught. */
WEFT_LUAU_API void weft_luau_set_memory_limit(lua_State* L, size_t bytes);

/* Bytes allocated by the state. */
WEFT_LUAU_API size_t weft_luau_memory_used(lua_State* L);

/* A soft limit on a memory category (lua_setmemcat), in bytes, 0 for none. Going over it raises a memory error in code of that
 * category at its next function call or loop iteration, so it can be exceeded by what one operation allocates; the hard limit
 * of weft_luau_newstate cannot. */
WEFT_LUAU_API void weft_luau_set_category_limit(lua_State* L, int category, size_t bytes);

/* The time budget of a protected call or resume that is not given one, in milliseconds (0: none). */
WEFT_LUAU_API void weft_luau_set_time_budget(lua_State* L, int milliseconds);

/* Declares a global whose fields change (a host object): scripts compiled from now on read `name.field` every time. Without it the
 * compiler resolves a chain of globals once, when the function loads, and caches the value. Call it before the scripts that use it are loaded. */
WEFT_LUAU_API void weft_luau_add_mutable_global(lua_State* L, const char* name);

/* Whether native code generation is on for this state. */
WEFT_LUAU_API int weft_luau_native_enabled(lua_State* L);

/* Pushes a function that calls fn, with nup upvalues taken from the top of the stack (see weft_luau_function). */
WEFT_LUAU_API void weft_luau_pushfunction(lua_State* L, weft_luau_function fn, const char* debug_name, int nup);

/* lua_pcall with a traceback appended to a string error and a time budget (milliseconds, 0 for the default one). The budget is
 * armed by the outermost call only: calls made inside it share its deadline, and once it has passed every function call or loop
 * iteration raises, so a script cannot get past it with pcall. */
WEFT_LUAU_API weft_luau_result weft_luau_pcall(lua_State* L, int nargs, int nresults, int budget_milliseconds);

/* lua_resume under the same time budget rules. A failure leaves the message on top of the coroutine's stack. */
WEFT_LUAU_API weft_luau_result weft_luau_resume(lua_State* L, lua_State* from, int nargs, int budget_milliseconds);

/* Compiles source and pushes it as a function (WEFT_LUAU_OK), or pushes the compile error. The bytecode never leaves the
 * library. With native code generation on, a chunk that starts with --!native is compiled to machine code. env is the stack index
 * of the function's environment, 0 for the globals. */
WEFT_LUAU_API weft_luau_result weft_luau_load(lua_State* L, const char* chunk_name, const char* source, size_t size, int env);

/* Frees memory that luau_compile returned (it comes from this library's C runtime, which is not necessarily the caller's). */
WEFT_LUAU_API void weft_luau_free(void* memory);

#ifdef __cplusplus
}
#endif
