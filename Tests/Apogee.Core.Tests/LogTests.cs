using System.Collections.Generic;
using Apogee.Core.Logging;
using Xunit;

namespace Apogee.Core.Tests;

// Log is process-wide state, so these tests must not run in parallel with each other.
[Collection("Log")]
public class LogTests
{
    private sealed class ListSink : ILogSink
    {
        public List<LogEntry> Entries { get; } = [];

        public void Write(in LogEntry entry)
        {
            lock (Entries)
            {
                Entries.Add(entry);
            }
        }
    }

    [Fact]
    public void EntriesReachSinksAtOrAboveTheMinimumLevel()
    {
        var sink = new ListSink();
        Log.AddSink(sink);
        LogLevel previous = Log.MinimumLevel;
        try
        {
            Log.MinimumLevel = LogLevel.Warning;

            Log.Info("Test", "dropped");
            Log.Warn("Test", "kept");
            Log.Error("Test", "kept too");

            Assert.Equal(["kept", "kept too"], sink.Entries.ConvertAll(e => e.Message));
            Assert.Equal(LogLevel.Error, sink.Entries[1].Level);
            Assert.Equal("Test", sink.Entries[0].Category);
        }
        finally
        {
            Log.MinimumLevel = previous;
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    public void RemovedSinksStopReceiving()
    {
        var sink = new ListSink();
        Log.AddSink(sink);
        Log.RemoveSink(sink);

        Log.Error("Test", "nobody listens");

        Assert.Empty(sink.Entries);
    }
}
