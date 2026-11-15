using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Apogee.Core.Diagnostics;

namespace Apogee.Core.Logging;

/// <summary>
/// Writes each entry as a line of a file that is this run's own: <c>&lt;yyyyMMdd-HHmmss&gt;-&lt;pid&gt;.log</c> (UTC) in a folder that keeps the newest few. The file is flushed after every line, so
/// that a crash keeps the last ones. Next to it, a <c>.running</c> marker (the process id, when it started and the log's name) lives until <see cref="Dispose"/>: a marker whose process is gone is
/// a run that did not end cleanly (<see cref="CrashHandler.CheckPreviousRun"/>). The sink never throws: a file that cannot be written disables it, with one line on stderr.
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    /// <summary>The extension of a log file.</summary>
    public const string LogExtension = ".log";

    /// <summary>The extension of the marker of a run that has not ended.</summary>
    public const string MarkerExtension = ".running";

    private readonly Lock _lock = new();
    private readonly long _maxBytes;
    private StreamWriter? _writer;
    private string? _markerPath;
    private long _bytes;
    private bool _truncated;

    /// <summary>Opens a new log file in <paramref name="directory"/> (made if it is missing) and removes the oldest ones, so that <paramref name="keepFiles"/> remain with the new one.</summary>
    /// <param name="runName">Named in the first line of the file, with the engine version, the OS and the command line.</param>
    /// <param name="maxBytes">The size after which the file gets one last line and nothing more.</param>
    public FileLogSink(string directory, string runName, int keepFiles = 10, long maxBytes = 64L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keepFiles, 1);
        _maxBytes = maxBytes;
        try
        {
            Directory.CreateDirectory(directory);
            DeleteOldest(directory, keepFiles - 1);
            Open(directory);
            WriteLine(string.Create(CultureInfo.InvariantCulture, $"Apogee {CrashReport.EngineVersion}, {runName}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, pid {Environment.ProcessId}"));
            WriteLine("Command line: " + Environment.CommandLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Disable(e);
        }
    }

    /// <summary>The log file; null when the sink could not open one.</summary>
    public string? Path { get; private set; }

    /// <summary>False once the file could not be opened or written: nothing is logged any more.</summary>
    public bool IsActive => Volatile.Read(ref _writer) is not null;

    public void Write(in LogEntry entry)
    {
        string line = entry.ToLine();
        lock (_lock)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                if (_truncated)
                {
                    return;
                }

                if (_bytes >= _maxBytes)
                {
                    _truncated = true;
                    WriteLine($"Log truncated: the file is over {_maxBytes} bytes.");
                    return;
                }

                WriteLine(line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Disable(e);
            }
        }
    }

    /// <summary>Writes the last line of the run, closes the file and removes the marker. Safe to call again.</summary>
    public void Dispose()
    {
        Log.RemoveSink(this);
        lock (_lock)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                WriteLine("End of run.");
                _writer.Dispose();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Console.Error.WriteLine($"The log {Path} could not be closed: {e.Message}");
            }

            _writer = null;
            DeleteMarker();
        }
    }

    private void Open(string directory)
    {
        string stem = string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
        for (int attempt = 0; ; attempt++)
        {
            // Two sinks of one process in one second (a test, a restarted session) get names of their own.
            string name = attempt == 0 ? stem : stem + "-" + attempt.ToString("00", CultureInfo.InvariantCulture);
            string path = System.IO.Path.Combine(directory, name + LogExtension);
            try
            {
                // Shared for reading, writing and deleting: whoever tails the log, reads it for a report or prunes it while the run goes on must not be locked out (Windows refuses otherwise).
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                Path = path;
                _markerPath = System.IO.Path.Combine(directory, name + MarkerExtension);
                File.WriteAllText(_markerPath, MarkerText(name + LogExtension));
                return;
            }
            catch (IOException) when (File.Exists(path) && attempt < 100)
            {
            }
        }
    }

    private static string MarkerText(string logName)
    {
        long started = 0;
        try
        {
            using Process self = Process.GetCurrentProcess();
            started = self.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // an unknown start time (0) only makes the check of a dead run a little less sure
        }

        return string.Create(CultureInfo.InvariantCulture, $"pid={Environment.ProcessId}\nstart={started}\nlog={logName}\n");
    }

    private void WriteLine(string line)
    {
        _writer!.WriteLine(line);
        _bytes += Encoding.UTF8.GetByteCount(line) + 1;
    }

    private void DeleteMarker()
    {
        if (_markerPath is null)
        {
            return;
        }

        try
        {
            File.Delete(_markerPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The marker {_markerPath} could not be removed: {e.Message}");
        }

        _markerPath = null;
    }

    private void Disable(Exception e)
    {
        Console.Error.WriteLine($"The log file {Path ?? "(none)"} cannot be written, so there is no log file for this run: {e.Message}");
        try
        {
            _writer?.Dispose();
        }
        catch (Exception inner) when (inner is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // already failing
        }

        _writer = null;
        DeleteMarker();
    }

    private static void DeleteOldest(string directory, int keep)
    {
        // The names start with the UTC date, so the order of the names is the order of the runs.
        string[] logs = [.. Directory.EnumerateFiles(directory, "*" + LogExtension).OrderByDescending(static p => System.IO.Path.GetFileNameWithoutExtension(p),StringComparer.Ordinal)];
        for (int i = keep; i < logs.Length; i++)
        {
            try
            {
                File.Delete(logs[i]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"The old log {logs[i]} could not be removed: {e.Message}");
            }
        }
    }
}
