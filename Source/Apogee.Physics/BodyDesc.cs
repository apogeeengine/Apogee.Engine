using System.Numerics;
using Apogee.Reflection;

namespace Apogee.Physics;

/// <summary>
/// What a body is made of and how it behaves: the shape, how it moves, its mass, surface and layers. Fill one in and give it to <see cref="PhysicsWorld.AddBody"/>; the same description can make
/// any number of bodies. Where the body is comes from its entity's transform (its scale is applied to the shape).
/// </summary>
[ApogeeClass(Creatable = true)]
public sealed partial class BodyDesc : EngineObject
{
    /// <summary>What it collides as. Required.</summary>
    [Property]
    public PhysicsShape? Shape { get; set; }

    [Property]
    public MotionKind Motion { get; set; } = MotionKind.Dynamic;

    /// <summary>The mass in kilograms of a dynamic body; 0 takes it from the shape's volume and <see cref="Density"/>.</summary>
    [Property]
    public float Mass { get; set; }

    /// <summary>Kilograms per cubic meter, for a body with no <see cref="Mass"/> (water is 1000).</summary>
    [Property]
    public float Density { get; set; } = 1000;

    [Property]
    public float Friction { get; set; } = 0.5f;

    /// <summary>How much of its speed a body keeps when it bounces: 0 to 1.</summary>
    [Property]
    public float Restitution { get; set; }

    /// <summary>The fraction of linear speed lost per second.</summary>
    [Property]
    public float LinearDamping { get; set; } = 0.05f;

    [Property]
    public float AngularDamping { get; set; } = 0.05f;

    /// <summary>1 is the world's gravity, 0 none, negative lifts.</summary>
    [Property]
    public float GravityFactor { get; set; } = 1;

    /// <summary>The collision layers the body is in, a bit each (<see cref="PhysicsLayers"/>).</summary>
    [Property]
    public uint Layers { get; set; } = PhysicsLayers.Default;

    /// <summary>The layers it collides with.</summary>
    [Property]
    public uint Mask { get; set; } = PhysicsLayers.All;

    /// <summary>A sensor does not collide: it reports the bodies inside it (<see cref="PhysicsWorld.SensorEntered"/>).</summary>
    [Property]
    public bool IsSensor { get; set; }

    /// <summary>Report its contacts (<see cref="PhysicsWorld.ContactBegan"/>) and when it wakes or sleeps. Off by default: events cost something for every pair.</summary>
    [Property]
    public bool Events { get; set; }

    [Property]
    public bool AllowSleeping { get; set; } = true;

    /// <summary>Check the whole path of a fast body instead of its position at each step (bullets, fast wheels), so it cannot tunnel through thin walls. Costs more.</summary>
    [Property]
    public bool Bullet { get; set; }

    /// <summary>
    /// Where the center of mass is, in the body's space, when it is not where the shape's volume puts it (a truck's engine and axles are low: <c>(0, 0.9, 0.5)</c>). Unset keeps the shape's.
    /// A vehicle drives well only with a low center.
    /// </summary>
    [Property]
    public Vector3? CenterOfMass { get; set; }

    [Property]
    public Vector3 LinearVelocity { get; set; }

    [Property]
    public Vector3 AngularVelocity { get; set; }
}
