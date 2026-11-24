using System;
using System.IO;
using Weft.Content.Packages;
using Xunit;

namespace Weft.Content.Tests;

public class GameManifestTests
{
    private const string Full = """
        <Game name="Fixture Game" organization="Weft" version="1.4.2" executable="FixtureGame" identifier="org.weft.fixturegame" userFolder="fixture-game"
              steamAppId="480" description="A game &amp; its fixture." copyright="Copyright 2026 The Weft Authors">
          <EntryScene path="scenes/main.ascene"/>
          <Icon path="art/icon.png"/>
          <Window width="1920" height="1080" fullscreen="true" resizable="false"/>
        </Game>
        """;

    private static GameManifest Parse(string text)
    {
        Assert.True(GameManifest.TryParse(text, "game.xml", out GameManifest? manifest, out System.Collections.Generic.IReadOnlyList<string> problems), string.Join("\n", problems));
        Assert.Empty(problems);
        return manifest!;
    }

    private static string[] Problems(string text)
    {
        Assert.False(GameManifest.TryParse(text, "game.xml", out GameManifest? manifest, out System.Collections.Generic.IReadOnlyList<string> problems));
        Assert.Null(manifest);
        Assert.NotEmpty(problems);
        return [.. problems];
    }

    [Fact]
    public void AFullManifestReadsEveryField()
    {
        GameManifest game = Parse(Full);

        Assert.Equal("Fixture Game", game.Name);
        Assert.Equal("Weft", game.Organization);
        Assert.Equal(new PackageVersion(1, 4, 2), game.Version);
        Assert.Equal("FixtureGame", game.Executable);
        Assert.Equal("org.weft.fixturegame", game.Identifier);
        Assert.Equal("fixture-game", game.UserFolder);
        Assert.Equal(480u, game.SteamAppId);
        Assert.Equal("A game & its fixture.", game.Description);
        Assert.Equal("Copyright 2026 The Weft Authors", game.Copyright);
        Assert.Equal("scenes/main.ascene", game.EntryScene);
        Assert.Equal("art/icon.png", game.Icon);
        Assert.Equal(new GameWindow(1920, 1080, true, false), game.Window);
    }

    [Fact]
    public void DefaultsComeFromTheName()
    {
        GameManifest game = Parse("""<Game name="My Game: Deluxe!" organization="Studio" version="1.0.0"/>""");

        Assert.Equal("MyGameDeluxe", game.Executable);
        Assert.Equal("com.studio.mygamedeluxe", game.Identifier);
        Assert.Equal("My Game: Deluxe!", game.UserFolder);
        Assert.Equal(Path.Combine("data", "Studio", "My Game Deluxe!"), game.UserDirectory("data"));
        Assert.Equal(0u, game.SteamAppId);
        Assert.Equal("", game.Description);
        Assert.Equal("", game.Copyright);
        Assert.Null(game.EntryScene);
        Assert.Null(game.Icon);
        Assert.Equal(new GameWindow(1280, 720, false, true), game.Window);
    }

    [Fact]
    public void AnExecutableChangesTheDefaultIdentifierAndAWindowWithoutAttributesHasTheDefaults()
    {
        GameManifest game = Parse("""<Game name="Space Truckers" organization="Big Studio Inc." version="2.0.0" executable="Truckers"><Window/></Game>""");

        Assert.Equal("com.bigstudioinc.truckers", game.Identifier);
        Assert.Equal(GameWindow.Default, game.Window);
    }

    [Fact]
    public void ANameWithNothingToMakeAnExecutableOfStillHasOne()
    {
        GameManifest game = Parse("""<Game name=":::" organization="..." version="1.0.0"/>""");

        Assert.Equal("Game", game.Executable);
        Assert.Equal("com.studio.game", game.Identifier);
        Assert.Equal(Path.Combine("data", "Game", "Game"), game.UserDirectory("data"));
    }

    [Fact]
    public void MistakesAreReportedWithTheirLine()
    {
        string[] noName = Problems("<Game organization=\"a\" version=\"1.0.0\"/>");
        Assert.Contains(noName, p => p.StartsWith("game.xml:1: ", StringComparison.Ordinal) && p.Contains("name", StringComparison.Ordinal));

        string[] shortVersion = Problems("<Game name=\"a\" organization=\"b\" version=\"1.0\"/>");
        Assert.Contains(shortVersion, p => p.Contains("version", StringComparison.Ordinal));

        string[] spaced = Problems("<Game name=\"a\" organization=\"b\" version=\"1.0.0\" executable=\"a b\"/>");
        Assert.Contains(spaced, p => p.Contains("executable", StringComparison.Ordinal));

        string[] negative = Problems("<Game name=\"a\" organization=\"b\" version=\"1.0.0\" steamAppId=\"-3\"/>");
        Assert.Contains(negative, p => p.Contains("steamAppId", StringComparison.Ordinal));

        string[] identifier = Problems("<Game name=\"a\" organization=\"b\" version=\"1.0.0\" identifier=\"nodots\"/>");
        Assert.Contains(identifier, p => p.Contains("identifier", StringComparison.Ordinal));

        string[] small = Problems("<Game name=\"a\" organization=\"b\" version=\"1.0.0\">\n  <Window width=\"8\"/>\n</Game>");
        Assert.Contains(small, p => p.StartsWith("game.xml:2: ", StringComparison.Ordinal) && p.Contains("width", StringComparison.Ordinal));

        string[] twice = Problems("<Game name=\"a\" organization=\"b\" version=\"1.0.0\">\n  <Icon path=\"a.png\"/>\n  <Icon path=\"b.png\"/>\n</Game>");
        Assert.NotEmpty(twice);

        string[] root = Problems("<Mod id=\"a\" version=\"1.0.0\"/>");
        Assert.Equal(["game.xml:1: the root element is <Mod>, not <Game>."], root);

        string[] broken = Problems("<Game name=\"a\"");
        Assert.StartsWith("game.xml:", broken[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/icon.png")]
    [InlineData("../icon.png")]
    [InlineData("a/../../icon.png")]
    [InlineData("C:/icon.png")]
    [InlineData("res://icon.png")]
    [InlineData("a/")]
    public void PathsHaveToStayInsideTheGame(string path)
    {
        string[] icon = Problems($"<Game name=\"a\" organization=\"b\" version=\"1.0.0\">\n<Icon path=\"{path}\"/>\n</Game>");
        Assert.Contains(icon, p => p.StartsWith("game.xml:2: ", StringComparison.Ordinal) && p.Contains("Icon", StringComparison.Ordinal));

        string[] scene = Problems($"<Game name=\"a\" organization=\"b\" version=\"1.0.0\">\n<EntryScene path=\"{path}\"/>\n</Game>");
        Assert.Contains(scene, p => p.StartsWith("game.xml:2: ", StringComparison.Ordinal) && p.Contains("EntryScene", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIconHasToBeAPng()
    {
        string[] problems = Problems("<Game name=\"a\" organization=\"b\" version=\"1.0.0\">\n<Icon path=\"icon.jpg\"/>\n</Game>");

        Assert.Contains(problems, p => p.StartsWith("game.xml:2: ", StringComparison.Ordinal) && p.Contains(".png", StringComparison.Ordinal));
    }

    [Fact]
    public void ToXmlRoundTrips()
    {
        foreach (string text in new[] { Full, """<Game name="A &lt;b&gt;" organization="Q &amp; Co" version="3.0.1"/>""" })
        {
            GameManifest game = Parse(text);
            GameManifest again = Parse(game.ToXml());

            Assert.Equal(game.ToXml(), again.ToXml());
            Assert.Equal(
                (game.Name, game.Organization, game.Version, game.Executable, game.Identifier, game.UserFolder, game.SteamAppId, game.Description, game.Copyright, game.EntryScene, game.Icon, game.Window),
                (again.Name, again.Organization, again.Version, again.Executable, again.Identifier, again.UserFolder, again.SteamAppId, again.Description, again.Copyright, again.EntryScene, again.Icon, again.Window));
        }
    }

    [Fact]
    public void AnotherIconMakesACopyAndLeavesTheManifestAlone()
    {
        GameManifest game = Parse(Full);

        GameManifest without = game.WithIcon(null);
        GameManifest other = game.WithIcon("icons/game.png");

        Assert.NotNull(game.Icon);
        Assert.Null(without.Icon);
        Assert.Equal("icons/game.png", other.Icon);
        Assert.DoesNotContain("<Icon", without.ToXml(), StringComparison.Ordinal);
        Assert.Equal((game.Name, game.Version, game.Window, game.SteamAppId), (other.Name, other.Version, other.Window, other.SteamAppId));
    }

    [Fact]
    public void TheUserDirectoryIsUnderTheOrganization()
    {
        GameManifest game = Parse("""<Game name="Fixture Game" organization="The: Studio?" version="1.0.0" userFolder="saves/../x."/>""");

        Assert.Equal(Path.Combine("local", "The Studio", "saves..x"), game.UserDirectory("local"));
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), game.UserDirectory(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Plain Name", "Plain Name")]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "abcdefghij")]
    [InlineData("tab\tand\nnewline", "tabandnewline")]
    [InlineData("trailing. . ", "trailing")]
    [InlineData("", "Game")]
    [InlineData("...", "Game")]
    [InlineData("  ", "Game")]
    [InlineData("..", "Game")]
    public void FileNamesAreMadeValid(string name, string expected) => Assert.Equal(expected, GameManifest.SanitizeFileName(name));

    [Fact]
    public void NoFileIsNoManifestAndNoProblem()
    {
        string folder = Path.Combine(Path.GetTempPath(), "weft-game-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(GameManifest.Load(folder, out System.Collections.Generic.IReadOnlyList<string> none));
            Assert.Empty(none);

            File.WriteAllText(Path.Combine(folder, GameManifest.FileName), Full);
            GameManifest? loaded = GameManifest.Load(folder, out System.Collections.Generic.IReadOnlyList<string> fine);
            Assert.Empty(fine);
            Assert.Equal("Fixture Game", loaded!.Name);

            File.WriteAllText(Path.Combine(folder, GameManifest.FileName), "<Game/>");
            Assert.Null(GameManifest.Load(folder, out System.Collections.Generic.IReadOnlyList<string> wrong));
            Assert.All(wrong, p => Assert.Contains("game.xml:1:", p, StringComparison.Ordinal));
            Assert.NotEmpty(wrong);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheFixtureGameHasAValidManifest()
    {
        string? folder = null;
        for (string? directory = AppContext.BaseDirectory; directory is not null && folder is null; directory = Path.GetDirectoryName(directory))
        {
            string candidate = Path.Combine(directory, "Tests", "Fixtures", "ShippedGame");
            folder = Directory.Exists(candidate) ? candidate : null;
        }

        Assert.NotNull(folder);
        GameManifest? game = GameManifest.Load(folder, out System.Collections.Generic.IReadOnlyList<string> problems);
        Assert.Empty(problems);
        Assert.Equal(("Fixture Game", "FixtureGame", "icon.png"), (game!.Name, game.Executable, game.Icon));
        Assert.True(File.Exists(Path.Combine(folder, game.Icon!)));
    }
}
