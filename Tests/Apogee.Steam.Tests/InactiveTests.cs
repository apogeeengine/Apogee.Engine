using System.Collections.Generic;
using Apogee.Steam;
using Xunit;

namespace Apogee.Steam.Tests;

/// <summary>
/// A game runs the same without Steam: the system is inactive, every call does nothing and answers with something inert. No Steam client is needed, nor is anything of Steam initialized.
/// </summary>
public class InactiveTests
{
    private static SteamSystem Inactive() => new(new SteamOptions { Enabled = false });

    [Fact]
    public void ItIsInactiveAndSaysWhy()
    {
        using SteamSystem steam = Inactive();
        Assert.False(steam.IsActive);
        Assert.False(steam.RestartRequested);
        Assert.NotEmpty(steam.Status);
        Assert.Equal("", steam.UserId);
        Assert.Equal("", steam.UserName);
        Assert.Equal("", steam.Language);
        Assert.Equal(0, steam.AppId);
        Assert.False(steam.IsOverlayEnabled);
        Assert.False(steam.IsCloudEnabled);
        Assert.False(steam.IsSubscribed);
        Assert.False(steam.IsSteamDeck);
        steam.Update(); // does nothing
    }

    [Fact]
    public void AchievementsAndStatsAreInert()
    {
        using SteamSystem steam = Inactive();
        Assert.False(steam.Achievement("ACH_WIN_ONE_GAME"));
        Assert.False(steam.UnlockAchievement("ACH_WIN_ONE_GAME"));
        Assert.False(steam.ClearAchievement("ACH_WIN_ONE_GAME"));
        Assert.False(steam.AchievementProgress("ACH_TRAVEL_FAR", 1, 10));
        Assert.Empty(steam.AchievementNames());
        Assert.Equal(0, steam.StatInt("NumGames"));
        Assert.Equal(0f, steam.StatFloat("FeetTraveled"));
        Assert.False(steam.SetStatInt("NumGames", 3));
        Assert.False(steam.SetStatFloat("FeetTraveled", 3));
        Assert.False(steam.StoreStats());
        Assert.False(steam.ResetAllStats(true));
        Assert.Equal(0, steam.StatInt("NumGames"));
    }

    [Fact]
    public void LeaderboardCallsAnswerThatTheyFailed()
    {
        using SteamSystem steam = Inactive();
        var log = new List<string>();
        steam.LeaderboardFound.Connect((name, found) => log.Add($"found {name} {found}"));
        steam.ScoreUploaded.Connect((name, ok, rank) => log.Add($"uploaded {name} {ok} {rank}"));
        steam.ScoresDownloaded.Connect((name, users, ranks, scores) => log.Add($"scores {name} {users.Length}/{ranks.Length}/{scores.Length}"));

        steam.FindLeaderboard("fastest");
        steam.UploadScore("fastest", 100);
        steam.DownloadScores("fastest", 1, 5);

        Assert.Equal(["found fastest False", "uploaded fastest False 0", "scores fastest 0/0/0"], log);
    }

    [Fact]
    public void WorkshopCallsAnswerThatTheyFailed()
    {
        using SteamSystem steam = Inactive();
        var log = new List<string>();
        steam.WorkshopSubscribed.Connect((id, ok) => log.Add($"subscribed {id} {ok}"));
        steam.WorkshopUnsubscribed.Connect((id, ok) => log.Add($"unsubscribed {id} {ok}"));

        steam.Subscribe("123");
        steam.Unsubscribe("not a number");

        Assert.Equal(["subscribed 123 False", "unsubscribed not a number False"], log);
        Assert.Empty(steam.SubscribedItems());
        Assert.Empty(steam.InstalledWorkshopItems());
        Assert.Equal("", steam.WorkshopItemFolder("123"));
        Assert.False(steam.DownloadWorkshopItem("123"));
    }

    [Fact]
    public void SocialCallsDoNothing()
    {
        using SteamSystem steam = Inactive();
        steam.ActivateOverlay("friends");
        steam.OpenWebPage("https://example.com");
        steam.OpenStore(480);
        Assert.False(steam.SetRichPresence("status", "In the garage"));
        steam.ClearRichPresence();
        steam.TriggerScreenshot();
        Assert.False(steam.AddScreenshot("missing.png", 100, 100));
        steam.AddTimelineEvent("Crash", "Hit a tree", "steam_death");
        steam.SetTimelineState("In a race");
        steam.SetTimelineMode("menus");
        Assert.False(steam.ShowTextInput("Name"));
        Assert.False(steam.OwnsDlc(480));
        Assert.False(steam.IsDlcInstalled(480));
        Assert.Equal((0L, 0L), steam.CloudQuota());
    }

    [Fact]
    public void AnUploadOfAnInactiveSystemFailsAtOnce()
    {
        using SteamSystem steam = Inactive();
        WorkshopUploadResult? result = null;
        steam.UploadWorkshopItem(new WorkshopUpload { Folder = "nowhere" }, r => result = r);
        Assert.NotNull(result);
        Assert.False(result.Value.Ok);
        Assert.Equal(("", 0UL, 0UL), (steam.UploadProgress().Stage, steam.UploadProgress().Sent, steam.UploadProgress().Total));
    }

    [Fact]
    public void ItIsInactiveWhenTheLibraryIsMissingOrSteamIsNotRunning()
    {
        // Enabled, but without a Steam client (or, where the library is not installed, without the library): either way the game must start.
        using var steam = new SteamSystem(new SteamOptions { Enabled = true, AppId = 0 });
        if (!steam.IsActive)
        {
            Assert.NotEmpty(steam.Status);
            Assert.False(steam.RestartRequested);
        }
    }
}
