using System;
using Apogee.Interop;
using Apogee.Native.Steam;
using Apogee.Reflection;
using SteamApi = Apogee.Native.Steam.Steam;

namespace Apogee.Steam;

// Achievements, stats and leaderboards. Steam loads the player's stats when the game starts (StatsReceived says when); setting one only changes it in memory until StoreStats.
public sealed unsafe partial class SteamSystem
{
    /// <summary>Whether the achievement is unlocked (false when Steam is not running, or the name is not one of the game's).</summary>
    [Method]
    public bool Achievement(string name)
    {
        if (!IsActive)
        {
            return false;
        }

        fixed (byte* key = Utf8.ToZeroTerminated(name))
        {
            CBool achieved;
            return SteamApi.UserStatsGetAchievement(_stats, key, &achieved) && achieved;
        }
    }

    /// <summary>Unlocks an achievement and sends it to Steam at once (it shows the pop-up). False when there is no such achievement or Steam is not running.</summary>
    [Method]
    public bool UnlockAchievement(string name)
    {
        if (!IsActive)
        {
            return false;
        }

        fixed (byte* key = Utf8.ToZeroTerminated(name))
        {
            return SteamApi.UserStatsSetAchievement(_stats, key) && SteamApi.UserStatsStoreStats(_stats);
        }
    }

    /// <summary>Locks an achievement again (for testing).</summary>
    [Method]
    public bool ClearAchievement(string name)
    {
        if (!IsActive)
        {
            return false;
        }

        fixed (byte* key = Utf8.ToZeroTerminated(name))
        {
            return SteamApi.UserStatsClearAchievement(_stats, key) && SteamApi.UserStatsStoreStats(_stats);
        }
    }

    /// <summary>Shows a progress pop-up for an achievement that counts (<paramref name="current"/> of <paramref name="max"/>) without unlocking it; it also stores the progress.</summary>
    [Method]
    public bool AchievementProgress(string name, int current, int max)
    {
        if (!IsActive)
        {
            return false;
        }

        fixed (byte* key = Utf8.ToZeroTerminated(name))
        {
            return SteamApi.UserStatsIndicateAchievementProgress(_stats, key, (uint)Math.Max(current, 0), (uint)Math.Max(max, 0));
        }
    }

    /// <summary>The names of the game's achievements, as set up in Steamworks.</summary>
    [Method]
    public string[] AchievementNames()
    {
        if (!IsActive)
        {
            return [];
        }

        string[] names = new string[SteamApi.UserStatsGetNumAchievements(_stats)];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = Utf8.FromPointer(SteamApi.UserStatsGetAchievementName(_stats, (uint)i)) ?? "";
        }

        return names;
    }

    /// <summary>The value of an integer stat (0 when there is none).</summary>
    [Method]
    public int StatInt(string name)
    {
        if (!IsActive)
        {
            return 0;
        }

        fixed (byte* key = Utf8.ToZeroTerminated(name))
        {
            int value;
            return SteamApi.UserStatsGetStatInt32(_stats, key, &value) ? value : 0;
        }
    }

    /// <summary>The value of a float stat (0 when there is none).</summary>
    [Method]
    public float StatFloat(string name)
    {
        if (!IsActive)
        {
            return 0;
        }

        fixed (byte* key = Utf8.ToZeroTerminated(name))
        {
            float value;
            return SteamApi.UserStatsGetStatFloat(_stats, key, &value) ? value : 0;
        }
    }

    /// <summary>Sets an integer stat in memory; <see cref="StoreStats"/> sends the changes.</summary>
    [Method]
    public bool SetStatInt(string name, int value)
    {
        if (!IsActive)
        {
            return false;
        }

        fixed (byte* key = Utf8.ToZeroTerminated(name))
        {
            return SteamApi.UserStatsSetStatInt32(_stats, key, value);
        }
    }

    /// <summary>Sets a float stat in memory; <see cref="StoreStats"/> sends the changes.</summary>
    [Method]
    public bool SetStatFloat(string name, float value)
    {
        if (!IsActive)
        {
            return false;
        }

        fixed (byte* key = Utf8.ToZeroTerminated(name))
        {
            return SteamApi.UserStatsSetStatFloat(_stats, key, value);
        }
    }

    /// <summary>Sends the changed stats and achievements to Steam; <see cref="StatsStored"/> says when it is done. Do not call it more than once a few minutes: Steam limits it.</summary>
    [Method]
    public bool StoreStats() => IsActive && SteamApi.UserStatsStoreStats(_stats);

    /// <summary>Resets every stat, and the achievements too when asked. For testing a new player.</summary>
    [Method]
    public bool ResetAllStats(bool achievements) => IsActive && SteamApi.UserStatsResetAllStats(_stats, achievements) && SteamApi.UserStatsStoreStats(_stats);

    // ---- Leaderboards -----------------------------------------------------------------------------------------------------------

    /// <summary>A leaderboard was looked up by name (<c>true</c> when it exists).</summary>
    [Signal]
    public Signal<string, bool> LeaderboardFound { get; } = new();

    /// <summary>A score was sent: the leaderboard, whether it worked, and the player's new global rank (0 when unchanged or failed).</summary>
    [Signal]
    public Signal<string, bool, int> ScoreUploaded { get; } = new();

    /// <summary>Entries arrived: the leaderboard, then for each entry the player's id, global rank and score, in rank order.</summary>
    [Signal]
    public Signal<string, string[], int[], int[]> ScoresDownloaded { get; } = new();

    /// <summary>Looks a leaderboard up by name; <see cref="LeaderboardFound"/> follows. The other leaderboard calls do this by themselves when they need to.</summary>
    [Method]
    public void FindLeaderboard(string name) => WithLeaderboard(name, _ => LeaderboardFound.Emit(name, true), () => LeaderboardFound.Emit(name, false));

    /// <summary>
    /// Sends a score; <see cref="ScoreUploaded"/> follows. With <paramref name="keepBest"/> the leaderboard keeps the player's better score (by the leaderboard's sort order), otherwise this one replaces it.
    /// </summary>
    [Method]
    public void UploadScore(string leaderboard, int score, bool keepBest = true) =>
        WithLeaderboard(leaderboard, handle => Watch<LeaderboardScoreUploaded>(
            SteamApi.UserStatsUploadLeaderboardScore(_stats, handle, keepBest ? ELeaderboardUploadScoreMethod.KeepBest : ELeaderboardUploadScoreMethod.ForceUpdate, score, null, 0),
            uploaded => ScoreUploaded.Emit(leaderboard, uploaded.Success != 0, uploaded.ScoreChanged != 0 ? uploaded.GlobalRankNew : 0),
            () => ScoreUploaded.Emit(leaderboard, false, 0)),
            () => ScoreUploaded.Emit(leaderboard, false, 0));

    /// <summary>
    /// Asks for the entries from rank <paramref name="from"/> to <paramref name="to"/> (1 is the best) of <paramref name="kind"/> (<c>global</c>, <c>around_user</c>, where the range is
    /// relative to the player, or <c>friends</c>); <see cref="ScoresDownloaded"/> follows, with no entries when it failed.
    /// </summary>
    [Method]
    public void DownloadScores(string leaderboard, int from = 1, int to = 10, string kind = "global")
    {
        ELeaderboardDataRequest request = kind switch
        {
            "around_user" => ELeaderboardDataRequest.GlobalAroundUser,
            "friends" => ELeaderboardDataRequest.Friends,
            _ => ELeaderboardDataRequest.Global,
        };

        void Empty() => ScoresDownloaded.Emit(leaderboard, [], [], []);
        WithLeaderboard(leaderboard, handle => Watch<LeaderboardScoresDownloaded>(
            SteamApi.UserStatsDownloadLeaderboardEntries(_stats, handle, request, from, to),
            downloaded => ReadEntries(leaderboard, downloaded),
            Empty), Empty);
    }

    private void ReadEntries(string leaderboard, in LeaderboardScoresDownloaded downloaded)
    {
        int count = Math.Max(downloaded.EntryCount, 0);
        string[] users = new string[count];
        int[] ranks = new int[count];
        int[] scores = new int[count];
        for (int i = 0; i < count; i++)
        {
            LeaderboardEntry entry;
            if (!SteamApi.UserStatsGetDownloadedLeaderboardEntry(_stats, downloaded.SteamLeaderboardEntries, i, &entry, null, 0))
            {
                Array.Resize(ref users, i);
                Array.Resize(ref ranks, i);
                Array.Resize(ref scores, i);
                break;
            }

            users[i] = Id(entry.IDUser);
            ranks[i] = entry.GlobalRank;
            scores[i] = entry.Score;
        }

        ScoresDownloaded.Emit(leaderboard, users, ranks, scores);
    }

    private void WithLeaderboard(string name, Action<ulong> then, Action failed)
    {
        if (!IsActive)
        {
            failed();
            return;
        }

        if (_leaderboards.TryGetValue(name, out ulong known))
        {
            then(known);
            return;
        }

        fixed (byte* key = Utf8.ToZeroTerminated(name))
        {
            Watch<LeaderboardFindResult>(SteamApi.UserStatsFindLeaderboard(_stats, key), found =>
            {
                if (found.LeaderboardFound != 0)
                {
                    _leaderboards[name] = found.SteamLeaderboard;
                    then(found.SteamLeaderboard);
                }
                else
                {
                    failed();
                }
            }, failed);
        }
    }
}
