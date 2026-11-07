using System;
using Apogee.DevTools;
using Apogee.Editor.Editing;
using Apogee.Graphics;
using Apogee.Rendering;

namespace Apogee.Editor;

/// <summary>What the panels share: the project, the camera, the log, the profiler and the viewport renderer of the current session.</summary>
public sealed class EditorContext(EditorProject project, EditorCamera camera, EditorLog log, ImGuiHost host)
{
    public EditorProject Project { get; } = project;

    public EditorCamera Camera { get; } = camera;

    public EditorLog Log { get; } = log;

    public ImGuiHost Host { get; } = host;

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
    public GizmoSettings Gizmo { get; } = new();

    /// <summary>Asks the app to show a message the player must read (a failed Play).</summary>
    public string? Message { get; set; }
}

/// <summary>Which gizmo the viewport draws and how it snaps.</summary>
public sealed class GizmoSettings
{
    public int Operation { get; set; } = (int)Apogee.Native.ImGuizmo.AgzOperation.Translate;

    public bool World { get; set; } = true;

    public bool Snap { get; set; }

    public float MoveStep { get; set; } = 0.5f;

    public float RotateStep { get; set; } = 15f;

    public float ScaleStep { get; set; } = 0.1f;

    public bool ShowHelpers { get; set; } = true;

    public bool PreferGameCamera { get; set; } = true;
}
