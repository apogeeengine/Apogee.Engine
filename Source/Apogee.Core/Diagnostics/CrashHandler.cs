using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Apogee.Core.Logging;

namespace Apogee.Core.Diagnostics;

/// <summary>
/// Writes a crash report when an exception ends the program, and one for the previous run when it did not end cleanly. It sees managed exceptions only (<see cref="AppDomain.UnhandledException"/>
/// and unobserved task exceptions): a crash in native code kills the process without running any managed code, and is found on the next start by <see cref="CheckPreviousRun"/>, from the marker the
/// dead run left (see <see cref="FileLogSink"/>). Reports are plain text files named <c>crash-*.txt</c>; the newest twenty are kept.
/// </summary>
public static class CrashHandler
{
    private const int KeptReports = 20;
    private const int PreviousRunLines = 200;

    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, string> s_context = [];
    private static bool s_installed;
    private static bool s_unobservedReported;
    private static string? s_reportDirectory;
    private static RecentLogSink? s_recent;
    private static IDisposable? s_logSink;

    /// <summary>
    /// Starts watching for unhandled exceptions. Reports go to <paramref name="reportDirectory"/> (<see cref="SetReportDirectory"/> moves them), with the lines <paramref name="recent"/> kept (add it to
    /// <see cref="Log"/> yourself). The hooks are made once; calling again only replaces the folder and the sink.
    /// </summary>
    public static void Install(string reportDirectory, RecentLogSink recent)
    {
        lock (s_lock)
        {
            s_reportDirectory = reportDirectory;
            s_recent = recent;
            s_unobservedReported = false;
            if (s_installed)
            {
                return;
            }

            s_installed = true;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>Moves the reports to another folder, once the game's own is known.</summary>
    public static void SetReportDirectory(string reportDirectory)
    {
        lock (s_lock)
        {
            s_reportDirectory = reportDirectory;
        }
    }

    /// <summary>Adds a line to the reports (<c>key: value</c>): the game, its version, the GPU. A key set again is replaced.</summary>
    public static void SetContext(string key, string value)
    {
        lock (s_lock)
        {
            s_context[key] = value;
        }
    }

    /// <summary>The log file of the run: it is closed (and its marker removed) when an exception ends the program, since the crash has been reported by then. Null: none.</summary>
    public static void SetLogFile(IDisposable? logFile)
    {
        lock (s_lock)
        {
            s_logSink = logFile;
        }
    }

    /// <summary>
    /// Writes a report now, with the lines <see cref="Install"/>'s sink kept, and keeps the newest twenty files of the folder. Never throws.
    /// </summary>
    /// <returns>The path of the report; null when there is no folder yet, or the report could not be written (a line on stderr says why).</returns>
    public static string? WriteReport(CrashKind kind, Exception? exception)
    {
        string[] lines;
        lock (s_lock)
        {
            lines = s_recent?.Snapshot() ?? [];
        }

        return WriteReport(kind, exception, lines, extraContext: null);
    }

    /// <summary>
    /// The start of a run: the markers that earlier runs left in <paramref name="logDirectory"/> (see <see cref="FileLogSink"/>) whose process is gone are runs that did not end cleanly. Each gets
    /// a report of kind <see cref="CrashKind.UncleanExit"/> with the end of its log, and its marker is removed. A marker of a process that still runs (another instance) is left alone.
    /// </summary>
    /// <returns>The number of reports written.</returns>
    public static int CheckPreviousRun(string logDirectory)
    {
        if (!Directory.Exists(logDirectory))
        {
            return 0;
        }

        int written = 0;
        string[] markers;
        try
        {
            markers = [.. Directory.EnumerateFiles(logDirectory, "*" + FileLogSink.MarkerExtension).Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The log folder {logDirectory} cannot be read: {e.Message}");
            return 0;
        }

        foreach (string marker in markers)
        {
            try
            {
                if (!TryReadMarker(marker, out int pid, out long start, out string log))
                {
                    File.Delete(marker); // not a marker of ours
                    continue;
                }

                if (IsAlive(pid, start))
                {
                    continue;
                }

                string[] lines = ReadTail(Path.Combine(logDirectory, log), PreviousRunLines);
                var previous = new Dictionary<string, string>
                {
                    ["Previous run"] = string.Create(CultureInfo.InvariantCulture, $"{log}, process {pid}"),
                };
                if (WriteReport(CrashKind.UncleanExit, null, lines, previous) is { } path)
                {
                    Log.Warn("Crash", $"The previous run ({log}) did not end cleanly. A report was written to {path}");
                    written++;
                }

                File.Delete(marker);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"The marker {marker} could not be handled: {e.Message}");
            }
        }

        return written;
    }

    private static string? WriteReport(CrashKind kind, Exception? exception, string[] lines, Dictionary<string, string>? extraContext)
    {
        string? directory;
        Dictionary<string, string> context;
        lock (s_lock)
        {
            directory = s_reportDirectory;
            context = new Dictionary<string, string>(s_context);
        }

        if (directory is null)
        {
            return null;
        }

        if (extraContext is not null)
        {
            foreach (KeyValuePair<string, string> pair in extraContext)
            {
                context[pair.Key] = pair.Value;
            }
        }

        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string text = CrashReport.Build(kind, exception, lines, context, now);
            Directory.CreateDirectory(directory);
            string stem = string.Create(CultureInfo.InvariantCulture, $"crash-{now:yyyyMMdd-HHmmss-fff}-{KindName(kind)}");
            string path = "";
            for (int attempt = 0; attempt < 100; attempt++)
            {
                path = Path.Combine(directory, attempt == 0 ? stem + ".txt" : $"{stem}-{attempt.ToString("00", CultureInfo.InvariantCulture)}.txt");
                if (!File.Exists(path))
                {
                    break;
                }
            }

            File.WriteAllText(path, text);
            DeleteOldest(directory);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"A crash report could not be written to {directory}: {e.Message}");
            return null;
        }
    }

    private static string KindName(CrashKind kind) => kind switch
    {
        CrashKind.UnhandledException => "unhandled",
        CrashKind.UnobservedTask => "task",
        _ => "unclean-exit",
    };

    private static void DeleteOldest(string directory)
    {
        // crash-<UTC date>-...: the order of the names is the order of the reports.
        string[] reports = [.. Directory.EnumerateFiles(directory, "crash-*.txt").OrderByDescending(static p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal)];
        for (int i = KeptReports; i < reports.Length; i++)
        {
            File.Delete(reports[i]);
        }
    }

    internal static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception exception = e.ExceptionObject as Exception ?? new InvalidOperationException("A non-exception object was thrown: " + e.ExceptionObject);
        string[] lines;
        lock (s_lock)
        {
            lines = s_recent?.Snapshot() ?? [];
        }

        Log.Error("Crash", "Unhandled exception: " + exception);
        string? path = WriteReport(CrashKind.UnhandledException, exception, lines, extraContext: null);
        if (path is not null)
        {
            Log.Error("Crash", "A crash report was written to " + path);
            Console.Error.WriteLine("A crash report was written to " + path);
        }

        if (e.IsTerminating)
        {
            IDisposable? logFile;
            lock (s_lock)
            {
                logFile = s_logSink;
            }

            logFile?.Dispose(); // the crash is reported: the next start has nothing to find
        }
    }

    internal static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        string[] lines;
        bool first;
        lock (s_lock)
        {
            lines = s_recent?.Snapshot() ?? [];
            first = !s_unobservedReported;
            s_unobservedReported = true;
        }

        Log.Error("Crash", "Unobserved task exception: " + e.Exception);
        if (first && WriteReport(CrashKind.UnobservedTask, e.Exception, lines, extraContext: null) is { } path)
        {
            Log.Error("Crash", "A report was written to " + path + " (the program goes on; further unobserved task exceptions are only logged)");
        }
    }

    private static bool TryReadMarker(string path, out int pid, out long start, out string log)
    {
        pid = 0;
        start = 0;
        log = null!;
        foreach (string line in File.ReadLines(path))
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                continue;
            }

            string value = line[(equals + 1)..];
            switch (line[..equals])
            {
                case "pid":
                    _ = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out pid);
                    break;
                case "start":
                    _ = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out start);
                    break;
                case "log":
                    log = Path.GetFileName(value); // never a path outside the folder
                    break;
            }
        }

        return pid > 0 && log is not null;
    }

    private static bool IsAlive(int pid, long startTicks)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            // A pid is reused after a while (after a reboot, at once): it is the same run only if the process started when the marker says.
            return startTicks == 0 || Math.Abs(process.StartTime.ToUniversalTime().Ticks - startTicks) < 5 * TimeSpan.TicksPerSecond;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true; // another user's process that cannot be asked: not ours to call dead
        }
    }

    private static string[] ReadTail(string logPath, int count)
    {
        if (!File.Exists(logPath))
        {
            return [];
        }

        var tail = new Queue<string>(count);
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (tail.Count == count)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
        }

        return [.. tail];
    }
}
