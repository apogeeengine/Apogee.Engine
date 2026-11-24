using System.Linq;
using Weft.Editor.Input;
using Weft.Native.CImGui;
using Xunit;

namespace Weft.Editor.Tests;

/// <summary>The logic of the rebinding screen with a fake keyboard: capturing a chord, giving up, collisions, resets.</summary>
public sealed class KeyBindingEditorTests
{
    private static (Keymap Map, KeyBindingEditor Editor) Make() => Make(new Keymap());

    private static (Keymap Map, KeyBindingEditor Editor) Make(Keymap map) => (map, new KeyBindingEditor(map, macOs: false));

    [Fact]
    public void TheNextKeyPressedBecomesTheBinding()
    {
        (Keymap map, KeyBindingEditor editor) = Make();

        editor.Begin("edit.undo");
        Assert.Equal("edit.undo", editor.Capturing);
        Assert.Null(editor.Poll(new FakeKeys())); // nothing pressed yet: still waiting
        Assert.Equal("edit.undo", editor.Capturing);

        KeyChord? chord = editor.Poll(new FakeKeys().Press("Ctrl+Shift+K"));

        Assert.Equal(KeyChord.Parse("Ctrl+Shift+K"), chord);
        Assert.Null(editor.Capturing);
        Assert.Equal([KeyChord.Parse("Ctrl+Shift+K")], map.Bindings("edit.undo"));
        Assert.Null(editor.Notice);
    }

    [Fact]
    public void AModifierAloneIsNotAChord()
    {
        (Keymap map, KeyBindingEditor editor) = Make();
        editor.Begin("edit.undo");

        var keys = new FakeKeys();
        keys.Hold("Ctrl+Z"); // Ctrl and Z are held, but none went down this frame
        Assert.Null(editor.Poll(keys));
        Assert.True(map.IsDefault("edit.undo"));
        Assert.NotNull(editor.Capturing);

        // Only Ctrl pressed: the modifier keys are not part of the capturable keys.
        Assert.DoesNotContain(ImGuiKey.ImGuiKeyLeftCtrl, KeyChord.Keys);
        Assert.DoesNotContain(ImGuiKey.ImGuiKeyLeftShift, KeyChord.Keys);
    }

    [Fact]
    public void EscapeGivesUpWithoutChangingAnything()
    {
        (Keymap map, KeyBindingEditor editor) = Make();
        editor.Begin("edit.undo");

        Assert.Null(editor.Poll(new FakeKeys().Press("Escape")));

        Assert.Null(editor.Capturing);
        Assert.True(map.IsDefault("edit.undo"));
        Assert.False(map.Suspended);
    }

    [Fact]
    public void EscapeWithAModifierIsAChord()
    {
        (Keymap map, KeyBindingEditor editor) = Make();
        editor.Begin("view.frame");

        Assert.Equal(KeyChord.Parse("Shift+Escape"), editor.Poll(new FakeKeys().Press("Shift+Escape")));
        Assert.Equal([KeyChord.Parse("Shift+Escape")], map.Bindings("view.frame"));
    }

    [Fact]
    public void NoShortcutFiresWhileAKeyIsAwaited()
    {
        (Keymap map, KeyBindingEditor editor) = Make();
        var keys = new FakeKeys().Press("Ctrl+Z");
        Assert.True(map.Pressed("edit.undo", KeyContext.Global, keys));

        editor.Begin("file.save");
        Assert.True(map.Suspended);
        Assert.False(map.Pressed("edit.undo", KeyContext.Global, keys));
        Assert.False(map.IsHeld("view.fly_forward", KeyContext.ViewportFly, new FakeKeys().Hold("W")));

        editor.Cancel();
        Assert.False(map.Suspended);
        Assert.True(map.Pressed("edit.undo", KeyContext.Global, keys));
    }

    [Fact]
    public void ACollidingChordIsAcceptedAndWarnedAbout()
    {
        (Keymap map, KeyBindingEditor editor) = Make();
        editor.Begin("file.save");

        KeyChord? chord = editor.Poll(new FakeKeys().Press("Ctrl+Z"));

        Assert.Equal(KeyChord.Parse("Ctrl+Z"), chord);
        Assert.Equal([KeyChord.Parse("Ctrl+Z")], map.Bindings("file.save")); // saved all the same
        Assert.NotNull(editor.Notice);
        Assert.Contains("Undo", editor.Notice);

        BindingRow save = editor.Rows().Single(r => r.Action.Id == "file.save");
        BindingRow undo = editor.Rows().Single(r => r.Action.Id == "edit.undo");
        Assert.Contains("Ctrl+Z is also bound to Undo", Assert.Single(save.Conflicts));
        Assert.Contains("Ctrl+Z is also bound to Save", Assert.Single(undo.Conflicts));
        Assert.Empty(editor.Rows().Single(r => r.Action.Id == "edit.copy").Conflicts);
    }

    [Fact]
    public void ACollisionBetweenContextsThatCannotBeActiveTogetherIsNotWarnedAbout()
    {
        (Keymap map, KeyBindingEditor editor) = Make();
        editor.Begin("entity.rename"); // Hierarchy

        _ = editor.Poll(new FakeKeys().Press("F")); // view.frame is Viewport: they never apply together

        Assert.Empty(map.Conflicts());
        Assert.Null(editor.Notice);
    }

    [Fact]
    public void ClearUnbindsAndResetPutsTheDefaultBack()
    {
        (Keymap map, KeyBindingEditor editor) = Make();

        editor.Clear("edit.undo");
        Assert.Empty(map.Bindings("edit.undo"));
        Assert.Equal("(unbound)", editor.Rows().Single(r => r.Action.Id == "edit.undo").Chords);
        Assert.False(editor.Rows().Single(r => r.Action.Id == "edit.undo").IsDefault);

        editor.Reset("edit.undo");
        Assert.True(map.IsDefault("edit.undo"));
        Assert.True(editor.Rows().Single(r => r.Action.Id == "edit.undo").IsDefault);

        map.Rebind("edit.copy", KeyChord.Parse("F9"));
        map.Rebind("edit.paste", KeyChord.Parse("F10"));
        editor.ResetAll();
        Assert.All(map.Actions, a => Assert.True(map.IsDefault(a.Id)));
    }

    [Fact]
    public void RowsShowEveryActionWithItsChordsForThePlatform()
    {
        (_, KeyBindingEditor editor) = Make();
        var mac = new KeyBindingEditor(new Keymap(), macOs: true);

        Assert.Equal(Keymap.Defaults.Count, editor.Rows().Count);
        Assert.Equal("Ctrl+Y or Ctrl+Shift+Z", editor.Rows().Single(r => r.Action.Id == "edit.redo").Chords);
        Assert.Equal("Cmd+Y or Cmd+Shift+Z", mac.Rows().Single(r => r.Action.Id == "edit.redo").Chords);
        Assert.All(editor.Rows(), r => Assert.True(r.IsDefault));
    }

    [Fact]
    public void BeginningAnotherCaptureDropsTheFirst()
    {
        (Keymap map, KeyBindingEditor editor) = Make();
        editor.Begin("edit.undo");
        editor.Begin("edit.redo");

        _ = editor.Poll(new FakeKeys().Press("F9"));

        Assert.True(map.IsDefault("edit.undo"));
        Assert.Equal([KeyChord.Parse("F9")], map.Bindings("edit.redo"));
    }

    [Fact]
    public void AnUnknownActionCannotBeCaptured()
    {
        (_, KeyBindingEditor editor) = Make();
        Assert.Throws<System.ArgumentException>(() => editor.Begin("no.such.action"));
    }

    [Fact]
    public void TheKeymapReportsEveryChange()
    {
        var map = new Keymap();
        int changes = 0;
        map.Changed += () => changes++;

        map.Rebind("edit.undo", KeyChord.Parse("F9"));
        map.SetBindings("edit.redo", []);
        map.Reset("edit.undo");
        map.ResetAll();

        Assert.Equal(4, changes);
    }
}
