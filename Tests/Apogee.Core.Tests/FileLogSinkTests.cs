using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apogee.Core.Logging;
using Xunit;

namespace Apogee.Core.Tests;

// FileLogSink.Dispose takes itself out of Log, which is process-wide state.
[Collection("Log")]
public sealed class FileLogSinkTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("apogee-log-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static LogEntry Entry(string message, LogLevel level = LogLevel.Information) => new(new DateTimeOffset(2026, 11, 15, 12, 30, 5, 42, TimeSpan.Zero), level, "Test", message);

    // The file is read while the sink may still have it open: a reader has to share it with the writer (File.ReadAllText asks for FileShare.Read, which Windows refuses next to an open writer).
    private static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new System.Collections.Generic.List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    private string[] Logs() => [.. Directory.GetFiles(_dir.FullName, "*.log").Order(StringComparer.Ordinal)];

    [Fact]
    public void WritesEveryLineWithTheSharedFormat()
    {
        using var sink = new FileLogSink(_dir.FullName, "Fixture");
        LogEntry entry = Entry("hello");
        sink.Write(entry);
        sink.Write(Entry("careful", LogLevel.Warning));

        // Flushed after each line: the file can be read while the sink is open.
        string[] lines = ReadLines(sink.Path!);
        Assert.Contains("Fixture", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Command line: ", lines[1], StringComparison.Ordinal);
        Assert.Equal([entry.ToLine(), Entry("careful", LogLevel.Warning).ToLine()], lines[2..]);
        Assert.Equal("12:30:05.042 INF [Test] hello", entry.ToLine());
    }

    [Fact]
    public void TheFileIsNamedAfterTheRunAndHasAMarkerWhileItRuns()
    {
        string name;
        using (var sink = new FileLogSink(_dir.FullName, "Fixture"))
        {
            name = System.IO.Path.GetFileNameWithoutExtension(sink.Path!);
            Assert.Matches($@"^\d{{8}}-\d{{6}}-{Environment.ProcessId}$", name);
            Assert.True(File.Exists(System.IO.Path.Combine(_dir.FullName, name + FileLogSink.MarkerExtension)));
        }

        Assert.False(File.Exists(System.IO.Path.Combine(_dir.FullName, name + FileLogSink.MarkerExtension)));
    }

    [Fact]
    public void KeepsOnlyTheNewestFiles()
    {
        for (int i = 1; i <= 12; i++)
        {
            File.WriteAllText(System.IO.Path.Combine(_dir.FullName, $"2020{i:00}01-000000-1.log"), "old");
        }

        using var sink = new FileLogSink(_dir.FullName, "Fixture");

        string[] logs = Logs();
        Assert.Equal(10, logs.Length);
        Assert.Contains(sink.Path, logs);
        // The three oldest of the twelve went (nine stay with the new one): the names order like the dates.
        Assert.DoesNotContain(logs, static p => p.Contains("20200101", StringComparison.Ordinal) || p.Contains("20200201", StringComparison.Ordinal) || p.Contains("20200301", StringComparison.Ordinal));
        Assert.Contains(logs, static p => p.Contains("20200401", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoSinksInTheSameSecondGetFilesOfTheirOwn()
    {
        using var first = new FileLogSink(_dir.FullName, "One");
        using var second = new FileLogSink(_dir.FullName, "Two");

        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(2, Logs().Length);
    }

    [Fact]
    public void TruncatesPastMaxBytesWithOneLine()
    {
        using var sink = new FileLogSink(_dir.FullName, "Fixture", maxBytes: 20_000);
        for (int i = 0; i < 100; i++)
        {
            sink.Write(Entry(new string('x', 1000)));
        }

        string[] lines = ReadLines(sink.Path!);
        Assert.Single(lines, static l => l.StartsWith("Log truncated", StringComparison.Ordinal));
        Assert.StartsWith("Log truncated", lines[^1], StringComparison.Ordinal);
        Assert.True(new FileInfo(sink.Path!).Length < 20_000 + 1100 + 100); // the line that crossed the limit, and the last line
    }

    [Fact]
    public void WritesFromManyThreadsWithoutLosingLines()
    {
        using var sink = new FileLogSink(_dir.FullName, "Fixture");
        Parallel.For(0, 8, thread =>
        {
            for (int i = 0; i < 250; i++)
            {
                sink.Write(Entry($"thread {thread} line {i}"));
            }
        });

        string[] lines = ReadLines(sink.Path!);
        Assert.Equal(2000, lines.Count(static l => l.Contains("] thread ", StringComparison.Ordinal)));
        Assert.Equal(2000, lines.Distinct().Count(static l => l.Contains("] thread ", StringComparison.Ordinal)));
    }

    [Fact]
    public void DisposeWritesEndOfRunAndRemovesTheMarker()
    {
        var sink = new FileLogSink(_dir.FullName, "Fixture");
        sink.Write(Entry("last"));
        sink.Dispose();
        sink.Dispose(); // safe again
        sink.Write(Entry("after")); // ignored

        string[] lines = ReadLines(sink.Path!);
        Assert.Equal("End of run.", lines[^1]);
        Assert.DoesNotContain(lines, static l => l.EndsWith("after", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_dir.FullName, "*" + FileLogSink.MarkerExtension));
        Assert.False(sink.IsActive);
    }

    [Fact]
    public void AnIoErrorDisablesTheSinkWithoutThrowing()
    {
        // The "folder" is a file, so no log can be opened there.
        string notAFolder = System.IO.Path.Combine(_dir.FullName, "file");
        File.WriteAllText(notAFolder, "");
        TextWriter previous = Console.Error;
        var error = new StringWriter();
        Console.SetError(error);
        try
        {
            using var sink = new FileLogSink(System.IO.Path.Combine(notAFolder, "logs"), "Fixture");
            sink.Write(Entry("nowhere"));

            Assert.False(sink.IsActive);
            Assert.Null(sink.Path);
            Assert.Contains("no log file", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Fact]
    public void ADisposedSinkLeavesTheLog()
    {
        var sink = new FileLogSink(_dir.FullName, "Fixture");
        Log.AddSink(sink);
        sink.Dispose();

        // Logging after that must not reach (or throw from) the closed file.
        Log.Info("Test", "after the end");
        Assert.DoesNotContain("after the end", ReadText(sink.Path!), StringComparison.Ordinal);
    }
}
