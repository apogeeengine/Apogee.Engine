using System;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.DevTools;
using Apogee.Ecs;
using Apogee.Editor.Editing;
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
    private bool _gizmoWasUsing;
    private Vector2 _pressAt;

    /// <summary>True when the panel asked for an image this frame, so the app records the scene into it.</summary>
    public bool Wanted { get; private set; }

    /// <summary>The viewport's rectangle on the screen, in points.</summary>
    public (Vector2 Min, Vector2 Size) Rect { get; private set; }

    public void Draw(EditorContext ctx, Frame? frame, float density, float deltaSeconds)
    {
        Wanted = false;
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
        void Mode(string label, AgzOperation op)
        {
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

        Mode("Move (W)", AgzOperation.Translate);
        Mode("Rotate (E)", AgzOperation.Rotate);
        Mode("Scale (R)", AgzOperation.Scale);
        bool world = g.World;
        if (Gui.Checkbox("World", ref world))
        {
            g.World = world;
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
            PickRay ray = Picking.RayThrough(renderer.Camera, Gui.Mouse - min, size);
            Double3 at = Picking.HitGround(ray) ?? ray.Origin + (Double3)(ray.Direction * 5f);
            ctx.Project.CreateModel(path, at);
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
        if (project.Selection is { } entity && !project.IsPlaying && world.Has<Transform>(entity))
        {
            Trs placed = world.ComputeWorldTransform(entity);
            Matrix4x4 model = placed.ToMatrix(camera.Position);
            GizmoSettings g = ctx.Gizmo;
            float* snap = stackalloc float[3];
            float step = g.Operation == (int)AgzOperation.Rotate ? g.RotateStep : g.Operation == (int)AgzOperation.Scale ? g.ScaleStep : g.MoveStep;
            snap[0] = snap[1] = snap[2] = step;
            bool changed = Gizmo.Manipulate((float*)&view, (float*)&projection, g.Operation, g.World ? (int)AgzMode.World : (int)AgzMode.Local, (float*)&model, null, g.Snap ? snap : null, null, null);
            if (changed && Matrix4x4.Decompose(model, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
            {
                bool scaling = g.Operation == (int)AgzOperation.Scale;
                var world3 = new Trs(camera.Position + (Double3)translation, rotation, scaling ? scale : placed.Scale);
                Entity parent = world.GetParent(entity);
                Trs local = parent.IsNull ? world3 : world3.InLocalOf(world.ComputeWorldTransform(parent));
                var transform = Transform.From(local);
                Entity id = world.Component<Transform>();
                SetComponentCommand? command = SetComponentCommand.Create(project.WorldRef, entity, id, new ReadOnlySpan<byte>(&transform, sizeof(Transform)), $"Move {world.GetName(entity) ?? "entity"}");
                if (command is not null)
                {
                    project.History.Execute(command);
                }
            }

            bool using3d = Gizmo.IsUsing();
            if (_gizmoWasUsing && !using3d)
            {
                project.History.Seal();
            }

            _gizmoWasUsing = using3d;
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
                float speed = (Gui.Shift ? 18f : 6f);
                Vector3 move = default;
                if (Gui.KeyDown(ImGuiKey.ImGuiKeyW)) { move.Z += speed; }
                if (Gui.KeyDown(ImGuiKey.ImGuiKeyS)) { move.Z -= speed; }
                if (Gui.KeyDown(ImGuiKey.ImGuiKeyD)) { move.X += speed; }
                if (Gui.KeyDown(ImGuiKey.ImGuiKeyA)) { move.X -= speed; }
                if (Gui.KeyDown(ImGuiKey.ImGuiKeyE)) { move.Y += speed; }
                if (Gui.KeyDown(ImGuiKey.ImGuiKeyQ)) { move.Y -= speed; }
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

            // A click that did not drag picks what is under the pointer.
            if (Gui.MouseReleased(ImGuiMouseButton.Left) && Vector2.Distance(Gui.Mouse, _pressAt) < 4f)
            {
                PickRay ray = Picking.RayThrough(renderer.Camera, Gui.Mouse - min, size);
                ModelLibrary models = ctx.Project.Session.Models;
                Entity? hit = Picking.Pick(ctx.Project.World, ray, id => models.BoundsOf(id));
                if (hit is not null || !Gui.Ctrl)
                {
                    ctx.Project.Selection = hit;
                }
            }
        }

        if (ctx.ViewportFocused && !rmb && !ctx.Host.WantsKeyboard && !ctx.Project.IsPlaying)
        {
            if (Gui.KeyPressed(ImGuiKey.ImGuiKeyW)) { ctx.Gizmo.Operation = (int)AgzOperation.Translate; }
            if (Gui.KeyPressed(ImGuiKey.ImGuiKeyE)) { ctx.Gizmo.Operation = (int)AgzOperation.Rotate; }
            if (Gui.KeyPressed(ImGuiKey.ImGuiKeyR)) { ctx.Gizmo.Operation = (int)AgzOperation.Scale; }
            if (Gui.KeyPressed(ImGuiKey.ImGuiKeyF) && ctx.Project.Selection is { } selected && ctx.Project.World.TryGet(selected, out WorldTransform placed))
            {
                camera.Frame(placed.Position, 2f);
            }
        }
    }
}
