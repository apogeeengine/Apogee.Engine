using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Core.Logging;
using Apogee.Core.Numerics;
using Apogee.Platform;
using Apogee.Host;

namespace Apogee.Editor.Assets;

/// <summary>Where in the editor's window a dropped file landed.</summary>
public enum DropArea
{
    /// <summary>Over no panel that takes files (the hierarchy, the inspector, the console).</summary>
    None,

    /// <summary>Over the asset browser: the files are added to the folder it shows.</summary>
    AssetBrowser,

    /// <summary>Over the viewport: models are added and placed where they were dropped.</summary>
    Viewport,
}

/// <summary>What <see cref="DropFlow"/> needs to know about the editor's screen; the panels answer in the app, a fake in tests.</summary>
public interface IDropSurface
{
    /// <summary>Which panel is at the window point (in window points, the unit of <see cref="FileDrop.X"/>).</summary>
    DropArea AreaAt(float x, float y);

    /// <summary>The folder the asset browser shows (a virtual path, <c>res://</c> at the top).</summary>
    string AssetFolder { get; }

    /// <summary>A modal dialog is open: a drop is refused, the user is busy with something else.</summary>
    bool ModalOpen { get; }

    /// <summary>The point of the ground (or in front of the camera) that the window point looks at in the viewport, or null when the viewport has no image to look through.</summary>
    Double3? PlacementAt(float x, float y);
}

/// <summary>A drop that waits for the user to say what to do with the names the project already has.</summary>
/// <param name="Clashes">The files of the project that the drop would meet (virtual paths).</param>
public sealed record PendingDrop(IReadOnlyList<string> Clashes);

/// <summary>
/// What the editor does with files dragged in from the operating system: decides whether the drop is accepted, where it goes, asks about name clashes, starts the <see cref="ImportJob"/> and, when it has
/// ended, reports and (for models dropped on the viewport) makes the entities. No ImGui: the screen is behind <see cref="IDropSurface"/>, the message box is <c>tell</c>.
/// <para>
/// A drop is <b>refused with a message</b> when no project is open, the game is playing, a dialog is open, an import is running or a question is waiting, or it landed on a panel that takes no files. Everything is
/// called from the main thread; the worker never touches this class.
/// </para>
/// </summary>
/// <param name="current">The open project, or null.</param>
/// <param name="surface">The screen.</param>
/// <param name="windowId">The editor's window: a drop on another window is not ours.</param>
/// <param name="tell">Shows a message the user must read.</param>
public sealed class DropFlow(Func<EditorProject?> current, IDropSurface surface, uint windowId, Action<string> tell)
{
    /// <summary>The folder a model dropped on the viewport goes to when the asset browser is not in a folder of the game.</summary>
    public const string ModelFolder = "res://models";

    // What the running job was started for.
    private sealed record Running(EditorProject Project, DropArea Area, Double3? At);

    private readonly List<string> _paths = [];
    private EditorProject? _askedFor;
    private string _folder = "";
    private DropArea _area;
    private Double3? _at;
    private Running? _running;

    /// <summary>The question about name clashes the user has to answer (<see cref="Resolve"/>), or null.</summary>
    public PendingDrop? Pending { get; private set; }

    /// <summary>A line for the menu bar while a job runs (<c>Importing 2/5: res://models/car.glb</c>), or null.</summary>
    public string? Status => _running is not null && current() is { Job: { IsRunning: true } job } ? $"Importing {Math.Min(job.Done + 1, job.Total)}/{job.Total}: {job.Current}" : null;

    /// <summary>How far the running job is, 0 to 1, or null when none runs.</summary>
    public float? Progress => _running is not null && current() is { Job: { IsRunning: true } job } && job.Total > 0 ? (float)job.Done / job.Total : null;

    /// <summary>Stops the running job at its next file.</summary>
    public void Cancel() => current()?.Job?.Cancel();

    /// <summary>A drop from the platform. Anything not ours or not wanted is refused or ignored here, with the reason said.</summary>
    public void Handle(FileDrop drop)
    {
        ArgumentNullException.ThrowIfNull(drop);
        if (drop.WindowId != windowId)
        {
            return;
        }

        if (current() is not { } project)
        {
            tell("Open a project first: files are added to the project that is open.");
            return;
        }

        if (Refusal(project) is { } refusal)
        {
            tell(refusal);
            return;
        }

        // No position: the system did not say where. The asset browser is the safe answer; a point is never guessed.
        DropArea area = drop.HasPosition ? surface.AreaAt(drop.X, drop.Y) : DropArea.AssetBrowser;
        if (area == DropArea.None)
        {
            tell("Drop the files on the Assets panel (to add them to its folder) or on the Viewport (to add models to the scene).");
            return;
        }

        _paths.Clear();
        _paths.AddRange(drop.Paths);
        _askedFor = project;
        _area = area;
        _folder = area == DropArea.Viewport ? ViewportFolder() : surface.AssetFolder;
        _at = area == DropArea.Viewport && drop.HasPosition ? surface.PlacementAt(drop.X, drop.Y) : null;

        DropPlan plan = MakePlan(project, ClashPolicy.KeepBoth);
        if (plan.HasClashes && plan.Files.Count + plan.Skipped.Count > 0)
        {
            Pending = new PendingDrop(plan.Clashes);
            return;
        }

        Start(project, plan);
    }

    /// <summary>The user answered the question about clashes: <c>null</c> cancels the drop.</summary>
    public void Resolve(ClashPolicy? choice)
    {
        EditorProject? project = _askedFor;
        Pending = null;
        _askedFor = null;
        if (choice is not { } policy || project is null)
        {
            _paths.Clear();
            return;
        }

        if (!ReferenceEquals(project, current()) || project.IsDisposed)
        {
            Log.Info("Import", "The project changed while the drop waited: it was dropped.");
            _paths.Clear();
            return;
        }

        if (Refusal(project) is { } refusal)
        {
            tell(refusal);
            _paths.Clear();
            return;
        }

        Start(project, MakePlan(project, policy));
    }

    /// <summary>Called every frame: reports a job that has ended and places the models of a viewport drop. A job of a project that was closed meanwhile is forgotten.</summary>
    public void Update()
    {
        if (_running is not { } running)
        {
            return;
        }

        EditorProject? project = current();
        if (!ReferenceEquals(project, running.Project) || project.IsDisposed)
        {
            _running = null; // the project went away (its job was cancelled and waited for as it closed)
            return;
        }

        if (project.Job is not { IsEnded: true } job)
        {
            return;
        }

        _running = null;
        ImportJobReport report = job.Report!;
        PlannedFile[] imported = [.. job.Plan.Importable.Where(f => report.Imported.Contains(f.Target))];
        project.ReleaseJob();
        Summarize(report, job.Plan);

        if (running.Area == DropArea.Viewport && !project.IsPlaying)
        {
            Double3 at = running.At ?? default;
            var models = imported
                .Where(f => IsModel(f.Target))
                .Select((f, i) => (f.Target, At: at + new Double3(2.0 * i, 0, 0))) // several models side by side, not all in one spot
                .ToList();
            if (models.Count > 0)
            {
                _ = project.CreateModels(models);
            }
        }
    }

    private string? Refusal(EditorProject project)
    {
        if (project.IsPlaying)
        {
            return "Files cannot be added while the game is playing. Stop it first.";
        }

        if (surface.ModalOpen)
        {
            return "Close the open dialog first, then drop the files again.";
        }

        if (project.ImportGate.IsHeld || project.Job is not null || _running is not null)
        {
            return "Files are being imported: drop the next ones when it has ended.";
        }

        if (Pending is not null)
        {
            return "A question about the files you dropped is waiting: answer it first.";
        }

        return null;
    }

    private string ViewportFolder() => surface.AssetFolder.StartsWith("res://", StringComparison.Ordinal) && surface.AssetFolder != "res://" ? surface.AssetFolder.TrimEnd('/') : ModelFolder;

    private DropPlan MakePlan(EditorProject project, ClashPolicy policy)
    {
        IReadOnlyCollection<string> extensions = [.. ScriptSession.Importers.SelectMany(i => i.Extensions)];
        var targets = new ProjectDropTargets(project.Session.Files!, project.GameFolder);
        return DropPlan.Make(_paths, _folder, targets, extensions, policy);
    }

    private void Start(EditorProject project, DropPlan plan)
    {
        _paths.Clear();
        foreach (RefusedFile refused in plan.Refused.Take(20))
        {
            Log.Warn("Import", $"{refused.Path}: not added: {refused.Reason}");
        }

        if (plan.Refused.Count > 20)
        {
            Log.Warn("Import", $"... and {plan.Refused.Count - 20} more files that were not added.");
        }

        foreach (string skipped in plan.Skipped)
        {
            Log.Info("Import", $"{skipped} is in the project already: skipped.");
        }

        if (plan.Files.Count == 0)
        {
            // Only a drop that was refused needs the user's attention; one the user chose to skip is in the console already.
            if (plan.Refused.Count > 0)
            {
                tell("Nothing was added:\n" + string.Join("\n", plan.Refused.Take(5).Select(r => $"{System.IO.Path.GetFileName(r.Path)}: {r.Reason}")) + (plan.Refused.Count > 5 ? "\n(the console lists them all)" : ""));
            }

            return;
        }

        if (!project.TryStartImport(plan, out ImportJob? _, out string? problem))
        {
            tell(problem!);
            return;
        }

        _running = new Running(project, _area, _at);
        Log.Info("Import", $"Adding {plan.Files.Count} file(s) to {_folder}.");
    }

    private static bool IsModel(string path) => path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

    private static void Summarize(ImportJobReport report, DropPlan plan)
    {
        string text = $"Added {report.Copied.Count} file(s), imported {report.Imported.Count}"
            + (report.Failed.Count > 0 ? $", {report.Failed.Count} failed" : "")
            + (report.Cancelled ? " (cancelled: what was copied stays)" : "")
            + (plan.Refused.Count > 0 ? $", {plan.Refused.Count} not added" : "") + ".";
        if (report.Error is not null)
        {
            Log.Error("Import", $"The import stopped: {report.Error}");
        }

        if (report.Success && plan.Refused.Count == 0)
        {
            Log.Info("Import", text);
        }
        else
        {
            Log.Warn("Import", text);
        }

        foreach ((string path, string message) in report.Failed)
        {
            Log.Error("Import", $"{path}: {message} (the file stays in the project; use Re-import in the Assets panel to try again)");
        }

        foreach ((string path, Apogee.Core.Assets.AssetUid old, Apogee.Core.Assets.AssetUid fresh) in report.Reassigned)
        {
            Log.Info("Import", $"{path}: its settings came with the UID {old}, which another asset has; it has {fresh}.");
        }
    }
}
