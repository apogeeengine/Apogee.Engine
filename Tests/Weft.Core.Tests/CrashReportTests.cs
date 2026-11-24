using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Weft.Core.Diagnostics;
using Weft.Core.Logging;
using Xunit;

namespace Weft.Core.Tests;

// CrashHandler and Log are process-wide state.
[Collection("Log")]
public sealed class CrashReportTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("weft-crash-");
    private readonly RecentLogSink _recent = new(50);

    public CrashReportTests()
    {
        CrashHandler.Install(Path.Combine(_dir.FullName, "crashes"), _recent);
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private string[] Reports() => Directory.Exists(Path.Combine(_dir.FullName, "crashes")) ? [.. Directory.GetFiles(Path.Combine(_dir.FullName, "crashes"), "crash-*.txt").OrderBy(static p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal)] : [];

    private static LogEntry Entry(string message) => new(DateTimeOffset.Now, LogLevel.Information, "Test", message);

    [Fact]
    public void TheReportHasVersionOsContextExceptionAndLastLines()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("outer", new ArgumentException("inner cause"));
        }
        catch (InvalidOperationException e)
        {
            exception = e;
        }

        string text = CrashReport.Build(
            CrashKind.UnhandledException,
            exception,
            ["12:00:00.000 INF [A] first", "12:00:01.000 ERR [B] second"],
            new Dictionary<string, string> { ["Game"] = "Fixture", ["GPU"] = "Test GPU" },
            new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.FromHours(1)));

        Assert.Contains("unhandled exception", text, StringComparison.Ordinal);
        Assert.Contains("Time (UTC): 2026-11-15 09:00:00Z", text, StringComparison.Ordinal);
        Assert.Contains("Engine: " + CrashReport.EngineVersion, text, StringComparison.Ordinal);
        Assert.Contains("Game: Fixture", text, StringComparison.Ordinal);
        Assert.Contains("GPU: Test GPU", text, StringComparison.Ordinal);
        Assert.Contains(".NET: ", text, StringComparison.Ordinal);
        Assert.Contains("OS: ", text, StringComparison.Ordinal);
        Assert.Contains("Thread: ", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: outer", text, StringComparison.Ordinal);
        Assert.Contains("ArgumentException: inner cause", text, StringComparison.Ordinal);
        Assert.Contains("Last 2 log lines:", text, StringComparison.Ordinal);
        Assert.EndsWith("12:00:01.000 ERR [B] second" + Environment.NewLine, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUncleanExitReportHasNoExceptionSection()
    {
        string text = CrashReport.Build(CrashKind.UncleanExit, null, [], new Dictionary<string, string>(), DateTimeOffset.UtcNow);

        Assert.Contains("did not end cleanly", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentLogSinkKeepsTheLastNLines()
    {
        var recent = new RecentLogSink(3);
        Assert.Empty(recent.Snapshot());

        for (int i = 1; i <= 5; i++)
        {
            recent.Write(Entry("line " + i.ToString(CultureInfo.InvariantCulture)));
        }

        string[] lines = recent.Snapshot();
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("line 3", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("line 5", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void WriteReportUsesTheRecentLinesAndTheContext()
    {
        _recent.Write(Entry("before the crash"));
        CrashHandler.SetContext("Game", "Fixture");

        string? path = CrashHandler.WriteReport(CrashKind.UnhandledException, new InvalidOperationException("boom"));

        Assert.NotNull(path);
        string text = File.ReadAllText(path);
        Assert.Contains("before the crash", text, StringComparison.Ordinal);
        Assert.Contains("Game: Fixture", text, StringComparison.Ordinal);
        Assert.Contains("boom", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteReportKeepsTheNewestTwentyAndNeverThrows()
    {
        for (int i = 0; i < 25; i++)
        {
            Assert.NotNull(CrashHandler.WriteReport(CrashKind.UnobservedTask, new InvalidOperationException("n" + i.ToString(CultureInfo.InvariantCulture))));
        }

        string[] reports = Reports();
        Assert.Equal(20, reports.Length);
        Assert.Contains("n24", File.ReadAllText(reports[^1]), StringComparison.Ordinal);

        // The folder cannot be made: it would have to be inside a file.
        string file = Path.Combine(_dir.FullName, "file");
        File.WriteAllText(file, "");
        CrashHandler.SetReportDirectory(Path.Combine(file, "crashes"));
        TextWriter previous = Console.Error;
        Console.SetError(new StringWriter());
        try
        {
            Assert.Null(CrashHandler.WriteReport(CrashKind.UnhandledException, new InvalidOperationException("x")));
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    [Fact]
    public void AnUnhandledExceptionWritesAReportAndClosesTheLogFile()
    {
        var logFile = new FakeLogFile();
        CrashHandler.SetLogFile(logFile);
        try
        {
            CrashHandler.OnUnhandledException(this, new UnhandledExceptionEventArgs(new InvalidOperationException("fatal"), isTerminating: false));
            Assert.False(logFile.Disposed); // the program goes on: the log stays open
            Assert.Single(Reports());

            CrashHandler.OnUnhandledException(this, new UnhandledExceptionEventArgs(new InvalidOperationException("fatal"), isTerminating: true));
            Assert.True(logFile.Disposed);
            Assert.Equal(2, Reports().Length);
            Assert.Contains("fatal", File.ReadAllText(Reports()[0]), StringComparison.Ordinal);
        }
        finally
        {
            CrashHandler.SetLogFile(null);
        }
    }

    [Fact]
    public void AStaleMarkerFromADeadPidMakesAnUncleanExitReportWithThatLogsTail()
    {
        string logs = Path.Combine(_dir.FullName, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllLines(Path.Combine(logs, "20260101-000000-5.log"), Enumerable.Range(1, 300).Select(static i => "line " + i.ToString(CultureInfo.InvariantCulture)));
        File.WriteAllText(Path.Combine(logs, "20260101-000000-5" + FileLogSink.MarkerExtension), $"pid={DeadPid()}\nstart=0\nlog=20260101-000000-5.log\n");

        Assert.Equal(1, CrashHandler.CheckPreviousRun(logs));

        string text = File.ReadAllText(Assert.Single(Reports()));
        Assert.Contains("did not end cleanly", text, StringComparison.Ordinal);
        Assert.Contains("Previous run: 20260101-000000-5.log", text, StringComparison.Ordinal);
        Assert.Contains("Last 200 log lines:", text, StringComparison.Ordinal);
        Assert.Contains("line 300", text, StringComparison.Ordinal);
        Assert.Contains("line 101", text, StringComparison.Ordinal);
        Assert.DoesNotContain("line 100" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(logs, "*" + FileLogSink.MarkerExtension)); // reported once
        Assert.Equal(0, CrashHandler.CheckPreviousRun(logs));
    }

    [Fact]
    public void AMarkerOfALiveProcessIsNotACrash()
    {
        string logs = Path.Combine(_dir.FullName, "logs");

        // The marker of this very process, as a running sink writes it (with its start time).
        using var sink = new FileLogSink(logs, "Fixture");
        Assert.Single(Directory.GetFiles(logs, "*" + FileLogSink.MarkerExtension));

        Assert.Equal(0, CrashHandler.CheckPreviousRun(logs));
        Assert.Empty(Reports());
        Assert.Single(Directory.GetFiles(logs, "*" + FileLogSink.MarkerExtension));
    }

    [Fact]
    public void AMarkerWhosePidWasReusedByAnotherProcessIsStale()
    {
        string logs = Path.Combine(_dir.FullName, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "a.log"), "the end");
        long longAgo = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        File.WriteAllText(Path.Combine(logs, "a" + FileLogSink.MarkerExtension), $"pid={Environment.ProcessId}\nstart={longAgo}\nlog=a.log\n");

        Assert.Equal(1, CrashHandler.CheckPreviousRun(logs));
    }

    [Fact]
    public void AMarkerThatIsNotOursIsRemoved()
    {
        string logs = Path.Combine(_dir.FullName, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "junk" + FileLogSink.MarkerExtension), "nonsense");

        Assert.Equal(0, CrashHandler.CheckPreviousRun(logs));
        Assert.Empty(Directory.GetFiles(logs));
        Assert.Equal(0, CrashHandler.CheckPreviousRun(Path.Combine(logs, "missing")));
    }

    [Fact]
    public void UnobservedTaskExceptionsAreLoggedAndReportedOnce()
    {
        var sink = new RecentLogSink();
        Log.AddSink(sink);
        try
        {
            CrashHandler.OnUnobservedTaskException(this, new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("first"))));
            CrashHandler.OnUnobservedTaskException(this, new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("second"))));

            string text = File.ReadAllText(Assert.Single(Reports()));
            Assert.Contains("first", text, StringComparison.Ordinal);
            Assert.Contains(sink.Snapshot(), static l => l.Contains("Unobserved task exception", StringComparison.Ordinal) && l.Contains("second", StringComparison.Ordinal));
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    public void AFaultedTaskThatNobodyAwaitedReachesTheHandler()
    {
        FaultAndForget();

        // The task's finalizer raises the event: nudge the collector until it has run.
        for (int i = 0; i < 100 && Reports().Length == 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(20);
        }

        Assert.SkipWhen(Reports().Length == 0, "the finalizer of the task did not run in time on this machine");
        Assert.Contains("unobserved by the test", File.ReadAllText(Reports()[0]), StringComparison.Ordinal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FaultAndForget()
    {
        Task task = Task.Run(static () => throw new InvalidOperationException("unobserved by the test"));
        while (!task.IsFaulted)
        {
            Thread.Sleep(1);
        }
    }

    private static int DeadPid()
    {
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd", "/c exit") : new ProcessStartInfo("sh", "-c true");
        start.CreateNoWindow = true;
        using Process child = Process.Start(start)!;
        child.WaitForExit();
        return child.Id;
    }

    private sealed class FakeLogFile : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
