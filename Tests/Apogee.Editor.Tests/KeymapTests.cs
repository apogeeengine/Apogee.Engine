using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Editor.Input;
using Apogee.Native.CImGui;
using Xunit;

namespace Apogee.Editor.Tests;

/// <summary>A keyboard the test drives: which keys went down this frame, which are held, whether a text field has the keyboard.</summary>
internal sealed class FakeKeys : IKeyState
{
    private readonly HashSet<ImGuiKey> _pressed = [];
    private readonly HashSet<ImGuiKey> _down = [];

    public bool KeyboardCaptured { get; set; }

    public bool IsPressed(ImGuiKey key) => _pressed.Contains(key);

    public bool IsDown(ImGuiKey key) => _down.Contains(key);

    /// <summary>Holds the chord's modifiers and presses its key, as a user does.</summary>
    public FakeKeys Press(string chord)
    {
        KeyChord parsed = KeyChord.Parse(chord);
        Hold(parsed.Mods);
        _pressed.Add(parsed.Key);
        _down.Add(parsed.Key);
        return this;
    }

    /// <summary>Holds a key down without a new press (it went down in an earlier frame).</summary>
    public FakeKeys Hold(string chord)
    {
        KeyChord parsed = KeyChord.Parse(chord);
        Hold(parsed.Mods);
        _down.Add(parsed.Key);
        return this;
    }

    private void Hold(KeyMods mods)
    {
        if ((mods & KeyMods.Primary) != 0) { _down.Add(ImGuiKey.ImGuiKeyLeftCtrl); }
        if ((mods & KeyMods.Shift) != 0) { _down.Add(ImGuiKey.ImGuiKeyRightShift); }
        if ((mods & KeyMods.Alt) != 0) { _down.Add(ImGuiKey.ImGuiKeyLeftAlt); }
    }
}

/// <summary>The keymap table: chords, defaults, collisions and matching, with no ImGui and no window.</summary>
public sealed class KeymapTests
{
    private static EditorAction Action(string id, KeyContext context, params string[] chords) => new(id, id, context, [.. chords.Select(KeyChord.Parse)]);

    [Fact]
    public void ChordsParseAndPrintPerPlatform()
    {
        KeyChord chord = KeyChord.Parse("ctrl+SHIFT+z");
        Assert.Equal(new KeyChord(ImGuiKey.ImGuiKeyZ, KeyMods.Primary | KeyMods.Shift), chord);
        Assert.Equal("Ctrl+Shift+Z", chord.ToString(macOs: false));
        Assert.Equal("Cmd+Shift+Z", chord.ToString(macOs: true));
        Assert.Equal(chord, KeyChord.Parse("Cmd+Shift+Z"));
        Assert.Equal(chord, KeyChord.Parse("Shift + Primary + Z"));

        Assert.Equal("F5", KeyChord.Parse("f5").ToString(false));
        Assert.Equal("Delete", KeyChord.Parse("Del").ToString(false));
        Assert.Equal("Escape", KeyChord.Parse("Esc").ToString(false));
        Assert.Equal("Alt+PageUp", KeyChord.Parse("alt+pageup").ToString(false));
        Assert.Equal("", KeyChord.None.ToString());
        Assert.True(KeyChord.None.IsNone);

        // Every key the table knows prints as something that parses back to it.
        foreach (string name in new[] { "A", "Q", "Z", "0", "9", "F1", "F12", "Escape", "Delete", "Backspace", "Enter", "Space", "Tab", "Insert", "Home", "End", "PageUp", "PageDown", "Left", "Right", "Up", "Down" })
        {
            KeyChord parsed = KeyChord.Parse("Ctrl+Alt+" + name);
            Assert.Equal(parsed, KeyChord.Parse(parsed.ToString(false)));
            Assert.Equal(parsed, KeyChord.Parse(parsed.ToString(true)));
            Assert.EndsWith(name, parsed.ToString(false));
        }

        foreach (string bad in new[] { "", "  ", "Ctrl", "Ctrl+", "Z+X", "Ctrl+Banana", "Shift+Alt" })
        {
            Assert.False(KeyChord.TryParse(bad, out _), bad);
            Assert.Throws<FormatException>(() => KeyChord.Parse(bad));
        }
    }

    [Fact]
    public void TheDefaultsAreTheEditorsShortcuts()
    {
        var keymap = new Keymap();
        string[] expected =
        [
            "edit.undo=Ctrl+Z/Global", "edit.redo=Ctrl+Y,Ctrl+Shift+Z/Global", "file.save=Ctrl+S/Global", "file.save_as=Ctrl+Shift+S/Global", "play.toggle=F5/Global",
            "edit.cut=Ctrl+X/Global", "edit.copy=Ctrl+C/Global", "edit.paste=Ctrl+V/Global", "edit.paste_child=Ctrl+Shift+V/Global", "edit.duplicate=Ctrl+D/Global",
            "edit.delete=Delete/Hierarchy", "entity.rename=F2/Hierarchy",
            "selection.clear=Escape/Viewport", "view.frame=F/Viewport", "gizmo.move=W/Viewport", "gizmo.rotate=E/Viewport", "gizmo.scale=R/Viewport",
            "view.fly_forward=W/ViewportFly", "view.fly_back=S/ViewportFly", "view.fly_left=A/ViewportFly", "view.fly_right=D/ViewportFly", "view.fly_up=E/ViewportFly", "view.fly_down=Q/ViewportFly",
        ];
        Assert.Equal(expected, keymap.Actions.Select(a => $"{a.Id}={string.Join(",", keymap.Bindings(a.Id).Select(c => c.ToString(false)))}/{a.Context}"));

        // A menu shows the first chord, and nothing for an unbound action.
        Assert.Equal("Ctrl+Y", keymap.ShortcutText("edit.redo", macOs: false));
        Assert.Equal("Cmd+Y", keymap.ShortcutText("edit.redo", macOs: true));
        Assert.Equal("Delete", keymap.ShortcutText("edit.delete", macOs: false));
        keymap.Rebind("edit.delete", KeyChord.None);
        Assert.Null(keymap.ShortcutText("edit.delete"));
        Assert.True(keymap.Binding("edit.delete").IsNone);
    }

    [Fact]
    public void DefaultsHaveNoConflicts()
    {
        Assert.Empty(new Keymap().Conflicts());
        Assert.Equal(Keymap.Defaults.Count, Keymap.Defaults.Select(a => a.Id).Distinct().Count());
    }

    [Fact]
    public void ARebindThatCollidesIsReported()
    {
        var keymap = new Keymap();
        keymap.Rebind("gizmo.rotate", KeyChord.Parse("W"));   // Move is W in the same context
        KeyConflict conflict = Assert.Single(keymap.Conflicts());
        Assert.Equal(("gizmo.move", "gizmo.rotate"), (conflict.A, conflict.B));
        Assert.Equal(KeyChord.Parse("W"), conflict.Chord);
        Assert.Equal("W is bound to both gizmo.move (Viewport) and gizmo.rotate (Viewport)", conflict.ToString());

        // Another panel's context does not collide (hierarchy and viewport are never focused together), and unbound or reset actions are out.
        keymap.Reset("gizmo.rotate");
        keymap.Rebind("entity.rename", KeyChord.Parse("W"));
        Assert.Empty(keymap.Conflicts());
        keymap.Rebind("entity.rename", KeyChord.None);
        keymap.Rebind("edit.delete", KeyChord.None);
        Assert.Empty(keymap.Conflicts());

        // One action may list a chord twice without colliding with itself.
        Assert.Empty(new Keymap([Action("a", KeyContext.Global, "Ctrl+Q", "Ctrl+Q")]).Conflicts());

        keymap.ResetAll();
        Assert.Equal(KeyChord.Parse("F2"), keymap.Binding("entity.rename"));
        Assert.Throws<ArgumentException>(() => keymap.Binding("no.such.action"));
        Assert.Throws<ArgumentException>(() => new Keymap([Action("a", KeyContext.Global, "A"), Action("a", KeyContext.Global, "B")]));
    }

    [Fact]
    public void GlobalCollidesWithEveryContext()
    {
        var keymap = new Keymap(
        [
            Action("global", KeyContext.Global, "Ctrl+K"),
            Action("viewport", KeyContext.Viewport, "Ctrl+K"),
            Action("hierarchy", KeyContext.Hierarchy, "Ctrl+K"),
            Action("assets", KeyContext.Assets, "Ctrl+K"),
            Action("other", KeyContext.Assets, "Ctrl+J"),
        ]);
        string[] pairs = [.. keymap.Conflicts().Select(c => $"{c.A}+{c.B}")];
        Assert.Equal(["global+viewport", "global+hierarchy", "global+assets"], pairs);

        // Two panels' own chords do not collide with each other.
        Assert.DoesNotContain(pairs, p => p.StartsWith("viewport+", StringComparison.Ordinal));
    }

    [Fact]
    public void PressedNeedsTheRightModifiersOnly()
    {
        var keymap = new Keymap();
        Assert.True(keymap.Pressed("edit.undo", KeyContext.Global, new FakeKeys().Press("Ctrl+Z")));
        Assert.False(keymap.Pressed("edit.undo", KeyContext.Global, new FakeKeys().Press("Z")));                 // Z alone
        Assert.False(keymap.Pressed("edit.undo", KeyContext.Global, new FakeKeys().Press("Ctrl+Shift+Z")));      // that is redo
        Assert.False(keymap.Pressed("edit.undo", KeyContext.Global, new FakeKeys().Press("Ctrl+Alt+Z")));
        Assert.True(keymap.Pressed("edit.redo", KeyContext.Global, new FakeKeys().Press("Ctrl+Shift+Z")));
        Assert.True(keymap.Pressed("edit.redo", KeyContext.Global, new FakeKeys().Press("Ctrl+Y")));             // the second chord
        Assert.False(keymap.Pressed("edit.redo", KeyContext.Global, new FakeKeys().Press("Ctrl+Z")));
        Assert.True(keymap.Pressed("edit.paste", KeyContext.Global, new FakeKeys().Press("Ctrl+V")));
        Assert.False(keymap.Pressed("edit.paste", KeyContext.Global, new FakeKeys().Press("Ctrl+Shift+V")));     // paste as child
        Assert.True(keymap.Pressed("edit.paste_child", KeyContext.Global, new FakeKeys().Press("Ctrl+Shift+V")));
        Assert.True(keymap.Pressed("play.toggle", KeyContext.Global, new FakeKeys().Press("F5")));
        Assert.False(keymap.Pressed("play.toggle", KeyContext.Global, new FakeKeys().Press("Ctrl+F5")));

        // A key that is only held (it went down in an earlier frame) is not a press.
        Assert.False(keymap.Pressed("edit.undo", KeyContext.Global, new FakeKeys().Hold("Ctrl+Z")));

        // A rebind takes effect, and the old chord stops working.
        keymap.Rebind("edit.undo", KeyChord.Parse("Ctrl+Alt+U"));
        Assert.True(keymap.Pressed("edit.undo", KeyContext.Global, new FakeKeys().Press("Ctrl+Alt+U")));
        Assert.False(keymap.Pressed("edit.undo", KeyContext.Global, new FakeKeys().Press("Ctrl+Z")));
        keymap.Reset("edit.undo");
        Assert.True(keymap.Pressed("edit.undo", KeyContext.Global, new FakeKeys().Press("Ctrl+Z")));
    }

    [Fact]
    public void AContextOnlyFiresWhenFocused()
    {
        var keymap = new Keymap();
        var f2 = new FakeKeys().Press("F2");
        Assert.True(keymap.Pressed("entity.rename", KeyContext.Hierarchy, f2));
        Assert.False(keymap.Pressed("entity.rename", KeyContext.Global, f2));       // no panel focused
        Assert.False(keymap.Pressed("entity.rename", KeyContext.Viewport, f2));

        var w = new FakeKeys().Press("W");
        Assert.True(keymap.Pressed("gizmo.move", KeyContext.Viewport, w));
        Assert.False(keymap.Pressed("gizmo.move", KeyContext.Hierarchy, w));
        Assert.False(keymap.Pressed("gizmo.move", KeyContext.ViewportFly, w));       // flying: W is the camera's

        // A global action fires whichever panel is focused.
        var save = new FakeKeys().Press("Ctrl+S");
        foreach (KeyContext focused in Enum.GetValues<KeyContext>())
        {
            Assert.True(keymap.Pressed("file.save", focused, save), focused.ToString());
        }
    }

    [Fact]
    public void TypingInATextFieldFiresNothing()
    {
        var keymap = new Keymap();
        foreach (EditorAction action in keymap.Actions)
        {
            var keys = new FakeKeys { KeyboardCaptured = true };
            foreach (KeyChord chord in keymap.Bindings(action.Id))
            {
                keys.Press(chord.ToString(false));
            }

            Assert.False(keymap.Pressed(action.Id, action.Context, keys), action.Id);
            Assert.False(keymap.IsHeld(action.Id, action.Context, keys), action.Id);
        }
    }

    [Fact]
    public void HeldKeysAllowExtraModifiers()
    {
        var keymap = new Keymap();
        Assert.True(keymap.IsHeld("view.fly_forward", KeyContext.ViewportFly, new FakeKeys().Hold("W")));
        Assert.True(keymap.IsHeld("view.fly_forward", KeyContext.ViewportFly, new FakeKeys().Hold("Shift+W")));    // Shift flies faster
        Assert.False(keymap.IsHeld("view.fly_forward", KeyContext.ViewportFly, new FakeKeys().Hold("S")));
        Assert.False(keymap.IsHeld("view.fly_forward", KeyContext.Viewport, new FakeKeys().Hold("W")));
        Assert.False(keymap.IsHeld("view.fly_forward", KeyContext.ViewportFly, new FakeKeys()));

        // A held action bound with a modifier needs it.
        keymap.Rebind("view.fly_up", KeyChord.Parse("Alt+E"));
        Assert.False(keymap.IsHeld("view.fly_up", KeyContext.ViewportFly, new FakeKeys().Hold("E")));
        Assert.True(keymap.IsHeld("view.fly_up", KeyContext.ViewportFly, new FakeKeys().Hold("Alt+E")));
        Assert.True(keymap.IsHeld("view.fly_up", KeyContext.ViewportFly, new FakeKeys().Hold("Ctrl+Alt+E")));
    }

    [Fact]
    public void TheManualsShortcutTableIsTheKeymap()
    {
        string? folder = AppContext.BaseDirectory;
        while (folder is not null && !File.Exists(Path.Combine(folder, "Apogee.slnx")))
        {
            folder = Path.GetDirectoryName(folder);
        }

        Assert.NotNull(folder);
        string page = File.ReadAllText(Path.Combine(folder, "Docs", "manual", "tools", "editor.md")).Replace("\r\n", "\n");
        const string Begin = "<!-- keymap:begin (checked against Keymap.Defaults by KeymapTests) -->\n";
        const string End = "<!-- keymap:end -->";
        int start = page.IndexOf(Begin, StringComparison.Ordinal);
        Assert.True(start >= 0, "editor.md has no keymap table markers");
        start += Begin.Length;
        int end = page.IndexOf(End, start, StringComparison.Ordinal);
        Assert.True(end > start);
        Assert.Equal(new Keymap().MarkdownTable(macOs: false).TrimEnd('\n'), page[start..end].Trim('\n'));
    }
}
