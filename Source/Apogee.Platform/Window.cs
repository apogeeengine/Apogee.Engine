using System;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.Sdl;
using SdlWindow = Apogee.Native.Sdl.Window;

namespace Apogee.Platform;

[Flags]
public enum WindowOptions
{
    None = 0,
    Resizable = 1,
    /// <summary>Uses the display's real pixel density (Retina, 150% scaling) so the swapchain is sharp; <see cref="Window.PixelSize"/> is then larger than the window's size in points.</summary>
    HighPixelDensity = 2,
    /// <summary>Loads the Vulkan loader and makes the window usable for a Vulkan surface (the graphics device creates it). A window is made for one graphics API: <c>GraphicsDevice.RequiredWindowOptions</c> says which.</summary>
    Vulkan = 4,
    Fullscreen = 8,
    Hidden = 16,
    /// <summary>Makes the window usable for a Metal layer (macOS).</summary>
    Metal = 32,
}

/// <summary>An SDL window. Create it after <see cref="PlatformHost.Initialize"/>, on the main thread, and dispose it before <see cref="PlatformHost.Shutdown"/>.</summary>
public sealed unsafe class Window : IDisposable
{
    private SdlWindow* _handle;
    private PixelSize _lastReportedSize;

    public Window(string title, int width, int height, WindowOptions options = WindowOptions.Resizable | WindowOptions.HighPixelDensity)
    {
        PlatformHost.EnsureInitialized();

        WindowFlags flags = 0;
        flags |= options.HasFlag(WindowOptions.Resizable) ? WindowFlags.Resizable : 0;
        flags |= options.HasFlag(WindowOptions.HighPixelDensity) ? WindowFlags.HighPixelDensity : 0;
        flags |= options.HasFlag(WindowOptions.Vulkan) ? WindowFlags.Vulkan : 0;
        flags |= options.HasFlag(WindowOptions.Metal) ? WindowFlags.Metal : 0;
        flags |= options.HasFlag(WindowOptions.Fullscreen) ? WindowFlags.Fullscreen : 0;
        flags |= options.HasFlag(WindowOptions.Hidden) ? WindowFlags.Hidden : 0;

        fixed (byte* text = Utf8.ToZeroTerminated(title))
        {
            _handle = SDL.CreateWindow(text, width, height, flags);
        }

        if (_handle is null)
        {
            throw new PlatformException($"SDL_CreateWindow failed: {PlatformHost.LastError()}");
        }

        Id = SDL.GetWindowID(_handle);
        _lastReportedSize = PixelSize;
        PlatformHost.Register(this);
        Log.Debug("Platform", $"Window {Id} '{title}' created, {PixelSize} pixels");
    }

    public uint Id { get; }

    /// <summary>The SDL window, for code that talks to SDL directly (the dev tools' input backend).</summary>
    public SdlWindow* NativeHandle => _handle is null ? throw new ObjectDisposedException(nameof(Window)) : _handle;

    /// <summary>The user clicked the close button (or the platform asked the window to close). The application decides what happens.</summary>
    public bool CloseRequested { get; private set; }

    public bool IsMinimized { get; private set; }

    /// <summary>The window has keyboard focus.</summary>
    public bool HasFocus { get; private set; } = true;

    /// <summary>The size of the window's drawable area in pixels: the swapchain's size. Empty while minimized on some platforms.</summary>
    public PixelSize PixelSize
    {
        get
        {
            int width;
            int height;
            return SDL.GetWindowSizeInPixels(NativeHandle, &width, &height) ? new PixelSize(width, height) : default;
        }
    }

    /// <summary>The size in points, which is what the window manager and mouse coordinates use.</summary>
    public (int Width, int Height) LogicalSize
    {
        get
        {
            int width;
            int height;
            return SDL.GetWindowSize(NativeHandle, &width, &height) ? (width, height) : default;
        }
    }

    /// <summary>Pixels per point: 1 on a standard display, 2 on Retina, 1.5 at 150% scaling.</summary>
    public float PixelDensity => SDL.GetWindowPixelDensity(NativeHandle);

    public string Title
    {
        get => Utf8.FromPointer(SDL.GetWindowTitle(NativeHandle)) ?? "";
        set
        {
            fixed (byte* text = Utf8.ToZeroTerminated(value))
            {
                SDL.SetWindowTitle(NativeHandle, text);
            }
        }
    }

    /// <summary>True once when the pixel size differs from the last time this returned true or the window was created: the swapchain has to be recreated.</summary>
    public bool ConsumeResize()
    {
        PixelSize current = PixelSize;
        if (current == _lastReportedSize)
        {
            return false;
        }

        _lastReportedSize = current;
        return true;
    }

    public void SetFullscreen(bool fullscreen)
    {
        if (!SDL.SetWindowFullscreen(NativeHandle, fullscreen))
        {
            Log.Warn("Platform", $"Could not change fullscreen: {PlatformHost.LastError()}");
        }
    }

    public void ClearCloseRequest() => CloseRequested = false;

    /// <summary>Hides the pointer and reports only its movement (for a camera that turns with the mouse); false gives the pointer back.</summary>
    public void SetMouseLook(bool enabled)
    {
        if (SDL.GetWindowFlags(NativeHandle).HasFlag(WindowFlags.MouseRelativeMode) != enabled)
        {
            SDL.SetWindowRelativeMouseMode(NativeHandle, enabled);
        }
    }

    internal void Handle(EventType type)
    {
        switch (type)
        {
            case EventType.WindowCloseRequested:
                CloseRequested = true;
                break;
            case EventType.WindowMinimized:
                IsMinimized = true;
                break;
            case EventType.WindowRestored:
                IsMinimized = false;
                break;
            case EventType.WindowFocusGained:
                HasFocus = true;
                break;
            case EventType.WindowFocusLost:
                HasFocus = false;
                break;
        }
    }

    public void Dispose()
    {
        if (_handle is null)
        {
            return;
        }

        PlatformHost.Unregister(this);
        SDL.DestroyWindow(_handle);
        _handle = null;
    }
}
