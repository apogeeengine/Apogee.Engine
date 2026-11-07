using System;
using System.Numerics;
using Apogee.Core.Graphics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Reflection;
using Apogee.Rendering;

namespace Apogee.Editor;

/// <summary>What the editor draws into the viewport besides the scene, as debug lines: a ground grid, the selection's bounds, and a mark for lights and cameras (which have no model to see).</summary>
public static class EditorHelpers
{
    private static readonly uint GridColor = DebugColor.Rgba(90, 90, 100, 120);
    private static readonly uint SelectionColor = DebugColor.Rgba(255, 170, 40);

    public static void Draw(World world, ModelLibrary models, DebugDraw debug, Entity? selection, Double3 around)
    {
        DrawGrid(debug, around);
        foreach (Entity entity in world.UserEntities())
        {
            if (!world.TryGet(entity, out WorldTransform placed))
            {
                continue;
            }

            Trs placement = placed.ToTrs();
            bool selected = selection == entity;
            if (world.TryGet(entity, out Light light))
            {
                uint color = DebugColor.FromVector(new Vector4(light.Color, 1f));
                if (light.Kind == (byte)Apogee.Content.LightKind.Point)
                {
                    debug.Sphere(placement.Position, 0.2f, color, 12);
                }
                else
                {
                    Vector3 forward = Vector3.Transform(-Vector3.UnitZ, placement.Rotation);
                    debug.Arrow(placement.Position, placement.Position + (Double3)(forward * 1.5f), color, 0.25f);
                }
            }

            if (world.Has<CameraComponent>(entity))
            {
                DrawCameraMark(debug, placement, world.Has<MainCamera>(entity) ? DebugColor.Cyan : DebugColor.Gray);
            }

            if (selected)
            {
                DrawSelection(world, models, debug, entity, placement);
            }
        }
    }

    private static void DrawGrid(DebugDraw debug, Double3 around)
    {
        const int half = 20;
        double cx = Math.Round(around.X);
        double cz = Math.Round(around.Z);
        for (int i = -half; i <= half; i++)
        {
            uint colorX = i == 0 ? DebugColor.Blue : GridColor;
            uint colorZ = i == 0 ? DebugColor.Red : GridColor;
            debug.Line(new Double3(cx + i, 0, cz - half), new Double3(cx + i, 0, cz + half), colorX);
            debug.Line(new Double3(cx - half, 0, cz + i), new Double3(cx + half, 0, cz + i), colorZ);
        }
    }

    private static void DrawCameraMark(DebugDraw debug, Trs placement, uint color)
    {
        // A pyramid along -Z: the camera's apex and the rectangle it sees two meters ahead.
        Double3 apex = placement.Position;
        Double3 Corner(float x, float y) => placement.TransformPoint(new Vector3(x, y, -2f));
        Double3 a = Corner(-1.2f, 0.7f);
        Double3 b = Corner(1.2f, 0.7f);
        Double3 c = Corner(1.2f, -0.7f);
        Double3 d = Corner(-1.2f, -0.7f);
        debug.Line(apex, a, color);
        debug.Line(apex, b, color);
        debug.Line(apex, c, color);
        debug.Line(apex, d, color);
        debug.Line(a, b, color);
        debug.Line(b, c, color);
        debug.Line(c, d, color);
        debug.Line(d, a, color);
    }

    private static void DrawSelection(World world, ModelLibrary models, DebugDraw debug, Entity entity, Trs placement)
    {
        if (world.TryGet(entity, out ModelRenderer renderer) && models.BoundsOf(renderer.Model) is { } bounds)
        {
            Vector3 center = (bounds.Min + bounds.Max) * 0.5f;
            Vector3 half = (bounds.Max - bounds.Min) * 0.5f * placement.Scale;
            debug.Box(placement.TransformPoint(center), half, placement.Rotation, SelectionColor);
        }
        else
        {
            debug.Sphere(placement.Position, Apogee.Editor.Editing.Picking.MarkerRadius, SelectionColor, 16);
        }
    }
}
