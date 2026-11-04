using System;
using System.Linq;
using Xunit;

namespace Apogee.Scripting.Tests;

public class ModuleReloadTests
{
    private static ModuleReload Reload(ScriptFolder folder, string relative) => folder.Fixture.Vm.ReloadModule(folder.Root + "/" + relative);

    [Fact]
    public void AReloadedModuleIsUpdatedWhereItIsSoWhoeverHoldsItSeesTheNewCode()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("lib.luau", "return { value = function() return 1 end, old_only = true }");
        folder.Write("holder.luau", "local lib = require('./lib') return { lib = lib, copy = lib.value }");
        folder.Write("main.luau", "local h = require('./holder') print(h.lib.value())");
        folder.RunMain();

        folder.Write("lib.luau", "return { value = function() return 2 end, added = true }");
        ModuleReload result = Reload(folder, "lib.luau");
        folder.Write("second.luau", "local h = require('./holder') print(h.lib.value(), h.lib.old_only, h.lib.added, h.copy(), h.lib == require('./lib'))");
        folder.RunMain("second.luau");

        Assert.True(result.Success, result.Message);
        Assert.Equal("updated in place", result.Message);
        Assert.Equal(["1", "2\tnil\ttrue\t1\ttrue"], f.Output.Select(o => o.Text)); // the function copied into a local is still the old one
    }

    [Fact]
    public void ANewVersionThatDoesNotCompileOrFailsChangesNothing()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("lib.luau", "return { value = function() return 1 end }");
        folder.Write("main.luau", "local lib = require('./lib') print(lib.value())");
        folder.RunMain();

        folder.Write("lib.luau", "return { value = function( return 2 end }");
        ModuleReload syntax = Reload(folder, "lib.luau");
        folder.Write("lib.luau", "error('boom') return {}");
        ModuleReload runtime = Reload(folder, "lib.luau");
        folder.Write("lib.luau", "while true do end");
        f.Vm.CreateOwner("slow", isMod: false, timeBudgetMilliseconds: 50);
        folder.RunMain();

        Assert.False(syntax.Success);
        Assert.Contains("lib.luau", syntax.Message);
        Assert.False(runtime.Success);
        Assert.Contains("boom", runtime.Message);
        Assert.Equal(["1", "1"], f.Output.Select(o => o.Text)); // the old module is still what is required
    }

    [Fact]
    public void AModuleThatIsNotLoadedCannotBeReloaded()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("lib.luau", "return 1");
        folder.Write("main.luau", "return 1");
        folder.RunMain();

        Assert.False(Reload(folder, "lib.luau").Success);   // never required
        Assert.False(Reload(folder, "main.luau").Success);  // an entry script, not a module
        Assert.Contains("is not a loaded module", Reload(folder, "nothing.luau").Message);
        Assert.False(f.Vm.IsModuleLoaded(folder.Root + "/lib.luau"));
    }

    [Fact]
    public void TheModulesThatHaveLoadedAreKnown()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("a.luau", "return 1");
        folder.Write("main.luau", "require('./a')");
        folder.RunMain();

        Assert.True(f.Vm.IsModuleLoaded(folder.Root + "/a.luau"));
        Assert.Equal([folder.Root + "/a.luau"], f.Vm.LoadedModules);
    }

    [Fact]
    public void OnReloadLetsTheNewModuleTakeStateFromTheOldOne()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        // State lives in a table of its own, which the new module takes over: its functions work on the new module table M, and the state is shared.
        folder.Write("lib.luau", "local M = { state = { count = 0 } } function M.inc() M.state.count += 1 end return M");
        folder.Write("main.luau", "local lib = require('./lib') lib.inc() lib.inc() print(lib.state.count)");
        folder.RunMain();

        folder.Write("lib.luau", """
            local M = { state = { count = 0 } }
            function M.on_reload(new, old) new.state = old.state end
            function M.inc() M.state.count += 10 end
            return M
            """);
        Assert.True(Reload(folder, "lib.luau").Success);
        folder.Write("again.luau", "local lib = require('./lib') print(lib.state.count) lib.inc() print(lib.state.count)");
        folder.RunMain("again.luau");

        Assert.Equal(["2", "2", "12"], f.Output.Select(o => o.Text));
    }

    [Fact]
    public void AFailingOnReloadIsLoggedAndTheUpdateGoesOn()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("lib.luau", "return { v = 1 }");
        folder.Write("main.luau", "print(require('./lib').v)");
        folder.RunMain();

        folder.Write("lib.luau", "return { v = 2, on_reload = function() error('hook broke') end }");
        Assert.True(Reload(folder, "lib.luau").Success);
        folder.Write("again.luau", "print(require('./lib').v)");
        folder.RunMain("again.luau");

        Assert.Equal(["1", "2"], f.Output.Select(o => o.Text));
    }

    [Fact]
    public void AFrozenModuleAndAModuleThatIsNotATableAreReplacedInTheCache()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("frozen.luau", "return table.freeze({ v = 1 })");
        folder.Write("number.luau", "return 1");
        folder.Write("main.luau", "print(require('./frozen').v, require('./number'))");
        folder.RunMain();

        folder.Write("frozen.luau", "return table.freeze({ v = 2 })");
        folder.Write("number.luau", "return 2");
        ModuleReload frozen = Reload(folder, "frozen.luau");
        ModuleReload number = Reload(folder, "number.luau");
        folder.Write("again.luau", "print(require('./frozen').v, require('./number'))");
        folder.RunMain("again.luau");

        Assert.Equal("replaced", frozen.Message);
        Assert.Equal("replaced", number.Message);
        Assert.Equal(["1\t1", "2\t2"], f.Output.Select(o => o.Text));
    }

    [Fact]
    public void WhatAModuleRegisteredIsTakenBackAndRegisteredAgainByTheNewRun()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("lib.luau", "defs.on_change(function() print('listener v1') end) return {}");
        folder.Write("main.luau", "require('./lib')");
        folder.RunMain();

        f.Vm.NotifyDefsChanged();
        folder.Write("lib.luau", "defs.on_change(function() print('listener v2') end) return {}");
        Assert.True(Reload(folder, "lib.luau").Success);
        f.Vm.NotifyDefsChanged();

        Assert.Equal(["listener v1", "listener v2"], f.Output.Select(o => o.Text));
    }

    [Fact]
    public void AFailedReloadTakesBackWhatTheNewCodeRegisteredBeforeItFailedAndKeepsTheOld()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("lib.luau", "defs.on_change(function() print('old listener') end) return {}");
        folder.Write("main.luau", "require('./lib')");
        folder.RunMain();

        folder.Write("lib.luau", "defs.on_change(function() print('partial') end) error('late failure')");
        ModuleReload result = Reload(folder, "lib.luau");
        f.Vm.NotifyDefsChanged();

        Assert.False(result.Success);
        Assert.Equal(["old listener"], f.Output.Select(o => o.Text));
    }

    [Fact]
    public void SignalConnectionsAreMovedToTheNewCode()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("holder.luau", "return { animal = Animal.new() }");
        folder.Write("lib.luau", "require('./holder').animal.hurt:connect(function(n) print('v1', n) end) return {}");
        folder.Write("main.luau", "require('./lib')");
        folder.RunMain();
        Assert.Equal(1, f.Vm.ConnectionCount);

        folder.Write("lib.luau", "require('./holder').animal.hurt:connect(function(n) print('v2', n) end) return {}");
        Assert.True(Reload(folder, "lib.luau").Success);
        folder.Write("hurt.luau", "require('./holder').animal:wound(5)");
        folder.RunMain("hurt.luau");

        Assert.Equal(1, f.Vm.ConnectionCount);
        Assert.Equal(["v2\t5"], f.Output.Select(o => o.Text));
    }

    [Fact]
    public void AModuleThatFailsToLoadTheFirstTimeLeavesNothingRegistered()
    {
        using var f = new VmFixture();
        using var folder = new ScriptFolder(f);
        folder.Write("lib.luau", "defs.on_change(function() print('never') end) error('first load fails')");
        folder.Write("main.luau", "pcall(require, './lib')");
        folder.RunMain();

        f.Vm.NotifyDefsChanged();

        Assert.Empty(f.Output);
        Assert.False(f.Vm.IsModuleLoaded(folder.Root + "/lib.luau"));
    }
}
