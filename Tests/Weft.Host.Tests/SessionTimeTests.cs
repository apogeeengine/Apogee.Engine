using System;
using System.IO;
using Weft.Native.Tests;
using Weft.Reflection;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;
using Lua = Weft.Native.Luau.Lua;

namespace Weft.Host.Tests;

[Component]
public struct Counter
{
    public int Value;
}

[Component]
public struct Seen
{
    public int Value;
}

/// <summary>Game time in a session: the time scale, and the tasks that follow it.</summary>
public sealed class SessionTimeTests : IDisposable
{
    private const float Frame = 1f / 60;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "weft-session-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');

    public SessionTimeTests()
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a test failure.
        }
    }

    private ScriptSession Start(string source)
    {
        string path = Path.Combine(_root, "main.luau");
        File.WriteAllText(path, source);
        return ScriptSession.Start(path.Replace('\\', '/'));
    }

    private static void Frames(ScriptSession session, int count)
    {
        for (int i = 0; i < count; i++)
        {
            session.Progress(Frame);
            session.EndFrame();
        }
    }

    [Fact]
    public void TimeScaleZeroFreezesGameWaitsButNotRealWaits()
    {
        using ScriptSession session = Start("""
            game.set_time_scale(0)
            task.spawn(function() task.wait(0.1) end)
            task.spawn(function() task.wait_real(0.1) end)
            """);
        Assert.Equal(2, session.Tasks.Count);

        Frames(session, 10);
        Assert.Equal(1, session.Tasks.Count); // the real wait is over, the game wait is still frozen
        Assert.Equal(0, session.Tasks.Time);
        Assert.Equal(10 * Frame, session.Tasks.RealTime, 4);

        GameLibrary.SetTimeScale(1);
        Frames(session, 6);
        Assert.Equal(0, session.Tasks.Count);
    }

    [Fact]
    public void ASlowTimeScaleSlowsTheGameClockAndNotTheRealOne()
    {
        using ScriptSession session = Start("game.set_time_scale(0.5)");

        Frames(session, 60);

        Assert.Equal(0.5, session.Tasks.Time, 4);
        Assert.Equal(1.0, session.Tasks.RealTime, 4);
    }

    [Fact]
    public void ProgressResumesTasksAfterTheWorldsSystems()
    {
        using ScriptSession session = Start("""
            local Counter = world:component('Counter')
            local Seen = world:component('Seen')
            local e = world:create_entity('counter')
            world:set(e, Counter, { Value = 0 })
            world:set(e, Seen, { Value = -1 })
            world:system('Inc', { Counter }, function(_, counter) counter.Value += 1 end)
            task.spawn(function()
                while true do
                    task.wait()
                    world:set(e, Seen, { Value = world:get(e, Counter).Value })
                end
            end)
            """);
        Entity e = session.World.Lookup("counter");

        Frames(session, 3);

        // The task saw the value the system wrote in the same frame, not the one before.
        Assert.Equal(3, session.World.Get<Counter>(e).Value);
        Assert.Equal(3, session.World.Get<Seen>(e).Value);
    }

    [Fact]
    public void EndFrameRunsWhatWasDeferredAfterTheStep()
    {
        using ScriptSession session = Start("""
            local Seen = world:component('Seen')
            local e = world:create_entity('flag')
            world:set(e, Seen, { Value = 0 })
            task.defer(function() world:set(e, Seen, { Value = 1 }) end)
            """);
        Entity flag = session.World.Lookup("flag");

        // A task deferred outside the step (by a handler, a GUI function) starts when the frame ends.
        Assert.Equal(0, session.World.Get<Seen>(flag).Value);
        session.EndFrame();
        Assert.Equal(1, session.World.Get<Seen>(flag).Value);
    }

    [Fact]
    public void GameResetRestoresTimeScaleAndTheLibraryClampsIt()
    {
        GameLibrary.SetTimeScale(0.5);
        Assert.Equal(0.5, GameLibrary.TimeScale());
        GameLibrary.SetTimeScale(500);
        Assert.Equal(100, GameLibrary.TimeScale());
        GameLibrary.SetTimeScale(-3);
        Assert.Equal(0, GameLibrary.TimeScale());
        Assert.Throws<ValueException>(() => GameLibrary.SetTimeScale(double.NaN));

        using (ScriptSession session = Start("game.set_time_scale(0.25)")) // a session resets it first
        {
            Assert.Equal(0.25, GameLibrary.TimeScale());
        }

        using ScriptSession fresh = Start("assert(game.time_scale() == 1)");
        Assert.Equal(1, GameLibrary.TimeScale());
    }
}
