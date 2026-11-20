using System;
using System.Collections.Generic;
using System.Text;
using Apogee.Native.CImGui;

namespace Apogee.Editor.Input;

/// <summary>The modifier keys of a <see cref="KeyChord"/>. The Windows or Super key is not one: no editor shortcut uses it.</summary>
[Flags]
public enum KeyMods
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>The platform's command key: Cmd on macOS, Ctrl elsewhere (ImGui reports Cmd as Ctrl on macOS, so the two agree).</summary>
    Primary = 1,

    /// <summary>Shift.</summary>
    Shift = 2,

    /// <summary>Alt (Option on macOS).</summary>
    Alt = 4,
}

/// <summary>A key with the modifiers that must be held with it, such as <c>Ctrl+Shift+Z</c>. <see cref="None"/> (no key) is an unbound chord.</summary>
/// <param name="Key">The key; <see cref="ImGuiKey.ImGuiKeyNone"/> for no key.</param>
/// <param name="Mods">The modifiers held with it.</param>
public readonly record struct KeyChord(ImGuiKey Key, KeyMods Mods = KeyMods.None)
{
    private static readonly Dictionary<string, ImGuiKey> KeysByName = BuildNames();
    private static readonly Dictionary<ImGuiKey, string> NamesByKey = BuildReverse();

    /// <summary>The chord that is no key at all: an action bound to it never fires.</summary>
    public static KeyChord None => default;

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Key == ImGuiKey.ImGuiKeyNone;

    /// <summary>
    /// Reads <c>Ctrl+Shift+Z</c>, <c>F5</c>, <c>Cmd+S</c>: modifiers (Ctrl, Cmd, Primary and Command all mean <see cref="KeyMods.Primary"/>; Shift; Alt or Option) and a key, joined with <c>+</c>, in any
    /// order and case. Throws <see cref="FormatException"/> on text it does not understand.
    /// </summary>
    public static KeyChord Parse(string text) =>
        TryParse(text, out KeyChord chord) ? chord : throw new FormatException($"'{text}' is not a key chord (expected modifiers and a key, such as Ctrl+Shift+Z).");

    /// <summary>The non-throwing <see cref="Parse"/>. An empty text is not a chord (use <see cref="None"/>).</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        KeyMods mods = KeyMods.None;
        ImGuiKey key = ImGuiKey.ImGuiKeyNone;
        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            string lower = part.ToLowerInvariant();
            switch (lower)
            {
                case "ctrl" or "control" or "cmd" or "command" or "primary":
                    mods |= KeyMods.Primary;
                    break;
                case "shift":
                    mods |= KeyMods.Shift;
                    break;
                case "alt" or "option":
                    mods |= KeyMods.Alt;
                    break;
                default:
                    if (key != ImGuiKey.ImGuiKeyNone || !KeysByName.TryGetValue(lower, out key))
                    {
                        return false;
                    }

                    break;
            }
        }

        if (key == ImGuiKey.ImGuiKeyNone)
        {
            return false;
        }

        chord = new KeyChord(key, mods);
        return true;
    }

    /// <summary>The chord as text for the current platform ("Cmd" for <see cref="KeyMods.Primary"/> on macOS, "Ctrl" elsewhere); <see cref="None"/> is empty.</summary>
    public override string ToString() => ToString(OperatingSystem.IsMacOS());

    /// <summary>The chord as text, with the primary modifier shown as Cmd (<paramref name="macOs"/>) or Ctrl. The result parses back to the same chord.</summary>
    public string ToString(bool macOs)
    {
        if (IsNone)
        {
            return "";
        }

        var text = new StringBuilder();
        if ((Mods & KeyMods.Primary) != 0)
        {
            text.Append(macOs ? "Cmd+" : "Ctrl+");
        }

        if ((Mods & KeyMods.Alt) != 0)
        {
            text.Append("Alt+");
        }

        if ((Mods & KeyMods.Shift) != 0)
        {
            text.Append("Shift+");
        }

        text.Append(NamesByKey.TryGetValue(Key, out string? name) ? name : Key.ToString());
        return text.ToString();
    }

    private static Dictionary<string, ImGuiKey> BuildNames()
    {
        var names = new Dictionary<string, ImGuiKey>
        {
            ["escape"] = ImGuiKey.ImGuiKeyEscape,
            ["esc"] = ImGuiKey.ImGuiKeyEscape,
            ["delete"] = ImGuiKey.ImGuiKeyDelete,
            ["del"] = ImGuiKey.ImGuiKeyDelete,
            ["backspace"] = ImGuiKey.ImGuiKeyBackspace,
            ["enter"] = ImGuiKey.ImGuiKeyEnter,
            ["return"] = ImGuiKey.ImGuiKeyEnter,
            ["space"] = ImGuiKey.ImGuiKeySpace,
            ["tab"] = ImGuiKey.ImGuiKeyTab,
            ["insert"] = ImGuiKey.ImGuiKeyInsert,
            ["home"] = ImGuiKey.ImGuiKeyHome,
            ["end"] = ImGuiKey.ImGuiKeyEnd,
            ["pageup"] = ImGuiKey.ImGuiKeyPageUp,
            ["pagedown"] = ImGuiKey.ImGuiKeyPageDown,
            ["left"] = ImGuiKey.ImGuiKeyLeftArrow,
            ["right"] = ImGuiKey.ImGuiKeyRightArrow,
            ["up"] = ImGuiKey.ImGuiKeyUpArrow,
            ["down"] = ImGuiKey.ImGuiKeyDownArrow,
        };

        // ImGui numbers the letters, digits and function keys consecutively.
        for (int i = 0; i < 26; i++)
        {
            names[((char)('a' + i)).ToString()] = ImGuiKey.ImGuiKeyA + (uint)i;
        }

        for (int i = 0; i < 10; i++)
        {
            names[((char)('0' + i)).ToString()] = ImGuiKey.ImGuiKey0 + (uint)i;
        }

        for (int i = 1; i <= 12; i++)
        {
            names["f" + i] = ImGuiKey.ImGuiKeyF1 + (uint)(i - 1);
        }

        return names;
    }

    private static Dictionary<ImGuiKey, string> BuildReverse()
    {
        // The name each key is shown as: letters upper case, "Escape" and "Delete" in full, "F5", "PageUp".
        var names = new Dictionary<ImGuiKey, string>
        {
            [ImGuiKey.ImGuiKeyEscape] = "Escape",
            [ImGuiKey.ImGuiKeyDelete] = "Delete",
            [ImGuiKey.ImGuiKeyBackspace] = "Backspace",
            [ImGuiKey.ImGuiKeyEnter] = "Enter",
            [ImGuiKey.ImGuiKeySpace] = "Space",
            [ImGuiKey.ImGuiKeyTab] = "Tab",
            [ImGuiKey.ImGuiKeyInsert] = "Insert",
            [ImGuiKey.ImGuiKeyHome] = "Home",
            [ImGuiKey.ImGuiKeyEnd] = "End",
            [ImGuiKey.ImGuiKeyPageUp] = "PageUp",
            [ImGuiKey.ImGuiKeyPageDown] = "PageDown",
            [ImGuiKey.ImGuiKeyLeftArrow] = "Left",
            [ImGuiKey.ImGuiKeyRightArrow] = "Right",
            [ImGuiKey.ImGuiKeyUpArrow] = "Up",
            [ImGuiKey.ImGuiKeyDownArrow] = "Down",
        };
        for (int i = 0; i < 26; i++)
        {
            names[ImGuiKey.ImGuiKeyA + (uint)i] = ((char)('A' + i)).ToString();
        }

        for (int i = 0; i < 10; i++)
        {
            names[ImGuiKey.ImGuiKey0 + (uint)i] = ((char)('0' + i)).ToString();
        }

        for (int i = 1; i <= 12; i++)
        {
            names[ImGuiKey.ImGuiKeyF1 + (uint)(i - 1)] = "F" + i;
        }

        return names;
    }
}
