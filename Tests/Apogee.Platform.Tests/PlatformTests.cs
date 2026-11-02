using System;
using Apogee.Native.Sdl;
using Apogee.Native.Tests;
using Xunit;
using SDL = Apogee.Native.Sdl.SDL;

namespace Apogee.Platform.Tests;

/// <summary>SDL is process-wide state, so these run one at a time, on SDL's "dummy" video driver (windows without a display).</summary>
public unsafe class PlatformTests : IDisposable
{
    public PlatformTests()
    {
        NativeRequirements.Require(typeof(SDL).Assembly, "SDL3");
        PlatformHost.Initialize("dummy");
    }

    public void Dispose()
    {
        PlatformHost.Shutdown();
        GC.SuppressFinalize(this);
    }

    private static void Push(EventType type, uint windowId = 0)
    {
        Event sdlEvent = default;
        sdlEvent.Type = (uint)type;
        sdlEvent.Window.WindowID = windowId;
        Assert.True(SDL.PushEvent(&sdlEvent));
    }

    [Fact]
    public void InitializeReportsTheDriverAndIsIdempotent()
    {
        Assert.True(PlatformHost.IsInitialized);
        Assert.Equal("dummy", PlatformHost.VideoDriver);

        PlatformHost.Initialize();
        Assert.True(PlatformHost.IsInitialized);
    }

    [Fact]
    public void WindowHasTheRequestedSizeAndTitle()
    {
        using var window = new Window("Apogee test", 320, 200, WindowOptions.Resizable);

        Assert.Equal("Apogee test", window.Title);
        Assert.Equal((320, 200), window.LogicalSize);
        Assert.Equal(new PixelSize(320, 200), window.PixelSize);
        Assert.False(window.PixelSize.IsEmpty);

        window.Title = "Renamed";
        Assert.Equal("Renamed", window.Title);
    }

    [Fact]
    public void CloseRequestIsReportedToTheWindowThatGotIt()
    {
        using var first = new Window("first", 64, 64);
        using var second = new Window("second", 64, 64);

        Push(EventType.WindowCloseRequested, second.Id);
        PlatformHost.PumpEvents();

        Assert.False(first.CloseRequested);
        Assert.True(second.CloseRequested);

        second.ClearCloseRequest();
        Assert.False(second.CloseRequested);
    }

    [Fact]
    public void MinimizeAndFocusEventsUpdateTheWindow()
    {
        using var window = new Window("state", 64, 64);

        Push(EventType.WindowMinimized, window.Id);
        Push(EventType.WindowFocusLost, window.Id);
        PlatformHost.PumpEvents();
        Assert.True(window.IsMinimized);
        Assert.False(window.HasFocus);

        Push(EventType.WindowRestored, window.Id);
        Push(EventType.WindowFocusGained, window.Id);
        PlatformHost.PumpEvents();
        Assert.False(window.IsMinimized);
        Assert.True(window.HasFocus);
    }

    [Fact]
    public void QuitEventSetsQuitRequestedUntilCleared()
    {
        Assert.False(PlatformHost.QuitRequested);

        Push(EventType.Quit);
        PlatformHost.PumpEvents();
        Assert.True(PlatformHost.QuitRequested);

        PlatformHost.ClearQuitRequest();
        Assert.False(PlatformHost.QuitRequested);
    }

    [Fact]
    public void RawEventsSeeEveryEventBeforeTheyAreHandled()
    {
        var seen = new System.Collections.Generic.List<uint>();
        RawEventCallback callback = (in Event e) => seen.Add(e.Type);
        PlatformHost.RawEvent += callback;
        try
        {
            Push(EventType.LowMemory);
            Push(EventType.Quit);
            PlatformHost.PumpEvents();
        }
        finally
        {
            PlatformHost.RawEvent -= callback;
            PlatformHost.ClearQuitRequest();
        }

        Assert.Contains((uint)EventType.LowMemory, seen);
        Assert.Contains((uint)EventType.Quit, seen);
    }

    [Fact]
    public void ResizeIsConsumedOnce()
    {
        using var window = new Window("resize", 200, 100);
        Assert.False(window.ConsumeResize());

        Assert.True(SDL.SetWindowSize(window.NativeHandle, 400, 300));
        PlatformHost.PumpEvents();

        Assert.Equal(new PixelSize(400, 300), window.PixelSize);
        Assert.True(window.ConsumeResize());
        Assert.False(window.ConsumeResize());
    }

    [Fact]
    public void DisposedWindowsStopReceivingEventsAndRefuseUse()
    {
        var window = new Window("gone", 64, 64);
        uint id = window.Id;
        window.Dispose();
        window.Dispose();

        Push(EventType.WindowCloseRequested, id);
        PlatformHost.PumpEvents();

        Assert.Throws<ObjectDisposedException>(() => window.PixelSize);
    }

    [Fact]
    public void WindowsNeedAnInitializedPlatform()
    {
        PlatformHost.Shutdown();

        Assert.Throws<InvalidOperationException>(() => new Window("early", 64, 64));
    }
}
