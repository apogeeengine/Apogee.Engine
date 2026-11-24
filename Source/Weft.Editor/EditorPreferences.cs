using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Weft.Content.FileSystem;
using Weft.Content.Xml;
using Weft.Core.Logging;
using Weft.Editor.Editing;
using Weft.Editor.Input;
using Weft.Host;
using Weft.Native.ImGuizmo;
using Weft.Reflection;

namespace Weft.Editor;

/// <summary>
/// What the editor remembers between runs: its preferences (camera speed, the gizmo's mode, space and snap steps, the interface scale), the recently opened projects, the last scene of each project and the
/// key bindings that differ from the defaults. They are kept in <c>preferences.xml</c> in the editor's own folder, by the engine's <see cref="SettingsStore"/> over a file system of that folder alone: never in a
/// game's <c>user://settings.xml</c>, which belongs to one game and its mods and is replaced when Play starts. The dock layout stays in <c>imgui.ini</c>.
/// <para>
/// A file that does not read is never a crash and never lost: one that is not XML is kept as <c>preferences.bad.xml</c> beside it, one written by a newer editor as <c>preferences.v&lt;N&gt;.xml</c>, and the
/// defaults are used (<see cref="Problems"/> says so); one that cannot be read at all (locked, no permission) is left alone and the preferences are not saved in this run.
/// </para>
/// </summary>
public sealed class EditorPreferences
{
    /// <summary>The name of the file, in the editor's folder.</summary>
    public const string FileName = "preferences.xml";

    /// <summary>The format version this editor writes; a file with a higher one was written by a newer editor.</summary>
    public const int FormatVersion = 1;

    /// <summary>How many recent projects are kept.</summary>
    public const int MaxRecent = 10;

    /// <summary>The least time between two writes of the file while preferences change (a slider that is dragged).</summary>
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(1);

    /// <summary>The fly speed of the editor camera, in metres per second (Shift is three times as fast).</summary>
    public const string CameraSpeedKey = "editor.camera.speed";

    /// <summary>The gizmo's tool: <c>move</c>, <c>rotate</c> or <c>scale</c>.</summary>
    public const string GizmoModeKey = "editor.gizmo.mode";

    /// <summary>Whether the gizmo works in world space (otherwise local).</summary>
    public const string GizmoWorldKey = "editor.gizmo.world";

    /// <summary>Where a group turns and scales: <c>center</c>, <c>active</c> or <c>individual</c>.</summary>
    public const string GizmoPivotKey = "editor.gizmo.pivot";

    /// <summary>Whether the gizmo snaps.</summary>
    public const string GizmoSnapKey = "editor.gizmo.snap";

    /// <summary>The snap step of a move, in metres.</summary>
    public const string SnapMoveKey = "editor.gizmo.snap_move";

    /// <summary>The snap step of a rotation, in degrees.</summary>
    public const string SnapRotateKey = "editor.gizmo.snap_rotate";

    /// <summary>The snap step of a scale.</summary>
    public const string SnapScaleKey = "editor.gizmo.snap_scale";

    /// <summary>Whether the viewport draws the helpers (lights, cameras).</summary>
    public const string HelpersKey = "editor.helpers";

    /// <summary>Whether the viewport looks through the scene's main camera while playing.</summary>
    public const string GameCameraKey = "editor.game_camera";

    /// <summary>The scale of the interface, 1 being normal.</summary>
    public const string UiScaleKey = "editor.ui.scale";

    private static readonly string[] Modes = ["move", "rotate", "scale"];
    private static readonly string[] Pivots = ["center", "active", "individual"];

    private readonly SettingsStore _store;
    private readonly List<string> _problems = [];
    private List<string> _recent = [];

    private EditorPreferences(SettingsStore store, string? path, bool persistent)
    {
        _store = store;
        FilePath = path;
        Persistent = persistent;
        Define();
        ReadRecent();
    }

    /// <summary>The preferences of the editor whose folder is <paramref name="folder"/> (made when it is missing): <c>preferences.xml</c> in it. Pass the real editor folder, or a temporary one in a test.</summary>
    /// <param name="folder">The editor's folder. Nothing outside it is read or written.</param>
    public static EditorPreferences Open(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        string path = Path.Combine(Path.GetFullPath(folder), FileName);
        var problems = new List<string>();
        bool persistent = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            persistent = CheckFile(path, problems);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The editor's folder {Path.GetDirectoryName(path)} cannot be used ({e.Message}): preferences are not saved in this run.");
            persistent = false;
        }

        SettingsStore store;
        if (persistent)
        {
            var files = new VirtualFileSystem();
            files.Mount("user://", new DirectorySource(Path.GetDirectoryName(path)!, writable: true));
            store = SettingsStore.Open(files, "user://" + FileName);
        }
        else
        {
            store = new SettingsStore();
        }

        var preferences = new EditorPreferences(store, path, persistent);
        preferences._problems.AddRange(problems);
        return preferences;
    }

    /// <summary>Preferences that live in memory and are never written (the default of a context made without an editor folder, and tests of the panels).</summary>
    public static EditorPreferences InMemory() => new(new SettingsStore(), null, persistent: false);

    /// <summary>The file, or null for <see cref="InMemory"/> preferences.</summary>
    public string? FilePath { get; }

    /// <summary>False when nothing is written: the preferences are in memory, or the file could not be read and is left as it is.</summary>
    public bool Persistent { get; }

    /// <summary>What went wrong when the file was opened (a file that did not read, a newer one), one sentence each: the editor shows it once at the start.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>The clock the debounce of <see cref="SaveIfDirty"/> counts on; tests set a fake one.</summary>
    public TimeProvider Clock
    {
        get => _store.Clock;
        set => _store.Clock = value;
    }

    /// <summary>True when something changed that is not in the file yet.</summary>
    public bool IsDirty => _store.IsDirty;

    /// <summary>
    /// Looks at the file before the store reads it. Returns false when it must not be written over: it cannot be read. A newer file is moved aside (its copy kept) and an unparseable one is the store's own case
    /// (it keeps a <c>.bad.xml</c> copy); both leave the defaults in use.
    /// </summary>
    private static bool CheckFile(string path, List<string> problems)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{path} cannot be read ({e.Message}): the defaults are used and nothing is saved in this run, so the file stays as it is.");
            Log.Warn("Editor", problems[^1]);
            return false;
        }

        XmlNode root;
        try
        {
            root = XmlNode.Parse(text, path);
            if (root.Name != "settings")
            {
                throw new XmlParseException(path, root.Line, $"the root element is <{root.Name}>, not <settings>");
            }
        }
        catch (XmlParseException e)
        {
            problems.Add($"{e.Message}\nThe defaults are used, and the file is kept as {Path.GetFileName(path)[..^4]}.bad.xml.");
            return true; // the store sets the file aside
        }

        if (int.TryParse(root.Attribute("version"), out int version) && version > FormatVersion)
        {
            string aside = Path.Combine(Path.GetDirectoryName(path)!, $"preferences.v{version}.xml");
            try
            {
                File.Copy(path, aside, overwrite: true);
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{path} was written by a newer editor (version {version}) and could not be set aside ({e.Message}): the defaults are used and nothing is saved in this run.");
                Log.Warn("Editor", problems[^1]);
                return false;
            }

            problems.Add($"{path} was written by a newer editor (version {version}): its preferences are not read, and the file is kept as {Path.GetFileName(aside)}.");
            Log.Warn("Editor", problems[^1]);
        }

        return true;
    }

    private void Define()
    {
        _store.Define(SettingDef.OfNumber(CameraSpeedKey, 6, min: 0.5, max: 200, help: "How fast the camera flies, in metres per second."));
        _store.Define(SettingDef.OfChoice(GizmoModeKey, "move", Modes));
        _store.Define(SettingDef.OfBool(GizmoWorldKey, true));
        _store.Define(SettingDef.OfChoice(GizmoPivotKey, "center", Pivots));
        _store.Define(SettingDef.OfBool(GizmoSnapKey, false));
        _store.Define(SettingDef.OfNumber(SnapMoveKey, 0.5, min: 0.001, max: 1000));
        _store.Define(SettingDef.OfNumber(SnapRotateKey, 15, min: 0.01, max: 180));
        _store.Define(SettingDef.OfNumber(SnapScaleKey, 0.1, min: 0.001, max: 100));
        _store.Define(SettingDef.OfBool(HelpersKey, true));
        _store.Define(SettingDef.OfBool(GameCameraKey, true));
        _store.Define(SettingDef.OfNumber(UiScaleKey, 1, min: 0.5, max: 3, help: "The scale of the interface."));
        for (int i = 0; i < MaxRecent; i++)
        {
            _store.Define(SettingDef.OfString(RecentKey(i), ""));
        }
    }

    // ---- Typed values -----------------------------------------------------------------------------------------------------------

    /// <summary>The fly speed of the camera, in metres per second.</summary>
    public double CameraSpeed
    {
        get => _store.GetNumber(CameraSpeedKey);
        set => _store.Set(CameraSpeedKey, Value.Float(value));
    }

    /// <summary>The interface scale (applied at once).</summary>
    public double UiScale
    {
        get => _store.GetNumber(UiScaleKey);
        set => _store.Set(UiScaleKey, Value.Float(value));
    }

    /// <summary>Gives the gizmo settings the values that were saved (at the start).</summary>
    public void ApplyTo(GizmoSettings gizmo)
    {
        ArgumentNullException.ThrowIfNull(gizmo);
        gizmo.Operation = (int)(_store.GetString(GizmoModeKey) switch
        {
            "rotate" => AgzOperation.Rotate,
            "scale" => AgzOperation.Scale,
            _ => AgzOperation.Translate,
        });
        gizmo.World = _store.GetBool(GizmoWorldKey);
        gizmo.Pivot = _store.GetString(GizmoPivotKey) switch
        {
            "active" => PivotMode.Active,
            "individual" => PivotMode.Individual,
            _ => PivotMode.Center,
        };
        gizmo.Snap = _store.GetBool(GizmoSnapKey);
        gizmo.MoveStep = (float)_store.GetNumber(SnapMoveKey);
        gizmo.RotateStep = (float)_store.GetNumber(SnapRotateKey);
        gizmo.ScaleStep = (float)_store.GetNumber(SnapScaleKey);
        gizmo.ShowHelpers = _store.GetBool(HelpersKey);
        gizmo.PreferGameCamera = _store.GetBool(GameCameraKey);
    }

    /// <summary>Keeps the gizmo settings as they are now (called every frame: a value that did not change costs nothing and is not written).</summary>
    public void CaptureFrom(GizmoSettings gizmo)
    {
        ArgumentNullException.ThrowIfNull(gizmo);
        string mode = (AgzOperation)gizmo.Operation switch
        {
            AgzOperation.Rotate => "rotate",
            AgzOperation.Scale => "scale",
            _ => "move",
        };
        _store.Set(GizmoModeKey, Value.String(mode));
        _store.Set(GizmoWorldKey, Value.Bool(gizmo.World));
        _store.Set(GizmoPivotKey, Value.String(gizmo.Pivot switch
        {
            PivotMode.Active => "active",
            PivotMode.Individual => "individual",
            _ => "center",
        }));
        _store.Set(GizmoSnapKey, Value.Bool(gizmo.Snap));
        SetSingle(SnapMoveKey, gizmo.MoveStep);
        SetSingle(SnapRotateKey, gizmo.RotateStep);
        SetSingle(SnapScaleKey, gizmo.ScaleStep);
        _store.Set(HelpersKey, Value.Bool(gizmo.ShowHelpers));
        _store.Set(GameCameraKey, Value.Bool(gizmo.PreferGameCamera));
    }

    /// <summary>
    /// Stores a single-precision value without disturbing the double the store holds: a value that is the same as a float is left alone (0.1f is not the double 0.1, and must not be written as a difference from the
    /// default), and a new one is stored as the shortest decimal that reads back as the float (0.1, not 0.10000000149).
    /// </summary>
    private void SetSingle(string key, float value)
    {
        if ((float)_store.GetNumber(key) != value)
        {
            _store.Set(key, Value.Float(double.Parse(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>Puts every preference (not the recent projects or the key bindings) back to its default.</summary>
    public void ResetValues()
    {
        _store.ResetAll("editor.");
    }

    // ---- Recent projects --------------------------------------------------------------------------------------------------------

    /// <summary>The recently opened game folders as full paths, the latest first, at most <see cref="MaxRecent"/>, none of them gone from the disk when the list was last updated.</summary>
    public IReadOnlyList<string> Recent => _recent;

    /// <summary>Puts <paramref name="folder"/> at the top of the recent projects: a project that is already there moves up (the same folder under another spelling counts as the same), and the list is cut to <see cref="MaxRecent"/>.</summary>
    public void AddRecent(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        string full = Normalize(folder);
        _recent = [full, .. _recent.Where(r => !SamePath(r, full))];
        if (_recent.Count > MaxRecent)
        {
            _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
        }

        WriteRecent();
    }

    /// <summary>Takes a folder out of the recent projects (one that turned out to be gone).</summary>
    public void RemoveRecent(string folder)
    {
        string full = Normalize(folder);
        if (_recent.RemoveAll(r => SamePath(r, full)) > 0)
        {
            WriteRecent();
        }
    }

    /// <summary>Forgets the folders that no longer exist (done when the file is read; a menu may call it again).</summary>
    public void PruneRecent()
    {
        if (_recent.RemoveAll(r => !Directory.Exists(r)) > 0)
        {
            WriteRecent();
        }
    }

    private void ReadRecent()
    {
        var found = new List<string>();
        for (int i = 0; i < MaxRecent; i++)
        {
            string text = _store.GetString(RecentKey(i));
            if (text.Length > 0 && Path.IsPathRooted(text))
            {
                string full = Normalize(text);
                if (!found.Any(f => SamePath(f, full)))
                {
                    found.Add(full);
                }
            }
        }

        _recent = found;
        PruneRecent();
    }

    private void WriteRecent()
    {
        for (int i = 0; i < MaxRecent; i++)
        {
            _store.Set(RecentKey(i), Value.String(i < _recent.Count ? _recent[i] : ""));
        }
    }

    private static string RecentKey(int index) => $"recent.{index}";

    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    // ---- The last scene of each project ------------------------------------------------------------------------------------------

    /// <summary>The key that holds the last scene of a project: <c>project.&lt;first 16 hex digits of the SHA-256 of its full path&gt;.scene</c>.</summary>
    public static string SceneKey(string gameFolder)
    {
        string full = Normalize(gameFolder);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsLinux() ? full : full.ToLowerInvariant()));
        return "project." + Convert.ToHexStringLower(hash.AsSpan(0, 8)) + ".scene";
    }

    /// <summary>The virtual path (<c>res://scenes/level.ascene</c>) of the scene that was open last in the project, or null.</summary>
    public string? LastScene(string gameFolder)
    {
        string key = SceneKey(gameFolder);
        if (_store.Definition(key) is null)
        {
            _store.Define(SettingDef.OfString(key, ""));
        }

        string scene = _store.GetString(key);
        return scene.Length == 0 ? null : scene;
    }

    /// <summary>Remembers the scene that is open in the project (null: none, the game's own start).</summary>
    public void SetLastScene(string gameFolder, string? scene)
    {
        string key = SceneKey(gameFolder);
        if (_store.Definition(key) is null)
        {
            _store.Define(SettingDef.OfString(key, ""));
        }

        _store.Set(key, Value.String(scene ?? ""));
    }

    // ---- Key bindings -----------------------------------------------------------------------------------------------------------

    /// <summary>The key of an action's binding: <c>keys.&lt;action id&gt;</c>.</summary>
    public static string KeysKey(string actionId) => "keys." + actionId;

    /// <summary>
    /// Applies the saved key bindings to <paramref name="keymap"/> and keeps it saved from then on. Only what differs from the defaults is stored: <c>keys.edit.undo</c> holds the chords joined with <c> | </c>
    /// (<c>Ctrl+Y | Ctrl+Shift+Z</c>), <c>none</c> for an action that was unbound, and nothing for one that is on its defaults. Bindings of actions this editor does not know stay in the file untouched (a newer editor's).
    /// Returns what to tell the user: a saved chord that does not read (the action keeps its default) and collisions the saved bindings cause.
    /// </summary>
    public IReadOnlyList<string> AttachKeymap(Keymap keymap)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        var problems = new List<string>();
        foreach (EditorAction action in keymap.Actions)
        {
            string key = KeysKey(action.Id);
            if (!SettingDef.IsValidKey(key))
            {
                continue;
            }

            if (_store.Definition(key) is null)
            {
                _store.Define(SettingDef.OfString(key, ""));
            }

            string text = _store.GetString(key);
            if (text.Length == 0)
            {
                continue;
            }

            if (TryParseChords(text, out List<KeyChord>? chords))
            {
                keymap.SetBindings(action.Id, chords);
            }
            else
            {
                problems.Add($"{action.Label}: '{text}' is not a list of key chords (such as Ctrl+Shift+K), so the default is used.");
                _store.Reset(key);
            }
        }

        foreach (KeyConflict conflict in keymap.Conflicts())
        {
            problems.Add($"{conflict}.");
        }

        keymap.Changed += () => SyncKeymap(keymap);
        return problems;
    }

    /// <summary>The chords of a saved binding: <c>none</c>, or chords joined with <c>|</c>. False when one of them does not read.</summary>
    public static bool TryParseChords(string text, out List<KeyChord> chords)
    {
        chords = [];
        if (text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string part in text.Split('|', StringSplitOptions.TrimEntries))
        {
            if (!KeyChord.TryParse(part, out KeyChord chord))
            {
                return false;
            }

            chords.Add(chord);
        }

        return true;
    }

    /// <summary>The text a binding is saved as (the primary modifier is always written as Ctrl: the file reads the same on every platform).</summary>
    public static string FormatChords(IReadOnlyList<KeyChord> chords) =>
        chords.Count == 0 ? "none" : string.Join(" | ", chords.Select(c => c.ToString(macOs: false)));

    private void SyncKeymap(Keymap keymap)
    {
        foreach (EditorAction action in keymap.Actions)
        {
            string key = KeysKey(action.Id);
            if (_store.Definition(key) is not null)
            {
                _store.Set(key, Value.String(keymap.IsDefault(action.Id) ? "" : FormatChords(keymap.Bindings(action.Id))));
            }
        }
    }

    // ---- Saving -----------------------------------------------------------------------------------------------------------------

    /// <summary>Writes the changes when there are any and the last write was at least <see cref="SaveInterval"/> ago; called every frame.</summary>
    public void SaveIfDirty() => _store.SaveIfDirty(SaveInterval);

    /// <summary>Writes what is not written yet, now: at the end of the session.</summary>
    public void Flush() => _store.Flush();
}
