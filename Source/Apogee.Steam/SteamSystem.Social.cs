using System;
using Apogee.Interop;
using Apogee.Native.Steam;
using Apogee.Reflection;
using SteamApi = Apogee.Native.Steam.Steam;

namespace Apogee.Steam;

// The overlay, rich presence, screenshots, the timeline, and the on-screen keyboard.
public sealed unsafe partial class SteamSystem
{
    /// <summary>Whether the overlay works in this run (Steam is running and the player did not switch it off).</summary>
    [Property]
    public bool IsOverlayEnabled => IsActive && SteamApi.UtilsIsOverlayEnabled(_utils);

    /// <summary>
    /// Opens a part of the overlay: <c>friends</c>, <c>community</c>, <c>players</c>, <c>settings</c>, <c>officialgamegroup</c>, <c>stats</c> or <c>achievements</c>; "" opens it as Shift+Tab does.
    /// </summary>
    [Method]
    public void ActivateOverlay(string dialog = "")
    {
        if (IsOverlayEnabled)
        {
            fixed (byte* name = Utf8.ToZeroTerminated(dialog))
            {
                SteamApi.FriendsActivateGameOverlay(_friends, name);
            }
        }
    }

    /// <summary>Opens the overlay's browser on a web page.</summary>
    [Method]
    public void OpenWebPage(string url)
    {
        if (IsOverlayEnabled)
        {
            fixed (byte* page = Utf8.ToZeroTerminated(url))
            {
                SteamApi.FriendsActivateGameOverlayToWebPage(_friends, page, EActivateGameOverlayToWebPageMode.Default);
            }
        }
    }

    /// <summary>Opens the store page of an app in the overlay (the game's own page, or a DLC's).</summary>
    [Method]
    public void OpenStore(int appId)
    {
        if (IsOverlayEnabled)
        {
            SteamApi.FriendsActivateGameOverlayToStore(_friends, (uint)appId, EOverlayToStoreFlag.None);
        }
    }

    /// <summary>
    /// Sets a rich presence key, which friends see in their list (<c>status</c> is the line shown; others are for <c>steam_display</c> localization). An empty value removes the key.
    /// </summary>
    [Method]
    public bool SetRichPresence(string key, string value)
    {
        if (!IsActive)
        {
            return false;
        }

        fixed (byte* k = Utf8.ToZeroTerminated(key))
        fixed (byte* v = Utf8.ToZeroTerminated(value))
        {
            return SteamApi.FriendsSetRichPresence(_friends, k, value.Length == 0 ? null : v);
        }
    }

    /// <summary>Removes all of this game's rich presence.</summary>
    [Method]
    public void ClearRichPresence()
    {
        if (IsActive)
        {
            SteamApi.FriendsClearRichPresence(_friends);
        }
    }

    // ---- Screenshots ------------------------------------------------------------------------------------------------------------

    /// <summary>Asks Steam to take a screenshot of the game's window (the player's screenshot key does it too).</summary>
    [Method]
    public void TriggerScreenshot()
    {
        if (IsActive)
        {
            SteamApi.ScreenshotsTriggerScreenshot(_screenshots);
        }
    }

    /// <summary>Adds a screenshot file the game wrote (a JPEG, PNG or TGA on the disk) to the player's Steam library. False when Steam refused it.</summary>
    [Method]
    public bool AddScreenshot(string path, int width, int height)
    {
        if (!IsActive)
        {
            return false;
        }

        fixed (byte* file = Utf8.ToZeroTerminated(path))
        {
            return SteamApi.ScreenshotsAddScreenshotToLibrary(_screenshots, file, null, width, height) != 0;
        }
    }

    // ---- Timeline ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Marks a moment in the player's Steam timeline (the game recording): a <paramref name="title"/>, a <paramref name="description"/>, one of Steam's icon names
    /// (<c>steam_achievement</c>, <c>steam_death</c>, <c>steam_marker</c>…), a priority among the icons, and an offset in seconds from now (negative: something that just happened). A <paramref name="featured"/>
    /// event is offered first as a clip.
    /// </summary>
    [Method]
    public void AddTimelineEvent(string title, string description = "", string icon = "steam_marker", int priority = 0, float offsetSeconds = 0, bool featured = false)
    {
        if (!IsActive)
        {
            return;
        }

        fixed (byte* t = Utf8.ToZeroTerminated(title))
        fixed (byte* d = Utf8.ToZeroTerminated(description))
        fixed (byte* i = Utf8.ToZeroTerminated(icon))
        {
            SteamApi.TimelineAddInstantaneousTimelineEvent(_timeline, t, d, i, (uint)Math.Max(priority, 0), offsetSeconds, featured ? ETimelineEventClipPriority.Featured : ETimelineEventClipPriority.Standard);
        }
    }

    /// <summary>Sets the line of text that describes what the player is doing now on the timeline (a tooltip); "" clears it.</summary>
    [Method]
    public void SetTimelineState(string description)
    {
        if (!IsActive)
        {
            return;
        }

        if (description.Length == 0)
        {
            SteamApi.TimelineClearTimelineTooltip(_timeline, 0);
            return;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(description))
        {
            SteamApi.TimelineSetTimelineTooltip(_timeline, text, 0);
        }
    }

    /// <summary>Tells the timeline what the game is doing: <c>playing</c>, <c>staging</c> (a lobby, a briefing), <c>menus</c> or <c>loading</c>; the recording skips menus and loading screens.</summary>
    [Method]
    public void SetTimelineMode(string mode)
    {
        if (!IsActive)
        {
            return;
        }

        SteamApi.TimelineSetTimelineGameMode(_timeline, mode switch
        {
            "staging" => ETimelineGameMode.Staging,
            "menus" => ETimelineGameMode.Menus,
            "loading" => ETimelineGameMode.LoadingScreen,
            _ => ETimelineGameMode.Playing,
        });
    }

    // ---- The on-screen keyboard -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens Steam's on-screen keyboard (the overlay's, for a Deck or a controller) for a line of text; <see cref="TextInputDismissed"/> follows. False when it cannot be shown (the overlay is off).
    /// </summary>
    [Method]
    public bool ShowTextInput(string description, int maxLength = 128, bool multiline = false, bool password = false)
    {
        if (!IsOverlayEnabled)
        {
            return false;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(description))
        fixed (byte* existing = Utf8.ToZeroTerminated(""))
        {
            return SteamApi.UtilsShowGamepadTextInput(_utils, password ? EGamepadTextInputMode.Password : EGamepadTextInputMode.Normal,
                multiline ? EGamepadTextInputLineMode.MultipleLines : EGamepadTextInputLineMode.SingleLine, text, (uint)Math.Max(maxLength, 1), existing);
        }
    }
}
