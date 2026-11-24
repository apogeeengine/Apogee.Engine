using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Weft.Core.Diagnostics;

/// <summary>What a crash report is about.</summary>
public enum CrashKind
{
    /// <summary>An exception that nothing caught ended the process.</summary>
    UnhandledException,

    /// <summary>A task failed and nobody looked at its exception. The process goes on.</summary>
    UnobservedTask,

    /// <summary>The previous run left its marker behind: it did not end cleanly (a crash in native code, or it was killed).</summary>
    UncleanExit,
}

/// <summary>The text of a crash report. <see cref="Build"/> is a pure function; <see cref="CrashHandler"/> decides when to write one and where.</summary>
public static class CrashReport
{
    /// <summary>The version of the engine: the informational version of <c>Weft.Core</c>, or <c>unknown</c>.</summary>
    public static string EngineVersion { get; } = typeof(CrashReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>
    /// The plain text of a report: the kind and the time (UTC), the engine and runtime versions, the machine, <paramref name="context"/> (the game, its version, the GPU, ...), the thread, the exception
    /// with its inner ones, and <paramref name="lastLines"/> of the log.
    /// </summary>
    /// <param name="exception">Null for an <see cref="CrashKind.UncleanExit"/>, which has no exception to show.</param>
    public static string Build(CrashKind kind, Exception? exception, IReadOnlyList<string> lastLines, IReadOnlyDictionary<string, string> context, DateTimeOffset time)
    {
        var text = new StringBuilder();
        text.Append("Weft crash report: ").AppendLine(Describe(kind));
        text.Append("Time (UTC): ").AppendLine(time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        text.Append("Engine: ").AppendLine(EngineVersion);
        foreach (KeyValuePair<string, string> pair in context)
        {
            text.Append(pair.Key).Append(": ").AppendLine(pair.Value);
        }

        text.Append(".NET: ").Append(RuntimeInformation.FrameworkDescription).AppendLine(RuntimeFeature.IsDynamicCodeSupported ? " (JIT)" : " (NativeAOT)");
        text.Append("OS: ").Append(RuntimeInformation.OSDescription).Append(' ').Append(RuntimeInformation.OSArchitecture).AppendLine();
        text.Append("Process: ").Append(RuntimeInformation.ProcessArchitecture).Append(", ").Append(Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)).Append(" processors, ")
            .Append((GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)).AppendLine(" MiB of memory");
        Thread thread = Thread.CurrentThread;
        text.Append("Thread: ").Append(thread.Name ?? "(unnamed)").Append(" #").AppendLine(thread.ManagedThreadId.ToString(CultureInfo.InvariantCulture));
        if (exception is not null)
        {
            text.AppendLine().AppendLine("Exception:").AppendLine(exception.ToString());
        }

        text.AppendLine().Append("Last ").Append(lastLines.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" log lines:");
        foreach (string line in lastLines)
        {
            text.AppendLine(line);
        }

        return text.ToString();
    }

    private static string Describe(CrashKind kind) => kind switch
    {
        CrashKind.UnhandledException => "an unhandled exception ended the program",
        CrashKind.UnobservedTask => "a task failed and nobody looked at its exception",
        _ => "the previous run did not end cleanly (a native crash, or it was killed)",
    };
}
