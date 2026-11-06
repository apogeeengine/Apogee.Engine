namespace Apogee.Native.Steam;

/// <summary>
/// The packing of Steam's callback structs: 4 bytes on Linux and macOS, 8 on Windows (<c>VALVE_CALLBACK_PACK_SMALL</c> and <c>_LARGE</c> in <c>steamclientpublic.h</c>). The structs the
/// generator marks as depending on it say <c>Pack = SteamPack.Callback</c>. Build the binding for the operating system it runs on (<c>APOGEE_STEAM_PACK8</c> is set for Windows).
/// </summary>
internal static class SteamPack
{
#if APOGEE_STEAM_PACK8
    public const int Callback = 8;
#else
    public const int Callback = 4;
#endif
}
