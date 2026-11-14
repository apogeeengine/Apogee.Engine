using Apogee.Native.Luau;

namespace Apogee.Scripting;

public sealed unsafe partial class LuauVm
{
    /// <summary>The tasks scripts started with the <c>task</c> library: sleeping, deferred and running.</summary>
    public ScriptScheduler Scheduler { get; }

    /// <summary>
    /// Moves the game clock by <paramref name="gameSeconds"/> and the real clock by <paramref name="realSeconds"/> (the game's time scale makes them differ), resumes the tasks that are due, and
    /// then runs the deferred ones. There is no clock inside the VM: this is how time passes for scripts, so a test calls it with a fake one.
    /// </summary>
    public void AdvanceTime(double gameSeconds, double realSeconds)
    {
        ThrowIfDisposed();
        Scheduler.Advance(gameSeconds, realSeconds);
    }

    /// <summary>Runs the tasks that were deferred (<c>task.defer</c>) since the last time they ran: what the end of a frame does for the ones that GUI functions, UI handlers and signals deferred after the frame's step.</summary>
    public void RunDeferred()
    {
        ThrowIfDisposed();
        Scheduler.Drain();
    }

    /// <summary>Registers the <c>task</c> library. It comes with its definitions.</summary>
    private static void RegisterTaskNatives()
    {
        ScriptNatives.RegisterLibrary("task", "spawn", &TaskSpawnBody, "(f: ((...any) -> ...any) | thread, ...any) -> thread",
            "Runs the function now, in a new task, until its first task.wait or its end; also takes a suspended coroutine. Returns the task's thread. An error in the task is logged, not raised here.");
        ScriptNatives.RegisterLibrary("task", "defer", &TaskDeferBody, "(f: ((...any) -> ...any) | thread, ...any) -> thread",
            "Like spawn, but the task starts at the next drain: after the world's systems and the other tasks of this frame, or at the end of the frame. A task deferred while a drain runs starts at the next one.");
        ScriptNatives.RegisterLibrary("task", "delay", &TaskDelayBody, "(seconds: number, f: ((...any) -> ...any) | thread, ...any) -> thread",
            "Like spawn, after that many seconds of game time (so it follows game.set_time_scale).");
        ScriptNatives.RegisterLibrary("task", "wait", &TaskWaitBody, "(seconds: number?) -> number",
            "Pauses the running task for that many seconds of game time and returns how many really passed. nil or 0 waits for the next frame. It must run in a task (task.spawn, task.defer, task.delay or task.every), not at a module's top level or in a callback.");
        ScriptNatives.RegisterLibrary("task", "wait_real", &TaskWaitRealBody, "(seconds: number?) -> number",
            "Like wait, on the real clock that game.set_time_scale does not change: a pause menu or a countdown while time is stopped.");
        ScriptNatives.RegisterLibrary("task", "every", &TaskEveryBody, "(seconds: number, f: (...any) -> ...any, ...any) -> thread",
            "Calls the function every that many seconds of game time, at most once per frame (the periods a long frame covers are skipped), until task.cancel or an error in it.");
        ScriptNatives.RegisterLibrary("task", "cancel", &TaskCancelBody, "(task: thread) -> ()",
            "Stops a task that is waiting or deferred, so that it never runs again. A thread that is over, or is not a task, is ignored. A task cannot cancel itself: return from it.");
        ScriptNatives.RegisterLibrary("task", "time", &TaskTimeBody, "() -> number", "The game seconds since the game started: the clock of wait, delay and every. It stops while the time scale is 0.");
        ScriptNatives.RegisterLibrary("task", "real_time", &TaskRealTimeBody, "() -> number", "The real seconds since the game started, whatever the time scale.");
    }

    private static int TaskSpawnBody(State* L) => VmOf(L).Scheduler.Spawn(L);

    private static int TaskDeferBody(State* L) => VmOf(L).Scheduler.Defer(L);

    private static int TaskDelayBody(State* L) => VmOf(L).Scheduler.Delay(L);

    private static int TaskEveryBody(State* L) => VmOf(L).Scheduler.Every(L);

    private static int TaskWaitBody(State* L) => VmOf(L).Scheduler.Wait(L, ScriptScheduler.Clock.Game);

    private static int TaskWaitRealBody(State* L) => VmOf(L).Scheduler.Wait(L, ScriptScheduler.Clock.Real);

    private static int TaskCancelBody(State* L)
    {
        VmOf(L).Scheduler.Cancel(L);
        return 0;
    }

    private static int TaskTimeBody(State* L)
    {
        Lua.Pushnumber(L, VmOf(L).Scheduler.Time);
        return 1;
    }

    private static int TaskRealTimeBody(State* L)
    {
        Lua.Pushnumber(L, VmOf(L).Scheduler.RealTime);
        return 1;
    }
}
