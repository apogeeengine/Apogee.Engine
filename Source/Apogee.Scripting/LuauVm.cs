using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Apogee.Core.Collections;
using Apogee.Native.Luau;
using Apogee.Reflection;

namespace Apogee.Scripting;

public sealed class LuauVmOptions
{
    /// <summary>A hard limit on the bytes the VM may allocate while scripts run (0: none). Per-owner limits are softer and finer.</summary>
    public long MemoryLimit { get; init; }

    /// <summary>The time one call of a script may take when its owner has no budget of its own, in milliseconds (0: none).</summary>
    public int TimeBudgetMilliseconds { get; init; }

    /// <summary>Where <c>require</c> reads scripts from; the real file system by default.</summary>
    public IScriptFileSystem Files { get; init; } = new PhysicalScriptFileSystem();

    /// <summary>Receives what scripts <c>print</c> (and <c>warn</c>, flagged); the log by default.</summary>
    public Action<ScriptOwner, string, bool>? Output { get; init; }

    /// <summary>
    /// Says whether <c>require</c> may load a module for a script: given the owner of the requiring script and the module's path (<c>mod://cool/ui.luau</c>; engine modules are not asked
    /// about). Null: any module inside a root. This is how a mod is confined to its own files and its dependencies'.
    /// </summary>
    public Func<ScriptOwner, string, bool>? CanLoadModule { get; init; }

    /// <summary>The file system the <c>fs</c> library reads and writes (null: <c>fs</c> finds nothing and writes nothing).</summary>
    public Apogee.Content.FileSystem.VirtualFileSystem? FileSystem { get; init; }

    /// <summary>
    /// Says whether a script of an owner may read (<c>write</c> false) or write a path of <see cref="FileSystem"/>: given the owner, the canonical path and whether it is a write. Null:
    /// whatever the file system allows (it only takes writes in writable mounts).
    /// </summary>
    public Func<ScriptOwner, string, bool, bool>? CanAccessFile { get; init; }

    /// <summary>Chooses the owner a required module runs as, from its path; null (or no hook) keeps the requiring script's owner.</summary>
    public Func<string, ScriptOwner?>? OwnerOfModule { get; init; }
}

/// <summary>
/// One Luau VM for the game and its mods. Every script runs in a thread of its own with its own globals over the read-only ones
/// (<c>luaL_sandbox</c>), and belongs to a <see cref="ScriptOwner"/>: the owner's memory category counts what it allocates, and its time
/// budget limits each call. Not thread safe: use it from the thread that made it.
/// </summary>
public sealed unsafe partial class LuauVm : IDisposable, IScriptEnvironments
{
    private static readonly ConcurrentDictionary<nint, LuauVm> s_vms = new();

    private readonly State* _main;
    private readonly LuauVmOptions _options;
    private readonly List<ScriptOwner> _owners = [];
    private readonly ModuleResolver _resolver;
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReloadScope> _scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, nint> _nativeNames = new(StringComparer.Ordinal);
    private int _nextCategory = 1;
    private bool _disposed;

    public LuauVm(LuauVmOptions? options = null)
    {
        _options = options ?? new LuauVmOptions();
        _resolver = new ModuleResolver(_options.Files);

        _main = Lua.NewVm(0);
        if (_main is null)
        {
            throw new InvalidOperationException("Luau could not create a state.");
        }

        s_vms[(nint)_main] = this;
        BaseOwner = new ScriptOwner(this, 0, "base", isMod: false, category: 0, memoryLimit: 0, _options.TimeBudgetMilliseconds);
        _owners.Add(BaseOwner);
        Lua.Setthreaddata(_main, (void*)1);
        Lua.Callbacks(_main)->Userthread = &UserThread;
        Lua.SetTimeBudget(_main, _options.TimeBudgetMilliseconds);

        OpenLibraries();
        foreach ((string name, string source) in EngineModules)
        {
            _resolver.AddEmbedded(name, source);
        }

        // After the libraries: an allocation that fails outside a call cannot be caught.
        Lua.SetMemoryLimit(_main, (nuint)_options.MemoryLimit);
    }

    /// <summary>The engine's own Luau modules (<c>@engine/test</c>…): name and source, from the library's embedded resources.</summary>
    public static IReadOnlyList<(string Name, string Source)> EngineModules { get; } = ReadEngineModules();

    private static List<(string Name, string Source)> ReadEngineModules()
    {
        var modules = new List<(string Name, string Source)>();
        System.Reflection.Assembly assembly = typeof(LuauVm).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            // A module in a folder is @engine/folder/name (the build writes the folder with the platform's separator).
            string normalized = resource.Replace('\\', '/');
            if (normalized.StartsWith("Lib/", StringComparison.Ordinal) && normalized.EndsWith(".luau", StringComparison.Ordinal))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!, Encoding.UTF8);
                modules.Add((normalized["Lib/".Length..^".luau".Length], reader.ReadToEnd()));
            }
        }

        modules.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return modules;
    }

    /// <summary>The engine's and the base game's own scripts.</summary>
    public ScriptOwner BaseOwner { get; }

    public bool NativeCodeEnabled => Lua.NativeEnabled(_main) != 0;

    /// <summary>Bytes the VM has allocated, all owners together.</summary>
    public long MemoryUsed() => (long)Lua.MemoryUsed(_main);

    internal long MemoryUsed(ScriptOwner owner) => (long)Lua.Totalbytes(_main, owner.Category);

    /// <summary>Makes an owner (a mod) with its own memory category: up to 255 in one VM.</summary>
    public ScriptOwner CreateOwner(string id, bool isMod, long memoryLimit = 0, int timeBudgetMilliseconds = 0)
    {
        ThrowIfDisposed();
        if (_nextCategory >= Lua.MemoryCategories)
        {
            throw new InvalidOperationException($"A VM has {Lua.MemoryCategories - 1} owners at most.");
        }

        var owner = new ScriptOwner(this, _owners.Count, id, isMod, _nextCategory++, memoryLimit, timeBudgetMilliseconds > 0 ? timeBudgetMilliseconds : _options.TimeBudgetMilliseconds);
        _owners.Add(owner);
        Lua.SetCategoryLimit(_main, owner.Category, (nuint)memoryLimit);
        return owner;
    }

    /// <summary>Lets <c>require</c> load files below <paramref name="directory"/>; nothing outside the roots can be required.</summary>
    public void AddRoot(string directory) => _resolver.AddRoot(directory);

    /// <summary>Makes <c>require("@name/x")</c> mean <paramref name="path"/> plus <c>/x</c> for every script (a package's id for its folder, <c>mod://cool</c>).</summary>
    public void AddAlias(string name, string path) => _resolver.AddAlias(name, path);

    /// <summary>Adds a module of the engine, required as <c>@engine/name</c>.</summary>
    public void AddEmbeddedModule(string name, string source) => _resolver.AddEmbedded(name, source);

    /// <summary>
    /// Compiles and runs a chunk as <paramref name="owner"/> and returns what it returns. A chunk name that starts with <c>@</c> is a path
    /// (relative requires start from its folder); <c>=name</c> is a name for messages only. Failures are <see cref="ScriptException"/>s.
    /// </summary>
    public IReadOnlyList<Value> Execute(ScriptOwner owner, string chunkName, string source)
    {
        ThrowIfDisposed();
        CheckOwner(owner);

        // An owner that hit its limit has the garbage of the failed call counted until a collection: do it now, not at Luau's leisure,
        // or its next call would fail too.
        if (owner.MemoryLimit > 0 && MemoryUsed(owner) > owner.MemoryLimit)
        {
            CollectGarbage();
        }

        State* thread = NewThread(owner, out int reference);
        try
        {
            Load(thread, chunkName, source);
            ThrowIfFailed(Lua.ProtectedCall(thread, 0, Lua.Multret, owner.TimeBudgetMilliseconds), thread);

            int count = Lua.Gettop(thread);
            var results = new Value[count];
            for (int i = 0; i < count; i++)
            {
                results[i] = ReadValue(thread, i + 1, TypeRef.Any);
            }

            return results;
        }
        finally
        {
            _ = Lua.Unref(_main, reference);
            ProcessPending();
        }
    }

    /// <summary>Runs a full collection: what no script can reach any more is freed, and the owners' counts drop.</summary>
    public void CollectGarbage()
    {
        ThrowIfDisposed();
        _ = Lua.Gc(_main, 2, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closing the state collects every userdata, which notes the objects scripts made; those the engine did not adopt end here.
        Lua.CloseVm(_main);
        s_vms.TryRemove((nint)_main, out _);
        while (_orphans.TryDequeue(out SlotHandle handle))
        {
            if (EngineObject.TryGet(handle, out EngineObject? obj) && !obj.IsAdopted)
            {
                obj.Destroy();
            }
        }

        foreach (nint name in _nativeNames.Values)
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    // ---- Threads and errors ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A thread for a script of <paramref name="owner"/>: its objects count in the owner's category, and it has globals of its own over the
    /// read-only ones. Free <paramref name="reference"/> when done; until then the thread cannot be collected.
    /// </summary>
    internal State* NewThread(ScriptOwner owner, out int reference)
    {
        Lua.Setmemcat(_main, owner.Category); // a thread inherits the category it is made in
        State* thread = Lua.Newthread(_main);
        reference = Lua.Ref(_main, -1);
        Lua.Settop(_main, -2);
        Lua.Setmemcat(_main, 0);

        Lua.Setthreaddata(thread, (void*)(nint)(owner.Index + 1));
        Lua.LuaLSandboxthread(thread);
        return thread;
    }

    internal static void Load(State* thread, string chunkName, string source)
    {
        byte[] name = Encoding.UTF8.GetBytes(chunkName + "\0");
        byte[] text = Encoding.UTF8.GetBytes(source);
        fixed (byte* namePointer = name)
        fixed (byte* textPointer = text)
        {
            ThrowIfFailed(Lua.LoadSource(thread, namePointer, textPointer, (nuint)text.Length, 0), thread);
        }
    }

    /// <summary>Throws the <see cref="ScriptException"/> for a failed result; the message is on top of the stack of <paramref name="thread"/>.</summary>
    internal static void ThrowIfFailed(CallResult result, State* thread)
    {
        if (result is CallResult.Ok or CallResult.Yielded)
        {
            return;
        }

        string message = Stack.Is(thread, -1, Apogee.Native.Luau.Type.Tstring) ? Stack.ReadString(thread, -1) : Stack.Describe(thread, -1);
        ScriptErrorKind kind = result switch
        {
            CallResult.CompileError => ScriptErrorKind.Compile,
            CallResult.Timeout => ScriptErrorKind.Timeout,
            CallResult.MemoryError => ScriptErrorKind.Memory,
            _ => ScriptErrorKind.Runtime,
        };
        throw new ScriptException(kind, message);
    }

    private ScriptOwner OwnerOf(State* thread) => _owners[(int)(nint)Lua.Getthreaddata(thread) - 1];

    private static LuauVm VmOf(State* thread) => s_vms[(nint)Lua.Mainthread(thread)];

    private void CheckOwner(ScriptOwner owner)
    {
        if (owner is null || owner.Vm != this)
        {
            throw new ArgumentException("The owner belongs to another VM.", nameof(owner));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>A thread made by a script (a coroutine) belongs to the owner of the script that made it.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void UserThread(State* parent, State* child)
    {
        if (parent is not null)
        {
            Lua.Setthreaddata(child, Lua.Getthreaddata(parent));
        }
    }
}
