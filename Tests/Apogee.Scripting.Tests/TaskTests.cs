using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Core.Logging;
using Xunit;

namespace Apogee.Scripting.Tests;

/// <summary>The <c>task</c> library on a bare VM: the test is the clock, through <see cref="LuauVm.AdvanceTime"/>.</summary>
[Collection("Global log")]
public sealed class TaskTests : IDisposable
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

    public TaskTests()
    {
        Log.AddSink(_log);
        _f = new VmFixture();
    }

    public void Dispose()
    {
        _f.Dispose();
        Log.RemoveSink(_log);
    }

    private LuauVm Vm => _f.Vm;

    private string[] Printed => [.. _f.Output.Select(o => o.Text)];

    private void Step(int frames = 1)
    {
        for (int i = 0; i < frames; i++)
        {
            Vm.AdvanceTime(Frame, Frame);
        }
    }

    private string[] ErrorsOf(string category)
    {
        lock (_log.Entries)
        {
            return [.. _log.Entries.Where(e => e.Level == LogLevel.Error && e.Category == category).Select(e => e.Message)];
        }
    }

    [Fact]
    public void SpawnRunsAtOnceUntilTheFirstWait()
    {
        _f.Run("""
            local t = task.spawn(function(a, b)
                print('start', a, b)
                task.wait()
                print('after')
            end, 1, 2)
            print('main', type(t))
            """);

        Assert.Equal(["start\t1\t2", "main\tthread"], Printed);
        Step();
        Assert.Equal(["start\t1\t2", "main\tthread", "after"], Printed);
        Assert.Equal(0, Vm.Scheduler.Count);
    }

    [Fact]
    public void WaitResumesAfterItsGameSecondsAndReturnsTheElapsedTime()
    {
        _f.Run("task.spawn(function() local t = task.wait(0.5) print(string.format('%.4f', t)) end)");

        Step(29);
        Assert.Empty(Printed);
        Step();
        Assert.Equal(["0.5000"], Printed); // 30 frames of 1/60 meet half a second exactly
    }

    [Fact]
    public void WaitZeroResumesNextFrameNeverTheSameFrame()
    {
        _f.Run("""
            local n = 0
            task.spawn(function()
                while n < 3 do
                    n += 1
                    print(n)
                    task.wait(0)
                end
            end)
            """);

        Assert.Equal(["1"], Printed);
        Step();
        Assert.Equal(["1", "2"], Printed);
        Step();
        Assert.Equal(["1", "2", "3"], Printed);
        Step();
        Assert.Equal(["1", "2", "3"], Printed);
    }

    [Fact]
    public void TasksDueTogetherResumeInTheOrderTheyWereScheduled()
    {
        _f.Run("""
            for _, name in { 'a', 'b', 'c' } do
                task.spawn(function() task.wait(1) print(name) end)
            end
            task.spawn(function() task.wait(0.5) print('early') end)
            """);

        Vm.AdvanceTime(1, 1); // everything is due in one step: by wake time, then in scheduling order
        Assert.Equal(["early", "a", "b", "c"], Printed);
    }

    [Fact]
    public void DeferRunsAtTheNextDrainAndADeferChainDoesNotHangAFrame()
    {
        _f.Run("""
            task.defer(function() print('deferred') end)
            print('main')
            """);
        Assert.Equal(["main"], Printed);
        Vm.RunDeferred();
        Assert.Equal(["main", "deferred"], Printed);

        _f.Output.Clear();
        _f.Run("""
            local function chain(n)
                task.defer(function() print(n) if n < 2 then chain(n + 1) end end)
            end
            chain(1)
            """);
        Vm.RunDeferred(); // one drain runs what was queued before it began
        Assert.Equal(["1"], Printed);
        Step();
        Assert.Equal(["1", "2"], Printed); // and the end of AdvanceTime is a drain too

        _f.Output.Clear();
        _f.Run("task.spawn(function() task.wait() task.defer(function() print('deferred') end) print('task') end)");
        Step();
        Assert.Equal(["task", "deferred"], Printed); // a task's defer runs in the same step's drain
    }

    [Fact]
    public void DelayAndEveryFollowTheGameClockAndEverySkipsMissedPeriods()
    {
        _f.Run("""
            task.delay(0.25, function(a) print('delay', a) end, 'x')
            local n = 0
            task.every(0.5, function(a) n += 1 print('every', n, a) end, 'y')
            """);

        Step(14);
        Assert.Empty(Printed);
        Step();
        Assert.Equal(["delay\tx"], Printed); // 15 frames: 0.25 s
        Step(15);
        Assert.Equal(["delay\tx", "every\t1\ty"], Printed);
        Step(30);
        Assert.Equal("every\t2\ty", Printed[^1]);

        // One long frame covers several periods: the call is made once, and the next is the first multiple above now.
        _f.Output.Clear();
        Vm.AdvanceTime(2, 2); // from 1.0 to 3.0: periods 1.5, 2, 2.5 and 3 are all due
        Assert.Equal(["every\t3\ty"], Printed);
        Vm.AdvanceTime(0.4, 0.4); // 3.4
        Assert.Single(Printed);
        Vm.AdvanceTime(0.1, 0.1); // 3.5
        Assert.Equal(2, Printed.Length);
    }

    [Fact]
    public void AnErrorInAnEveryCallEndsItAndIsLoggedOnce()
    {
        ScriptOwner owner = Vm.CreateOwner("everyerror", isMod: false);
        _f.Run("task.every(0.1, function() error('again') end)", owner);

        Step(30);

        Assert.Single(ErrorsOf("Script:everyerror"));
        Assert.Equal(0, Vm.Scheduler.Count);
    }

    [Fact]
    public void AnEveryCallMayWaitAndTheNextPeriodStartsAfterIt()
    {
        _f.Run("task.every(1, function() print('start') task.wait(0.25) print('end') end)");

        Step(60);
        Assert.Equal(["start"], Printed);
        Step(15);
        Assert.Equal(["start", "end"], Printed);
        Step(45);
        Assert.Equal(["start", "end", "start"], Printed);
    }

    [Fact]
    public void CancelStopsAWaitingTaskAndIsANoOpOnADeadOne()
    {
        _f.Run("""
            local waiting = task.spawn(function() task.wait(1) print('never') end)
            local deferred = task.defer(function() print('never') end)
            local timer = task.delay(1, function() print('never') end)
            task.cancel(waiting)
            task.cancel(deferred)
            task.cancel(timer)
            local finished = task.spawn(function() end)
            task.cancel(finished)
            task.cancel(finished)
            task.cancel(coroutine.create(function() end))
            """);

        Assert.Equal(0, Vm.Scheduler.Count);
        Step(120);
        Assert.Empty(Printed);
    }

    [Fact]
    public void ATaskCannotCancelItself()
    {
        _f.Run("""
            task.spawn(function()
                local ok, message = pcall(task.cancel, coroutine.running())
                print(ok, message)
            end)
            """);

        Assert.Equal(["false\ta task cannot cancel itself; return from it instead"], Printed);
    }

    [Fact]
    public void WaitOutsideATaskIsAClearError()
    {
        const string Message = "task.wait must run in a task: start the function with task.spawn, task.defer or task.delay";

        // A chunk run by the engine.
        Assert.Contains(Message, _f.Fails("task.wait(1)").Message);

        // A module's top level.
        using var folder = new ScriptFolder(_f);
        folder.Write("lib.luau", "task.wait(1) return {}");
        folder.Write("main.luau", "require('./lib')");
        Assert.Contains(Message, Assert.Throws<ScriptException>(() => folder.RunMain()).Message);

        // A callback of the engine.
        var animal = new Animal();
        Vm.SetGlobal("pet", animal);
        _f.Run("pet.died:connect(function() print(select(2, pcall(task.wait, 1))) end)");
        animal.Died.Emit();
        Assert.Equal(Message, Printed[^1]);

        // A coroutine a script made itself.
        Assert.Contains(Message, _f.Fails("coroutine.wrap(function() task.wait(1) end)()").Message);
    }

    [Fact]
    public void WaitInsideAPcallWorksAndInsideAMetamethodFailsClearly()
    {
        _f.Run("""
            task.spawn(function()
                local ok = pcall(function() task.wait(0.1) end)
                print('pcall', ok)
                local t = setmetatable({}, { __index = function() task.wait() return 1 end })
                local ok2, message = pcall(function() return t.x end)
                print('meta', ok2, string.find(message, 'cannot yield') ~= nil)
            end)
            """);

        Step(6);
        Assert.Equal(["pcall\ttrue", "meta\tfalse\ttrue"], Printed);
    }

    [Fact]
    public void ASuspendedCoroutineCanBeAdoptedAndADeadOneCannot()
    {
        _f.Run("""
            local co = coroutine.create(function(a)
                print('first', a)
                local waited = task.wait(0.1)
                print('second', waited > 0)
            end)
            task.spawn(co, 'arg')
            local dead = coroutine.create(function() end)
            coroutine.resume(dead)
            print(pcall(task.spawn, dead))
            print(select(2, pcall(task.spawn, 5)))
            """);

        Step(6);
        Assert.Equal("first\targ", Printed[0]);
        Assert.StartsWith("false\t", Printed[1]);
        Assert.Contains("not suspended", Printed[1]);
        Assert.Contains("function or a thread expected", Printed[2]);
        Assert.Equal("second\ttrue", Printed[3]);
    }

    [Fact]
    public void AFailingTaskIsLoggedForItsOwnerAndRaisesTaskFailed()
    {
        ScriptOwner mod = Vm.CreateOwner("failmod", isMod: true);
        var failures = new List<(ScriptOwner Owner, string Message)>();
        Vm.Scheduler.TaskFailed += (owner, message) => failures.Add((owner, message));

        _f.Run("task.spawn(function() task.wait() error('boom') end) print('spawned')", mod);
        Step();

        Assert.Equal(["spawned"], Printed); // the caller of spawn never sees the error
        Assert.Single(failures);
        Assert.Same(mod, failures[0].Owner);
        Assert.Contains("boom", failures[0].Message);
        string logged = Assert.Single(ErrorsOf("Script:failmod"));
        Assert.Contains("task failed", logged);
        Assert.Contains("boom", logged);
        Assert.Contains("\ntest:1", logged); // the traceback follows the message
        Assert.Equal(0, Vm.Scheduler.Count);
    }

    [Fact]
    public void ATaskOverItsBudgetTimesOutAloneAndOthersRun()
    {
        ScriptOwner slow = Vm.CreateOwner("slowmod", isMod: true, timeBudgetMilliseconds: 50);
        var failures = new List<string>();
        Vm.Scheduler.TaskFailed += (_, message) => failures.Add(message);

        _f.Run("task.spawn(function() task.wait() while true do end end)", slow);
        _f.Run("task.spawn(function() task.wait() print('other') end)");
        Step();

        Assert.Equal(["other"], Printed);
        Assert.Single(failures);
        Assert.Contains("exceeded its time budget", failures[0]);
        Assert.Contains("timed out", Assert.Single(ErrorsOf("Script:slowmod")));
    }

    [Fact]
    public void AModsTasksCountInItsMemoryCategory()
    {
        ScriptOwner mod = Vm.CreateOwner("memmod", isMod: true);
        long before = mod.MemoryUsed;

        _f.Run("task.spawn(function() local big = table.create(20000, 0) task.wait(10) print(#big) end)", mod);
        long during = mod.MemoryUsed;

        Assert.True(during - before > 100_000, $"the task's table counts for the mod: {before} -> {during}");

        Vm.Scheduler.CancelOwner(mod);
        Vm.CollectGarbage();
        Assert.True(mod.MemoryUsed < during - 100_000, "a cancelled task gives its memory back");
    }

    [Fact]
    public void ATaskThatRunsOutOfItsOwnersMemoryEndsAndIsReported()
    {
        ScriptOwner mod = Vm.CreateOwner("hogmod", isMod: true, memoryLimit: 1_000_000);
        var failures = new List<string>();
        Vm.Scheduler.TaskFailed += (_, message) => failures.Add(message);

        _f.Run("task.spawn(function() task.wait() local keep = {} for i = 1, 100 do keep[i] = table.create(50000, 0) end end)", mod);
        _f.Run("task.spawn(function() task.wait() print('other') end)");
        Step();

        Assert.Single(failures);
        Assert.Equal(["other"], Printed);
        Assert.Contains("ran out of memory", Assert.Single(ErrorsOf("Script:hogmod")));
    }

    [Fact]
    public void WaitRealIgnoresTheGameClock()
    {
        _f.Run("""
            task.spawn(function() task.wait(1) print('game') end)
            task.spawn(function() task.wait_real(1) print('real') end)
            """);

        Vm.AdvanceTime(0, 0.5);
        Assert.Empty(Printed);
        Vm.AdvanceTime(0, 0.5); // a second of real time, none of game time
        Assert.Equal(["real"], Printed);
        Vm.AdvanceTime(1, 0);
        Assert.Equal(["real", "game"], Printed);
        Assert.Equal(1.0, Vm.Scheduler.Time, 9);
        Assert.Equal(1.0, Vm.Scheduler.RealTime, 9);
        Assert.Equal(1.0, _f.Run("return task.time()")[0].ToDouble(), 9);
        Assert.Equal(1.0, _f.Run("return task.real_time()")[0].ToDouble(), 9);
    }

    [Fact]
    public void BadArgumentsAreScriptErrors()
    {
        Assert.Contains("number expected", _f.Fails("task.delay('x', function() end)").Message);
        Assert.Contains("finite", _f.Fails("task.delay(0/0, function() end)").Message);
        Assert.Contains("greater than 0", _f.Fails("task.every(0, function() end)").Message);
        Assert.Contains("function or a thread", _f.Fails("task.spawn(5)").Message);
        Assert.Contains("thread expected", _f.Fails("task.cancel(5)").Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm.AdvanceTime(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm.AdvanceTime(double.NaN, 0));
    }

    [Fact]
    public void ReloadingAModuleCancelsTheTasksItStartedAndItsChildren()
    {
        using var folder = new ScriptFolder(_f);
        folder.Write("lib.luau", """
            task.every(1, function() print('tick') end)
            task.spawn(function()
                task.wait(0.5)
                task.spawn(function() while true do task.wait(1) print('child') end end)
            end)
            return {}
            """);
        folder.Write("main.luau", "require('./lib')");
        folder.RunMain();
        Step(30);
        Assert.Equal(2, Vm.Scheduler.Count); // the every and the child; the task that made the child is over

        folder.Write("lib.luau", "task.every(1, function() print('tock') end) return {}");
        ModuleReload result = Vm.ReloadModule(folder.Root + "/lib.luau");

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, Vm.Scheduler.Count);
        Step(180);
        Assert.Equal(["tock", "tock", "tock"], Printed.Where(p => p != "tick" && p != "child").ToArray());
        Assert.DoesNotContain("tick", Printed);
        Assert.DoesNotContain("child", Printed);
    }

    [Fact]
    public void AFailedReloadKeepsTheOldTasks()
    {
        using var folder = new ScriptFolder(_f);
        folder.Write("lib.luau", "task.every(1, function() print('tick') end) return {}");
        folder.Write("main.luau", "require('./lib')");
        folder.RunMain();

        folder.Write("lib.luau", "task.every(1, function() print('tock') end) error('boom') return {}");
        ModuleReload result = Vm.ReloadModule(folder.Root + "/lib.luau");

        Assert.False(result.Success);
        Assert.Equal(1, Vm.Scheduler.Count); // the new run's task is gone, the old one stays
        Step(60);
        Assert.Equal(["tick"], Printed);
    }

    [Fact]
    public void AModuleThatFailsToLoadLeavesNoTasks()
    {
        using var folder = new ScriptFolder(_f);
        folder.Write("lib.luau", "task.every(1, function() print('tick') end) error('boom')");
        folder.Write("main.luau", "require('./lib')");

        Assert.Throws<ScriptException>(() => folder.RunMain());

        Assert.Equal(0, Vm.Scheduler.Count);
    }

    [Fact]
    public void CancelOwnerStopsOnlyThatOwnersTasks()
    {
        ScriptOwner a = Vm.CreateOwner("owner_a", isMod: true);
        ScriptOwner b = Vm.CreateOwner("owner_b", isMod: true);
        _f.Run("task.spawn(function() task.wait(1) print('a') end) task.every(1, function() print('a every') end)", a);
        _f.Run("task.spawn(function() task.wait(1) print('b') end)", b);
        Assert.Equal(2, Vm.Scheduler.CountOf(a));
        Assert.Equal(1, Vm.Scheduler.CountOf(b));

        Vm.Scheduler.CancelOwner(a);

        Assert.Equal(0, Vm.Scheduler.CountOf(a));
        Assert.Equal(1, Vm.Scheduler.CountOf(b));
        Step(60);
        Assert.Equal(["b"], Printed);
    }

    [Fact]
    public void DisposingTheVmWithWaitingTasksIsClean()
    {
        var f = new VmFixture();
        f.Run("""
            task.spawn(function() task.wait(10) end)
            task.every(1, function() end)
            task.defer(function() end)
            task.delay(5, function() end)
            local co = coroutine.create(function() task.wait(1) end)
            task.spawn(co)
            """);
        Assert.Equal(5, f.Vm.Scheduler.Count);

        f.Dispose();

        Assert.Equal(0, f.Vm.Scheduler.Count);
        Assert.Throws<ObjectDisposedException>(() => f.Vm.AdvanceTime(1, 1));
    }

    [Fact]
    public void ACoroutineYieldInATaskEndsItWithAWarning()
    {
        _f.Run("task.spawn(function() coroutine.yield() print('never') end)");

        Step(2);

        Assert.Empty(Printed);
        Assert.Equal(0, Vm.Scheduler.Count);
        lock (_log.Entries)
        {
            Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("coroutine.yield", StringComparison.Ordinal));
        }
    }
}
