using System;
using System.Collections.Generic;
using System.IO;
using Apogee.Content;
using Apogee.Core.Logging;
using Apogee.Platform;
using Apogee.Scripting;

namespace Apogee.Editor.Editing;

/// <summary>What a dialog was asked for; the editor's own path entry opens for the same kind when the system has no dialog.</summary>
public enum DialogKind
{
    OpenScene,
    SaveSceneAs,
    OpenProject,
}

/// <summary>
/// What the file dialogs do for the editor: <b>Open scene</b>, <b>Save scene as</b> and <b>Open project</b>. It starts a request, and when the result arrives (on the main thread, later) it checks the
/// result is still wanted, turns the chosen path into the game's virtual path (only paths inside the game folder have one), and does the work or says why not. No ImGui here: the editor gives it
/// the dialogs, the message box and the project switch.
/// </summary>
/// <param name="dialogs">The system's dialogs, or a fake.</param>
/// <param name="current">The open project, or null on the start screen. A result that arrives after this changed (another project was opened) is dropped.</param>
/// <param name="tell">Shows a message the user must read.</param>
/// <param name="openProject">Switches to the game folder the user chose.</param>
/// <param name="unavailable">The system could not show the dialog (no backend): the editor opens its own path entry for this kind instead.</param>
public sealed class DialogFlow(IFileDialogs dialogs, Func<EditorProject?> current, Action<string> tell, Action<string> openProject, Action<DialogKind> unavailable)
{
    /// <summary>The filters of the scene dialogs.</summary>
    public static IReadOnlyList<(string Name, string Pattern)> SceneFilters { get; } = [("Scenes", "ascene"), ("All files", "*")];

    /// <summary>A dialog is open: the menus are greyed, and another request is refused.</summary>
    public bool Busy => dialogs.Busy;

    /// <summary>Asks for a scene file of the game and opens it.</summary>
    public void OpenScene()
    {
        if (current() is not { } project || !Begin())
        {
            return;
        }

        string? where = SceneFolder(project);
        _ = dialogs.OpenFile(SceneFilters, where, result => Finish(project, DialogKind.OpenScene, result, path => OpenScenePath(project, path)));
    }

    /// <summary>Asks where to save the scene and saves it there.</summary>
    public void SaveSceneAs()
    {
        if (current() is not { } project || !Begin())
        {
            return;
        }

        string? where = project.ScenePath is { } scene && project.Session.Files?.TryGetPhysicalPath(scene) is { } file ? file : SceneFolder(project);
        _ = dialogs.SaveFile(SceneFilters, where, result => Finish(project, DialogKind.SaveSceneAs, result, path => SaveScenePath(project, path)));
    }

    /// <summary>Asks for a game folder and hands it to <c>openProject</c>. Works on the start screen too.</summary>
    public void OpenProject()
    {
        if (!Begin())
        {
            return;
        }

        EditorProject? started = current();
        _ = dialogs.OpenFolder(started?.GameFolder, result => Finish(started, DialogKind.OpenProject, result, path => openProject(path)));
    }

    private bool Begin()
    {
        if (dialogs.Busy)
        {
            tell("A file dialog is already open. Finish or close it first.");
            return false;
        }

        return true;
    }

    /// <summary>The default location: the game's <c>scenes</c> folder when it has one, else the game folder.</summary>
    private static string SceneFolder(EditorProject project)
    {
        string scenes = Path.Combine(project.GameFolder, "scenes");
        return Directory.Exists(scenes) ? scenes + Path.DirectorySeparatorChar : project.GameFolder + Path.DirectorySeparatorChar;
    }

    private void Finish(EditorProject? started, DialogKind kind, FileDialogResult result, Action<string> chosen)
    {
        if (!ReferenceEquals(current(), started))
        {
            Log.Info("Editor", $"The {kind} dialog ended after the project changed; its result was dropped.");
            return;
        }

        if (result.Error is { } error)
        {
            Log.Warn("Editor", $"The system's file dialog is not available ({error}); using the built-in one.");
            unavailable(kind);
            return;
        }

        if (result.HasPaths)
        {
            chosen(result.Paths[0]);
        }
    }

    private void OpenScenePath(EditorProject project, string path)
    {
        if (!path.EndsWith(".ascene", StringComparison.OrdinalIgnoreCase))
        {
            tell($"{path} is not a scene: scene files end in .ascene.");
            return;
        }

        if (VirtualPaths.ToVirtual(project.GameFolder, path) is not { } virtualPath)
        {
            tell(OutsideMessage(project, path));
            return;
        }

        try
        {
            project.OpenScene(virtualPath);
        }
        catch (Exception e) when (e is IOException or ContentException or FormatException or ScriptException or UnauthorizedAccessException)
        {
            Log.Error("Editor", $"Could not open {virtualPath}: {e.Message}");
            tell($"The scene could not be opened:\n{e.Message}");
        }
    }

    private void SaveScenePath(EditorProject project, string path)
    {
        if (!path.EndsWith(".ascene", StringComparison.OrdinalIgnoreCase))
        {
            path += ".ascene";
        }

        if (VirtualPaths.ToVirtual(project.GameFolder, path) is not { } virtualPath)
        {
            tell(OutsideMessage(project, path));
            return;
        }

        if (project.Session.Files is { } files && !files.CanWrite(virtualPath))
        {
            tell($"{virtualPath} cannot be written: that part of the game is read-only.");
            return;
        }

        try
        {
            project.SaveSceneAs(virtualPath);
        }
        catch (Exception e) when (e is IOException or ContentException or FormatException or UnauthorizedAccessException)
        {
            Log.Error("Editor", $"Could not save {virtualPath}: {e.Message}");
            tell($"The scene could not be saved:\n{e.Message}");
        }
    }

    private static string OutsideMessage(EditorProject project, string path) =>
        $"{path} is outside the game folder.\nChoose a file inside {project.GameFolder}: only files there can be opened or saved as part of the game.";
}
