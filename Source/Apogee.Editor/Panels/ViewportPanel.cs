using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.DevTools;
using Apogee.Ecs;
using Apogee.Editor.Editing;
using Apogee.Editor.Input;
using Apogee.Graphics;
using Apogee.Host.Editing;
using Apogee.Native.CImGui;
using Apogee.Native.ImGuizmo;
using Apogee.Reflection;
using Apogee.Rendering;

namespace Apogee.Editor.Panels;

/// <summary>
/// The world as the viewport image: the scene through the editor's camera, a gizmo on the selection, a view cube, picking by click, and drag and drop of models. While playing it shows the game, and the
/// game gets the pointer and the keyboard when the viewport has them.
/// </summary>
public sealed unsafe class ViewportPanel
{
    private GizmoDrag? _drag;               // the gizmo drag's life, over the project's history
    private Vector2 _pressAt;
    private Vector2? _boxFrom;      // where a selection box was started, while the left button is held

    /// <summary>True when the panel asked for an image this frame, so the app records the scene into it.</summary>
    public bool Wanted { get; private set; }

    /// <summary>The viewport's rectangle on the screen, in points.</summary>
    public (Vector2 Min, Vector2 Size) Rect { get; private set; }

    /// <summary>The whole panel (toolbar and image) on the screen, in points, as it was drawn last; null when it was not drawn (hidden behind another tab). Where a file dropped from the system is routed.</summary>
    public (Vector2 Min, Vector2 Size)? WindowRect { get; private set; }

    /// <summary>
    /// The point in the world that the window point <paramref name="point"/> (clamped to the image) looks at: where the ray through it meets the ground, else five metres along the ray. Null when the
    /// panel has no image this frame. Used for a model dropped from the system, which is placed where it was let go.
    /// </summary>
    public Double3? PlacementAt(EditorContext ctx, Vector2 point)
    {
        if (!Wanted || ctx.Viewport is not { } renderer || Rect.Size.X < 1 || Rect.Size.Y < 1)
        {
            return null;
        }

        return Place(renderer, Vector2.Clamp(point, Rect.Min, Rect.Min + Rect.Size) - Rect.Min, Rect.Size);
    }

    private static Double3 Place(ViewportRenderer renderer, Vector2 local, Vector2 size)
    {
        PickRay ray = Picking.RayThrough(renderer.Camera, local, size);
        return Picking.HitGround(ray) ?? ray.Origin + (Double3)(ray.Direction * 5f);
    }

    public void Draw(EditorContext ctx, Frame? frame, float density, float deltaSeconds)
    {
        Wanted = false;
        WindowRect = null;
        ImGui.PushStyleVarVec2(ImGuiStyleVar.WindowPadding, new ImVec2());
        bool visible = Ui.Begin("Viewport", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar(1);
        if (!visible)
        {
            ctx.ViewportHovered = false;
            ctx.ViewportFocused = false;
            Ui.End();
            return;
        }

        WindowRect = (Gui.N(ImGui.GetWindowPos()), Gui.N(ImGui.GetWindowSize()));
        DrawOptions(ctx);

        Vector2 size = Gui.Available;
        Vector2 min = Gui.CursorScreen;
        Rect = (min, size);
        ViewportRenderer? renderer = ctx.Viewport;
        if (renderer is null || frame is null || size.X < 16 || size.Y < 16)
        {
            Gui.InvisibleButton("##viewport", new Vector2(Math.Max(size.X, 1), Math.Max(size.Y, 1)));
            ctx.ViewportHovered = false;
            Ui.End();
            return;
        }

        uint index = renderer.Acquire(frame, (uint)(size.X * density), (uint)(size.Y * density));
        Wanted = true;
        ImGui.Image(new ImTextureRef { TexID = index }, Gui.V(size), new ImVec2 { X = 0, Y = 0 }, new ImVec2 { X = 1, Y = 1 });
        bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.None);
        ctx.ViewportHovered = hovered;
        ctx.ViewportFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        HandleDrop(ctx, renderer, min, size);

        EditorProject project = ctx.Project;
        bool gameView = project.IsPlaying && renderer.UsedGameCamera;
        if (project.IsPlaying)
        {
            ForwardToGame(ctx, min, density, hovered);
        }

        if (!gameView)
        {
            bool gizmoBusy = DrawGizmo(ctx, renderer, min, size, density);
            Navigate(ctx, renderer, hovered, gizmoBusy, size, min, deltaSeconds);
        }

        Ui.End();
    }

    private static void DrawOptions(EditorContext ctx)
    {
        GizmoSettings g = ctx.Gizmo;
        void Mode(string label, string action, AgzOperation op)
        {
            label = ctx.Shortcut(action) is { } shortcut ? $"{label} ({shortcut})" : label; // follows a rebinding
            bool on = g.Operation == (int)op;
            if (on)
            {
                ImGui.PushStyleColorVec4(ImGuiCol.Button, new ImVec4 { X = 0.2f, Y = 0.45f, Z = 0.75f, W = 1f });
            }

            if (Gui.SmallButton(label))
            {
                g.Operation = (int)op;
            }

            if (on)
            {
                ImGui.PopStyleColor(1);
            }

            Gui.SameLine();
        }

        Mode("Move", "gizmo.move", AgzOperation.Translate);
        Mode("Rotate", "gizmo.rotate", AgzOperation.Rotate);
        Mode("Scale", "gizmo.scale", AgzOperation.Scale);
        bool world = g.World;
        if (Gui.Checkbox("World", ref world))
        {
            g.World = world;
        }

        Gui.SameLine();
        int pivot = (int)g.Pivot;
        ImGui.SetNextItemWidth(110);
        if (Ui.Combo("##pivot", ref pivot, "Pivot: Center", "Pivot: Active", "Pivot: Individual"))
        {
            g.Pivot = (PivotMode)pivot;
        }

        Gui.SameLine();
        bool snap = g.Snap;
        if (Gui.Checkbox("Snap", ref snap))
        {
            g.Snap = snap;
        }

        Gui.SameLine();
        bool helpers = g.ShowHelpers;
        if (Gui.Checkbox("Helpers", ref helpers))
        {
            g.ShowHelpers = helpers;
        }

        if (ctx.Project.IsPlaying)
        {
            Gui.SameLine();
            bool game = g.PreferGameCamera;
            if (Gui.Checkbox("Game camera", ref game))
            {
                g.PreferGameCamera = game;
            }
        }
    }

    private static void HandleDrop(EditorContext ctx, ViewportRenderer renderer, Vector2 min, Vector2 size)
    {
        if (ctx.Project.IsPlaying || !Gui.BeginDropTarget())
        {
            return;
        }

        if (Gui.AcceptText("ASSET") is { } path && (path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Project.CreateModel(path, Place(renderer, Gui.Mouse - min, size));
        }

        Gui.EndDropTarget();
    }

    /// <summary>While playing, the game sees the pointer over the viewport: position, buttons and the wheel (keys go through the input actions).</summary>
    private static void ForwardToGame(EditorContext ctx, Vector2 min, float density, bool hovered)
    {
        Apogee.UI.UiSystem ui = ctx.Project.Session.Ui;
        if (!hovered)
        {
            ui.MouseLeave();
            return;
        }

        Vector2 local = (Gui.Mouse - min) * density;
        ui.MouseMove((int)local.X, (int)local.Y);
        for (int button = 0; button < 3; button++)
        {
            var imgui = (ImGuiMouseButton)button;
            int rml = button == 0 ? 0 : button == 1 ? 1 : 2;
            if (Gui.MouseClicked(imgui))
            {
                ui.MouseButton(rml, true);
            }
            else if (Gui.MouseReleased(imgui))
            {
                ui.MouseButton(rml, false);
            }
        }

        float wheel = ctx.Host.IO->MouseWheel;
        if (wheel != 0)
        {
            ui.MouseWheel(0, -wheel);
        }
    }

    private bool DrawGizmo(EditorContext ctx, ViewportRenderer renderer, Vector2 min, Vector2 size, float density)
    {
        EditorProject project = ctx.Project;
        World world = project.World;
        Camera camera = renderer.Camera;
        float aspect = size.X / size.Y;
        Matrix4x4 view = camera.View;
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(camera.VerticalFov, aspect, MathF.Max(camera.Near, 0.01f), 2000f);

        Gizmo.SetOrthographic(false);
        Gizmo.SetDrawlist(ImGui.GetWindowDrawList());
        Gizmo.SetRect(min.X, min.Y, size.X, size.Y);

        bool busy = false;
        GizmoSettings g = ctx.Gizmo;
        bool scaling = g.Operation == (int)AgzOperation.Scale;
        GizmoDrag drag = _drag ??= new GizmoDrag(project.History);

        // The drag's life (captured idle every frame, started when ImGuizmo reports use, dropped and sealed when cut short) is GizmoDrag's; see there.
        TransformGesture? gesture = drag.Prepare(world, project.Selected.Version, !project.IsPlaying, Gui.MouseDown(ImGuiMouseButton.Left), () =>
        {
            ModelLibrary models = project.Session.Models;
            return TransformGesture.Begin(world, project.Selected.TopLevel(), project.Selected.Active, g.Pivot, !g.World || scaling, id => EntityBounds(world, models, id));
        });

        if (gesture is not null)
        {
            Matrix4x4 sent = drag.Pivot.ToMatrix(camera.Position);
            Matrix4x4 model = sent;
            float* snap = stackalloc float[3];
            float step = g.Operation == (int)AgzOperation.Rotate ? g.RotateStep : scaling ? g.ScaleStep : g.MoveStep;
            snap[0] = snap[1] = snap[2] = step;
            bool changed = Gizmo.Manipulate((float*)&view, (float*)&projection, g.Operation, g.World ? (int)AgzMode.World : (int)AgzMode.Local, (float*)&model, null, g.Snap ? snap : null, null, null);
            bool using3d = Gizmo.IsUsing();
            drag.Observe(using3d);

            if (changed && drag.Active is { } active)
            {
                // ImGuizmo works in floats relative to the camera. Of what it gives back only the part this operation changes is used (a move: the difference of the translations, added to the double position), so
                // rounding noise never moves or turns the group.
                GroupOperation operation = g.Operation == (int)AgzOperation.Rotate ? GroupOperation.Rotate : scaling ? GroupOperation.Scale : GroupOperation.Move;
                Trs pivot = GroupTransform.PivotAfter(drag.Pivot, operation, sent, model);
                drag.MoveTo(pivot);
                string verb = operation == GroupOperation.Rotate ? "Rotate" : scaling ? "Scale" : "Move";
                string what = active.Members.Count == 1 ? world.GetName(active.Members[0]) ?? "entity" : $"{active.Members.Count} entities";
                SetTransformsCommand? command = SetTransformsCommand.Create(project.WorldRef, active.Evaluate(pivot).Select(e => (e.Entity, e.Local)), $"{verb} {what}");
                if (command is not null)
                {
                    project.History.Execute(command);
                }
            }

            busy = using3d || Gizmo.IsOver();
        }

        // The view cube: clicking a face turns the camera to look along that axis.
        Matrix4x4 before = view;
        Gizmo.ViewManipulate((float*)&view, ctx.Camera.Distance, min.X + size.X - 96, min.Y + 6, 90, 90, 0x20101010);
        if (view != before)
        {
            Vector3 forward = -new Vector3(view.M13, view.M23, view.M33);
            ctx.Camera.Yaw = MathF.Atan2(-forward.X, -forward.Z);
            ctx.Camera.Pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        }

        return busy || Gizmo.IsUsingViewManipulate() || Gizmo.IsViewManipulateHovered();
    }

    /// <summary>The bounds of an entity's model in its own space, or null for an entity with no model (or one that does not load).</summary>
    private static (Vector3 Min, Vector3 Max)? EntityBounds(World world, ModelLibrary models, Entity entity) =>
        world.TryGet(entity, out ModelRenderer renderer) ? models.BoundsOf(renderer.Model) : null;

    /// <summary>
    /// A selection box dragged from empty space: drawn while the left button is down (Escape or another button cancels it), and on release it selects what <see cref="Picking.InRect"/> finds (Ctrl toggles
    /// those, Shift adds them, otherwise they replace the selection). A press that did not move is a click, which <see cref="Navigate"/> handles.
    /// </summary>
    private void DrawSelectionBox(EditorContext ctx, ViewportRenderer renderer, Vector2 min, Vector2 size)
    {
        if (_boxFrom is not { } from)
        {
            return;
        }

        bool cancelled = Gui.MouseDown(ImGuiMouseButton.Right) || Gui.MouseDown(ImGuiMouseButton.Middle) || ctx.Pressed("selection.clear", KeyContext.Viewport);
        bool released = Gui.MouseReleased(ImGuiMouseButton.Left);
        if (cancelled || (!released && !Gui.MouseDown(ImGuiMouseButton.Left)))
        {
            _boxFrom = null;
            return;
        }

        Vector2 to = Vector2.Clamp(Gui.Mouse, min, min + size);
        if (Vector2.Distance(to, from) < 4f)
        {
            if (released)
            {
                _boxFrom = null;
            }

            return;
        }

        Vector2 low = Vector2.Min(from, to);
        Vector2 high = Vector2.Max(from, to);
        if (!released)
        {
            ImDrawList* draw = ImGui.GetWindowDrawList();
            ImGui.ImDrawListAddRectFilled(draw, Gui.V(low), Gui.V(high), 0x3028AAFF, 0f, ImDrawFlags.None);      // ImGui colours are 0xAABBGGRR: orange
            ImGui.ImDrawListAddRect(draw, Gui.V(low), Gui.V(high), 0xFF28AAFF, 0f, 1f, ImDrawFlags.None);
            return;
        }

        _boxFrom = null;
        ModelLibrary models = ctx.Project.Session.Models;
        List<Entity> inside = Picking.InRect(ctx.Project.World, renderer.Camera, size, low - min, high - min, id => models.BoundsOf(id));
        Selection selection = ctx.Project.Selected;
        if (Gui.Ctrl)
        {
            foreach (Entity entity in inside)
            {
                selection.Toggle(entity);
            }
        }
        else if (Gui.Shift)
        {
            foreach (Entity entity in inside)
            {
                selection.Add(entity);
            }
        }
        else
        {
            selection.Set(inside);
        }
    }

    private void Navigate(EditorContext ctx, ViewportRenderer renderer, bool hovered, bool gizmoBusy, Vector2 size, Vector2 min, float dt)
    {
        EditorCamera camera = ctx.Camera;
        ImGuiIO* io = ctx.Host.IO;
        Vector2 delta = Gui.N(io->MouseDelta);
        bool rmb = Gui.MouseDown(ImGuiMouseButton.Right);
        if (hovered && Gui.MouseClicked(ImGuiMouseButton.Left))
        {
            _pressAt = Gui.Mouse;
        }

        if (hovered && !gizmoBusy)
        {
            if (rmb)
            {
                camera.Orbit(-delta.X * 0.005f, -delta.Y * 0.005f);
                float speed = (float)ctx.Preferences.CameraSpeed * (Gui.Shift ? 3f : 1f);
                Vector3 move = default;
                if (ctx.IsHeld("view.fly_forward", KeyContext.ViewportFly)) { move.Z += speed; }
                if (ctx.IsHeld("view.fly_back", KeyContext.ViewportFly)) { move.Z -= speed; }
                if (ctx.IsHeld("view.fly_right", KeyContext.ViewportFly)) { move.X += speed; }
                if (ctx.IsHeld("view.fly_left", KeyContext.ViewportFly)) { move.X -= speed; }
                if (ctx.IsHeld("view.fly_up", KeyContext.ViewportFly)) { move.Y += speed; }
                if (ctx.IsHeld("view.fly_down", KeyContext.ViewportFly)) { move.Y -= speed; }
                camera.Fly(move, dt);
            }
            else if (Gui.MouseDown(ImGuiMouseButton.Middle))
            {
                float scale = 2f * MathF.Tan(camera.FovDegrees * MathF.PI / 360f) / size.Y;
                camera.Pan(delta.X * scale, delta.Y * scale);
            }

            if (io->MouseWheel != 0)
            {
                camera.Zoom(io->MouseWheel);
            }

            // A click that did not drag picks what is under the pointer: Ctrl toggles it, Shift adds it, and a click on nothing clears the selection (unless one of them is held).
            if (Gui.MouseReleased(ImGuiMouseButton.Left) && Vector2.Distance(Gui.Mouse, _pressAt) < 4f)
            {
                PickRay ray = Picking.RayThrough(renderer.Camera, Gui.Mouse - min, size);
                ModelLibrary models = ctx.Project.Session.Models;
                Entity? hit = Picking.Pick(ctx.Project.World, ray, id => models.BoundsOf(id));
                Selection selection = ctx.Project.Selected;
                if (hit is { } picked)
                {
                    if (Gui.Ctrl)
                    {
                        selection.Toggle(picked);
                    }
                    else if (Gui.Shift)
                    {
                        selection.Add(picked);
                    }
                    else
                    {
                        selection.Set(picked);
                    }
                }
                else if (!Gui.Ctrl && !Gui.Shift)
                {
                    selection.Clear();
                }
            }

            // A left press on empty space (no entity, not the gizmo) starts a selection box.
            if (Gui.MouseClicked(ImGuiMouseButton.Left) && !gizmoBusy && !rmb)
            {
                PickRay ray = Picking.RayThrough(renderer.Camera, Gui.Mouse - min, size);
                ModelLibrary models = ctx.Project.Session.Models;
                _boxFrom = Picking.Pick(ctx.Project.World, ray, id => models.BoundsOf(id)) is null ? Gui.Mouse : null;
            }
        }

        bool boxing = _boxFrom is not null;
        DrawSelectionBox(ctx, renderer, min, size);

        // Escape cancels a box being dragged; otherwise it clears the selection. The keymap says which keys; the viewport has to be focused and not flying.
        KeyContext keys = ctx.ViewportFocused && !rmb ? KeyContext.Viewport : KeyContext.Global;
        if (!boxing && ctx.Pressed("selection.clear", keys))
        {
            ctx.Project.Selected.Clear();
        }

        if (!ctx.Project.IsPlaying)
        {
            if (ctx.Pressed("gizmo.move", keys)) { ctx.Gizmo.Operation = (int)AgzOperation.Translate; }
            if (ctx.Pressed("gizmo.rotate", keys)) { ctx.Gizmo.Operation = (int)AgzOperation.Rotate; }
            if (ctx.Pressed("gizmo.scale", keys)) { ctx.Gizmo.Operation = (int)AgzOperation.Scale; }
            if (ctx.Pressed("view.frame", keys) && ctx.Project.Selection is { } selected && ctx.Project.World.TryGet(selected, out WorldTransform placed))
            {
                camera.Frame(placed.Position, 2f);
            }
        }
    }
}
