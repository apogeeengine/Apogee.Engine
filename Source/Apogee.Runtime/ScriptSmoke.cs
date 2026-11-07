using System;
using System.Collections.Generic;
using Apogee.Core.Logging;
using Apogee.Reflection;
using Apogee.Scripting;

namespace Apogee.Runtime;

/// <summary>A class for the smoke test: a property, a method and a signal that a script uses.</summary>
[ApogeeClass(Creatable = true)]
public sealed partial class SmokeActor : EngineObject
{
    [Property]
    public int Hits { get; set; }

    [Signal]
    public Signal<int> Struck { get; } = new();

    [Method]
    public void Hit(int amount)
    {
        Hits += amount;
        Struck.Emit(Hits);
    }
}

/// <summary>
/// Runs the scripting layer under NativeAOT: managed functions called back from Luau (UnmanagedCallersOnly through function pointers),
/// require, owners with their own limits, and the errors that come back across the native boundary.
/// </summary>
internal static class ScriptSmoke
{
    public static void Run()
    {
        var printed = new List<string>();
        using var vm = new LuauVm(new LuauVmOptions { Output = (owner, text, _) => printed.Add($"{owner.Id}:{text}") });
        vm.AddEmbeddedModule("answer", "return { value = 42 }");
        ScriptOwner mod = vm.CreateOwner("smokemod", isMod: true, memoryLimit: 1024 * 1024, timeBudgetMilliseconds: 50);

        IReadOnlyList<Value> results = vm.Execute(vm.BaseOwner, "=smoke", "local x = 2 print('hi') return require('@engine/answer').value, Vector3(x, 0, 1)");
        Expect(results[0].ToInt32() == 42 && results[1].ToVector3() == new System.Numerics.Vector3(2, 0, 1) && printed[0] == "base:hi", "values and require");

        Expect(Fails(vm, mod, "while true do end") == ScriptErrorKind.Timeout, "time budget");
        Expect(Fails(vm, mod, "local t = {} for i = 1, 1e7 do t[i] = { i } end") == ScriptErrorKind.Memory, "memory limit");
        Expect(Fails(vm, mod, "error('boom')") == ScriptErrorKind.Runtime, "runtime error");
        Expect(Fails(vm, mod, "return +") == ScriptErrorKind.Compile, "compile error");

        // The bridge: a reflected class made by the script, a signal back into Luau, and the ECS with a system the script defined.
        using var world = new Apogee.Ecs.World();
        vm.SetGlobal("world", world);
        IReadOnlyList<Value> bridged = vm.Execute(vm.BaseOwner, "=bridge", @"
            local actor = SmokeActor.new()
            local seen = 0
            actor.struck:connect(function(n) seen = n end)
            actor:hit(3) actor:hit(4)
            local Counter = world:component('Counter', { n = 'i32' })
            local entity = world:create_entity()
            world:set(entity, Counter, { n = 1 })
            world:system('Inc', { Counter }, function(e, c) c.n += seen end)
            world:progress(0.016)
            return actor.hits, world:get(entity, Counter).n");
        Expect(bridged[0].ToInt32() == 7 && bridged[1].ToInt32() == 8, "objects, signals and ECS from scripts");

        Log.Info("Runtime", $"Scripting: {vm.Execute(mod, "=after", "return 'ok'")[0].ToStringValue()} (native code {(vm.NativeCodeEnabled ? "on" : "off")})");
    }

    private static ScriptErrorKind Fails(LuauVm vm, ScriptOwner owner, string source)
    {
        try
        {
            vm.Execute(owner, "=smoke", source);
        }
        catch (ScriptException e)
        {
            return e.Kind;
        }

        throw new InvalidOperationException($"The script did not fail: {source}");
    }

    private static void Expect(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"The scripting smoke test failed: {what}.");
        }
    }
}
