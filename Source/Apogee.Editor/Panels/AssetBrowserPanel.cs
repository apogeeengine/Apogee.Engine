using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Core.Logging;
using Apogee.DevTools;
using Apogee.Host;
using Apogee.Native.CImGui;

namespace Apogee.Editor.Panels;

/// <summary>
/// The files of the game: the virtual file system's mounts (<c>res://</c>, and the packages' <c>mod://id/</c> folders), each file with where its import stands (<see cref="AssetStatus"/>), a re-import for
/// a file that has changed, and drag and drop: a model dragged into the viewport makes an entity, a scene file opens on a double click.
/// </summary>
public sealed class AssetBrowserPanel
{
    private string _directory = "res://";
    private string _filter = "";
    private readonly Dictionary<string, AssetStatus> _statuses = new(StringComparer.Ordinal);
    private double _statusAge;
    private string _statusDirectory = "";
    private AssetImporter? _importer;
    private object? _importerSession;
    private bool _wasBusy;

    /// <summary>The folder the panel shows (a virtual path): where files dropped from the system are put.</summary>
    public string Folder => _directory;

    /// <summary>The panel on the screen, in points, as it was drawn last; null when it was not drawn (hidden behind another tab). Where a file dropped from the system is routed.</summary>
    public (Vector2 Min, Vector2 Size)? WindowRect { get; private set; }

    public void Draw(EditorContext ctx, double now)
    {
        WindowRect = null;
        if (!Ui.Begin("Assets"))
        {
            Ui.End();
            return;
        }

        WindowRect = (Gui.N(ImGui.GetWindowPos()), Gui.N(ImGui.GetWindowSize()));

        VirtualFileSystem? files = ctx.Project.Session.Files;
        if (files is null)
        {
            Ui.Text("The game has no file system.");
            Ui.End();
            return;
        }

        if (!ReferenceEquals(_importerSession, ctx.Project.Session))
        {
            _importerSession = ctx.Project.Session;
            _importer = new AssetImporter(files, ScriptSession.Importers);
            _statuses.Clear();
        }

        DrawBreadcrumbs(files);
        Gui.SameLine();
        ImGui.SetNextItemWidth(160);
        Gui.InputText("##assetfilter", ref _filter);
        Ui.Separator();

        // An import job writes the index and the artifacts: nothing here reads or imports them meanwhile (see ImportGate), and what it did is looked at again as soon as it has ended.
        bool busy = ctx.Project.ImportGate.IsHeld;
        if (_wasBusy && !busy)
        {
            _statusAge = 0;
        }

        _wasBusy = busy;
        if (!busy && (_statusDirectory != _directory || now - _statusAge > 2.0))
        {
            RefreshStatuses(files);
            _statusDirectory = _directory;
            _statusAge = now;
        }

        string? enter = null;
        if (_directory != "res://" && !IsMountRoot(_directory) && Gui.Selectable("[..]", false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClickedNil(ImGuiMouseButton.Left))
        {
            enter = VfsPath.ParentOf(_directory);
        }

        List<string> directories = Safe(() => files.EnumerateDirectories(_directory));
        foreach (string directory in directories)
        {
            string name = VfsPath.FileName(directory.TrimEnd('/'));
            if (name.StartsWith('.') || name == AssetImporter.CacheFolder || !name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Gui.Selectable($"[{name}]", false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClickedNil(ImGuiMouseButton.Left))
            {
                enter = directory;
            }
        }

        foreach (string file in Safe(() => files.EnumerateFiles(_directory)))
        {
            string name = VfsPath.FileName(file);
            if (name.StartsWith('.') || !name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DrawFile(ctx, file, name, busy);
        }

        if (enter is not null)
        {
            _directory = enter;
        }

        Ui.End();
    }

    private static bool IsMountRoot(string directory) => directory.EndsWith("://", StringComparison.Ordinal) || (directory.StartsWith("mod://", StringComparison.Ordinal) && directory.TrimEnd('/').Count(c => c == '/') <= 2);

    private static List<string> Safe(Func<List<string>> list)
    {
        try
        {
            return list();
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void DrawBreadcrumbs(VirtualFileSystem files)
    {
        if (Gui.SmallButton("res://"))
        {
            _directory = "res://";
        }

        // The packages' folders, when the file system lists them.
        List<string> mods = Safe(() => files.EnumerateDirectories("mod://"));
        if (mods.Count > 0)
        {
            Gui.SameLine();
            if (Gui.SmallButton("mods..."))
            {
                Gui.OpenPopup("mods_popup");
            }

            if (Gui.BeginPopup("mods_popup"))
            {
                foreach (string mod in mods)
                {
                    if (Gui.MenuItem(mod))
                    {
                        _directory = mod.EndsWith('/') ? mod : mod + "/";
                    }
                }

                Gui.EndPopup();
            }
        }

        Gui.SameLine();
        Ui.Text(_directory);
    }

    private void RefreshStatuses(VirtualFileSystem files)
    {
        _statuses.Clear();
        if (_importer is null || !_directory.StartsWith("res://", StringComparison.Ordinal))
        {
            return;
        }

        foreach (string file in Safe(() => files.EnumerateFiles(_directory)))
        {
            _statuses[file] = _importer.StatusOf(file["res://".Length..]);
        }
    }

    private void DrawFile(EditorContext ctx, string path, string name, bool busy)
    {
        _statuses.TryGetValue(path, out AssetStatus status);
        bool importable = status != AssetStatus.NotImportable && _statuses.ContainsKey(path);
        string tag = !importable ? "" : status switch
        {
            AssetStatus.UpToDate => "  (imported)",
            AssetStatus.Stale => "  (changed: re-import)",
            _ => "  (not imported)",
        };

        bool isScene = path.EndsWith(".ascene", StringComparison.OrdinalIgnoreCase);
        Gui.PushId(path);
        if (Gui.Selectable(name + tag, false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClickedNil(ImGuiMouseButton.Left) && isScene)
        {
            ctx.Project.OpenScene(path);
        }

        if (Gui.BeginDragSource())
        {
            Gui.PayloadText("ASSET", path);
            Ui.Text(name);
            Gui.EndDragSource();
        }

        if (Gui.BeginContextMenu())
        {
            if (isScene && Gui.MenuItem("Open scene"))
            {
                ctx.Project.OpenScene(path);
            }

            if (Gui.MenuItem("Re-import", null, false, importable && _importer is not null && !busy) && ctx.Project.ImportGate.TryEnter())
            {
                ImportReport report;
                try
                {
                    report = _importer!.Reimport(path["res://".Length..]);
                }
                finally
                {
                    ctx.Project.ImportGate.Exit();
                }

                foreach ((string source, string message) in report.Failed)
                {
                    Log.Error("Editor", $"{source}: {message}");
                }

                if (report.Imported.Count > 0)
                {
                    Log.Info("Editor", $"Imported {path} again");
                }

                _statusAge = 0;
            }

            Gui.EndPopup();
        }

        Gui.PopId();
    }
}
