using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Xunit;

namespace Apogee.Scripting.Tests;

public class HotReloaderTests
{
    private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(50);

    /// <summary>Polls until the report says something happened, or ten seconds pass.</summary>
    private static ReloadReport Next(HotReloader reloader)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < end)
        {
            ReloadReport report = reloader.Poll();
            if (report.Any)
            {
                return report;
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException("Nothing was reloaded within ten seconds.");
    }

    private static string Core(GameFixture game, string relative) => Path.Combine(game.Root, "res", "packages", "core", relative);

    private static void Edit(GameFixture game, string relative, string text) => File.WriteAllText(Core(game, relative), text);

    private static GameFixture Game()
    {
        var game = new GameFixture();
        game.Base(
            "core",
            "<Content path=\"data\"/><Schema path=\"schemas\"/><Script path=\"main.luau\"/>",
            ("main.luau", "defs.on_change(function() print('defs changed') end) print(require('./lib').value())"),
            ("lib.luau", "return { value = function() return 'v1' end }"),
            ("data/crates.xml", "<Crate id=\"wooden\" weight=\"5\"/>"),
            ("schemas/crate.xsd", "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"Crate\"><xs:complexType><xs:attribute name=\"id\" type=\"xs:string\" use=\"required\"/><xs:attribute name=\"weight\" type=\"xs:decimal\" default=\"1\"/></xs:complexType></xs:element></xs:schema>"));
        game.Start();
        return game;
    }

    private static string Run(GameFixture game, string source)
    {
        game.Output.Clear();
        game.Vm!.Execute(game.Vm.BaseOwner, "@mod://core/probe.luau", source);
        return string.Join("|", game.Output.Select(o => o.Text));
    }

    [Fact]
    public void AChangedModuleIsReloadedAndScriptsSeeTheNewCode()
    {
        using GameFixture game = Game();
        using var reloader = new HotReloader(game.Files, game.Vm!, game.Packages, s_debounce);
        Assert.Equal("v1", Run(game, "print(require('./lib').value())"));

        Edit(game, "lib.luau", "return { value = function() return 'v2' end }");
        ReloadReport report = Next(reloader);

        Assert.Equal(["mod://core/lib.luau"], report.Modules);
        Assert.Empty(report.Failures);
        Assert.Equal("v2", Run(game, "print(require('./lib').value())"));
    }

    [Fact]
    public void ABrokenEditKeepsTheOldModuleAndSaysWhy()
    {
        using GameFixture game = Game();
        using var reloader = new HotReloader(game.Files, game.Vm!, game.Packages, s_debounce);

        Edit(game, "lib.luau", "return { value = function( }");
        ReloadReport report = Next(reloader);

        Assert.Empty(report.Modules);
        Assert.Equal("mod://core/lib.luau", report.Failures.Single().Path);
        Assert.Equal("v1", Run(game, "print(require('./lib').value())"));

        Edit(game, "lib.luau", "return { value = function() return 'fixed' end }"); // fixing it works as if nothing had happened
        Assert.Equal(["mod://core/lib.luau"], Next(reloader).Modules);
        Assert.Equal("fixed", Run(game, "print(require('./lib').value())"));
    }

    [Fact]
    public void ChangedDefinitionsAreReadAgainAndListenersAreCalled()
    {
        using GameFixture game = Game();
        using var reloader = new HotReloader(game.Files, game.Vm!, game.Packages, s_debounce);
        Assert.Equal("5", Run(game, "print(defs.get('Crate', 'wooden').weight)"));
        game.Output.Clear();

        Edit(game, "data/crates.xml", "<Crates><Crate id=\"wooden\" weight=\"9\"/><Crate id=\"steel\" weight=\"80\"/></Crates>");
        ReloadReport report = Next(reloader);

        Assert.True(report.DefsReloaded);
        Assert.Contains("defs changed", game.Output.Select(o => o.Text)); // the listener the package's script registered
        Assert.Equal("9|80", Run(game, "print(defs.get('Crate', 'wooden').weight) print(defs.get('Crate', 'steel').weight)"));
    }

    [Fact]
    public void ASchemaChangeReloadsTheDefinitionsToo()
    {
        using GameFixture game = Game();
        using var reloader = new HotReloader(game.Files, game.Vm!, game.Packages, s_debounce);

        Edit(game, "schemas/crate.xsd", "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"Crate\"><xs:complexType><xs:attribute name=\"id\" type=\"xs:string\" use=\"required\"/><xs:attribute name=\"weight\" type=\"xs:decimal\" default=\"42\"/></xs:complexType></xs:element></xs:schema>");
        Edit(game, "data/crates.xml", "<Crate id=\"light\"/>");
        ReloadReport report = Next(reloader);
        while (!report.DefsReloaded)
        {
            report = Next(reloader);
        }

        Assert.Equal("42", Run(game, "print(defs.get('Crate', 'light').weight)")); // the default of the new schema
    }

    [Fact]
    public void WhatOnlyApplyOnStartIsListedNotAppliedOrIgnored()
    {
        using GameFixture game = Game();
        using var reloader = new HotReloader(game.Files, game.Vm!, game.Packages, s_debounce);

        Edit(game, "main.luau", "print('a different entry script')");
        File.WriteAllText(Core(game, "mod.xml"), "<Mod id=\"core\" version=\"2.0.0\"><Script path=\"main.luau\"/></Mod>");
        var restart = new List<string>();
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (restart.Count < 4 && DateTime.UtcNow < end)
        {
            restart.AddRange(reloader.Poll().NeedsRestart);
            Thread.Sleep(25);
        }

        Assert.Contains("mod://core/main.luau", restart);
        Assert.Contains("mod://core/mod.xml", restart);
    }

    [Fact]
    public void ModulesThatNothingRequiredYetAreNotLoadedByAChange()
    {
        using GameFixture game = Game();
        using var reloader = new HotReloader(game.Files, game.Vm!, game.Packages, s_debounce);

        Edit(game, "never_required.luau", "print('should not run') return {}");
        Thread.Sleep(400);

        Assert.False(reloader.Poll().Any);
        Assert.DoesNotContain("should not run", game.Output.Select(o => o.Text));
    }

    private sealed class Project : IDisposable
    {
        private readonly TempFolderLike _folder = new();

        public Project()
        {
            Files = new VirtualFileSystem();
            Files.Mount("res://", new DirectorySource(_folder.Root, writable: true));
            Vm = new LuauVm(new LuauVmOptions { Output = (_, _, _) => { } });
        }

        public VirtualFileSystem Files { get; }

        public LuauVm Vm { get; }

        public string Root => _folder.Root;

        public void Dispose()
        {
            Vm.Dispose();
            _folder.Dispose();
        }
    }

    private sealed class TempFolderLike : IDisposable
    {
        public TempFolderLike() => Directory.CreateDirectory(Root);

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "apogee-hot-" + Guid.NewGuid().ToString("N"));

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

    private static byte[] Png(int size, int seed)
    {
        byte[] rgba = new byte[size * size * 4];
        new Random(seed).NextBytes(rgba);
        return Apogee.Content.Png.Encode(size, size, rgba);
    }

    [Fact]
    public void AnImageThatChangedIsImportedAgainWhenTheProjectIsImported()
    {
        Apogee.Native.Tests.NativeRequirements.Require(typeof(Apogee.Native.Luau.Lua).Assembly, "luau");
        using var project = new Project();
        File.WriteAllBytes(Path.Combine(project.Root, "ground.png"), Png(16, 1));
        Assert.True(new AssetImporter(project.Files).Run().Success);
        byte[] before = File.ReadAllBytes(Path.Combine(project.Root, "imported", "ground.png.ktx2"));
        using var reloader = new HotReloader(project.Files, project.Vm, null, s_debounce);
        IReadOnlyList<string>? raised = null;
        reloader.AssetsChanged += paths => raised = paths;

        File.WriteAllBytes(Path.Combine(project.Root, "ground.png"), Png(16, 2));
        ReloadReport report = Next(reloader);

        Assert.Equal(["res://ground.png"], report.Assets);
        Assert.Equal(["res://ground.png"], raised);
        Assert.NotEqual(before, File.ReadAllBytes(Path.Combine(project.Root, "imported", "ground.png.ktx2")));
        Thread.Sleep(300);
        Assert.False(reloader.Poll().Any); // writing the artifact is not a change of an asset
    }

    [Fact]
    public void WithoutAnImportTheHostIsToldTheSourceChangedAndShadersAreReported()
    {
        Apogee.Native.Tests.NativeRequirements.Require(typeof(Apogee.Native.Luau.Lua).Assembly, "luau");
        using var project = new Project();
        using var reloader = new HotReloader(project.Files, project.Vm, null, s_debounce);
        var shaders = new List<string>();
        reloader.ShadersChanged += paths => shaders.AddRange(paths);

        File.WriteAllBytes(Path.Combine(project.Root, "tree.png"), Png(8, 1));
        File.WriteAllText(Path.Combine(project.Root, "lit.slang"), "// shader");
        var assets = new List<string>();
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while ((assets.Count == 0 || shaders.Count == 0) && DateTime.UtcNow < end)
        {
            assets.AddRange(reloader.Poll().Assets);
            Thread.Sleep(25);
        }

        Assert.Equal(["res://tree.png"], assets);
        Assert.Equal(["res://lit.slang"], shaders);
        Assert.False(Directory.Exists(Path.Combine(project.Root, "imported"))); // nothing was imported: the project never was
    }
}
