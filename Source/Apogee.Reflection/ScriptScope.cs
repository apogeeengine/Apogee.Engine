using System;
using System.Collections.Generic;

namespace Apogee.Reflection;

/// <summary>
/// What a module registered while it loaded, so that loading it again can take it back: a function that draws, a signal connection, a system. A reload makes a new scope, runs the new
/// module in it, and then either runs the old scope's cleanups (the new code worked) or this one's (it did not, and the old registrations stay).
/// </summary>
public sealed class ReloadScope
{
    private readonly List<Action> _cleanups = [];

    internal void Add(Action cleanup) => _cleanups.Add(cleanup);

    /// <summary>Takes back everything registered in the scope, the last first. A cleanup that fails does not stop the others.</summary>
    public void Cleanup()
    {
        for (int i = _cleanups.Count - 1; i >= 0; i--)
        {
            try
            {
                _cleanups[i]();
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or ObjectDisposedException)
            {
                // Something already gone (the object a connection was on): nothing to take back.
            }
        }

        _cleanups.Clear();
    }
}

/// <summary>The scope of the module that is loading now, on this thread; libraries that register things for scripts call <see cref="OnReload"/>.</summary>
public static class ScriptScope
{
    [ThreadStatic]
    private static ReloadScope? s_current;

    /// <summary>Makes <paramref name="scope"/> the current one until the result is disposed.</summary>
    public static Restore Enter(ReloadScope scope)
    {
        ReloadScope? previous = s_current;
        s_current = scope;
        return new Restore(previous);
    }

    /// <summary>Records how to take back what was just registered; does nothing outside a module that is loading (what a script registers later is the script's to manage).</summary>
    public static void OnReload(Action cleanup) => s_current?.Add(cleanup);

    public readonly struct Restore(ReloadScope? previous) : IDisposable
    {
        public void Dispose() => s_current = previous;
    }
}
