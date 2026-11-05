using System;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Native.Jolt;
using Apogee.Reflection;
using JoltApi = Apogee.Native.Jolt.Jolt;

namespace Apogee.Physics;

public sealed unsafe partial class PhysicsWorld
{
    private static uint NoBody => unchecked((uint)JoltApi.InvalidBody);

    private uint IgnoredBody(Entity ignore) => ignore.Id == 0 ? NoBody : RecordOf(ignore)?.BodyId ?? NoBody;

    /// <summary>
    /// Casts a ray and returns the nearest hit: whether there was one, the entity, the hit point, the surface normal and the distance. <paramref name="mask"/> picks the collision layers
    /// that count; <paramref name="ignore"/> is an entity (the shooter's own body) the ray passes through. Sensors are never hit.
    /// </summary>
    [Method]
    public (bool Hit, Entity Entity, Double3 Point, Vector3 Normal, float Distance) Raycast(Double3 origin, Vector3 direction, float maxDistance, uint mask = PhysicsLayers.All, Entity ignore = default)
    {
        ThrowIfDisposed();
        float length = direction.Length();
        if (!(maxDistance > 0) || length < 1e-9f)
        {
            return default;
        }

        Vector3 unit = direction / length;
        AjRayHit hit;
        if (!JoltApi.Raycast(_native, Convert.ToJolt(origin), Convert.ToJolt(unit * maxDistance), mask, IgnoredBody(ignore), &hit) || RecordOfBody(hit.Body) is not { } record)
        {
            return default;
        }

        float distance = maxDistance * hit.Fraction;
        return (true, record.Entity, origin + (Double3)(unit * distance), Convert.FromJolt(hit.Normal), distance);
    }

    /// <summary>Sweeps a sphere along <paramref name="direction"/> and returns the first body it touches: whether there was one, the entity, the point of contact, the normal and how far the sphere got.</summary>
    [Method]
    public (bool Hit, Entity Entity, Double3 Point, Vector3 Normal, float Distance) CastSphere(Double3 origin, Vector3 direction, float radius, float maxDistance, uint mask = PhysicsLayers.All, Entity ignore = default)
    {
        ThrowIfDisposed();
        float length = direction.Length();
        if (!(radius > 0) || !(maxDistance > 0) || length < 1e-9f)
        {
            return default;
        }

        AjCastHit hit;
        if (!JoltApi.CastSphere(_native, Convert.ToJolt(origin), Convert.ToJolt(direction / length * maxDistance), radius, mask, IgnoredBody(ignore), &hit) || RecordOfBody(hit.Body) is not { } record)
        {
            return default;
        }

        return (true, record.Entity, Convert.FromJolt(hit.Point), Convert.FromJolt(hit.Normal), maxDistance * hit.Fraction);
    }

    /// <summary>The entities of the bodies that touch this sphere (sensors excluded), each once.</summary>
    [Method]
    public Entity[] OverlapSphere(Double3 center, float radius, uint mask = PhysicsLayers.All, Entity ignore = default)
    {
        ThrowIfDisposed();
        if (!(radius > 0))
        {
            return [];
        }

        return Overlapping((ids, capacity) => JoltApi.OverlapSphere(_native, Convert.ToJolt(center), radius, mask, IgnoredBody(ignore), ids, capacity));
    }

    /// <summary>The entities of the bodies that touch this box (centered on <paramref name="center"/>, turned by <paramref name="rotation"/>), each once.</summary>
    [Method]
    public Entity[] OverlapBox(Double3 center, Quaternion rotation, Vector3 halfExtents, uint mask = PhysicsLayers.All, Entity ignore = default)
    {
        ThrowIfDisposed();
        if (!(halfExtents.X > 0 && halfExtents.Y > 0 && halfExtents.Z > 0))
        {
            return [];
        }

        return Overlapping((ids, capacity) => JoltApi.OverlapBox(_native, Convert.ToJolt(center), Convert.ToJolt(rotation), Convert.ToJolt(halfExtents), mask, IgnoredBody(ignore), ids, capacity));
    }

    private unsafe delegate uint OverlapCall(uint* ids, uint capacity);

    // Asks with room for 64; if there were more, once more with room for all of them.
    private Entity[] Overlapping(OverlapCall call)
    {
        uint* ids = stackalloc uint[64];
        uint count = call(ids, 64);
        var bodies = new uint[count];
        if (count <= 64)
        {
            for (int i = 0; i < count; i++)
            {
                bodies[i] = ids[i];
            }
        }
        else
        {
            fixed (uint* all = bodies)
            {
                call(all, count);
            }
        }

        var result = new System.Collections.Generic.List<Entity>((int)count);
        foreach (uint id in bodies)
        {
            if (RecordOfBody(id) is { } record)
            {
                result.Add(record.Entity);
            }
        }

        return [.. result];
    }
}
