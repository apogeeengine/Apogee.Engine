using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Apogee.Scripting;
using Xunit;
using Lua = Apogee.Native.Luau.Lua;

namespace Apogee.Scripting.Tests;

public sealed class VmFixture : IDisposable
{
    public List<(string Owner, string Text, bool Warning)> Output { get; } = [];

    public LuauVm Vm { get; }

    public VmFixture(long memoryLimit = 0, int timeBudget = 0, Func<string, ScriptOwner?>? ownerOfModule = null, IScriptFileSystem? files = null)
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        Vm = new LuauVm(new LuauVmOptions
        {
            MemoryLimit = memoryLimit,
            TimeBudgetMilliseconds = timeBudget,
            OwnerOfModule = ownerOfModule,
            Files = files ?? new PhysicalScriptFileSystem(),
            Output = (owner, text, warning) => Output.Add((owner.Id, text, warning)),
        });
    }

    public IReadOnlyList<Value> Run(string source, ScriptOwner? owner = null, string chunk = "=test") => Vm.Execute(owner ?? Vm.BaseOwner, chunk, source);

    public ScriptException Fails(string source, ScriptOwner? owner = null, string chunk = "=test") =>
        Assert.Throws<ScriptException>(() => Run(source, owner, chunk));

    public void Dispose() => Vm.Dispose();
}

public class VmTests
{
    [Fact]
    public void AChunkReturnsItsValues()
    {
        using var f = new VmFixture();

        IReadOnlyList<Value> results = f.Run("return 1, 'two', true, Vector3(1, 2, 3)");

        Assert.Equal(4, results.Count);
        Assert.Equal(1.0, results[0].ToDouble());
        Assert.Equal("two", results[1].ToStringValue());
        Assert.True(results[2].ToBool());
        Assert.Equal(new Vector3(1, 2, 3), results[3].ToVector3());
        Assert.Empty(f.Run("local x = 1"));
    }

    [Fact]
    public void Vector3IsAGlobalFunction()
    {
        using var f = new VmFixture();

        Assert.Equal(new Vector3(2, 0, 4), f.Run("local x = 2 local z = x * 2 return Vector3(x, 0, z)")[0].ToVector3()); // not constant folded
        Assert.Equal(Vector3.Zero, f.Run("local n = 0 return Vector3()")[0].ToVector3());
        Assert.Contains("Vector3 expects numbers", f.Fails("local s = 'a' return Vector3(s)").Message);
    }

    [Fact]
    public void PrintAndWarnGoToTheHostWithTheOwner()
    {
        using var f = new VmFixture();
        ScriptOwner mod = f.Vm.CreateOwner("coolmod", isMod: true);

        f.Run("print(1, 2.5, 'x', nil, true)");
        f.Run("warn('careful')", mod);

        Assert.Equal([("base", "1\t2.5\tx\tnil\ttrue", false), ("coolmod", "careful", true)], f.Output);
    }

    [Fact]
    public void TheStandardLibrariesWorkAndDynamicCodeIsGone()
    {
        using var f = new VmFixture();

        Assert.Equal(3.0, f.Run("return math.floor(3.7)")[0].ToDouble());
        Assert.Equal("a-1", f.Run("return string.format('a-%d', 1)")[0].ToStringValue());
        Assert.Equal("nil nil nil", f.Run("return tostring(getfenv) .. ' ' .. tostring(setfenv) .. ' ' .. tostring(loadstring)")[0].ToStringValue());
    }

    [Fact]
    public void ScriptsHaveGlobalsOfTheirOwnAndCannotChangeTheBuiltIns()
    {
        using var f = new VmFixture();

        f.Run("shared = 5");
        Assert.True(f.Run("return shared")[0].IsNil); // the next script is in another thread with other globals
        Assert.Equal(5.0, f.Run("shared = 5 return shared")[0].ToDouble());

        Assert.Contains("readonly", f.Fails("string.len = nil").Message);
        Assert.Contains("readonly", f.Fails("math.pi = 3").Message);
        Assert.Contains("readonly", f.Fails("getmetatable('').__index = nil").Message);
    }

    // ---- Errors ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ACompileErrorIsReportedWithItsPosition()
    {
        using var f = new VmFixture();

        ScriptException error = f.Fails("return +", chunk: "=broken");

        Assert.Equal(ScriptErrorKind.Compile, error.Kind);
        Assert.StartsWith("broken:1:", error.Message);
    }

    [Fact]
    public void ARuntimeErrorHasATraceback()
    {
        using var f = new VmFixture();

        ScriptException error = f.Fails("local function inner() error('boom') end\nlocal function outer() inner() end\nouter()", chunk: "=trace");

        Assert.Equal(ScriptErrorKind.Runtime, error.Kind);
        Assert.Contains("trace:1: boom", error.Message);
        Assert.Contains("function inner", error.Message);
        Assert.Contains("function outer", error.Message);
    }

    [Fact]
    public void AnErrorThatIsNotAStringIsDescribed()
    {
        using var f = new VmFixture();

        Assert.StartsWith("table: 0x", f.Fails("error({})").Message);
    }

    [Fact]
    public void AScriptCanCatchItsOwnErrors()
    {
        using var f = new VmFixture();

        Assert.Equal("caught", f.Run("local ok, err = pcall(function() error('x') end) return ok and 'no' or 'caught'")[0].ToStringValue());
    }

    // ---- Limits per owner -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ACallThatOutlivesTheBudgetOfItsOwnerTimesOut()
    {
        using var f = new VmFixture();
        ScriptOwner slow = f.Vm.CreateOwner("slowmod", isMod: true, timeBudgetMilliseconds: 40);

        ScriptException error = f.Fails("while true do end", slow);

        Assert.Equal(ScriptErrorKind.Timeout, error.Kind);
        Assert.Contains("time budget of 40 ms", error.Message);
        Assert.Equal(7.0, f.Run("return 3 + 4", slow)[0].ToDouble()); // the owner and the VM are fine afterwards
    }

    [Fact]
    public void TheVmDefaultBudgetAppliesToAnOwnerWithoutOne()
    {
        using var f = new VmFixture(timeBudget: 40);

        Assert.Equal(ScriptErrorKind.Timeout, f.Fails("while true do end").Kind);
        Assert.Equal(ScriptErrorKind.Timeout, f.Fails("while true do end", f.Vm.CreateOwner("m", true)).Kind);
    }

    [Fact]
    public void AnOwnerCannotUseMoreMemoryThanItsLimitAndTheOthersAreNotAffected()
    {
        using var f = new VmFixture();
        ScriptOwner greedy = f.Vm.CreateOwner("greedy", isMod: true, memoryLimit: 1024 * 1024);
        ScriptOwner other = f.Vm.CreateOwner("other", isMod: true, memoryLimit: 512L * 1024 * 1024);
        const string Allocate = "local t = {} for i = 1, 400000 do t[i] = { i } end return #t";

        ScriptException error = f.Fails(Allocate, greedy);
        Assert.Equal(ScriptErrorKind.Memory, error.Kind);

        Assert.Equal(400000.0, f.Run(Allocate, other)[0].ToDouble());
        Assert.True(other.MemoryUsed > 0);

        Assert.Equal(2.0, f.Run("return 1 + 1", greedy)[0].ToDouble()); // the greedy owner can run again at once: its garbage is collected first
    }

    [Fact]
    public void ACoroutineBelongsToTheOwnerThatMadeIt()
    {
        using var f = new VmFixture();
        ScriptOwner mod = f.Vm.CreateOwner("coroutines", isMod: true, memoryLimit: 1024 * 1024);

        // The coroutine allocates under the mod's category and limit, not the base game's.
        ScriptException error = f.Fails(
            "local co = coroutine.wrap(function() local t = {} for i = 1, 400000 do t[i] = { i } end end) co()",
            mod);

        Assert.Contains("not enough memory", error.Message); // an error crossing coroutine.wrap is a plain runtime error
        Assert.Equal(1.0, f.Run("local co = coroutine.wrap(function() local t = {} for i = 1, 400000 do t[i] = { i } end return 1 end) return co()")[0].ToDouble());
    }

    [Fact]
    public void TheHardLimitOfTheVmStopsEveryone()
    {
        using var f = new VmFixture(memoryLimit: 8 * 1024 * 1024);

        Assert.Equal(ScriptErrorKind.Memory, f.Fails("local t = {} for i = 1, 100000000 do t[i] = { i } end").Kind);
        Assert.True(f.Vm.MemoryUsed() <= 8 * 1024 * 1024);
    }

    [Fact]
    public void ThingsCreatedInOneOwnerAreCountedInThatOwner()
    {
        using var f = new VmFixture();
        ScriptOwner a = f.Vm.CreateOwner("a", true);
        ScriptOwner b = f.Vm.CreateOwner("b", true);

        f.Run("keep = {} for i = 1, 20000 do keep[i] = { i } end return 0", a);
        long used = a.MemoryUsed;
        f.Vm.CollectGarbage();

        Assert.True(used > 0);
        Assert.True(b.MemoryUsed < used);
    }

    [Fact]
    public void AnOwnerBelongsToItsVm()
    {
        using var first = new VmFixture();
        using var second = new VmFixture();

        Assert.Throws<ArgumentException>(() => second.Vm.Execute(first.Vm.BaseOwner, "=x", "return 1"));
    }

    // ---- Native code and lifetime ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ANativeChunkGivesTheSameResult()
    {
        using var f = new VmFixture();
        Assert.Equal(RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64, f.Vm.NativeCodeEnabled);

        const string Body = "local s = 0 for i = 1, 1000 do s += i * 3 end return s";
        Assert.Equal(1501500.0, f.Run("--!native\n" + Body)[0].ToDouble());
        Assert.Equal(1501500.0, f.Run(Body)[0].ToDouble());
    }

    [Fact]
    public void ADisposedVmCannotBeUsedAndTwoVmsAreIndependent()
    {
        var a = new VmFixture();
        using var b = new VmFixture();
        a.Run("answer = 42"); // in a thread of its own, but also proves the VM works

        a.Dispose();
        a.Dispose(); // twice is harmless

        Assert.Throws<ObjectDisposedException>(() => a.Run("return 1"));
        Assert.Equal(1.0, b.Run("return 1")[0].ToDouble());
    }

    [Fact]
    public void ManyVmsCanBeMadeAndDroppedInARow()
    {
        for (int i = 0; i < 20; i++)
        {
            using var f = new VmFixture(memoryLimit: 16 * 1024 * 1024);
            Assert.Equal(i, f.Run($"return {i}")[0].ToInt32());
        }
    }
}
