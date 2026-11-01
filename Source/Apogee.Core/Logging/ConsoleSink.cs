using System;
using System.Globalization;
using System.Threading;

namespace Apogee.Core.Logging;

/// <summary>Writes entries to the console: errors and warnings to stderr, the rest to stdout.</summary>
public sealed class ConsoleSink : ILogSink
{
    private static readonly Lock s_lock = new();

    public void Write(in LogEntry entry)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{entry.Time:HH:mm:ss.fff} {Abbreviation(entry.Level)} [{entry.Category}] {entry.Message}");
        lock (s_lock)
        {
            (entry.Level >= LogLevel.Warning ? Console.Error : Console.Out).WriteLine(line);
        }
    }

    private static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        _ => "ERR",
    };
}
