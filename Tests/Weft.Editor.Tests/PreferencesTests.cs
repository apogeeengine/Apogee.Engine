using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Weft.Editor.Editing;
using Weft.Editor.Input;
using Weft.Native.CImGui;
using Weft.Native.ImGuizmo;
using Weft.Native.Tests;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;
using Lua = Weft.Native.Luau.Lua;

namespace Weft.Editor.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan time) => _ticks += (long)time.TotalMilliseconds;
}

/// <summary>
/// The editor's preferences file: values, recent projects, the last scene of a project and the key bindings, in a temporary editor folder (never the real one), with no window.
/// </summary>
public sealed class PreferencesTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("weft-prefs-");

    public PreferencesTests()
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
    }

    public void Dispose() => _root.Delete(recursive: true);

    private string EditorFolder => Path.Combine(_root.FullName, "editor");

    private string PreferencesFile => Path.Combine(EditorFolder, EditorPreferences.FileName);

    private EditorPreferences Open() => EditorPreferences.Open(EditorFolder);

    private string Folder(string name)
    {
        string path = Path.Combine(_root.FullName, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ValuesSurviveAReopen()
    {
        EditorPreferences first = Open();
        Assert.Equal(6, first.CameraSpeed);
        Assert.True(first.Persistent);
        Assert.Empty(first.Problems);

        first.CameraSpeed = 12.5;
        first.UiScale = 1.25;
        var gizmo = new GizmoSettings { Operation = (int)AgzOperation.Rotate, World = false, Pivot = PivotMode.Individual, Snap = true, MoveStep = 2f, RotateStep = 45f, ScaleStep = 0.25f, ShowHelpers = false, PreferGameCamera = false };
        first.CaptureFrom(gizmo);
        first.Flush();

        Assert.True(File.Exists(PreferencesFile));
        Assert.False(File.Exists(Path.Combine(EditorFolder, "settings.xml")), "the editor never writes the game's settings file");
        Assert.Equal(["preferences.xml"], Directory.GetFiles(EditorFolder).Select(Path.GetFileName)); // the atomic write leaves no temporary file

        EditorPreferences second = Open();
        Assert.Equal(12.5, second.CameraSpeed);
        Assert.Equal(1.25, second.UiScale);
        var restored = new GizmoSettings();
        second.ApplyTo(restored);
        Assert.Equal((int)AgzOperation.Rotate, restored.Operation);
        Assert.False(restored.World);
        Assert.Equal(PivotMode.Individual, restored.Pivot);
        Assert.True(restored.Snap);
        Assert.Equal(2f, restored.MoveStep);
        Assert.Equal(45f, restored.RotateStep);
        Assert.Equal(0.25f, restored.ScaleStep);
        Assert.False(restored.ShowHelpers);
        Assert.False(restored.PreferGameCamera);
    }

    [Fact]
    public void OnlyWhatDiffersFromTheDefaultsIsWritten()
    {
        EditorPreferences prefs = Open();
        prefs.CaptureFrom(new GizmoSettings()); // the defaults
        prefs.Flush();
        Assert.False(File.Exists(PreferencesFile));

        prefs.CameraSpeed = 9;
        prefs.Flush();
        string text = File.ReadAllText(PreferencesFile);
        Assert.Contains("editor.camera.speed", text);
        Assert.DoesNotContain("editor.gizmo", text);
    }

    [Fact]
    public void ASingleStepIsWrittenAsTheDecimalTheUserTyped()
    {
        EditorPreferences prefs = Open();
        prefs.CaptureFrom(new GizmoSettings { ScaleStep = 0.3f });
        prefs.Flush();

        Assert.Contains(">0.3<", File.ReadAllText(PreferencesFile)); // not 0.30000001192092896
        var restored = new GizmoSettings();
        Open().ApplyTo(restored);
        Assert.Equal(0.3f, restored.ScaleStep);
    }

    [Fact]
    public void ANumberOutsideItsRangeIsClamped()
    {
        EditorPreferences prefs = Open();
        prefs.CameraSpeed = 100000;
        Assert.Equal(200, prefs.CameraSpeed);
        prefs.UiScale = 0;
        Assert.Equal(0.5, prefs.UiScale);
    }

    [Fact]
    public void WritesAreDebouncedAndFlushedAtTheEnd()
    {
        var time = new ManualTime();
        EditorPreferences prefs = Open();
        prefs.Clock = time;

        prefs.CameraSpeed = 7;
        prefs.SaveIfDirty();
        Assert.False(prefs.IsDirty); // the first change is written at once
        Assert.Contains(">7<", File.ReadAllText(PreferencesFile));

        prefs.CameraSpeed = 8;
        prefs.SaveIfDirty();
        Assert.True(prefs.IsDirty); // too soon after the last write
        Assert.DoesNotContain(">8<", File.ReadAllText(PreferencesFile));

        time.Advance(EditorPreferences.SaveInterval + TimeSpan.FromMilliseconds(1));
        prefs.SaveIfDirty();
        Assert.False(prefs.IsDirty);
        Assert.Contains(">8<", File.ReadAllText(PreferencesFile));

        prefs.CameraSpeed = 9;
        prefs.Flush();
        Assert.Contains(">9<", File.ReadAllText(PreferencesFile));
    }

    [Fact]
    public void RecentProjectsAreMostRecentFirstWithoutDuplicatesAndMissingOnesDrop()
    {
        string a = Folder("a");
        string b = Folder("b");
        string c = Folder("c");
        EditorPreferences prefs = Open();
        Assert.Empty(prefs.Recent);

        prefs.AddRecent(a);
        prefs.AddRecent(b);
        prefs.AddRecent(c);
        Assert.Equal([c, b, a], prefs.Recent);

        prefs.AddRecent(a + Path.DirectorySeparatorChar); // the same folder, spelled differently: moves up, no duplicate
        Assert.Equal([a, c, b], prefs.Recent);
        prefs.Flush();

        Assert.Equal([a, c, b], Open().Recent);

        Directory.Delete(c);
        EditorPreferences pruned = Open();
        Assert.Equal([a, b], pruned.Recent);
        pruned.Flush();
        Assert.DoesNotContain(c, File.ReadAllText(PreferencesFile)); // the file forgot it too

        pruned.RemoveRecent(a);
        Assert.Equal([b], pruned.Recent);
    }

    [Fact]
    public void TheRecentListIsBounded()
    {
        EditorPreferences prefs = Open();
        string[] folders = [.. Enumerable.Range(0, EditorPreferences.MaxRecent + 3).Select(i => Folder("p" + i))];
        foreach (string folder in folders)
        {
            prefs.AddRecent(folder);
        }

        Assert.Equal(EditorPreferences.MaxRecent, prefs.Recent.Count);
        Assert.Equal(folders[^1], prefs.Recent[0]);
        Assert.DoesNotContain(folders[0], prefs.Recent);
        prefs.Flush();
        Assert.Equal(prefs.Recent, Open().Recent);
    }

    [Fact]
    public void ARecentEntryThatIsNotAFullPathIsIgnored()
    {
        Directory.CreateDirectory(EditorFolder);
        File.WriteAllText(PreferencesFile, "<settings version=\"1\"><value key=\"recent.0\">relative/folder</value><value key=\"recent.1\">" + Folder("ok") + "</value></settings>");
        Assert.Equal([Path.Combine(_root.FullName, "ok")], Open().Recent);
    }

    [Fact]
    public void TheLastSceneIsKeptPerProject()
    {
        string a = Folder("a");
        string b = Folder("b");
        EditorPreferences prefs = Open();
        Assert.Null(prefs.LastScene(a));

        prefs.SetLastScene(a, "res://scenes/one.ascene");
        prefs.SetLastScene(b, "res://other.ascene");
        prefs.Flush();

        Assert.Matches(@"^project\.[0-9a-f]{16}\.scene$", EditorPreferences.SceneKey(a));
        Assert.NotEqual(EditorPreferences.SceneKey(a), EditorPreferences.SceneKey(b));
        Assert.Equal(EditorPreferences.SceneKey(a), EditorPreferences.SceneKey(a + Path.DirectorySeparatorChar));

        EditorPreferences second = Open();
        Assert.Equal("res://scenes/one.ascene", second.LastScene(a));
        Assert.Equal("res://other.ascene", second.LastScene(b));

        second.SetLastScene(a, null);
        second.Flush();
        Assert.Null(Open().LastScene(a));
    }

    // ---- Key bindings -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARebindIsSavedAndADefaultIsNotWritten()
    {
        var keymap = new Keymap();
        EditorPreferences prefs = Open();
        Assert.Empty(prefs.AttachKeymap(keymap));
        prefs.Flush();
        Assert.False(File.Exists(PreferencesFile), "no rebind, so nothing to write");

        keymap.Rebind("edit.undo", KeyChord.Parse("Ctrl+Alt+U"));
        keymap.Rebind("file.save", KeyChord.None);
        keymap.SetBindings("edit.redo", [KeyChord.Parse("Ctrl+Shift+Y"), KeyChord.Parse("F9")]);
        prefs.Flush();

        string text = File.ReadAllText(PreferencesFile);
        Assert.Contains("keys.edit.undo", text);
        Assert.Contains("Ctrl+Alt+U", text);
        Assert.Contains("keys.file.save", text);
        Assert.Contains("keys.edit.redo", text);
        Assert.DoesNotContain("keys.edit.copy", text); // untouched actions are not written
        Assert.DoesNotContain("keys.play.toggle", text);

        var reopened = new Keymap();
        Assert.Empty(Open().AttachKeymap(reopened));
        Assert.Equal([KeyChord.Parse("Ctrl+Alt+U")], reopened.Bindings("edit.undo"));
        Assert.Empty(reopened.Bindings("file.save"));
        Assert.Equal([KeyChord.Parse("Ctrl+Shift+Y"), KeyChord.Parse("F9")], reopened.Bindings("edit.redo"));
        Assert.True(reopened.IsDefault("edit.copy"));

        // Putting an action back removes it from the file again.
        keymap.Reset("edit.undo");
        keymap.Reset("file.save");
        keymap.Reset("edit.redo");
        prefs.Flush();
        Assert.False(File.Exists(PreferencesFile) && File.ReadAllText(PreferencesFile).Contains("keys."));
    }

    [Fact]
    public void ResettingEverythingEmptiesTheSavedBindings()
    {
        var keymap = new Keymap();
        EditorPreferences prefs = Open();
        _ = prefs.AttachKeymap(keymap);
        keymap.Rebind("edit.undo", KeyChord.Parse("F9"));
        keymap.Rebind("edit.copy", KeyChord.Parse("F10"));
        prefs.Flush();
        Assert.Contains("keys.edit.copy", File.ReadAllText(PreferencesFile));

        keymap.ResetAll();
        prefs.Flush();
        Assert.DoesNotContain("keys.", File.ReadAllText(PreferencesFile));
    }

    [Fact]
    public void BindingsOfActionsThisEditorDoesNotKnowAreKept()
    {
        Directory.CreateDirectory(EditorFolder);
        File.WriteAllText(PreferencesFile, "<settings version=\"1\"><value key=\"keys.future.thing\">Ctrl+Q</value><value key=\"keys.edit.undo\">F9</value></settings>");

        var keymap = new Keymap();
        EditorPreferences prefs = Open();
        Assert.Empty(prefs.AttachKeymap(keymap));
        Assert.Equal([KeyChord.Parse("F9")], keymap.Bindings("edit.undo"));

        keymap.Rebind("edit.copy", KeyChord.Parse("F10"));
        prefs.Flush();
        string text = File.ReadAllText(PreferencesFile);
        Assert.Contains("keys.future.thing", text);
        Assert.Contains("Ctrl+Q", text);
        Assert.Contains("keys.edit.copy", text);
    }

    [Fact]
    public void ConflictsFromTheSavedBindingsAreReportedOnLoad()
    {
        Directory.CreateDirectory(EditorFolder);
        File.WriteAllText(PreferencesFile, "<settings version=\"1\"><value key=\"keys.file.save\">Ctrl+Z</value></settings>");

        var keymap = new Keymap();
        IReadOnlyList<string> problems = Open().AttachKeymap(keymap);

        string problem = Assert.Single(problems);
        Assert.Contains(KeyChord.Parse("Ctrl+Z").ToString(), problem); // Cmd+Z on a Mac: the message uses the label of the platform
        Assert.Contains("file.save", problem);
        Assert.Contains("edit.undo", problem);
        Assert.Equal([KeyChord.Parse("Ctrl+Z")], keymap.Bindings("file.save")); // reported, not refused
    }

    [Fact]
    public void AChordThatDoesNotReadKeepsTheDefaultAndSaysSo()
    {
        Directory.CreateDirectory(EditorFolder);
        File.WriteAllText(PreferencesFile, "<settings version=\"1\"><value key=\"keys.edit.undo\">Ctrl+Banana</value><value key=\"keys.edit.copy\">F10 | Ctrl+Shift+Nope</value></settings>");

        var keymap = new Keymap();
        EditorPreferences prefs = Open();
        IReadOnlyList<string> problems = prefs.AttachKeymap(keymap);

        Assert.Equal(2, problems.Count);
        Assert.True(keymap.IsDefault("edit.undo"));
        Assert.True(keymap.IsDefault("edit.copy")); // all or nothing: a half-read list is not applied
        prefs.Flush();
        Assert.DoesNotContain("Banana", File.ReadAllText(PreferencesFile)); // the bad value is dropped, and the report is not repeated at the next start
    }

    [Fact]
    public void BindingsAreWrittenTheSameOnEveryPlatform()
    {
        Assert.Equal("Ctrl+Shift+K", EditorPreferences.FormatChords([KeyChord.Parse("Cmd+Shift+K")]));
        Assert.Equal("none", EditorPreferences.FormatChords([]));
        Assert.True(EditorPreferences.TryParseChords("Cmd+K | Alt+F2", out var chords));
        Assert.Equal(2, chords.Count);
        Assert.True(EditorPreferences.TryParseChords("none", out chords));
        Assert.Empty(chords);
        Assert.False(EditorPreferences.TryParseChords("K |", out _));
    }

    // ---- Files that do not read -------------------------------------------------------------------------------------------------

    [Fact]
    public void ACorruptFileGivesDefaultsAndIsKeptAsACopy()
    {
        Directory.CreateDirectory(EditorFolder);
        File.WriteAllText(PreferencesFile, "this is <not xml");

        EditorPreferences prefs = Open();

        Assert.Equal(6, prefs.CameraSpeed);
        Assert.Single(prefs.Problems);
        Assert.Contains("preferences.bad.xml", prefs.Problems[0]);
        Assert.Equal("this is <not xml", File.ReadAllText(Path.Combine(EditorFolder, "preferences.bad.xml")));

        prefs.CameraSpeed = 10;
        prefs.Flush();
        Assert.Equal(10, Open().CameraSpeed);
        Assert.Equal("this is <not xml", File.ReadAllText(Path.Combine(EditorFolder, "preferences.bad.xml"))); // the copy stays
    }

    [Fact]
    public void AFileWithAnotherRootIsTreatedAsCorrupt()
    {
        Directory.CreateDirectory(EditorFolder);
        File.WriteAllText(PreferencesFile, "<scene version=\"1\"/>");

        EditorPreferences prefs = Open();

        Assert.Single(prefs.Problems);
        Assert.Equal("<scene version=\"1\"/>", File.ReadAllText(Path.Combine(EditorFolder, "preferences.bad.xml")));
        Assert.Equal(6, prefs.CameraSpeed);
    }

    [Fact]
    public void AFileFromANewerEditorIsSetAsideAndNeverOverwritten()
    {
        Directory.CreateDirectory(EditorFolder);
        const string newer = "<settings version=\"2\"><value key=\"editor.camera.speed\">20</value><value key=\"editor.something_new\">x</value></settings>";
        File.WriteAllText(PreferencesFile, newer);

        EditorPreferences prefs = Open();

        Assert.Equal(6, prefs.CameraSpeed); // not read
        Assert.Single(prefs.Problems);
        Assert.Contains("newer", prefs.Problems[0]);
        Assert.Equal(newer, File.ReadAllText(Path.Combine(EditorFolder, "preferences.v2.xml")));

        prefs.CameraSpeed = 11;
        prefs.Flush();
        Assert.Equal(11, Open().CameraSpeed);
        Assert.Equal(newer, File.ReadAllText(Path.Combine(EditorFolder, "preferences.v2.xml"))); // still there for the newer editor
    }

    [Fact]
    public void AValueThatIsTheWrongTypeFallsBackToItsDefault()
    {
        Directory.CreateDirectory(EditorFolder);
        File.WriteAllText(PreferencesFile, "<settings version=\"1\"><value key=\"editor.camera.speed\">fast</value><value key=\"editor.gizmo.pivot\">sideways</value><value key=\"editor.helpers\">false</value></settings>");

        EditorPreferences prefs = Open();

        Assert.Equal(6, prefs.CameraSpeed);
        var gizmo = new GizmoSettings();
        prefs.ApplyTo(gizmo);
        Assert.Equal(PivotMode.Center, gizmo.Pivot);
        Assert.False(gizmo.ShowHelpers);
    }

    [Fact]
    public void InMemoryPreferencesNeverWrite()
    {
        EditorPreferences prefs = EditorPreferences.InMemory();
        Assert.Null(prefs.FilePath);
        Assert.False(prefs.Persistent);

        prefs.CameraSpeed = 30;
        prefs.AddRecent(Folder("x"));
        prefs.Flush();

        Assert.Equal(30, prefs.CameraSpeed);
        Assert.Single(prefs.Recent);
        Assert.False(Directory.Exists(EditorFolder));
    }

    [Fact]
    public void AMissingEditorFolderIsMade()
    {
        Assert.False(Directory.Exists(EditorFolder));
        EditorPreferences prefs = Open();
        Assert.True(Directory.Exists(EditorFolder));
        Assert.Equal(PreferencesFile, prefs.FilePath);
    }
}
