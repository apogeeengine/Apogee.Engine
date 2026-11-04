using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Apogee.Ecs;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Apogee.Scripting;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Scripting.Tests;

[Component]
public struct Health
{
    public int Current;
    public int Max;
}

public class EcsScriptTests
{
    private static (VmFixture Fixture, Apogee.Ecs.World World) WithWorld()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        var f = new VmFixture();
        var world = new Apogee.Ecs.World();
        f.Vm.SetGlobal("world", world);
        return (f, world);
    }

    private const string Setup = "local Pos = world:component('Pos', { value = 'vec3', on = 'bool' })\n";

    [Fact]
    public void AScriptDefinesAComponentAndSetsAndGetsIt()
    {
        var (f, _) = WithWorld();
        using (f)
        {
            IReadOnlyList<Value> results = f.Run(Setup + @"
                local e = world:create_entity()
                world:set(e, Pos, { value = Vector3(1, 2, 3), on = true })
                local p = world:get(e, Pos)
                return p.value, p.on, world:get(e, world:component('Pos')).value");

            Assert.Equal(new Vector3(1, 2, 3), results[0].ToVector3());
            Assert.True(results[1].ToBool());
            Assert.Equal(new Vector3(1, 2, 3), results[2].ToVector3());
        }
    }

    [Fact]
    public void AFieldGivenAsATableCarriesWhatAnInspectorShows()
    {
        var (f, world) = WithWorld();
        using (f)
        {
            f.Run("""
                world:component('Vehicle', {
                    speed = { type = 'f32', min = 0, max = 90, step = 0.5, category = 'Motion' },
                    gear = 'i32',
                    token = { type = 'u32', hidden = true },
                    odometer = { type = 'f64', read_only = true },
                })
                """);
            ComponentInfo info = world.GetComponentInfo(world.FindComponent("Vehicle"))!;
            Assert.Equal(["gear", "odometer", "speed", "token"], info.Fields.Select(x => x.Name));   // the layout is in name order, as before
            Assert.Equal(FieldKind.Float32, info.FindField("speed")!.Kind);
            InspectorMeta speed = info.FindField("speed")!.Meta!;
            Assert.Equal("0 to 90", speed.RangeText);
            Assert.Equal(0.5, speed.Step);
            Assert.Equal("Motion", speed.Category);
            Assert.Null(info.FindField("gear")!.Meta);
            Assert.True(info.FindField("token")!.Meta!.Hidden);
            Assert.True(info.FindField("odometer")!.Meta!.ReadOnly);

            Assert.Contains("needs a type", f.Fails("world:component('NoType', { x = { min = 0 } })").Message);
            Assert.Contains("unknown entry 'minimum'", f.Fails("world:component('Typo', { x = { type = 'f32', minimum = 0 } })").Message);
        }
    }

    [Fact]
    public void ADefinitionIsTheSameComponentAgainAndAConflictIsAnError()
    {
        var (f, _) = WithWorld();
        using (f)
        {
            Assert.True(f.Run(Setup + "return world:component('Pos', { on = 'bool', value = 'vec3' }) == Pos")[0].ToBool());
            Assert.Contains("already registered with another layout", f.Fails("world:component('Pos', { value = 'f64' })").Message);
            Assert.Contains("'quaternion' is not a field type", f.Fails("world:component('Odd', { q = 'quaternion' })").Message);
            Assert.Contains("no component named Nope is registered", f.Fails("world:component('Nope')").Message);
        }
    }

    [Fact]
    public void ARegisteredComponentOfTheEngineIsFoundByName()
    {
        var (f, world) = WithWorld();
        using (f)
        {
            f.Run("local Health = world:component('Health') local e = world:create_entity('hero') world:set(e, Health, { Current = 5, Max = 10 })");

            Assert.Equal(5, world.Get<Health>(world.Lookup("hero")).Current);
            Assert.Equal(10.0, f.Run("local Health = world:component('Health') return world:get(world:lookup('hero'), Health).Max")[0].ToDouble());
        }
    }

    [Fact]
    public void MistakesWithFieldsAreErrors()
    {
        var (f, _) = WithWorld();
        using (f)
        {
            Assert.Contains("'nope' is not a field of Pos", f.Fails(Setup + "world:set(world:create_entity(), Pos, { nope = 1 })").Message);
            Assert.Contains("Pos.on: expected boolean, got number", f.Fails(Setup + "world:set(world:create_entity(), Pos, { on = 5 })").Message);
            Assert.Contains("is not alive", f.Fails(Setup + "local e = world:create_entity() world:destroy(e) world:set(e, Pos, {})").Message);
        }
    }

    [Fact]
    public void EachGivesViewsThatWriteInPlace()
    {
        var (f, _) = WithWorld();
        using (f)
        {
            IReadOnlyList<Value> results = f.Run(Setup + @"
                local entities = {}
                for i = 1, 3 do
                    local e = world:create_entity()
                    world:set(e, Pos, { value = Vector3(i, 0, 0) })
                    entities[i] = e
                end
                local visited = 0
                world:each({ Pos }, function(entity, pos)
                    visited += 1
                    pos.value = pos.value + Vector3(10, 0, 0)
                    pos.on = true
                end)
                return visited, world:get(entities[1], Pos).value, world:get(entities[3], Pos).value, world:get(entities[2], Pos).on");

            Assert.Equal(3, results[0].ToInt32());
            Assert.Equal(new Vector3(11, 0, 0), results[1].ToVector3());
            Assert.Equal(new Vector3(13, 0, 0), results[2].ToVector3());
            Assert.True(results[3].ToBool());
        }
    }

    [Fact]
    public void EachCanMatchSeveralComponentsExcludeOnesAndCount()
    {
        var (f, _) = WithWorld();
        using (f)
        {
            IReadOnlyList<Value> results = f.Run(Setup + @"
                local Vel = world:component('Vel', { value = 'vec3' })
                local Frozen = world:component('Frozen', {})
                for i = 1, 4 do
                    local e = world:create_entity()
                    world:set(e, Pos, { value = Vector3(i, 0, 0) })
                    if i % 2 == 0 then world:set(e, Vel, { value = Vector3(0, i, 0) }) end
                    if i == 4 then world:set(e, Frozen) end
                end
                local both, moving = 0, 0
                world:each({ Pos, Vel }, function(e, pos, vel) both += 1 end)
                world:each({ Pos, Vel, { Frozen, 'not' } }, function(e, pos, vel) moving += 1 end)
                return both, moving, world:count({ Pos }), world:count({ Pos, { Vel, 'not' } })");

            Assert.Equal([2, 1, 4, 2], [results[0].ToInt32(), results[1].ToInt32(), results[2].ToInt32(), results[3].ToInt32()]);
        }
    }

    [Fact]
    public void AViewIsOnlyValidDuringItsCall()
    {
        var (f, _) = WithWorld();
        using (f)
        {
            ScriptException error = f.Fails(Setup + @"
                world:set(world:create_entity(), Pos, {})
                local kept
                world:each({ Pos }, function(e, pos) kept = pos end)
                return kept.value");

            Assert.Contains("a component view is only valid during the call it was given to", error.Message);
        }
    }

    [Fact]
    public void AnErrorInEachReachesTheCallerAndLeavesTheWorldUsable()
    {
        var (f, world) = WithWorld();
        using (f)
        {
            Assert.Contains("inside each", f.Fails(Setup + "world:set(world:create_entity(), Pos, {}) world:each({ Pos }, function() error('inside each') end)").Message);

            // The query's iteration was ended cleanly: the world still works, structural changes included.
            Assert.Equal(1.0, f.Run(Setup + "return world:count({ Pos })")[0].ToDouble());
            f.Run(Setup + "world:set(world:create_entity(), Pos, {})");
        }
    }

    [Fact]
    public void StructuralChangesInEachAreDeferredUntilItEnds()
    {
        var (f, _) = WithWorld();
        using (f)
        {
            IReadOnlyList<Value> results = f.Run(Setup + @"
                local Tag = world:component('Tag', {})
                for i = 1, 5 do world:set(world:create_entity(), Pos, {}) end
                local visited = 0
                world:each({ Pos }, function(e, pos) visited += 1 world:set(e, Tag) end)
                return visited, world:count({ Pos, Tag })");

            Assert.Equal(5, results[0].ToInt32());
            Assert.Equal(5, results[1].ToInt32());
        }
    }

    [Fact]
    public void ASystemRunsEveryFrameInItsPhase()
    {
        var (f, world) = WithWorld();
        using (f)
        {
            f.Run(Setup + @"
                local Vel = world:component('Vel', { value = 'vec3' })
                local e = world:create_entity('mover')
                world:set(e, Pos, { value = Vector3(0, 0, 0) })
                world:set(e, Vel, { value = Vector3(2, 0, 0) })
                world:system('Move', { Pos, Vel }, function(entity, pos, vel)
                    pos.value = pos.value + vel.value * world.delta_time
                end, 'on_update')");

            world.Progress(0.5f);
            world.Progress(0.25f);

            Assert.Equal(1.5, f.Run(Setup + "return world:get(world:lookup('mover'), Pos).value.X")[0].ToDouble());
            Assert.Contains("'sometime' is not a phase", f.Fails("world:system('S', {}, function() end, 'sometime')").Message);
        }
    }

    [Fact]
    public void SystemsRunInPhaseOrder()
    {
        var (f, world) = WithWorld();
        using (f)
        {
            f.Run(Setup + @"
                world:set(world:create_entity(), Pos, {})
                log = {}
                world:system('A', { Pos }, function() print('update') end)
                world:system('B', { Pos }, function() print('post') end, 'post_update')
                world:system('C', { Pos }, function() print('pre') end, 'pre_update')");

            world.Progress(1);

            Assert.Equal(["pre", "update", "post"], f.Output.ConvertAll(o => o.Text));
        }
    }

    [Fact]
    public void AFailingScriptSystemIsLoggedAndSkippedWhileTheOthersRun()
    {
        var (f, world) = WithWorld();
        using (f)
        {
            f.Run(Setup + @"
                world:set(world:create_entity(), Pos, {})
                world:system('Bad', { Pos }, function() error('system broke') end)
                world:system('Good', { Pos }, function() print('good ran') end)");

            world.Progress(1); // does not throw: a script's failure is the script's
            world.Progress(1);

            Assert.Equal(["good ran", "good ran"], f.Output.ConvertAll(o => o.Text));
        }
    }

    [Fact]
    public void ASystemOfAModIsHeldToItsTimeBudget()
    {
        var (f, world) = WithWorld();
        using (f)
        {
            ScriptOwner mod = f.Vm.CreateOwner("loopmod", isMod: true, timeBudgetMilliseconds: 40);
            f.Run(Setup + "world:set(world:create_entity(), Pos, {}) world:system('Loop', { Pos }, function() while true do end end)", mod);

            var watch = Stopwatch.StartNew();
            world.Progress(1);

            Assert.True(watch.ElapsedMilliseconds < 5000);
        }
    }

    [Fact]
    public void EntitiesAreNumbersAndPairsAreTablesOfTwo()
    {
        var (f, _) = WithWorld();
        using (f)
        {
            IReadOnlyList<Value> results = f.Run(@"
                local likes, apple = world:create_entity('likes'), world:create_entity('apple')
                local e = world:create_entity()
                world:add(e, { likes, apple })
                local had = world:has(e, { likes, apple })
                world:remove(e, { likes, apple })
                return type(e), had, world:has(e, { likes, apple }), world:is_alive(e), world:get_name(apple)");

            Assert.Equal("number", results[0].ToStringValue());
            Assert.True(results[1].ToBool());
            Assert.False(results[2].ToBool());
            Assert.True(results[3].ToBool());
            Assert.Equal("apple", results[4].ToStringValue());
            Assert.Contains("a pair is a table of two entity ids", f.Fails("world:add(world:create_entity(), { 1, 'x' })").Message);
        }
    }

    [Fact]
    public void AScriptCanMakeItsOwnWorld()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        using var f = new VmFixture();

        IReadOnlyList<Value> results = f.Run(@"
            local w = World.new()
            local C = w:component('Counter', { n = 'i32' })
            local e = w:create_entity()
            w:set(e, C, { n = 41 })
            w:system('Count', { C }, function(entity, c) c.n = c.n + 1 end)
            w:progress(0.1)
            return w:get(e, C).n, w.frame");

        Assert.Equal(42.0, results[0].ToDouble());
        Assert.Equal(1.0, results[1].ToDouble());
    }
}
