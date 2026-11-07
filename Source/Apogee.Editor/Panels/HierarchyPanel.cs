using System;
using Apogee.DevTools;
using Apogee.Ecs;
using Apogee.Native.CImGui;
using Apogee.Reflection;

namespace Apogee.Editor.Panels;

/// <summary>The scene as a tree: select, rename, create, duplicate, delete, and drag an entity onto another to make it a child (or onto the empty space below to make it a root).</summary>
public sealed class HierarchyPanel
{
    private Entity? _renaming;
    private string _renameText = "";
    private bool _focusRename;

    public void Draw(EditorContext ctx)
    {
        if (!Ui.Begin("Hierarchy"))
        {
            Ui.End();
            return;
        }

        EditorProject project = ctx.Project;
        World world = project.World;
        bool locked = project.IsPlaying;

        Gui.Disabled(locked, () =>
        {
            if (Gui.Button("+ Create"))
            {
                Gui.OpenPopup("create_root");
            }

            if (Gui.BeginPopup("create_root"))
            {
                CreateMenu(project, default);
                Gui.EndPopup();
            }
        });
        if (locked)
        {
            Gui.SameLine();
            Ui.Text("(playing: stop to edit)");
        }

        Ui.Separator();

        foreach (Entity root in world.Roots())
        {
            DrawNode(ctx, root, locked);
        }

        // Empty space: a drop here makes an entity a root, a click clears the selection, a right click creates.
        EmptySpace(ctx, locked);

        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !ctx.Host.WantsKeyboard && !locked && project.Selection is { } selected)
        {
            if (Gui.KeyPressed(ImGuiKey.ImGuiKeyDelete))
            {
                project.Delete(selected);
            }
            else if (Gui.Ctrl && Gui.KeyPressed(ImGuiKey.ImGuiKeyD))
            {
                project.Duplicate(selected);
            }
            else if (Gui.KeyPressed(ImGuiKey.ImGuiKeyF2))
            {
                BeginRename(world, selected);
            }
        }

        Ui.End();
    }

    private static void EmptySpace(EditorContext ctx, bool locked)
    {
        var size = Gui.Available;
        if (size.Y < 8)
        {
            size.Y = 8;
        }

        Gui.InvisibleButton("##empty", size);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            ctx.Project.Selection = null;
        }

        if (!locked && Gui.BeginDropTarget())
        {
            if (Gui.AcceptId("ENTITY") is { } id)
            {
                ctx.Project.Reparent(new Entity(id), default);
            }

            Gui.EndDropTarget();
        }

        if (!locked && Gui.BeginContextMenu("empty_menu", onItem: true))
        {
            CreateMenu(ctx.Project, default);
            Gui.EndPopup();
        }
    }

    private void BeginRename(World world, Entity entity)
    {
        _renaming = entity;
        _renameText = world.GetName(entity) ?? "";
        _focusRename = true;
    }

    private static void CreateMenu(EditorProject project, Entity parent)
    {
        if (Gui.MenuItem("Empty"))
        {
            project.CreateEmpty(parent);
        }

        if (Gui.MenuItem("Point light"))
        {
            project.CreatePointLight(parent);
        }

        if (Gui.MenuItem("Sun"))
        {
            project.CreateSun(parent);
        }

        if (Gui.MenuItem("Camera"))
        {
            project.CreateCamera(parent);
        }
    }

    private void DrawNode(EditorContext ctx, Entity entity, bool locked)
    {
        EditorProject project = ctx.Project;
        World world = project.World;
        var children = world.Children(entity);
        string name = world.GetName(entity) ?? $"#{entity.Id & 0xFFFFFFFF}";
        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
        if (children.Count == 0)
        {
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        }

        if (project.Selection == entity)
        {
            flags |= ImGuiTreeNodeFlags.Selected;
        }

        Gui.PushId((int)(entity.Id & 0x7FFFFFFF));
        bool open;
        if (_renaming == entity)
        {
            if (_focusRename)
            {
                ImGui.SetKeyboardFocusHere(0);
                _focusRename = false;
            }

            if (Gui.InputText("##rename", ref _renameText, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
            {
                project.Rename(entity, _renameText);
                _renaming = null;
            }
            else if (ImGui.IsItemDeactivated())
            {
                _renaming = null;
            }

            open = false;
        }
        else
        {
            open = Gui.TreeNode(name, flags);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
            {
                project.Selection = entity;
            }

            if (!locked && ImGui.IsItemHovered(ImGuiHoveredFlags.None) && Gui.MouseClicked(ImGuiMouseButton.Left) == false && ImGui.IsMouseDoubleClickedNil(ImGuiMouseButton.Left))
            {
                BeginRename(world, entity);
            }

            if (!locked && Gui.BeginDragSource())
            {
                Gui.PayloadId("ENTITY", entity.Id);
                Ui.Text(name);
                Gui.EndDragSource();
            }

            if (!locked && Gui.BeginDropTarget())
            {
                if (Gui.AcceptId("ENTITY") is { } id)
                {
                    project.Reparent(new Entity(id), entity);
                }

                Gui.EndDropTarget();
            }

            if (!locked && Gui.BeginContextMenu())
            {
                project.Selection = entity;
                if (Gui.MenuItem("Rename", "F2"))
                {
                    BeginRename(world, entity);
                }

                if (Gui.MenuItem("Duplicate", "Ctrl+D"))
                {
                    project.Duplicate(entity);
                }

                if (Gui.MenuItem("Delete", "Del"))
                {
                    project.Delete(entity);
                }

                Ui.Separator();
                if (Gui.BeginMenu("Create child"))
                {
                    CreateMenu(project, entity);
                    Gui.EndMenu();
                }

                Gui.EndPopup();
            }
        }

        if (open && children.Count > 0)
        {
            foreach (Entity child in children)
            {
                DrawNode(ctx, child, locked);
            }

            Gui.TreePop();
        }

        Gui.PopId();
    }
}
