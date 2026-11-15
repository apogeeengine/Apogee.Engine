using System.Collections.Generic;
using Apogee.Native.Luau;
using Apogee.Reflection;

namespace Apogee.Scripting;

/// <summary>
/// The playing tweens of one VM, in the order they were made. <see cref="Step"/> moves each one by the time that passed on its clock (the game clock, or the real one for an <c>unscaled</c> tween). While a
/// tween plays the runner keeps the registry slot of its userdata, so the script that made it may drop the handle; when it ends the slot is freed and the userdata is collected as soon as nothing else holds it.
/// Not thread safe: use it from the thread that made the VM.
/// </summary>
internal sealed unsafe class TweenRunner(LuauVm vm, ScriptScheduler scheduler)
{
    private readonly List<Tween> _tweens = [];

    /// <summary>True while the due tasks of a step run: a tween made then is not moved by the step that follows.</summary>
    internal bool TaskPhase { get; set; }

    /// <summary>The tweens that have not ended.</summary>
    internal int Count
    {
        get
        {
            int count = 0;
            foreach (Tween tween in _tweens)
            {
                if (tween.State == Tween.StateKind.Active)
                {
                    count++;
                }
            }

            return count;
        }
    }

    internal int CountOf(ScriptOwner owner)
    {
        int count = 0;
        foreach (Tween tween in _tweens)
        {
            if (tween.State == Tween.StateKind.Active && tween.Owner == owner)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Starts playing the tween whose userdata is on top of <paramref name="L"/>'s stack (it stays there).</summary>
    internal void Add(State* L, Tween tween)
    {
        tween.Reference = Lua.Ref(L, -1);
        tween.Scope = scheduler.ScopeForNewWork();
        tween.SkipStep = TaskPhase;
        _tweens.Add(tween);
    }

    /// <summary>Moves every tween that plays. A tween made or ended by a handler during the step is handled at the next one.</summary>
    internal void Step(double gameSeconds, double realSeconds)
    {
        int count = _tweens.Count; // a handler may start tweens: they begin with the next step
        for (int i = 0; i < count; i++)
        {
            Tween tween = _tweens[i];
            if (tween.State != Tween.StateKind.Active)
            {
                continue;
            }

            if (tween.SkipStep)
            {
                tween.SkipStep = false;
                continue;
            }

            // A tween whose clock stands still (the game at a time scale of 0) does nothing: it does not even rewrite its value.
            double seconds = tween.Unscaled ? realSeconds : gameSeconds;
            if (!tween.IsPaused && seconds > 0)
            {
                tween.Step(seconds);
            }
        }

        _ = _tweens.RemoveAll(static t => t.State != Tween.StateKind.Active);
    }

    /// <summary>
    /// Ends the tween: it lets go of what it kept alive, wakes the tasks that wait for it (with true when it completed) and, unless it was dropped with its owner, emits <c>completed</c> or <c>cancelled</c>.
    /// Does nothing when it has ended already.
    /// </summary>
    internal void Finish(Tween tween, TweenEnd how)
    {
        if (tween.State != Tween.StateKind.Active)
        {
            return;
        }

        tween.MarkEnded(how);
        if (tween.Reference != Lua.Noref)
        {
            _ = Lua.Unref(vm.Main, tween.Reference);
            tween.Reference = Lua.Noref;
        }

        tween.Setter?.Release();
        if (tween.Waiters is { } waiters)
        {
            tween.Waiters = null;
            foreach (ScriptScheduler.ParkedTask parked in waiters)
            {
                scheduler.Wake(parked, how == TweenEnd.Completed);
            }
        }

        tween.EmitEnd(how);
    }

    internal void CancelOwner(ScriptOwner owner) => DropWhere(t => t.Owner == owner);

    internal void CancelScope(ReloadScope scope) => DropWhere(t => t.Scope == scope);

    internal void CancelAll()
    {
        DropWhere(static _ => true);
        _tweens.Clear();
    }

    private void DropWhere(System.Func<Tween, bool> match)
    {
        foreach (Tween tween in _tweens.ToArray())
        {
            if (match(tween))
            {
                Finish(tween, TweenEnd.Dropped);
            }
        }
    }
}
