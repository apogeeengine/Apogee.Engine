using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Apogee.Native.CImGui;

namespace Apogee.Editor.Input;

/// <summary>Where a shortcut applies: everywhere, or only while one panel has the keyboard focus.</summary>
public enum KeyContext
{
    /// <summary>Anywhere in the editor.</summary>
    Global,

    /// <summary>The viewport has the keyboard focus (and the right mouse button is not held).</summary>
    Viewport,

    /// <summary>The viewport has the focus and the right mouse button is held: the camera flies.</summary>
    ViewportFly,

    /// <summary>The hierarchy has the keyboard focus.</summary>
    Hierarchy,

    /// <summary>The asset browser has the keyboard focus.</summary>
    Assets,
}

/// <summary>What the keyboard looks like this frame: the key presses the matching reads. ImGui implements it (<see cref="ImGuiKeyState"/>); tests use a fake.</summary>
public interface IKeyState
{
    /// <summary>True on the frame <paramref name="key"/> went down (no auto-repeat).</summary>
    bool IsPressed(ImGuiKey key);

    /// <summary>True while <paramref name="key"/> is held.</summary>
    bool IsDown(ImGuiKey key);

    /// <summary>True when a widget (a text field, a menu being navigated) is using the keyboard: no shortcut fires then, so typing never triggers one.</summary>
    bool KeyboardCaptured { get; }
}

/// <summary>An editor command that has a key binding.</summary>
/// <param name="Id">The stable id (<c>edit.undo</c>) the handlers and the menus look it up by.</param>
/// <param name="Label">What the command is called in the documentation and (later) in the settings.</param>
/// <param name="Context">Where it applies.</param>
/// <param name="Defaults">The chords it is bound to until rebound (any one of them fires it).</param>
/// <param name="Held">True for a command that acts while its key is held (flying the camera) rather than once per press; extra modifiers (Shift to go faster) do not stop it.</param>
public sealed record EditorAction(string Id, string Label, KeyContext Context, IReadOnlyList<KeyChord> Defaults, bool Held = false);

/// <summary>Two actions whose chords collide: the same chord, in contexts that can both be active (the same one, or one of them <see cref="KeyContext.Global"/>).</summary>
/// <param name="A">The first action's id.</param>
/// <param name="B">The second action's id.</param>
/// <param name="Chord">The chord they share.</param>
/// <param name="ContextA">The first action's context.</param>
/// <param name="ContextB">The second action's context.</param>
public sealed record KeyConflict(string A, string B, KeyChord Chord, KeyContext ContextA, KeyContext ContextB)
{
    /// <summary>A sentence that says what collides, such as "Ctrl+Z is bound to both edit.undo (Global) and view.frame (Viewport)".</summary>
    public override string ToString() => $"{Chord} is bound to both {A} ({ContextA}) and {B} ({ContextB})";
}

/// <summary>
/// The editor's key bindings: a table from action id to chords, with the contexts they apply in, the matching (<see cref="Pressed"/>, <see cref="Held"/>) and the check for collisions. It holds no ImGui state:
/// the keys come through <see cref="IKeyState"/>. The in-memory table starts as <see cref="Defaults"/>; <see cref="Rebind"/> and <see cref="Reset"/> change it (nothing saves it yet).
/// </summary>
public sealed class Keymap
{
    private readonly Dictionary<string, EditorAction> _actions = [];
    private readonly List<EditorAction> _order = [];
    private readonly Dictionary<string, KeyChord[]> _bindings = [];

    /// <summary>Raised after a binding changed (<see cref="Rebind"/>, <see cref="SetBindings"/>, <see cref="Reset"/>, <see cref="ResetAll"/>): what keeps the table in the preferences listens.</summary>
    public event Action? Changed;

    /// <summary>
    /// True while no shortcut may fire (<see cref="Pressed"/> and <see cref="IsHeld"/> answer false): the rebinding screen is waiting for a key, and the key it gets must not also do what it is bound to.
    /// </summary>
    public bool Suspended { get; set; }

    /// <summary>The default actions and their chords: today's shortcuts.</summary>
    public static IReadOnlyList<EditorAction> Defaults { get; } =
    [
        new("edit.undo", "Undo", KeyContext.Global, [Chord("Ctrl+Z")]),
        new("edit.redo", "Redo", KeyContext.Global, [Chord("Ctrl+Y"), Chord("Ctrl+Shift+Z")]),
        new("file.save", "Save", KeyContext.Global, [Chord("Ctrl+S")]),
        new("file.save_as", "Save as", KeyContext.Global, [Chord("Ctrl+Shift+S")]),
        new("play.toggle", "Play or stop", KeyContext.Global, [Chord("F5")]),
        new("edit.cut", "Cut", KeyContext.Global, [Chord("Ctrl+X")]),
        new("edit.copy", "Copy", KeyContext.Global, [Chord("Ctrl+C")]),
        new("edit.paste", "Paste", KeyContext.Global, [Chord("Ctrl+V")]),
        new("edit.paste_child", "Paste as child", KeyContext.Global, [Chord("Ctrl+Shift+V")]),
        new("edit.duplicate", "Duplicate", KeyContext.Global, [Chord("Ctrl+D")]),
        new("edit.delete", "Delete", KeyContext.Hierarchy, [Chord("Delete")]),
        new("entity.rename", "Rename", KeyContext.Hierarchy, [Chord("F2")]),
        new("selection.clear", "Clear the selection", KeyContext.Viewport, [Chord("Escape")]),
        new("view.frame", "Frame the selection", KeyContext.Viewport, [Chord("F")]),
        new("gizmo.move", "Move tool", KeyContext.Viewport, [Chord("W")]),
        new("gizmo.rotate", "Rotate tool", KeyContext.Viewport, [Chord("E")]),
        new("gizmo.scale", "Scale tool", KeyContext.Viewport, [Chord("R")]),
        new("view.fly_forward", "Fly forward", KeyContext.ViewportFly, [Chord("W")], Held: true),
        new("view.fly_back", "Fly back", KeyContext.ViewportFly, [Chord("S")], Held: true),
        new("view.fly_left", "Fly left", KeyContext.ViewportFly, [Chord("A")], Held: true),
        new("view.fly_right", "Fly right", KeyContext.ViewportFly, [Chord("D")], Held: true),
        new("view.fly_up", "Fly up", KeyContext.ViewportFly, [Chord("E")], Held: true),
        new("view.fly_down", "Fly down", KeyContext.ViewportFly, [Chord("Q")], Held: true),
    ];

    /// <summary>A table with the <see cref="Defaults"/>.</summary>
    public Keymap()
        : this(Defaults)
    {
    }

    /// <summary>A table over the given actions, bound to their default chords (tests make small ones).</summary>
    public Keymap(IEnumerable<EditorAction> actions)
    {
        foreach (EditorAction action in actions)
        {
            if (!_actions.TryAdd(action.Id, action))
            {
                throw new ArgumentException($"The action '{action.Id}' is listed twice.", nameof(actions));
            }

            _order.Add(action);
            _bindings[action.Id] = [.. action.Defaults];
        }
    }

    /// <summary>The actions, in the order they were given.</summary>
    public IReadOnlyList<EditorAction> Actions => _order;

    /// <summary>The action with this id; throws <see cref="ArgumentException"/> when there is none.</summary>
    public EditorAction Action(string id) =>
        _actions.TryGetValue(id, out EditorAction? action) ? action : throw new ArgumentException($"There is no action '{id}'.", nameof(id));

    /// <summary>The first chord the action is bound to (what a menu shows), or <see cref="KeyChord.None"/> when it is unbound.</summary>
    public KeyChord Binding(string id)
    {
        KeyChord[] chords = Chords(id);
        return chords.Length == 0 ? KeyChord.None : chords[0];
    }

    /// <summary>Every chord the action is bound to.</summary>
    public IReadOnlyList<KeyChord> Bindings(string id) => Chords(id);

    /// <summary>The text a menu item shows next to the action: its first chord for the current platform, or null when it is unbound.</summary>
    public string? ShortcutText(string id, bool? macOs = null)
    {
        KeyChord chord = Binding(id);
        return chord.IsNone ? null : chord.ToString(macOs ?? OperatingSystem.IsMacOS());
    }

    /// <summary>Binds the action to this one chord, replacing what it had. <see cref="KeyChord.None"/> unbinds it. Collisions are not refused: <see cref="Conflicts"/> reports them.</summary>
    public void Rebind(string id, KeyChord chord) => SetBindings(id, chord.IsNone ? [] : [chord]);

    /// <summary>Binds the action to exactly these chords (none: it is unbound), replacing what it had; a chord that is <see cref="KeyChord.None"/> and a repeated one are dropped.</summary>
    public void SetBindings(string id, IEnumerable<KeyChord> chords)
    {
        _ = Action(id);
        _bindings[id] = [.. chords.Where(c => !c.IsNone).Distinct()];
        Changed?.Invoke();
    }

    /// <summary>Puts the action back to its default chords.</summary>
    public void Reset(string id)
    {
        _bindings[id] = [.. Action(id).Defaults];
        Changed?.Invoke();
    }

    /// <summary>Puts every action back to its default chords.</summary>
    public void ResetAll()
    {
        foreach (EditorAction action in _order)
        {
            _bindings[action.Id] = [.. action.Defaults];
        }

        Changed?.Invoke();
    }

    /// <summary>True when the action is bound to its default chords (what the preferences write nothing for).</summary>
    public bool IsDefault(string id) => Chords(id).AsSpan().SequenceEqual(Action(id).Defaults.ToArray());

    /// <summary>
    /// The pairs of actions that share a chord where both could be active: the same context, or one of them <see cref="KeyContext.Global"/> (which is active in every panel). An action's own chords
    /// never collide with each other; unbound actions do not collide.
    /// </summary>
    public IReadOnlyList<KeyConflict> Conflicts()
    {
        var found = new List<KeyConflict>();
        List<EditorAction> list = _order;
        for (int i = 0; i < list.Count; i++)
        {
            for (int j = i + 1; j < list.Count; j++)
            {
                EditorAction a = list[i];
                EditorAction b = list[j];
                if (a.Context != b.Context && a.Context != KeyContext.Global && b.Context != KeyContext.Global)
                {
                    continue;
                }

                foreach (KeyChord chord in _bindings[a.Id].Distinct())
                {
                    if (!chord.IsNone && _bindings[b.Id].Contains(chord))
                    {
                        found.Add(new KeyConflict(a.Id, b.Id, chord, a.Context, b.Context));
                    }
                }
            }
        }

        return found;
    }

    /// <summary>The collisions that involve the action <paramref name="id"/>, as <see cref="Conflicts()"/> reports them.</summary>
    public IReadOnlyList<KeyConflict> Conflicts(string id) => [.. Conflicts().Where(c => c.A == id || c.B == id)];

    /// <summary>
    /// True on the frame the action's chord went down (the key pressed with exactly its modifiers: Shift+Z is not Z) while <paramref name="focused"/> is the action's context or the action is
    /// <see cref="KeyContext.Global"/>. Nothing fires while <see cref="IKeyState.KeyboardCaptured"/> is true.
    /// </summary>
    /// <param name="id">The action.</param>
    /// <param name="focused">The context that has the keyboard now: <see cref="KeyContext.Global"/> when no panel in particular does.</param>
    /// <param name="keys">The keyboard.</param>
    public bool Pressed(string id, KeyContext focused, IKeyState keys)
    {
        EditorAction action = Action(id);
        if (Suspended || !Applies(action, focused, keys))
        {
            return false;
        }

        KeyMods mods = Modifiers(keys);
        foreach (KeyChord chord in _bindings[id])
        {
            if (!chord.IsNone && chord.Mods == mods && keys.IsPressed(chord.Key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True while the action's key is held with at least its modifiers (extra ones, such as Shift for speed, do not matter): what a continuous action like flying reads. The same context and
    /// keyboard rules as <see cref="Pressed"/>.
    /// </summary>
    /// <param name="id">The action.</param>
    /// <param name="focused">The context that has the keyboard now.</param>
    /// <param name="keys">The keyboard.</param>
    public bool IsHeld(string id, KeyContext focused, IKeyState keys)
    {
        EditorAction action = Action(id);
        if (Suspended || !Applies(action, focused, keys))
        {
            return false;
        }

        KeyMods mods = Modifiers(keys);
        foreach (KeyChord chord in _bindings[id])
        {
            if (!chord.IsNone && (mods & chord.Mods) == chord.Mods && keys.IsDown(chord.Key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The Markdown table of the actions, their chords and where they apply, as the manual shows it (<see cref="Defaults"/> for a new table). A test keeps the manual's copy equal to this.</summary>
    /// <param name="macOs">Show Cmd instead of Ctrl.</param>
    public string MarkdownTable(bool macOs = false)
    {
        var text = new StringBuilder();
        text.Append("| Action | Shortcut | Where |\n| --- | --- | --- |\n");
        foreach (EditorAction action in _order)
        {
            string chords = string.Join(" or ", _bindings[action.Id].Select(c => c.ToString(macOs)));
            text.Append("| ").Append(action.Label).Append(" | ").Append(chords.Length == 0 ? "(unbound)" : chords).Append(" | ").Append(Describe(action.Context)).Append(" |\n");
        }

        return text.ToString();
    }

    /// <summary>The manual's words for a context.</summary>
    public static string Describe(KeyContext context) => context switch
    {
        KeyContext.Global => "Anywhere",
        KeyContext.Viewport => "Viewport focused",
        KeyContext.ViewportFly => "Viewport, right mouse button held",
        KeyContext.Hierarchy => "Hierarchy focused",
        KeyContext.Assets => "Assets focused",
        _ => context.ToString(),
    };

    private static bool Applies(EditorAction action, KeyContext focused, IKeyState keys) =>
        !keys.KeyboardCaptured && (action.Context == KeyContext.Global || action.Context == focused);

    /// <summary>The modifier keys that are down now (either Ctrl, either Shift, either Alt).</summary>
    public static KeyMods Modifiers(IKeyState keys)
    {
        KeyMods mods = KeyMods.None;
        if (keys.IsDown(ImGuiKey.ImGuiKeyLeftCtrl) || keys.IsDown(ImGuiKey.ImGuiKeyRightCtrl))
        {
            mods |= KeyMods.Primary;
        }

        if (keys.IsDown(ImGuiKey.ImGuiKeyLeftShift) || keys.IsDown(ImGuiKey.ImGuiKeyRightShift))
        {
            mods |= KeyMods.Shift;
        }

        if (keys.IsDown(ImGuiKey.ImGuiKeyLeftAlt) || keys.IsDown(ImGuiKey.ImGuiKeyRightAlt))
        {
            mods |= KeyMods.Alt;
        }

        return mods;
    }

    private KeyChord[] Chords(string id) =>
        _bindings.TryGetValue(id, out KeyChord[]? chords) ? chords : throw new ArgumentException($"There is no action '{id}'.", nameof(id));

    private static KeyChord Chord(string text) => KeyChord.Parse(text);
}
