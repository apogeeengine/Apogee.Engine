using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;
using Apogee.Native.Luau;

namespace Apogee.Native.Tests;

/// <summary>The Luau library and our layer over it (Native/luau/apogee_luau.h): loading, errors, limits, managed functions.</summary>
public unsafe class LuauShimTests
{
    private static void RequireLuau() => NativeRequirements.Require(typeof(Lua).Assembly, "luau");

    private static CallResult Load(State* state, string source, string chunk = "=test")
    {
        byte[] name = Encoding.UTF8.GetBytes(chunk + "\0");
        byte[] text = Encoding.UTF8.GetBytes(source);
        fixed (byte* namePointer = name)
        fixed (byte* textPointer = text)
        {
            return Lua.LoadSource(state, namePointer, textPointer, (nuint)text.Length, 0);
        }
    }

    /// <summary>Loads and runs a chunk with no arguments; what it returns (up to <paramref name="results"/> values) or the error is on the stack.</summary>
    private static CallResult Run(State* state, string source, int results = 0, int budget = 0)
    {
        CallResult loaded = Load(state, source);
        return loaded == CallResult.Ok ? Lua.ProtectedCall(state, 0, results, budget) : loaded;
    }

    private static string Message(State* state) => Marshal.PtrToStringUTF8((IntPtr)Lua.Tolstring(state, -1, null)) ?? "";

    [Fact]
    public void ACompiledChunkRuns()
    {
        RequireLuau();

        State* state = Lua.NewVm(0);
        try
        {
            Lua.LuaLOpenlibs(state);
            Assert.Equal(CallResult.Ok, Run(state, "return 6 * 7", results: 1));
            Assert.Equal(42.0, Lua.Tonumberx(state, -1, null));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void BytecodeFromLuauCompileLoadsAndIsFreedByTheLibrary()
    {
        RequireLuau();

        State* state = Lua.NewVm(0);
        try
        {
            byte[] source = "return 6 * 7"u8.ToArray();
            nuint size;
            byte* bytecode;
            fixed (byte* text = source)
            {
                bytecode = Lua.LuauCompile(text, (nuint)source.Length, null, &size);
            }

            try
            {
                fixed (byte* name = "=bytecode\0"u8)
                {
                    Assert.Equal(0, Lua.LuauLoad(state, name, bytecode, size, 0));
                }
            }
            finally
            {
                Lua.Free(bytecode); // not NativeMemory.Free: the library's C runtime allocated it
            }

            Assert.Equal(CallResult.Ok, Lua.ProtectedCall(state, 0, 1, 0));
            Assert.Equal(42.0, Lua.Tonumberx(state, -1, null));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void ASyntaxErrorIsReportedWithItsMessage()
    {
        RequireLuau();

        // The parser formats its message with Luau::format (Common/src), which the library must contain.
        State* state = Lua.NewVm(0);
        try
        {
            Assert.Equal(CallResult.CompileError, Load(state, "return +", "=bad"));
            Assert.StartsWith("bad:1:", Message(state));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void ARuntimeErrorCarriesATraceback()
    {
        RequireLuau();

        State* state = Lua.NewVm(0);
        try
        {
            Lua.LuaLOpenlibs(state);
            Assert.Equal(CallResult.RuntimeError, Run(state, "local function explode() error('boom') end\nexplode()"));
            string message = Message(state);
            Assert.Contains("boom", message);
            Assert.Contains("test:1 function explode", message); // the traceback: one line per frame
            Assert.Contains("test:2", message);
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void ALoopThatOutlivesItsBudgetTimesOut()
    {
        RequireLuau();

        State* state = Lua.NewVm(0);
        try
        {
            Lua.LuaLOpenlibs(state);
            Assert.Equal(CallResult.Timeout, Run(state, "while true do end", budget: 50));
            Assert.Contains("time budget of 50 ms", Message(state));
            Lua.Settop(state, 0);

            // The deadline was reset with the call: the next one gets a full budget.
            Assert.Equal(CallResult.Ok, Run(state, "local n = 0 for i = 1, 1000 do n += i end return n", results: 1, budget: 50));
            Assert.Equal(500500.0, Lua.Tonumberx(state, -1, null));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void AScriptCannotSwallowTheTimeoutWithPcall()
    {
        RequireLuau();

        State* state = Lua.NewVm(0);
        try
        {
            Lua.LuaLOpenlibs(state);
            Assert.Equal(CallResult.Timeout, Run(state, "while true do pcall(function() while true do end end) end", budget: 50));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void TheDefaultBudgetAppliesWhenACallGivesNone()
    {
        RequireLuau();

        State* state = Lua.NewVm(0);
        try
        {
            Lua.SetTimeBudget(state, 40);
            Assert.Equal(CallResult.Timeout, Run(state, "while true do end"));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void TheHardMemoryLimitRaisesAMemoryErrorAndTheStateSurvivesIt()
    {
        RequireLuau();

        State* state = Lua.NewVm(8 * 1024 * 1024);
        try
        {
            Lua.LuaLOpenlibs(state);
            Assert.Equal(CallResult.MemoryError, Run(state, "local t = {} for i = 1, 100000000 do t[i] = { i } end"));
            Assert.True(Lua.MemoryUsed(state) <= 8 * 1024 * 1024);
            Lua.Settop(state, 0);

            _ = Lua.Gc(state, 2, 0);
            Assert.Equal(CallResult.Ok, Run(state, "return 1 + 1", results: 1));
            Assert.Equal(2.0, Lua.Tonumberx(state, -1, null));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void TheHostIsNeverRefusedMemoryOutsideACall()
    {
        RequireLuau();

        // An error raised by an allocation the host makes (a thread, a global) would have no pcall to catch it: the process would abort.
        State* state = Lua.NewVm(0);
        try
        {
            Lua.LuaLOpenlibs(state);
            Lua.SetMemoryLimit(state, Lua.MemoryUsed(state) + 256 * 1024);

            Lua.Createtable(state, 200_000, 0); // 3 MB, far past the limit
            Assert.True(Lua.MemoryUsed(state) > 1024 * 1024);
            Lua.Settop(state, 0);

            // The limit still applies to scripts, and the state is usable once the memory is back.
            Assert.Equal(CallResult.MemoryError, Run(state, "local t = {} for i = 1, 10 do t[i] = { i } end"));
            Lua.Settop(state, 0);
            _ = Lua.Gc(state, 2, 0);
            Assert.Equal(CallResult.Ok, Run(state, "return 'fine'", results: 1));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void ACategoryLimitRaisesAMemoryErrorOnlyInThatCategory()
    {
        RequireLuau();

        State* state = Lua.NewVm(0);
        try
        {
            Lua.LuaLOpenlibs(state);
            Lua.SetCategoryLimit(state, 5, 1024 * 1024);
            const string Allocate = "local t = {} for i = 1, 1000000 do t[i] = { i } end return #t";

            Lua.Setmemcat(state, 0);
            Assert.Equal(CallResult.Ok, Run(state, Allocate, results: 1));
            Lua.Settop(state, 0);
            _ = Lua.Gc(state, 2, 0);

            Lua.Setmemcat(state, 5);
            Assert.Equal(CallResult.MemoryError, Run(state, Allocate));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int Add(State* state)
    {
        Lua.Pushnumber(state, Lua.Tonumberx(state, 1, null) + Lua.Tonumberx(state, 2, null));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int Raise(State* state)
    {
        fixed (byte* message = "nope\0"u8)
        {
            Lua.Pushstring(state, message);
        }

        return Lua.Raise;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int AllocateALot(State* state)
    {
        Lua.Createtable(state, 1_000_000, 0); // 16 MB, more than the limit of the test
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int Counter(State* state)
    {
        // Upvalue 1 is the wrapper's; ours starts at 2.
        double value = Lua.Tonumberx(state, Lua.Globalsindex - 2, null) + 1;
        Lua.Pushnumber(state, value);
        Lua.Pushnumber(state, value);
        Lua.Replace(state, Lua.Globalsindex - 2);
        return 1;
    }

    private static void Register(State* state, delegate* unmanaged[Cdecl]<State*, int> function, string global, int upvalues = 0)
    {
        fixed (byte* name = Encoding.UTF8.GetBytes(global + "\0"))
        {
            Lua.PushManagedFunction(state, function, name, upvalues);
            Lua.Rawsetfield(state, Lua.Globalsindex, name);
        }
    }

    [Fact]
    public void AManagedFunctionIsCalledWithItsArguments()
    {
        RequireLuau();

        State* state = Lua.NewVm(0);
        try
        {
            Lua.LuaLOpenlibs(state);
            Register(state, &Add, "add");
            Assert.Equal(CallResult.Ok, Run(state, "return add(40, 2)", results: 1));
            Assert.Equal(42.0, Lua.Tonumberx(state, -1, null));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void AManagedFunctionKeepsItsUpvalues()
    {
        RequireLuau();

        State* state = Lua.NewVm(0);
        try
        {
            Lua.LuaLOpenlibs(state);
            Lua.Pushnumber(state, 10);
            Register(state, &Counter, "counter", upvalues: 1);
            Assert.Equal(CallResult.Ok, Run(state, "counter() counter() return counter()", results: 1));
            Assert.Equal(13.0, Lua.Tonumberx(state, -1, null));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void AnErrorFromAManagedFunctionIsCaughtByPcall()
    {
        RequireLuau();

        State* state = Lua.NewVm(0);
        try
        {
            Lua.LuaLOpenlibs(state);
            Register(state, &Raise, "raise");

            // From a script's own pcall, and from the host's.
            Assert.Equal(CallResult.Ok, Run(state, "local ok, err = pcall(raise) return ok, err", results: 2));
            Assert.Equal(0, Lua.Toboolean(state, -2));
            Assert.Equal("nope", Message(state));
            Lua.Settop(state, 0);

            Assert.Equal(CallResult.RuntimeError, Run(state, "raise()"));
            Assert.Contains("nope", Message(state));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void ManagedCodeIsNeverUnwoundByTheMemoryLimit()
    {
        RequireLuau();

        State* state = Lua.NewVm(4 * 1024 * 1024);
        try
        {
            Lua.LuaLOpenlibs(state);
            Register(state, &AllocateALot, "allocate");

            // The managed function gets its 16 MB (it cannot be unwound); the error is raised when it returns.
            Assert.Equal(CallResult.MemoryError, Run(state, "allocate()"));
            Lua.Settop(state, 0);

            _ = Lua.Gc(state, 2, 0);
            Assert.Equal(CallResult.Ok, Run(state, "return 'still here'", results: 1));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void NativeCodeGenerationRunsAnOptedInChunk()
    {
        RequireLuau();

        State* state = Lua.NewVm(0);
        try
        {
            Lua.LuaLOpenlibs(state);
            bool expected = RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;
            Assert.Equal(expected, Lua.NativeEnabled(state) != 0);

            const string Body = "local s = 0 for i = 1, 100 do s += i * 2 end return s";
            Assert.Equal(CallResult.Ok, Run(state, "--!native\n" + Body, results: 1));
            Assert.Equal(10100.0, Lua.Tonumberx(state, -1, null));
            Lua.Settop(state, 0);

            Assert.Equal(CallResult.Ok, Run(state, Body, results: 1)); // interpreted
            Assert.Equal(10100.0, Lua.Tonumberx(state, -1, null));
        }
        finally
        {
            Lua.CloseVm(state);
        }
    }

    [Fact]
    public void TheBindingsDoNotExposeWhatCanRaiseAnError()
    {
        RequireLuau();

        string[] names = [.. typeof(Lua).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(m => m.Name)];

        // Managed code could not survive the longjmp these cause (Native/luau/apogee_luau.h).
        foreach (string unsafeFunction in new[] { "Error", "Call", "Gettable", "Settable", "Getfield", "Setfield", "Concat", "Equal", "Lessthan", "Next",
            "Yield", "Pushcclosurek", "LuaLCheckinteger", "LuaLChecklstring", "LuaLCheckstack", "LuaLOptinteger", "LuaLTolstring", "LuaLNewstate", "Newstate", "Close" })
        {
            Assert.DoesNotContain(unsafeFunction, names);
        }

        Assert.Contains("ProtectedCall", names);
        Assert.Contains("Rawgetfield", names);
    }
}
