# Tweens

The `tween` library moves a value from one number to another over some seconds, with an easing curve: a door that swings, a light that fades, a colour that pulses, a camera that turns. A tween
plays by itself; the script starts it and goes on. It can animate a number, a vector, a colour (a linear-RGB vector, as `Light.color` is), a rotation (`Quat`, slerped), a property of an engine
object, or a field of an entity's component.

```lua
local Light = world:component("Light")
local Transform = world:component("Transform")

-- A light fades in over two seconds and a door turns, eased; a score counts up.
tween.field(world, lamp, Light, "Intensity", 800, 2, { ease = "quad_out" })
tween.field(world, door, Transform, "Rotation", Quat.axisAngle(Vector3(0, 1, 0), math.pi / 2), 0.6, { ease = "back_out" })
tween.value(0, 1500, 1.5, function(v) label_text = tostring(math.floor(v)) end)

-- Fade the music out with an engine object's property, then wait for it in a task.
task.spawn(function()
    local fade = tween.property(audio:get_bus("music"), "volume", 0, 1)
    if fade:wait() then print("silent") end      -- true when it completed, false when it was cancelled
end)

-- Pulse a colour for ever.
tween.field(world, lamp, Light, "Color", Vector3(1, 0.8, 0.2), 0.8, { repeats = -1, reverses = true })
```

| Function | What it does |
| --- | --- |
| `tween.value(from, to, seconds, set, options?)` | Calls `set(value)` every step with a number, a `vector` or a `Quat` between `from` and `to`. Both ends must be the same kind. |
| `tween.property(object, name, to, seconds, options?)` | Animates a property of an engine object (a number, vector or Quat property with a setter) from its value to `to`. |
| `tween.field(world, entity, component, field, to, seconds, options?)` | Animates a field of a component in place: a number (`f32`, `f64` or an integer, which is rounded), a `vec3`, a `dvec3` (a world position, interpolated in double precision) or a `quat`. |
| `tween.ease(ease, t)` | The easing curve at time `t`, from 0 to 1. |

Each of the first three returns a `Tween`.

## Options

The last argument is an optional table:

| Option | Default | Meaning |
| --- | --- | --- |
| `ease` | `"linear"` | The curve, from the table below. |
| `delay` | `0` | Seconds to wait before it starts. `property` and `field` read their start value when the delay ends, not when the tween is made. |
| `repeats` | `0` | The number of extra passes; `-1` repeats for ever. |
| `reverses` | `false` | Every other pass plays backwards, so a repeating tween goes there and back. |
| `unscaled` | `false` | Use the real clock instead of the game clock (below). |

An unknown option, a bad ease name, or the wrong type is a script error at the call. Nothing is written when the tween is made: the first write is in the next step, and the last write is
exactly `to`. A tween with `seconds <= 0` finishes on its first step. With `reverses`, a tween whose last pass runs backwards (an odd number of extra passes) ends on `from`.

## The Tween handle

| Member | |
| --- | --- |
| `playing` | True while the tween runs: not paused, not over. |
| `paused` | True while it is paused. |
| `progress` | How far the current pass has got, 0 to 1 in time (the curve is not applied). 1 once it has completed. |
| `duration` | The seconds of one pass. |
| `ease` | The curve. |
| `cancel()` | Stops it where it is, keeps the value it has, and emits `cancelled`. |
| `complete()` | Jumps to the end value, writes it, and emits `completed`. On a tween that repeats for ever it stops it at `to`. |
| `pause()`, `resume()` | Hold and release it (the delay too). A paused tween is still alive. |
| `completed`, `cancelled` | Signals, emitted at most once each, and only one of them. |
| `wait()` | Yields the running task until the tween ends. Returns `true` if it completed and `false` if it was cancelled. |

`cancel()` and `complete()` do nothing once the tween is over. `completed` is emitted after the last value is written, and never by a tween that repeats for ever (apart from `complete()`).

`wait()` is `task.wait` for a tween: it works only in a task (`task.spawn`, `task.defer`, `task.delay`, `task.every`) and is the same error anywhere else ("must run in a task"), see
[Tasks and game time](tasks.md#why-wait-needs-a-task). On a tween that is over it returns at once, in a task or not. A task that is cancelled while it waits is not woken.

## Easing

`Linear`, and ten families with an `_in`, an `_out` and an `_in_out` form each: `quad`, `cubic`, `quart`, `quint`, `sine`, `expo`, `circ`, `back`, `elastic` and `bounce`. The names are the
snake_case ones: `quad_in_out`, `bounce_out`. `in` starts slowly, `out` ends slowly, `in_out` does both. The curves are Robert Penner's, written from the formulas. Every curve is exactly 0 at
time 0 and exactly 1 at time 1.

| Family | `in` | Shape |
| --- | --- | --- |
| `quad`, `cubic`, `quart`, `quint` | `t^2` … `t^5` | A smoother and smoother start (or end). |
| `sine` | `1 - cos(t * pi / 2)` | The gentlest. |
| `expo` | `2^(10t - 10)` | Almost still, then fast. |
| `circ` | `1 - sqrt(1 - t^2)` | A quarter of a circle. |
| `back` | `2.70158 t^3 - 1.70158 t^2` | Pulls back before it goes, or overshoots the end. Goes outside 0 to 1. |
| `elastic` | a decaying sine | Swings like a spring. Goes outside 0 to 1. |
| `bounce` | a ball dropped | Bounces on the end (`out`) or off the start (`in`). |

`tween.ease("quad_in", 0.5)` is `0.25`. The same curves are `Easing.Apply(Ease, t)` in C#.

## Clocks

A tween follows the **game clock** of the task library ([Tasks and game time](tasks.md#which-clock)): at `game.set_time_scale(0.5)` it plays at half speed, and at 0 it stands still (it does not
rewrite its value either). With `unscaled = true` it follows the **real clock**, which the time scale does not change: a fade of a pause menu. The editor's pause stops both.

Tweens are stepped by `LuauVm.AdvanceTime`, after the due tasks and before the deferred ones, in the order they were made. A tween made by a task in a step first moves in the next step; one made
anywhere else (a system, a module, an event) moves in the next step too, so the first write is never in the call that made the tween. A task that waits for a tween that ends in a step resumes in that same step.

A field tween writes the component's memory and calls `World.Modified`, so observers see each write. The world transform system runs in `World.Progress`, which comes *before* the tweens in a
frame: a tween on `Transform` is seen by the transform system, and so by rendering, one frame later.

## Lifetime, owners and reloading

While a tween plays the engine keeps it, so a script may drop the handle (`tween.value(...)` as a statement is the usual way to use it). When it ends the engine lets go of it, and it is
collected when no script holds it; a script that kept the handle can still read it (`playing` is false).

A tween is cancelled, without emitting `cancelled`, when:

- the module that made it (or a task of that module) is reloaded or fails to load ([Hot reload](../content/hot-reload.md)) — a failed reload cancels the new run's tweens and keeps the old ones;
- its owner is unloaded: a mod that fails to load has its tweens cancelled, as its tasks are;
- the VM ends, with the session.

The handlers of those tweens belong to what is being taken away, so they are not called; a task that waits for one is woken with `false`.

A tween is cancelled, with `cancelled`, when a script calls `cancel()`, when the setter of a `tween.value` or the setter of a property raises an error (the error is logged for the owner that made the
tween, as `Script:<owner>`, with "tween failed"), when a value is out of range for the field (an integer field that would overflow), or when its target is gone: the object was destroyed, the
entity died, or the component was removed. A target that goes away is not an error and is not logged.

## Cost

`tween.property` and `tween.field` make **no Luau call** per frame: the engine reads the start once, then interpolates and writes the property or the memory of the component itself. Only
`tween.value` calls a Luau function (its setter) every step, under the time budget and memory category of the owner that made it. Use the first two for anything that has a property or a field.

## From C#

```csharp
ScriptScheduler tasks = vm.Scheduler;     // TweenCount, TweenCountOf(owner), CancelOwner(owner), CancelAll()
vm.AdvanceTime(1.0 / 60, 1.0 / 60);       // one frame: tasks, then tweens, then the deferred tasks
double eased = Easing.Apply(Ease.QuadInOut, 0.25);
```

`Tween` is an engine class (`Tween.Playing`, `Tween.Cancel()`, `Tween.Completed`…), so it is in the generated definitions and reference. The tests of the library are in
`Tests/Apogee.Scripting.Tests/TweenTests.cs` and `Tests/Luau/tween.test.luau`, whose cases count frames at the runner's 60 Hz clock.
