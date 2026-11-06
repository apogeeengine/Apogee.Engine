using System;
using System.Collections.Generic;
using System.IO;
using Apogee.Content.FileSystem;
using Xunit;

namespace Apogee.Steam.Tests;

/// <summary>
/// The system against a real Steam client, with Valve's test app Spacewar (480). Off unless <c>APOGEE_STEAM_TESTS=1</c> and the Steam client is running and signed in: they change the
/// signed-in account's Spacewar stats and cloud files, which nobody minds, but they are not something to run by surprise.
/// </summary>
public sealed class SteamFixture : IDisposable
{
    public SteamSystem? Steam { get; }

    public SteamFixture()
    {
        if (Environment.GetEnvironmentVariable("APOGEE_STEAM_TESTS") != "1")
        {
            return;
        }

        // Steam finds the app id in a file next to the program (or in the working folder).
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "steam_appid.txt"), "480");
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "steam_appid.txt"), "480");
        Steam = new SteamSystem(new SteamOptions { AppId = 0 });
    }

    public void Dispose() => Steam?.Dispose();

    /// <summary>Runs the callbacks until <paramref name="done"/> is true, or fails after <paramref name="seconds"/>.</summary>
    public void PumpUntil(Func<bool> done, int seconds = 20)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Steam!.Update();
            System.Threading.Thread.Sleep(20);
        }

        Assert.True(done(), "Steam did not answer in time.");
    }
}

[CollectionDefinition("Steam")]
public sealed class SteamTestDefinition : ICollectionFixture<SteamFixture>;

[Collection("Steam")]
public sealed class LiveSteamTests(SteamFixture fixture)
{
    private SteamSystem Live()
    {
        Assert.SkipUnless(fixture.Steam is not null, "Set APOGEE_STEAM_TESTS=1 (and run the Steam client) to test against Steam.");
        Assert.True(fixture.Steam.IsActive, fixture.Steam.Status);
        return fixture.Steam;
    }

    [Fact]
    public void ItStartsAndKnowsThePlayer()
    {
        SteamSystem steam = Live();
        Assert.Equal(480, steam.AppId);
        Assert.NotEqual("", steam.UserId);
        Assert.NotEqual("", steam.UserName);
        Assert.NotEqual("", steam.Language);
    }

    [Fact]
    public void AStatSurvivesAStore()
    {
        SteamSystem steam = Live();
        bool received = false;
        steam.StatsReceived.Connect(ok => received = ok);
        fixture.PumpUntil(() => received || steam.StatInt("NumGames") >= 0, 5);

        int before = steam.StatInt("NumGames");
        Assert.True(steam.SetStatInt("NumGames", before + 1));
        bool stored = false;
        steam.StatsStored.Connect(ok => stored = ok);
        Assert.True(steam.StoreStats());
        fixture.PumpUntil(() => stored);
        Assert.Equal(before + 1, steam.StatInt("NumGames"));
    }

    [Fact]
    public void ALeaderboardIsFoundAndItsEntriesRead()
    {
        SteamSystem steam = Live();
        bool? found = null;
        steam.LeaderboardFound.Connect((_, ok) => found = ok);
        steam.FindLeaderboard("Quickest Win");
        fixture.PumpUntil(() => found is not null);
        Assert.True(found);

        string[]? users = null;
        steam.ScoresDownloaded.Connect((_, u, _, _) => users = u);
        steam.DownloadScores("Quickest Win", 1, 5);
        fixture.PumpUntil(() => users is not null);
    }

    [Fact]
    public void CloudFilesRoundTrip()
    {
        SteamSystem steam = Live();
        Assert.SkipUnless(steam.IsCloudEnabled, "Steam Cloud is off for this account or app.");
        IFileSource cloud = steam.CreateCloudSource(Path.Combine(Path.GetTempPath(), "apogee-cloud-fallback"));
        var files = new VirtualFileSystem();
        files.Mount("cloud://", cloud);

        files.WriteAllText("cloud://apogee-test/hello.txt", "hello");
        try
        {
            Assert.Equal("hello", files.ReadAllText("cloud://apogee-test/hello.txt"));
            Assert.Contains("cloud://apogee-test/hello.txt", files.EnumerateFiles("cloud://apogee-test", "*"));
            Assert.True(files.DirectoryExists("cloud://apogee-test"));
            (long total, long available) = steam.CloudQuota();
            Assert.True(total > 0 && available > 0);
        }
        finally
        {
            files.Delete("cloud://apogee-test");
        }

        Assert.False(files.Exists("cloud://apogee-test/hello.txt"));
    }

    [Fact]
    public void SubscribedWorkshopItemsAreListed()
    {
        SteamSystem steam = Live();
        IReadOnlyList<(string ItemId, string Folder)> installed = steam.InstalledWorkshopItems();
        Assert.All(installed, item => Assert.True(Directory.Exists(item.Folder)));
    }
}
