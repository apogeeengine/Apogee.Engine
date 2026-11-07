using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Reflection;
using Apogee.Rendering;

namespace Apogee.Editor.Editing;

/// <summary>A ray in the world: the origin is a position (double), the direction a unit vector.</summary>
public readonly record struct PickRay(Double3 Origin, Vector3 Direction);

/// <summary>What is under a point of the viewport: a ray through the pixel, tested against the entities' bounds on the CPU.</summary>
public static class Picking
{
    /// <summary>How big an entity that has no model is for picking (a light, a camera, an empty): a sphere of this radius, in meters, around its position.</summary>
    public const float MarkerRadius = 0.3f;

    /// <summary>
    /// The ray through a pixel of the viewport (<paramref name="pixel"/> from its top left corner, <paramref name="size"/> its size in pixels), seen by the camera. The camera looks down its
    /// forward axis; the viewport's y grows downward.
    /// </summary>
    public static PickRay RayThrough(Camera camera, Vector2 pixel, Vector2 size)
    {
        float aspect = size.X / MathF.Max(size.Y, 1f);
        float tan = MathF.Tan(camera.VerticalFov * 0.5f);
        float x = ((2f * pixel.X / MathF.Max(size.X, 1f)) - 1f) * tan * aspect;
        float y = (1f - (2f * pixel.Y / MathF.Max(size.Y, 1f))) * tan;
        Vector3 forward = camera.Forward;
        Vector3 right = camera.Right;
        Vector3 up = Vector3.Cross(right, forward);
        return new PickRay(camera.Position, Vector3.Normalize(forward + (right * x) + (up * y)));
    }

    /// <summary>
    /// The distance along the ray at which it enters an oriented box (<paramref name="min"/> to <paramref name="max"/> in the box's own space, placed in the world by <paramref name="placement"/>), or
    /// null when it misses. A non-uniform scale is exact when the box is not rotated against it, as everywhere (see <see cref="Trs"/>).
    /// </summary>
    public static float? HitBox(PickRay ray, in Trs placement, Vector3 min, Vector3 max)
    {
        Quaternion inverse = Quaternion.Inverse(placement.Rotation);
        Vector3 origin = placement.InverseTransformPoint(ray.Origin);
        Vector3 direction = Vector3.Transform(ray.Direction, inverse) / placement.Scale;

        float near = 0f;
        float far = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = Component(origin, axis);
            float d = Component(direction, axis);
            float lo = Component(min, axis);
            float hi = Component(max, axis);
            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < lo || o > hi)
                {
                    return null;
                }

                continue;
            }

            float t1 = (lo - o) / d;
            float t2 = (hi - o) / d;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            near = MathF.Max(near, t1);
            far = MathF.Min(far, t2);
            if (near > far)
            {
                return null;
            }
        }

        return near;
    }

    /// <summary>The distance along the ray at which it enters a sphere, or null.</summary>
    public static float? HitSphere(PickRay ray, Double3 center, float radius)
    {
        Vector3 toCenter = center.RelativeTo(ray.Origin);
        float along = Vector3.Dot(toCenter, ray.Direction);
        float perpendicularSquared = toCenter.LengthSquared() - (along * along);
        float radiusSquared = radius * radius;
        if (perpendicularSquared > radiusSquared)
        {
            return null;
        }

        float inside = MathF.Sqrt(radiusSquared - perpendicularSquared);
        float entry = along - inside;
        float exit = along + inside;
        return exit < 0 ? null : MathF.Max(entry, 0f);
    }

    /// <summary>
    /// The user entity the ray hits first. An entity with a <see cref="ModelRenderer"/> is its model's box (from <paramref name="boundsOf"/>, which says null for a model it cannot measure: that one
    /// counts as a marker); every other entity with a <see cref="WorldTransform"/> is a small sphere. Null when nothing is hit.
    /// </summary>
    public static Entity? Pick(World world, PickRay ray, Func<uint, (Vector3 Min, Vector3 Max)?> boundsOf, IEnumerable<Entity>? candidates = null)
    {
        Entity? best = null;
        float bestDistance = float.MaxValue;
        foreach (Entity entity in candidates ?? world.UserEntities())
        {
            if (!world.TryGet(entity, out WorldTransform placed))
            {
                continue;
            }

            Trs placement = placed.ToTrs();
            float? hit = null;
            if (world.TryGet(entity, out ModelRenderer renderer) && boundsOf(renderer.Model) is { } bounds)
            {
                hit = HitBox(ray, placement, bounds.Min, bounds.Max);
            }
            else
            {
                hit = HitSphere(ray, placement.Position, MarkerRadius);
            }

            if (hit is { } distance && distance < bestDistance)
            {
                best = entity;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>Where the ray meets the ground plane (y = 0), or null when it points away from it or along it: where a model dropped into the viewport lands.</summary>
    public static Double3? HitGround(PickRay ray)
    {
        if (MathF.Abs(ray.Direction.Y) < 1e-6f)
        {
            return null;
        }

        double t = -ray.Origin.Y / ray.Direction.Y;
        return t < 0 ? null : ray.Origin + (Double3)(ray.Direction * (float)t);
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
