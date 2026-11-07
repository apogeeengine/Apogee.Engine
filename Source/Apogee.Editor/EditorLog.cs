using System;
using System.Collections.Generic;
using Apogee.Core.Logging;

namespace Apogee.Editor;

/// <summary>Keeps the last log entries for the console panel and prints them to the terminal too.</summary>
public sealed class EditorLog : ILogSink
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _entries = [];

    /// <summary>How many entries are kept; the oldest go first.</summary>
    public int Capacity { get; set; } = 2000;

    /// <summary>Bumped on every entry, so a panel knows to scroll.</summary>
    public long Version { get; private set; }

    /// <summary>Whether entries are also written to the terminal.</summary>
    public bool EchoToConsole { get; set; } = true;

    public void Write(in LogEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity)
            {
                _entries.RemoveRange(0, _entries.Count - Capacity);
            }

            Version++;
        }

        if (EchoToConsole)
        {
            Console.Error.WriteLine($"{entry.Time:HH:mm:ss.fff} {entry.Level.ToString()[..3].ToUpperInvariant()} [{entry.Category}] {entry.Message}");
        }
    }

    /// <summary>A copy of the entries at or above <paramref name="minimum"/>, oldest first.</summary>
    public List<LogEntry> Entries(LogLevel minimum = LogLevel.Trace)
    {
        lock (_lock)
        {
            return _entries.FindAll(e => e.Level >= minimum);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            Version++;
        }
    }
}
