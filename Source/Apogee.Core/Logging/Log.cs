using System;
using System.Collections.Immutable;
using System.Threading;

namespace Apogee.Core.Logging;

/// <summary>The engine's log. Entries below <see cref="MinimumLevel"/> are dropped before they reach a sink.</summary>
public static class Log
{
    private static ImmutableArray<ILogSink> s_sinks = [];
    private static int s_minimumLevel = (int)LogLevel.Information;

    public static LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref s_minimumLevel);
        set => Volatile.Write(ref s_minimumLevel, (int)value);
    }

    public static void AddSink(ILogSink sink) => ImmutableInterlocked.Update(ref s_sinks, static (sinks, added) => sinks.Add(added), sink);

    public static void RemoveSink(ILogSink sink) => ImmutableInterlocked.Update(ref s_sinks, static (sinks, removed) => sinks.Remove(removed), sink);

    public static bool IsEnabled(LogLevel level) => level >= MinimumLevel;

    public static void Write(LogLevel level, string category, string message)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var entry = new LogEntry(DateTimeOffset.Now, level, category, message);
        foreach (ILogSink sink in s_sinks)
        {
            sink.Write(entry);
        }
    }

    public static void Trace(string category, string message) => Write(LogLevel.Trace, category, message);

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message);

    public static void Info(string category, string message) => Write(LogLevel.Information, category, message);

    public static void Warn(string category, string message) => Write(LogLevel.Warning, category, message);

    public static void Error(string category, string message) => Write(LogLevel.Error, category, message);
}
