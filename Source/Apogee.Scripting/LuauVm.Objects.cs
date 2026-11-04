using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apogee.Core.Collections;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.Luau;
using Apogee.Reflection;
using LuaType = Apogee.Native.Luau.Type;

namespace Apogee.Scripting;

public sealed unsafe partial class LuauVm
{
    private const int ObjectTag = 1;
    private const int SignalTag = 2;
    private const int ConnectionTag = 3;
    private const int ViewTag = 4;

    /// <summary>What a script holds of an engine object: the slot and its generation, never a pointer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectRef
    {
        public uint Index;
        public uint Generation;

        /// <summary>1 for an object the script created: it is destroyed when the userdata is collected, unless the engine adopted it.</summary>
        public int Owned;
    }

    private enum MemberKind
    {
        Property,
        Method,
        Signal,
        Native,
    }

    private sealed class Member(MemberKind kind, string name)
    {
        public MemberKind Kind { get; } = kind;

        public string Name { get; } = name;

        public PropertyDef? Property { get; init; }

        public MethodDef? Method { get; init; }

        public SignalDef? Signal { get; init; }

        public nint Native { get; init; }

        /// <summary>Position in the VM's list of methods or signals, for the upvalue of a function value.</summary>
        public int Index { get; init; }
    }

    /// <summary>The members a script can reach on a class, inherited ones included, found by atom or by name.</summary>
    private sealed class ClassBridge
    {
        public Dictionary<int, Member> ByAtom { get; } = [];

        public Dictionary<string, Member> ByName { get; } = new(StringComparer.Ordinal);

        public Member? Find(int atom, byte* name) =>
            atom >= 0 && ByAtom.TryGetValue(atom, out Member? member) ? member : ByName.GetValueOrDefault(Utf8.FromPointer(name) ?? "");
    }

    private readonly Dictionary<ClassInfo, ClassBridge> _bridges = [];
    private readonly Dictionary<string, int> _atoms = new(StringComparer.Ordinal);
    private readonly List<MethodDef> _methods = [];
    private readonly Dictionary<MethodDef, int> _methodIndex = [];
    private readonly List<SignalDef> _signalDefs = [];
    private readonly Dictionary<SignalDef, int> _signalIndex = [];
    private readonly List<ClassInfo> _classList = [];
    private readonly Queue<SlotHandle> _orphans = new();

    // ---- Names as atoms --------------------------------------------------------------------------------------------------------------

    /// <summary>The atom of a name: Luau asks for one the first time it sees a string, and then compares numbers instead of strings.</summary>
    private int AtomOf(string name)
    {
        if (!_atoms.TryGetValue(name, out int atom))
        {
            atom = _atoms.Count;
            if (atom >= short.MaxValue)
            {
                return -1;
            }

            _atoms[name] = atom;
        }

        return atom;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static short UseAtom(State* L, byte* text, nuint length)
    {
        try
        {
            string name = System.Text.Encoding.UTF8.GetString(text, (int)length);
            return s_vms.TryGetValue((nint)Lua.Mainthread(L), out LuauVm? vm) && vm._atoms.TryGetValue(name, out int atom) ? (short)atom : (short)-1;
        }
#pragma warning disable CA1031 // Called by the VM itself: nothing may unwind through it. An unknown atom is always safe.
        catch (Exception)
#pragma warning restore CA1031
        {
            return -1;
        }
    }

    // ---- Installing the bridge ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The engine's own classes (<c>World</c>) register from a module initializer, which CoreCLR runs when the assembly is first used and
    /// NativeAOT at startup. Anything that reads the registry (a VM, the definitions) makes sure it has happened.
    /// </summary>
    internal static void EnsureEngineClasses() => RuntimeHelpers.RunModuleConstructor(typeof(Apogee.Ecs.World).Module.ModuleHandle);

    private void InstallBridge()
    {
        EnsureEngineClasses();

        Lua.Callbacks(_main)->Useratom = &UseAtom;

        foreach (ClassInfo info in TypeRegistry.Classes)
        {
            foreach (PropertyDef property in info.Properties)
            {
                AtomOf(property.ScriptName);
            }

            foreach (MethodDef method in info.Methods)
            {
                AtomOf(method.ScriptName);
            }

            foreach (SignalDef signal in info.Signals)
            {
                AtomOf(signal.ScriptName);
            }
        }

        foreach (string name in new[] { "connect", "disconnect", "connected" })
        {
            AtomOf(name);
        }

        RegisterEcsNatives();
        RegisterContentNatives();

        SetUserdataMetatable(ObjectTag, "object", &ObjectIndex, &ObjectNewIndex, &ObjectNameCall, &ObjectToString);
        SetUserdataMetatable(SignalTag, "signal", null, null, &SignalNameCall, &SignalToString);
        SetUserdataMetatable(ConnectionTag, "connection", &ConnectionIndex, null, &ConnectionNameCall, &ConnectionToString);
        SetUserdataMetatable(ViewTag, "component", &ViewIndex, &ViewNewIndex, null, &ViewToString);
        Lua.Setuserdatadtor(_main, ObjectTag, &ObjectDestructor);
        InstallQuat();

        InstallVectorMethods();
        InstallClassGlobals();
        InstallLibraries();
    }

    private void SetUserdataMetatable(
        int tag,
        string name,
        delegate* unmanaged[Cdecl]<State*, int> index,
        delegate* unmanaged[Cdecl]<State*, int> newIndex,
        delegate* unmanaged[Cdecl]<State*, int> nameCall,
        delegate* unmanaged[Cdecl]<State*, int> toString)
    {
        Lua.Createtable(_main, 0, 6);
        SetMetaFunction("__index", index);
        SetMetaFunction("__newindex", newIndex);
        SetMetaFunction("__namecall", nameCall);
        SetMetaFunction("__tostring", toString);
        Stack.PushString(_main, "The metatable is locked");
        SetField("__metatable");
        Lua.Setreadonly(_main, -1, 1);
        Lua.Setuserdatametatable(_main, tag);
    }

    private void SetMetaFunction(string field, delegate* unmanaged[Cdecl]<State*, int> function)
    {
        if (function is null)
        {
            return;
        }

        Lua.PushManagedFunction(_main, function, NativeName(field), 0);
        SetField(field);
    }

    /// <summary>Sets <c>table[field]</c> from the value on top of the main stack, popping it; the table is below it.</summary>
    private void SetField(string field)
    {
        fixed (byte* key = Utf8.ToZeroTerminated(field))
        {
            Lua.Rawsetfield(_main, -2, key);
        }
    }

    // ---- Objects ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>Pushes the userdata of an object; there is one per live object, so <c>==</c> and table keys work. Nil for an object that is gone or not for scripts.</summary>
    internal void PushObject(State* L, EngineObject? obj, bool owned = false)
    {
        if (obj is null || obj.IsDestroyed || !obj.Class.Scriptable)
        {
            Lua.Pushnil(L);
            return;
        }

        Stack.Reserve(L, 5);
        PushObjectCache(L);
        double key = CacheKey(obj.Handle);
        Lua.Pushnumber(L, key);
        _ = Lua.Rawget(L, -2);
        if (Stack.Is(L, -1, LuaType.Tuserdata))
        {
            Lua.Remove(L, -2);
            return;
        }

        Lua.Settop(L, -2);
        var reference = (ObjectRef*)Lua.Newuserdatataggedwithmetatable(L, (nuint)sizeof(ObjectRef), ObjectTag);
        reference->Index = obj.Handle.Index;
        reference->Generation = obj.Handle.Generation;
        reference->Owned = owned ? 1 : 0;
        Lua.Pushnumber(L, key);
        Lua.Pushvalue(L, -2);
        Lua.Rawset(L, -4);
        Lua.Remove(L, -2);
    }

    private static double CacheKey(SlotHandle handle) => (double)(((ulong)handle.Generation << 26) + handle.Index);

    /// <summary>Pushes the registry's table of userdata by object (weak values), making it on first use.</summary>
    private static void PushObjectCache(State* L)
    {
        fixed (byte* name = "apogee.objects\0"u8)
        {
            if (Lua.Rawgetfield(L, Lua.Registryindex, name) == (int)LuaType.Ttable)
            {
                return;
            }

            Lua.Settop(L, -2);
            Lua.Createtable(L, 0, 16);
            Lua.Createtable(L, 0, 1);
            Stack.PushString(L, "v");
            fixed (byte* mode = "__mode\0"u8)
            {
                Lua.Rawsetfield(L, -2, mode);
            }

            _ = Lua.Setmetatable(L, -2);
            Lua.Pushvalue(L, -1);
            Lua.Rawsetfield(L, Lua.Registryindex, name);
        }
    }

    /// <summary>The object a userdata stands for; an error for anything else, a destroyed object, or a class a mod may not use.</summary>
    internal EngineObject GetObject(State* L, int index)
    {
        var reference = (ObjectRef*)Lua.Touserdatatagged(L, index, ObjectTag);
        if (reference is null)
        {
            throw new ValueException($"expected an object, got {Stack.TypeName(L, index)}");
        }

        if (!EngineObject.TryGet(new SlotHandle(reference->Index, reference->Generation), out EngineObject? obj))
        {
            throw new ValueException("attempt to use a destroyed object");
        }

        ScriptOwner owner = OwnerOf(L);
        if (owner.IsMod && IsModsBlocked(obj.Class))
        {
            throw new ValueException($"mod \"{owner.Id}\" can't use {obj.Class.Name}: it is the base game's");
        }

        return obj;
    }

    private static bool IsModsBlocked(ClassInfo info)
    {
        for (ClassInfo? current = info; current is not null; current = current.Base)
        {
            if (current.ModsBlocked)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Makes <paramref name="value"/> a global of every script (read-only for them), such as <c>world</c>. Null removes it.</summary>
    public void SetGlobal(string name, EngineObject? value)
    {
        ThrowIfDisposed();

        // Scripts compiled from now on must read the members of this global each time, not once when they load.
        fixed (byte* key = Utf8.ToZeroTerminated(name))
        {
            Lua.AddMutableGlobal(_main, key);
        }

        Lua.Setreadonly(_main, Lua.Globalsindex, 0);
        PushObject(_main, value);
        SetMainGlobal(name);
        Lua.Setreadonly(_main, Lua.Globalsindex, 1);
        Lua.Setsafeenv(_main, Lua.Globalsindex, 1);
    }

    /// <summary>Destroys the objects scripts made and let go of, and releases function references that were dropped. Called after every <see cref="Execute"/>; a game loop can call it too.</summary>
    public void ProcessPending()
    {
        ThrowIfDisposed();
        while (_orphans.TryDequeue(out SlotHandle handle))
        {
            if (EngineObject.TryGet(handle, out EngineObject? obj) && !obj.IsAdopted)
            {
                obj.Destroy();
            }
        }

        // A connection to an object that is gone can never fire again: let the registry forget its handler.
        if (_connections.Count > 0)
        {
            foreach ((int id, ConnectionEntry entry) in new List<KeyValuePair<int, ConnectionEntry>>(_connections))
            {
                if (!EngineObject.TryGet(entry.Target, out _) && _connections.Remove(id))
                {
                    entry.Handler.Release();
                }
            }
        }

        while (_droppedReferences.TryDequeue(out int reference))
        {
            _ = Lua.Unref(_main, reference);
        }
    }

    /// <summary>Called by the collector for a userdata of an object: no Luau API, no engine code, only a note for <see cref="ProcessPending"/>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ObjectDestructor(State* L, void* data)
    {
        var reference = (ObjectRef*)data;
        if (reference->Owned != 0 && s_vms.TryGetValue((nint)Lua.Mainthread(L), out LuauVm? vm))
        {
            vm._orphans.Enqueue(new SlotHandle(reference->Index, reference->Generation));
        }
    }

    // ---- Members of a class ---------------------------------------------------------------------------------------------------------------

    private ClassBridge BridgeOf(ClassInfo info)
    {
        if (_bridges.TryGetValue(info, out ClassBridge? known))
        {
            return known;
        }

        var bridge = new ClassBridge();
        for (ClassInfo? current = info; current is not null; current = current.Base)
        {
            foreach (PropertyDef property in current.Properties)
            {
                Add(bridge, property.ScriptName, new Member(MemberKind.Property, property.ScriptName) { Property = property });
            }

            foreach (MethodDef method in current.Methods)
            {
                if (!method.IsStatic)
                {
                    Add(bridge, method.ScriptName, new Member(MemberKind.Method, method.ScriptName) { Method = method, Index = IndexOf(method) });
                }
            }

            foreach (SignalDef signal in current.Signals)
            {
                Add(bridge, signal.ScriptName, new Member(MemberKind.Signal, signal.ScriptName) { Signal = signal, Index = IndexOf(signal) });
            }

            foreach (ScriptNatives.NativeMethod native in ScriptNatives.Of(current.Name))
            {
                Add(bridge, native.Name, new Member(MemberKind.Native, native.Name) { Native = native.Body });
            }
        }

        _bridges[info] = bridge;
        return bridge;
    }

    private void Add(ClassBridge bridge, string name, Member member)
    {
        if (bridge.ByName.TryAdd(name, member))
        {
            int atom = AtomOf(name);
            if (atom >= 0)
            {
                bridge.ByAtom.TryAdd(atom, member);
            }
        }
    }

    private int IndexOf(MethodDef method)
    {
        if (!_methodIndex.TryGetValue(method, out int index))
        {
            index = _methods.Count;
            _methods.Add(method);
            _methodIndex[method] = index;
        }

        return index;
    }

    private int IndexOf(SignalDef signal)
    {
        if (!_signalIndex.TryGetValue(signal, out int index))
        {
            index = _signalDefs.Count;
            _signalDefs.Add(signal);
            _signalIndex[signal] = index;
        }

        return index;
    }

    // ---- Calling a method ----------------------------------------------------------------------------------------------------------------------

    /// <summary>Calls the method with the arguments from <paramref name="firstArgument"/> on, and pushes its results.</summary>
    private int InvokeMethod(State* L, EngineObject? target, MethodDef method, int firstArgument)
    {
        int count = Lua.Gettop(L) - firstArgument + 1;
        int parameters = method.Parameters.Length;
        Value[] arguments = ArrayPool<Value>.Shared.Rent(Math.Max(Math.Max(parameters, count), 1));
        Value[] results = ArrayPool<Value>.Shared.Rent(Math.Max(method.Results.Length, 1));
        try
        {
            for (int i = 0; i < count && i < parameters; i++)
            {
                try
                {
                    arguments[i] = ReadValue(L, firstArgument + i, method.Parameters[i].Type);
                }
                catch (ValueException e)
                {
                    throw new ValueException($"bad argument #{i + 1} ({method.Parameters[i].Name}) to '{method.ScriptName}': {e.Message}");
                }
            }

            using (ScriptCaller.Enter(OwnerOf(L)))
            {
                method.Invoke(target, arguments.AsSpan(0, count), results.AsSpan(0, method.Results.Length));
            }

            Stack.Reserve(L, method.Results.Length + 2);
            for (int i = 0; i < method.Results.Length; i++)
            {
                PushValue(L, results[i]);
            }

            return method.Results.Length;
        }
        finally
        {
            ArrayPool<Value>.Shared.Return(arguments, clearArray: true);
            ArrayPool<Value>.Shared.Return(results, clearArray: true);
        }
    }

    // ---- The metamethods ------------------------------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ObjectIndex(State* L) => Guard(L, &ObjectIndexBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ObjectNewIndex(State* L) => Guard(L, &ObjectNewIndexBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ObjectNameCall(State* L) => Guard(L, &ObjectNameCallBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ObjectToString(State* L) => Guard(L, &ObjectToStringBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int BoundMethodCall(State* L) => Guard(L, &BoundMethodCallBody);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NativeMethodCall(State* L) => Guard(L, &NativeMethodCallBody);

    private static Member FindMember(LuauVm vm, State* L, EngineObject obj, string what)
    {
        int atom;
        byte* name = what == "namecall" ? Lua.Namecallatom(L, &atom) : Lua.Tostringatom(L, 2, &atom);
        ClassBridge bridge = vm.BridgeOf(obj.Class);
        return bridge.Find(atom, name) ?? throw new ValueException($"'{Utf8.FromPointer(name)}' is not a member of {obj.Class.Name}");
    }

    private static int ObjectIndexBody(State* L)
    {
        LuauVm vm = VmOf(L);
        EngineObject obj = vm.GetObject(L, 1);
        if (!Stack.Is(L, 2, LuaType.Tstring))
        {
            throw new ValueException("an object is indexed by the names of its members");
        }

        Member member = FindMember(vm, L, obj, "index");
        Stack.Reserve(L, 4);
        switch (member.Kind)
        {
            case MemberKind.Property:
                vm.PushValue(L, member.Property!.Getter(obj));
                return 1;
            case MemberKind.Method:
                Lua.Pushnumber(L, member.Index);
                Lua.PushManagedFunction(L, &BoundMethodCall, vm.NativeName(member.Name), 1);
                return 1;
            case MemberKind.Signal:
                vm.PushSignal(L, obj, member);
                return 1;
            default:
                Lua.Pushnumber(L, member.Native);
                Lua.PushManagedFunction(L, &NativeMethodCall, vm.NativeName(member.Name), 1);
                return 1;
        }
    }

    private static int ObjectNewIndexBody(State* L)
    {
        LuauVm vm = VmOf(L);
        EngineObject obj = vm.GetObject(L, 1);
        if (!Stack.Is(L, 2, LuaType.Tstring))
        {
            throw new ValueException("an object is indexed by the names of its members");
        }

        Member member = FindMember(vm, L, obj, "newindex");
        if (member.Kind != MemberKind.Property || member.Property!.Setter is null)
        {
            throw new ValueException($"{obj.Class.Name}.{member.Name} cannot be set");
        }

        PropertyDef property = member.Property;
        Value value;
        try
        {
            value = vm.ReadValue(L, 3, property.Type);
        }
        catch (ValueException e)
        {
            throw new ValueException($"bad value for {obj.Class.Name}.{member.Name}: {e.Message}");
        }

        using (ScriptCaller.Enter(vm.OwnerOf(L)))
        {
            property.Setter!(obj, in value);
        }
        return 0;
    }

    private static int ObjectNameCallBody(State* L)
    {
        LuauVm vm = VmOf(L);
        EngineObject obj = vm.GetObject(L, 1);
        Member member = FindMember(vm, L, obj, "namecall");
        switch (member.Kind)
        {
            case MemberKind.Method:
                return vm.InvokeMethod(L, obj, member.Method!, 2);
            case MemberKind.Native:
                return ((delegate*<State*, int>)member.Native)(L);
            default:
                throw new ValueException($"{obj.Class.Name}.{member.Name} is not a method");
        }
    }

    private static int ObjectToStringBody(State* L)
    {
        var reference = (ObjectRef*)Lua.Touserdatatagged(L, 1, ObjectTag);
        string text = reference is not null && EngineObject.TryGet(new SlotHandle(reference->Index, reference->Generation), out EngineObject? obj)
            ? obj.Class.Name
            : "destroyed object";
        Stack.PushString(L, text);
        return 1;
    }

    /// <summary><c>local f = obj.method; f(obj, …)</c>: the method index is the function's first upvalue.</summary>
    private static int BoundMethodCallBody(State* L)
    {
        LuauVm vm = VmOf(L);
        MethodDef method = vm._methods[(int)Lua.Tonumberx(L, Lua.Globalsindex - 2, null)];
        EngineObject obj = vm.GetObject(L, 1);
        return vm.InvokeMethod(L, obj, method, 2);
    }

    private static int NativeMethodCallBody(State* L)
    {
        var body = (delegate*<State*, int>)(nint)Lua.Tonumberx(L, Lua.Globalsindex - 2, null);
        return body(L);
    }

    // ---- Class globals and libraries ---------------------------------------------------------------------------------------------------------

    private void InstallClassGlobals()
    {
        foreach (ClassInfo info in TypeRegistry.Classes)
        {
            if (!info.Scriptable || info.Name == "EngineObject")
            {
                continue;
            }

            bool anyStatic = false;
            foreach (MethodDef method in info.Methods)
            {
                anyStatic |= method.IsStatic;
            }

            if (!info.Creatable && !anyStatic)
            {
                continue;
            }

            Lua.Createtable(_main, 0, 4);
            if (info.Creatable)
            {
                _classList.Add(info);
                Lua.Pushnumber(_main, _classList.Count - 1);
                Lua.PushManagedFunction(_main, &ClassNew, NativeName(info.Name + ".new"), 1);
                SetField("new");
            }

            foreach (MethodDef method in info.Methods)
            {
                if (method.IsStatic)
                {
                    PushStaticFunction(method, info.Name);
                    SetField(method.ScriptName);
                }
            }

            Lua.Setreadonly(_main, -1, 1);
            SetMainGlobal(info.Name);
        }
    }

    private void InstallLibraries()
    {
        foreach (LibraryInfo library in TypeRegistry.Libraries)
        {
            Lua.Createtable(_main, 0, library.Methods.Length);
            foreach (MethodDef method in library.Methods)
            {
                PushStaticFunction(method, library.Name);
                SetField(method.ScriptName);
            }

            Lua.Setreadonly(_main, -1, 1);
            SetMainGlobal(library.Name);
        }

        // Libraries of hand-written functions (xml, defs).
        foreach ((string name, IReadOnlyList<ScriptNatives.NativeFunction> functions) in ScriptNatives.Libraries())
        {
            Lua.Createtable(_main, 0, functions.Count);
            foreach (ScriptNatives.NativeFunction function in functions)
            {
                Lua.Pushnumber(_main, function.Body);
                Lua.PushManagedFunction(_main, &NativeMethodCall, NativeName(name + "." + function.Name), 1);
                SetField(function.Name);
            }

            Lua.Setreadonly(_main, -1, 1);
            SetMainGlobal(name);
        }
    }

    private void PushStaticFunction(MethodDef method, string owner)
    {
        Lua.Pushnumber(_main, IndexOf(method));
        Lua.PushManagedFunction(_main, &StaticCall, NativeName(owner + "." + method.ScriptName), 1);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int StaticCall(State* L) => Guard(L, &StaticCallBody);

    private static int StaticCallBody(State* L)
    {
        LuauVm vm = VmOf(L);
        MethodDef method = vm._methods[(int)Lua.Tonumberx(L, Lua.Globalsindex - 2, null)];
        return vm.InvokeMethod(L, null, method, 1);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ClassNew(State* L) => Guard(L, &ClassNewBody);

    private static int ClassNewBody(State* L)
    {
        LuauVm vm = VmOf(L);
        ClassInfo info = vm._classList[(int)Lua.Tonumberx(L, Lua.Globalsindex - 2, null)];
        vm.PushObject(L, info.Create(), owned: true);
        return 1;
    }

    /// <summary>Gives a <c>vector</c> the methods <c>v:length()</c>, <c>v:normalized()</c>, <c>v:dot(w)</c>… from Luau's own <c>vector</c> library.</summary>
    private void InstallVectorMethods()
    {
        Lua.Pushvector(_main, 0, 0, 0);
        Lua.Createtable(_main, 0, 2);
        Lua.Createtable(_main, 0, 12);
        fixed (byte* library = "vector\0"u8)
        {
            if (Lua.Rawgetfield(_main, Lua.Globalsindex, library) == (int)LuaType.Ttable)
            {
                foreach ((string method, string function) in new[]
                {
                    ("length", "magnitude"), ("normalized", "normalize"), ("dot", "dot"), ("cross", "cross"), ("angle", "angle"),
                    ("floor", "floor"), ("ceil", "ceil"), ("abs", "abs"), ("sign", "sign"), ("clamp", "clamp"), ("min", "min"), ("max", "max"), ("lerp", "lerp"),
                })
                {
                    fixed (byte* name = Utf8.ToZeroTerminated(function))
                    {
                        if (Lua.Rawgetfield(_main, -1, name) == (int)LuaType.Tfunction)
                        {
                            SetField2(method);
                        }
                        else
                        {
                            Lua.Settop(_main, -2);
                        }
                    }
                }
            }

            Lua.Settop(_main, -2); // the vector library
        }

        SetField("__index");
        Lua.Pushnumber(_main, 0); // (no other metamethod: arithmetic is native)
        Lua.Settop(_main, -2);
        _ = Lua.Setmetatable(_main, -2);
        Lua.Settop(_main, -2);
    }

    /// <summary>Sets <c>table[field]</c> from the value on top, popping it; the table is two below (the library is between).</summary>
    private void SetField2(string field)
    {
        fixed (byte* key = Utf8.ToZeroTerminated(field))
        {
            Lua.Rawsetfield(_main, -3, key);
        }
    }
}
