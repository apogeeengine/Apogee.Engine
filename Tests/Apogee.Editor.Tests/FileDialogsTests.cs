using System;
using System.Collections.Generic;
using System.IO;
using Apogee.Editor.Editing;
using Apogee.Native.Tests;
using Apogee.Platform;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;
using Lua = Apogee.Native.Luau.Lua;

namespace Apogee.Editor.Tests;

/// <summary>
/// The file dialogs' side of the editor, with the dialogs scripted (the system's need a display and a person): which OS paths become game paths, what Open scene, Save scene as and Open project do with
/// a result, and the switch from one project to another.
/// </summary>
[Collection("editor")]
public sealed class FileDialogsTests
{
    private static string Game(ProjectRig rig) => Path.Combine(rig.Root, "game");

    /// <summary>A dialog flow over a project, with the messages, the folders asked to open and the fallbacks it asked for collected.</summary>
    private static (DialogFlow Flow, ScriptedFileDialogs Dialogs, List<string> Messages, List<string> Opened, List<DialogKind> Fallbacks) Start(Func<EditorProject?> current)
    {
        var dialogs = new ScriptedFileDialogs();
        List<string> messages = [];
        List<string> opened = [];
        List<DialogKind> fallbacks = [];
        return (new DialogFlow(dialogs, current, messages.Add, opened.Add, fallbacks.Add), dialogs, messages, opened, fallbacks);
    }

    [Fact]
    public void OsPathsBecomeVirtualOnlyInsideTheGame()
    {
        string game = Path.Combine(Path.GetTempPath(), "apogee-game");
        Assert.Equal("res://scenes/level.ascene", VirtualPaths.ToVirtual(game, Path.Combine(game, "scenes", "level.ascene")));
        Assert.Equal("res://a.ascene", VirtualPaths.ToVirtual(game + Path.DirectorySeparatorChar, Path.Combine(game, "a.ascene")));
        Assert.Equal("res://scenes/level.ascene", VirtualPaths.ToVirtual(game, Path.Combine(game, "scenes", "other", "..", "level.ascene")));

        Assert.Null(VirtualPaths.ToVirtual(game, game)); // the folder itself
        Assert.Null(VirtualPaths.ToVirtual(game, game + Path.DirectorySeparatorChar));
        Assert.Null(VirtualPaths.ToVirtual(game, Path.Combine(Path.GetTempPath(), "elsewhere", "level.ascene")));
        Assert.Null(VirtualPaths.ToVirtual(game, game + "-two" + Path.DirectorySeparatorChar + "level.ascene")); // a sibling that starts with the same letters
        Assert.Null(VirtualPaths.ToVirtual(game, Path.Combine(game, "..", "level.ascene")));
        Assert.Null(VirtualPaths.ToVirtual(game, ""));
        Assert.Null(VirtualPaths.ToVirtual(game, "  "));
        Assert.Null(VirtualPaths.ToVirtual(game, "bad\0path"));
    }

    [Fact]
    public void ScriptedDialogsRefuseASecondRequestAndAnswerOnce()
    {
        var dialogs = new ScriptedFileDialogs();
        FileDialogResult? first = null;
        Assert.True(dialogs.OpenFile([("Scenes", "ascene")], "/x", r => first = r));
        Assert.True(dialogs.Busy);
        Assert.False(dialogs.SaveFile([], null, _ => { }));
        Assert.False(dialogs.OpenFolder(null, _ => { }));
        Assert.Single(dialogs.History);

        dialogs.Choose("/x/a.ascene");
        Assert.False(dialogs.Busy);
        Assert.Equal(["/x/a.ascene"], first!.Paths);
        Assert.Throws<InvalidOperationException>(() => dialogs.Cancel());
    }

    [Fact]
    public void SaveAsThroughADialogWritesTheChosenFile()
    {
        using var rig = new ProjectRig();
        (DialogFlow flow, ScriptedFileDialogs dialogs, List<string> messages, _, _) = Start(() => rig.Project);
        rig.Project.CreateEmpty();

        flow.SaveSceneAs();
        Assert.True(flow.Busy);
        Assert.Equal(DialogRequestKind.SaveFile, dialogs.Pending!.Kind);
        Assert.Contains(dialogs.Pending.Filters, f => f.Pattern == "ascene");
        string chosen = Path.Combine(Game(rig), "scenes", "level"); // no extension typed: the editor adds it
        dialogs.Choose(chosen);

        Assert.Empty(messages);
        Assert.Equal("res://scenes/level.ascene", rig.Project.ScenePath);
        Assert.True(File.Exists(Path.Combine(Game(rig), "scenes", "level.ascene")));
        Assert.False(rig.Project.Dirty);
    }

    [Fact]
    public void SaveAsOutsideTheGameFolderIsRefusedWithTheReason()
    {
        using var rig = new ProjectRig();
        (DialogFlow flow, ScriptedFileDialogs dialogs, List<string> messages, _, _) = Start(() => rig.Project);
        string outside = Path.Combine(rig.Root, "elsewhere", "level.ascene");

        flow.SaveSceneAs();
        dialogs.Choose(outside);

        Assert.Single(messages);
        Assert.Contains("outside the game folder", messages[0]);
        Assert.Contains(Game(rig), messages[0]);
        Assert.Null(rig.Project.ScenePath);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void OpenSceneThroughADialogOpensTheChosenFileAndRefusesOtherPaths()
    {
        using var rig = new ProjectRig();
        (DialogFlow flow, ScriptedFileDialogs dialogs, List<string> messages, _, _) = Start(() => rig.Project);
        rig.Project.CreateEmpty();
        rig.Project.SaveSceneAs("res://scenes/level");
        rig.Project.NewScene();
        Assert.Empty(rig.World.Roots());

        flow.OpenScene();
        Assert.Equal(DialogRequestKind.OpenFile, dialogs.Pending!.Kind);
        Assert.EndsWith("scenes" + Path.DirectorySeparatorChar, dialogs.Pending.DefaultLocation); // the game's scenes folder exists
        dialogs.Choose(Path.Combine(Game(rig), "scenes", "level.ascene"));
        Assert.Empty(messages);
        Assert.Equal("res://scenes/level.ascene", rig.Project.ScenePath);
        Assert.Single(rig.World.Roots());

        flow.OpenScene();
        dialogs.Choose(Path.Combine(rig.Root, "elsewhere", "level.ascene"));
        Assert.Contains("outside the game folder", Assert.Single(messages));

        messages.Clear();
        flow.OpenScene();
        dialogs.Choose(Path.Combine(Game(rig), "packages", "scene", "main.luau"));
        Assert.Contains("not a scene", Assert.Single(messages));

        messages.Clear();
        flow.OpenScene();
        dialogs.Choose(Path.Combine(Game(rig), "scenes", "missing.ascene"));
        Assert.Contains("could not be opened", Assert.Single(messages));
        Assert.Equal("res://scenes/level.ascene", rig.Project.ScenePath);
    }

    [Fact]
    public void ACancelledDialogDoesNothingAndAnErrorFallsBackToTheBuiltInEntry()
    {
        using var rig = new ProjectRig();
        (DialogFlow flow, ScriptedFileDialogs dialogs, List<string> messages, List<string> opened, List<DialogKind> fallbacks) = Start(() => rig.Project);

        flow.OpenScene();
        dialogs.Cancel();
        flow.SaveSceneAs();
        dialogs.Cancel();
        flow.OpenProject();
        dialogs.Cancel();
        Assert.Empty(messages);
        Assert.Empty(opened);
        Assert.Empty(fallbacks);

        flow.OpenScene();
        dialogs.Fail("Could not run zenity: exit code 127");
        flow.SaveSceneAs();
        dialogs.Fail("no backend");
        flow.OpenProject();
        dialogs.Fail("no backend");
        Assert.Equal([DialogKind.OpenScene, DialogKind.SaveSceneAs, DialogKind.OpenProject], fallbacks);
        Assert.Empty(messages);
        Assert.False(flow.Busy);
    }

    [Fact]
    public void ADialogWhileOneIsOpenIsRefusedWithAMessage()
    {
        using var rig = new ProjectRig();
        (DialogFlow flow, ScriptedFileDialogs dialogs, List<string> messages, _, _) = Start(() => rig.Project);

        flow.OpenScene();
        flow.SaveSceneAs();
        flow.OpenProject();

        Assert.Single(dialogs.History);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Contains("already open", m));
    }

    [Fact]
    public void AResultThatArrivesAfterTheProjectChangedIsDropped()
    {
        using var rig = new ProjectRig();
        EditorProject? current = rig.Project;
        (DialogFlow flow, ScriptedFileDialogs dialogs, List<string> messages, _, List<DialogKind> fallbacks) = Start(() => current);
        rig.Project.CreateEmpty();

        flow.SaveSceneAs();
        current = null; // the project was closed (or another opened) while the dialog was up
        dialogs.Choose(Path.Combine(Game(rig), "scenes", "late.ascene"));

        Assert.Empty(messages);
        Assert.False(File.Exists(Path.Combine(Game(rig), "scenes", "late.ascene")));
        Assert.Null(rig.Project.ScenePath);

        current = rig.Project;
        flow.OpenScene();
        current = null;
        dialogs.Fail("late failure");
        Assert.Empty(fallbacks);
    }

    [Fact]
    public void OpenProjectHandsTheChosenFolderOnAndWorksWithNoProject()
    {
        (DialogFlow flow, ScriptedFileDialogs dialogs, List<string> messages, List<string> opened, _) = Start(() => null);

        flow.OpenProject();
        Assert.Equal(DialogRequestKind.OpenFolder, dialogs.Pending!.Kind);
        dialogs.Choose("/some/game");

        Assert.Equal(["/some/game"], opened);
        Assert.Empty(messages);

        flow.OpenScene(); // a scene needs a project: nothing opens
        flow.SaveSceneAs();
        Assert.False(dialogs.Busy);
    }

    /// <summary>Makes the folders of games (a package each) and hands them to a host; no project is open until the host opens one.</summary>
    private sealed class Games : IDisposable
    {
        public Games()
        {
            NativeRequirements.Require(typeof(Lua).Assembly, "luau");
            NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
            Root = Path.Combine(Path.GetTempPath(), "apogee-games-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        }

        public string Root { get; }

        public string Make(string name, string? manifest = null)
        {
            string folder = Path.Combine(Root, name);
            Directory.CreateDirectory(Path.Combine(folder, "packages", "scene"));
            if (manifest is not null)
            {
                File.WriteAllText(Path.Combine(folder, "game.xml"), manifest);
            }

            File.WriteAllText(Path.Combine(folder, "packages", "scene", "mod.xml"), "<Mod id=\"scene\" version=\"1.0.0\"><Script path=\"main.luau\"/></Mod>");
            File.WriteAllText(Path.Combine(folder, "packages", "scene", "main.luau"), "print('scene ran')");
            return folder;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary folder is not a failure.
            }
        }
    }

    [Fact]
    public void SwitchingProjectsDisposesTheOldSession()
    {
        using var games = new Games();
        string one = games.Make("one");
        string two = games.Make("two");
        using var host = new ProjectHost(Path.Combine(games.Root, "user"));
        var events = new List<string>();
        host.Closing += p => events.Add("closing " + Path.GetFileName(p.GameFolder));
        host.Opened += p => events.Add("opened " + Path.GetFileName(p.GameFolder));

        Assert.True(host.TryOpen(one, out string? error));
        Assert.Null(error);
        EditorProject first = host.Current!;
        first.CreateEmpty();

        Assert.True(host.TryOpen(two, out error));
        Assert.Null(error);
        Assert.NotSame(first, host.Current);
        Assert.Equal(Path.GetFullPath(two), host.Current!.GameFolder);
        Assert.Empty(host.Current.World.Roots()); // a fresh world, not the first project's entity
        Assert.Equal(["opened one", "closing one", "opened two"], events);

        Assert.True(first.IsDisposed); // its session was disposed (before the new one started: there is one at a time)
        Assert.False(host.Current.IsDisposed);

        host.Close();
        Assert.Null(host.Current);
        Assert.Equal(["opened one", "closing one", "opened two", "closing two"], events);
    }

    [Fact]
    public void AFolderThatCannotBeOpenedKeepsTheOpenProject()
    {
        using var games = new Games();
        string one = games.Make("one");
        using var host = new ProjectHost(Path.Combine(games.Root, "user"));
        Assert.True(host.TryOpen(one, out _));
        EditorProject first = host.Current!;
        int closings = 0;
        host.Closing += _ => closings++;

        Assert.False(host.TryOpen(Path.Combine(games.Root, "missing"), out string? error));

        Assert.Contains("is not a folder", error);
        Assert.Same(first, host.Current); // a typo does not close the project
        Assert.Equal(0, closings);
    }

    [Fact]
    public void AGameThatFailsToStartIsReportedAndThePreviousProjectComesBack()
    {
        using var games = new Games();
        string good = games.Make("good");
        string bad = games.Make("bad", "<Game name=");
        using var host = new ProjectHost(Path.Combine(games.Root, "user"));
        Assert.True(host.TryOpen(good, out _));
        EditorProject first = host.Current!;

        bool opened = host.TryOpen(bad, out string? error);

        Assert.False(opened);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.NotNull(host.Current); // the project that was open is open again
        Assert.NotSame(first, host.Current);
        Assert.Equal(Path.GetFullPath(good), host.Current.GameFolder);

        Assert.False(host.TryOpen(bad, out _));
        host.Close();
        Assert.False(host.TryOpen(bad, out _));
        Assert.Null(host.Current); // nothing to go back to
    }
}
