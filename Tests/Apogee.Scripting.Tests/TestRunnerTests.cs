using System;
using System.IO;
using System.Linq;
using Apogee.Native.Tests;
using Apogee.Scripting;
using Xunit;
using Lua = Apogee.Native.Luau.Lua;

namespace Apogee.Scripting.Tests;

public sealed class TestFolder : IDisposable
{
    public TestFolder()
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        NativeRequirements.Require(typeof(Apogee.Native.Flecs.Ecs).Assembly, "flecs");
        Root = Path.Combine(Path.GetTempPath(), "apogee-tests-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Write(string relative, string source)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a failure.
        }
    }
}

public class TestRunnerTests
{
    private const string Passing = "local test = require('@engine/test')\ntest.describe('d', function() test.it('c', function() test.expect(1).toBe(1) end) end)";

    [Fact]
    public void TheEnginesOwnLuauTestsPass()
    {
        string root = FindRoot();
        var output = new StringWriter();

        TestRunResult run = LuauTestRunner.Run(Path.Combine(root, "Tests", "Luau"), output: output);

        Assert.True(run.Success, output.ToString());
        Assert.True(run.Passed >= 15, output.ToString());
        Assert.Contains("ok   library.test.luau", output.ToString());
    }

    [Fact]
    public void TheScriptingSamplesTestsPass()
    {
        var output = new StringWriter();

        TestRunResult run = LuauTestRunner.Run(Path.Combine(FindRoot(), "Samples", "Scripting"), output: output);

        Assert.True(run.Success, output.ToString());
        Assert.Contains("ok   tests/sim.test.luau", output.ToString());
    }

    private static string FindRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "Apogee.slnx")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException("Apogee.slnx not found above the test output.");
    }

    [Fact]
    public void FilesAreFoundInOrderAndDotFoldersAreSkipped()
    {
        using var folder = new TestFolder();
        folder.Write("b.test.luau", Passing);
        folder.Write("a/z.test.luau", Passing);
        folder.Write("a/y.test.luau", Passing);
        folder.Write(".hidden/skipped.test.luau", Passing);
        folder.Write("a/not_a_test.luau", Passing);

        Assert.Equal(["a/y.test.luau", "a/z.test.luau", "b.test.luau"], LuauTestRunner.Discover(folder.Root));
    }

    [Fact]
    public void APassingFileIsReportedAndTheRunSucceeds()
    {
        using var folder = new TestFolder();
        folder.Write("one.test.luau", Passing);
        var output = new StringWriter();

        TestRunResult run = LuauTestRunner.Run(folder.Root, output: output);

        Assert.True(run.Success);
        Assert.Equal(1, run.Passed);
        Assert.Contains("ok   one.test.luau (1 passed)", output.ToString());
        Assert.Contains("Luau tests: 1 passed, 0 failed, in 1 files (", output.ToString());
    }

    [Fact]
    public void AFailingCaseIsReportedWithItsNameAndMessage()
    {
        using var folder = new TestFolder();
        folder.Write("bad.test.luau", @"
            local test = require('@engine/test')
            test.describe('group', function()
                test.it('passes', function() end)
                test.it('fails', function() test.expect(1).toBe(2) end)
            end)");
        var output = new StringWriter();

        TestRunResult run = LuauTestRunner.Run(folder.Root, output: output);

        Assert.False(run.Success);
        Assert.Equal((1, 1), (run.Passed, run.Failed));
        string text = output.ToString().ReplaceLineEndings("\n");
        Assert.Contains("FAIL bad.test.luau (1 passed, 1 failed)\n  - group > fails\n    ", text);
        Assert.Contains("expected: 1 to be 2", text);
    }

    [Fact]
    public void AFileThatCannotRunIsAFailureOfItsOwn()
    {
        using var folder = new TestFolder();
        folder.Write("syntax.test.luau", "return +");
        folder.Write("raises.test.luau", "error('at load')");
        folder.Write("ok.test.luau", Passing);
        var output = new StringWriter();

        TestRunResult run = LuauTestRunner.Run(folder.Root, output: output);

        Assert.False(run.Success);
        Assert.Equal(2, run.Failed);
        Assert.Equal(1, run.Passed);
        Assert.Contains("the file raised an error: ", output.ToString());
        Assert.Contains("syntax.test.luau:1:", output.ToString());
        Assert.Contains("at load", output.ToString());
    }

    [Fact]
    public void AnErrorOutsideACaseIsReportedAgainstTheDescribe()
    {
        using var folder = new TestFolder();
        folder.Write("outside.test.luau", "local test = require('@engine/test')\ntest.describe('broken', function() error('while collecting') end)");

        TestRunResult run = LuauTestRunner.Run(folder.Root);

        TestFailure failure = Assert.Single(Assert.Single(run.Files).Failures);
        Assert.Equal("broken", failure.Name);
        Assert.Contains("error outside a test: ", failure.Message);
        Assert.Contains("while collecting", failure.Message);
    }

    [Fact]
    public void AFilterRunsOnlyTheCasesWhoseNameContainsIt()
    {
        using var folder = new TestFolder();
        folder.Write("f.test.luau", @"
            local test = require('@engine/test')
            test.describe('math', function()
                test.it('adds', function() end)
                test.it('subtracts', function() error('should not run') end)
            end)");
        var output = new StringWriter();

        TestRunResult run = LuauTestRunner.Run(folder.Root, "adds", output);

        Assert.True(run.Success);
        Assert.Equal((1, 1), (run.Passed, run.Skipped));
        Assert.Contains("1 not matching \"adds\"", output.ToString());
    }

    [Fact]
    public void AFolderWithoutTestsIsNotAPass()
    {
        using var folder = new TestFolder();
        var output = new StringWriter();

        TestRunResult run = LuauTestRunner.Run(folder.Root, output: output);

        Assert.False(run.Success);
        Assert.Contains("No *.test.luau files found under", output.ToString());
    }

    [Fact]
    public void FilesDoNotShareAnything()
    {
        using var folder = new TestFolder();
        folder.Write("a.test.luau", "local test = require('@engine/test')\nrequire('./shared').count += 1\ntest.it('sees one', function() test.expect(require('./shared').count).toBe(1) end)");
        folder.Write("b.test.luau", "local test = require('@engine/test')\nrequire('./shared').count += 1\ntest.it('sees one', function() test.expect(require('./shared').count).toBe(1) end)");
        folder.Write("shared.luau", "return { count = 0 }");

        Assert.True(LuauTestRunner.Run(folder.Root).Success); // each file has a VM, so each has its own module
    }

    [Fact]
    public void ThePrintOfATestGoesToTheOutput()
    {
        using var folder = new TestFolder();
        folder.Write("p.test.luau", "local test = require('@engine/test')\ntest.it('prints', function() print('hello', 5) end)");
        var output = new StringWriter();

        LuauTestRunner.Run(folder.Root, output: output);

        Assert.Contains("hello\t5", output.ToString());
    }

    [Fact]
    public void AnInfiniteLoopInACaseIsStoppedByTheTimeBudget()
    {
        using var folder = new TestFolder();
        folder.Write("loop.test.luau", "local test = require('@engine/test')\ntest.it('hangs', function() while true do end end)");
        var output = new StringWriter();

        TestRunResult run = LuauTestRunner.Run(folder.Root, output: output, timeBudgetMilliseconds: 100);

        Assert.False(run.Success);
        Assert.Contains("time budget of 100 ms", output.ToString());
    }

    [Fact]
    public void EngineModulesAreEmbeddedAndListed()
    {
        Assert.Contains("test", LuauVm.EngineModules.Select(m => m.Name));
        Assert.All(LuauVm.EngineModules, m => Assert.False(string.IsNullOrWhiteSpace(m.Source)));
    }
}
