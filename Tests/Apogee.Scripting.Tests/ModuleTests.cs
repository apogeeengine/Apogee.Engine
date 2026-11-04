using System;
using System.Collections.Generic;
using System.IO;
using Apogee.Reflection;
using Apogee.Scripting;
using Xunit;

namespace Apogee.Scripting.Tests;

/// <summary>A folder of scripts the test writes, and a VM that may load from it.</summary>
public sealed class ScriptFolder : IDisposable
{
    public ScriptFolder(VmFixture fixture)
    {
        Fixture = fixture;
        Root = Path.Combine(Path.GetTempPath(), "apogee-scripts-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        Directory.CreateDirectory(Root);
        fixture.Vm.AddRoot(Root);
    }

    public VmFixture Fixture { get; }

    public string Root { get; }

    public void Write(string relative, string source)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source);
    }

    /// <summary>Runs <c>relative</c> as the main script of the folder.</summary>
    public IReadOnlyList<Value> RunMain(string relative = "main.luau", ScriptOwner? owner = null)
    {
        string path = Root + "/" + relative;
        return Fixture.Vm.Execute(owner ?? Fixture.Vm.BaseOwner, "@" + path, File.ReadAllText(path));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a test failure.
        }
    }
}

public class ModuleTests
{
    private static string Messages(VmFixture f) => string.Join("|", f.Output.ConvertAll(o => o.Text));

    [Fact]
    public void ARelativeModuleIsLoadedAndItsResultReturned()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("main.luau", "local util = require('./lib/util') return util.double(21)");
        folder.Write("lib/util.luau", "local M = {} function M.double(x) return x * 2 end return M");

        Assert.Equal(42.0, folder.RunMain()[0].ToDouble());
    }

    [Fact]
    public void RequiresAreRelativeToTheRequiringFile()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("main.luau", "return require('./a/one')");
        folder.Write("a/one.luau", "return require('./two') + require('../shared')");
        folder.Write("a/two.luau", "return 10");
        folder.Write("shared.luau", "return 5");

        Assert.Equal(15.0, folder.RunMain()[0].ToDouble());
    }

    [Fact]
    public void AModuleRunsOnceAndIsCached()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("main.luau", "local a = require('./counter') local b = require('./counter') print(a == b) return a.n");
        folder.Write("counter.luau", "print('loading') return { n = 1 }");

        Assert.Equal(1.0, folder.RunMain()[0].ToDouble());
        Assert.Equal("loading|true", Messages(f));
    }

    [Fact]
    public void ADirectoryWithAnInitFileIsAModule()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("main.luau", "return require('./pkg')");
        folder.Write("pkg/init.luau", "return 'from init'");

        Assert.Equal("from init", folder.RunMain()[0].ToStringValue());
    }

    [Fact]
    public void AFileAndADirectoryOfTheSameNameAreAmbiguous()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("main.luau", "return require('./pkg')");
        folder.Write("pkg.luau", "return 1");
        folder.Write("pkg/init.luau", "return 2");

        Assert.Contains("ambiguous", Assert.Throws<ScriptException>(() => folder.RunMain()).Message);
    }

    [Fact]
    public void AMissingModuleSaysWhereItLooked()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("main.luau", "return require('./nothing')");

        string message = Assert.Throws<ScriptException>(() => folder.RunMain()).Message;

        Assert.Contains("module not found: ./nothing", message);
        Assert.Contains("nothing.luau", message);
        Assert.Contains("nothing/init.luau", message);
    }

    [Fact]
    public void ModulesOutsideTheRootsCannotBeRequired()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("main.luau", "return require('../../../../../../etc/passwd')");

        Assert.Contains("outside the folders", Assert.Throws<ScriptException>(() => folder.RunMain()).Message);
    }

    [Fact]
    public void ARequirePathNeedsADotOrAnAlias()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("main.luau", "return require('util')");

        Assert.Contains("must start with ./, ../ or @alias/", Assert.Throws<ScriptException>(() => folder.RunMain()).Message);
    }

    [Fact]
    public void ACycleIsReported()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("main.luau", "return require('./a')");
        folder.Write("a.luau", "return require('./b')");
        folder.Write("b.luau", "return require('./a')");

        Assert.Contains("cyclic require", Assert.Throws<ScriptException>(() => folder.RunMain()).Message);
    }

    [Fact]
    public void AModuleThatYieldsOrReturnsTheWrongNumberOfValuesIsAnError()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("yields.luau", "coroutine.yield() return 1");
        folder.Write("two.luau", "return 1, 2");
        folder.Write("none.luau", "local x = 1");

        folder.Write("m1.luau", "return require('./yields')");
        folder.Write("m2.luau", "return require('./two')");
        folder.Write("m3.luau", "return require('./none')");

        Assert.Contains("can not yield", Assert.Throws<ScriptException>(() => folder.RunMain("m1.luau")).Message);
        Assert.Contains("must return a single value, it returned 2", Assert.Throws<ScriptException>(() => folder.RunMain("m2.luau")).Message);
        Assert.Contains("must return a single value, it returned 0", Assert.Throws<ScriptException>(() => folder.RunMain("m3.luau")).Message);
    }

    [Fact]
    public void AnErrorInAModuleReachesTheRequirerAndCanBeCaught()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("bad.luau", "error('module broke')");
        folder.Write("syntax.luau", "return +");
        folder.Write("main.luau", "local ok, err = pcall(require, './bad') local ok2, err2 = pcall(require, './syntax') return err, err2");

        IReadOnlyList<Value> results = folder.RunMain();

        Assert.Contains("module broke", results[0].ToStringValue());
        Assert.Contains("syntax.luau:1:", results[1].ToStringValue());
    }

    [Fact]
    public void AnAliasComesFromTheNearestLuaurc()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write(".luaurc", "{ // comments and trailing commas are fine\n  \"aliases\": { \"Lib\": \"./libs\", }\n}");
        folder.Write("main.luau", "return require('@lib/util') + require('./sub/deep')");
        folder.Write("libs/util.luau", "return 1");
        folder.Write("sub/deep.luau", "return require('@lib/util') + 10"); // the .luaurc of a parent folder applies below it

        Assert.Equal(12.0, folder.RunMain()[0].ToDouble());
    }

    [Fact]
    public void ANearerLuaurcWinsAndAnUnknownAliasIsAnError()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write(".luaurc", "{ \"aliases\": { \"x\": \"./outer\" } }");
        folder.Write("sub/.luaurc", "{ \"aliases\": { \"x\": \"./inner\" } }");
        folder.Write("outer/v.luau", "return 'outer'");
        folder.Write("sub/inner/v.luau", "return 'inner'");
        folder.Write("sub/main.luau", "return require('@x/v')");
        folder.Write("lost.luau", "return require('@missing/v')");

        Assert.Equal("inner", folder.RunMain("sub/main.luau")[0].ToStringValue());
        Assert.Contains("alias @missing is not defined", Assert.Throws<ScriptException>(() => folder.RunMain("lost.luau")).Message);
    }

    [Fact]
    public void SelfIsTheFolderOfAnInitFile()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("main.luau", "return require('./pkg') .. require('./other')");
        folder.Write("pkg/init.luau", "return require('@self/part')");
        folder.Write("pkg/part.luau", "return 'part'");
        folder.Write("other.luau", "return require('@self/x')");

        // From init.luau, @self is its folder; from any other file it is an error.
        Assert.Contains("'@self' can only be used from an init.luau", Assert.Throws<ScriptException>(() => folder.RunMain()).Message);
        folder.Write("main2.luau", "return require('./pkg')");
        Assert.Equal("part", folder.RunMain("main2.luau")[0].ToStringValue());
    }

    [Fact]
    public void EngineModulesAreEmbedded()
    {
        using var f = new VmFixture();
        f.Vm.AddEmbeddedModule("greet", "return function(name) return 'hello ' .. name end");

        Assert.Equal("hello you", f.Run("return require('@engine/greet')('you')")[0].ToStringValue());
        Assert.Contains("module not found: @engine/nothing", f.Fails("return require('@engine/nothing')").Message);
    }

    [Fact]
    public void ARelativeRequireNeedsAFileChunk()
    {
        using var f = new VmFixture();

        Assert.Contains("is not a file", f.Fails("return require('./x')").Message);
    }

    [Fact]
    public void AModuleRunsAsTheOwnerThatRequiredItUnlessTheHostChoosesAnother()
    {
        ScriptOwner? baseOwner = null;
        using var f = new VmFixture(ownerOfModule: path => path.EndsWith("/base_lib.luau", StringComparison.Ordinal) ? baseOwner : null);
        baseOwner = f.Vm.BaseOwner;
        ScriptOwner mod = f.Vm.CreateOwner("themod", isMod: true);
        using var folder = new ScriptFolder(f);
        folder.Write("main.luau", "require('./mod_lib') require('./base_lib')");
        folder.Write("mod_lib.luau", "print('mod lib') return 1");
        folder.Write("base_lib.luau", "print('base lib') return 1");

        folder.RunMain(owner: mod);

        // The owner that prints shows who ran each module: the mod's own file runs as the mod, the one the host assigned as the base game.
        Assert.Equal([("themod", "mod lib", false), ("base", "base lib", false)], f.Output);
    }
}
