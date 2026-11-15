using System;
using Apogee.Audio;
using Apogee.Platform;

namespace Apogee.Host;

/// <summary>What a game's <c>game.xml</c> says about its window, as the defaults of the engine's window settings.</summary>
/// <param name="Width">The window's width, in points.</param>
/// <param name="Height">The window's height, in points.</param>
/// <param name="Fullscreen">Whether the window starts fullscreen.</param>
/// <param name="VSync">Whether frames wait for the display.</param>
public sealed record EngineSettingDefaults(int Width = 1280, int Height = 720, bool Fullscreen = false, bool VSync = true);

/// <summary>The engine's own settings (<c>window.*</c>, <c>graphics.*</c>, <c>audio.*</c>) and what makes them do something.</summary>
public static class EngineSettings
{
    /// <summary>The window's width in points.</summary>
    public const string WindowWidth = "window.width";

    /// <summary>The window's height in points.</summary>
    public const string WindowHeight = "window.height";

    /// <summary>Whether the window is fullscreen.</summary>
    public const string WindowFullscreen = "window.fullscreen";

    /// <summary>Whether frames wait for the display (read when the game starts).</summary>
    public const string GraphicsVSync = "graphics.vsync";

    /// <summary>The master volume, 0 to 1.</summary>
    public const string AudioVolume = "audio.volume";

    /// <summary>The start of the keys of the audio buses: <c>audio.bus.music</c> is the volume of the bus <c>music</c>.</summary>
    public const string AudioBusPrefix = "audio.bus.";

    /// <summary>Defines the engine's settings; scripts cannot define these keys again. The defaults of the window come from the game's <c>game.xml</c>.</summary>
    /// <param name="store">The store to define them in.</param>
    /// <param name="defaults">The game's defaults.</param>
    public static void Register(SettingsStore store, EngineSettingDefaults defaults)
    {
        store.Define(SettingDef.OfInt(WindowWidth, defaults.Width, min: 320, help: "The width of the window."));
        store.Define(SettingDef.OfInt(WindowHeight, defaults.Height, min: 240, help: "The height of the window."));
        store.Define(SettingDef.OfBool(WindowFullscreen, defaults.Fullscreen, help: "Fullscreen."));
        store.Define(SettingDef.OfBool(GraphicsVSync, defaults.VSync, restart: true, help: "Wait for the display before showing a frame. Applies at the next start."));
        store.Define(SettingDef.OfNumber(AudioVolume, 1, min: 0, max: 1, help: "The master volume."));
    }

    /// <summary>
    /// Makes the volumes of the audio system follow the settings, now and when they change: <c>audio.volume</c> is the master volume and <c>audio.bus.&lt;name&gt;</c> (a number setting that a script defines) the volume of
    /// the bus of that name. A bus whose key is defined later is set when it is defined.
    /// </summary>
    /// <param name="store">The settings.</param>
    /// <param name="audio">The audio system.</param>
    public static void ConnectAudio(SettingsStore store, AudioSystem audio)
    {
        void Apply(string key)
        {
            if (key == AudioVolume)
            {
                audio.Volume = (float)store.GetNumber(key);
            }
            else if (key.StartsWith(AudioBusPrefix, StringComparison.Ordinal) && store.TryGet(key, out Apogee.Reflection.Value value))
            {
                audio.GetBus(key[AudioBusPrefix.Length..]).Volume = (float)value.ToDouble();
            }
        }

        foreach (string key in store.Keys("audio."))
        {
            Apply(key);
        }

        store.KeyDefined += Apply;
        _ = store.Changed.Connect(Apply);
    }

    /// <summary>
    /// Makes a window follow <c>window.fullscreen</c> and remember its size. The window must have been made from the settings (the size and whether it starts fullscreen); this keeps it in step afterwards.
    /// Dispose the result when the window goes.
    /// </summary>
    /// <param name="store">The settings.</param>
    /// <param name="window">The game's window.</param>
    /// <param name="trackSize">Whether a size the player gives the window is stored (not when the size came from the command line).</param>
    public static WindowSettings ConnectWindow(SettingsStore store, Window window, bool trackSize = true) => new(store, window, trackSize);
}

/// <summary>Keeps a window and the <c>window.*</c> settings together: fullscreen follows the setting, and the size the player gives the window while it is not fullscreen is stored.</summary>
public sealed class WindowSettings : IDisposable
{
    /// <summary>How many frames a size has to stay the same before it is stored: the player is still dragging, or the window manager is still settling after a mode change.</summary>
    public const int SettledFrames = 20;

    private readonly SettingsStore _store;
    private readonly Window? _window;
    private readonly bool _trackSize;
    private readonly Apogee.Reflection.Connection _connection;
    private (int Width, int Height) _seen;
    private int _stable;

    internal WindowSettings(SettingsStore store, Window window, bool trackSize)
        : this(store, trackSize)
    {
        _window = window;
        _seen = window.LogicalSize;
    }

    /// <summary>Follows a window that is not there: for tests, which feed <see cref="Observe"/> sizes.</summary>
    public WindowSettings(SettingsStore store, bool trackSize = true)
    {
        _store = store;
        _trackSize = trackSize;
        _connection = _store.Changed.Connect(OnChanged);
    }

    private void OnChanged(string key)
    {
        if (key != EngineSettings.WindowFullscreen)
        {
            return;
        }

        _stable = 0; // the size is in flux until the mode has changed
        _window?.SetFullscreen(_store.GetBool(EngineSettings.WindowFullscreen));
    }

    /// <summary>Call once a frame: stores the window's size once the player has left it alone for a moment.</summary>
    public void Update()
    {
        if (_window is not null)
        {
            Observe(_window.LogicalSize.Width, _window.LogicalSize.Height);
        }
    }

    /// <summary>Takes the window's size of this frame. It is stored (as <c>window.width</c> and <c>window.height</c>) when it differs from the stored one, has not changed for <see cref="SettledFrames"/> frames and the window is not fullscreen.</summary>
    /// <param name="width">The width in points.</param>
    /// <param name="height">The height in points.</param>
    public void Observe(int width, int height)
    {
        if (!_trackSize || width <= 0 || height <= 0)
        {
            return;
        }

        if ((width, height) != _seen)
        {
            _seen = (width, height);
            _stable = 0;
            return;
        }

        if (_stable < SettledFrames)
        {
            _stable++;
            return;
        }

        if (_store.GetBool(EngineSettings.WindowFullscreen) || (_store.GetInt(EngineSettings.WindowWidth) == width && _store.GetInt(EngineSettings.WindowHeight) == height))
        {
            return;
        }

        _store.Set(EngineSettings.WindowWidth, Apogee.Reflection.Value.Int(width));
        _store.Set(EngineSettings.WindowHeight, Apogee.Reflection.Value.Int(height));
    }

    /// <summary>Stops following the settings.</summary>
    public void Dispose() => _connection.Disconnect();
}
