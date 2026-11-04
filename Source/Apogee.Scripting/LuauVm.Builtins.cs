using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.Luau;
using Apogee.Reflection;
using LuaDebug = Apogee.Native.Luau.Debug;
using LuaType = Apogee.Native.Luau.Type;

namespace Apogee.Scripting;

public sealed unsafe partial class LuauVm
{
    // The functions below are called by Luau, so no exception may leave them: Guard turns one into the script error.
    private static int Guard(State* L, delegate*<State*, int> body)
    {
        try
        {
            return body(L);
        }
        catch (ScriptException e)
        {
            return Fail(L, e.Message);
        }
        catch (ValueException e)
        {
            return Fail(L, e.Message);
        }
#pragma warning disable CA1031 // A bug in a builtin must not unwind through Luau either; it is logged and the script gets the error.
        catch (Exception e)
#pragma warning restore CA1031
        {
            Log.Error("Scripting", $"A builtin failed: {e}");
            return Fail(L, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static int Fail(State* L, string message)
    {
        if (Stack.Reserve(L, 2))
        {
            Stack.PushString(L, message);
        }

        return Lua.Raise;
    }

    private void OpenLibraries()
    {
        Lua.LuaLOpenlibs(_main);

        // Dynamic code and environment access are not for scripts: modules come through require, and the sandbox owns the environments.
        foreach (string name in new[] { "getfenv", "setfenv", "loadstring" })
        {
            Lua.Pushnil(_main);
            SetMainGlobal(name);
        }

        RegisterGlobal("print", &Print);
        RegisterGlobal("warn", &Warn);
        RegisterGlobal("require", &Require);
        RegisterGlobal("Vector3", &NewVector3);
        InstallBridge();

        Lua.LuaLSandbox(_main);
    }

    /// <summary>Sets the global from the value on top of the stack of the main thread, popping it.</summary>
    private void SetMainGlobal(string name)
    {
        fixed (byte* key = Utf8.ToZeroTerminated(name))
        {
            Lua.Rawsetfield(_main, Lua.Globalsindex, key);
        }
    }

    private void RegisterGlobal(string name, delegate* unmanaged[Cdecl]<State*, int> function)
    {
        Lua.PushManagedFunction(_main, function, NativeName(name), 0);
        SetMainGlobal(name);
    }

    /// <summary>
    /// The name as a C string that lives as long as the VM. Luau keeps the debug name of a native function by pointer, without copying it
    /// (tracebacks read it later), so a pinned managed array would dangle.
    /// </summary>
    internal byte* NativeName(string name)
    {
        if (!_nativeNames.TryGetValue(name, out nint pointer))
        {
            pointer = Marshal.StringToCoTaskMemUTF8(name);
            _nativeNames[name] = pointer;
        }

        return (byte*)pointer;
    }

    // ---- print, warn ---------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Print(State* L) => Guard(L, &PrintBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Warn(State* L) => Guard(L, &WarnBody);

    private static int PrintBody(State* L) => Output(L, warning: false);

    private static int WarnBody(State* L) => Output(L, warning: true);

    private static int Output(State* L, bool warning)
    {
        LuauVm vm = VmOf(L);
        int count = Lua.Gettop(L);
        var text = new StringBuilder();
        for (int i = 1; i <= count; i++)
        {
            if (i > 1)
            {
                text.Append('\t');
            }

            text.Append(Stack.Describe(L, i));
        }

        ScriptOwner owner = vm.OwnerOf(L);
        if (vm._options.Output is { } output)
        {
            output(owner, text.ToString(), warning);
        }
        else if (warning)
        {
            Log.Warn("Script:" + owner.Id, text.ToString());
        }
        else
        {
            Log.Info("Script:" + owner.Id, text.ToString());
        }

        return 0;
    }

    // ---- Vector3 ---------------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NewVector3(State* L) => Guard(L, &NewVector3Body);

    /// <summary>Vector3(x, y, z): missing components are zero. The compiler turns calls with constant arguments into constants.</summary>
    private static int NewVector3Body(State* L)
    {
        int count = Lua.Gettop(L);
        if (count > 3)
        {
            throw new ValueException("Vector3 takes up to 3 numbers");
        }

        Span<float> components = stackalloc float[3];
        for (int i = 1; i <= count; i++)
        {
            if (!Stack.Is(L, i, LuaType.Tnumber))
            {
                throw new ValueException($"Vector3 expects numbers, got {Stack.Describe(L, i)}");
            }

            components[i - 1] = (float)Lua.Tonumberx(L, i, null);
        }

        Lua.Pushvector(L, components[0], components[1], components[2]);
        return 1;
    }

    // ---- require ---------------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Require(State* L) => Guard(L, &RequireBody);

    private static int RequireBody(State* L)
    {
        LuauVm vm = VmOf(L);
        if (Lua.Gettop(L) != 1 || !Stack.Is(L, 1, LuaType.Tstring))
        {
            throw new ValueException("require expects the path of a module as its only argument");
        }

        string request = Stack.ReadString(L, 1);
        ResolvedModule module = vm._resolver.Resolve(CallerChunk(L), request);
        if (module.Path is not null && vm._options.CanLoadModule is { } canLoad && !canLoad(vm.OwnerOf(L), module.Path))
        {
            throw new ValueException($"module '{request}' is not one that this script may load");
        }

        byte[] key = Utf8.ToZeroTerminated(module.Key);

        // The cache is a table in the registry, by module key.
        PushModuleCache(L);
        fixed (byte* keyPointer = key)
        {
            if (Lua.Rawgetfield(L, -1, keyPointer) != (int)LuaType.Tnil)
            {
                Lua.Remove(L, -2);
                return 1;
            }

            Lua.Settop(L, -2); // the nil

            if (!vm._loading.Add(module.Key))
            {
                throw new ValueException($"cyclic require: {module.Key} is already being loaded");
            }

            var scope = new ReloadScope();
            try
            {
                using (ScriptScope.Enter(scope))
                {
                    vm.RunModule(L, module);
                }

                if (module.Path is not null)
                {
                    vm._scopes[module.Key] = scope; // what the module registered, for ReloadModule
                }
            }
            catch
            {
                scope.Cleanup(); // a module that failed to load leaves nothing registered
                throw;
            }
            finally
            {
                vm._loading.Remove(module.Key);
            }

            Lua.Pushvalue(L, -1);
            Lua.Rawsetfield(L, -3, keyPointer);
            Lua.Remove(L, -2);
            return 1;
        }
    }

    /// <summary>Runs the module in a thread of its own (so it cannot yield into the requirer) and leaves its single result on top of <paramref name="requester"/>.</summary>
    private void RunModule(State* requester, ResolvedModule module)
    {
        ScriptOwner owner = _options.OwnerOfModule?.Invoke(module.Key) ?? OwnerOf(requester);
        string source = module.EmbeddedSource ?? _options.Files.ReadAllText(module.Path!);

        State* thread = NewThread(owner, out int reference);
        try
        {
            Load(thread, module.ChunkName, source);
            CallResult result = Lua.ProtectedResume(thread, requester, 0, owner.TimeBudgetMilliseconds);
            if (result == CallResult.Yielded)
            {
                throw new ValueException($"module {module.Key} can not yield");
            }

            ThrowIfFailed(result, thread);
            if (Lua.Gettop(thread) != 1)
            {
                throw new ValueException($"module {module.Key} must return a single value, it returned {Lua.Gettop(thread)}");
            }

            Lua.Xmove(thread, requester, 1);
        }
        finally
        {
            _ = Lua.Unref(_main, reference);
        }
    }

    private static void PushModuleCache(State* L)
    {
        fixed (byte* name = "apogee.modules\0"u8)
        {
            if (Lua.Rawgetfield(L, Lua.Registryindex, name) == (int)LuaType.Ttable)
            {
                return;
            }

            Lua.Settop(L, -2);
            Lua.Createtable(L, 0, 8);
            Lua.Pushvalue(L, -1);
            Lua.Rawsetfield(L, Lua.Registryindex, name);
        }
    }

    /// <summary>
    /// The chunk name of the Luau function that is requiring: the nearest caller that is not a native function, so that
    /// <c>pcall(require, "./x")</c> still resolves from the script that wrote it.
    /// </summary>
    private static string CallerChunk(State* L)
    {
        LuaDebug info = default;
        fixed (byte* what = "s\0"u8)
        {
            for (int level = 1; Lua.Getinfo(L, level, what, &info) != 0; level++)
            {
                if (Utf8.FromPointer(info.What) != "C")
                {
                    return Utf8.FromPointer(info.Source) ?? "=";
                }
            }
        }

        return "=";
    }
}
