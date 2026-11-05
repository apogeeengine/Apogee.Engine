using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Apogee.Native.Sdl;

namespace Apogee.Input;

/// <summary>
/// The names keys go by in bindings. They name the physical key by its place on a US QWERTY keyboard (SDL's scancodes), so a binding means the same
/// place on every layout. The left and right Ctrl, Shift, Alt and Meta share a name.
/// </summary>
public static class KeyNames
{
    private static readonly Dictionary<string, Scancode> s_byName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Scancode, string> s_byKey = [];

    static KeyNames()
    {
        for (int i = 0; i < 26; i++)
        {
            Add((Scancode)((int)Scancode.A + i), ((char)('A' + i)).ToString());
        }

        for (int i = 1; i <= 9; i++)
        {
            Add((Scancode)((int)Scancode._1 + i - 1), i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Add(Scancode._0, "0");
        for (int i = 1; i <= 12; i++)
        {
            Add((Scancode)((int)Scancode.F1 + i - 1), "F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        for (int i = 13; i <= 24; i++)
        {
            Add((Scancode)((int)Scancode.F13 + i - 13), "F" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        for (int i = 1; i <= 9; i++)
        {
            Add((Scancode)((int)Scancode.Kp1 + i - 1), "Kp" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Add(Scancode.Kp0, "Kp0");

        Add(Scancode.Return, "Enter");
        Add(Scancode.Escape, "Escape");
        Add(Scancode.Backspace, "Backspace");
        Add(Scancode.Tab, "Tab");
        Add(Scancode.Space, "Space");
        Add(Scancode.Minus, "Minus");
        Add(Scancode.Equals, "Equal");
        Add(Scancode.Leftbracket, "BracketLeft");
        Add(Scancode.Rightbracket, "BracketRight");
        Add(Scancode.Backslash, "Backslash");
        Add(Scancode.Semicolon, "Semicolon");
        Add(Scancode.Apostrophe, "Apostrophe");
        Add(Scancode.Grave, "Grave");
        Add(Scancode.Comma, "Comma");
        Add(Scancode.Period, "Period");
        Add(Scancode.Slash, "Slash");
        Add(Scancode.Nonusbackslash, "IntlBackslash");
        Add(Scancode.Capslock, "CapsLock");
        Add(Scancode.Printscreen, "PrintScreen");
        Add(Scancode.Scrolllock, "ScrollLock");
        Add(Scancode.Pause, "Pause");
        Add(Scancode.Insert, "Insert");
        Add(Scancode.Home, "Home");
        Add(Scancode.Pageup, "PageUp");
        Add(Scancode.Delete, "Delete");
        Add(Scancode.End, "End");
        Add(Scancode.Pagedown, "PageDown");
        Add(Scancode.Right, "Right");
        Add(Scancode.Left, "Left");
        Add(Scancode.Down, "Down");
        Add(Scancode.Up, "Up");
        Add(Scancode.Numlockclear, "NumLock");
        Add(Scancode.KpDivide, "KpDivide");
        Add(Scancode.KpMultiply, "KpMultiply");
        Add(Scancode.KpMinus, "KpMinus");
        Add(Scancode.KpPlus, "KpPlus");
        Add(Scancode.KpEnter, "KpEnter");
        Add(Scancode.KpPeriod, "KpPeriod");
        Add(Scancode.Application, "Menu");

        Add(Scancode.Lctrl, "Ctrl");
        Add(Scancode.Rctrl, "Ctrl", reverse: false);
        Add(Scancode.Lshift, "Shift");
        Add(Scancode.Rshift, "Shift", reverse: false);
        Add(Scancode.Lalt, "Alt");
        Add(Scancode.Ralt, "Alt", reverse: false);
        Add(Scancode.Lgui, "Meta");
        Add(Scancode.Rgui, "Meta", reverse: false);
    }

    private static void Add(Scancode key, string name, bool reverse = true)
    {
        s_byKey[key] = name;
        if (reverse)
        {
            s_byName[name] = key;
        }
    }

    /// <summary>Every name, once (the left key stands for a pair).</summary>
    public static IEnumerable<string> All => s_byName.Keys;

    /// <summary>The name of a key, or null for one that bindings do not name (media keys, international keys…).</summary>
    public static string? NameOf(Scancode key) => s_byKey.GetValueOrDefault(key);

    /// <summary>Reads a name (any case) into the key (the left one of a pair) and the name as it is written.</summary>
    public static bool TryParse(string text, out Scancode key, [NotNullWhen(true)] out string? name)
    {
        if (s_byName.TryGetValue(text, out key))
        {
            name = s_byKey[key];
            return true;
        }

        name = null;
        return false;
    }

    /// <summary>True for Ctrl, Shift, Alt and Meta, which are modifiers when held with another input.</summary>
    public static bool IsModifier(string name) => name is "Ctrl" or "Shift" or "Alt" or "Meta";
}
