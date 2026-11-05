using System;
using System.Collections.Generic;
using System.Text;
using Apogee.Reflection;

namespace Apogee.Input;

/// <summary>Keys held with an input: <c>Ctrl+Shift+L</c>.</summary>
[Flags]
public enum Modifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>How an input fires an action: when it is pressed (or quickly released, if the same input also has a hold), when it has been held, or on its second tap.</summary>
public enum Trigger
{
    Press,
    Hold,
    DoubleTap,
}

public enum InputDevice
{
    Keyboard,
    Mouse,
    Gamepad,
}

/// <summary>
/// What an action is bound to, in the text form players edit: <c>[Hold |Double ][Ctrl+][Shift+][Alt+]input</c>. Keys go by their position on a US
/// QWERTY keyboard (<c>W</c>, <c>Tab</c>, <c>F1</c>, <c>BracketLeft</c>…: <see cref="KeyNames"/>), so AZERTY players get ZQSD for WASD; mouse inputs are
/// <c>Mouse Left</c>, <c>Mouse Right</c>, <c>Mouse Middle</c>, <c>Mouse Wheel Up</c>, <c>Mouse Wheel Down</c>, <c>Mouse Back</c>, <c>Mouse Forward</c> and the motion
/// <c>Mouse X-</c> … <c>Mouse Y+</c>; gamepad inputs are <c>Joy A</c> … <c>Joy Right</c> (Xbox names), the sticks <c>Joy LX-</c> … <c>Joy RY+</c> (Y+ is down) and
/// the triggers <c>Joy LT</c>, <c>Joy RT</c>. Names are case-insensitive when read and written the way this lists them.
/// </summary>
public readonly record struct Binding(string Input, Modifiers Modifiers, Trigger Trigger)
{
    private static readonly string[] s_mouse =
        ["Mouse Left", "Mouse Right", "Mouse Middle", "Mouse Wheel Up", "Mouse Wheel Down", "Mouse Back", "Mouse Forward", "Mouse X-", "Mouse X+", "Mouse Y-", "Mouse Y+"];

    private static readonly string[] s_gamepad =
    [
        "Joy A", "Joy B", "Joy X", "Joy Y", "Joy LB", "Joy RB", "Joy Back", "Joy Start", "Joy Guide", "Joy L3", "Joy R3", "Joy Up", "Joy Down", "Joy Left", "Joy Right",
        "Joy LX-", "Joy LX+", "Joy LY-", "Joy LY+", "Joy RX-", "Joy RX+", "Joy RY-", "Joy RY+", "Joy LT", "Joy RT",
    ];

    /// <summary>Every mouse input, as it is written.</summary>
    public static IReadOnlyList<string> MouseInputs => s_mouse;

    /// <summary>Every gamepad input, as it is written.</summary>
    public static IReadOnlyList<string> GamepadInputs => s_gamepad;

    public static InputDevice DeviceOf(string input) =>
        input.StartsWith("Mouse ", StringComparison.Ordinal) ? InputDevice.Mouse : input.StartsWith("Joy ", StringComparison.Ordinal) ? InputDevice.Gamepad : InputDevice.Keyboard;

    /// <summary>A stick direction, a mouse motion direction or a trigger: inputs with a strength between 0 and 1 (more, for the mouse), not just down or up.</summary>
    public static bool IsAxis(string input) =>
        input.EndsWith('-') || input.EndsWith('+') ? DeviceOf(input) != InputDevice.Keyboard : IsTrigger(input);

    /// <summary>A gamepad trigger: an axis that only goes one way, so its name has no sign.</summary>
    public static bool IsTrigger(string input) => input is "Joy LT" or "Joy RT";

    /// <summary>The text form: <c>Hold Ctrl+Shift+L</c>.</summary>
    public override string ToString() => Prefix(Trigger) + Prefix(Modifiers) + Input;

    public static string Prefix(Modifiers modifiers) =>
        (modifiers.HasFlag(Modifiers.Ctrl) ? "Ctrl+" : "") + (modifiers.HasFlag(Modifiers.Shift) ? "Shift+" : "") + (modifiers.HasFlag(Modifiers.Alt) ? "Alt+" : "");

    public static string Prefix(Trigger trigger) => trigger switch { Trigger.Hold => "Hold ", Trigger.DoubleTap => "Double ", _ => "" };

    /// <summary>Reads the text form; <paramref name="error"/> says what is wrong when it returns false.</summary>
    public static bool TryParse(string text, out Binding binding, out string error)
    {
        binding = default;
        error = "";
        string rest = text.Trim();
        Trigger trigger = Trigger.Press;
        if (rest.StartsWith("hold ", StringComparison.OrdinalIgnoreCase))
        {
            trigger = Trigger.Hold;
            rest = rest[5..].TrimStart();
        }
        else if (rest.StartsWith("double ", StringComparison.OrdinalIgnoreCase))
        {
            trigger = Trigger.DoubleTap;
            rest = rest[7..].TrimStart();
        }

        Modifiers modifiers = Modifiers.None;
        for (bool more = true; more;)
        {
            more = false;
            foreach ((string name, Modifiers flag) in new[] { ("ctrl+", Modifiers.Ctrl), ("shift+", Modifiers.Shift), ("alt+", Modifiers.Alt) })
            {
                if (rest.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= flag;
                    rest = rest[name.Length..];
                    more = true;
                    break;
                }
            }
        }

        if (rest.Length == 0)
        {
            error = $"'{text}' names no input";
            return false;
        }

        string? known = Canonical(rest);
        InputDevice device = DeviceOf(known ?? rest);
        string input;
        if (device != InputDevice.Keyboard)
        {
            if (known is null)
            {
                error = device == InputDevice.Mouse
                    ? $"'{rest}' is not a mouse input ({string.Join(", ", s_mouse)})"
                    : $"'{rest}' is not a gamepad input ({string.Join(", ", s_gamepad)})";
                return false;
            }

            if (device == InputDevice.Gamepad && modifiers != Modifiers.None)
            {
                error = $"'{text}': Ctrl, Shift and Alt go with keys and mouse buttons";
                return false;
            }

            input = known;
        }
        else if (KeyNames.TryParse(rest, out _, out string? key))
        {
            input = key;
        }
        else
        {
            error = $"unknown key '{rest}'";
            return false;
        }

        binding = new Binding(input, modifiers, trigger);
        return true;
    }

    /// <summary>Reads the text form, or throws a <see cref="ValueException"/> (a script error) that says what is wrong.</summary>
    public static Binding Parse(string text) => TryParse(text, out Binding binding, out string error) ? binding : throw new ValueException(error);

    private static string? Canonical(string input)
    {
        foreach (string name in s_mouse)
        {
            if (string.Equals(name, input, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        foreach (string name in s_gamepad)
        {
            if (string.Equals(name, input, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    internal static string Join(IEnumerable<Binding> bindings) => string.Join(" | ", bindings);

    internal static StringBuilder AppendLine(StringBuilder builder, string id, IEnumerable<Binding> bindings) => builder.Append(id).Append(" = ").Append(Join(bindings)).Append('\n');
}
