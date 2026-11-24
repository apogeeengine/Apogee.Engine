using System;
using Weft.Reflection;

namespace Weft.Host;

/// <summary>
/// What a game asks of the program that runs it, as the script table <c>game</c>: ending the run (a menu's Quit), and whether the Escape key still ends it. A shipped game's
/// menu does the first; one with a pause menu of its own does the second, since Escape is its key.
/// </summary>
[ScriptLibrary("game")]
public static partial class GameLibrary
{
    /// <summary>True once a script called <c>game.quit()</c>; the loops end at the end of the frame.</summary>
    public static bool QuitRequested { get; private set; }

    /// <summary>Whether Escape ends a run in a window (the default: it is how a script or a sample is closed). Games with their own Escape menu turn it off.</summary>
    public static bool EscapeQuits { get; private set; } = true;

    private static double s_timeScale = 1;

    /// <summary>Starts a run with the defaults (a new session, tests).</summary>
    internal static void Reset()
    {
        QuitRequested = false;
        EscapeQuits = true;
        s_timeScale = 1;
    }

    /// <summary>Ends the run at the end of this frame (the window closes, a headless run stops).</summary>
    [Method]
    public static void Quit() => QuitRequested = true;

    /// <summary>Says whether the Escape key ends the run. A game that uses Escape for its own menu says false.</summary>
    [Method]
    public static void SetEscapeQuits(bool quits) => EscapeQuits = quits;

    /// <summary>
    /// Sets how fast game time runs (1 is normal, 0.5 half speed, 0 stops it, 2 doubles it), from 0 to 100; a larger number is 100. It scales the physics, the world's systems and the game clock of
    /// <c>task</c> (<c>task.wait</c>, <c>task.delay</c>, <c>task.every</c>, <c>task.time</c>); <c>task.wait_real</c>, audio, the user interface and input keep real time. A new game starts at 1.
    /// </summary>
    [Method]
    public static void SetTimeScale(double scale)
    {
        if (double.IsNaN(scale))
        {
            throw new ValueException("the time scale must be a number");
        }

        s_timeScale = Math.Clamp(scale, 0, 100);
    }

    /// <summary>The time scale set by <c>set_time_scale</c> (1 by default).</summary>
    [Method]
    public static double TimeScale() => s_timeScale;
}
