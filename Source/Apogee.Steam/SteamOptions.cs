namespace Apogee.Steam;

/// <summary>How <see cref="SteamSystem"/> starts.</summary>
public sealed record SteamOptions
{
    /// <summary>False makes an inactive system without touching Steam: single scripts, tests, headless runs.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The game's Steam app id, or 0 when it is not known. When it is known and <see cref="RestartIfNecessary"/> is set, a game that was not started by Steam is relaunched through Steam
    /// (<c>SteamAPI_RestartAppIfNecessary</c>) and <see cref="SteamSystem.RestartRequested"/> says the process should end. A <c>steam_appid.txt</c> next to the program turns that off, for development.
    /// </summary>
    public uint AppId { get; init; }

    public bool RestartIfNecessary { get; init; } = true;
}
