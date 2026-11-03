using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Apogee.Content.FileSystem;
using Apogee.Content.Packages;
using Apogee.Content.Xml;
using Xunit;

namespace Apogee.Content.Tests;

public class VersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("7", 7, 0, 0)]
    [InlineData("v2.0.1", 2, 0, 1)]
    [InlineData("1.4.0-beta.2", 1, 4, 0)]
    [InlineData("1.4.0+build5", 1, 4, 0)]
    public void VersionsParseWithMissingPartsZero(string text, int major, int minor, int patch)
    {
        Assert.True(PackageVersion.TryParse(text, out PackageVersion version));
        Assert.Equal(new PackageVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("1.2.3.4")]
    [InlineData("1..2")]
    [InlineData("-1.0")]
    public void SomethingElseIsNotAVersion(string text) => Assert.False(PackageVersion.TryParse(text, out _));

    [Fact]
    public void VersionsCompareByPartInOrder()
    {
        Assert.True(new PackageVersion(1, 10, 0) > new PackageVersion(1, 9, 9));
        Assert.True(new PackageVersion(2, 0, 0) > new PackageVersion(1, 99, 99));
        Assert.True(new PackageVersion(1, 2, 3) <= new PackageVersion(1, 2, 3));
        Assert.Equal("1.2.0", new PackageVersion(1, 2, 0).ToString());
    }

    [Theory]
    [InlineData(">=1.2", "1.2.0", true)]
    [InlineData(">=1.2", "1.1.9", false)]
    [InlineData(">1.2", "1.2.0", false)]
    [InlineData(">1.2", "1.2.1", true)]
    [InlineData("<2", "1.99.9", true)]
    [InlineData("<2", "2.0.0", false)]
    [InlineData("<=2", "2.0.0", true)]
    [InlineData("=1.4", "1.4.0", true)]
    [InlineData("1.4", "1.4.1", false)]
    [InlineData("*", "9.9.9", true)]
    [InlineData("", "0.0.1", true)]
    [InlineData("^1.2.3", "1.9.0", true)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData("^1.2.3", "1.2.2", false)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("^0.0.3", "0.0.3", true)]
    [InlineData("^0.0.3", "0.0.4", false)]
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData(">=1.2 <2.0", "1.5.0", true)]
    [InlineData(">=1.2 <2.0", "2.0.0", false)]
    [InlineData(">=1.2, <2.0", "1.1.0", false)]
    public void ARangeIsAllOfItsComparators(string range, string version, bool expected)
    {
        Assert.True(PackageVersion.TryParse(version, out PackageVersion parsed));
        Assert.Equal(expected, VersionRange.Parse(range).Matches(parsed));
    }

    [Fact]
    public void ABadComparatorSaysWhich()
    {
        Assert.False(VersionRange.TryParse(">=1.x", out _, out string? error));
        Assert.Contains("'>=1.x' is not a version comparator", error);
        Assert.Throws<FormatException>(() => VersionRange.Parse("^"));
        Assert.Equal(">=1.2 <2", VersionRange.Parse(">=1.2 <2").ToString());
        Assert.Equal("*", VersionRange.Any.ToString());
    }
}

public class ManifestTests
{
    private static PackageManifest Parse(string xml)
    {
        Assert.True(PackageManifest.TryParse(xml, "mod.xml", out PackageManifest? manifest, out IReadOnlyList<string> problems), string.Join("\n", problems));
        return manifest!;
    }

    private static IReadOnlyList<string> Fail(string xml)
    {
        Assert.False(PackageManifest.TryParse(xml, "mod.xml", out PackageManifest? manifest, out IReadOnlyList<string> problems));
        Assert.Null(manifest);
        Assert.NotEmpty(problems);
        return problems;
    }

    [Fact]
    public void AFullManifestIsRead()
    {
        PackageManifest manifest = Parse("""
            <Mod id="bigger_crates" name="Bigger Crates" version="1.2.3-beta" author="Ada" description="Crates, bigger." apiVersion="1" gameVersion="&gt;=1.0 &lt;3">
              <Dependency id="base_game" version="^1.0"/>
              <Dependency id="other"/>
              <Script path="main.luau"/>
              <Script path="ui/panel.luau"/>
              <Content path="data"/>
              <Schema path="schemas/"/>
            </Mod>
            """);

        Assert.Equal(("bigger_crates", "Bigger Crates", new PackageVersion(1, 2, 3), "Ada", "Crates, bigger."), (manifest.Id, manifest.Name, manifest.Version, manifest.Author, manifest.Description));
        Assert.Equal(1, manifest.ApiVersion);
        Assert.Equal(">=1.0 <3", manifest.GameVersion.ToString());
        Assert.Equal(["base_game", "other"], manifest.Dependencies.Select(d => d.Id));
        Assert.Equal(["^1.0", "*"], manifest.Dependencies.Select(d => d.Version.ToString()));
        Assert.Equal(["main.luau", "ui/panel.luau"], manifest.Scripts);
        Assert.Equal(["data"], manifest.ContentFolders);
        Assert.Equal(["schemas"], manifest.SchemaFolders);
    }

    [Fact]
    public void AMinimalManifestHasDefaults()
    {
        PackageManifest manifest = Parse("<Mod id=\"tiny\" version=\"0.1\"/>");

        Assert.Equal("tiny", manifest.Name);
        Assert.Equal(1, manifest.ApiVersion);
        Assert.Equal("*", manifest.GameVersion.ToString());
        Assert.Empty(manifest.Dependencies);
        Assert.Empty(manifest.Scripts);
    }

    [Theory]
    [InlineData("Bad-Id")]
    [InlineData("UPPER")]
    [InlineData("has space")]
    public void AnIdIsLowercaseLettersDigitsAndUnderscores(string id)
    {
        Assert.Contains(Fail($"<Mod id=\"{id}\" version=\"1\"/>"), p => p.Contains("does not match the pattern", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("self")]
    [InlineData("res")]
    [InlineData("user")]
    [InlineData("mod")]
    public void NamesTheEngineUsesAreNotIds(string id) => Assert.Contains(Fail($"<Mod id=\"{id}\" version=\"1\"/>"), p => p.Contains("a name the engine uses", StringComparison.Ordinal));

    [Fact]
    public void SchemaProblemsAreListedWithTheFileAndLine()
    {
        IReadOnlyList<string> problems = Fail("<Mod id=\"a\"\n version=\"1\" apiVersion=\"0\">\n<Dependency version=\"1\"/></Mod>");

        Assert.Equal(["mod.xml:1: 0 is below the minimum 1", "mod.xml:3: <Dependency> needs the attribute 'id'."], problems);
    }

    [Fact]
    public void WhatTheSchemaCannotSayIsCheckedAfterIt()
    {
        IReadOnlyList<string> problems = Fail("<Mod id=\"a\" version=\"one\" gameVersion=\"&gt;=x\"><Dependency id=\"a\"/><Script path=\"../evil.luau\"/><Content path=\"/abs\"/><Content path=\"http://x\"/></Mod>");

        Assert.Contains(problems, p => p.Contains("the version 'one' is not major.minor.patch", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'>=x' is not a version comparator", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("depends on itself", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'../evil.luau' of a <Script> must be a plain relative path", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'/abs' of a <Content> must be a plain relative path", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'http://x' of a <Content>", StringComparison.Ordinal));
        Assert.All(problems, p => Assert.StartsWith("mod.xml:1:", p));
    }

    [Fact]
    public void ScriptsMustBeLuau()
    {
        Assert.Contains(Fail("<Mod id=\"a\" version=\"1\"><Script path=\"main.py\"/></Mod>"), p => p.Contains("'main.py' is not a .luau file", StringComparison.Ordinal));
    }

    [Fact]
    public void NotAModOrNotXmlIsRefused()
    {
        Assert.Contains("the root element is <Package>", Fail("<Package id=\"a\"/>").Single());
        Assert.StartsWith("mod.xml:", Fail("<Mod").Single());
        Assert.Contains(Fail("<Mod id=\"a\" version=\"1\"><Surprise/></Mod>"), p => p.Contains("<Surprise> is not allowed in <Mod>", StringComparison.Ordinal));
    }
}

public class SolverTests
{
    private static PackageCandidate Candidate(string id, string version = "1.0.0", PackageKind kind = PackageKind.User, string dependencies = "", string attributes = "")
    {
        string xml = $"<Mod id=\"{id}\" version=\"{version}\" {attributes}>{dependencies}</Mod>";
        Assert.True(PackageManifest.TryParse(xml, $"{id}/mod.xml", out PackageManifest? manifest, out IReadOnlyList<string> problems), string.Join("\n", problems));
        return new PackageCandidate(manifest!, kind, id, () => throw new InvalidOperationException());
    }

    private static string Dependency(string id, string version = "*") => $"<Dependency id=\"{id}\" version=\"{version}\"/>";

    private static SolveResult Solve(SolveOptions? options, params PackageCandidate[] candidates) => PackageSolver.Solve(candidates, options);

    private static string[] Order(SolveResult result) => [.. result.LoadOrder.Select(p => p.Id)];

    private static string? Reason(SolveResult result, string id) => result.Packages.Single(p => p.Id == id).Reason;

    [Fact]
    public void DependenciesComeFirstBaseBeforeModsThenById()
    {
        SolveResult result = Solve(
            null,
            Candidate("zeta", kind: PackageKind.User),
            Candidate("alpha", kind: PackageKind.User, dependencies: Dependency("zeta")),
            Candidate("beta", kind: PackageKind.User),
            Candidate("core", kind: PackageKind.Base),
            Candidate("aaa_base", kind: PackageKind.Base, dependencies: Dependency("core")));

        Assert.Equal(["core", "aaa_base", "beta", "zeta", "alpha"], Order(result));
        Assert.All(result.Packages, p => Assert.True(p.Enabled));
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void TheOrderDoesNotDependOnTheOrderOfDiscovery()
    {
        PackageCandidate[] candidates = [Candidate("b"), Candidate("a", dependencies: Dependency("b")), Candidate("c")];

        Assert.Equal(Order(Solve(null, candidates)), Order(Solve(null, [.. candidates.Reverse()])));
    }

    [Fact]
    public void TheFirstPackageWithAnIdWinsAndLaterOnesAreIgnoredWithAProblem()
    {
        SolveResult result = Solve(null, Candidate("dup", "1.0.0", PackageKind.Base), Candidate("dup", "2.0.0", PackageKind.User));

        Assert.Single(result.Packages);
        Assert.Equal(new PackageVersion(1, 0, 0), result.Packages[0].Manifest.Version);
        Assert.Contains("'dup' in dup is ignored: dup has the same id.", result.Problems.Single());
    }

    [Fact]
    public void AMissingDisabledOrOutOfRangeDependencyDisablesAndItCascades()
    {
        SolveResult result = Solve(
            null,
            Candidate("lib", "1.0.0"),
            Candidate("needs_missing", dependencies: Dependency("ghost")),
            Candidate("needs_new_lib", dependencies: Dependency("lib", ">=2.0")),
            Candidate("indirect", dependencies: Dependency("needs_missing")),
            Candidate("deeper", dependencies: Dependency("indirect")),
            Candidate("fine", dependencies: Dependency("lib", "^1.0")));

        Assert.Equal("needs 'ghost', which is not installed", Reason(result, "needs_missing"));
        Assert.Equal("needs 'lib' >=2.0, but 1.0.0 is installed", Reason(result, "needs_new_lib"));
        Assert.Equal("needs 'needs_missing', which does not load", Reason(result, "indirect"));
        Assert.Equal("needs 'indirect', which does not load", Reason(result, "deeper"));
        Assert.Equal(["lib", "fine"], Order(result));
        Assert.Null(Reason(result, "fine"));
    }

    [Fact]
    public void ARingOfPackagesIsDisabledTogetherAndWhatNeedsItToo()
    {
        SolveResult result = Solve(
            null,
            Candidate("a", dependencies: Dependency("b")),
            Candidate("b", dependencies: Dependency("c")),
            Candidate("c", dependencies: Dependency("a")),
            Candidate("consumer", dependencies: Dependency("a")),
            Candidate("free"));

        Assert.Equal("is part of a dependency cycle (a, b, c)", Reason(result, "a"));
        Assert.Equal("is part of a dependency cycle (a, b, c)", Reason(result, "c"));
        Assert.Equal("needs 'a', which does not load", Reason(result, "consumer"));
        Assert.Equal(["free"], Order(result));
    }

    [Fact]
    public void ThePlayerCanSwitchModsOffButNotTheBaseGame()
    {
        var disabled = new HashSet<string> { "mod_a", "core" };
        SolveResult result = Solve(
            new SolveOptions(UserDisabled: disabled),
            Candidate("core", kind: PackageKind.Base),
            Candidate("mod_a"),
            Candidate("mod_b", dependencies: Dependency("mod_a")),
            Candidate("mod_c"));

        Assert.Equal("is switched off", Reason(result, "mod_a"));
        Assert.Equal("needs 'mod_a', which does not load", Reason(result, "mod_b"));
        Assert.Equal(["core", "mod_c"], Order(result));
    }

    [Fact]
    public void ABasePackageThatNeedsAModDoesNotLoad()
    {
        SolveResult result = Solve(null, Candidate("mod_a"), Candidate("core", kind: PackageKind.Base, dependencies: Dependency("mod_a")));

        Assert.Equal("is a base package but needs the mod 'mod_a'", Reason(result, "core"));
        Assert.Equal(["mod_a"], Order(result));
    }

    [Fact]
    public void ThePackageApiAndTheGameVersionAreChecked()
    {
        SolveResult result = Solve(
            new SolveOptions(ApiVersion: 1, GameVersion: new PackageVersion(1, 5, 0)),
            Candidate("future", attributes: "apiVersion=\"2\""),
            Candidate("old_game", attributes: "gameVersion=\"&lt;1.0\""),
            Candidate("right_game", attributes: "gameVersion=\"^1.2\""));

        Assert.Equal("needs package API version 2, but this engine has 1", Reason(result, "future"));
        Assert.Equal("needs game version <1.0, but this is 1.5.0", Reason(result, "old_game"));
        Assert.Equal(["right_game"], Order(result));
        Assert.Equal(["x"], Order(Solve(null, Candidate("x", attributes: "gameVersion=\"&lt;1.0\""))));  // with no game version given, it is not checked
    }
}

public class PackageSetTests
{
    private sealed class Game : IDisposable
    {
        private readonly TempFolder _res = new();
        private readonly TempFolder _user = new();

        public Game()
        {
            Files = new VirtualFileSystem();
            Files.Mount("res://", new DirectorySource(_res.Root));
            Files.Mount("user://", new DirectorySource(_user.Root, writable: true));
        }

        public VirtualFileSystem Files { get; }

        public void Res(string path, string text) => _res.Write(path, text);

        public void User(string path, string text) => _user.Write(path, text);

        public string UserFolder => _user.Root;

        public void Dispose()
        {
            _res.Dispose();
            _user.Dispose();
        }
    }

    private static string Mod(string id, string inner = "", string version = "1.0.0") => $"<Mod id=\"{id}\" version=\"{version}\">{inner}</Mod>";

    [Fact]
    public void PackagesAreFoundInThePlacesAndMountedAtTheirIds()
    {
        using var game = new Game();
        game.Res("packages/core/mod.xml", Mod("core", "<Script path=\"main.luau\"/>"));
        game.Res("packages/core/main.luau", "return 1");
        game.Res("dev_mods/devmod/mod.xml", Mod("devmod", "<Dependency id=\"core\"/>"));
        game.User("mods/usermod/mod.xml", Mod("usermod", "<Dependency id=\"core\"/>"));
        game.User("mods/usermod/data/x.xml", "<X id=\"a\"/>");

        using var set = PackageSet.Load(game.Files);

        Assert.Equal(["core", "devmod", "usermod"], set.LoadOrder.Select(p => p.Id));
        Assert.Equal([PackageKind.Base, PackageKind.Dev, PackageKind.User], set.LoadOrder.Select(p => p.Kind));
        Assert.Empty(set.Problems);
        Assert.Equal("return 1", game.Files.ReadAllText("mod://core/main.luau"));
        Assert.Equal("<X id=\"a\"/>", game.Files.ReadAllText("mod://usermod/data/x.xml"));
        Assert.Equal(["mod://core", "mod://devmod", "mod://usermod"], game.Files.EnumerateDirectories("mod://"));
        Assert.Equal([("core", "mod://core/main.luau")], set.Scripts.Select(s => (s.Package.Id, s.Path)));
        Assert.Equal(Path.Combine(game.UserFolder, "mods", "usermod", "data", "x.xml"), game.Files.TryGetPhysicalPath("mod://usermod/data/x.xml"));
    }

    [Fact]
    public void AZippedModIsMountedLikeAFolder()
    {
        using var game = new Game();
        game.Res("packages/core/mod.xml", Mod("core"));
        string zipPath = Path.Combine(game.UserFolder, "mods", "zipped.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach ((string name, string text) in new[] { ("mod.xml", Mod("zipped", "<Dependency id=\"core\"/><Script path=\"main.luau\"/>")), ("main.luau", "return 2") })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }

        using var set = PackageSet.Load(game.Files);

        Assert.Equal(["core", "zipped"], set.LoadOrder.Select(p => p.Id));
        Assert.Equal("return 2", game.Files.ReadAllText("mod://zipped/main.luau"));
        Assert.Single(set.Scripts);
    }

    [Fact]
    public void WhatIsWrongWithTheSearchIsAProblemNotAnException()
    {
        using var game = new Game();
        game.Res("packages/core/mod.xml", Mod("core"));
        game.Res("packages/no_manifest/readme.txt", "x");
        game.Res("packages/wrong_name/mod.xml", Mod("something_else"));
        game.Res("packages/broken/mod.xml", "<Mod id=\"broken\"");
        game.User("mods/junk.zip", "not a zip");
        game.User("mods/empty/mod.xml", Mod("empty"));

        using var set = PackageSet.Load(game.Files);

        Assert.Equal(["core", "empty"], set.LoadOrder.Select(p => p.Id));
        Assert.Contains(set.Problems, p => p.Contains("no_manifest has no mod.xml", StringComparison.Ordinal));
        Assert.Contains(set.Problems, p => p.Contains("the id 'something_else' must be the name of the package's folder", StringComparison.Ordinal) && p.Contains("'wrong_name'", StringComparison.Ordinal));
        Assert.Contains(set.Problems, p => p.StartsWith("res://packages/broken/mod.xml:", StringComparison.Ordinal));
        Assert.Contains(set.Problems, p => p.Contains("junk.zip", StringComparison.Ordinal) && p.Contains("not a valid zip", StringComparison.Ordinal));
    }

    [Fact]
    public void ModsThePlayerSwitchedOffDoNotLoadAndAreNotMounted()
    {
        using var game = new Game();
        game.Res("packages/core/mod.xml", Mod("core"));
        game.User("mods/on/mod.xml", Mod("on"));
        game.User("mods/off/mod.xml", Mod("off"));
        UserModSettings.SaveDisabled(game.Files, ["off", "core", "off"]);

        using var set = PackageSet.Load(game.Files);

        Assert.Equal(["core", "on"], set.LoadOrder.Select(p => p.Id));
        Assert.Equal("is switched off", set.Find("off")!.Reason);
        Assert.False(game.Files.Exists("mod://off"));
        Assert.Equal(3, set.Packages.Count);
        Assert.Equal(["core", "off"], UserModSettings.LoadDisabled(game.Files).Order());
    }

    [Fact]
    public void ADamagedSettingsFileSwitchesNothingOff()
    {
        using var game = new Game();
        game.User("mods.xml", "<Mods><Disabled");

        Assert.Empty(UserModSettings.LoadDisabled(game.Files));
        Assert.Empty(UserModSettings.LoadDisabled(new VirtualFileSystem()));
    }

    [Fact]
    public void TheContentAndSchemasOfThePackagesBuildTheDefinitions()
    {
        using var game = new Game();
        game.Res("packages/core/mod.xml", Mod("core", "<Content path=\"data\"/><Schema path=\"schemas\"/>"));
        game.Res("packages/core/schemas/crate.xsd", "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"Crate\"><xs:complexType><xs:attribute name=\"id\" type=\"xs:string\" use=\"required\"/><xs:attribute name=\"weight\" type=\"xs:decimal\" default=\"1\"/></xs:complexType></xs:element></xs:schema>");
        game.Res("packages/core/data/crates.xml", "<Crates><Crate id=\"wooden\" weight=\"12\"/></Crates>");
        game.User("mods/bigger/mod.xml", Mod("bigger", "<Dependency id=\"core\" version=\"^1.0\"/><Content path=\"data\"/>"));
        game.User("mods/bigger/data/patch.xml", "<Crate id=\"wooden\" patch=\"true\" weight=\"20\"/>");

        using var set = PackageSet.Load(game.Files);
        DefDatabase defs = set.LoadDefs();

        Assert.Empty(defs.Problems);
        Assert.Equal("{id: \"wooden\", weight: 20}", defs.Get("Crate", "wooden")!.Value.ToString());
        Assert.Equal(["core", "bigger"], defs.Get("Crate", "wooden")!.Sources.Select(s => s.Package));
        Assert.Equal("mod://bigger/data/patch.xml", defs.Get("Crate", "wooden")!.Sources[1].Path);
    }

    [Fact]
    public void ABrokenSchemaInAModIsAProblemAndTheGameStillLoads()
    {
        using var game = new Game();
        game.Res("packages/core/mod.xml", Mod("core"));
        game.User("mods/bad/mod.xml", Mod("bad", "<Schema path=\"s\"/>"));
        game.User("mods/bad/s/x.xsd", "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:key name=\"k\"/></xs:schema>");

        using var set = PackageSet.Load(game.Files);
        SchemaSet schemas = set.LoadSchemas();

        Assert.Empty(schemas.ElementNames);
        Assert.Contains(set.Problems, p => p.Contains("<xs:key> is not supported", StringComparison.Ordinal));
    }

    [Fact]
    public void DisposingTakesTheMountsAway()
    {
        using var game = new Game();
        game.Res("packages/core/mod.xml", Mod("core"));
        game.User("mods/m/mod.xml", Mod("m"));
        var set = PackageSet.Load(game.Files);
        Assert.True(game.Files.DirectoryExists("mod://core"));

        set.Dispose();

        Assert.False(game.Files.Exists("mod://core"));
        Assert.False(game.Files.Exists("mod://m"));
    }

    [Fact]
    public void ThereCanBeNoPackagesAtAll()
    {
        using var set = PackageSet.Load(new VirtualFileSystem());

        Assert.Empty(set.Packages);
        Assert.Empty(set.Problems);
    }
}
