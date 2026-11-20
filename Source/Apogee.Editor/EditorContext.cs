using System;
using Apogee.DevTools;
using Apogee.Editor.Editing;
using Apogee.Editor.Input;
using Apogee.Graphics;
using Apogee.Rendering;

namespace Apogee.Editor;

/// <summary>What the panels share: the project, the camera, the log, the profiler and the viewport renderer of the current session.</summary>
public sealed class EditorContext(EditorProject project, EditorCamera camera, EditorLog log, ImGuiHost host)
{
    /// <summary>The open project. Opening another project (<see cref="Editing.ProjectHost"/>) replaces it; the rest of the context (the log, the keymap, the gizmo settings) stays.</summary>
    public EditorProject Project { get; set; } = project;

    public EditorCamera Camera { get; } = camera;

    public EditorLog Log { get; } = log;

    public ImGuiHost Host { get; } = host;

    /// <summary>The system clipboard that Copy, Cut and Paste of entities use.</summary>
    public IClipboard Clipboard { get; init; } = new SdlClipboard();

    /// <summary>The key bindings of every shortcut and the labels the menus show.</summary>
    public Keymap Keymap { get; init; } = new();

    /// <summary>What the editor remembers between runs (camera speed, snap steps, recent projects, key bindings); in memory only when the context is made without the editor's folder.</summary>
    public EditorPreferences Preferences { get; init; } = EditorPreferences.InMemory();

    /// <summary>The keyboard the shortcuts read: ImGui's, or a fake in tests.</summary>
    public IKeyState Keys { get; init; } = new ImGuiKeyState(host);

    /// <summary>True on the frame the action's chord went down while <paramref name="focused"/> has the keyboard (see <see cref="Input.Keymap.Pressed"/>).</summary>
    public bool Pressed(string id, KeyContext focused = KeyContext.Global) => Keymap.Pressed(id, focused, Keys);

    /// <summary>True while the action's key is held (see <see cref="Input.Keymap.IsHeld"/>).</summary>
    public bool IsHeld(string id, KeyContext focused) => Keymap.IsHeld(id, focused, Keys);

    /// <summary>The shortcut text a menu item shows for the action, or null when it is unbound.</summary>
    public string? Shortcut(string id) => Keymap.ShortcutText(id);

    public DevOverlay Profiler { get; } = new() { Visible = false };

    /// <summary>Filled by the app each frame: GPU name, timings and the render graph.</summary>
    public DevOverlayData ProfilerData { get; set; } = new();

    /// <summary>The renderer of the current session; null with no GPU (tests) or while the session is being replaced.</summary>
    public ViewportRenderer? Viewport { get; set; }

    /// <summary>The render graph plan of the last viewport frame.</summary>
    public Apogee.Rendering.Graph.GraphPlan? Plan => Viewport?.Renderer.LastPlan;

    /// <summary>Set by the viewport panel: it is hovered or focused, so the game (while playing) gets the keyboard and the pointer.</summary>
    public bool ViewportHovered { get; set; }

    public bool ViewportFocused { get; set; }

    /// <summary>The selection tool of the gizmo.</summary>
    public GizmoSettings Gizmo { get; init; } = new();

    /// <summary>Asks the app to show a message the player must read (a failed Play).</summary>
    public string? Message { get; set; }
}

/// <summary>Which gizmo the viewport draws and how it snaps.</summary>
public sealed class GizmoSettings
{
    public int Operation { get; set; } = (int)Apogee.Native.ImGuizmo.AgzOperation.Translate;

    public bool World { get; set; } = true;

    /// <summary>Where a group of selected entities turns and scales around (see <see cref="PivotMode"/>); one entity always uses its own origin.</summary>
    public PivotMode Pivot { get; set; } = PivotMode.Center;

    public bool Snap { get; set; }

    public float MoveStep { get; set; } = 0.5f;

    public float RotateStep { get; set; } = 15f;

    public float ScaleStep { get; set; } = 0.1f;

    public bool ShowHelpers { get; set; } = true;

    public bool PreferGameCamera { get; set; } = true;
}
