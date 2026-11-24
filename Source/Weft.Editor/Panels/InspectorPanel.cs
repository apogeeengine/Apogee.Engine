using System.Collections.Generic;
using Weft.DevTools;
using Weft.Ecs;
using Weft.Editor.Inspector;
using Weft.Native.CImGui;
using Weft.Reflection;
using Weft.Rendering;

namespace Weft.Editor.Panels;

/// <summary>
/// The components of the selected entities, drawn from the registry's descriptions through <see cref="InspectorView"/> and the <see cref="InspectorDrawers"/> table. With several entities selected it
/// shows the active entity's components that all of them have, and an edit changes every selected entity. This is the window around it: the name, the component headings with their menus, and the
/// <i>Add component</i> popup; every change is a command on the history, and the changes of one drag are one step.
/// </summary>
public sealed class InspectorPanel
{
    private readonly ImGuiInspectorUi _ui = new();
    private string _filter = "";
    private string _nameText = "";
    private Entity? _nameFor;
    private EditorProject? _viewFor;
    private InspectorView? _view;
    private InspectorEditor? _editor;

    public void Draw(EditorContext ctx)
    {
        if (!Ui.Begin("Inspector"))
        {
            Ui.End();
            return;
        }

        EditorProject project = ctx.Project;
        World world = project.World;
        if (project.Selection is not { } entity)
        {
            Ui.Text("Nothing selected.");
            Ui.End();
            return;
        }

        if (_viewFor != project || _view is null || _editor is null)
        {
            _viewFor = project;
            _view = new InspectorView(project);
            _editor = new InspectorEditor(project);
        }

        IReadOnlyList<Entity> targets = _editor.Targets;
        bool locked = project.IsPlaying;
        if (_nameFor != entity)
        {
            _nameFor = entity;
            _nameText = world.GetName(entity) ?? "";
        }

        ImGui.BeginDisabled(locked);
        if (Gui.InputText("Name", ref _nameText, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            if (!project.Rename(entity, _nameText))
            {
                _nameText = world.GetName(entity) ?? "";
            }
        }

        ImGui.EndDisabled();
        Ui.Text($"Entity {entity.Id & 0xFFFFFFFF}" + (locked ? "   (playing: values show the game's, stop to edit)" : ""));
        if (targets.Count > 1)
        {
            Ui.Text($"{targets.Count} entities selected: the active one's values, and the components all of them have. An edit changes every one.");
        }

        Ui.Separator();

        foreach ((Entity id, ComponentInfo info) in _editor.SharedComponents(entity, targets))
        {
            if (info.Name == nameof(EditorOnly))
            {
                continue;
            }

            Gui.PushId((int)(id.Id & 0x7FFFFFFF));
            bool open = Gui.TreeNode(info.Name, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.SpanAvailWidth);
            if (!locked && Gui.BeginContextMenu())
            {
                if (Gui.MenuItem("Reset to default", null, false, !info.IsTag))
                {
                    _editor.ResetComponent(targets, id, info);
                }

                if (Gui.MenuItem("Remove component", null, false, info.Name != nameof(Transform)))
                {
                    _editor.RemoveComponent(targets, id, info);
                }

                Gui.EndPopup();
            }

            if (open)
            {
                if (info.IsTag)
                {
                    Ui.Text("(a tag: it has no fields)");
                }
                else
                {
                    _view.DrawComponent(_ui, targets, entity, id, info, locked);
                }

                Gui.TreePop();
            }

            Gui.PopId();
        }

        Ui.Separator();
        ImGui.BeginDisabled(locked);
        if (Gui.Button("Add component", new System.Numerics.Vector2(-1, 0)))
        {
            Gui.OpenPopup("add_component");
        }

        ImGui.EndDisabled();
        DrawAddPopup(targets, _editor);
        Ui.End();
    }

    private void DrawAddPopup(IReadOnlyList<Entity> targets, InspectorEditor editor)
    {
        if (!Gui.BeginPopup("add_component"))
        {
            return;
        }

        Gui.InputText("##filter", ref _filter);
        foreach (ComponentInfo info in editor.AddCandidates(targets, _filter))
        {
            if (Gui.MenuItem(info.Name))
            {
                editor.AddComponent(targets, info);
                Gui.ClosePopup();
            }
        }

        Gui.EndPopup();
    }
}
