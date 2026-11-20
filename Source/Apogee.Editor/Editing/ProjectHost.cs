using System;
using System.IO;
using Apogee.Content;
using Apogee.Core.Logging;
using Apogee.Scripting;

namespace Apogee.Editor.Editing;

/// <summary>
/// Which project the editor has open, and the switch from one to another. There is at most one (a session owns process-wide state such as the UI library's, so two cannot coexist): opening another
/// closes the first, which is disposed before the new one starts. With none open the editor shows its start screen.
/// </summary>
public sealed class ProjectHost : IDisposable
{
    private readonly string? _userFolder;

    public ProjectHost(string? userFolder = null) => _userFolder = userFolder;

    /// <summary>The open project, or null on the start screen.</summary>
    public EditorProject? Current { get; private set; }

    /// <summary>Raised with the old project before it is disposed, while its session still exists: whatever was made from it (renderers, the models on the GPU) lets go first.</summary>
    public event Action<EditorProject>? Closing;

    /// <summary>Raised with the new project once it is open (also when a project that failed to open is replaced by the one that was open before).</summary>
    public event Action<EditorProject>? Opened;

    /// <summary>
    /// Closes the open project (its session is disposed) and opens the game folder <paramref name="folder"/>. When the folder cannot be opened (no such folder, a game that fails to start) the previous
    /// project is opened again, as it was saved, and the reason is in <paramref name="error"/>; with no previous project (or one that does not reopen either) the host ends with none.
    /// Unsaved changes are the caller's to ask about before: they are lost here.
    /// </summary>
    public bool TryOpen(string folder, out string? error)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
        {
            error = $"{folder} is not a folder.";
            return false; // Nothing was closed: a typo does not cost the open project.
        }

        string? previous = Current?.GameFolder;
        Close();
        try
        {
            Attach(Create(folder));
            error = null;
            return true;
        }
        catch (Exception e) when (IsOpenFailure(e))
        {
            error = e.Message;
            Log.Error("Editor", $"Could not open {folder}: {e.Message}");
        }

        if (previous is not null)
        {
            try
            {
                Attach(Create(previous));
            }
            catch (Exception e) when (IsOpenFailure(e))
            {
                Log.Error("Editor", $"Could not reopen {previous}: {e.Message}");
            }
        }

        return false;
    }

    /// <summary>Closes the open project, if any.</summary>
    public void Close()
    {
        if (Current is not { } project)
        {
            return;
        }

        Closing?.Invoke(project);
        Current = null;
        project.Dispose();
    }

    public void Dispose() => Close();

    private EditorProject Create(string folder) => new(folder, _userFolder);

    private void Attach(EditorProject project)
    {
        Current = project;
        Opened?.Invoke(project);
    }

    private static bool IsOpenFailure(Exception e) => e is ScriptException or ContentException or IOException or UnauthorizedAccessException or FormatException;
}
