using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apogee.Interop;
using Apogee.Native.CImGui;
using Apogee.Platform;
using Sdl = Apogee.Native.Sdl;

namespace Apogee.DevTools;

/// <summary>
/// Feeds Dear ImGui from SDL: mouse, wheel, keys, text, focus, the mouse cursor shape and the clipboard. It listens to
/// <see cref="PlatformHost.RawEvent"/>, so events reach ImGui as the platform pumps them.
/// </summary>
internal sealed unsafe class ImGuiSdlInput : IDisposable
{
    private static byte* s_clipboard;

    private readonly ImGuiIO* _io;
    private readonly uint _windowId;
    private readonly Sdl.Cursor*[] _cursors = new Sdl.Cursor*[(int)ImGuiMouseCursor.Count];
    private bool _textInput;

    public ImGuiSdlInput(ImGuiIO* io, ImGuiPlatformIO* platformIO, uint windowId)
    {
        _io = io;
        _windowId = windowId;
        PlatformHost.RawEvent += OnEvent;

        platformIO->PlatformGetClipboardTextFn = &GetClipboardText;
        platformIO->PlatformSetClipboardTextFn = &SetClipboardText;
    }

    private void OnEvent(in Sdl.Event e)
    {
        var type = (Sdl.EventType)e.Type;
        switch (type)
        {
            case Sdl.EventType.MouseMotion when Mine(e.Motion.WindowID):
                ImGui.ImGuiIOAddMousePosEvent(_io, e.Motion.X, e.Motion.Y);
                break;
            case Sdl.EventType.MouseButtonDown or Sdl.EventType.MouseButtonUp when Mine(e.Button.WindowID):
                int button = MapButton(e.Button.Button);
                if (button >= 0)
                {
                    ImGui.ImGuiIOAddMousePosEvent(_io, e.Button.X, e.Button.Y);
                    ImGui.ImGuiIOAddMouseButtonEvent(_io, button, type == Sdl.EventType.MouseButtonDown);
                }

                break;
            case Sdl.EventType.MouseWheel when Mine(e.Wheel.WindowID):
                ImGui.ImGuiIOAddMouseWheelEvent(_io, e.Wheel.X, e.Wheel.Y);
                break;
            case Sdl.EventType.KeyDown or Sdl.EventType.KeyUp when Mine(e.Key.WindowID):
                ImGuiKey key = MapKey(e.Key.Scancode);
                if (key != ImGuiKey.ImGuiKeyNone)
                {
                    ImGui.ImGuiIOAddKeyEvent(_io, key, type == Sdl.EventType.KeyDown);
                }

                break;
            case Sdl.EventType.TextInput when Mine(e.Text.WindowID):
                ImGui.ImGuiIOAddInputCharactersUTF8(_io, e.Text.Text);
                break;
            case Sdl.EventType.WindowFocusGained when Mine(e.Window.WindowID):
                ImGui.ImGuiIOAddFocusEvent(_io, true);
                break;
            case Sdl.EventType.WindowFocusLost when Mine(e.Window.WindowID):
                ImGui.ImGuiIOAddFocusEvent(_io, false);
                break;
            case Sdl.EventType.WindowMouseLeave when Mine(e.Window.WindowID):
                ImGui.ImGuiIOAddMousePosEvent(_io, float.MinValue, float.MinValue);
                break;
        }
    }

    /// <summary>A window id of 0 means "any window" (headless use); otherwise only this host's window counts.</summary>
    private bool Mine(uint windowId) => _windowId == 0 || windowId == _windowId;

    /// <summary>SDL's buttons are left 1, middle 2, right 3, then two thumb buttons; ImGui's are left 0, right 1, middle 2.</summary>
    internal static int MapButton(byte sdlButton) => sdlButton switch { 1 => 0, 2 => 2, 3 => 1, 4 => 3, 5 => 4, _ => -1 };

    /// <summary>Sets the OS cursor to the one ImGui wants over its widgets (arrow, text beam, resize arrows) and starts or stops text input.</summary>
    public void UpdateWindowState(Window? window)
    {
        if ((_io->ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) == 0)
        {
            ImGuiMouseCursor wanted = ImGui.GetMouseCursor();
            if (wanted == ImGuiMouseCursor.None)
            {
                Sdl.SDL.HideCursor();
            }
            else
            {
                int index = (int)wanted;
                _cursors[index] = _cursors[index] != null ? _cursors[index] : Sdl.SDL.CreateSystemCursor(SystemCursorFor(wanted));
                Sdl.SDL.SetCursor(_cursors[index]);
                Sdl.SDL.ShowCursor();
            }
        }

        if (window is not null && _textInput != _io->WantTextInput)
        {
            _textInput = _io->WantTextInput;
            if (_textInput)
            {
                Sdl.SDL.StartTextInput(window.NativeHandle);
            }
            else
            {
                Sdl.SDL.StopTextInput(window.NativeHandle);
            }
        }
    }

    internal static Sdl.SystemCursor SystemCursorFor(ImGuiMouseCursor cursor) => cursor switch
    {
        ImGuiMouseCursor.TextInput => Sdl.SystemCursor.Text,
        ImGuiMouseCursor.ResizeAll => Sdl.SystemCursor.Move,
        ImGuiMouseCursor.ResizeNS => Sdl.SystemCursor.NsResize,
        ImGuiMouseCursor.ResizeEW => Sdl.SystemCursor.EwResize,
        ImGuiMouseCursor.ResizeNESW => Sdl.SystemCursor.NeswResize,
        ImGuiMouseCursor.ResizeNWSE => Sdl.SystemCursor.NwseResize,
        ImGuiMouseCursor.Hand => Sdl.SystemCursor.Pointer,
        ImGuiMouseCursor.Wait => Sdl.SystemCursor.Wait,
        ImGuiMouseCursor.Progress => Sdl.SystemCursor.Progress,
        ImGuiMouseCursor.NotAllowed => Sdl.SystemCursor.NotAllowed,
        _ => Sdl.SystemCursor.Default,
    };

    /// <summary>The ImGui key for an SDL scancode, or <see cref="ImGuiKey.ImGuiKeyNone"/> for one ImGui has no key for.</summary>
    internal static ImGuiKey MapKey(Sdl.Scancode scancode)
    {
        int code = (int)scancode;
        switch (code)
        {
            case >= 4 and <= 29:
                return ImGuiKey.ImGuiKeyA + (uint)(code - 4);                                 // A..Z
            case >= 30 and <= 38:
                return ImGuiKey.ImGuiKey1 + (uint)(code - 30);                                // 1..9
            case 39:
                return ImGuiKey.ImGuiKey0;
            case >= 58 and <= 69:
                return ImGuiKey.ImGuiKeyF1 + (uint)(code - 58);                               // F1..F12
            case >= 104 and <= 115:
                return ImGuiKey.ImGuiKeyF13 + (uint)(code - 104);                             // F13..F24
            case >= 89 and <= 97:
                return ImGuiKey.ImGuiKeyKeypad1 + (uint)(code - 89);                          // keypad 1..9
        }

        return scancode switch
        {
            Sdl.Scancode.Return => ImGuiKey.ImGuiKeyEnter,
            Sdl.Scancode.Escape => ImGuiKey.ImGuiKeyEscape,
            Sdl.Scancode.Backspace => ImGuiKey.ImGuiKeyBackspace,
            Sdl.Scancode.Tab => ImGuiKey.ImGuiKeyTab,
            Sdl.Scancode.Space => ImGuiKey.ImGuiKeySpace,
            Sdl.Scancode.Minus => ImGuiKey.ImGuiKeyMinus,
            Sdl.Scancode.Equals => ImGuiKey.ImGuiKeyEqual,
            Sdl.Scancode.Leftbracket => ImGuiKey.ImGuiKeyLeftBracket,
            Sdl.Scancode.Rightbracket => ImGuiKey.ImGuiKeyRightBracket,
            Sdl.Scancode.Backslash => ImGuiKey.ImGuiKeyBackslash,
            Sdl.Scancode.Semicolon => ImGuiKey.ImGuiKeySemicolon,
            Sdl.Scancode.Apostrophe => ImGuiKey.ImGuiKeyApostrophe,
            Sdl.Scancode.Grave => ImGuiKey.ImGuiKeyGraveAccent,
            Sdl.Scancode.Comma => ImGuiKey.ImGuiKeyComma,
            Sdl.Scancode.Period => ImGuiKey.ImGuiKeyPeriod,
            Sdl.Scancode.Slash => ImGuiKey.ImGuiKeySlash,
            Sdl.Scancode.Capslock => ImGuiKey.ImGuiKeyCapsLock,
            Sdl.Scancode.Printscreen => ImGuiKey.ImGuiKeyPrintScreen,
            Sdl.Scancode.Scrolllock => ImGuiKey.ImGuiKeyScrollLock,
            Sdl.Scancode.Pause => ImGuiKey.ImGuiKeyPause,
            Sdl.Scancode.Insert => ImGuiKey.ImGuiKeyInsert,
            Sdl.Scancode.Home => ImGuiKey.ImGuiKeyHome,
            Sdl.Scancode.Pageup => ImGuiKey.ImGuiKeyPageUp,
            Sdl.Scancode.Delete => ImGuiKey.ImGuiKeyDelete,
            Sdl.Scancode.End => ImGuiKey.ImGuiKeyEnd,
            Sdl.Scancode.Pagedown => ImGuiKey.ImGuiKeyPageDown,
            Sdl.Scancode.Right => ImGuiKey.ImGuiKeyRightArrow,
            Sdl.Scancode.Left => ImGuiKey.ImGuiKeyLeftArrow,
            Sdl.Scancode.Down => ImGuiKey.ImGuiKeyDownArrow,
            Sdl.Scancode.Up => ImGuiKey.ImGuiKeyUpArrow,
            Sdl.Scancode.KpDivide => ImGuiKey.ImGuiKeyKeypadDivide,
            Sdl.Scancode.KpMultiply => ImGuiKey.ImGuiKeyKeypadMultiply,
            Sdl.Scancode.KpMinus => ImGuiKey.ImGuiKeyKeypadSubtract,
            Sdl.Scancode.KpPlus => ImGuiKey.ImGuiKeyKeypadAdd,
            Sdl.Scancode.KpEnter => ImGuiKey.ImGuiKeyKeypadEnter,
            Sdl.Scancode.Kp0 => ImGuiKey.ImGuiKeyKeypad0,
            Sdl.Scancode.KpPeriod => ImGuiKey.ImGuiKeyKeypadDecimal,
            Sdl.Scancode.Application => ImGuiKey.ImGuiKeyMenu,
            Sdl.Scancode.Lctrl => ImGuiKey.ImGuiKeyLeftCtrl,
            Sdl.Scancode.Lshift => ImGuiKey.ImGuiKeyLeftShift,
            Sdl.Scancode.Lalt => ImGuiKey.ImGuiKeyLeftAlt,
            Sdl.Scancode.Lgui => ImGuiKey.ImGuiKeyLeftSuper,
            Sdl.Scancode.Rctrl => ImGuiKey.ImGuiKeyRightCtrl,
            Sdl.Scancode.Rshift => ImGuiKey.ImGuiKeyRightShift,
            Sdl.Scancode.Ralt => ImGuiKey.ImGuiKeyRightAlt,
            Sdl.Scancode.Rgui => ImGuiKey.ImGuiKeyRightSuper,
            _ => ImGuiKey.ImGuiKeyNone,
        };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* GetClipboardText(ImGuiContext* context)
    {
        // ImGui keeps the pointer until the next call, so the previous string is freed only now.
        if (s_clipboard != null)
        {
            Sdl.SDL.Free(s_clipboard);
        }

        s_clipboard = Sdl.SDL.GetClipboardText();
        return s_clipboard;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetClipboardText(ImGuiContext* context, byte* text) => Sdl.SDL.SetClipboardText(text);

    public void Dispose()
    {
        PlatformHost.RawEvent -= OnEvent;
        foreach (Sdl.Cursor* cursor in _cursors)
        {
            if (cursor != null)
            {
                Sdl.SDL.DestroyCursor(cursor);
            }
        }
    }
}
