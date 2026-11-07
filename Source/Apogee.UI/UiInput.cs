using System;
using Apogee.Interop;
using Apogee.Native.RmlUi;
using Apogee.Platform;
using Sdl = Apogee.Native.Sdl;

namespace Apogee.UI;

/// <summary>
/// Feeds the UI from SDL: the pointer, buttons, wheel, keys and typed text of one window, as <see cref="PlatformHost"/> pumps them. Every event goes to the UI, which uses what lands on its elements;
/// <see cref="UiSystem.WantsMouse"/> and <see cref="UiSystem.WantsKeyboard"/> tell the game whether it should leave an event alone. Call <see cref="Update"/> once a frame.
/// </summary>
public sealed unsafe class UiInput : IDisposable
{
    private readonly UiSystem _ui;
    private readonly uint _windowId;
    private readonly Sdl.Cursor*[] _cursors = new Sdl.Cursor*[(int)Sdl.SystemCursor.Count];
    private float _pixelScale = 1f;
    private bool _textInput;

    /// <param name="windowId">The window whose events count; 0 takes them from any window (headless use, tests).</param>
    public UiInput(UiSystem ui, uint windowId)
    {
        _ui = ui;
        _windowId = windowId;
        PlatformHost.RawEvent += OnEvent;
    }

    /// <summary>False leaves the pointer to the host (an editor that shows the game in a panel feeds the pointer itself, in the panel's coordinates): mouse events are ignored.</summary>
    public bool PointerEnabled { get; set; } = true;

    /// <summary>Asked when a key or typed text arrives: false ignores it (the editor's viewport does not have the keyboard). Null always takes it.</summary>
    public Func<bool>? KeyboardEnabled { get; set; }

    private bool Mine(uint windowId) => _windowId == 0 || windowId == _windowId;

    private void OnEvent(in Sdl.Event e)
    {
        var type = (Sdl.EventType)e.Type;
        switch (type)
        {
            case Sdl.EventType.MouseMotion when PointerEnabled && Mine(e.Motion.WindowID):
                _ui.MouseMove(Pixels(e.Motion.X), Pixels(e.Motion.Y), Modifiers());
                break;
            case Sdl.EventType.MouseButtonDown or Sdl.EventType.MouseButtonUp when PointerEnabled && Mine(e.Button.WindowID):
                int button = MapButton(e.Button.Button);
                if (button >= 0)
                {
                    _ui.MouseMove(Pixels(e.Button.X), Pixels(e.Button.Y), Modifiers());
                    _ui.MouseButton(button, type == Sdl.EventType.MouseButtonDown, Modifiers());
                }

                break;
            case Sdl.EventType.MouseWheel when PointerEnabled && Mine(e.Wheel.WindowID):
                _ui.MouseWheel(e.Wheel.X, -e.Wheel.Y, Modifiers()); // SDL's y is up, RmlUi's is down
                break;
            case Sdl.EventType.KeyDown or Sdl.EventType.KeyUp when KeyboardTaken() && Mine(e.Key.WindowID):
                ArKey key = MapKey(e.Key.Scancode);
                if (key != ArKey.Unknown)
                {
                    _ui.Key(key, type == Sdl.EventType.KeyDown, Modifiers());
                }

                break;
            case Sdl.EventType.TextInput when KeyboardTaken() && Mine(e.Text.WindowID):
                if (Utf8.FromPointer(e.Text.Text) is { Length: > 0 } text)
                {
                    _ui.Text(text);
                }

                break;
            case Sdl.EventType.WindowMouseLeave when Mine(e.Window.WindowID):
                _ui.MouseLeave();
                break;
        }
    }

    private bool KeyboardTaken() => KeyboardEnabled?.Invoke() ?? true;

    private int Pixels(float point) => (int)MathF.Round(point * _pixelScale);

    /// <summary>SDL's buttons are left 1, middle 2, right 3; RmlUi's are left 0, right 1, middle 2.</summary>
    internal static int MapButton(byte sdlButton) => sdlButton switch { 1 => 0, 3 => 1, 2 => 2, _ => -1 };

    /// <summary>RmlUi's key modifiers from SDL's current ones.</summary>
    internal static int Modifiers()
    {
        ushort state = Sdl.SDL.GetModState();
        int result = 0;
        result |= (state & 0x00C0) != 0 ? (int)ArKeyModifier.Ctrl : 0;
        result |= (state & 0x0003) != 0 ? (int)ArKeyModifier.Shift : 0;
        result |= (state & 0x0300) != 0 ? (int)ArKeyModifier.Alt : 0;
        result |= (state & 0x0C00) != 0 ? (int)ArKeyModifier.Meta : 0;
        result |= (state & 0x2000) != 0 ? (int)ArKeyModifier.Capslock : 0;
        result |= (state & 0x1000) != 0 ? (int)ArKeyModifier.Numlock : 0;
        return result;
    }

    /// <summary>
    /// Once a frame, after the events were pumped: sizes the pointer to the window's pixels, starts and stops SDL's text input as a text field takes the focus, and, with
    /// <paramref name="applyCursor"/>, sets the OS cursor to the one the UI wants while the pointer is over it.
    /// </summary>
    public void Update(Window? window, bool applyCursor = true)
    {
        if (window is null)
        {
            return;
        }

        _pixelScale = window.PixelDensity;
        bool wantsText = _ui.WantsKeyboard;
        if (wantsText != _textInput)
        {
            _textInput = wantsText;
            if (wantsText)
            {
                Sdl.SDL.StartTextInput(window.NativeHandle);
            }
            else
            {
                Sdl.SDL.StopTextInput(window.NativeHandle);
            }
        }

        if (applyCursor && _ui.WantsMouse)
        {
            Sdl.SystemCursor wanted = CursorFor(_ui.Cursor);
            int index = (int)wanted;
            _cursors[index] = _cursors[index] != null ? _cursors[index] : Sdl.SDL.CreateSystemCursor(wanted);
            Sdl.SDL.SetCursor(_cursors[index]);
        }
    }

    /// <summary>The SDL cursor for a CSS cursor name as RmlUi reports it.</summary>
    internal static Sdl.SystemCursor CursorFor(string name) => name switch
    {
        "pointer" => Sdl.SystemCursor.Pointer,
        "text" => Sdl.SystemCursor.Text,
        "move" => Sdl.SystemCursor.Move,
        "wait" => Sdl.SystemCursor.Wait,
        "progress" => Sdl.SystemCursor.Progress,
        "ew-resize" or "col-resize" => Sdl.SystemCursor.EwResize,
        "ns-resize" or "row-resize" => Sdl.SystemCursor.NsResize,
        "nesw-resize" => Sdl.SystemCursor.NeswResize,
        "nwse-resize" => Sdl.SystemCursor.NwseResize,
        "unavailable" or "not-allowed" => Sdl.SystemCursor.NotAllowed,
        "crosshair" => Sdl.SystemCursor.Crosshair,
        _ => Sdl.SystemCursor.Default,
    };

    /// <summary>The RmlUi key for an SDL scancode (USB HID usage numbers), or <see cref="ArKey.Unknown"/>.</summary>
    internal static ArKey MapKey(Sdl.Scancode scancode)
    {
        int code = (int)scancode;
        switch (code)
        {
            case >= 4 and <= 29:
                return ArKey.A + (uint)(code - 4);          // A..Z
            case >= 30 and <= 38:
                return ArKey._1 + (uint)(code - 30);        // 1..9
            case 39:
                return ArKey._0;
            case >= 58 and <= 69:
                return ArKey.F1 + (uint)(code - 58);        // F1..F12
            case >= 104 and <= 115:
                return ArKey.F13 + (uint)(code - 104);      // F13..F24
            case >= 89 and <= 97:
                return ArKey.Numpad1 + (uint)(code - 89);   // keypad 1..9
        }

        return scancode switch
        {
            Sdl.Scancode.Return => ArKey.Return,
            Sdl.Scancode.Escape => ArKey.Escape,
            Sdl.Scancode.Backspace => ArKey.Back,
            Sdl.Scancode.Tab => ArKey.Tab,
            Sdl.Scancode.Space => ArKey.Space,
            Sdl.Scancode.Minus => ArKey.OemMinus,
            Sdl.Scancode.Equals => ArKey.OemPlus,
            Sdl.Scancode.Leftbracket => ArKey.Oem4,
            Sdl.Scancode.Rightbracket => ArKey.Oem6,
            Sdl.Scancode.Backslash => ArKey.Oem5,
            Sdl.Scancode.Semicolon => ArKey.Oem1,
            Sdl.Scancode.Apostrophe => ArKey.Oem7,
            Sdl.Scancode.Grave => ArKey.Oem3,
            Sdl.Scancode.Comma => ArKey.OemComma,
            Sdl.Scancode.Period => ArKey.OemPeriod,
            Sdl.Scancode.Slash => ArKey.Oem2,
            Sdl.Scancode.Capslock => ArKey.Capital,
            Sdl.Scancode.Scrolllock => ArKey.Scroll,
            Sdl.Scancode.Pause => ArKey.Pause,
            Sdl.Scancode.Insert => ArKey.Insert,
            Sdl.Scancode.Home => ArKey.Home,
            Sdl.Scancode.Pageup => ArKey.Prior,
            Sdl.Scancode.Delete => ArKey.Delete,
            Sdl.Scancode.End => ArKey.End,
            Sdl.Scancode.Pagedown => ArKey.Next,
            Sdl.Scancode.Right => ArKey.Right,
            Sdl.Scancode.Left => ArKey.Left,
            Sdl.Scancode.Down => ArKey.Down,
            Sdl.Scancode.Up => ArKey.Up,
            Sdl.Scancode.Numlockclear => ArKey.Numlock,
            Sdl.Scancode.KpDivide => ArKey.Divide,
            Sdl.Scancode.KpMultiply => ArKey.Multiply,
            Sdl.Scancode.KpMinus => ArKey.Subtract,
            Sdl.Scancode.KpPlus => ArKey.Add,
            Sdl.Scancode.KpEnter => ArKey.Numpadenter,
            Sdl.Scancode.Kp0 => ArKey.Numpad0,
            Sdl.Scancode.KpPeriod => ArKey.Decimal,
            Sdl.Scancode.Lctrl => ArKey.Lcontrol,
            Sdl.Scancode.Lshift => ArKey.Lshift,
            Sdl.Scancode.Lalt => ArKey.Lmenu,
            Sdl.Scancode.Lgui => ArKey.Lmeta,
            Sdl.Scancode.Rctrl => ArKey.Rcontrol,
            Sdl.Scancode.Rshift => ArKey.Rshift,
            Sdl.Scancode.Ralt => ArKey.Rmenu,
            Sdl.Scancode.Rgui => ArKey.Rmeta,
            _ => ArKey.Unknown,
        };
    }

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
