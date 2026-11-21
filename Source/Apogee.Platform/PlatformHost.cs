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
    private static readonly DropCollector s_drops = new();
    private static bool s_initialized;

    public static bool IsInitialized => s_initialized;

    /// <summary>True once the user or the system asked the application to quit (<c>SDL_EVENT_QUIT</c>); see <see cref="ClearQuitRequest"/>.</summary>
    public static bool QuitRequested { get; private set; }

    /// <summary>The name of the video driver in use ("wayland", "x11", "windows", "cocoa", "dummy"…), or null before <see cref="Initialize"/>.</summary>
    public static string? VideoDriver => s_initialized ? Utf8.FromPointer(SDL.GetCurrentVideoDriver()) : null;

    public static event RawEventCallback? RawEvent;

    /// <summary>
    /// Raised when the user drops files or folders from the operating system onto a window: once per drag-and-drop (see <see cref="FileDrop"/>), from <see cref="PumpEvents"/> on the main thread, with copies of
    /// the paths. A handler that throws is logged and does not stop the pump.
    /// </summary>
    public static event Action<FileDrop>? FilesDropped;

    /// <summary>Where the pointer is, in window points, while the user drags something from the operating system over a window, or null when no drag is going on (or the system gives no position).</summary>
    public static (float X, float Y)? DragHover => s_drops.Hover;

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

    /// <summary>
    /// Tells SDL what the application is called, before <see cref="Initialize"/>: the name the platform shows (the macOS menu, audio mixers), the version, and the reverse-DNS identifier that
    /// Wayland uses as the window's application id (so that a desktop entry of the same name finds its icon) and that PulseAudio and PipeWire use for the stream. Failing is only logged.
    /// </summary>
    public static void SetAppMetadata(string name, string version, string identifier)
    {
        fixed (byte* nameText = Utf8.ToZeroTerminated(name))
        fixed (byte* versionText = Utf8.ToZeroTerminated(version))
        fixed (byte* identifierText = Utf8.ToZeroTerminated(identifier))
        {
            if (!SDL.SetAppMetadata(nameText, versionText, identifierText))
            {
                Log.Warn(LogCategory, $"Could not set the application metadata: {LastError()}");
            }
        }
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

        FileDialog.Reset();
        s_drops.Reset();
        SDL.Quit();
        s_windows.Clear();
        s_initialized = false;
    }

    /// <summary>
    /// Processes every pending event: window events update their <see cref="Window"/>, <see cref="RawEvent"/> sees all of them. Then it runs the continuations of the file dialogs that ended (see <see cref="FileDialog"/>).
    /// </summary>
    public static void PumpEvents()
    {
        EnsureInitialized();

        Event sdlEvent;
        while (SDL.PollEvent(&sdlEvent))
        {
            RawEvent?.Invoke(in sdlEvent);
            Deliver(s_drops.Handle(in sdlEvent));

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

        Deliver(s_drops.CloseStale());
        FileDialog.Dispatch();
    }

    /// <summary>Hands a finished drop to the subscribers. Application code: one that fails must not stop the pump or the next subscriber.</summary>
    private static void Deliver(FileDrop? drop)
    {
        if (drop is null)
        {
            return;
        }

        if (FilesDropped is not { } handlers)
        {
            return;
        }

        // One by one: a multicast call would stop at the first handler that throws.
        foreach (Action<FileDrop> handler in handlers.GetInvocationList())
        {
#pragma warning disable CA1031 // The handler is application code.
            try
            {
                handler(drop);
            }
            catch (Exception e)
            {
                Log.Error(LogCategory, $"A file drop handler failed: {e}");
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>The test seam: feeds an event as the pump would, and delivers what it finishes.</summary>
    internal static void HandleDropEvent(in Event sdlEvent) => Deliver(s_drops.Handle(in sdlEvent));

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
