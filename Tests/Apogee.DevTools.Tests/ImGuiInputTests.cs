using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Native.CImGui;
using Apogee.Native.Tests;
using Apogee.Platform;
using Xunit;
using Sdl = Apogee.Native.Sdl;

namespace Apogee.DevTools.Tests;

/// <summary>ImGui keeps a process-wide "current context", so every test that makes one runs alone. The GPU device is shared by the ones that draw.</summary>
[CollectionDefinition("imgui", DisableParallelization = true)]
public sealed class ImGuiTests : ICollectionFixture<Apogee.Graphics.Tests.GpuFixture>
{
}

[Collection("imgui")]
public sealed unsafe class ImGuiInputTests : IDisposable
{
    private readonly ImGuiHost _host;

    public ImGuiInputTests()
    {
        NativeRequirements.Require(typeof(ImGui).Assembly, "cimgui");
        NativeRequirements.Require(typeof(Sdl.SDL).Assembly, "SDL3");
        PlatformHost.Initialize("dummy");
        _host = new ImGuiHost();
        _host.AttachToAllWindows();
    }

    public void Dispose()
    {
        _host.Dispose();
        PlatformHost.Shutdown();
        GC.SuppressFinalize(this);
    }

    private static void Push(Sdl.Event e) => Assert.True(Sdl.SDL.PushEvent(&e));

    /// <summary>Delivers the pushed events and runs one ImGui frame, after which the input is visible to the application.</summary>
    private void Frame()
    {
        PlatformHost.PumpEvents();
        _host.BeginFrame(640, 360, 1f, 1f / 60f);
        _host.EndFrame();
    }

    [Fact]
    public void LettersDigitsAndFunctionKeysMapInOrder()
    {
        for (int i = 0; i < 26; i++)
        {
            Assert.Equal(ImGuiKey.ImGuiKeyA + (uint)i, ImGuiSdlInput.MapKey((Sdl.Scancode)((int)Sdl.Scancode.A + i)));
        }

        Assert.Equal(ImGuiKey.ImGuiKey1, ImGuiSdlInput.MapKey(Sdl.Scancode._1));
        Assert.Equal(ImGuiKey.ImGuiKey9, ImGuiSdlInput.MapKey(Sdl.Scancode._9));
        Assert.Equal(ImGuiKey.ImGuiKey0, ImGuiSdlInput.MapKey(Sdl.Scancode._0));
        Assert.Equal(ImGuiKey.ImGuiKeyF1, ImGuiSdlInput.MapKey(Sdl.Scancode.F1));
        Assert.Equal(ImGuiKey.ImGuiKeyF12, ImGuiSdlInput.MapKey(Sdl.Scancode.F12));
        Assert.Equal(ImGuiKey.ImGuiKeyKeypad1, ImGuiSdlInput.MapKey(Sdl.Scancode.Kp1));
        Assert.Equal(ImGuiKey.ImGuiKeyKeypad9, ImGuiSdlInput.MapKey(Sdl.Scancode.Kp9));
    }

    [Fact]
    public void NamedKeysMapAndNoTwoScancodesShareAKey()
    {
        Assert.Equal(ImGuiKey.ImGuiKeyEnter, ImGuiSdlInput.MapKey(Sdl.Scancode.Return));
        Assert.Equal(ImGuiKey.ImGuiKeyEscape, ImGuiSdlInput.MapKey(Sdl.Scancode.Escape));
        Assert.Equal(ImGuiKey.ImGuiKeyLeftArrow, ImGuiSdlInput.MapKey(Sdl.Scancode.Left));
        Assert.Equal(ImGuiKey.ImGuiKeyRightCtrl, ImGuiSdlInput.MapKey(Sdl.Scancode.Rctrl));
        Assert.Equal(ImGuiKey.ImGuiKeyNone, ImGuiSdlInput.MapKey(Sdl.Scancode.Unknown));

        var mapped = new Dictionary<ImGuiKey, Sdl.Scancode>();
        for (int code = 0; code < 512; code++)
        {
            ImGuiKey key = ImGuiSdlInput.MapKey((Sdl.Scancode)code);
            if (key != ImGuiKey.ImGuiKeyNone)
            {
                Assert.True(mapped.TryAdd(key, (Sdl.Scancode)code), $"{(Sdl.Scancode)code} and {mapped.GetValueOrDefault(key)} both map to {key}");
            }
        }

        Assert.True(mapped.Count > 100);
    }

    [Fact]
    public void MouseButtonsAreReorderedForImGui()
    {
        Assert.Equal([0, 2, 1, 3, 4, -1], new byte[] { 1, 2, 3, 4, 5, 9 }.Select(ImGuiSdlInput.MapButton));
    }

    [Fact]
    public void MouseMotionMovesImGuisPointer()
    {
        Frame();
        var motion = default(Sdl.Event);
        motion.Type = (uint)Sdl.EventType.MouseMotion;
        motion.Motion.X = 123f;
        motion.Motion.Y = 45f;
        Push(motion);

        Frame();

        Assert.Equal(123f, ImGui.GetMousePos().X);
        Assert.Equal(45f, ImGui.GetMousePos().Y);
    }

    [Fact]
    public void AClickIsSeenForAFrameAndReleased()
    {
        var down = default(Sdl.Event);
        down.Type = (uint)Sdl.EventType.MouseButtonDown;
        down.Button.Button = 3;                    // SDL's right button is ImGui's button 1
        down.Button.X = 10;
        down.Button.Y = 20;
        down.Button.Down = true;
        Push(down);
        Frame();
        Assert.True(ImGui.IsMouseDownNil(ImGuiMouseButton.Right));
        Assert.False(ImGui.IsMouseDownNil(ImGuiMouseButton.Left));

        var up = down;
        up.Type = (uint)Sdl.EventType.MouseButtonUp;
        up.Button.Down = false;
        Push(up);
        Frame();
        Assert.False(ImGui.IsMouseDownNil(ImGuiMouseButton.Right));
    }

    [Fact]
    public void TheWheelScrolls()
    {
        var wheel = default(Sdl.Event);
        wheel.Type = (uint)Sdl.EventType.MouseWheel;
        wheel.Wheel.Y = 2f;
        Push(wheel);
        PlatformHost.PumpEvents();

        // The wheel is read inside the frame: EndFrame resets it.
        _host.BeginFrame(640, 360, 1f, 1f / 60f);
        Assert.Equal(2f, _host.IO->MouseWheel);
        _host.EndFrame();
    }

    [Fact]
    public void KeysAreHeldUntilReleased()
    {
        var down = default(Sdl.Event);
        down.Type = (uint)Sdl.EventType.KeyDown;
        down.Key.Scancode = Sdl.Scancode.A;
        down.Key.Down = true;
        Push(down);
        Frame();
        Assert.True(ImGui.IsKeyDownNil(ImGuiKey.ImGuiKeyA));
        Frame();
        Assert.True(ImGui.IsKeyDownNil(ImGuiKey.ImGuiKeyA));

        var up = down;
        up.Type = (uint)Sdl.EventType.KeyUp;
        up.Key.Down = false;
        Push(up);
        Frame();
        Assert.False(ImGui.IsKeyDownNil(ImGuiKey.ImGuiKeyA));
    }

    [Fact]
    public void TypedTextReachesTheInputQueue()
    {
        var text = default(Sdl.Event);
        text.Type = (uint)Sdl.EventType.TextInput;
        byte* hello = stackalloc byte[] { (byte)'h', (byte)'i', 0 };
        text.Text.Text = hello;
        Push(text);
        PlatformHost.PumpEvents();
        _host.BeginFrame(640, 360, 1f, 1f / 60f);

        Assert.Equal(2, _host.IO->InputQueueCharacters.Size);
        Assert.Equal((ushort)'h', _host.IO->InputQueueCharacters.Data[0]);
        _host.EndFrame();
    }

    [Fact]
    public void ABeginFrameWithoutAWindowNeedsExplicitSizes()
    {
        Assert.Throws<InvalidOperationException>(() => _host.BeginFrame(1f / 60f));
    }

    [Fact]
    public void AHostAttachedToOneWindowIgnoresTheOthers()
    {
        using var mine = new Window("mine", 100, 100);
        using var other = new Window("other", 100, 100);
        _host.Attach(mine);

        var motion = default(Sdl.Event);
        motion.Type = (uint)Sdl.EventType.MouseMotion;
        motion.Motion.WindowID = other.Id;
        motion.Motion.X = 77f;
        motion.Motion.Y = 66f;
        Push(motion);
        Frame();
        Assert.NotEqual(77f, ImGui.GetMousePos().X);

        motion.Motion.WindowID = mine.Id;
        Push(motion);
        Frame();
        Assert.Equal(77f, ImGui.GetMousePos().X);
    }

    [Fact]
    public void TheDisplaySizeComesFromTheWindowInPointsWithItsDensity()
    {
        using var window = new Window("sized", 300, 200);
        _host.Attach(window);

        _host.BeginFrame(1f / 60f);

        Assert.Equal(300f, _host.IO->DisplaySize.X);
        Assert.Equal(200f, _host.IO->DisplaySize.Y);
        Assert.Equal(window.PixelDensity, _host.IO->DisplayFramebufferScale.X);
        _host.EndFrame();
    }

    [Fact]
    public void ImGuiWantsTheMouseOnlyOverItsWidgets()
    {
        // A window at (50, 50), 200 x 100: the pointer inside it is captured, outside it is not.
        var motion = default(Sdl.Event);
        motion.Type = (uint)Sdl.EventType.MouseMotion;
        motion.Motion.X = 100f;
        motion.Motion.Y = 80f;
        Push(motion);
        PlatformHost.PumpEvents();

        for (int i = 0; i < 3; i++)
        {
            _host.BeginFrame(640, 360, 1f, 1f / 60f);
            Ui.SetNextWindow(new System.Numerics.Vector2(50, 50), new System.Numerics.Vector2(200, 100), ImGuiCond.Always);
            if (Ui.Begin("capture"))
            {
                Ui.Text("hello");
            }

            Ui.End();
            _host.EndFrame();
        }

        Assert.True(_host.WantsMouse);

        motion.Motion.X = 500f;
        motion.Motion.Y = 300f;
        Push(motion);
        PlatformHost.PumpEvents();
        for (int i = 0; i < 3; i++)
        {
            _host.BeginFrame(640, 360, 1f, 1f / 60f);
            Ui.SetNextWindow(new System.Numerics.Vector2(50, 50), new System.Numerics.Vector2(200, 100), ImGuiCond.Always);
            Ui.Begin("capture");
            Ui.End();
            _host.EndFrame();
        }

        Assert.False(_host.WantsMouse);
    }
}
