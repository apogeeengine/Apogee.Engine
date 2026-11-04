using System;
using System.Collections.Generic;
using System.Text;
using Apogee.Interop;
using Apogee.Native.Luau;
using Apogee.Reflection;
using LuaType = Apogee.Native.Luau.Type;

namespace Apogee.Scripting;

/// <summary>A table a script gave the engine (a UI data model's fields), kept by registry reference and read by path when needed.</summary>
internal sealed unsafe class LuauTable : ScriptTable
{
    private bool _released;

    internal LuauTable(LuauVm vm, ScriptOwner owner, int reference)
    {
        Vm = vm;
        Owner = owner;
        Reference = reference;
    }

    public LuauVm Vm { get; }

    public ScriptOwner Owner { get; }

    public int Reference { get; }

    public override ScriptDataKind KindAt(ReadOnlySpan<DataPathSegment> path)
    {
        State* L = Vm.PushPath(this, path, out int top);
        try
        {
            return LuauVm.KindOf(L, -1);
        }
        finally
        {
            Lua.Settop(L, top);
        }
    }

    public override Value GetAt(ReadOnlySpan<DataPathSegment> path)
    {
        State* L = Vm.PushPath(this, path, out int top);
        try
        {
            return (LuaType)Lua.Type(L, -1) is LuaType.Tboolean or LuaType.Tnumber or LuaType.Tstring ? Stack.ReadValue(L, -1) : default;
        }
        finally
        {
            Lua.Settop(L, top);
        }
    }

    public override bool SetAt(ReadOnlySpan<DataPathSegment> path, Value value)
    {
        if (path.IsEmpty)
        {
            return false;
        }

        State* L = Vm.PushPath(this, path[..^1], out int top);
        try
        {
            if (!Stack.Is(L, -1, LuaType.Ttable) || Lua.Getreadonly(L, -1) != 0)
            {
                return false;
            }

            DataPathSegment last = path[^1];
            Stack.Reserve(L, 3);
            Stack.PushValue(L, value);
            if (last.Name is { } name)
            {
                fixed (byte* key = Utf8.ToZeroTerminated(name))
                {
                    Lua.Rawsetfield(L, -2, key);
                }
            }
            else
            {
                Lua.Rawseti(L, -2, last.Index + 1);
            }

            return true;
        }
        finally
        {
            Lua.Settop(L, top);
        }
    }

    public override int LengthAt(ReadOnlySpan<DataPathSegment> path)
    {
        State* L = Vm.PushPath(this, path, out int top);
        try
        {
            return Stack.Is(L, -1, LuaType.Ttable) ? Lua.Objlen(L, -1) : 0;
        }
        finally
        {
            Lua.Settop(L, top);
        }
    }

    public override IReadOnlyList<string> KeysAt(ReadOnlySpan<DataPathSegment> path)
    {
        State* L = Vm.PushPath(this, path, out int top);
        try
        {
            var keys = new List<string>();
            if (Stack.Is(L, -1, LuaType.Ttable))
            {
                int table = Lua.Absindex(L, -1);
                Stack.Reserve(L, 4);
                for (int iterator = 0; (iterator = Lua.Rawiter(L, table, iterator)) >= 0;)
                {
                    if (Stack.Is(L, -2, LuaType.Tstring))
                    {
                        keys.Add(Stack.ReadString(L, -2));
                    }

                    Lua.Settop(L, -3);
                }
            }

            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
        finally
        {
            Lua.Settop(L, top);
        }
    }

    public override ScriptFunction? FunctionAt(ReadOnlySpan<DataPathSegment> path)
    {
        State* L = Vm.PushPath(this, path, out int top);
        try
        {
            return Stack.Is(L, -1, LuaType.Tfunction) ? Vm.CreateFunction(L, -1) : null;
        }
        finally
        {
            Lua.Settop(L, top);
        }
    }

    public override void Dispose()
    {
        if (!_released)
        {
            _released = true;
            Vm.DropReference(Reference);
            GC.SuppressFinalize(this);
        }
    }

    ~LuauTable()
    {
        if (!_released)
        {
            Vm.DropReference(Reference);
        }
    }
}

/// <summary>The globals of one UI document: a table over the caller's, in which its scripts run, so that what a <c>&lt;script&gt;</c> defines is there for its <c>onclick</c>s.</summary>
internal sealed unsafe class LuauScriptEnvironment : ScriptEnvironment
{
    private readonly LuauVm _vm;
    private readonly ScriptOwner _owner;
    private int _table;
    private bool _disposed;

    internal LuauScriptEnvironment(LuauVm vm, ScriptOwner owner)
    {
        _vm = vm;
        _owner = owner;
        State* L = vm.CallThread(owner);
        int top = Lua.Gettop(L);
        try
        {
            Stack.Reserve(L, 4);
            Lua.Createtable(L, 0, 8);
            Lua.Createtable(L, 0, 1);
            Lua.Pushvalue(L, Lua.Globalsindex);
            fixed (byte* index = "__index\0"u8)
            {
                Lua.Rawsetfield(L, -2, index);
            }

            _ = Lua.Setmetatable(L, -2);
            _table = Lua.Ref(L, -1);
        }
        finally
        {
            Lua.Settop(L, top);
        }
    }

    public override object Owner => _owner;

    public override bool CanRead(string path) => _vm.CanAccess(_owner, path);

    public override void SetGlobal(string name, Value value)
    {
        State* L = _vm.CallThread(_owner);
        int top = Lua.Gettop(L);
        try
        {
            Stack.Reserve(L, 4);
            _ = Stack.RawGetI(L, Lua.Registryindex, _table);
            _vm.PushValue(L, value);
            fixed (byte* key = Utf8.ToZeroTerminated(name))
            {
                Lua.Rawsetfield(L, -2, key);
            }
        }
        finally
        {
            Lua.Settop(L, top);
        }
    }

    public override void Run(string chunkName, string source)
    {
        State* L = _vm.CallThread(_owner);
        int top = Lua.Gettop(L);
        try
        {
            LoadInto(L, chunkName, source);
            LuauVm.ThrowIfFailed(Lua.ProtectedCall(L, 0, 0, _owner.TimeBudgetMilliseconds), L);
        }
        finally
        {
            Lua.Settop(L, top);
        }
    }

    public override ScriptFunction Compile(string chunkName, string parameters, string body)
    {
        State* L = _vm.CallThread(_owner);
        int top = Lua.Gettop(L);
        try
        {
            // One line, so that the error messages of the body keep its line numbers.
            LoadInto(L, chunkName, $"return function({parameters}) {body}\nend");
            LuauVm.ThrowIfFailed(Lua.ProtectedCall(L, 0, 1, _owner.TimeBudgetMilliseconds), L);
            return _vm.CreateFunction(L, -1);
        }
        finally
        {
            Lua.Settop(L, top);
        }
    }

    /// <summary>Loads the chunk and gives it the environment: a function is on top of the stack afterwards.</summary>
    private void LoadInto(State* L, string chunkName, string source)
    {
        Stack.Reserve(L, 4);
        LuauVm.Load(L, chunkName, source);
        _ = Stack.RawGetI(L, Lua.Registryindex, _table);
        _ = Lua.Setfenv(L, -2);
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _vm.DropReference(_table);
            GC.SuppressFinalize(this);
        }
    }

    ~LuauScriptEnvironment()
    {
        if (!_disposed)
        {
            _vm.DropReference(_table);
        }
    }
}

public sealed unsafe partial class LuauVm
{
    /// <summary>A reference to the table at <paramref name="index"/>, owned by the owner of the running script.</summary>
    internal LuauTable CreateTable(State* L, int index) => new(this, OwnerOf(L), Lua.Ref(L, index));

    /// <summary>A new environment for the script that is calling the engine (<see cref="ScriptCaller.Current"/>); the base game's when none is.</summary>
    public ScriptEnvironment Create()
    {
        ThrowIfDisposed();
        ScriptOwner owner = ScriptCaller.Current is ScriptOwner caller && caller.Vm == this ? caller : BaseOwner;
        return new LuauScriptEnvironment(this, owner);
    }

    internal bool CanAccess(ScriptOwner owner, string path) => _options.CanAccessFile?.Invoke(owner, path, false) ?? true;

    /// <summary>Pushes what is at <paramref name="path"/> in the table (nil when there is nothing) on its owner's call thread, and says where the stack was before so the caller can restore it.</summary>
    internal State* PushPath(LuauTable table, ReadOnlySpan<DataPathSegment> path, out int top)
    {
        State* L = CallThread(table.Owner);
        top = Lua.Gettop(L);
        Stack.Reserve(L, 6);
        _ = Stack.RawGetI(L, Lua.Registryindex, table.Reference);
        foreach (DataPathSegment segment in path)
        {
            if (!Stack.Is(L, -1, LuaType.Ttable))
            {
                Lua.Settop(L, -2);
                Lua.Pushnil(L);
                return L;
            }

            if (segment.Name is { } name)
            {
                fixed (byte* key = Utf8.ToZeroTerminated(name))
                {
                    _ = Lua.Rawgetfield(L, -1, key);
                }
            }
            else
            {
                _ = Lua.Rawgeti(L, -1, segment.Index + 1);
            }

            Lua.Remove(L, -2);
        }

        return L;
    }

    /// <summary>What the value at <paramref name="index"/> is to a data model: a table with elements or none at all is a list, one with only named fields a struct.</summary>
    internal static ScriptDataKind KindOf(State* L, int index)
    {
        switch ((LuaType)Lua.Type(L, index))
        {
            case LuaType.Tboolean or LuaType.Tnumber or LuaType.Tstring:
                return ScriptDataKind.Scalar;
            case LuaType.Tfunction:
                return ScriptDataKind.Function;
            case LuaType.Ttable:
                int table = Lua.Absindex(L, index);
                if (Lua.Objlen(L, table) > 0)
                {
                    return ScriptDataKind.Array;
                }

                Stack.Reserve(L, 4);
                int iterator = Lua.Rawiter(L, table, 0);
                if (iterator < 0)
                {
                    return ScriptDataKind.Array;
                }

                Lua.Settop(L, -3);
                return ScriptDataKind.Struct;
            default:
                return ScriptDataKind.Missing;
        }
    }
}
