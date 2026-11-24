using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Weft.Reflection;
using Weft.Editor.Editing;
using Weft.Native.Tests;
using Weft.Platform;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;
using Lua = Weft.Native.Luau.Lua;

namespace Weft.Editor.Tests;

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
        string game = Path.Combine(Path.GetTempPath(), "weft-game");
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
        // The message names the folder with forward slashes on every OS; Windows and macOS file systems ignore case, so the comparison does too (and prints both strings: the test runner shortens them).
        string folder = Game(rig).Replace('\\', '/');
        string text = messages[0].Replace('\\', '/'); // the message mixes the separators of the chosen path and of the folder
        Assert.True(text.Contains(folder, StringComparison.OrdinalIgnoreCase), $"expected '{folder}' in '{text}'");
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
        using var games = new Games();
        string game = games.Make("game");
        (DialogFlow flow, ScriptedFileDialogs dialogs, List<string> messages, List<string> opened, _) = Start(() => null);

        flow.OpenProject();
        Assert.Equal(DialogRequestKind.OpenFolder, dialogs.Pending!.Kind);
        dialogs.Choose(game);

        Assert.Equal([game], opened);
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
            Root = Path.Combine(Path.GetTempPath(), "weft-games-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
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
        using var host = new ProjectHost();
        var events = new List<string>();
        host.Closing += p => events.Add("closing " + Path.GetFileName(p.GameFolder));
        host.Opened += p => events.Add("opened " + Path.GetFileName(p.GameFolder));

        Assert.True(host.TryOpen(one, Path.Combine(games.Root, "user"), out string? error));
        Assert.Null(error);
        EditorProject first = host.Current!;
        first.CreateEmpty();

        Assert.True(host.TryOpen(two, Path.Combine(games.Root, "user-two"), out error));
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
        using var host = new ProjectHost();
        Assert.True(host.TryOpen(one, Path.Combine(games.Root, "user"), out _));
        EditorProject first = host.Current!;
        int closings = 0;
        host.Closing += _ => closings++;

        Assert.False(host.TryOpen(Path.Combine(games.Root, "missing"), Path.Combine(games.Root, "user"), out string? error));

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
        using var host = new ProjectHost();
        Assert.True(host.TryOpen(good, Path.Combine(games.Root, "user"), out _));
        EditorProject first = host.Current!;

        bool opened = host.TryOpen(bad, Path.Combine(games.Root, "user"), out string? error);

        Assert.False(opened);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.NotNull(host.Current); // the project that was open is open again
        Assert.NotSame(first, host.Current);
        Assert.Equal(Path.GetFullPath(good), host.Current.GameFolder);

        Assert.False(host.TryOpen(bad, Path.Combine(games.Root, "user"), out _));
        host.Close();
        Assert.False(host.TryOpen(bad, Path.Combine(games.Root, "user"), out _));
        Assert.Null(host.Current); // nothing to go back to
    }

    private static string SceneFile(ProjectRig rig, string name) => Path.Combine(Game(rig), "scenes", name + ".ascene");

    [Fact]
    public void ASceneThatFailsToLoadLeavesTheEditorExactlyAsItWas()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        Entity a = project.CreateEmpty();
        Entity b = project.CreateEmpty(a);
        project.SaveSceneAs("res://scenes/level");
        Entity c = project.CreateEmpty(); // an edit that is not saved
        project.Selected.Set(b);
        project.History.Seal();
        Entity[] roots = [.. rig.World.Roots()];
        bool dirty = project.Dirty;
        string? undoName = project.History.UndoName;
        Assert.True(dirty);

        string[] bad =
        [
            "<Scene version=\"1\"",                                                 // does not parse
            "<Scene version=\"3\"><Entity name=\"newer\"/></Scene>",                // from a newer engine: refused after the world was emptied
            "<Level/>",                                                             // not a scene
            "",
        ];
        foreach (string text in bad)
        {
            File.WriteAllText(SceneFile(rig, "broken"), text);

            Assert.False(project.TryOpenScene("res://scenes/broken.ascene", out string? error));
            Assert.Contains("could not be opened", error);
            Assert.Throws<FormatException>(() => project.OpenScene("res://scenes/broken.ascene"));

            Assert.True(roots.AsSpan().SequenceEqual(rig.World.Roots().ToArray()));
            Assert.Equal("Empty", rig.World.GetName(a));
            Assert.Equal("Empty", rig.World.GetName(b));
            Assert.Equal("Empty 2", rig.World.GetName(c));
            Assert.Equal(a, rig.World.GetParent(b));
            Assert.Equal("res://scenes/level.ascene", project.ScenePath);
            Assert.Equal(dirty, project.Dirty);
            Assert.Equal(b, project.Selection);
            Assert.Equal(undoName, project.History.UndoName);
            Assert.True(project.History.CanUndo);
        }

        // The next Save writes the scene that is open, not an empty one over it.
        Assert.True(project.TrySaveScene(out string? saveError), saveError);
        string saved = File.ReadAllText(SceneFile(rig, "level"));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(saved, "<Entity "));
        Assert.Contains("Empty 2", saved, StringComparison.Ordinal);
        Assert.False(project.Dirty);

        // And the history still works on the same entities.
        project.History.Undo();
        Assert.False(rig.World.IsAlive(c));
    }

    [Fact]
    public void ASaveThatFailsIsReportedKeepsTheSceneModifiedAndSaysNoToTheCaller()
    {
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        project.CreateEmpty();
        project.SaveSceneAs("res://scenes/level");
        string file = SceneFile(rig, "level");
        project.CreateEmpty();
        Assert.True(project.Dirty);

        // A file and a folder the user cannot write. (A process that can anyway, such as root, cannot run this test.)
        File.SetAttributes(file, FileAttributes.ReadOnly);
        string folder = Path.GetDirectoryName(file)!;
        UnixFileMode folderMode = OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(folder);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        try
        {
            if (CanWrite(file))
            {
                Assert.Skip("This process can write read-only files.");
            }

            Assert.False(project.TrySaveScene(out string? error));
            Assert.NotNull(error); // a failure, not "no file yet"
            Assert.Contains("could not be saved", error);
            Assert.True(project.Dirty);
            Assert.Equal("res://scenes/level.ascene", project.ScenePath);

            // Through the dialog flow: a message, nothing else.
            (DialogFlow flow, ScriptedFileDialogs dialogs, List<string> messages, _, _) = Start(() => project);
            flow.SaveSceneAs();
            dialogs.Choose(file);
            Assert.Contains("could not be saved", Assert.Single(messages));
            Assert.True(project.Dirty);
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(folder, folderMode);
            }

            File.SetAttributes(file, FileAttributes.Normal);
        }

        Assert.True(project.TrySaveScene(out string? ok), ok); // writable again: it saves
        Assert.False(project.Dirty);
    }

    private static bool CanWrite(string file)
    {
        try
        {
            using FileStream stream = File.Open(file, FileMode.Open, FileAccess.Write);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void ANameTheGameCannotUseIsNotCalledReadOnly()
    {
        // A colon is a drive or a stream on Windows: the path is then outside the game folder for another reason, so the name has to be one that only the virtual file system refuses.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a colon in a name means a drive or a stream on Windows");
        using var rig = new ProjectRig();
        (DialogFlow flow, ScriptedFileDialogs dialogs, List<string> messages, _, _) = Start(() => rig.Project);

        flow.SaveSceneAs();
        dialogs.Choose(Path.Combine(Game(rig), "scenes", "a:b"));

        string message = Assert.Single(messages);
        Assert.Contains("cannot use the name", message);
        Assert.DoesNotContain("read-only", message);
        Assert.Null(rig.Project.ScenePath);
    }

    [Fact]
    public void ADialogResultWhilePlayingEndsTheRunAndIsApplied()
    {
        // A result is not dropped or deferred because the game is running: Open scene and Save as stop the run first, as their menu entries always did (Stop puts the edited world back).
        using var rig = new ProjectRig();
        EditorProject project = rig.Project;
        project.CreateEmpty();
        project.SaveSceneAs("res://scenes/level");
        project.CreateEmpty();
        (DialogFlow flow, ScriptedFileDialogs dialogs, List<string> messages, _, _) = Start(() => project);

        Assert.True(project.Play(), project.PlayError);
        flow.SaveSceneAs();
        dialogs.Choose(SceneFile(rig, "copy"));
        Assert.False(project.IsPlaying);
        Assert.Empty(messages);
        Assert.Equal("res://scenes/copy.ascene", project.ScenePath);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(File.ReadAllText(SceneFile(rig, "copy")), "<Entity ")); // the edited world, not the run's

        Assert.True(project.Play(), project.PlayError);
        flow.OpenScene();
        dialogs.Choose(SceneFile(rig, "level"));
        Assert.False(project.IsPlaying);
        Assert.Equal("res://scenes/level.ascene", project.ScenePath);
        Assert.Single(rig.World.Roots());
    }

    [Fact]
    public void AnOpenDialogMakesAnotherRequestUseTheBuiltInEntry()
    {
        using var rig = new ProjectRig();
        (DialogFlow flow, ScriptedFileDialogs dialogs, List<string> messages, _, List<DialogKind> fallbacks) = Start(() => rig.Project);

        flow.OpenScene();
        flow.SaveSceneAs();
        flow.OpenProject();

        Assert.Single(dialogs.History);
        Assert.Equal([DialogKind.SaveSceneAs, DialogKind.OpenProject], fallbacks);
        Assert.Empty(messages);
    }

    [Fact]
    public void OnlyAGameFolderIsOpenedAsAProject()
    {
        using var games = new Games();
        string game = games.Make("game");
        string elsewhere = Path.Combine(games.Root, "documents");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(Path.Combine(games.Root, "withmanifest"));
        File.WriteAllText(Path.Combine(games.Root, "withmanifest", "game.xml"), "<Game/>");

        Assert.Null(ProjectHost.CheckGameFolder(game));
        Assert.Null(ProjectHost.CheckGameFolder(Path.Combine(games.Root, "withmanifest")));
        Assert.Contains("does not look like a game folder", ProjectHost.CheckGameFolder(elsewhere));
        Assert.Contains("is not a folder", ProjectHost.CheckGameFolder(Path.Combine(games.Root, "missing")));

        List<string> messages = [];
        List<string> opened = [];
        var flow = new DialogFlow(new ScriptedFileDialogs(), () => null, messages.Add, opened.Add, _ => { });
        flow.ChooseProject(elsewhere);
        Assert.Contains("does not look like a game folder", Assert.Single(messages));
        Assert.Empty(opened);
        flow.ChooseProject(game);
        Assert.Equal([game], opened);
    }

    [Fact]
    public void ChoosingTheOpenProjectAgainDoesNothing()
    {
        using var rig = new ProjectRig();
        List<string> opened = [];
        var flow = new DialogFlow(new ScriptedFileDialogs(), () => rig.Project, _ => { }, opened.Add, _ => { });

        flow.ChooseProject(Game(rig));
        flow.ChooseProject(Game(rig) + Path.DirectorySeparatorChar);
        Assert.Empty(opened);
    }

    [Fact]
    public void TheUserFolderOfTheCommandLineBelongsToThatGameOnly()
    {
        using var games = new Games();
        string one = games.Make("one");
        string two = games.Make("two");
        string userOne = Path.Combine(games.Root, "user-one");
        string defaultTwo = Weft.Host.ScriptSession.UserDirectoryFor(two, null, null);
        using var host = new ProjectHost();
        try
        {
            Assert.True(host.TryOpen(one, userOne, out _));
            Assert.Equal(userOne, host.Current!.UserFolder);

            Assert.True(host.TryOpen(two, null, out _)); // chosen later: its own user folder, not the command line's
            Assert.Null(host.Current!.UserFolder);
            Assert.NotEqual(userOne, Weft.Host.ScriptSession.UserDirectoryFor(host.Current.GameFolder, host.Current.UserFolder, null));

            // A switch that fails brings back the first game with the user folder it had.
            string bad = games.Make("bad", "<Game name=");
            Assert.True(host.TryOpen(one, userOne, out _));
            Assert.False(host.TryOpen(bad, null, out _));
            Assert.Equal(userOne, host.Current!.UserFolder);
        }
        finally
        {
            host.Close();
            try
            {
                Directory.Delete(defaultTwo, recursive: true); // the default user folder that opening game two made
            }
            catch (IOException)
            {
                // Not there, or not ours to remove.
            }
        }
    }

    [Fact]
    public void ARootLevelAndASymlinkedGameFolderStillMapToTheGame()
    {
        using var games = new Games();
        string real = games.Make("real");
        Directory.CreateDirectory(Path.Combine(real, "scenes"));
        string file = Path.Combine(real, "scenes", "a.ascene");
        File.WriteAllText(file, "x");

        // A game folder at the root of a drive.
        string driveRoot = Path.GetPathRoot(real)!;
        Assert.Equal("res://" + Path.GetRelativePath(driveRoot, file).Replace('\\', '/'), VirtualPaths.ToVirtual(driveRoot, file));
        Assert.Null(VirtualPaths.ToVirtual(driveRoot, driveRoot));

        // A game folder opened through a link, with a dialog that hands back the resolved path (and the other way round).
        string link = Path.Combine(games.Root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This system cannot make symbolic links here: " + e.Message);
        }

        Assert.Equal("res://scenes/a.ascene", VirtualPaths.ToVirtual(link, file));
        Assert.Equal("res://scenes/a.ascene", VirtualPaths.ToVirtual(real, Path.Combine(link, "scenes", "a.ascene")));
        Assert.Equal("res://scenes/new.ascene", VirtualPaths.ToVirtual(link, Path.Combine(real, "scenes", "new.ascene"))); // a file that does not exist yet
        Assert.Null(VirtualPaths.ToVirtual(link, Path.Combine(games.Root, "one", "a.ascene")));
    }

    [Fact]
    public void ABackslashInANameIsNotASeparatorOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("A backslash is a separator on Windows.");
        }

        string game = Path.Combine(Path.GetTempPath(), "weft-game");
        Assert.Null(VirtualPaths.ToVirtual(game, Path.Combine(game, "scenes\\level.ascene")));
    }

    [Fact]
    public void APathAnotherPartOfTheGameAnswersForIsNotTheChosenFile()
    {
        using var rig = new ProjectRig();
        rig.Project.CreateEmpty();
        rig.Project.SaveSceneAs("res://scenes/level");
        var files = rig.Project.Session.Files!;
        string file = SceneFile(rig, "level");

        Assert.True(VirtualPaths.ResolvesTo(files, "res://scenes/level.ascene", file));
        Assert.True(VirtualPaths.ResolvesTo(files, "res://scenes/none.ascene", SceneFile(rig, "none"))); // nothing there yet: a new file
        Assert.False(VirtualPaths.ResolvesTo(files, "res://scenes/level.ascene", SceneFile(rig, "other"))); // that path answers with a different file
    }

    // ---- The gate: one dialog at a time, and a way out of a dialog that never answers ----

    private sealed class FakeClock : TimeProvider
    {
        public long Ticks { get; set; }

        public override long GetTimestamp() => Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [Fact]
    public void TheGateRefusesASecondDialogUntilTheFirstAnswers()
    {
        var gate = new DialogGate(TimeSpan.FromMinutes(5));
        Action<FileDialogResult>? answer = null;
        FileDialogResult? got = null;

        Assert.True(gate.Start(r => got = r, finished => answer = finished));
        Assert.True(gate.Busy);
        Assert.False(gate.Start(_ => { }, _ => Assert.Fail("a second dialog must not be shown")));

        answer!(FileDialogResult.Cancel);
        Assert.False(gate.Busy);
        Assert.True(got!.Cancelled);
        Assert.True(gate.Start(_ => { }, _ => { }));
    }

    [Fact]
    public void TheGateIsFreeAgainAfterTheCallThrows()
    {
        var gate = new DialogGate(TimeSpan.FromMinutes(5));

        Assert.Throws<InvalidOperationException>(() => gate.Start(_ => { }, _ => throw new InvalidOperationException("boom")));
        Assert.False(gate.Busy);

        // A call that SDL cannot make is an answer (the caller falls back), delivered at once.
        FileDialogResult? got = null;
        Assert.True(gate.Start(r => got = r, _ => throw new PlatformException("no dialogs here")));
        Assert.Equal("no dialogs here", got!.Error);
        Assert.False(gate.Busy);
        got = null;
        Assert.True(gate.Start(r => got = r, _ => throw new DllNotFoundException("libSDL3")));
        Assert.NotNull(got!.Error);
        Assert.False(gate.Busy);
    }

    [Fact]
    public void ADialogThatNeverAnswersIsLostAndItsLateAnswerIsDropped()
    {
        var clock = new FakeClock();
        var gate = new DialogGate(TimeSpan.FromMinutes(5), clock);
        Action<FileDialogResult>? first = null;
        FileDialogResult? firstGot = null;
        Assert.True(gate.Start(r => firstGot = r, finished => first = finished));

        clock.Ticks = TimeSpan.FromMinutes(4).Ticks;
        Assert.True(gate.Busy); // still within the time a person may take
        clock.Ticks = TimeSpan.FromMinutes(6).Ticks;
        Assert.False(gate.Busy); // lost

        FileDialogResult? secondGot = null;
        Assert.True(gate.Start(r => secondGot = r, _ => { }));
        first!(new FileDialogResult(["/late"], false, null)); // the lost dialog answers after all
        Assert.Null(firstGot);
        Assert.Null(secondGot);
        Assert.True(gate.Busy); // and did not free the gate for the second one
    }
}
