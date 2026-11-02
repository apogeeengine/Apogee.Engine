using System;
using System.Collections.Generic;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.Sdl;

namespace Apogee.Platform;

/// <summary>Called for every SDL event while <see cref="PlatformHost.PumpEvents"/> runs, before the platform handles it; dev tools hook their input in here.</summary>
public delegate void RawEventCallback(in Event sdlEvent);

/// <summary>
/// The process-wide platform layer over SDL: its lifetime, the event pump and the windows it dispatches window events to.
/// Everything here runs on the main thread, as SDL requires.
/// </summary>
public static unsafe class PlatformHost
{
    private const string LogCategory = "Platform";

    private static readonly Dictionary<uint, Window> s_windows = [];
    private static bool s_initialized;

    public static bool IsInitialized => s_initialized;

    /// <summary>True once the user or the system asked the application to quit (<c>SDL_EVENT_QUIT</c>); see <see cref="ClearQuitRequest"/>.</summary>
    public static bool QuitRequested { get; private set; }

    /// <summary>The name of the video driver in use ("wayland", "x11", "windows", "cocoa", "dummy"…), or null before <see cref="Initialize"/>.</summary>
    public static string? VideoDriver => s_initialized ? Utf8.FromPointer(SDL.GetCurrentVideoDriver()) : null;

    public static event RawEventCallback? RawEvent;

    /// <summary>
    /// Initializes SDL's video, event and gamepad systems. <paramref name="videoDriver"/> forces a driver; the tests use "dummy", which has
    /// windows but no display. Calling it again while initialized does nothing.
    /// </summary>
    public static void Initialize(string? videoDriver = null)
    {
        if (s_initialized)
        {
            return;
        }

        if (videoDriver is not null)
        {
            fixed (byte* name = Utf8.ToZeroTerminated(SDL.HintVideoDriver))
            fixed (byte* value = Utf8.ToZeroTerminated(videoDriver))
            {
                SDL.SetHint(name, value);
            }
        }

        if (!SDL.Init(InitFlags.Video | InitFlags.Events | InitFlags.Gamepad))
        {
            throw new PlatformException($"SDL_Init failed: {LastError()}");
        }

        s_initialized = true;
        QuitRequested = false;
        Log.Info(LogCategory, $"SDL {SDL.GetVersion() / 1_000_000}.{SDL.GetVersion() / 1000 % 1000}.{SDL.GetVersion() % 1000}, video driver {VideoDriver}");
    }

    /// <summary>Quits SDL. Destroy the windows first.</summary>
    public static void Shutdown()
    {
        if (!s_initialized)
        {
            return;
        }

        if (s_windows.Count > 0)
        {
            Log.Warn(LogCategory, $"{s_windows.Count} window(s) were still open at shutdown.");
        }

        SDL.Quit();
        s_windows.Clear();
        s_initialized = false;
    }

    /// <summary>Processes every pending event: window events update their <see cref="Window"/>, <see cref="RawEvent"/> sees all of them.</summary>
    public static void PumpEvents()
    {
        EnsureInitialized();

        Event sdlEvent;
        while (SDL.PollEvent(&sdlEvent))
        {
            RawEvent?.Invoke(in sdlEvent);

            EventType type = (EventType)sdlEvent.Type;
            if (type == EventType.Quit)
            {
                QuitRequested = true;
            }
            else if (type is >= EventType.WindowShown and <= EventType.WindowHdrStateChanged
                && s_windows.TryGetValue(sdlEvent.Window.WindowID, out Window? window))
            {
                window.Handle(type);
            }
        }
    }

    public static void ClearQuitRequest() => QuitRequested = false;

    internal static void Register(Window window) => s_windows.Add(window.Id, window);

    internal static void Unregister(Window window) => s_windows.Remove(window.Id);

    internal static void EnsureInitialized()
    {
        if (!s_initialized)
        {
            throw new InvalidOperationException("PlatformHost.Initialize has not been called.");
        }
    }

    internal static string LastError() => Utf8.FromPointer(SDL.GetError()) ?? "unknown error";
}
