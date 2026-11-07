using System;
using Apogee.Native.Steam;
using Xunit;
using SteamApi = Apogee.Native.Steam.Steam;

namespace Apogee.Native.Tests;

/// <summary>
/// Steam's flat API through its redistributable library (committed under ThirdParty/steamworks/lib). Nothing here initializes Steam, so no client, account or app id is needed.
/// </summary>
public unsafe class SteamSmokeTests
{
    [Fact]
    public void TheLibraryAnswersWithoutBeingInitialized()
    {
        NativeRequirements.Require(typeof(SteamApi).Assembly, "steam_api");

        // The client may or may not be running on the machine that runs the tests: either answer is fine, the call must just work.
        _ = SteamApi.IsSteamRunning();

        // Before init there is no pipe to the client.
        Assert.Equal(0, SteamApi.GetHSteamPipe());
    }

    [Fact]
    public void InitFailsCleanlyWithoutAnApp()
    {
        NativeRequirements.Require(typeof(SteamApi).Assembly, "steam_api");
        if (SteamApi.IsSteamRunning())
        {
            return; // with a client running and no steam_appid.txt this would be a real (failing) init: leave it to the opt-in Apogee.Steam tests
        }

        byte* message = stackalloc byte[1024];
        ESteamAPIInitResult result = SteamApi.InitFlat(message);
        Assert.NotEqual(ESteamAPIInitResult.OK, result);
        Assert.NotEqual(0, message[0]); // Steam says why
    }

    [Fact]
    public void CallbackIdsAreThoseOfTheHeaders()
    {
        Assert.Equal(331, SteamApi.GameOverlayActivatedId);
        Assert.Equal(703, SteamApi.SteamAPICallCompletedId);
    }
}
