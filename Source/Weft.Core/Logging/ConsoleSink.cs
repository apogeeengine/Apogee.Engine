using System;
using System.Threading;

namespace Weft.Core.Logging;

/// <summary>Writes entries to the console: errors and warnings to stderr, the rest to stdout.</summary>
public sealed class ConsoleSink : ILogSink
{
    private static readonly Lock s_lock = new();

    public void Write(in LogEntry entry)
    {
        string line = entry.ToLine();
        lock (s_lock)
        {
            (entry.Level >= LogLevel.Warning ? Console.Error : Console.Out).WriteLine(line);
        }
    }
}
