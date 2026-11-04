using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Apogee.Reflection;

namespace Apogee.Scripting;

public sealed record TestFailure(string Name, string Message);

/// <param name="Error">Set when the file itself failed (it did not compile, raised an error outside a case, or never finished).</param>
public sealed record TestFileResult(string File, int Passed, int Failed, int Skipped, string? Error, IReadOnlyList<TestFailure> Failures)
{
    public bool Ok => Error is null && Failed == 0;
}

public sealed record TestRunResult(IReadOnlyList<TestFileResult> Files, TimeSpan Elapsed)
{
    public int Passed => Files.Sum(f => f.Passed);

    public int Failed => Files.Sum(f => f.Failed) + Files.Count(f => f.Error is not null);

    public int Skipped => Files.Sum(f => f.Skipped);

    /// <summary>True when something ran and nothing failed: a folder with no tests is not a pass.</summary>
    public bool Success => Failed == 0 && Passed > 0;
}

/// <summary>
/// Runs the <c>*.test.luau</c> files under a folder, each in a VM of its own (so one file's globals, modules and worlds cannot touch
/// another's). A file's <c>describe</c> bodies run as it loads; its cases then run in order, one frame at a time, with <c>@engine/test</c>.
/// </summary>
public static class LuauTestRunner
{
    /// <summary>A case that waits for more frames than this has hung.</summary>
    private const int MaxFrames = 100_000;

    public static TestRunResult Run(string directory, string? filter = null, TextWriter? output = null, int timeBudgetMilliseconds = 10_000)
    {
        output ??= TextWriter.Null;
        var watch = Stopwatch.StartNew();
        string root = Path.GetFullPath(directory).Replace('\\', '/').TrimEnd('/');

        var files = new List<TestFileResult>();
        foreach (string relative in Discover(root))
        {
            TestFileResult result = RunFile(root, relative, filter, output, timeBudgetMilliseconds);
            files.Add(result);
            Report(output, result);
        }

        var run = new TestRunResult(files, watch.Elapsed);
        if (files.Count == 0)
        {
            output.WriteLine($"No *.test.luau files found under {directory}.");
        }

        string skipped = run.Skipped > 0 ? $", {run.Skipped} not matching \"{filter}\"" : "";
        output.WriteLine();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Luau tests: {run.Passed} passed, {run.Failed} failed{skipped}, in {files.Count} files ({run.Elapsed.TotalSeconds:0.00} s)."));
        return run;
    }

    /// <summary>The test files by path relative to the folder, in order; folders that start with a dot are skipped.</summary>
    public static IReadOnlyList<string> Discover(string directory)
    {
        string root = Path.GetFullPath(directory).Replace('\\', '/').TrimEnd('/');
        var found = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*.test.luau", SearchOption.AllDirectories))
        {
            string relative = path.Replace('\\', '/')[(root.Length + 1)..];
            if (!relative.Split('/')[..^1].Any(segment => segment.StartsWith('.')))
            {
                found.Add(relative);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static TestFileResult RunFile(string root, string relative, string? filter, TextWriter output, int timeBudgetMilliseconds)
    {
        string path = root + "/" + relative;
        try
        {
            using var vm = new LuauVm(new LuauVmOptions
            {
                TimeBudgetMilliseconds = timeBudgetMilliseconds,
                Output = (owner, text, warning) => output.WriteLine(warning ? "warning: " + text : text),
            });
            vm.AddRoot(root);
            ScriptOwner owner = vm.BaseOwner;

            vm.Execute(owner, "=runner", $"require('@engine/test').reset({(filter is null ? "nil" : Quote(filter))})");
            vm.Execute(owner, "@" + path, File.ReadAllText(path, Encoding.UTF8));

            for (int frame = 0; ; frame++)
            {
                if (vm.Execute(owner, "=step", "return require('@engine/test').step()")[0].ToBool())
                {
                    break;
                }

                if (frame >= MaxFrames)
                {
                    return new TestFileResult(relative, 0, 0, 0, $"a test case waited for more than {MaxFrames} frames", []);
                }
            }

            IReadOnlyList<Value> summary = vm.Execute(owner, "=summary", "return require('@engine/test').summary()");
            IReadOnlyList<Value> names = summary[3].ToList();
            IReadOnlyList<Value> messages = summary[4].ToList();
            var failures = new List<TestFailure>();
            for (int i = 0; i < names.Count; i++)
            {
                failures.Add(new TestFailure(names[i].ToStringValue(), messages[i].ToStringValue()));
            }

            return new TestFileResult(relative, summary[0].ToInt32(), summary[1].ToInt32(), summary[2].ToInt32(), null, failures);
        }
        catch (ScriptException e)
        {
            return new TestFileResult(relative, 0, 0, 0, e.Message, []);
        }
        catch (ValueException e)
        {
            return new TestFileResult(relative, 0, 0, 0, e.Message, []);
        }
    }

    private static void Report(TextWriter output, TestFileResult result)
    {
        if (result.Error is not null)
        {
            output.WriteLine($"FAIL {result.File}");
            output.WriteLine($"  the file raised an error: {result.Error}");
            return;
        }

        if (result.Failed == 0)
        {
            output.WriteLine($"ok   {result.File} ({result.Passed} passed)");
            return;
        }

        output.WriteLine($"FAIL {result.File} ({result.Passed} passed, {result.Failed} failed)");
        foreach (TestFailure failure in result.Failures)
        {
            output.WriteLine($"  - {failure.Name}");
            foreach (string line in failure.Message.Split('\n'))
            {
                output.WriteLine($"    {line}");
            }
        }
    }

    private static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}
