using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Native.Jolt;
using Apogee.Reflection;
using JoltApi = Apogee.Native.Jolt.Jolt;

namespace Apogee.Physics;

/// <summary>How a hitch holds what it carries.</summary>
public enum HitchKind
{
    /// <summary>A rigid mount (a loader, a crane, a blade): the attachment becomes part of the truck (its shape and mass), carried rigidly where the hitch entity is. Lifting is moving the hitch entity.</summary>
    Mount,

    /// <summary>A drawbar or fifth wheel: the trailer stays a body of its own, held to the truck by a joint that lets it swing within angle limits.</summary>
    Trailer,
}

/// <summary>
/// A hitch on a vehicle: a point on its body where an attachment or a trailer is attached. The hitch is an entity under the vehicle's, whose transform is the hitch's frame in the vehicle's
/// space; <see cref="Attach"/> takes the attachment's entity and the attach point in the <b>attachment's</b> space, which meets the hitch's frame. Make one with
/// <see cref="PhysicsWorld.CreateHitch"/>. A hitch holds one body at a time; the pair do not collide while hitched.
/// </summary>
/// <remarks>
/// A mounted attachment leaves the simulation: its collision shape is added to the truck's body (so the truck feels what the attachment hits) and its mass is added to the truck's. Its
/// entity becomes a child of the hitch entity (placed at the inverse of the attach point), so the transform cascade carries it and moving the hitch entity lifts it. <see cref="Detach"/> gives it a body
/// again, where it is, moving as the truck point it was at. Both ends are restored when either body goes away.
/// </remarks>
[ApogeeClass]
public sealed unsafe partial class Hitch : EngineObject
{
    private readonly PhysicsWorld _world;
    private readonly BodyRecord _parent;
    private AjJoint* _joint;
    private MountedBody.Part? _part;
    private BodyRecord? _attachmentBody; // a trailer's
    private BodySettings? _attachmentSettings;
    private Vector3 _attachPosition;
    private Quaternion _attachRotation = Quaternion.Identity;

    internal Hitch(PhysicsWorld world, Entity hitchEntity, BodyRecord parent, HitchKind kind)
    {
        _world = world;
        _parent = parent;
        HitchEntity = hitchEntity;
        Kind = kind;
        Adopt();
    }

    /// <summary>The entity that is the hitch's frame, under the vehicle's entity.</summary>
    [Property]
    public Entity HitchEntity { get; }

    [Property]
    public HitchKind Kind { get; }

    /// <summary>What is attached, or 0.</summary>
    [Property]
    public Entity Attached { get; private set; }

    [Property]
    public bool IsAttached => Attached.Id != 0;

    /// <summary>A trailer hitch's swing limits about the hitch's X, Y and Z, in degrees (default 25, 75, 15: pitch, yaw, roll).</summary>
    [Property]
    public Vector3 AngularLimits { get; set; } = new(25, 75, 15);

    /// <summary>Where the attachment's attach point is, in the attachment's space.</summary>
    [Property]
    public Vector3 AttachPosition => _attachPosition;

    // ---- Attaching --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Attaches the body of <paramref name="attachment"/> so that its point <paramref name="point"/> (turned by <paramref name="pointRotation"/>), in the attachment's own space, meets the hitch.
    /// False if the hitch holds something already. A mounted attachment must be a dynamic or kinematic body without a vehicle; a trailer may be a vehicle.
    /// </summary>
    [Method]
    public bool Attach(Entity attachment, Vector3 point, Quaternion pointRotation)
    {
        _world.ThrowIfDisposed();
        ObjectDisposedException.ThrowIf(IsDestroyed, this);
        if (IsAttached)
        {
            return false;
        }

        BodyRecord body = _world.RecordOf(attachment) ?? throw new ValueException($"entity {attachment.Id} has no physics body to hitch");
        if (ReferenceEquals(body, _parent))
        {
            throw new ValueException("a body cannot hitch itself");
        }

        if (body.Motion == MotionKind.Static)
        {
            throw new ValueException("a static body cannot be hitched");
        }

        _attachPosition = point;
        _attachRotation = Quaternion.Normalize(pointRotation);
        if (Kind == HitchKind.Mount)
        {
            AttachMounted(attachment, body);
        }
        else
        {
            AttachTrailer(attachment, body);
        }

        Attached = attachment;
        _world.Register(this);
        return true;
    }

    // The hitch's frame in the vehicle's space: the locals of the entities between them, composed top down.
    private Trs HitchInParent()
    {
        Trs result = _world.Ecs.TryGet(HitchEntity, out Transform local) ? local.ToTrs() : Trs.Identity;
        for (Entity up = _world.Ecs.GetParent(HitchEntity); up.Id != 0 && up != _parent.Entity; up = _world.Ecs.GetParent(up))
        {
            if (_world.Ecs.TryGet(up, out Transform parentLocal))
            {
                result = result.InWorldOf(parentLocal.ToTrs());
            }
        }

        return result;
    }

    // Where the attachment's body origin is in the vehicle's space: the hitch's frame, then the attach point undone.
    private (Vector3 Position, Quaternion Rotation) AttachmentInParent()
    {
        Trs hitch = HitchInParent();
        Quaternion inverse = Quaternion.Inverse(_attachRotation);
        Vector3 offset = Vector3.Transform(-_attachPosition, inverse); // the attachment's origin in the attach point's frame
        Vector3 position = (Vector3)hitch.Position + Vector3.Transform(offset, hitch.Rotation);
        return (position, Quaternion.Concatenate(inverse, hitch.Rotation));
    }

    private void AttachMounted(Entity attachment, BodyRecord body)
    {
        if (_world.IsVehicle(body))
        {
            throw new ValueException("a mounted attachment cannot be a vehicle; hitch it as a trailer");
        }

        if (_parent.Motion != MotionKind.Dynamic)
        {
            throw new ValueException("the vehicle a mount hitch is on must be a dynamic body");
        }

        float mass = body.Motion == MotionKind.Dynamic ? _world.GetMass(attachment) : 0;
        (Vector3 position, Quaternion rotation) = AttachmentInParent();
        MountedBody mounted = _world.MountedOf(_parent);
        _attachmentSettings = body.Settings;
        _part = mounted.Add(this, JoltApi.BodyGetShape(_world.Native, body.BodyId), position, rotation, mass);
        _world.RemoveBody(attachment); // its shape lives on in the vehicle's

        // The entity rides on the hitch: its transform becomes the attach point undone, in the hitch's frame, and the cascade does the rest.
        Quaternion inverse = Quaternion.Inverse(_attachRotation);
        _world.Ecs.SetParent(attachment, HitchEntity);
        ref Transform transform = ref _world.Ecs.Ensure<Transform>(attachment);
        transform.Position = (Double3)Vector3.Transform(-_attachPosition, inverse);
        transform.Rotation = inverse;
        _world.Ecs.Ensure<WorldTransform>(attachment);
    }

    private void AttachTrailer(Entity attachment, BodyRecord body)
    {
        // The joint's frames in the world, from the exact poses of the bodies after the last step: the hitch on the vehicle, the attach point on the attachment.
        Trs hitch = HitchInParent();
        (Double3 hitchPosition, Quaternion hitchRotation) = Combine(_parent.CurrentPosition, _parent.CurrentRotation, hitch.Position, hitch.Rotation);
        (Double3 pointPosition, Quaternion pointRotation) = Combine(body.CurrentPosition, body.CurrentRotation, (Double3)_attachPosition, _attachRotation);

        Vector3 limits = Vector3.Clamp(AngularLimits, Vector3.Zero, new Vector3(180)) * (MathF.PI / 180);
        var desc = new AjDrawbarDesc
        {
            BodyA = _parent.BodyId,
            BodyB = body.BodyId,
            PositionA = Convert.ToJolt(hitchPosition),
            AxisXA = Convert.ToJolt(Vector3.Transform(Vector3.UnitX, hitchRotation)),
            AxisYA = Convert.ToJolt(Vector3.Transform(Vector3.UnitY, hitchRotation)),
            PositionB = Convert.ToJolt(pointPosition),
            AxisXB = Convert.ToJolt(Vector3.Transform(Vector3.UnitX, pointRotation)),
            AxisYB = Convert.ToJolt(Vector3.Transform(Vector3.UnitY, pointRotation)),
            AngularLimits = Convert.ToJolt(limits),
        };
        _joint = JoltApi.DrawbarCreate(_world.Native, &desc);
        if (_joint == null)
        {
            throw new PhysicsException("Jolt could not make the drawbar joint.");
        }

        _attachmentBody = body;
        _world.IgnoreCollisions(_parent, body);
    }

    private static (Double3 Position, Quaternion Rotation) Combine(Double3 outerPosition, Quaternion outerRotation, Double3 innerPosition, Quaternion innerRotation) =>
        (outerPosition + Vector3.Transform((Vector3)innerPosition, outerRotation), Quaternion.Concatenate(innerRotation, outerRotation));

    // ---- Each step --------------------------------------------------------------------------------------------------------------

    // Before each step: a mounted attachment follows the hitch entity, which the game may have moved (lifting).
    internal void Prepare()
    {
        if (_part is null)
        {
            if (_attachmentBody is not null && !_world.Ecs.IsAlive(Attached))
            {
                Detach(); // the trailer's entity is gone
            }

            return;
        }

        if (!_world.Ecs.IsAlive(Attached))
        {
            Release(restoreBody: false); // the attachment was destroyed while mounted
            return;
        }

        (Vector3 position, Quaternion rotation) = AttachmentInParent();
        _part.Owner.Move(_part, position, rotation);
    }

    // ---- Detaching --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Lets the attachment go. A mounted one gets a body of its own again where it is (it keeps moving as the point of the vehicle it was at, and its mass leaves the vehicle); a trailer's joint goes.
    /// A mounted entity is put back under the hitch's parent... in the world, in its place: it is a root again.
    /// </summary>
    [Method]
    public void Detach() => Release(restoreBody: true);

    internal void Release(bool restoreBody)
    {
        if (!IsAttached)
        {
            return;
        }

        Entity attachment = Attached;
        Attached = default;
        _world.Unregister(this);

        if (_joint != null)
        {
            JoltApi.DrawbarDestroy(_joint);
            _joint = null;
            if (_attachmentBody is not null)
            {
                _world.AllowCollisions(_parent, _attachmentBody);
            }

            _attachmentBody = null;
            return;
        }

        if (_part is null)
        {
            return;
        }

        MountedBody.Part part = _part;
        _part = null;
        BodyRecord? restored = null;
        if (restoreBody && _world.Ecs.IsAlive(attachment))
        {
            // Where the attachment is now, in the world: the hitch chain composed, with the vehicle's exact pose at the bottom.
            Trs world = _world.Ecs.ComputeWorldTransform(attachment);
            Vector3 offset = (Vector3)(world.Position - _parent.CurrentPosition);
            Vector3 velocity = _world.GetLinearVelocity(_parent.Entity);
            Vector3 spin = _world.GetAngularVelocity(_parent.Entity);

            _world.Ecs.SetParent(attachment, default);
            ref Transform transform = ref _world.Ecs.Ensure<Transform>(attachment);
            transform.Position = world.Position;
            transform.Rotation = world.Rotation;
            transform.Scale = world.Scale;
            // The new body is made while the shape is still in the vehicle's compound, which holds the reference.
            restored = _world.CreateBody(attachment, _attachmentSettings!, part.Shape, world, velocity + Vector3.Cross(spin, offset), spin);
        }

        part.Owner.Remove(part);
        _ = restored;
    }

    internal bool Involves(BodyRecord record) => ReferenceEquals(record, _parent) || ReferenceEquals(record, _attachmentBody);

    protected override void OnDestroyed()
    {
        Release(restoreBody: true);
        _world.ForgetHitch(this);
    }
}

/// <summary>
/// A vehicle's body once attachments are mounted on it: one mutable compound shape of the vehicle's own shape and a part for each attachment, and the vehicle's mass (its own plus the parts').
/// </summary>
internal sealed unsafe class MountedBody
{
    private readonly PhysicsWorld _world;
    private readonly BodyRecord _body;
    private readonly AjShape* _original;
    private readonly AjShape* _compound;
    private readonly List<Part> _parts = [];
    private readonly float _baseMass;

    public sealed class Part
    {
        public MountedBody Owner = null!;
        public Hitch Hitch = null!;
        public AjShape* Shape;
        public float Mass;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    public MountedBody(PhysicsWorld world, BodyRecord body)
    {
        _world = world;
        _body = body;
        _baseMass = world.GetMass(body.Entity);
        _original = JoltApi.BodyGetShape(world.Native, body.BodyId);

        // The vehicle's own shape is the first child, at the body's origin; the body's shape becomes the compound.
        _compound = JoltApi.MutableCompound(_original);
        JoltApi.BodySetShape(world.Native, body.BodyId, _compound, false);
        Reapply();
    }

    public bool IsEmpty => _parts.Count == 0;

    public Part Add(Hitch hitch, AjShape* shape, Vector3 position, Quaternion rotation, float mass)
    {
        var part = new Part { Owner = this, Hitch = hitch, Shape = shape, Mass = mass, Position = position, Rotation = rotation };
        AjVec3 before = JoltApi.ShapeCenterOfMass(_compound);
        uint index = JoltApi.MutableCompoundAdd(_compound, shape, Convert.ToJolt(position), Convert.ToJolt(rotation));
        _parts.Add(part);
        System.Diagnostics.Debug.Assert(index == _parts.Count, "the new child is the last");
        Changed(before);
        return part;
    }

    public void Move(Part part, Vector3 position, Quaternion rotation)
    {
        if ((position - part.Position).LengthSquared() < 1e-10f && Quaternion.Dot(rotation, part.Rotation) > 0.99999999f)
        {
            return;
        }

        part.Position = position;
        part.Rotation = rotation;
        AjVec3 before = JoltApi.ShapeCenterOfMass(_compound);
        JoltApi.MutableCompoundModify(_compound, IndexOf(part), Convert.ToJolt(position), Convert.ToJolt(rotation));
        Changed(before);
    }

    public void Remove(Part part)
    {
        AjVec3 before = JoltApi.ShapeCenterOfMass(_compound);
        JoltApi.MutableCompoundRemove(_compound, IndexOf(part));
        _parts.Remove(part);
        if (_parts.Count == 0)
        {
            // Nothing mounted: the body has its own shape and mass again, and the compound is gone.
            JoltApi.BodySetShape(_world.Native, _body.BodyId, _original, false);
            JoltApi.ShapeRelease(_compound);
            JoltApi.BodySetMass(_world.Native, _body.BodyId, _baseMass);
            _world.ForgetMounted(_body);
            return;
        }

        Changed(before);
    }

    private uint IndexOf(Part part) => (uint)(1 + _parts.IndexOf(part));

    // The compound's geometry changed: its center of mass moves, and the mass and inertia follow.
    private void Changed(AjVec3 previousCenterOfMass)
    {
        JoltApi.MutableCompoundAdjustCenterOfMass(_compound);
        JoltApi.BodyNotifyShapeChanged(_world.Native, _body.BodyId, previousCenterOfMass);
        Reapply();
    }

    // The body's mass is the vehicle's plus the parts', with the inertia of the shape scaled to it.
    private void Reapply()
    {
        float total = _baseMass;
        foreach (Part part in _parts)
        {
            total += part.Mass;
        }

        JoltApi.BodySetMass(_world.Native, _body.BodyId, total);
    }
}
