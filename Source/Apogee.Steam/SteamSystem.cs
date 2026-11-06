using System;
using System.Collections.Generic;
using System.Globalization;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.Steam;
using Apogee.Reflection;
using SteamApi = Apogee.Native.Steam.Steam;

namespace Apogee.Steam;

/// <summary>
/// The game's connection to Steam: the user, the overlay, achievements, stats and leaderboards, rich presence, cloud files and Workshop items. Steam's library is Valve's redistributable
/// and is in the repository; the client is the player's. <b>When Steam is not running, or the game was not started with an app id, <see cref="IsActive"/> is false and every call does
/// nothing and returns something inert</b>, so a game works the same without it.
/// </summary>
/// <remarks>
/// Call <see cref="Update"/> once a frame: it pumps Steam's callbacks (manual dispatch, all on the calling thread) and turns them into signals. Steam's ids (users, Workshop items) are 64-bit
/// and cross into scripts as decimal strings, because a script number cannot hold them. Mods cannot use this object: it is the game's.
/// </remarks>
[ApogeeClass(ModsBlocked = true)]
public sealed unsafe partial class SteamSystem : EngineObject, IDisposable
{
    private static readonly bool s_steamDeck = Environment.GetEnvironmentVariable("SteamDeck") == "1";

    private delegate void CallResult(byte* data, int size, bool ok);

    private readonly Dictionary<ulong, CallResult> _calls = [];
    private readonly Dictionary<string, ulong> _leaderboards = new(StringComparer.Ordinal);
    private readonly List<string> _failed = [];
    private ISteamUser* _user;
    private ISteamFriends* _friends;
    private ISteamUtils* _utils;
    private ISteamApps* _apps;
    private ISteamUserStats* _stats;
    private ISteamRemoteStorage* _storage;
    private ISteamUGC* _ugc;
    private ISteamScreenshots* _screenshots;
    private ISteamTimeline* _timeline;
    private int _pipe;
    private bool _active;

    /// <summary>Starts Steam. If it cannot be started the system is inactive, <see cref="Status"/> says why, and the reason is logged once.</summary>
    public SteamSystem(SteamOptions? options = null)
    {
        options ??= new SteamOptions();
        if (!options.Enabled)
        {
            Status = "Steam is off for this run.";
            return;
        }

        if (!IsInstalled())
        {
            Fail("Steam's library (steam_api) was not found next to the program.");
            return;
        }

        if (options.AppId != 0 && options.RestartIfNecessary && SteamApi.RestartAppIfNecessary(options.AppId))
        {
            RestartRequested = true;
            Fail($"The game was not started by Steam: Steam starts it again (app {options.AppId}).");
            return;
        }

        byte* message = stackalloc byte[1024];
        message[0] = 0;
        ESteamAPIInitResult result = SteamApi.InitFlat(message);
        if (result != ESteamAPIInitResult.OK)
        {
            Fail($"Steam did not start ({result}): {Utf8.FromPointer(message)}");
            return;
        }

        SteamApi.ManualDispatchInit();
        _pipe = SteamApi.GetHSteamPipe();
        _user = SteamApi.SteamUserV023();
        _friends = SteamApi.SteamFriendsV018();
        _utils = SteamApi.SteamUtilsV011();
        _apps = SteamApi.SteamAppsV009();
        _stats = SteamApi.SteamUserStatsV013();
        _storage = SteamApi.SteamRemoteStorageV016();
        _ugc = SteamApi.SteamUGCV021();
        _screenshots = SteamApi.SteamScreenshotsV003();
        _timeline = SteamApi.SteamTimelineV004();
        _active = true;
        Status = "Steam is running.";
        Log.Info("Steam", $"{UserName} ({UserId}), app {AppId}, language {Language}");
    }

    /// <summary>Makes an inactive Steam system from a script, which touches nothing of Steam: for testing the game's own code that expects a <c>steam</c> global. A game's real one is the global <c>steam</c>.</summary>
    [Method]
    public static SteamSystem CreateInactive() => new(new SteamOptions { Enabled = false });

    /// <summary>Whether Steam's library can be loaded.</summary>
    [Method]
    public static bool IsInstalled() => NativeLibraries.TryLoad(typeof(SteamApi).Assembly, "steam_api", out _);

    /// <summary>True when Steam started: the calls are real. False means every call does nothing.</summary>
    [Property]
    public bool IsActive => _active && !IsDestroyed;

    /// <summary>What state Steam is in, in words a player can read: running, off, or why it did not start.</summary>
    [Property]
    public string Status { get; private set; } = "";

    /// <summary>True when <see cref="SteamOptions.AppId"/> was set and the game was not started by Steam: Steam is starting it again, and this process should exit.</summary>
    [Property]
    public bool RestartRequested { get; private set; }

    internal ISteamUser* UserInterface => _user;

    internal ISteamUGC* Ugc => _ugc;

    internal ISteamRemoteStorage* Storage => _storage;

    private void Fail(string message)
    {
        Status = message;
        Log.Info("Steam", message + " Steam features stay off.");
    }

    internal static string Id(ulong id) => id.ToString(CultureInfo.InvariantCulture);

    internal static bool TryId(string? text, out ulong id)
    {
        id = 0;
        return text is not null && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
    }

    // ---- Signals ----------------------------------------------------------------------------------------------------------------

    /// <summary>The overlay opened (<c>true</c>) or closed: pause the game while it is open.</summary>
    [Signal]
    public Signal<bool> OverlayActivated { get; } = new();

    /// <summary>Steam wants the game to exit (the client is shutting down).</summary>
    [Signal]
    public Signal ShutdownRequested { get; } = new();

    /// <summary>The player's stats and achievements arrived from Steam (<c>true</c>) or could not be loaded.</summary>
    [Signal]
    public Signal<bool> StatsReceived { get; } = new();

    /// <summary>A <see cref="StoreStats"/> finished.</summary>
    [Signal]
    public Signal<bool> StatsStored { get; } = new();

    /// <summary>An achievement was unlocked or progressed, with its name and progress (0 and 0 for a plain unlock).</summary>
    [Signal]
    public Signal<string, int, int> AchievementStored { get; } = new();

    /// <summary>Steam's on-screen keyboard closed, with whether the text was submitted and the text.</summary>
    [Signal]
    public Signal<bool, string> TextInputDismissed { get; } = new();

    // ---- The frame --------------------------------------------------------------------------------------------------------------

    /// <summary>Once a frame: runs Steam's callbacks and call results and emits their signals. Nothing else runs on another thread.</summary>
    [Method]
    public void Update()
    {
        if (!IsActive)
        {
            return;
        }

        SteamApi.ManualDispatchRunFrame(_pipe);
        CallbackMsg message;
        while (SteamApi.ManualDispatchGetNextCallback(_pipe, &message))
        {
            try
            {
                Dispatch(in message);
            }
            catch (Exception e)
            {
                Log.Error("Steam", $"Callback {message.Callback} failed: {e}");
            }
            finally
            {
                SteamApi.ManualDispatchFreeLastCallback(_pipe);
            }
        }
    }

    private void Dispatch(in CallbackMsg message)
    {
        switch (message.Callback)
        {
            case SteamApi.SteamAPICallCompletedId:
                CompleteCall(*(SteamAPICallCompleted*)message.Param);
                break;
            case SteamApi.GameOverlayActivatedId:
                OverlayActivated.Emit(((GameOverlayActivated*)message.Param)->Active != 0);
                break;
            case SteamApi.SteamShutdownId:
                ShutdownRequested.Emit();
                break;
            case SteamApi.UserStatsReceivedId:
                {
                    UserStatsReceived* received = (UserStatsReceived*)message.Param;
                    if (received->IDUser == SteamApi.UserGetSteamID(_user))
                    {
                        StatsReceived.Emit(received->Result == EResult.OK);
                    }

                    break;
                }

            case SteamApi.UserStatsStoredId:
                StatsStored.Emit(((UserStatsStored*)message.Param)->Result == EResult.OK);
                break;
            case SteamApi.UserAchievementStoredId:
                {
                    UserAchievementStored* stored = (UserAchievementStored*)message.Param;
                    AchievementStored.Emit(Utf8.FromPointer(stored->AchievementName) ?? "", (int)stored->CurProgress, (int)stored->MaxProgress);
                    break;
                }

            case SteamApi.GamepadTextInputDismissedId:
                {
                    GamepadTextInputDismissed* dismissed = (GamepadTextInputDismissed*)message.Param;
                    TextInputDismissed.Emit(dismissed->Submitted, dismissed->Submitted ? EnteredText((int)dismissed->SubmittedText) : "");
                    break;
                }

            case SteamApi.ItemInstalledId:
                {
                    ItemInstalled* installed = (ItemInstalled*)message.Param;
                    if (installed->AppID == AppIdValue)
                    {
                        WorkshopItemInstalled.Emit(Id(installed->PublishedFileId));
                    }

                    break;
                }

            case SteamApi.DownloadItemResultId:
                {
                    DownloadItemResult* downloaded = (DownloadItemResult*)message.Param;
                    if (downloaded->AppID == AppIdValue)
                    {
                        WorkshopItemDownloaded.Emit(Id(downloaded->PublishedFileId), downloaded->Result == EResult.OK);
                    }

                    break;
                }
        }
    }

    private string EnteredText(int length)
    {
        byte* text = stackalloc byte[Math.Max(length, 1) + 1];
        return SteamApi.UtilsGetEnteredGamepadTextInput(_utils, text, (uint)length + 1) ? Utf8.FromPointer(text) ?? "" : "";
    }

    // ---- Call results -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Waits for the result of an asynchronous Steam call (<paramref name="call"/>, 0 when Steam refused to start it): <paramref name="onResult"/> runs from <see cref="Update"/> with the
    /// result, or <paramref name="onFailed"/> when the call failed.
    /// </summary>
    private void Watch<T>(ulong call, Action<T> onResult, Action? onFailed = null)
        where T : unmanaged
    {
        if (call == 0)
        {
            onFailed?.Invoke();
            return;
        }

        _calls[call] = (data, size, ok) =>
        {
            if (ok && size >= sizeof(T))
            {
                onResult(*(T*)data);
            }
            else
            {
                onFailed?.Invoke();
            }
        };
    }

    private void CompleteCall(in SteamAPICallCompleted completed)
    {
        if (!_calls.Remove(completed.AsyncCall, out CallResult? handler))
        {
            return; // somebody else's call (a library of the game that uses Steam by itself)
        }

        byte[] buffer = new byte[Math.Max((int)completed.Param, 1)];
        fixed (byte* data = buffer)
        {
            CBool failed;
            bool ok = SteamApi.ManualDispatchGetAPICallResult(_pipe, completed.AsyncCall, data, (int)completed.Param, completed.Callback, &failed) && !failed;
            handler(data, (int)completed.Param, ok);
        }
    }

    // ---- The user and the app ---------------------------------------------------------------------------------------------------

    private uint AppIdValue => IsActive ? SteamApi.UtilsGetAppID(_utils) : 0;

    /// <summary>The player's SteamID64 as a decimal string, "" when Steam is not running.</summary>
    [Property]
    public string UserId => IsActive ? Id(SteamApi.UserGetSteamID(_user)) : "";

    /// <summary>The player's Steam name.</summary>
    [Property]
    public string UserName => IsActive ? Utf8.FromPointer(SteamApi.FriendsGetPersonaName(_friends)) ?? "" : "";

    /// <summary>The game's Steam app id (0 when Steam is not running).</summary>
    [Property]
    public int AppId => (int)AppIdValue;

    /// <summary>The build of the game Steam installed (changes with every upload to a branch).</summary>
    [Property]
    public int BuildId => IsActive ? SteamApi.AppsGetAppBuildId(_apps) : 0;

    /// <summary>The language the player chose for the game in Steam, as Steam names it (<c>english</c>, <c>french</c>, <c>schinese</c>…); "" when Steam is not running.</summary>
    [Property]
    public string Language => IsActive ? Utf8.FromPointer(SteamApi.AppsGetCurrentGameLanguage(_apps)) ?? "" : "";

    /// <summary>The language of Steam's own interface.</summary>
    [Property]
    public string UiLanguage => IsActive ? Utf8.FromPointer(SteamApi.UtilsGetSteamUILanguage(_utils)) ?? "" : "";

    /// <summary>The player's country (two letters), from their address.</summary>
    [Property]
    public string Country => IsActive ? Utf8.FromPointer(SteamApi.UtilsGetIPCountry(_utils)) ?? "" : "";

    /// <summary>Whether the player owns the game (false for a family-shared or free-weekend copy that Steam still lets them play: <see cref="IsSubscribed"/> is about that account).</summary>
    [Property]
    public bool IsSubscribed => IsActive && SteamApi.AppsBIsSubscribed(_apps);

    /// <summary>The game runs on a Steam Deck (Steam sets the <c>SteamDeck</c> environment variable there).</summary>
    [Property]
    public bool IsSteamDeck => IsActive && s_steamDeck;

    /// <summary>Steam is in Big Picture mode (or the Deck's game mode).</summary>
    [Property]
    public bool IsBigPicture => IsActive && SteamApi.UtilsIsSteamInBigPictureMode(_utils);

    /// <summary>The player owns the DLC <paramref name="appId"/>.</summary>
    [Method]
    public bool OwnsDlc(int appId) => IsActive && SteamApi.AppsBIsSubscribedApp(_apps, (uint)appId);

    /// <summary>The DLC <paramref name="appId"/> is owned and installed.</summary>
    [Method]
    public bool IsDlcInstalled(int appId) => IsActive && SteamApi.AppsBIsDlcInstalled(_apps, (uint)appId);

    // ---- Disposal ---------------------------------------------------------------------------------------------------------------

    public void Dispose() => Destroy();

    protected override void OnDestroyed()
    {
        if (_active)
        {
            _active = false;
            _calls.Clear();
            SteamApi.Shutdown();
        }

        base.OnDestroyed();
    }
}
