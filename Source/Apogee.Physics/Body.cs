using Apogee.Core.Numerics;
using Apogee.Reflection;

namespace Apogee.Physics;

#pragma warning disable CA1051 // The fields of a component are its memory layout.

/// <summary>
/// An entity that has a body in the physics world (<see cref="PhysicsWorld.AddBody"/>). Removing it, or destroying the entity, removes the body. The fields are for tools and
/// inspectors; code asks the world (<see cref="PhysicsWorld.Pose"/>).
/// </summary>
[Component]
public struct Body
{
    /// <summary>Jolt's id of the body.</summary>
    [Property(Hidden = true)]
    public uint Id;

    /// <summary>The world's record of it.</summary>
    [Property(Hidden = true)]
    public uint Slot;

    /// <summary>A <see cref="MotionKind"/>.</summary>
    [Property(ReadOnly = true)]
    public byte Motion;
}

#pragma warning restore CA1051

/// <summary>What a body was made with, kept so that it can be made again (a hitched attachment gets its body back when it is let go).</summary>
internal sealed class BodySettings
{
    public MotionKind Motion;
    public float Mass;
    public float Friction;
    public float Restitution;
    public float LinearDamping;
    public float AngularDamping;
    public float GravityFactor;
    public uint Layers;
    public uint Mask;
    public bool Sensor;
    public bool Events;
    public bool AllowSleeping;
    public bool Bullet;
}

/// <summary>What the world keeps for one body.</summary>
internal sealed class BodyRecord
{
    public BodySettings Settings = new();
    public Entity Entity;
    public uint BodyId;
    public uint Generation;
    public MotionKind Motion;
    public bool Sensor;
    public bool Events;

    /// <summary>The pose after the last step and before it, in world space (for interpolation).</summary>
    public Double3 CurrentPosition;
    public System.Numerics.Quaternion CurrentRotation;
    public Double3 PreviousPosition;
    public System.Numerics.Quaternion PreviousRotation;
}
