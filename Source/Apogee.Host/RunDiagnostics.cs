using System.IO;
using Apogee.Core.Diagnostics;
using Apogee.Core.Logging;

namespace Apogee.Host;

/// <summary>The log file and the crash reports of a run, in the player's folder (<c>user://logs</c> and <c>user://crashes</c>).</summary>
public static class RunDiagnostics
{
    /// <summary>
    /// Starts the run's diagnostics: the crash reports go to <c>&lt;user&gt;/crashes</c>, a run that left its marker in <c>&lt;user&gt;/logs</c> without ending (see <see cref="CrashHandler.CheckPreviousRun"/>) gets its
    /// report, and a new log file is added to <see cref="Log"/>. Call <see cref="CrashHandler.Install"/> first (the program does, as early as it can).
    /// </summary>
    /// <param name="userDirectory">The folder of the player's files (<see cref="ScriptSession.UserDirectoryFor"/>).</param>
    /// <param name="gameName">Named in the log and in crash reports.</param>
    /// <param name="gameVersion">The game's version, for crash reports.</param>
    /// <returns>The log file, to dispose when the run ends cleanly: until then, the run counts as one that crashed.</returns>
    public static FileLogSink Start(string userDirectory, string gameName, string? gameVersion = null)
    {
        string logs = Path.Combine(userDirectory, "logs");
        CrashHandler.SetReportDirectory(Path.Combine(userDirectory, "crashes"));
        CrashHandler.SetContext("Game", gameName);
        if (gameVersion is not null)
        {
            CrashHandler.SetContext("Game version", gameVersion);
        }

        CrashHandler.CheckPreviousRun(logs);
        var sink = new FileLogSink(logs, gameName);
        Log.AddSink(sink);
        CrashHandler.SetLogFile(sink);
        return sink;
    }
}
