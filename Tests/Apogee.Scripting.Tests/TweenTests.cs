using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Apogee.Core.Logging;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Scripting.Tests;

/// <summary>The <c>tween</c> library on a bare VM: the test is the clock, through <see cref="LuauVm.AdvanceTime"/>.</summary>
public sealed class TweenTests : IDisposable
{
    private const double Frame = 1.0 / 60;

    private sealed class ListSink : ILogSink
    {
        public List<LogEntry> Entries { get; } = [];

        public void Write(in LogEntry entry)
        {
            lock (Entries)
            {
                Entries.Add(entry);
            }
        }
    }

    private readonly ListSink _log = new();
    private readonly VmFixture _f;
    private Apogee.Ecs.World? _world;

    public TweenTests()
    {
        Log.AddSink(_log);
        _f = new VmFixture();
    }

    public void Dispose()
    {
        _f.Dispose();
        _world?.Dispose();
        Log.RemoveSink(_log);
    }

    private LuauVm Vm => _f.Vm;

    private string[] Printed => [.. _f.Output.Select(o => o.Text)];

    /// <summary>A frame: the world's systems if there is a world, then the tasks and tweens, as the session orders them.</summary>
    private void Step(int frames = 1)
    {
        for (int i = 0; i < frames; i++)
        {
            _world?.Progress((float)Frame);
            Vm.AdvanceTime(Frame, Frame);
        }
    }

    private Apogee.Ecs.World WithWorld()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        _world = new Apogee.Ecs.World();
        Vm.SetGlobal("world", _world);
        return _world;
    }

    private string[] ErrorsOf(string category)
    {
        lock (_log.Entries)
        {
            return [.. _log.Entries.Where(e => e.Level == LogLevel.Error && e.Category == category).Select(e => e.Message)];
        }
    }

    private static Entity EntityOf(Value value) => new((ulong)value.ToDouble());

    // ---- Easing ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryEaseStartsAtZeroAndEndsAtOne()
    {
        foreach (Ease ease in Enum.GetValues<Ease>())
        {
            Assert.Equal(0, Easing.Apply(ease, 0));
            Assert.Equal(1, Easing.Apply(ease, 1));
            Assert.Equal(0, Easing.Apply(ease, -3)); // clamped
            Assert.Equal(1, Easing.Apply(ease, 7));
            Assert.Equal(0, Easing.Apply(ease, double.NaN));
            Assert.True(double.IsFinite(Easing.Apply(ease, 0.5)), ease.ToString());
        }

        Assert.Equal(31, Enum.GetValues<Ease>().Length); // linear and ten families of three
    }

    [Fact]
    public void EasesMatchTheirFormulasAtSamplePoints()
    {
        (Ease Ease, double T, double Expected)[] samples =
        [
            (Ease.Linear, 0.3, 0.3),
            (Ease.QuadIn, 0.5, 0.25),
            (Ease.QuadOut, 0.5, 0.75),
            (Ease.QuadInOut, 0.25, 0.125),
            (Ease.QuadInOut, 0.75, 0.875),
            (Ease.CubicIn, 0.5, 0.125),
            (Ease.CubicOut, 0.5, 0.875),
            (Ease.CubicInOut, 0.25, 0.0625),
            (Ease.QuartIn, 0.5, 0.0625),
            (Ease.QuartOut, 0.5, 0.9375),
            (Ease.QuartInOut, 0.25, 0.03125),
            (Ease.QuintIn, 0.5, 0.03125),
            (Ease.QuintOut, 0.5, 0.96875),
            (Ease.QuintInOut, 0.25, 0.015625),
            (Ease.SineIn, 0.5, 1 - Math.Cos(Math.PI / 4)),
            (Ease.SineOut, 0.5, Math.Sin(Math.PI / 4)),
            (Ease.SineInOut, 0.5, 0.5),
            (Ease.ExpoIn, 0.5, 0.03125),
            (Ease.ExpoOut, 0.5, 0.96875),
            (Ease.ExpoInOut, 0.25, 0.015625),
            (Ease.CircIn, 0.5, 1 - Math.Sqrt(0.75)),
            (Ease.CircOut, 0.5, Math.Sqrt(0.75)),
            (Ease.CircInOut, 0.5, 0.5),
            (Ease.BackIn, 0.5, (2.70158 * 0.125) - (1.70158 * 0.25)),
            (Ease.BackOut, 0.5, 1 + (2.70158 * -0.125) + (1.70158 * 0.25)),
            (Ease.BackInOut, 0.5, 0.5),
            (Ease.ElasticIn, 0.5, -0.015625),
            (Ease.ElasticOut, 0.5, 1.015625),
            (Ease.ElasticInOut, 0.5, 0.5),
            (Ease.BounceOut, 0.5, 0.765625),
            (Ease.BounceIn, 0.5, 0.234375),
            (Ease.BounceInOut, 0.5, 0.5),
        ];

        foreach ((Ease ease, double t, double expected) in samples)
        {
            Assert.True(Math.Abs(Easing.Apply(ease, t) - expected) < 1e-9, $"{ease} at {t}: {Easing.Apply(ease, t)} instead of {expected}");
        }

        // The curves that overshoot leave the range, the others stay in it.
        Assert.True(Easing.Apply(Ease.BackIn, 0.2) < 0);
        Assert.True(Easing.Apply(Ease.BackOut, 0.8) > 1);
        Assert.True(Easing.Apply(Ease.ElasticOut, 0.1) > 1);
        Assert.InRange(Easing.Apply(Ease.BounceOut, 0.9), 0, 1);
    }

    [Fact]
    public void ScriptsNameTheEasesInSnakeCase()
    {
        IReadOnlyList<Value> results = _f.Run("return tween.ease('quad_in_out', 0.25), tween.ease('linear', 2), tween.ease('bounce_out', 0.5)");

        Assert.Equal(0.125, results[0].ToDouble(), 9);
        Assert.Equal(1, results[1].ToDouble());
        Assert.Equal(0.765625, results[2].ToDouble(), 9);
        Assert.Contains("is not an ease", _f.Fails("tween.ease('quad', 0.5)").Message);
        Assert.Contains("string expected", _f.Fails("tween.ease(5, 0.5)").Message);
        Assert.Contains("NaN", _f.Fails("tween.ease('linear', 0/0)").Message);
    }

    [Fact]
    public void TheDefinitionsDeclareTheLibraryTheClassAndTheEases()
    {
        string text = DefinitionWriter.Write();

        Assert.Contains("type TweenOptions = { ease: Ease?, delay: number?, repeats: number?, reverses: boolean?, unscaled: boolean? }", text, StringComparison.Ordinal);
        Assert.Contains("type Ease = \"linear\" | \"quad_in\" | ", text, StringComparison.Ordinal);
        Assert.Contains("\"bounce_in_out\"", text, StringComparison.Ordinal);
        Assert.Contains("declare extern type Tween extends EngineObject with", text, StringComparison.Ordinal);
        Assert.Contains("completed: Signal<()>", text, StringComparison.Ordinal);
        Assert.Contains("function cancel(self): ()", text, StringComparison.Ordinal);
        Assert.Contains("function wait(self): boolean", text, StringComparison.Ordinal);
        Assert.Contains("declare tween: {", text, StringComparison.Ordinal);
        Assert.Contains("value: <T>(from: T, to: T, seconds: number, set: (T) -> (), options: TweenOptions?) -> Tween", text, StringComparison.Ordinal);
    }

    // ---- Values ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ANumberTweenWritesItsSetterEachStepAndEndsExactlyOnTo()
    {
        _f.Run("""
            local t = tween.value(0, 10, 1, function(v)
                print(string.format('%.4f', v))
                if v == 10 then print('exact') end
            end)
            print(t.playing, t.duration)
            """);

        Assert.Equal(["true\t1"], Printed); // nothing is set at creation
        Assert.Equal(1, Vm.Scheduler.TweenCount);

        Step();
        Assert.Equal("0.1667", Printed[1]);
        Step(58);
        Assert.Equal(60, Printed.Length); // 59 writes so far, plus the first line
        Assert.DoesNotContain("exact", Printed);
        Step();

        Assert.Equal("10.0000", Printed[^2]);
        Assert.Equal("exact", Printed[^1]);
        Assert.Equal(0, Vm.Scheduler.TweenCount);
        Step(5);
        Assert.Equal("exact", Printed[^1]); // it wrote no more
    }

    [Fact]
    public void AVectorAndAQuatTweenInterpolateAndSlerp()
    {
        _f.Run("""
            tween.value(Vector3(0, 0, 0), Vector3(2, 4, 6), 1, function(v) print(v.x, v.y, v.z) end)
            local target = Quat.axisAngle(Vector3(0, 1, 0), math.pi / 2)
            tween.value(Quat.identity, target, 1, function(q)
                print(string.format('%.6f', q:dot(Quat.axisAngle(Vector3(0, 1, 0), math.pi / 4))), q == target)
            end)
            """);

        Step(30);
        string[] half = Printed[^2..];
        Assert.Equal(["1\t2\t3", "1.000000\tfalse"], half);

        Step(30);
        Assert.Equal("2\t4\t6", Printed[^2]);
        Assert.StartsWith("0.9238", Printed[^1]); // dot of a half turn with a quarter turn: cos(22.5 degrees)
        Assert.EndsWith("\ttrue", Printed[^1]);
    }

    [Fact]
    public void AColourIsALinearRgbVector()
    {
        _f.Run("tween.value(Vector3(1, 0, 0), Vector3(0, 0, 1), 1, function(c) print(string.format('%.2f %.2f %.2f', c.x, c.y, c.z)) end, { ease = 'quad_in' })");

        Step(30);

        Assert.Equal("0.75 0.00 0.25", Printed[^1]); // quad_in at the half: 0.25 of the way
    }

    [Fact]
    public void MixedOrWrongTypesAreScriptErrors()
    {
        Assert.Contains("like 'from'", _f.Fails("tween.value(1, Vector3(1, 2, 3), 1, function() end)").Message);
        Assert.Contains("number, vector or Quat expected", _f.Fails("tween.value('a', 'b', 1, function() end)").Message);
        Assert.Contains("number expected", _f.Fails("tween.value(1, 2, 'x', function() end)").Message);
        Assert.Contains("finite", _f.Fails("tween.value(1, 2, 0/0, function() end)").Message);
        Assert.Contains("function expected", _f.Fails("tween.value(1, 2, 1, nil)").Message);
        Assert.Contains("finite", _f.Fails("tween.value(0/0, 2, 1, function() end)").Message);
        Assert.Contains("is not an option", _f.Fails("tween.value(1, 2, 1, function() end, { speed = 3 })").Message);
        Assert.Contains("is not an ease", _f.Fails("tween.value(1, 2, 1, function() end, { ease = 'wobble' })").Message);
        Assert.Contains("whole number", _f.Fails("tween.value(1, 2, 1, function() end, { repeats = 1.5 })").Message);
        Assert.Contains("boolean", _f.Fails("tween.value(1, 2, 1, function() end, { reverses = 1 })").Message);
        Assert.Contains("table of options", _f.Fails("tween.value(1, 2, 1, function() end, 'fast')").Message);
        Assert.Equal(0, Vm.Scheduler.TweenCount); // nothing was left half made
    }

    [Fact]
    public void ZeroSecondsFinishesOnTheFirstStep()
    {
        _f.Run("""
            local t = tween.value(0, 5, 0, function(v) print(v) end)
            t.completed:connect(function() print('completed') end)
            """);

        Assert.Empty(Printed);
        Step();
        Assert.Equal(["5", "completed"], Printed);
    }

    [Fact]
    public void TheTweenHandleReadsItsState()
    {
        _f.Run("""
            local t = tween.value(0, 1, 1, function() end, { ease = 'quad_in' })
            task.spawn(function()
                task.wait(0.5)
                print(t.playing, t.paused, string.format('%.3f', t.progress), t.duration, t.ease)
                t:pause()
                print(t.playing, t.paused)
                task.wait(1)
                print(string.format('%.3f', t.progress))
                t:resume()
                task.wait(1)
                print(t.playing, t.paused, t.progress)
            end)
            """);

        Step(30); // the task runs before the tween moves in a step, so the tween has made 29 steps
        Assert.Equal("true\tfalse\t0.483\t1\tquad_in", Printed[0]);
        Assert.Equal("false\ttrue", Printed[1]);
        Step(60);
        Assert.Equal("0.483", Printed[2]); // held while paused
        Step(60);
        Assert.Equal("false\tfalse\t1", Printed[3]);
    }

    // ---- Delay, repeats, clocks ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void DelayRepeatsAndReversesFollowTheClock()
    {
        _f.Run("""
            local t = tween.value(0, 1, 1, function(v) print(string.format('%.4f', v)) end, { delay = 0.5, repeats = 1, reverses = true })
            t.completed:connect(function() print('completed') end)
            """);

        Step(29);
        Assert.Empty(Printed); // the delay writes nothing
        Step();
        Assert.Equal(["0.0000"], Printed); // the delay ends and the start value is written
        Step(30);
        Assert.Equal("0.5000", Printed[^1]); // the first pass is half way
        Step(30);
        Assert.Equal("1.0000", Printed[^1]); // the second pass goes back and starts at 1
        Step(59);
        Assert.DoesNotContain("completed", Printed);
        Step();

        Assert.Equal(["0.0000", "completed"], Printed[^2..]); // the second pass ends on from
        Assert.Equal(0, Vm.Scheduler.TweenCount);
    }

    [Fact]
    public void ARepeatingTweenGoesOnAndTheLastPassEndsOnTo()
    {
        _f.Run("""
            local t = tween.value(0, 1, 0.5, function(v) print(string.format('%.4f', v)) end, { repeats = 2 })
            t.completed:connect(function() print('completed') end)
            """);

        Step(89);
        Assert.DoesNotContain("completed", Printed);
        Step();

        Assert.Equal(["1.0000", "completed"], Printed[^2..]);
        Assert.Equal(0, Vm.Scheduler.TweenCount);
    }

    [Fact]
    public void ARepeatForeverTweenNeverCompletesAndSurvivesAHugeStep()
    {
        _f.Run("""
            local t = tween.value(0, 1, 1, function(v) print(string.format('%.2f', v)) end, { repeats = -1, reverses = true })
            t.completed:connect(function() print('completed') end)
            """);

        Step(500);
        Vm.AdvanceTime(1000.25, 1000.25); // a time scale of a hundred: a thousand passes in one frame
        Assert.Equal(1, Vm.Scheduler.TweenCount);
        Assert.DoesNotContain("completed", Printed);
        Assert.InRange(double.Parse(Printed[^1], System.Globalization.CultureInfo.InvariantCulture), 0, 1);
    }

    [Fact]
    public void UnscaledTweensIgnoreTheGameClock()
    {
        _f.Run("""
            tween.value(0, 1, 1, function(v) print('game', string.format('%.4f', v)) end)
            tween.value(0, 1, 1, function(v) print('real', string.format('%.4f', v)) end, { unscaled = true })
            """);

        for (int i = 0; i < 30; i++)
        {
            Vm.AdvanceTime(0, Frame); // the game is paused
        }

        Assert.Equal("real\t0.5000", Printed[^1]);
        Assert.DoesNotContain(Printed, p => p.StartsWith("game", StringComparison.Ordinal));

        for (int i = 0; i < 15; i++)
        {
            Vm.AdvanceTime(Frame * 2, 0); // slow motion would be the other way: here the game runs at double speed and real time stands still
        }

        Assert.Equal("game\t0.5000", Printed[^1]);
    }

    [Fact]
    public void ATweenMadeByATaskMovesFromTheNextStep()
    {
        _f.Run("""
            task.spawn(function()
                task.wait(0.5)
                tween.value(0, 1, 1, function(v) print(string.format('%.4f', v)) end)
            end)
            """);

        Step(29);
        Step(); // the task makes the tween in this step, after the time moved
        Assert.Empty(Printed);
        Step();
        Assert.Equal(["0.0167"], Printed);
    }

    // ---- Properties and fields ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void APropertyTweenSetsAnEngineObjectsProperty()
    {
        var mover = new Mover { Speed = 2, Count = 1 };
        Vm.SetGlobal("mover", mover);
        _f.Run("""
            tween.property(mover, 'speed', 10, 1)
            tween.property(mover, 'count', 11, 1)
            tween.property(mover, 'tint', Vector3(4, 8, 12), 1)
            tween.property(mover, 'turn', Quat.axisAngle(Vector3(0, 0, 1), math.pi / 2), 1, { ease = 'sine_in_out' })
            """);

        Assert.Equal(2, mover.Speed); // read when the delay ends, written from the first step
        Step(30);

        Assert.Equal(6, mover.Speed, 3);
        Assert.Equal(6, mover.Count); // whole numbers are rounded
        Assert.Equal(new Vector3(2, 4, 6), mover.Tint);
        Assert.Equal(Math.Cos(Math.PI / 8), mover.Turn.W, 3); // half way to a quarter turn about z
        Assert.Equal(Math.Sin(Math.PI / 8), mover.Turn.Z, 3);

        Step(30);
        Assert.Equal(10, mover.Speed);
        Assert.Equal(11, mover.Count);
        Assert.Equal(new Vector3(4, 8, 12), mover.Tint);
        Assert.Equal(0, Vm.Scheduler.TweenCount);
        mover.Destroy();
    }

    [Fact]
    public void APropertyTweenReadsItsStartWhenTheDelayEnds()
    {
        var mover = new Mover { Speed = 0 };
        Vm.SetGlobal("mover", mover);
        _f.Run("tween.property(mover, 'speed', 10, 1, { delay = 1 })");

        Step(10);
        mover.Speed = 5; // changed during the delay: the tween starts from here
        Step(50);
        Assert.Equal(5, mover.Speed); // the delay ends: the start value is written
        Step(30);
        Assert.Equal(7.5, mover.Speed, 3);
        mover.Destroy();
    }

    [Fact]
    public void PropertyTweensRejectWhatTheyCannotAnimate()
    {
        var mover = new Mover();
        Vm.SetGlobal("mover", mover);

        Assert.Contains("is not a property", _f.Fails("tween.property(mover, 'nope', 1, 1)").Message);
        Assert.Contains("cannot be set", _f.Fails("tween.property(mover, 'locked', 1, 1)").Message);
        Assert.Contains("only numbers, vectors and Quats", _f.Fails("tween.property(mover, 'label', 1, 1)").Message);
        Assert.Contains("number expected", _f.Fails("tween.property(mover, 'speed', Vector3(1, 2, 3), 1)").Message);
        Assert.Contains("vector expected", _f.Fails("tween.property(mover, 'tint', 1, 1)").Message);
        Assert.Equal(0, Vm.Scheduler.TweenCount);
        mover.Destroy();
    }

    [Fact]
    public void ADestroyedObjectCancelsItsPropertyTween()
    {
        var mover = new Mover();
        Vm.SetGlobal("mover", mover);
        _f.Run("""
            local t = tween.property(mover, 'speed', 10, 1)
            t.cancelled:connect(function() print('cancelled') end)
            t.completed:connect(function() print('completed') end)
            """);

        Step(5);
        mover.Destroy();
        Step();

        Assert.Equal(["cancelled"], Printed);
        Assert.Equal(0, Vm.Scheduler.TweenCount);
    }

    [Fact]
    public void AFieldTweenWritesTheComponentWithoutAScriptCall()
    {
        Apogee.Ecs.World world = WithWorld();
        Entity entity = EntityOf(_f.Run("""
            local Transform = world:component('Transform')
            local e = world:create_entity()
            world:set(e, Transform, { Position = Vector3(0, 0, 0), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
            tween.field(world, e, Transform, 'Position', Vector3(10, 0, 0), 1)
            return e
            """)[0]);

        // The tween is not a Luau function: nothing in Luau runs while it plays.
        Step(30);
        Assert.True(world.TryGet(entity, out Transform local));
        Assert.Equal(5, local.Position.X, 3);
        world.Progress(0); // the transform system sees the write: the world transform follows the local one
        Assert.True(world.TryGet(entity, out WorldTransform global));
        Assert.Equal(5, global.Position.X, 3);

        Step(30);
        Assert.True(world.TryGet(entity, out local));
        Assert.Equal(10, local.Position.X);
        world.Progress(0);
        Assert.True(world.TryGet(entity, out global));
        Assert.Equal(10, global.Position.X);
        Assert.Equal(0, Vm.Scheduler.TweenCount);
    }

    [Fact]
    public void ADoubleFieldIsInterpolatedInDoublePrecision()
    {
        Apogee.Ecs.World world = WithWorld();
        Entity entity = EntityOf(_f.Run("""
            local Transform = world:component('Transform')
            local e = world:create_entity()
            world:set(e, Transform, { Position = Vector3(0, 0, 0), Rotation = Quat.identity, Scale = Vector3(1, 1, 1) })
            return e
            """)[0]);
        Entity transform = world.FindComponent("Transform");

        // 10 billion plus a half: a float cannot hold the half, a double can.
        world.Set(entity, new Transform(new Double3(1e10 + 0.5, 0, 0), Quaternion.Identity, Vector3.One));
        _f.Run("tween.field(world, " + entity.Id + ", " + transform.Id + ", 'Position', Vector3(1e10, 0, 0), 1)");
        Step(30);

        Assert.True(world.TryGet(entity, out Transform local));
        Assert.Equal(1e10 + 0.25, local.Position.X, 3);
    }

    [Fact]
    public void AnIntegerFieldIsRoundedAndEveryWriteTellsTheWorld()
    {
        Apogee.Ecs.World world = WithWorld();
        int sets = 0;
        _ = world.AddObserver<Health>("watch", ObserverEvent.OnSet, (Entity changed, ref Health health) => sets++);
        Entity entity = EntityOf(_f.Run("""
            local Health = world:component('Health')
            local e = world:create_entity()
            world:set(e, Health, { Current = 0, Max = 100 })
            tween.field(world, e, Health, 'Current', 60, 1)
            return e
            """)[0]);
        int before = sets;

        Step(30);
        Assert.True(world.TryGet(entity, out Health health));
        Assert.Equal(30, health.Current);
        Assert.Equal(100, health.Max);
        Assert.Equal(30, sets - before); // Modified once per step, so observers see each write

        Step(30);
        Assert.True(world.TryGet(entity, out health));
        Assert.Equal(60, health.Current);
        Assert.Equal(60, sets - before);
    }

    [Fact]
    public void FieldTweensRejectWhatTheyCannotAnimate()
    {
        WithWorld();
        _f.Run("""
            world:component('Health')
            world:component('Flag', { on = 'bool' })
            """);

        Assert.Contains("is not a field", _f.Fails("local H = world:component('Health') tween.field(world, world:create_entity(), H, 'Nope', 5, 1)").Message);
        Assert.Contains("only numbers", _f.Fails("local F = world:component('Flag') local e = world:create_entity() world:set(e, F, { on = true }) tween.field(world, e, F, 'on', 5, 1)").Message);
        Assert.Contains("is not alive", _f.Fails("local e = world:create_entity() local H = world:component('Health') world:destroy(e) tween.field(world, e, H, 'Current', 5, 1)").Message);
        Assert.Contains("has no", _f.Fails("tween.field(world, world:create_entity(), world:component('Health'), 'Current', 5, 1)").Message);
        Assert.Contains("number expected", _f.Fails("local e = world:create_entity() local H = world:component('Health') world:set(e, H, {}) tween.field(world, e, H, 'Current', Vector3(1, 2, 3), 1)").Message);
        Assert.Contains("no fields", _f.Fails("tween.field(world, world:create_entity(), world:create_entity(), 'x', 1, 1)").Message);
        Assert.Equal(0, Vm.Scheduler.TweenCount);
    }

    [Fact]
    public void ADeadEntityOrARemovedComponentCancelsTheTween()
    {
        WithWorld();
        _f.Run("""
            local Health = world:component('Health')
            local a = world:create_entity()
            local b = world:create_entity()
            world:set(a, Health, { Current = 0, Max = 10 })
            world:set(b, Health, { Current = 0, Max = 10 })
            local ta = tween.field(world, a, Health, 'Current', 10, 1)
            local tb = tween.field(world, b, Health, 'Current', 10, 1)
            ta.cancelled:connect(function() print('a cancelled') end)
            tb.cancelled:connect(function() print('b cancelled') end)
            task.spawn(function()
                task.wait(0.25)
                world:destroy(a)
                world:remove(b, Health)
            end)
            """);

        Step(30);

        Assert.Equal(["a cancelled", "b cancelled"], Printed);
        Assert.Equal(0, Vm.Scheduler.TweenCount);
        Assert.Empty(ErrorsOf("Script:base")); // an entity that is gone is not an error
    }

    // ---- Signals, cancel, complete -------------------------------------------------------------------------------------------------------

    [Fact]
    public void CompleteAndCancelEmitTheirSignalsOnce()
    {
        _f.Run("""
            local a = tween.value(0, 10, 1, function(v) print('a', string.format('%.2f', v)) end)
            a.completed:connect(function() print('a completed') end)
            a.cancelled:connect(function() print('a cancelled') end)
            local b = tween.value(0, 10, 1, function(v) print('b', string.format('%.2f', v)) end)
            b.completed:connect(function() print('b completed') end)
            b.cancelled:connect(function() print('b cancelled') end)
            task.spawn(function()
                task.wait(0.5)
                a:complete()
                a:complete()
                a:cancel()
                b:cancel()
                b:cancel()
                b:complete()
                print(a.playing, b.playing)
            end)
            """);

        Step(30);
        string[] after = Printed.Where(p => p.Contains("completed", StringComparison.Ordinal) || p.Contains("cancelled", StringComparison.Ordinal) || p.StartsWith("a\t10", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["a\t10.00", "a completed", "b cancelled"], after);
        Assert.Equal("false\tfalse", Printed[^1]);

        int count = Printed.Length;
        Step(60);
        Assert.Equal(count, Printed.Length); // neither writes any more: cancel keeps the value, complete wrote the end
    }

    [Fact]
    public void ASetterErrorCancelsAndIsLoggedForItsOwner()
    {
        ScriptOwner mod = Vm.CreateOwner("tweakmod", isMod: true);
        _f.Run("""
            local t = tween.value(0, 1, 1, function(v) error('boom') end)
            t.cancelled:connect(function() print('cancelled') end)
            t.completed:connect(function() print('completed') end)
            """, mod);

        Step(2);

        Assert.Equal(["cancelled"], Printed);
        string[] errors = ErrorsOf("Script:tweakmod");
        Assert.Single(errors);
        Assert.Contains("tween failed", errors[0], StringComparison.Ordinal);
        Assert.Contains("boom", errors[0], StringComparison.Ordinal);
        Assert.Equal(0, Vm.Scheduler.TweenCount);
    }

    [Fact]
    public void APropertySetterThatRefusesAValueCancelsTheTween()
    {
        var mover = new Mover();
        Vm.SetGlobal("mover", mover);
        _f.Run("""
            local t = tween.property(mover, 'limited', 10, 1)
            t.cancelled:connect(function() print('cancelled') end)
            """);

        Step(60);

        Assert.Equal(["cancelled"], Printed);
        Assert.Equal(5, mover.Limited);
        Assert.Contains(ErrorsOf("Script:base"), e => e.Contains("too big", StringComparison.Ordinal));
        mover.Destroy();
    }

    // ---- Lifetime ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADroppedHandleKeepsPlayingAndIsCollectedAfter()
    {
        // The script drops the handle at once; C# holds the object (not the script) so the test can look at it.
        Tween tween = _f.Run("return tween.value(0, 1, 1, function(v) print(string.format('%.2f', v)) end)")[0].ToObject<Tween>();
        Vm.CollectGarbage();
        Vm.ProcessPending();
        Assert.False(tween.IsDestroyed);
        Assert.True(tween.Playing);

        Step(30);
        Vm.CollectGarbage();
        Vm.ProcessPending();
        Assert.False(tween.IsDestroyed);
        Assert.Equal("0.50", Printed[^1]);

        Step(30);
        Assert.False(tween.Playing);
        Vm.CollectGarbage();
        Vm.ProcessPending();

        Assert.True(tween.IsDestroyed); // nothing holds the userdata any more
        Assert.False(EngineObject.TryGet(tween.Handle, out _));
    }

    [Fact]
    public void AHeldHandleOfAFinishedTweenStaysReadable()
    {
        _f.Run("""
            local t = tween.value(0, 1, 0.5, function() end)
            task.spawn(function()
                task.wait(1)
                print(t.playing, t.progress, t:wait())
            end)
            """);

        Step(60);

        Assert.Equal(["false\t1\ttrue"], Printed); // a wait on a tween that has ended answers at once
    }

    [Fact]
    public void WaitYieldsUntilTheTweenEnds()
    {
        _f.Run("""
            task.spawn(function()
                local t = tween.value(0, 1, 0.5, function() end)
                print('completed', t:wait())
                local u = tween.value(0, 1, 10, function() end)
                task.delay(0.1, function() u:cancel() end)
                print('cancelled', u:wait())
                local ok, result = pcall(function() return tween.value(0, 1, 0.1, function() end):wait() end)
                print('in a pcall', ok, result)
            end)
            """);

        Step(29);
        Assert.Empty(Printed);
        Step();
        Assert.Equal(["completed\ttrue"], Printed); // woken in the drain of the step that ended the tween
        Step(6);
        Assert.Equal(["completed\ttrue", "cancelled\tfalse"], Printed);
        Step(6);
        Assert.Equal("in a pcall\ttrue\ttrue", Printed[^1]);
        Assert.Equal(0, Vm.Scheduler.Count);
    }

    [Fact]
    public void WaitOutsideATaskIsAClearError()
    {
        Assert.Contains("must run in a task", _f.Fails("tween.value(0, 1, 1, function() end):wait()").Message);
        Assert.Equal(1, Vm.Scheduler.TweenCount); // the tween itself was made
    }

    [Fact]
    public void ACancelledTaskDoesNotWakeFromAWaitingTween()
    {
        _f.Run("""
            local t = tween.value(0, 1, 0.5, function() end)
            local waiter = task.spawn(function()
                t:wait()
                print('woke')
            end)
            task.delay(0.1, function() task.cancel(waiter) end)
            """);

        Step(60);

        Assert.Empty(Printed);
        Assert.Equal(0, Vm.Scheduler.Count);
    }

    [Fact]
    public void ReloadingTheModuleCancelsItsTweens()
    {
        using var folder = new ScriptFolder(_f);
        folder.Write("lib.luau", """
            local t = tween.value(0, 1, 10, function() print('old') end)
            t.cancelled:connect(function() print('old cancelled') end)
            task.spawn(function()
                task.wait(0.1)
                tween.value(0, 1, 10, function() print('child') end)
                print('woke', t:wait())
            end)
            return {}
            """);
        folder.Write("main.luau", "require('./lib')");
        folder.RunMain();
        Step(10);
        Assert.Equal(2, Vm.Scheduler.TweenCount); // the module's and the one its task made
        Assert.Contains("old", Printed);

        folder.Write("lib.luau", "tween.value(0, 1, 10, function() print('new') end) return {}");
        ModuleReload result = Vm.ReloadModule(folder.Root + "/lib.luau");

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, Vm.Scheduler.TweenCount); // only the new run's
        int before = Printed.Length;
        Step(30);
        Assert.All(Printed.Skip(before), p => Assert.Equal("new", p)); // the old ones and the task waiting on them are gone, and no signal went to their handlers
        Assert.NotEmpty(Printed.Skip(before));
        Assert.Equal(1, Vm.Scheduler.TweenCount);
    }

    [Fact]
    public void AFailedReloadKeepsTheOldTweens()
    {
        using var folder = new ScriptFolder(_f);
        folder.Write("lib.luau", "tween.value(0, 1, 10, function() end) return {}");
        folder.Write("main.luau", "require('./lib')");
        folder.RunMain();

        folder.Write("lib.luau", "tween.value(0, 1, 10, function() end) error('boom') return {}");
        ModuleReload result = Vm.ReloadModule(folder.Root + "/lib.luau");

        Assert.False(result.Success);
        Assert.Equal(1, Vm.Scheduler.TweenCount); // the new run's tween is gone, the old one stays
    }

    [Fact]
    public void AModuleThatFailsToLoadLeavesNoTweens()
    {
        using var folder = new ScriptFolder(_f);
        folder.Write("lib.luau", "tween.value(0, 1, 10, function() end) error('boom')");
        folder.Write("main.luau", "require('./lib')");

        Assert.Throws<ScriptException>(() => folder.RunMain());

        Assert.Equal(0, Vm.Scheduler.TweenCount);
    }

    [Fact]
    public void CancelOwnerStopsOnlyThatOwnersTweens()
    {
        ScriptOwner a = Vm.CreateOwner("owner_a", isMod: true);
        ScriptOwner b = Vm.CreateOwner("owner_b", isMod: true);
        Tween mine = _f.Run("""
            local t = tween.value(0, 1, 10, function() end)
            t.cancelled:connect(function() print('cancelled') end)
            return t
            """, a)[0].ToObject<Tween>();
        _f.Run("tween.value(0, 1, 10, function() end)", b);
        Vm.SetGlobal("shared", mine);
        _f.Run("task.spawn(function() print('waiter', shared:wait()) end)"); // a task of the base game waits for a tween of a mod
        Assert.Equal(1, Vm.Scheduler.TweenCountOf(a));

        Vm.Scheduler.CancelOwner(a);
        Step();

        Assert.Equal(0, Vm.Scheduler.TweenCountOf(a));
        Assert.Equal(1, Vm.Scheduler.TweenCountOf(b));
        Assert.False(mine.Playing);
        Assert.Equal(["waiter\tfalse"], Printed); // the waiter is woken with false; a dropped tween emits no signal
    }

    [Fact]
    public void DisposingTheVmWithTweensPlayingIsClean()
    {
        var f = new VmFixture();
        f.Run("""
            for i = 1, 10 do
                tween.value(0, 1, 10, function() end)
            end
            task.spawn(function() tween.value(0, 1, 10, function() end):wait() end)
            """);
        Assert.Equal(11, f.Vm.Scheduler.TweenCount);

        f.Dispose();

        Assert.Equal(0, f.Vm.Scheduler.TweenCount);
    }
}
