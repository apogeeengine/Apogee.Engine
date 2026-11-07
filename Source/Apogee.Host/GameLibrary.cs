using Apogee.Reflection;

namespace Apogee.Host;

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

    /// <summary>Starts a run with the defaults (a new session, tests).</summary>
    internal static void Reset()
    {
        QuitRequested = false;
        EscapeQuits = true;
    }

    /// <summary>Ends the run at the end of this frame (the window closes, a headless run stops).</summary>
    [Method]
    public static void Quit() => QuitRequested = true;

    /// <summary>Says whether the Escape key ends the run. A game that uses Escape for its own menu says false.</summary>
    [Method]
    public static void SetEscapeQuits(bool quits) => EscapeQuits = quits;
}
