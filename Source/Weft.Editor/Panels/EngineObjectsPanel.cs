using System.Collections.Generic;
using System.Linq;
using Weft.DevTools;
using Weft.Editor.Inspector;
using Weft.Native.CImGui;
using Weft.Reflection;

namespace Weft.Editor.Panels;

/// <summary>
/// <b>Debug &gt; Engine objects</b>: a floating window that lists the session's engine objects (the world, the UI, physics, audio, assets, models, input, network, Steam, the settings) and edits the one
/// that is picked through its registered properties. What it does is in <see cref="EngineObjectsModel"/> and <see cref="ObjectInspector"/>; this class only draws a combo, two buttons and the window
/// around them. The edits are live and are not on the scene's history.
/// </summary>
public sealed class EngineObjectsPanel
{
    private readonly ImGuiInspectorUi _ui = new();
    private EditorProject? _for;
    private EngineObjectsModel? _model;
    private bool _open;

    /// <summary>The window is shown.</summary>
    public bool IsOpen => _open;

    /// <summary>Shows or hides the window.</summary>
    public void Toggle() => _open = !_open;

    /// <summary>Draws the window if it is open; call it every frame.</summary>
    public void Draw(EditorContext ctx)
    {
        if (!_open)
        {
            return;
        }

        EditorProject project = ctx.Project;
        if (_for != project || _model is null)
        {
            _for = project;
            _model = new EngineObjectsModel(project);
        }

        if (Ui.Begin("Engine objects", ref _open))
        {
            ImGui.PushTextWrapPos(0f);
            Ui.Text(EngineObjectsModel.Notice);
            ImGui.PopTextWrapPos();
            IReadOnlyList<EngineObjectEntry> entries = _model.Entries();
            EngineObject? shown = _model.Selected;
            string[] names = [.. entries.Select(e => e.Name)];
            int selected = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (ReferenceEquals(entries[i].Target, shown))
                {
                    selected = i;
                }
            }

            int picked = _ui.Combo("Object", selected >= 0 ? names[selected] : "(none)", names, selected);
            if (picked >= 0 && picked < names.Length)
            {
                _model.SelectedName = names[picked];
            }

            ImGui.BeginDisabled(!_model.History.CanUndo);
            if (Gui.SmallButton(_model.History.UndoName is { } undo ? $"Undo {undo}" : "Undo"))
            {
                _model.Undo();
            }

            ImGui.EndDisabled();
            Gui.SameLine();
            ImGui.BeginDisabled(!_model.History.CanRedo);
            if (Gui.SmallButton(_model.History.RedoName is { } redo ? $"Redo {redo}" : "Redo"))
            {
                _model.Redo();
            }

            ImGui.EndDisabled();
            if (_model.Message is { } message)
            {
                _ui.ErrorText(message);
            }

            Ui.Separator();
            _model.Draw(_ui);
        }

        Ui.End();
    }
}
