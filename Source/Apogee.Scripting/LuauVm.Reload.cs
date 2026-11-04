using System;
using System.Collections.Generic;
using System.IO;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.Luau;
using Apogee.Reflection;
using LuaType = Apogee.Native.Luau.Type;

namespace Apogee.Scripting;

/// <summary>What <see cref="LuauVm.ReloadModule"/> did.</summary>
/// <param name="Success">False when the module is not loaded or the new code failed (then nothing changed).</param>
/// <param name="Message">Why it failed, or how the module was updated.</param>
public sealed record ModuleReload(bool Success, string Message);

public sealed unsafe partial class LuauVm
{
    /// <summary>The modules <c>require</c> has loaded from files, by path.</summary>
    public IReadOnlyCollection<string> LoadedModules => _scopes.Keys;

    public bool IsModuleLoaded(string path) => _scopes.ContainsKey(ModuleResolver.Normalize(path));

    /// <summary>
    /// Runs a loaded module again from its file and updates it where it is: the module's table that every script that required it holds gets the new fields (and loses the ones the new code
    /// does not have), so they see the new functions without being reloaded themselves. What the module registered when it first loaded (functions that draw, signal connections, systems,
    /// <c>defs.on_change</c> listeners) is taken back and registered again by the new run. If the new code fails (a syntax error, a runtime error, a timeout) nothing changes: the old module and
    /// its registrations stay. A module may have a function <c>on_reload(new, old)</c>, called before the update, to take state from the old table. A function a script copied into a local
    /// (<c>local f = module.f</c>) stays the old one; a module that returns something other than a table is replaced in the cache only.
    /// </summary>
    public ModuleReload ReloadModule(string path)
    {
        ThrowIfDisposed();
        string key = ModuleResolver.Normalize(path);
        if (!_scopes.TryGetValue(key, out ReloadScope? old))
        {
            return new ModuleReload(false, $"{key} is not a loaded module.");
        }

        State* L = _main;
        int top = Lua.Gettop(L);
        Stack.Reserve(L, 12);
        var scope = new ReloadScope();
        try
        {
            using (ScriptScope.Enter(scope))
            {
                RunModule(L, new ResolvedModule(key, "@" + key, key, null));
            }
        }
        catch (Exception e) when (e is ScriptException or ValueException or IOException)
        {
            scope.Cleanup();
            Lua.Settop(L, top);
            return new ModuleReload(false, e.Message);
        }

        int fresh = top + 1; // the value the new run returned
        PushModuleCache(L);
        int cache = top + 2;
        bool patched = false;
        fixed (byte* cacheKey = Utf8.ToZeroTerminated(key))
        {
            int previous = Lua.Rawgetfield(L, cache, cacheKey); // top + 3
            if (previous == (int)LuaType.Ttable && Lua.Type(L, fresh) == (int)LuaType.Ttable && Lua.Rawequal(L, top + 3, fresh) == 0 && Lua.Getreadonly(L, top + 3) == 0)
            {
                CallOnReload(L, fresh, top + 3, key);
                PatchTable(L, top + 3, fresh);
                patched = true;
            }

            if (!patched)
            {
                Lua.Pushvalue(L, fresh);
                Lua.Rawsetfield(L, cache, cacheKey);
            }
        }

        Lua.Settop(L, top);
        old.Cleanup();
        _scopes[key] = scope;
        return new ModuleReload(true, patched ? "updated in place" : "replaced");
    }

    /// <summary>Makes <paramref name="oldIndex"/> have what <paramref name="newIndex"/> has: its fields, and its metatable.</summary>
    private static void PatchTable(State* L, int oldIndex, int newIndex)
    {
        Stack.Reserve(L, 6);
        for (int iterator = 0; (iterator = Lua.Rawiter(L, oldIndex, iterator)) >= 0;)
        {
            Lua.Pushvalue(L, -2);
            Lua.Pushnil(L);
            Lua.Rawset(L, oldIndex); // old[key] = nil: clearing existing fields during a traversal is allowed
            Lua.Settop(L, -3);
        }

        for (int iterator = 0; (iterator = Lua.Rawiter(L, newIndex, iterator)) >= 0;)
        {
            Lua.Pushvalue(L, -2);
            Lua.Pushvalue(L, -2);
            Lua.Rawset(L, oldIndex);
            Lua.Settop(L, -3);
        }

        if (Lua.Getmetatable(L, newIndex) != 0)
        {
            _ = Lua.Setmetatable(L, oldIndex);
        }
    }

    /// <summary>Calls <c>new.on_reload(new, old)</c> if the new module has it, as the module's own owner. A failure is logged: the update goes on.</summary>
    private void CallOnReload(State* L, int newIndex, int oldIndex, string key)
    {
        if (GetField(L, newIndex, "on_reload") != (int)LuaType.Tfunction)
        {
            Lua.Settop(L, -2);
            return;
        }

        ScriptOwner owner = _options.OwnerOfModule?.Invoke(key) ?? BaseOwner;
        State* thread = NewThread(owner, out int reference);
        try
        {
            Lua.Xmove(L, thread, 1); // the function
            Lua.Pushvalue(L, newIndex);
            Lua.Pushvalue(L, oldIndex);
            Lua.Xmove(L, thread, 2);
            CallResult result = Lua.ProtectedResume(thread, L, 2, owner.TimeBudgetMilliseconds);
            ThrowIfFailed(result, thread);
        }
        catch (ScriptException e)
        {
            Log.Warn("Scripting", $"{key}: on_reload failed: {e.Message}");
        }
        finally
        {
            _ = Lua.Unref(_main, reference);
        }
    }
}
