using System;
using System.Collections.Generic;
using System.Diagnostics;
using Apogee.Core.Logging;
using Apogee.Native.Sdl;
using Apogee.Platform;

namespace Apogee.Input;

/// <summary>
/// Feeds an <see cref="InputActionRouter"/> from SDL's keyboard, mouse and gamepad events (through <see cref="PlatformHost.RawEvent"/>, so create it after
/// <see cref="PlatformHost.Initialize"/>). Gamepads are opened as they are plugged in and let go when they are removed or the window loses focus.
/// While a text field or a panel has the keyboard or the mouse (<see cref="KeyboardCaptured"/>, <see cref="MouseCaptured"/>), new presses and the mouse's motion are not routed;
/// releases always are, so nothing stays down.
/// </summary>
public sealed unsafe class InputDevices : IDisposable
{
    private const string LogCategory = "Input";
    private const float AxisMax = 32767f;

    private readonly InputActionRouter _router;
    private readonly HashSet<Scancode> _keys = [];
    private readonly HashSet<byte> _buttons = [];
    private readonly Dictionary<uint, nint> _gamepads = [];
    private readonly Dictionary<uint, HashSet<string>> _padDown = [];

    public InputDevices(InputActionRouter router)
    {
        _router = router;
        PlatformHost.RawEvent += OnEvent;
    }

    /// <summary>The seconds since the program started, which is the clock of the router's times.</summary>
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Asked when a key goes down: true while something else (a text box) is typing.</summary>
    public Func<bool>? KeyboardCaptured { get; set; }

    /// <summary>Asked when a mouse button goes down or the mouse moves: true while the pointer is over something else (a panel).</summary>
    public Func<bool>? MouseCaptured { get; set; }

    /// <summary>How many gamepads are open.</summary>
    public int GamepadCount => _gamepads.Count;

    private Modifiers Held()
    {
        Modifiers held = Modifiers.None;
        foreach (Scancode key in _keys)
        {
            held |= key switch
            {
                Scancode.Lctrl or Scancode.Rctrl => Modifiers.Ctrl,
                Scancode.Lshift or Scancode.Rshift => Modifiers.Shift,
                Scancode.Lalt or Scancode.Ralt => Modifiers.Alt,
                _ => Modifiers.None,
            };
        }

        return held;
    }

    private void OnEvent(in Event e)
    {
        double now = Now;
        switch ((EventType)e.Type)
        {
            case EventType.KeyDown when !e.Key.Repeat:
                OnKeyDown(e.Key.Scancode, now);
                break;
            case EventType.KeyUp:
                OnKeyUp(e.Key.Scancode, now);
                break;
            case EventType.MouseButtonDown when !(MouseCaptured?.Invoke() ?? false):
                if (MouseName(e.Button.Button) is { } down)
                {
                    _buttons.Add(e.Button.Button);
                    _router.Press(down, Held(), now);
                }

                break;
            case EventType.MouseButtonUp:
                if (_buttons.Remove(e.Button.Button) && MouseName(e.Button.Button) is { } up)
                {
                    _router.Release(up, now);
                }

                break;
            case EventType.MouseMotion when !(MouseCaptured?.Invoke() ?? false):
                _router.Axis("Mouse X", e.Motion.Xrel, now);
                _router.Axis("Mouse Y", e.Motion.Yrel, now);
                break;
            case EventType.MouseWheel when !(MouseCaptured?.Invoke() ?? false) && e.Wheel.Y != 0:
                string wheel = e.Wheel.Y > 0 ? "Mouse Wheel Up" : "Mouse Wheel Down";
                _router.Press(wheel, Held(), now); // a step of the wheel is a press and a release at once
                _router.Release(wheel, now);
                break;
            case EventType.GamepadAdded:
                OpenGamepad(e.Gdevice.Which);
                break;
            case EventType.GamepadRemoved:
                CloseGamepad(e.Gdevice.Which, now);
                break;
            case EventType.GamepadButtonDown:
                if (PadButtonName((GamepadButton)e.Gbutton.Button) is { } pad)
                {
                    PadDown(e.Gbutton.Which).Add(pad);
                    _router.Press(pad, Modifiers.None, now);
                }

                break;
            case EventType.GamepadButtonUp:
                if (PadButtonName((GamepadButton)e.Gbutton.Button) is { } released)
                {
                    PadDown(e.Gbutton.Which).Remove(released);
                    _router.Release(released, now);
                }

                break;
            case EventType.GamepadAxisMotion:
                if (PadAxisName((GamepadAxis)e.Gaxis.Axis) is { } axis)
                {
                    float value = Math.Clamp(e.Gaxis.Value / AxisMax, -1f, 1f);
                    _router.Axis(axis, value, now);
                }

                break;
            case EventType.WindowFocusLost:
                _keys.Clear();
                _buttons.Clear();
                foreach (HashSet<string> held in _padDown.Values)
                {
                    held.Clear();
                }

                _router.ReleaseAll();
                break;
        }
    }

    private void OnKeyDown(Scancode key, double now)
    {
        if (KeyboardCaptured?.Invoke() ?? false)
        {
            return;
        }

        if (KeyNames.NameOf(key) is not { } name)
        {
            return;
        }

        Modifiers held = Held();
        _keys.Add(key);
        // A modifier is not held with itself: Shift alone is "Shift", not "Shift+Shift".
        Modifiers withKey = name switch { "Ctrl" => held & ~Modifiers.Ctrl, "Shift" => held & ~Modifiers.Shift, "Alt" => held & ~Modifiers.Alt, _ => held };
        _router.Press(name, withKey, now);
    }

    private void OnKeyUp(Scancode key, double now)
    {
        if (!_keys.Remove(key) || KeyNames.NameOf(key) is not { } name)
        {
            return;
        }

        // Left and right Shift are one input: it is up when both are.
        foreach (Scancode other in _keys)
        {
            if (KeyNames.NameOf(other) == name)
            {
                return;
            }
        }

        _router.Release(name, now);
    }

    private HashSet<string> PadDown(uint id)
    {
        if (!_padDown.TryGetValue(id, out HashSet<string>? down))
        {
            _padDown[id] = down = [];
        }

        return down;
    }

    private void OpenGamepad(uint id)
    {
        if (_gamepads.ContainsKey(id))
        {
            return;
        }

        Gamepad* pad = SDL.OpenGamepad(id);
        if (pad is null)
        {
            Log.Warn(LogCategory, $"Could not open gamepad {id}: {Apogee.Interop.Utf8.FromPointer(SDL.GetError())}");
            return;
        }

        _gamepads[id] = (nint)pad;
        Log.Info(LogCategory, $"Gamepad opened: {Apogee.Interop.Utf8.FromPointer(SDL.GetGamepadName(pad))}");
    }

    private void CloseGamepad(uint id, double now)
    {
        if (_padDown.Remove(id, out HashSet<string>? down))
        {
            foreach (string button in down)
            {
                _router.Release(button, now);
            }
        }

        if (_gamepads.Remove(id, out nint pad))
        {
            SDL.CloseGamepad((Gamepad*)pad);
        }
    }

    private static string? MouseName(byte button) => button switch
    {
        1 => "Mouse Left",
        2 => "Mouse Middle",
        3 => "Mouse Right",
        4 => "Mouse Back",
        5 => "Mouse Forward",
        _ => null,
    };

    private static string? PadButtonName(GamepadButton button) => button switch
    {
        GamepadButton.South => "Joy A",
        GamepadButton.East => "Joy B",
        GamepadButton.West => "Joy X",
        GamepadButton.North => "Joy Y",
        GamepadButton.Back => "Joy Back",
        GamepadButton.Guide => "Joy Guide",
        GamepadButton.Start => "Joy Start",
        GamepadButton.LeftStick => "Joy L3",
        GamepadButton.RightStick => "Joy R3",
        GamepadButton.LeftShoulder => "Joy LB",
        GamepadButton.RightShoulder => "Joy RB",
        GamepadButton.DpadUp => "Joy Up",
        GamepadButton.DpadDown => "Joy Down",
        GamepadButton.DpadLeft => "Joy Left",
        GamepadButton.DpadRight => "Joy Right",
        _ => null,
    };

    private static string? PadAxisName(GamepadAxis axis) => axis switch
    {
        GamepadAxis.Leftx => "Joy LX",
        GamepadAxis.Lefty => "Joy LY",
        GamepadAxis.Rightx => "Joy RX",
        GamepadAxis.Righty => "Joy RY",
        GamepadAxis.LeftTrigger => "Joy LT",
        GamepadAxis.RightTrigger => "Joy RT",
        _ => null,
    };

    public void Dispose()
    {
        PlatformHost.RawEvent -= OnEvent;
        foreach (nint pad in _gamepads.Values)
        {
            SDL.CloseGamepad((Gamepad*)pad);
        }

        _gamepads.Clear();
    }
}
