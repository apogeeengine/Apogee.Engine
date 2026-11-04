using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Content.FileSystem;
using Apogee.Content.Packages;
using Apogee.Scripting;
using Apogee.Native.Tests;
using Xunit;
using Lua = Apogee.Native.Luau.Lua;

namespace Apogee.Scripting.Tests;

/// <summary>A game folder with its packages, a user folder, and a VM wired the way a game wires it.</summary>
public sealed class GameFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apogee-game-" + Guid.NewGuid().ToString("N"));

    public GameFixture()
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        Directory.CreateDirectory(Path.Combine(_root, "res"));
        Directory.CreateDirectory(Path.Combine(_root, "user"));
        Files = new VirtualFileSystem();
        Files.Mount("res://", new DirectorySource(Path.Combine(_root, "res")));
        Files.Mount("user://", new DirectorySource(Path.Combine(_root, "user"), writable: true));
    }

    public VirtualFileSystem Files { get; }

    public string Root => _root;

    public List<(string Owner, string Text)> Output { get; } = [];

    public PackageSet? Packages { get; private set; }

    public PackageLoader? Loader { get; private set; }

    public LuauVm? Vm { get; private set; }

    public void Base(string id, string manifestInner, params (string Path, string Text)[] files) => Write("res/packages/" + id, id, manifestInner, files);

    public void Mod(string id, string manifestInner, params (string Path, string Text)[] files) => Write("user/mods/" + id, id, manifestInner, files);

    private void Write(string folder, string id, string manifestInner, (string Path, string Text)[] files)
    {
        string path = Path.Combine(_root, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "mod.xml"), $"<Mod id=\"{id}\" version=\"1.0.0\">{manifestInner}</Mod>");
        foreach ((string file, string text) in files)
        {
            string full = Path.Combine(path, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
    }

    public void WriteUser(string relative, string text)
    {
        string full = Path.Combine(_root, "user", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>Finds the packages, makes the VM and runs their scripts.</summary>
    public IReadOnlyList<PackageRunResult> Start(PackageLoaderOptions? options = null)
    {
        Packages = PackageSet.Load(Files);
        Loader = new PackageLoader(Files, Packages, options);
        Vm = new LuauVm(new LuauVmOptions
        {
            Files = new VfsScriptFiles(Files),
            FileSystem = Files,
            OwnerOfModule = Loader.OwnerOfModule,
            CanLoadModule = Loader.CanLoadModule,
            CanAccessFile = Loader.CanAccessFile,
            Output = (owner, text, _) => Output.Add((owner.Id, text)),
            TimeBudgetMilliseconds = 2000,
        });
        Vm.Defs = Packages.LoadDefs();
        return Loader.Load(Vm);
    }

    public string[] Lines => [.. Output.Select(o => $"{o.Owner}: {o.Text}")];

    public void Dispose()
    {
        Vm?.Dispose();
        Packages?.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a failure.
        }
    }
}

public class PackageLoaderTests
{
    private const string Dep = "<Dependency id=\"core\"/>";

    [Fact]
    public void ScriptsRunInLoadOrderAsTheirOwnersAndABaseModuleStaysTheBaseGames()
    {
        using var game = new GameFixture();
        game.Base("core", "<Script path=\"main.luau\"/>", ("main.luau", "print('core main')"), ("lib.luau", "print('lib loaded') return { n = 7 }"));
        game.Mod("zeta", Dep + "<Script path=\"main.luau\"/>", ("main.luau", "print('zeta', require('@core/lib').n)"));
        game.Mod("alpha", Dep + "<Script path=\"a.luau\"/><Script path=\"b.luau\"/>", ("a.luau", "print('alpha a')"), ("b.luau", "print('alpha b')"));

        IReadOnlyList<PackageRunResult> results = game.Start();

        Assert.All(results, r => Assert.True(r.Ran, r.Error));
        Assert.Equal(["core", "alpha", "zeta"], results.Select(r => r.Package.Id));
        Assert.Equal(["base: core main", "alpha: alpha a", "alpha: alpha b", "base: lib loaded", "zeta: zeta\t7"], game.Lines);
    }

    [Fact]
    public void EachModGetsAnOwnerWithTheLimitsAndTheBaseGameRunsAsBase()
    {
        using var game = new GameFixture();
        game.Base("core", "");
        game.Mod("cool", Dep);

        game.Start(new PackageLoaderOptions { ModMemoryLimit = 5_000_000, ModTimeBudgetMilliseconds = 123 });

        ScriptOwner? owner = game.Loader!.OwnerOfModule("mod://cool/x.luau");
        Assert.NotNull(owner);
        Assert.Equal(("cool", true, 5_000_000L, 123), (owner.Id, owner.IsMod, owner.MemoryLimit, owner.TimeBudgetMilliseconds));
        Assert.Same(game.Vm!.BaseOwner, game.Loader.OwnerOfModule("mod://core/x.luau"));
        Assert.Null(game.Loader.OwnerOfModule("res://x.luau"));
        Assert.Null(game.Loader.OwnerOfModule("mod://nobody/x.luau"));
    }

    [Fact]
    public void AModMayRequireItsOwnFilesAndItsDependenciesButNotAnotherMods()
    {
        using var game = new GameFixture();
        game.Base("core", "", ("shared.luau", "return 'shared'"));
        game.Mod("lib_mod", Dep, ("util.luau", "return 'util'"));
        game.Mod("user_mod", Dep + "<Dependency id=\"lib_mod\"/><Script path=\"main.luau\"/>", ("main.luau", "print(require('./helper'), require('@core/shared'), require('@lib_mod/util'))"), ("helper.luau", "return 'helper'"));
        game.Mod("snoop", Dep + "<Script path=\"main.luau\"/>", ("main.luau", "print(pcall(require, '@lib_mod/util'))"));
        game.Mod("climber", Dep + "<Script path=\"main.luau\"/>", ("main.luau", "print(pcall(require, '../lib_mod/util'))"));

        IReadOnlyList<PackageRunResult> results = game.Start();

        Assert.All(results, r => Assert.True(r.Ran, r.Error));
        Assert.Contains("user_mod: helper\tshared\tutil", game.Lines);
        Assert.Contains(game.Lines, l => l.StartsWith("snoop: false\t", StringComparison.Ordinal) && l.Contains("not one that this script may load", StringComparison.Ordinal));
        Assert.Contains(game.Lines, l => l.StartsWith("climber: false\t", StringComparison.Ordinal) && l.Contains("not one that this script may load", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBaseGameMayRequireAnyPackage()
    {
        using var game = new GameFixture();
        game.Base("core", "<Script path=\"main.luau\"/>", ("main.luau", "print(require('@lib_mod/util'))"));
        game.Mod("lib_mod", Dep, ("util.luau", "return 'from a mod'"));

        // core loads first, before the mod it requires is a dependency of anything: the alias and root exist for every package up front.
        IReadOnlyList<PackageRunResult> results = game.Start();

        Assert.True(results.Single(r => r.Package.Id == "core").Ran);
        Assert.Contains("base: from a mod", game.Lines);
    }

    [Fact]
    public void FsLetsAModReadTheGameAndItsOwnFilesAndWriteOnlyItsDataFolder()
    {
        using var game = new GameFixture();
        game.Base("core", "");
        game.Files.Mount("res://", new DirectorySource(Path.Combine(game.Root, "res")));
        File.WriteAllText(Path.Combine(game.Root, "res", "readme.txt"), "game text");
        game.Mod("cool", Dep + "<Script path=\"main.luau\"/>", ("main.luau", """
            print(fs.read_text("res://readme.txt"))
            print(fs.read_text("mod://cool/main.luau") ~= nil)
            print(fs.write_text("user://moddata/cool/save.txt", "saved"))
            print(fs.read_text("user://moddata/cool/save.txt"))
            print(fs.exists("user://moddata/cool/save.txt"), fs.exists("user://moddata/cool/nothing"))
            print(fs.make_dir("user://moddata/cool/sub"), fs.write_bytes("user://moddata/cool/sub/b.bin", "a\0b"))
            print(#fs.read_bytes("user://moddata/cool/sub/b.bin"))
            print(table.concat(fs.list("user://moddata/cool", "*.txt"), ","), table.concat(fs.list("user://moddata/cool", "*", true), ","), table.concat(fs.list_dirs("user://moddata/cool"), ","))
            print(fs.remove("user://moddata/cool/save.txt"), fs.exists("user://moddata/cool/save.txt"))
            """));

        IReadOnlyList<PackageRunResult> results = game.Start();

        Assert.True(results.Single(r => r.Package.Id == "cool").Ran, results.Single(r => r.Package.Id == "cool").Error);
        Assert.Equal(
            [
                "cool: game text",
                "cool: true",
                "cool: true",
                "cool: saved",
                "cool: true\tfalse",
                "cool: true\ttrue",
                "cool: 3",
                "cool: user://moddata/cool/save.txt\tuser://moddata/cool/save.txt,user://moddata/cool/sub/b.bin\tuser://moddata/cool/sub",
                "cool: true\tfalse",
            ],
            game.Lines.Where(l => l.StartsWith("cool:", StringComparison.Ordinal)));
        Assert.False(File.Exists(Path.Combine(game.Root, "user", "moddata", "cool", "save.txt"))); // removed by the script
        Assert.True(File.Exists(Path.Combine(game.Root, "user", "moddata", "cool", "sub", "b.bin")));
    }

    [Fact]
    public void FsRefusesWhatAModMayNotDoAndNeverShowsARealPath()
    {
        using var game = new GameFixture();
        game.Base("core", "");
        game.WriteUser("secret.txt", "player's secret");
        game.WriteUser("moddata/other/theirs.txt", "someone else's");
        game.Mod("snoop", Dep + "<Script path=\"main.luau\"/>", ("main.luau", """
            print(fs.read_text("user://secret.txt"))
            print(fs.read_text("user://moddata/other/theirs.txt"))
            print(fs.write_text("res://hack.txt", "x"))
            print(fs.write_text("user://elsewhere.txt", "x"))
            print(fs.write_text("mod://snoop/own.txt", "x"))
            print(fs.exists("user://secret.txt"))
            print(fs.read_text("res://does/not/exist.txt"))
            print(fs.read_text("../../etc/passwd"))
            print(fs.read_text("res://a/../b"))
            print(fs.list("user://"))
            print(fs.remove("user://secret.txt"))
            """));

        game.Start();

        string[] lines = [.. game.Lines.Where(l => l.StartsWith("snoop:", StringComparison.Ordinal))];
        Assert.Equal("snoop: nil\tuser://secret.txt: access denied", lines[0]);
        Assert.Equal("snoop: nil\tuser://moddata/other/theirs.txt: access denied", lines[1]);
        Assert.Equal("snoop: nil\tres://hack.txt: access denied", lines[2]);
        Assert.Equal("snoop: nil\tuser://elsewhere.txt: access denied", lines[3]);
        Assert.Equal("snoop: nil\tmod://snoop/own.txt: access denied", lines[4]);
        Assert.Equal("snoop: false", lines[5]);
        Assert.Equal("snoop: nil\tres://does/not/exist.txt: not found", lines[6]);
        Assert.StartsWith("snoop: nil\t../../etc/passwd: not a valid path", lines[7], StringComparison.Ordinal);
        Assert.StartsWith("snoop: nil\tres://a/../b: not a valid path", lines[8], StringComparison.Ordinal);
        Assert.Equal("snoop: nil\tuser://: access denied", lines[9]);
        Assert.Equal("snoop: nil\tuser://secret.txt: access denied", lines[10]);
        Assert.All(game.Lines, l => Assert.DoesNotContain(game.Root, l, StringComparison.Ordinal));
        Assert.Equal("player's secret", File.ReadAllText(Path.Combine(game.Root, "user", "secret.txt")));
        Assert.False(File.Exists(Path.Combine(game.Root, "res", "hack.txt")));
    }

    [Fact]
    public void TheBaseGameMayWriteWhereTheFileSystemAllows()
    {
        using var game = new GameFixture();
        game.Base("core", "<Script path=\"main.luau\"/>", ("main.luau", """
            print(fs.write_text("user://settings.xml", "<s/>"))
            print(fs.write_text("res://nope.txt", "x"))
            """));

        game.Start();

        Assert.Equal("base: true", game.Lines[0]);
        Assert.Equal("base: nil\tres://nope.txt: read-only or not allowed", game.Lines[1]);
        Assert.Equal("<s/>", File.ReadAllText(Path.Combine(game.Root, "user", "settings.xml")));
    }

    [Fact]
    public void AScriptThatFailsStopsItsPackageAndWhatNeedsItButNotTheRest()
    {
        using var game = new GameFixture();
        game.Base("core", "<Script path=\"main.luau\"/>", ("main.luau", "print('core ok')"));
        game.Mod("broken", Dep + "<Script path=\"a.luau\"/><Script path=\"b.luau\"/>", ("a.luau", "print('before') error('boom')"), ("b.luau", "print('never')"));
        game.Mod("needs_broken", Dep + "<Dependency id=\"broken\"/><Script path=\"main.luau\"/>", ("main.luau", "print('never either')"));
        game.Mod("fine", Dep + "<Script path=\"main.luau\"/>", ("main.luau", "print('fine ran')"));
        game.Mod("missing", Dep + "<Script path=\"gone.luau\"/>");

        IReadOnlyList<PackageRunResult> results = game.Start();

        PackageRunResult Of(string id) => results.Single(r => r.Package.Id == id);
        Assert.True(Of("core").Ran);
        Assert.True(Of("fine").Ran);
        Assert.False(Of("broken").Ran);
        Assert.Contains("failed in a.luau", Of("broken").Error);
        Assert.Contains("boom", Of("broken").Error);
        Assert.False(Of("needs_broken").Ran);
        Assert.Contains("'broken', which it needs, failed", Of("needs_broken").Error);
        Assert.False(Of("missing").Ran);
        Assert.Contains("could not read gone.luau", Of("missing").Error);
        Assert.Contains("broken: before", game.Lines);
        Assert.DoesNotContain(game.Lines, l => l.Contains("never", StringComparison.Ordinal));
        Assert.Contains("fine: fine ran", game.Lines);
    }

    [Fact]
    public void AModThatLoopsForeverIsStoppedByItsTimeBudget()
    {
        using var game = new GameFixture();
        game.Base("core", "");
        game.Mod("spin", Dep + "<Script path=\"main.luau\"/>", ("main.luau", "while true do end"));
        game.Mod("after", Dep + "<Script path=\"main.luau\"/>", ("main.luau", "print('still here')"));

        IReadOnlyList<PackageRunResult> results = game.Start(new PackageLoaderOptions { ModTimeBudgetMilliseconds = 100 });

        Assert.False(results.Single(r => r.Package.Id == "spin").Ran);
        Assert.True(results.Single(r => r.Package.Id == "after").Ran);
        Assert.Contains("after: still here", game.Lines);
    }

    [Fact]
    public void ScriptsSeeTheDefinitionsOfAllThePackagesInOrder()
    {
        using var game = new GameFixture();
        game.Base("core", "<Content path=\"data\"/><Script path=\"main.luau\"/>", ("data/crates.xml", "<Crate id=\"wooden\" weight=\"5\"/>"), ("main.luau", "print(defs.get('Crate', 'wooden').weight)"));
        game.Mod("bigger", Dep + "<Content path=\"data\"/><Script path=\"main.luau\"/>", ("data/patch.xml", "<Crate id=\"wooden\" patch=\"true\" weight=\"9\"/>"), ("main.luau", "print(defs.get('Crate', 'wooden').weight, #defs.sources('Crate', 'wooden'))"));

        game.Start();

        Assert.Equal(["base: 9", "bigger: 9\t2"], game.Lines); // the content of every package is loaded before any script runs
    }

    [Fact]
    public void ScriptsCanAskWhichPackagesThereAreAndWhatBecameOfThem()
    {
        using var game = new GameFixture();
        game.Base("core", "<Script path=\"main.luau\"/>", ("main.luau", """
            for _, p in packages.list() do
                print(p.id, p.version, p.kind, p.loads, p.reason, p.ran, p.error and string.match(p.error, "[^\n]+"))
            end
            print(packages.get("nothing"), packages.get("core").name)
            """));
        game.Mod("fine", Dep + "<Script path=\"main.luau\"/>", ("main.luau", "print('ok')"));
        game.Mod("broken", Dep + "<Script path=\"main.luau\"/>", ("main.luau", "error('bug')"));
        game.Mod("homeless", "<Dependency id=\"ghost\"/>");

        game.Start();
        game.Vm!.Execute(game.Vm.BaseOwner, "=late", "for _, p in packages.list() do print('late', p.id, p.loads, p.ran, p.reason) end");

        // While core runs, only itself has run so far (the list fills as packages run); the later call sees all of it.
        Assert.Contains("base: core\t1.0.0\tbase\ttrue\tnil\tfalse\tnil", game.Lines);
        Assert.Contains("base: nil\tcore", game.Lines);
        Assert.Contains("base: late\tcore\ttrue\ttrue\tnil", game.Lines);
        Assert.Contains("base: late\tfine\ttrue\ttrue\tnil", game.Lines);
        Assert.Contains(game.Lines, l => l.StartsWith("base: late\tbroken\ttrue\tfalse", StringComparison.Ordinal));
        Assert.Contains("base: late\thomeless\tfalse\tfalse\tneeds 'ghost', which is not installed", game.Lines);
    }

    [Fact]
    public void WithoutAFileSystemFsSaysSo()
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        using var vm = new LuauVm(new LuauVmOptions { Output = (_, _, _) => { } });

        IReadOnlyList<Apogee.Reflection.Value> result = vm.Execute(vm.BaseOwner, "=t", "local ok, why = fs.read_text('res://x') return why");

        Assert.Equal("there is no file system", result[0].ToStringValue());
    }
}
