using Apogee.Content.FileSystem;
using Apogee.Reflection;
using SteamApi = Apogee.Native.Steam.Steam;

namespace Apogee.Steam;

// Steam Cloud: the player's saves on Steam's servers.
public sealed unsafe partial class SteamSystem
{
    /// <summary>Whether the cloud works: Steam is running, and the player has the cloud on for their account and for this game.</summary>
    [Property]
    public bool IsCloudEnabled => IsActive && SteamApi.RemoteStorageIsCloudEnabledForAccount(_storage) && SteamApi.RemoteStorageIsCloudEnabledForApp(_storage);

    /// <summary>The cloud space of the game for this player, in bytes: the total, then what is left. 0 and 0 without Steam.</summary>
    [Method]
    public (long Total, long Available) CloudQuota()
    {
        if (!IsActive)
        {
            return (0, 0);
        }

        ulong total, available;
        return SteamApi.RemoteStorageGetQuota(_storage, &total, &available) ? ((long)total, (long)available) : (0, 0);
    }

    /// <summary>
    /// The files to mount as <c>cloud://</c>: the Steam Cloud when it works, and otherwise a plain folder (<paramref name="fallback"/>, writable) so that a game saves the same way with or without
    /// Steam. Saves made in the folder are not copied into the cloud later: choose by what the game ships on.
    /// </summary>
    public IFileSource CreateCloudSource(string fallback) => IsCloudEnabled ? new SteamCloudSource(this) : new DirectorySource(fallback, writable: true);
}
