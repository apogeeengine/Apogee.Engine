using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.Luau;
using Apogee.Reflection;
using LuaType = Apogee.Native.Luau.Type;

namespace Apogee.Scripting;

/// <summary>
/// The tasks of one VM (and its tweens, which it steps and cancels along with them): what <c>task.spawn</c>, <c>task.defer</c>, <c>task.delay</c> and <c>task.every</c> start, and what <c>task.wait</c> puts to sleep. A task is a Luau thread the scheduler
/// resumes: at once (spawn), at the next drain (defer), when its time comes (delay, wait, every). There is no clock in here: <see cref="LuauVm.AdvanceTime"/> says how much game time and real time
/// passed, so a test drives it with a fake clock and the engine drives it with the frame. Every resume is a protected resume under the time budget of the task's owner, and the thread keeps the
/// memory category of the script that started it. Not thread safe: use it from the thread that made the VM.
/// </summary>
public sealed unsafe class ScriptScheduler
{
    /// <summary>A task is due when its wake time is at most this far in the future, so that 30 frames of 1/60 s meet a wait of half a second.</summary>
    private const double Epsilon = 1e-9;

    internal enum Phase
    {
        /// <summary>Asleep: in one of the heaps (a wait, a delay, the period of an <c>every</c>).</summary>
        Waiting,

        /// <summary>Asleep until something wakes it by hand (<c>Tween:wait</c>): in no queue.</summary>
        Parked,

        /// <summary>Queued for the next drain.</summary>
        Deferred,

        /// <summary>Being resumed now (it may be below another task that it resumed in turn).</summary>
        Running,

        /// <summary>Over: finished, failed or cancelled.</summary>
        Done,
    }

    internal enum Clock
    {
        Game,
        Real,
    }

    internal sealed class TaskEntry
    {
        public State* Thread;
        public int Reference;
        public ScriptOwner Owner = null!;
        public ReloadScope? Scope;
        public Phase Phase;

        /// <summary>Counts up whenever the entry is scheduled or cancelled: a heap item or a deferred item with an older token is stale.</summary>
        public int Token;

        /// <summary>True for a thread the scheduler made (it resets it when the task ends); false for a coroutine a script handed over.</summary>
        public bool Created;
        public bool CancelRequested;

        /// <summary>True until the first resume of the function (for <c>every</c>, of each call).</summary>
        public bool NeedsStart = true;
        public int PreparedArgs;

        public bool Every;
        public double Period;
        public double Origin;
        public int TemplateReference = Lua.Noref;
        public int TemplateCount;

        public Clock WaitClock;
        public double WaitStart;

        /// <summary>What a task that was parked is resumed with instead of the elapsed time: 1 for true, 2 for false, 0 for none.</summary>
        public int WakeResult;
    }

    /// <summary>A task that put itself to sleep until <see cref="Wake"/>: the entry and the token that was current when it parked, so that a task that was cancelled in between is not woken.</summary>
    internal readonly record struct ParkedTask(TaskEntry Entry, int Token);

    private readonly LuauVm _vm;
    private readonly Dictionary<nint, TaskEntry> _tasks = [];
    private readonly PriorityQueue<(TaskEntry Entry, int Token), (double Wake, long Seq)> _gameHeap = new();
    private readonly PriorityQueue<(TaskEntry Entry, int Token), (double Wake, long Seq)> _realHeap = new();
    private readonly Queue<(TaskEntry Entry, int Token)> _deferred = new();
    private readonly HashSet<ReloadScope> _taggedScopes = [];
    private readonly List<(TaskEntry Entry, int Token)> _due = [];
    private long _sequence;

    private readonly TweenRunner _tweens;

    internal ScriptScheduler(LuauVm vm)
    {
        _vm = vm;
        _tweens = new TweenRunner(vm, this);
    }

    /// <summary>The tweens of the VM, which are stepped after the tasks of each <see cref="LuauVm.AdvanceTime"/>.</summary>
    internal TweenRunner Tweens => _tweens;

    /// <summary>The tweens that have not ended: playing, delayed or paused.</summary>
    public int TweenCount => _tweens.Count;

    /// <summary>The tweens of <paramref name="owner"/> that have not ended.</summary>
    public int TweenCountOf(ScriptOwner owner) => _tweens.CountOf(owner);

    /// <summary>Game seconds since the VM started: what <c>task.time()</c> gives, and the clock of <c>task.wait</c>, <c>task.delay</c> and <c>task.every</c>. It stops while the game's time scale is 0.</summary>
    public double Time { get; private set; }

    /// <summary>Real seconds since the VM started, whatever the time scale: the clock of <c>task.wait_real</c>.</summary>
    public double RealTime { get; private set; }

    /// <summary>The tasks that have not ended: asleep, deferred or running.</summary>
    public int Count => _tasks.Count;

    /// <summary>The tasks of <paramref name="owner"/> that have not ended.</summary>
    public int CountOf(ScriptOwner owner)
    {
        int count = 0;
        foreach (TaskEntry entry in _tasks.Values)
        {
            if (entry.Owner == owner)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Raised when a task ends with an error, a timeout or a memory error (it is logged too), with its owner and the message. The script that started the task never sees the error.</summary>
    public event Action<ScriptOwner, string>? TaskFailed;

    /// <summary>Stops every task and tween of <paramref name="owner"/>: what a mod that failed to load leaves behind. A tween stopped this way emits no signal.</summary>
    public void CancelOwner(ScriptOwner owner)
    {
        foreach (TaskEntry entry in _tasks.Values.Where(t => t.Owner == owner).ToArray())
        {
            Cancel(entry);
        }

        _tweens.CancelOwner(owner);
    }

    /// <summary>Stops every task and tween: the VM is going away. A tween stopped this way emits no signal.</summary>
    public void CancelAll()
    {
        foreach (TaskEntry entry in _tasks.Values.ToArray())
        {
            Cancel(entry);
        }

        _tweens.CancelAll();
    }

    /// <summary>Stops the tasks and tweens started while a module loaded (or by those tasks): the module is being reloaded, or it failed to load.</summary>
    private void CancelScope(ReloadScope scope)
    {
        _ = _taggedScopes.Remove(scope);
        foreach (TaskEntry entry in _tasks.Values.Where(t => t.Scope == scope).ToArray())
        {
            Cancel(entry);
        }

        _tweens.CancelScope(scope);
    }

    /// <summary>
    /// The module scope that work started now belongs to: the module that is loading, or else the module of the task that is running. The first time a scope is seen it registers the one cleanup that cancels
    /// what was started in it, so a long-lived scope does not collect one cleanup per task or tween.
    /// </summary>
    internal ReloadScope? ScopeForNewWork()
    {
        ReloadScope? scope = ScriptScope.Current;
        if (scope is not null && _taggedScopes.Add(scope))
        {
            ScriptScope.OnReload(() => CancelScope(scope));
        }

        return scope ?? _current?.Scope;
    }

    // ---- Time ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Moves both clocks, resumes the tasks that are due, steps the tweens and then resumes the deferred tasks. Every task that is due is picked before the first one runs, so a task that waits again, even for 0 seconds, is
    /// resumed once per call at most. The due ones run in the order of their wake times (game clock first, then the real clock), then in the order they were scheduled.
    /// </summary>
    internal void Advance(double gameSeconds, double realSeconds)
    {
        if (!double.IsFinite(gameSeconds) || gameSeconds < 0 || !double.IsFinite(realSeconds) || realSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gameSeconds), "Time only moves forward, by a finite amount.");
        }

        Time += gameSeconds;
        RealTime += realSeconds;

        _due.Clear();
        PopDue(_gameHeap, Time);
        PopDue(_realHeap, RealTime);
        _tweens.TaskPhase = true; // a tween that a task makes now first moves with the next step
        try
        {
            for (int i = 0; i < _due.Count; i++) // nothing adds to the list while the tasks run
            {
                (TaskEntry entry, int token) = _due[i];
                if (entry.Phase == Phase.Waiting && entry.Token == token) // an earlier task of this step may have cancelled it
                {
                    Run(entry, _vm.Main, ElapsedOf(entry));
                }
            }
        }
        finally
        {
            _tweens.TaskPhase = false;
        }

        _due.Clear();

        // The tweens go after the due tasks and before the deferred ones, so that a task waiting for a tween that ends now resumes in this very drain.
        _tweens.Step(gameSeconds, realSeconds);
        Drain();
    }

    private void PopDue(PriorityQueue<(TaskEntry Entry, int Token), (double Wake, long Seq)> heap, double now)
    {
        while (heap.TryPeek(out (TaskEntry Entry, int Token) item, out (double Wake, long Seq) priority) && priority.Wake <= now + Epsilon)
        {
            _ = heap.Dequeue();
            if (item.Entry.Phase == Phase.Waiting && item.Entry.Token == item.Token)
            {
                _due.Add(item);
            }
        }
    }

    private double ElapsedOf(TaskEntry entry) => (entry.WaitClock == Clock.Game ? Time : RealTime) - entry.WaitStart;

    /// <summary>
    /// Runs the deferred tasks that were queued before the call. One deferred during the drain waits for the next, so a task that defers itself again cannot hang the frame.
    /// </summary>
    internal void Drain()
    {
        for (int pending = _deferred.Count; pending > 0 && _deferred.Count > 0; pending--)
        {
            (TaskEntry entry, int token) = _deferred.Dequeue();
            if (entry.Phase == Phase.Deferred && entry.Token == token)
            {
                Run(entry, _vm.Main, 0);
            }
        }
    }

    // ---- Starting tasks (the bodies of the task library call these) ---------------------------------------------------------------------------

    /// <summary><c>task.spawn(f, ...)</c>: runs now, until its first wait. Pushes the thread.</summary>
    internal int Spawn(State* L)
    {
        TaskEntry entry = Prepare(L, 1, 2, every: false, "spawn");
        _ = PushThread(L, entry); // before it runs: a task that ends at once lets go of its reference
        Run(entry, L, 0);
        return 1;
    }

    /// <summary><c>task.defer(f, ...)</c>: runs at the next drain. Pushes the thread.</summary>
    internal int Defer(State* L)
    {
        TaskEntry entry = Prepare(L, 1, 2, every: false, "defer");
        Queue(entry);
        return PushThread(L, entry);
    }

    /// <summary><c>task.delay(seconds, f, ...)</c>: runs after that many game seconds. Pushes the thread.</summary>
    internal int Delay(State* L)
    {
        double seconds = Seconds(L, 1, "delay");
        TaskEntry entry = Prepare(L, 2, 3, every: false, "delay");
        Sleep(entry, Clock.Game, Time + seconds);
        return PushThread(L, entry);
    }

    /// <summary><c>task.every(seconds, f, ...)</c>: calls f every that many game seconds. Pushes the thread.</summary>
    internal int Every(State* L)
    {
        double seconds = Seconds(L, 1, "every");
        if (seconds <= 0)
        {
            throw new ValueException("bad argument #1 to 'every' (the period must be greater than 0)");
        }

        TaskEntry entry = Prepare(L, 2, 3, every: true, "every");
        entry.Every = true;
        entry.Period = seconds;
        entry.Origin = Time;
        Sleep(entry, Clock.Game, Time + seconds);
        return PushThread(L, entry);
    }

    private static double Seconds(State* L, int index, string function)
    {
        if (!Stack.Is(L, index, LuaType.Tnumber))
        {
            throw new ValueException($"bad argument #{index} to '{function}' (number expected, got {Stack.TypeName(L, index)})");
        }

        double seconds = Lua.Tonumberx(L, index, null);
        return double.IsFinite(seconds) ? Math.Max(seconds, 0) : throw new ValueException($"bad argument #{index} to '{function}' (the time must be a finite number)");
    }

    /// <summary>
    /// Makes the task of the function (or the suspended coroutine) at <paramref name="functionIndex"/>, with the arguments from <paramref name="argumentIndex"/> to the top. It is registered but not
    /// scheduled. The thread belongs to the owner of the script that made it, so its memory counts there whoever wrote the function.
    /// </summary>
    private TaskEntry Prepare(State* L, int functionIndex, int argumentIndex, bool every, string function)
    {
        int argumentCount = Math.Max(Lua.Gettop(L) - argumentIndex + 1, 0);
        int type = Lua.Type(L, functionIndex);
        if (type != (int)LuaType.Tfunction && !(type == (int)LuaType.Tthread && !every))
        {
            string expected = every ? "a function" : "a function or a thread";
            throw new ValueException($"bad argument #{functionIndex} to '{function}' ({expected} expected, got {Stack.TypeName(L, functionIndex)})");
        }

        if (!Stack.Reserve(L, argumentCount + 4))
        {
            throw new ValueException("too many arguments for a task");
        }

        var entry = new TaskEntry();
        if (type == (int)LuaType.Tthread)
        {
            State* coroutine = Lua.Tothread(L, functionIndex);
            if (_tasks.ContainsKey((nint)coroutine))
            {
                throw new ValueException($"bad argument #{functionIndex} to '{function}' (the coroutine is a task already)");
            }

            if (Lua.Costatus(L, coroutine) != 1) // LUA_COSUS: not running, not resuming another, not dead
            {
                throw new ValueException($"bad argument #{functionIndex} to '{function}' (the coroutine is not suspended)");
            }

            entry.Thread = coroutine;
            entry.Owner = _vm.OwnerOf(coroutine);
            entry.Reference = Lua.Ref(L, functionIndex);
            if (!Stack.Reserve(coroutine, argumentCount + 2))
            {
                _ = Lua.Unref(_vm.Main, entry.Reference);
                throw new ValueException("too many arguments for a task");
            }

            PushArguments(L, coroutine, argumentIndex, argumentCount);
            entry.PreparedArgs = argumentCount;
        }
        else
        {
            ScriptOwner owner = _vm.OwnerOf(L);
            State* thread = _vm.NewThread(owner, out int reference);
            entry.Thread = thread;
            entry.Owner = owner;
            entry.Reference = reference;
            entry.Created = true;
            if (every)
            {
                // The function and its arguments wait in a table, to be pushed again for each call.
                Lua.Createtable(L, argumentCount + 1, 0);
                Lua.Pushvalue(L, functionIndex);
                Lua.Rawseti(L, -2, 1);
                for (int i = 0; i < argumentCount; i++)
                {
                    Lua.Pushvalue(L, argumentIndex + i);
                    Lua.Rawseti(L, -2, i + 2);
                }

                entry.TemplateReference = Lua.Ref(L, -1);
                entry.TemplateCount = argumentCount;
                Lua.Settop(L, -2);
            }
            else if (Stack.Reserve(thread, argumentCount + 2))
            {
                Lua.Pushvalue(L, functionIndex);
                Lua.Xmove(L, thread, 1);
                PushArguments(L, thread, argumentIndex, argumentCount);
                entry.PreparedArgs = argumentCount;
            }
            else
            {
                _ = Lua.Unref(_vm.Main, reference);
                throw new ValueException("too many arguments for a task");
            }
        }

        // The scope of the module that is loading, or the one of the task that starts this one: the module's tasks end when it is reloaded.
        entry.Scope = ScopeForNewWork();
        _tasks[(nint)entry.Thread] = entry;
        return entry;
    }

    private static void PushArguments(State* from, State* to, int first, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Lua.Pushvalue(from, first + i);
        }

        Lua.Xmove(from, to, count);
    }

    private static int PushThread(State* L, TaskEntry entry)
    {
        _ = Stack.RawGetI(L, Lua.Registryindex, entry.Reference);
        return 1;
    }

    private void Queue(TaskEntry entry)
    {
        entry.Phase = Phase.Deferred;
        entry.Token++;
        _deferred.Enqueue((entry, entry.Token));
    }

    private void Sleep(TaskEntry entry, Clock clock, double wake)
    {
        entry.Phase = Phase.Waiting;
        entry.Token++;
        entry.WaitClock = clock;
        entry.WaitStart = clock == Clock.Game ? Time : RealTime;
        (clock == Clock.Game ? _gameHeap : _realHeap).Enqueue((entry, entry.Token), (wake, _sequence++));
    }

    // ---- Waiting and cancelling ---------------------------------------------------------------------------------------------------------------

    /// <summary><c>task.wait(seconds)</c> and <c>task.wait_real(seconds)</c>: puts the running task to sleep and yields. Returns the yield result.</summary>
    internal int Wait(State* L, Clock clock)
    {
        if (!_tasks.TryGetValue((nint)L, out TaskEntry? entry) || entry.Phase != Phase.Running)
        {
            throw new ValueException("task.wait must run in a task: start the function with task.spawn, task.defer or task.delay");
        }

        double seconds = Stack.Is(L, 1, LuaType.Tnil) || Lua.Type(L, 1) == Lua.Tnone ? 0 : Seconds(L, 1, clock == Clock.Game ? "wait" : "wait_real");
        if (Lua.Isyieldable(L) == 0)
        {
            throw new ValueException("task.wait cannot yield here: it was called from a metamethod or from a call made by the engine, not from the task itself");
        }

        Sleep(entry, clock, (clock == Clock.Game ? Time : RealTime) + seconds);
        return Lua.Yield;
    }

    /// <summary>
    /// For <c>Tween:wait</c>: the running task goes to sleep until <see cref="Wake"/>. Checks everything first and changes nothing when it throws; the caller then returns <see cref="Lua.Yield"/>.
    /// </summary>
    internal ParkedTask Park(State* L, string what)
    {
        if (!_tasks.TryGetValue((nint)L, out TaskEntry? entry) || entry.Phase != Phase.Running)
        {
            throw new ValueException($"{what} must run in a task: start the function with task.spawn, task.defer or task.delay");
        }

        if (Lua.Isyieldable(L) == 0)
        {
            throw new ValueException($"{what} cannot yield here: it was called from a metamethod or from a call made by the engine, not from the task itself");
        }

        entry.Phase = Phase.Parked;
        entry.Token++;
        return new ParkedTask(entry, entry.Token);
    }

    /// <summary>Resumes a parked task at the next drain, with <paramref name="result"/> as the value its wait returns. Does nothing when the task ended or was cancelled in the meantime.</summary>
    internal void Wake(ParkedTask parked, bool result)
    {
        TaskEntry entry = parked.Entry;
        if (entry.Phase != Phase.Parked || entry.Token != parked.Token)
        {
            return;
        }

        entry.WakeResult = result ? 1 : 2;
        Queue(entry);
    }

    /// <summary><c>task.cancel(thread)</c>: a task that is asleep or deferred never runs again. A thread that is not a task (or is over) is not an error.</summary>
    internal void Cancel(State* L)
    {
        if (!Stack.Is(L, 1, LuaType.Tthread))
        {
            throw new ValueException($"bad argument #1 to 'cancel' (thread expected, got {Stack.TypeName(L, 1)})");
        }

        State* thread = Lua.Tothread(L, 1);
        if (_tasks.TryGetValue((nint)thread, out TaskEntry? entry))
        {
            if (entry.Phase == Phase.Running)
            {
                throw new ValueException("a task cannot cancel itself; return from it instead");
            }

            Cancel(entry);
        }
    }

    private void Cancel(TaskEntry entry)
    {
        if (entry.Phase == Phase.Done)
        {
            return;
        }

        if (entry.Phase == Phase.Running)
        {
            // It is on the stack of a resume that is still going (a reload from inside a task): it ends when it yields or returns.
            entry.CancelRequested = true;
            return;
        }

        Finish(entry, reset: true);
    }

    // ---- Running ------------------------------------------------------------------------------------------------------------------------

    private TaskEntry? _current;

    /// <summary>Resumes the task: its first run, the next call of an <c>every</c>, or the end of a wait (which returns the time that passed).</summary>
    private void Run(TaskEntry entry, State* from, double elapsed)
    {
        State* thread = entry.Thread;
        ScriptOwner owner = entry.Owner;

        // An owner that hit its limit has the garbage of the failed call counted until a collection: do it now, or this resume would fail too.
        if (owner.MemoryLimit > 0 && _vm.MemoryUsed(owner) > owner.MemoryLimit)
        {
            _vm.CollectGarbage();
        }

        int arguments;
        if (entry.NeedsStart)
        {
            entry.NeedsStart = false;
            if (entry.Every)
            {
                arguments = StartCall(entry);
            }
            else
            {
                arguments = entry.PreparedArgs;
            }
        }
        else if (Stack.Reserve(thread, 2))
        {
            if (entry.WakeResult != 0)
            {
                Lua.Pushboolean(thread, entry.WakeResult == 1 ? 1 : 0);
                entry.WakeResult = 0;
            }
            else
            {
                Lua.Pushnumber(thread, elapsed);
            }

            arguments = 1;
        }
        else
        {
            arguments = 0;
        }

        entry.Phase = Phase.Running;
        TaskEntry? previous = _current;
        _current = entry;
        CallResult result;
        try
        {
            result = Lua.ProtectedResume(thread, from, arguments, owner.TimeBudgetMilliseconds);
        }
        finally
        {
            _current = previous;
        }

        if (entry.CancelRequested)
        {
            Finish(entry, reset: result != CallResult.Yielded);
            return;
        }

        switch (result)
        {
            case CallResult.Ok when entry.Every:
                // The call is over: sleep until the first multiple of the period after now (a frame that covers several periods runs the call once).
                entry.NeedsStart = true;
                Lua.Settop(thread, 0);
                Sleep(entry, Clock.Game, entry.Origin + ((Math.Floor(((Time - entry.Origin) / entry.Period) + Epsilon) + 1) * entry.Period));
                break;
            case CallResult.Ok:
                Finish(entry, reset: true);
                break;
            case CallResult.Yielded when entry.Phase == Phase.Running:
                // A yield that did not come from task.wait: nothing will wake it, and the script that made the coroutine can resume it by hand.
                Log.Warn("Script:" + owner.Id, "a task yielded with coroutine.yield and ends: use task.wait to wait in a task");
                Finish(entry, reset: false);
                break;
            case CallResult.Yielded:
                break; // asleep: task.wait scheduled it
            default:
                Fail(entry, result);
                break;
        }
    }

    /// <summary>Pushes the function and the arguments of an <c>every</c> task on its thread, which is idle. Returns the number of arguments.</summary>
    private static int StartCall(TaskEntry entry)
    {
        State* thread = entry.Thread;
        Lua.Settop(thread, 0);
        if (!Stack.Reserve(thread, entry.TemplateCount + 4))
        {
            return 0; // the call fails with an error of its own: there is no function
        }

        _ = Stack.RawGetI(thread, Lua.Registryindex, entry.TemplateReference);
        for (int i = 1; i <= entry.TemplateCount + 1; i++)
        {
            _ = Stack.RawGetI(thread, 1, i);
        }

        Lua.Remove(thread, 1);
        return entry.TemplateCount;
    }

    private void Fail(TaskEntry entry, CallResult result)
    {
        State* thread = entry.Thread;
        string message = Stack.Is(thread, -1, LuaType.Tstring) ? Stack.ReadString(thread, -1) : Stack.Describe(thread, -1);

        string full = message;
        State* main = _vm.Main;
        if (Stack.Reserve(main, 4))
        {
            fixed (byte* text = Utf8.ToZeroTerminated(message))
            {
                Lua.LuaLTraceback(main, thread, text, 0);
            }

            full = Stack.ReadString(main, -1);
            Lua.Settop(main, -2);
        }

        string what = result switch
        {
            CallResult.Timeout => "timed out",
            CallResult.MemoryError => "ran out of memory",
            _ => "failed",
        };
        ScriptOwner owner = entry.Owner;
        Finish(entry, reset: true);
        Log.Error("Script:" + owner.Id, $"task {what}: {full}");
        TaskFailed?.Invoke(owner, message);
    }

    /// <summary>Forgets the task: the registry lets go of its thread (a collection frees it), and a thread the scheduler made is cleared at once.</summary>
    private void Finish(TaskEntry entry, bool reset)
    {
        entry.Phase = Phase.Done;
        entry.Token++;
        _ = _tasks.Remove((nint)entry.Thread);
        _ = Lua.Unref(_vm.Main, entry.Reference);
        if (entry.TemplateReference != Lua.Noref)
        {
            _ = Lua.Unref(_vm.Main, entry.TemplateReference);
            entry.TemplateReference = Lua.Noref;
        }

        // Only a thread of ours, and only when nothing is running on it: a script may hold a coroutine it gave us.
        if (reset && entry.Created)
        {
            Lua.Resetthread(entry.Thread);
        }
    }
}
