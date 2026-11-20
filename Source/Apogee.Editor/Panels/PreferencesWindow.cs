using System;
using System.Numerics;
using Apogee.DevTools;
using Apogee.Editor.Editing;
using Apogee.Editor.Input;
using Apogee.Native.CImGui;

namespace Apogee.Editor.Panels;

/// <summary>
/// <b>Edit &gt; Preferences</b>: a floating window with two tabs. <i>General</i> edits the values that are kept in <c>preferences.xml</c> (camera speed, gizmo space and snap steps, the interface scale), <i>Keys</i> lists
/// every action with its chord and rebinds it (<see cref="KeyBindingEditor"/> does the work; this class only draws it). It works with or without an open project.
/// </summary>
public sealed unsafe class PreferencesWindow(EditorPreferences preferences, Keymap keymap, GizmoSettings gizmo, IKeyState keys)
{
    private static readonly Vector4 Red = new(1f, 0.4f, 0.35f, 1f);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly KeyBindingEditor _editor = new(keymap);
    private bool _open;

    /// <summary>The window is shown.</summary>
    public bool IsOpen => _open;

    /// <summary>Shows the window (focused when it is already open).</summary>
    public void Show() => _open = true;

    /// <summary>True while the Keys tab waits for a key: the editor's own shortcuts are off meanwhile (<see cref="Keymap.Suspended"/>).</summary>
    public bool Capturing => _editor.Capturing is not null;

    /// <summary>Draws the window if it is open and takes the key it waits for; call it every frame.</summary>
    public void Draw()
    {
        if (!_open)
        {
            if (Capturing)
            {
                _editor.Cancel();
            }

            return;
        }

        _ = _editor.Poll(keys);
        if (Ui.Begin("Preferences", ref _open, ImGuiWindowFlags.NoDocking))
        {
            fixed (byte* bar = Apogee.Interop.Utf8.ToZeroTerminated("##preferences"))
            {
                if (ImGui.BeginTabBar(bar, ImGuiTabBarFlags.None))
                {
                    if (Tab("General"))
                    {
                        DrawGeneral();
                        ImGui.EndTabItem();
                    }

                    if (Tab("Keys"))
                    {
                        DrawKeys();
                        ImGui.EndTabItem();
                    }
                    else if (Capturing)
                    {
                        _editor.Cancel(); // the Keys tab is not shown: nobody sees the key being awaited
                    }

                    ImGui.EndTabBar();
                }
            }

            Ui.Spacing();
            Ui.Text(preferences.FilePath is null ? "These preferences are not saved." : preferences.Persistent ? "Saved in " + preferences.FilePath : "Not saved in this run: " + preferences.FilePath + " could not be read.");
        }

        Ui.End();
        if (!_open && Capturing)
        {
            _editor.Cancel();
        }
    }

    private static bool Tab(string label)
    {
        fixed (byte* text = Apogee.Interop.Utf8.ToZeroTerminated(label))
        {
            return ImGui.BeginTabItem(text, null, ImGuiTabItemFlags.None);
        }
    }

    private void DrawGeneral()
    {
        Gui.SeparatorText("Camera");
        float speed = (float)preferences.CameraSpeed;
        if (Gui.DragFloat("Fly speed (m/s)", ref speed, 0.1f, 0.5f, 200f, "%.1f"))
        {
            preferences.CameraSpeed = speed;
        }

        Gui.Tooltip("How fast WASD flies while the right mouse button is held. Shift is three times as fast.");

        Gui.SeparatorText("Gizmo");
        bool world = gizmo.World;
        if (Gui.Checkbox("World space", ref world))
        {
            gizmo.World = world;
        }

        int pivot = (int)gizmo.Pivot;
        if (Ui.Combo("Pivot", ref pivot, "Center", "Active", "Individual"))
        {
            gizmo.Pivot = (PivotMode)pivot;
        }

        bool snap = gizmo.Snap;
        if (Gui.Checkbox("Snap", ref snap))
        {
            gizmo.Snap = snap;
        }

        float move = gizmo.MoveStep;
        if (Gui.DragFloat("Move step (m)", ref move, 0.01f, 0.001f, 1000f))
        {
            gizmo.MoveStep = move;
        }

        float rotate = gizmo.RotateStep;
        if (Gui.DragFloat("Rotate step (degrees)", ref rotate, 0.1f, 0.01f, 180f, "%.2f"))
        {
            gizmo.RotateStep = rotate;
        }

        float scale = gizmo.ScaleStep;
        if (Gui.DragFloat("Scale step", ref scale, 0.01f, 0.001f, 100f))
        {
            gizmo.ScaleStep = scale;
        }

        Gui.SeparatorText("Viewport");
        bool helpers = gizmo.ShowHelpers;
        if (Gui.Checkbox("Helpers", ref helpers))
        {
            gizmo.ShowHelpers = helpers;
        }

        bool gameCamera = gizmo.PreferGameCamera;
        if (Gui.Checkbox("Game camera while playing", ref gameCamera))
        {
            gizmo.PreferGameCamera = gameCamera;
        }

        Gui.SeparatorText("Interface");
        float uiScale = (float)preferences.UiScale;
        if (Gui.DragFloat("Scale", ref uiScale, 0.01f, 0.5f, 3f, "%.2f"))
        {
            preferences.UiScale = uiScale;
        }

        Ui.Spacing();
        if (Gui.Button("Reset to defaults"))
        {
            preferences.ResetValues();
            preferences.ApplyTo(gizmo);
        }
    }

    private void DrawKeys()
    {
        if (Gui.Button("Reset all"))
        {
            _editor.ResetAll();
        }

        Gui.SameLine();
        Ui.Text(_editor.Capturing is { } waiting
            ? $"Press the new shortcut for {keymap.Action(waiting).Label}. Escape gives up."
            : "Click Rebind, then press the new shortcut.");
        if (_editor.Notice is { } notice)
        {
            Ui.TextColored(Red, notice);
        }

        if (!Ui.BeginTable("##keys", 4))
        {
            return;
        }

        Ui.TableHeader("Action", "Shortcut", "Where", "");
        foreach (BindingRow row in _editor.Rows())
        {
            Gui.PushId(row.Action.Id);
            Ui.Row();
            ImGui.TableNextColumn();
            Ui.Text(row.Action.Label);
            ImGui.TableNextColumn();
            bool waits = _editor.Capturing == row.Action.Id;
            Ui.TextColored(waits ? Grey : row.Conflicts.Count > 0 ? Red : new Vector4(1f, 1f, 1f, 1f), waits ? "press a key..." : row.Chords);
            foreach (string conflict in row.Conflicts)
            {
                Ui.TextColored(Red, conflict);
            }

            ImGui.TableNextColumn();
            Ui.Text(Keymap.Describe(row.Action.Context));
            ImGui.TableNextColumn();
            if (Gui.SmallButton(waits ? "Cancel" : "Rebind"))
            {
                if (waits)
                {
                    _editor.Cancel();
                }
                else
                {
                    _editor.Begin(row.Action.Id);
                }
            }

            Gui.SameLine();
            if (Gui.SmallButton("Clear"))
            {
                _editor.Clear(row.Action.Id);
            }

            Gui.SameLine();
            Gui.Disabled(row.IsDefault, () =>
            {
                if (Gui.SmallButton("Reset"))
                {
                    _editor.Reset(row.Action.Id);
                }
            });
            Gui.PopId();
        }

        Ui.EndTable();
    }
}
