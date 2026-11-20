using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Native.CImGui;

namespace Apogee.Editor.Input;

/// <summary>One line of the rebinding screen.</summary>
/// <param name="Action">The action.</param>
/// <param name="Chords">What it is bound to now, as text for this platform ("Ctrl+Y or Ctrl+Shift+Z"), or "(unbound)".</param>
/// <param name="IsDefault">It is bound to its defaults, so there is nothing to reset.</param>
/// <param name="Conflicts">What it collides with, one sentence each ("Ctrl+Z is also bound to Undo (Anywhere)"); empty when it does not collide.</param>
public sealed record BindingRow(EditorAction Action, string Chords, bool IsDefault, IReadOnlyList<string> Conflicts);

/// <summary>
/// The logic of the rebinding screen, with no ImGui: the rows to show, and the capture of a new chord (<see cref="Begin"/> waits for the next key press, <see cref="Poll"/> is called every frame with the
/// keyboard and takes it). The keyboard is an <see cref="IKeyState"/>, so a test drives it with a fake. While a key is awaited the keymap is <see cref="Keymap.Suspended"/>: the key that is being chosen
/// must not also do what it is bound to. A chord that collides with another action is accepted and reported (<see cref="Notice"/>, <see cref="BindingRow.Conflicts"/>).
/// </summary>
/// <param name="keymap">The table that is edited.</param>
/// <param name="macOs">Show Cmd instead of Ctrl; null for the platform the editor runs on.</param>
public sealed class KeyBindingEditor(Keymap keymap, bool? macOs = null)
{
    private readonly bool _macOs = macOs ?? OperatingSystem.IsMacOS();

    /// <summary>The id of the action that waits for a key, or null.</summary>
    public string? Capturing { get; private set; }

    /// <summary>What the last capture or change has to say (a collision), or null: shown beside the table until the next one.</summary>
    public string? Notice { get; private set; }

    /// <summary>The actions with their chords and collisions, in the keymap's order.</summary>
    public IReadOnlyList<BindingRow> Rows()
    {
        var rows = new List<BindingRow>();
        foreach (EditorAction action in keymap.Actions)
        {
            string chords = string.Join(" or ", keymap.Bindings(action.Id).Select(c => c.ToString(_macOs)));
            rows.Add(new BindingRow(action, chords.Length == 0 ? "(unbound)" : chords, keymap.IsDefault(action.Id), Describe(action.Id)));
        }

        return rows;
    }

    /// <summary>Waits for the next key press and binds <paramref name="id"/> to it (the screen's Rebind button). Escape alone gives up. Another capture that was waiting is dropped.</summary>
    public void Begin(string id)
    {
        _ = keymap.Action(id);
        Capturing = id;
        Notice = null;
        keymap.Suspended = true;
    }

    /// <summary>Stops waiting for a key; nothing changes (the screen was closed, or Escape was pressed).</summary>
    public void Cancel()
    {
        Capturing = null;
        keymap.Suspended = false;
    }

    /// <summary>
    /// Called every frame while the screen is open. When an action waits for a key and one went down this frame, binds the action to it with the modifiers that are held (Ctrl+Shift+K) and returns the chord;
    /// a modifier alone is not a chord, and Escape with no modifier cancels instead. Returns null when nothing was captured.
    /// </summary>
    /// <param name="keys">The keyboard this frame.</param>
    public KeyChord? Poll(IKeyState keys)
    {
        if (Capturing is not { } id)
        {
            return null;
        }

        KeyMods mods = Keymap.Modifiers(keys);
        foreach (ImGuiKey key in KeyChord.Keys)
        {
            if (!keys.IsPressed(key))
            {
                continue;
            }

            if (key == ImGuiKey.ImGuiKeyEscape && mods == KeyMods.None)
            {
                Cancel();
                return null;
            }

            var chord = new KeyChord(key, mods);
            Cancel();
            keymap.Rebind(id, chord);
            List<string> conflicts = Describe(id);
            Notice = conflicts.Count == 0 ? null : $"{keymap.Action(id).Label}: {string.Join("; ", conflicts)}. It is bound anyway: both actions may fire.";
            return chord;
        }

        return null;
    }

    /// <summary>Unbinds the action: no key fires it.</summary>
    public void Clear(string id)
    {
        Cancel();
        keymap.Rebind(id, KeyChord.None);
        Notice = null;
    }

    /// <summary>Puts the action back to its default chords.</summary>
    public void Reset(string id)
    {
        Cancel();
        keymap.Reset(id);
        Notice = null;
    }

    /// <summary>Puts every action back to its default chords.</summary>
    public void ResetAll()
    {
        Cancel();
        keymap.ResetAll();
        Notice = null;
    }

    private List<string> Describe(string id)
    {
        var found = new List<string>();
        foreach (KeyConflict conflict in keymap.Conflicts(id))
        {
            string other = conflict.A == id ? conflict.B : conflict.A;
            KeyContext otherContext = conflict.A == id ? conflict.ContextB : conflict.ContextA;
            found.Add($"{conflict.Chord.ToString(_macOs)} is also bound to {keymap.Action(other).Label} ({Keymap.Describe(otherContext)})");
        }

        return found;
    }
}
