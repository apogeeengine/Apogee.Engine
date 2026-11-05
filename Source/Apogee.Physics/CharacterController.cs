using System;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Native.Jolt;
using Apogee.Reflection;
using JoltApi = Apogee.Native.Jolt.Jolt;

namespace Apogee.Physics;

/// <summary>
/// A walking character: a capsule that slides along walls, climbs stairs and slopes, sticks to the ground going down and stands on moving platforms, without being a rigid body
/// (Jolt's <c>CharacterVirtual</c>). Make one with <see cref="PhysicsWorld.CreateCharacter"/>; it moves in every fixed step toward <see cref="DesiredVelocity"/>, and its entity's
/// transform (the feet) follows, blended between steps like a dynamic body's. Dynamic bodies are pushed by it and do not push it.
/// </summary>
[ApogeeClass]
public sealed unsafe partial class CharacterController : EngineObject
{
    internal const float MaxSlopeDegreesValue = 50;

    private readonly PhysicsWorld _world;
    private AjCharacter* _native;
    private float _verticalSpeed;
    private bool _jumpRequested;
    private float _jumpSpeed;

    internal CharacterController(PhysicsWorld world, Entity entity, AjCharacter* native, Double3 position, Quaternion rotation)
    {
        _world = world;
        Entity = entity;
        _native = native;
        CurrentPosition = PreviousPosition = position;
        CurrentRotation = PreviousRotation = rotation;
        Adopt();
    }

    /// <summary>The entity that follows the character.</summary>
    [Property]
    public Entity Entity { get; }

    /// <summary>The velocity the character tries to have, in meters per second, in world space. Only the horizontal part matters: gravity and jumps decide the vertical one.</summary>
    [Property]
    public Vector3 DesiredVelocity { get; set; }

    /// <summary>Whether the character stands on something that holds it (not a slope too steep to stand on).</summary>
    [Property]
    public bool IsOnGround => JoltApi.CharacterIsSupported(Native);

    /// <summary>The velocity of the ground under the character (a moving platform), or zero in the air.</summary>
    [Property]
    public Vector3 GroundVelocity => Convert.FromJolt(JoltApi.CharacterGetGroundVelocity(Native));

    /// <summary>How fast the character actually moved in the last step.</summary>
    [Property]
    public Vector3 Velocity => Convert.FromJolt(JoltApi.CharacterGetVelocity(Native));

    /// <summary>The steepest slope in degrees the character can stand and walk on.</summary>
    [Property]
    public float MaxSlopeDegrees { get; } = MaxSlopeDegreesValue;

    /// <summary>Where the feet are, after the last step.</summary>
    [Property]
    public Double3 Position => CurrentPosition;

    internal Double3 CurrentPosition { get; private set; }

    internal Quaternion CurrentRotation { get; private set; }

    internal Double3 PreviousPosition { get; set; }

    internal Quaternion PreviousRotation { get; set; }

    internal AjCharacter* Native => _native != null ? _native : throw new ObjectDisposedException(nameof(CharacterController));

    /// <summary>Starts a jump of this upward speed (m/s) at the next step, if the character is on the ground then.</summary>
    [Method]
    public void Jump(float speed)
    {
        _jumpRequested = true;
        _jumpSpeed = speed;
    }

    /// <summary>Puts the character (its feet) here at once; its entity follows with no blend.</summary>
    [Method]
    public void Teleport(Double3 position)
    {
        JoltApi.CharacterSetPosition(Native, Convert.ToJolt(position));
        _verticalSpeed = 0;
        CurrentPosition = PreviousPosition = position;
        _world.WriteTransform(Entity, position, CurrentRotation);
    }

    /// <summary>Turns the character about its up axis (its entity follows).</summary>
    [Method]
    public void SetRotation(Quaternion rotation)
    {
        JoltApi.CharacterSetRotation(Native, Convert.ToJolt(rotation));
        CurrentRotation = PreviousRotation = rotation;
    }

    // One fixed step: the horizontal velocity asked for, the vertical one from gravity and jumps, then the capsule moves.
    internal void Step(float dt, Vector3 gravity)
    {
        PreviousPosition = CurrentPosition;
        PreviousRotation = CurrentRotation;

        bool grounded = IsOnGround;
        if (grounded && _verticalSpeed < 0)
        {
            _verticalSpeed = 0;
        }

        Vector3 ground = grounded ? GroundVelocity : Vector3.Zero;
        if (_jumpRequested && grounded)
        {
            _verticalSpeed = _jumpSpeed;
        }

        _jumpRequested = false;
        _verticalSpeed = grounded && _verticalSpeed <= 0 ? 0 : _verticalSpeed + (gravity.Y * dt);

        JoltApi.CharacterStep(Native, dt, Convert.ToJolt(new Vector3(DesiredVelocity.X + ground.X, _verticalSpeed + ground.Y, DesiredVelocity.Z + ground.Z)));

        CurrentPosition = Convert.FromJolt(JoltApi.CharacterGetPosition(Native));
        if (!IsOnGround)
        {
            _verticalSpeed = Velocity.Y; // a ceiling stops a jump
        }
    }

    protected override void OnDestroyed()
    {
        if (_native != null)
        {
            _world.ForgetCharacter(this);
            JoltApi.CharacterDestroy(_native);
            _native = null;
        }
    }
}
