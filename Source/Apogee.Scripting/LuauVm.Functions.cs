using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Apogee.Native.Luau;
using Apogee.Reflection;

namespace Apogee.Scripting;

/// <summary>A Luau function the engine holds: a callback, a signal handler, a system. Calling it runs it as its owner, with the owner's budget.</summary>
internal sealed class LuauFunction : ScriptFunction
{
    private bool _released;

    internal LuauFunction(LuauVm vm, ScriptOwner owner, int reference)
    {
        Vm = vm;
        Owner = owner;
        Reference = reference;
    }

    public LuauVm Vm { get; }

    public ScriptOwner Owner { get; }

    /// <summary>The function's slot in the registry.</summary>
    public int Reference { get; }

    /// <summary>The frame of the world in which a system made from this function failed last; it is skipped for the rest of that frame.</summary>
    public long FailedFrame { get; set; } = -1;

    public override int Call(ReadOnlySpan<Value> arguments, Span<Value> results) => Vm.CallFunction(this, arguments, results);

    /// <summary>Lets the registry forget the function (it is collected unless a script still holds it).</summary>
    public void Release()
    {
        if (!_released)
        {
            _released = true;
            Vm.DropReference(Reference);
        }
    }

    // The collector of .NET runs on another thread: it can only note the reference; the VM's thread frees it (ProcessPending).
    ~LuauFunction()
    {
        if (!_released)
        {
            Vm.DropReference(Reference);
        }
    }
}

public sealed unsafe partial class LuauVm
{
    private readonly ConcurrentQueue<int> _droppedReferences = new();
    private readonly Dictionary<ScriptOwner, (nint Thread, int Reference)> _callThreads = [];

    internal void DropReference(int reference) => _droppedReferences.Enqueue(reference);

    /// <summary>A reference to the function at <paramref name="index"/>, owned by the owner of the running script.</summary>
    internal LuauFunction CreateFunction(State* L, int index) => new(this, OwnerOf(L), Lua.Ref(L, index));

    /// <summary>
    /// The thread the engine calls an owner's functions on. It stays below the stack top of whatever is running, so a function that
    /// calls the engine, which calls another function of the owner, simply nests on the same thread.
    /// </summary>
    internal State* CallThread(ScriptOwner owner)
    {
        if (!_callThreads.TryGetValue(owner, out (nint Thread, int Reference) known))
        {
            State* thread = NewThread(owner, out int reference);
            known = ((nint)thread, reference);
            _callThreads[owner] = known;
        }

        return (State*)known.Thread;
    }

    internal int CallFunction(LuauFunction function, ReadOnlySpan<Value> arguments, Span<Value> results)
    {
        ThrowIfDisposed();
        State* thread = CallThread(function.Owner);
        int top = Lua.Gettop(thread);
        try
        {
            if (!Stack.Reserve(thread, arguments.Length + 4))
            {
                throw new ScriptException(ScriptErrorKind.Runtime, "too many arguments for a script function");
            }

            _ = Stack.RawGetI(thread, Lua.Registryindex, function.Reference);
            foreach (ref readonly Value argument in arguments)
            {
                PushValue(thread, argument);
            }

            ThrowIfFailed(Lua.ProtectedCall(thread, arguments.Length, results.Length, function.Owner.TimeBudgetMilliseconds), thread);

            for (int i = 0; i < results.Length; i++)
            {
                results[i] = ReadValue(thread, top + 1 + i, TypeRef.Any);
            }

            return results.Length;
        }
        finally
        {
            Lua.Settop(thread, top);
        }
    }
}
