using System;
using System.Globalization;

namespace Apogee.Core.Logging;

public readonly record struct LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message)
{
    /// <summary>The line every text sink writes: <c>HH:mm:ss.fff LVL [Category] message</c>, in the machine's local time.</summary>
    public string ToLine() => string.Create(CultureInfo.InvariantCulture, $"{Time:HH:mm:ss.fff} {Abbreviation(Level)} [{Category}] {Message}");

    private static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        _ => "ERR",
    };
}
