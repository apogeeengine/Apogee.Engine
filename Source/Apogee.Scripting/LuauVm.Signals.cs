using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apogee.Core.Collections;
using Apogee.Native.Luau;
using Apogee.Reflection;
using LuaType = Apogee.Native.Luau.Type;

namespace Apogee.Scripting;

public sealed unsafe partial class LuauVm
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SignalRef
    {
        public uint Index;
        public uint Generation;
        public int Definition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectionRef
    {
        public int Id;
    }

    private sealed record ConnectionEntry(Connection Connection, LuauFunction Handler, SlotHandle Target);

    private readonly Dictionary<int, ConnectionEntry> _connections = [];

    /// <summary>The signal connections scripts hold that are still live.</summary>
    public int ConnectionCount => _connections.Count;
    private int _nextConnection;

    /// <summary>Pushes the signal of an object as a userdata with <c>:connect(fn)</c>.</summary>
    private void PushSignal(State* L, EngineObject obj, Member member)
    {
        var reference = (SignalRef*)Lua.Newuserdatataggedwithmetatable(L, (nuint)sizeof(SignalRef), SignalTag);
        reference->Index = obj.Handle.Index;
        reference->Generation = obj.Handle.Generation;
        reference->Definition = member.Index;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SignalNameCall(State* L) => Guard(L, &SignalNameCallBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SignalToString(State* L) => Guard(L, &SignalToStringBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ConnectionIndex(State* L) => Guard(L, &ConnectionIndexBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ConnectionNameCall(State* L) => Guard(L, &ConnectionNameCallBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ConnectionToString(State* L) => Guard(L, &ConnectionToStringBody);

    private static string NameCalled(State* L)
    {
        int atom;
        return Apogee.Interop.Utf8.FromPointer(Lua.Namecallatom(L, &atom)) ?? "";
    }

    private static int SignalNameCallBody(State* L)
    {
        LuauVm vm = VmOf(L);
        string method = NameCalled(L);
        if (method != "connect")
        {
            throw new ValueException($"a signal has connect(fn), not {method}");
        }

        var reference = (SignalRef*)Lua.Touserdatatagged(L, 1, SignalTag);
        if (reference is null)
        {
            throw new ValueException("connect must be called on a signal, with a colon");
        }

        if (!EngineObject.TryGet(new SlotHandle(reference->Index, reference->Generation), out EngineObject? obj))
        {
            throw new ValueException("attempt to use a destroyed object");
        }

        if (!Stack.Is(L, 2, LuaType.Tfunction))
        {
            throw new ValueException($"connect expects a function, got {Stack.TypeName(L, 2)}");
        }

        SignalDef signal = vm._signalDefs[reference->Definition];
        LuauFunction handler = vm.CreateFunction(L, 2);
        Connection connection = signal.Connect(obj!, arguments => handler.Call(arguments, []));

        int id = ++vm._nextConnection;
        vm._connections[id] = new ConnectionEntry(connection, handler, obj.Handle);
        ScriptScope.OnReload(() =>
        {
            if (vm._connections.Remove(id, out ConnectionEntry? entry))
            {
                entry.Connection.Disconnect();
                entry.Handler.Release();
            }
        });

        Stack.Reserve(L, 2);
        var created = (ConnectionRef*)Lua.Newuserdatataggedwithmetatable(L, (nuint)sizeof(ConnectionRef), ConnectionTag);
        created->Id = id;
        return 1;
    }

    private static int SignalToStringBody(State* L)
    {
        var reference = (SignalRef*)Lua.Touserdatatagged(L, 1, SignalTag);
        Stack.PushString(L, reference is null ? "signal" : "signal " + VmOf(L)._signalDefs[reference->Definition].ScriptName);
        return 1;
    }

    private static int ConnectionIndexBody(State* L)
    {
        LuauVm vm = VmOf(L);
        var reference = (ConnectionRef*)Lua.Touserdatatagged(L, 1, ConnectionTag);
        string name = Stack.Is(L, 2, LuaType.Tstring) ? Stack.ReadString(L, 2) : "";
        if (reference is null || name != "connected")
        {
            throw new ValueException($"a connection has connected and disconnect(), not {name}");
        }

        Lua.Pushboolean(L, vm._connections.TryGetValue(reference->Id, out ConnectionEntry? entry) && entry.Connection.IsConnected ? 1 : 0);
        return 1;
    }

    private static int ConnectionNameCallBody(State* L)
    {
        LuauVm vm = VmOf(L);
        string method = NameCalled(L);
        var reference = (ConnectionRef*)Lua.Touserdatatagged(L, 1, ConnectionTag);
        if (reference is null || method != "disconnect")
        {
            throw new ValueException($"a connection has disconnect(), not {method}");
        }

        if (vm._connections.Remove(reference->Id, out ConnectionEntry? entry))
        {
            entry.Connection.Disconnect();
            entry.Handler.Release();
        }

        return 0;
    }

    private static int ConnectionToStringBody(State* L)
    {
        Stack.PushString(L, "connection");
        return 1;
    }
}
