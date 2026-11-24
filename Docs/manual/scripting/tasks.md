# Tasks and game time

The `task` library runs a function over several frames. A task is a Luau thread that the engine resumes when its time comes: it can `task.wait` in the middle of a function and carry on later, with the
locals it had. Games use it for timers, cutscenes, spawn waves, countdowns and anything that reads "do this, wait, do that".

```lua
task.spawn(function()
    print("ready")
    task.wait(3)                    -- three seconds of game time
    print("go")
    local waited = task.wait()      -- the next frame; returns the seconds that passed
end)

local ticker = task.every(0.5, function() spawn_spark() end)
task.delay(10, function() task.cancel(ticker) end)
```

| Function | What it does |
| --- | --- |
| `task.spawn(f, ...)` | Runs `f(...)` now, in a new task, until its first `task.wait` or its end. Also takes a suspended coroutine. Returns the task's thread. |
| `task.defer(f, ...)` | The same, but `f` starts at the next drain (below) instead of now. |
| `task.delay(seconds, f, ...)` | The same, after `seconds` of game time. |
| `task.every(seconds, f, ...)` | Calls `f(...)` every `seconds` of game time, until `task.cancel` or an error in `f`. At most once per frame. |
| `task.wait(seconds?)` | Pauses the running task for `seconds` of game time and returns how many really passed. `nil` or `0` means the next frame. |
| `task.wait_real(seconds?)` | The same on the real clock. |
| `task.cancel(thread)` | Stops a task that is waiting or deferred. A thread that is over or is not a task is ignored. |
| `task.time()` | Game seconds since the game started. |
| `task.real_time()` | Real seconds since the game started. |

The values a task returns are dropped. An error in a task never reaches the code that started it: it is logged for the task's owner with a traceback (`Script:<owner>`), and
`ScriptScheduler.TaskFailed` is raised. The task is over.

## Which clock

There are two clocks. **Game time** is what `game.set_time_scale(scale)` changes (see below): `task.wait`, `task.delay`, `task.every` and `task.time` follow it, and at a scale of 0 they stop.
**Real time** goes on at any scale: `task.wait_real` and `task.real_time` are for a pause menu or a countdown that has to run while the game is frozen.

```lua
game.set_time_scale(0)                       -- the game is paused
task.spawn(function()
    task.wait_real(2)                        -- this one still runs
    game.set_time_scale(1)
end)
```

A wait is over when the clock reaches `start + seconds`. The engine steps at 60 Hz when it runs headless and by the real frame time in a window, so a wait for 0.5 s resumes on the first frame at or after it: a wait is
never shorter than asked, and with a time scale of 1 it is at most one frame longer. `task.wait(0)` resumes on the next frame, never in the same one.

## Time scale

`game.set_time_scale(scale)` sets how fast game time runs, from 0 to 100 (1 is normal, 0.5 slow motion, 0 a stop; a number outside the range is clamped and NaN is an error), and `game.time_scale()` reads it.
A new game starts at 1. The session scales the step it gives the physics, the world's systems (`world.delta_time`) and the game clock of `task`; audio, the user interface and input keep real time.

The editor's pause stops both clocks, because the frame is not stepped; its Step button runs one frame. Play and Stop make a new session, so no task survives Stop.

## Where a frame resumes tasks

In `ScriptSession.Progress`: Steam, the network, input, physics, the world's systems, then **the tasks**, then audio and the UI. A task so sees this frame's physics and transforms, and what it changes is heard and
laid out in the same frame. `ScriptSession.EndFrame` runs the deferred tasks once more, for the ones that GUI functions, UI handlers and signals deferred after the step.

Inside `LuauVm.AdvanceTime(game, real)` (what `Progress` calls):

1. Both clocks move.
2. Every task that is due is picked **before** the first one runs, so a task that waits again, even for 0 seconds, runs once per call at most.
3. They run in the order of their wake times (game clock first, then the real clock), and among equal times in the order they were scheduled, so a run is the same on every machine.
4. The deferred tasks run: those queued before this drain began. One deferred during a drain waits for the next, so a task that defers itself again cannot hang a frame.

`task.every` fires at `t0 + s`, `t0 + 2s`, and so on, once per frame: when one frame covers several periods (a long frame, or a time scale of 100), the others are skipped and the next call is at the first multiple
of the period after now. If the function itself waits, the next period is counted from when it returns.

## Budgets, memory and mods

Every resume is a protected resume under the time budget of the task's **owner**, as every other call into scripts is: a task that loops forever times out ("exceeded its time budget"), is logged and ends, and
the other tasks of the frame still run. The thread belongs to the owner of the script that started it and counts in that owner's memory category; an owner over its memory limit has its garbage collected before its next resume.
`task.spawn` called from inside another call is a nested resume, so it runs under the caller's deadline until its first wait.

Mods may use `task` freely: the budgets and the memory limit confine them. A mod that fails to load has its tasks cancelled.

## Why `wait` needs a task

`task.wait` yields the thread it runs in, so it works only in a thread the scheduler owns: one made by `spawn`, `defer`, `delay` or `every`, or a coroutine handed over with `task.spawn(co)`. Anywhere else it is an error:

> task.wait must run in a task: start the function with task.spawn, task.defer or task.delay

That covers a module's top level, a system or signal callback, a plain `coroutine.create` or `coroutine.wrap`, and the `@engine/test` cases (start a task from them and wait with `test.frames`). The scheduler never adopts
a coroutine on its own, so it cannot race one that a script resumes by hand. Inside a task, `task.wait` works in a `pcall` (Luau's `pcall` can yield); in a metamethod or in a function the engine called (a sort
comparator, a signal handler) it fails with "cannot yield". A `coroutine.yield` in a task ends the task with a warning: use `task.wait`.

A task cannot cancel itself with `task.cancel`: return from it instead.

## Hot reload

A task started while a module loads, or by a task that one started, belongs to that module. When the module is reloaded ([Hot reload](../content/hot-reload.md)), the tasks of the old run are cancelled
and the new run starts its own; if the new code fails, its tasks are cancelled and the old ones stay. A task started later by an event handler that is not in a task has no module, and is the script's to manage.

## From C#

```csharp
vm.AdvanceTime(gameSeconds: 1.0 / 60, realSeconds: 1.0 / 60);    // one frame; there is no clock inside the VM
vm.RunDeferred();                                                // the end of a frame
ScriptScheduler tasks = vm.Scheduler;                            // Time, RealTime, Count, CountOf(owner), TaskFailed, CancelOwner, CancelAll
```

Disposing the VM cancels every task (and tween). Tweens ([Tweens](tweens.md)) are stepped by the same `AdvanceTime`, after the due tasks, and a task can wait for one with `Tween:wait()`. `LuauTestRunner` calls `AdvanceTime(1/60, 1/60)` after each frame of a Luau test, so the tests of `Tests/Luau/task.test.luau` count frames with `test.frames(n)`.

The yield is made by a function in the native library (`WEFT_LUAU_YIELD` in `Native/luau/weft_luau.h`): run `./build.sh native` after updating, or `Weft.Native.Tests` will say that the library is old.
