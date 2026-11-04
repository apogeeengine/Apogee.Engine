// See apogee_luau.h for what this layer is for.
#include "apogee_luau.h"

#include <stdio.h>
#include <stdlib.h>
#include <string>
#include <vector>

#include "lualib.h"
#include "luacode.h"
#include "ldo.h"
#include "lstate.h"
#include "Luau/CodeGen.h"

namespace
{

struct Vm
{
    size_t memoryLimit = 0;
    size_t memoryUsed = 0;
    // True while Luau code is the topmost thing running (inside a protected call or resume, and not inside a managed function that
    // it called). Only then may an allocation fail: an error raised anywhere else has nothing to catch it, or would unwind managed code.
    bool luauRunning = false;
    bool managedOverLimit = false;

    size_t categoryLimit[LUA_MEMORY_CATEGORIES] = {};
    int categoryOver = -1;

    int defaultBudget = 0;
    int callDepth = 0;
    double deadline = 0; // lua_clock() seconds, 0 for none
    bool expired = false;
    int budgetMilliseconds = 0;
    unsigned ticks = 0;

    bool native = false;

    // Globals the host sets to things whose members change (objects): the compiler must not cache `global.field` in the function.
    std::vector<std::string> mutableGlobals;
};

Vm* vmOf(lua_State* L)
{
    return static_cast<Vm*>(lua_callbacks(L)->userdata);
}

void* allocate(void* ud, void* ptr, size_t osize, size_t nsize)
{
    Vm* vm = static_cast<Vm*>(ud);
    if (nsize == 0)
    {
        free(ptr);
        vm->memoryUsed -= osize;
        return nullptr;
    }

    if (nsize > osize && vm->memoryLimit != 0 && vm->memoryUsed + (nsize - osize) > vm->memoryLimit)
    {
        // Luau code is running: the allocation fails and Luau raises the memory error. Managed code and the host must not be unwound,
        // so they get their memory and the error is raised when the managed function returns (trampoline).
        if (vm->luauRunning)
            return nullptr;
        vm->managedOverLimit = true;
    }

    void* block = realloc(ptr, nsize);
    if (!block)
    {
        if (!vm->luauRunning)
        {
            fputs("apogee_luau: out of memory while managed code was running\n", stderr);
            abort();
        }
        return nullptr;
    }

    vm->memoryUsed = vm->memoryUsed - osize + nsize;
    return block;
}

void onAllocate(lua_State* L, void*, size_t, size_t, uint8_t memcat, int, int)
{
    Vm* vm = vmOf(L);
    size_t limit = vm->categoryLimit[memcat];
    if (limit != 0 && lua_totalbytes(L, memcat) > limit)
        vm->categoryOver = memcat;
}

void interrupt(lua_State* L, int gc)
{
    if (gc >= 0)
        return;

    Vm* vm = vmOf(L);

    if (vm->categoryOver >= 0 && L->activememcat == vm->categoryOver)
    {
        // The flag is set by an allocation and may be old: only a category that is over its limit now raises.
        int category = vm->categoryOver;
        vm->categoryOver = -1;
        if (lua_totalbytes(L, category) > vm->categoryLimit[category])
            luaD_throw(L, LUA_ERRMEM);
    }

    if (vm->deadline == 0)
        return;

    // Once the deadline has passed it stays passed until the outermost call returns, so a script's own pcall cannot swallow it.
    if (!vm->expired)
    {
        if ((++vm->ticks & 127) != 0 || lua_clock() < vm->deadline)
            return;
        vm->expired = true;
    }

    lua_pushfstring(L, "script exceeded its time budget of %d ms", vm->budgetMilliseconds);
    lua_error(L);
}

int trampoline(lua_State* L)
{
    Vm* vm = vmOf(L);
    apogee_luau_function fn = reinterpret_cast<apogee_luau_function>(lua_tolightuserdata(L, lua_upvalueindex(1)));

    bool wasRunning = vm->luauRunning;
    vm->luauRunning = false;
    int results = fn(L);
    vm->luauRunning = wasRunning;

    if (vm->managedOverLimit)
    {
        vm->managedOverLimit = false;
        if (vm->memoryUsed > vm->memoryLimit)
            luaD_throw(L, LUA_ERRMEM);
    }

    if (results < 0)
        lua_error(L);
    return results;
}

int tracebackHandler(lua_State* L)
{
    if (const char* message = lua_tolstring(L, 1, nullptr))
    {
        luaL_traceback(L, L, message, 1);
        return 1;
    }

    return 1; // not a string: the error value as it is
}

apogee_luau_result classify(Vm* vm, int status)
{
    switch (status)
    {
    case LUA_OK:
        return APOGEE_LUAU_OK;
    case LUA_YIELD:
        return APOGEE_LUAU_YIELDED;
    case LUA_ERRMEM:
        return APOGEE_LUAU_MEMORY_ERROR;
    default:
        return vm->expired ? APOGEE_LUAU_TIMEOUT : APOGEE_LUAU_RUNTIME_ERROR;
    }
}

// Arms the deadline in the outermost call. Returns whether this call is the outermost one.
bool enter(Vm* vm, int budgetMilliseconds)
{
    if (vm->callDepth++ != 0)
        return false;

    vm->expired = false;
    vm->budgetMilliseconds = budgetMilliseconds > 0 ? budgetMilliseconds : vm->defaultBudget;
    vm->deadline = vm->budgetMilliseconds > 0 ? lua_clock() + vm->budgetMilliseconds / 1000.0 : 0;
    return true;
}

void leave(Vm* vm, bool outermost)
{
    vm->callDepth--;
    if (outermost)
    {
        vm->deadline = 0;
        vm->expired = false;
    }
}

} // namespace

lua_State* apogee_luau_newstate(size_t memory_limit)
{
    Vm* vm = new Vm();
    vm->memoryLimit = memory_limit;

    lua_State* L = lua_newstate(allocate, vm);
    if (!L)
    {
        delete vm;
        return nullptr;
    }

    lua_Callbacks* callbacks = lua_callbacks(L);
    callbacks->userdata = vm;
    callbacks->interrupt = interrupt;
    callbacks->onallocate = onAllocate;

    if (Luau::CodeGen::isSupported())
    {
        Luau::CodeGen::create(L);
        vm->native = true;
    }

    return L;
}

void apogee_luau_close(lua_State* L)
{
    Vm* vm = vmOf(L);
    lua_close(L);
    delete vm;
}

void apogee_luau_set_memory_limit(lua_State* L, size_t bytes)
{
    vmOf(L)->memoryLimit = bytes;
}

size_t apogee_luau_memory_used(lua_State* L)
{
    return vmOf(L)->memoryUsed;
}

void apogee_luau_set_category_limit(lua_State* L, int category, size_t bytes)
{
    if (category >= 0 && category < LUA_MEMORY_CATEGORIES)
        vmOf(L)->categoryLimit[category] = bytes;
}

void apogee_luau_set_time_budget(lua_State* L, int milliseconds)
{
    vmOf(L)->defaultBudget = milliseconds;
}

void apogee_luau_add_mutable_global(lua_State* L, const char* name)
{
    std::vector<std::string>& names = vmOf(L)->mutableGlobals;
    for (const std::string& known : names)
        if (known == name)
            return;
    names.push_back(name);
}

int apogee_luau_native_enabled(lua_State* L)
{
    return vmOf(L)->native ? 1 : 0;
}

void apogee_luau_pushfunction(lua_State* L, apogee_luau_function fn, const char* debug_name, int nup)
{
    lua_pushlightuserdatatagged(L, reinterpret_cast<void*>(fn), 0);
    lua_insert(L, -(nup + 1));
    lua_pushcclosurek(L, trampoline, debug_name, nup + 1, nullptr);
}

apogee_luau_result apogee_luau_pcall(lua_State* L, int nargs, int nresults, int budget_milliseconds)
{
    Vm* vm = vmOf(L);

    int function = lua_gettop(L) - nargs;
    lua_pushcclosurek(L, tracebackHandler, "traceback", 0, nullptr);
    lua_insert(L, function);

    bool outermost = enter(vm, budget_milliseconds);
    bool wasRunning = vm->luauRunning;
    vm->luauRunning = true;
    int status = lua_pcall(L, nargs, nresults, function);
    vm->luauRunning = wasRunning;
    apogee_luau_result result = classify(vm, status);
    leave(vm, outermost);

    lua_remove(L, function);
    return result;
}

apogee_luau_result apogee_luau_resume(lua_State* L, lua_State* from, int nargs, int budget_milliseconds)
{
    Vm* vm = vmOf(L);

    bool outermost = enter(vm, budget_milliseconds);
    bool wasRunning = vm->luauRunning;
    vm->luauRunning = true;
    int status = lua_resume(L, from, nargs);
    vm->luauRunning = wasRunning;
    apogee_luau_result result = classify(vm, status);
    leave(vm, outermost);

    return result;
}

apogee_luau_result apogee_luau_load(lua_State* L, const char* chunk_name, const char* source, size_t size, int env)
{
    lua_CompileOptions options = {};
    options.optimizationLevel = 1;
    options.debugLevel = 1;
    options.vectorCtor = "Vector3";
    options.vectorType = "Vector3";

    // `object.field` on a global would otherwise be resolved once, when the function loads, and never read again.
    Vm* vm = vmOf(L);
    std::vector<const char*> mutableGlobals;
    for (const std::string& name : vm->mutableGlobals)
        mutableGlobals.push_back(name.c_str());
    mutableGlobals.push_back(nullptr);
    options.mutableGlobals = mutableGlobals.data();

    size_t bytecodeSize = 0;
    char* bytecode = luau_compile(source, size, &options, &bytecodeSize);
    int status = luau_load(L, chunk_name, bytecode, bytecodeSize, env);
    free(bytecode);

    if (status != 0)
        return APOGEE_LUAU_COMPILE_ERROR;

    if (vm->native)
        Luau::CodeGen::compile(L, -1, Luau::CodeGen::CodeGen_OnlyNativeModules);

    return APOGEE_LUAU_OK;
}

void apogee_luau_free(void* memory)
{
    free(memory);
}
