using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Weft.DevTools;
using Weft.Ecs;
using Weft.Editor.Editing;
using Weft.Editor.Input;
using Weft.Native.CImGui;
using Weft.Reflection;

namespace Weft.Editor.Panels;

/// <summary>
/// The scene as a tree: select (click, Ctrl-click to toggle, Shift-click for a range, drag a box on the empty space), rename, create, duplicate, delete, and drag the selected entities onto another to
/// make them children (or onto the empty space below to make them roots). ImGui's multi-select does the clicking and the box; what it asks for is applied to the project's selection by
/// <see cref="SelectionRequests"/>, against the list of nodes that are visible (a collapsed branch is skipped by a range).
/// </summary>
public sealed unsafe class HierarchyPanel
{
    private Entity? _renaming;
    private string _renameText = "";
    private bool _focusRename;
    private List<Entity> _visible = [];       // the nodes drawn this frame, top to bottom: what a multi-select request's item indexes mean
    private List<Entity> _drawnBefore = [];   // and last frame's, which the requests ImGui makes at the start of a frame refer to
    private Entity? _clicked;

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

        Selection selection = project.Selected;
        ImGuiMultiSelectIO* io = ImGui.BeginMultiSelect(ImGuiMultiSelectFlags.BoxSelect1d | ImGuiMultiSelectFlags.ClearOnEscape | ImGuiMultiSelectFlags.ClearOnClickVoid, selection.Count, _drawnBefore.Count);
        ApplyRequests(io, selection, _drawnBefore);
        _visible.Clear();
        _clicked = null;
        foreach (Entity root in world.Roots())
        {
            DrawNode(ctx, root, locked);
        }

        io = ImGui.EndMultiSelect();
        ApplyRequests(io, selection, _visible);
        if (_clicked is { } clicked)
        {
            selection.SetActive(clicked);
        }

        (_visible, _drawnBefore) = (_drawnBefore, _visible);

        // Empty space: a drop here makes the dragged entities roots, a click clears the selection (the multi-select does), a drag draws the selection box, a right click creates.
        EmptySpace(ctx, locked);

        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !locked && project.Selection is { } selected)
        {
            if (ctx.Pressed("edit.delete", KeyContext.Hierarchy))
            {
                project.Delete(selection.TopLevel());
            }
            else if (ctx.Pressed("entity.rename", KeyContext.Hierarchy))
            {
                BeginRename(world, selected);
            }
        }

        Ui.End();
    }

    /// <summary>
    /// Translates what ImGui's multi-select asked for (select all or none, select or deselect a range of items) into <see cref="SelectionRequest"/>s and applies them. <paramref name="visible"/> is the list the
    /// item indexes were numbered from.
    /// </summary>
    private static void ApplyRequests(ImGuiMultiSelectIO* io, Selection selection, IReadOnlyList<Entity> visible)
    {
        var requests = new List<SelectionRequest>(io->Requests.Size);
        for (int i = 0; i < io->Requests.Size; i++)
        {
            ImGuiSelectionRequest request = io->Requests.Data[i];
            bool selected = request.Selected == true;
            if (request.Type == ImGuiSelectionRequestType.SetAll)
            {
                requests.Add(SelectionRequest.SetAll(selected));
            }
            else if (request.Type == ImGuiSelectionRequestType.SetRange)
            {
                requests.Add(SelectionRequest.SetRange(selected, (int)request.RangeFirstItem, (int)request.RangeLastItem, request.RangeDirection));
            }
        }

        SelectionRequests.Apply(selection, visible, requests);
    }

    /// <summary>The entities a drag carried: one under "ENTITY", several under "ENTITIES" (their ids in a row). Null when nothing was dropped.</summary>
    private static List<Entity>? AcceptEntities()
    {
        if (Gui.AcceptId("ENTITY") is { } one)
        {
            return [new Entity(one)];
        }

        if (Gui.AcceptPayload("ENTITIES") is { } bytes)
        {
            var entities = new List<Entity>(bytes.Length / 8);
            for (int i = 0; i + 8 <= bytes.Length; i += 8)
            {
                entities.Add(new Entity(BitConverter.ToUInt64(bytes, i)));
            }

            return entities;
        }

        return null;
    }

    private static void EmptySpace(EditorContext ctx, bool locked)
    {
        // The space below the nodes is not an item (ImGui starts the selection box from empty space, and an item would be in the way): the drop target is a rectangle.
        Vector2 size = Gui.Available;
        if (size.Y < 8)
        {
            size.Y = 8;
        }

        Vector2 min = Gui.CursorScreen;
        var area = new ImRect { Min = Gui.V(min), Max = Gui.V(min + size) };
        uint dropId;
        fixed (byte* name = Weft.Interop.Utf8.ToZeroTerminated("##empty_drop"))
        {
            dropId = ImGui.GetIDStr(name);
        }

        if (!locked && ImGui.BeginDragDropTargetCustom(area, dropId))
        {
            if (AcceptEntities() is { } dropped)
            {
                ctx.Project.Reparent(dropped, default);
            }

            Gui.EndDropTarget();
        }

        if (!locked && Gui.BeginContextMenu("empty_menu", onItem: false))
        {
            if (Gui.MenuItem("Paste", ctx.Shortcut("edit.paste"), false, ClipboardScene.LooksLikeScene(ctx.Clipboard.GetText())))
            {
                ctx.Project.Paste(ctx.Clipboard, default(Entity));
            }

            Ui.Separator();
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

        Selection selection = project.Selected;
        if (selection.Contains(entity))
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
            ImGui.SetNextItemSelectionUserData(_visible.Count);     // the index in the list of visible nodes, which is what a request names
            _visible.Add(entity);
            open = Gui.TreeNode(name, flags);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
            {
                _clicked = entity;
            }

            if (!locked && ImGui.IsItemHovered(ImGuiHoveredFlags.None) && Gui.MouseClicked(ImGuiMouseButton.Left) == false && ImGui.IsMouseDoubleClickedNil(ImGuiMouseButton.Left))
            {
                BeginRename(world, entity);
            }

            if (!locked && Gui.BeginDragSource())
            {
                // Dragging a selected node carries the whole selection (what has no selected ancestor); an unselected one goes alone.
                IReadOnlyList<Entity> carried = selection.Contains(entity) ? selection.TopLevel() : [entity];
                if (carried.Count == 1)
                {
                    Gui.PayloadId("ENTITY", carried[0].Id);
                    Ui.Text(name);
                }
                else
                {
                    byte[] ids = new byte[carried.Count * 8];
                    for (int i = 0; i < carried.Count; i++)
                    {
                        BitConverter.TryWriteBytes(ids.AsSpan(i * 8), carried[i].Id);
                    }

                    Gui.SetPayload("ENTITIES", ids);
                    Ui.Text($"{carried.Count} entities");
                }

                Gui.EndDragSource();
            }

            if (!locked && Gui.BeginDropTarget())
            {
                if (AcceptEntities() is { } dropped)
                {
                    project.Reparent(dropped, entity);
                }

                Gui.EndDropTarget();
            }

            if (!locked && Gui.BeginContextMenu())
            {
                if (!selection.Contains(entity))
                {
                    selection.Set(entity);
                }

                if (Gui.MenuItem("Rename", ctx.Shortcut("entity.rename")))
                {
                    BeginRename(world, entity);
                }

                Ui.Separator();
                bool canPaste = ClipboardScene.LooksLikeScene(ctx.Clipboard.GetText());
                if (Gui.MenuItem("Cut", ctx.Shortcut("edit.cut")))
                {
                    project.Cut(ctx.Clipboard);
                }

                if (Gui.MenuItem("Copy", ctx.Shortcut("edit.copy")))
                {
                    project.Copy(ctx.Clipboard);
                }

                if (Gui.MenuItem("Paste", ctx.Shortcut("edit.paste"), false, canPaste))
                {
                    project.Paste(ctx.Clipboard, world.GetParent(entity));
                }

                if (Gui.MenuItem("Paste as child", ctx.Shortcut("edit.paste_child"), false, canPaste))
                {
                    project.Paste(ctx.Clipboard, entity);
                }

                if (Gui.MenuItem("Duplicate", ctx.Shortcut("edit.duplicate")))
                {
                    project.Duplicate(selection.TopLevel());
                }

                Ui.Separator();

                if (Gui.MenuItem(selection.Count > 1 ? $"Delete {selection.Count} entities" : "Delete", ctx.Shortcut("edit.delete")))
                {
                    project.Delete(selection.TopLevel());
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
