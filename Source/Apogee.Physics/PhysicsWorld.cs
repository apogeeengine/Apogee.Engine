using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Native.Jolt;
using Apogee.Reflection;
using JoltApi = Apogee.Native.Jolt.Jolt;

namespace Apogee.Physics;

/// <summary>
/// The physics simulation of one ECS <see cref="World"/>: Jolt Physics with double-precision positions, stepped at a fixed rate. Entities get a <see cref="Body"/> with
/// <see cref="AddBody"/>; a dynamic body's entity follows the simulation (its <see cref="Transform"/> is written, local to its parent, so the world's transform system
/// then fills in the <see cref="WorldTransform"/>), and a kinematic body follows its entity.
/// </summary>
/// <remarks>
/// A frame calls <see cref="Update"/> once, <b>before</b> <c>World.Progress</c>, so that the transforms the physics wrote are the ones the frame's systems see and the transform system
/// composes. Everything runs on the thread that calls it (Jolt's workers run inside <see cref="Update"/> and never call managed code). A body's ancestors must not be dynamic bodies.
/// </remarks>
[ApogeeClass]
public sealed unsafe partial class PhysicsWorld : EngineObject, IDisposable
{
    private const int EventBatch = 256;

    private readonly World _ecs;
    private readonly PhysicsOptions _options;
    private readonly float _step;
    private readonly AjWorld* _native;
    private readonly Entity _removeObserver;
    private readonly List<CharacterController> _characters = [];
    private readonly List<Vehicle> _vehicles = [];
    private readonly List<Hitch> _hitches = [];
    private readonly List<Hitch> _attached = [];
    private readonly Dictionary<BodyRecord, MountedBody> _mounted = [];
    private readonly List<BodyRecord?> _records = [];
    private readonly Stack<int> _freeSlots = new();
    private readonly Dictionary<uint, BodyRecord> _byBodyId = [];
    private readonly Dictionary<ulong, BodyRecord> _byEntity = [];
    private readonly AjEvent[] _eventBuffer = new AjEvent[EventBatch];
    private HashSet<uint> _lastActive = [];
    private uint[] _activeIds = new uint[64];
    private double _accumulator;
    private uint _generation;
    private bool _disposed;
    private long _steps;
    private uint _dropped;

    /// <summary>Makes the physics world of <paramref name="ecs"/>. Dispose it before the ECS world.</summary>
    public PhysicsWorld(World ecs, PhysicsOptions? options = null)
    {
        _ecs = ecs;
        _options = options ?? new PhysicsOptions();
        _step = 1f / Math.Max(1, _options.StepRate);
        PhysicsLibrary.EnsureInitialized();

        var desc = new AjWorldDesc
        {
            MaxBodies = (uint)_options.MaxBodies,
            MaxBodyPairs = (uint)_options.MaxBodyPairs,
            MaxContactConstraints = (uint)_options.MaxContactConstraints,
            TempBufferBytes = (uint)_options.TempBufferBytes,
            Threads = _options.ResolvedThreads,
            EventCapacity = (uint)_options.EventCapacity,
            VelocitySteps = (uint)_options.VelocitySteps,
            PositionSteps = (uint)_options.PositionSteps,
            Gravity = Convert.ToJolt(_options.Gravity),
        };
        _native = JoltApi.WorldCreate(&desc);
        if (_native == null)
        {
            throw new PhysicsException("Jolt could not make the physics world.");
        }

        Interpolate = _options.Interpolate;

        _ecs.Component<Body>();
        _removeObserver = _ecs.AddObserver<Body>("PhysicsBodyRemoved", ObserverEvent.OnRemove, (Entity entity, ref Body body) => OnBodyRemoved(entity, body));
    }

    /// <summary>Makes the physics of an ECS world from a script (a game's own is the global <c>physics</c>). Destroy it, or let the script drop it, before the ECS world goes.</summary>
    [Method]
    public static PhysicsWorld Create(World ecs) => new(ecs, new PhysicsOptions { ThreadCount = 1, TempBufferBytes = 8 * 1024 * 1024, MaxBodies = 2048 });

    /// <summary>The ECS world this one simulates.</summary>
    public World Ecs => _ecs;

    /// <summary>Seconds per step (<see cref="PhysicsOptions.StepRate"/>).</summary>
    [Property]
    public float StepSeconds => _step;

    /// <summary>The number of steps taken so far.</summary>
    [Property]
    public long Steps => _steps;

    /// <summary>How many bodies the world has.</summary>
    [Property]
    public int BodyCount => _byEntity.Count;

    /// <summary>Events that did not fit the queue and were lost (see <see cref="PhysicsOptions.EventCapacity"/>).</summary>
    [Property]
    public long DroppedEvents => _dropped;

    /// <summary>While true <see cref="Update"/> does nothing: no time is added and no step runs, so a pause menu stops the world and the bodies stay where they are.</summary>
    [Property]
    public bool Paused { get; set; }

    /// <summary>Show bodies between their last two steps instead of at the last one: smooth at any frame rate, at the price of showing the past by up to a step.</summary>
    [Property]
    public bool Interpolate { get; set; }

    [Property]
    public Vector3 Gravity
    {
        get => Convert.FromJolt(JoltApi.WorldGetGravity(_native));
        set => JoltApi.WorldSetGravity(_native, Convert.ToJolt(value));
    }

    // ---- Signals ----------------------------------------------------------------------------------------------------------------

    /// <summary>Emitted before each fixed step with its length. Vehicle controllers and anything else that must act once per step belongs here, not in a frame callback.</summary>
    [Signal]
    public Signal<float> Stepping { get; } = new();

    /// <summary>Two bodies started touching, at least one of them with <see cref="BodyDesc.Events"/>: the two entities, the contact point and the normal (pointing from the first to the second).</summary>
    [Signal]
    public Signal<Entity, Entity, Double3, Vector3> ContactBegan { get; } = new();

    [Signal]
    public Signal<Entity, Entity> ContactEnded { get; } = new();

    /// <summary>A body came into a sensor: the sensor's entity, then the other.</summary>
    [Signal]
    public Signal<Entity, Entity> SensorEntered { get; } = new();

    [Signal]
    public Signal<Entity, Entity> SensorExited { get; } = new();

    /// <summary>A body with <see cref="BodyDesc.Events"/> woke up or went to sleep.</summary>
    [Signal]
    public Signal<Entity> BodyWoke { get; } = new();

    [Signal]
    public Signal<Entity> BodySlept { get; } = new();

    // ---- Native access for the rest of the library --------------------------------------------------------------------------------

    internal AjWorld* Native => _native;

    internal PhysicsOptions Options => _options;

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal BodyRecord? RecordOf(Entity entity) => _byEntity.GetValueOrDefault(entity.Id);

    internal BodyRecord? RecordOfBody(uint bodyId) => _byBodyId.GetValueOrDefault(bodyId);

    private BodyRecord Require(Entity entity) => RecordOf(entity) ?? throw new ValueException($"entity {entity.Id} has no physics body");

    // ---- Bodies -----------------------------------------------------------------------------------------------------------------

    /// <summary>Whether <paramref name="entity"/> has a body here.</summary>
    [Method]
    public bool HasBody(Entity entity) => _byEntity.ContainsKey(entity.Id);

    /// <summary>
    /// Gives <paramref name="entity"/> a body made as <paramref name="desc"/> describes, where its transform puts it now (its scale scales the shape). The entity gets a <see cref="Transform"/> if it has none.
    /// </summary>
    [Method]
    public void AddBody(Entity entity, BodyDesc desc)
    {
        ThrowIfDisposed();
        if (!_ecs.IsAlive(entity))
        {
            throw new ValueException($"entity {entity.Id} does not exist");
        }

        if (_byEntity.ContainsKey(entity.Id))
        {
            throw new ValueException($"entity {entity.Id} has a physics body already");
        }

        PhysicsShape shape = desc.Shape ?? throw new ValueException("a body needs a shape");
        if (desc.Motion == MotionKind.Dynamic && shape.Kind is "mesh" or "height_field")
        {
            throw new ValueException($"a {shape.Kind} can only be the shape of a static or kinematic body");
        }

        if (desc.Mass < 0 || desc.Density <= 0)
        {
            throw new ValueException("mass cannot be negative and density must be positive");
        }

        if (!_ecs.Has<Transform>(entity))
        {
            _ecs.Set(entity, Transform.Identity);
        }

        for (Entity ancestor = _ecs.GetParent(entity); ancestor.Id != 0; ancestor = _ecs.GetParent(ancestor))
        {
            if (_byEntity.TryGetValue(ancestor.Id, out BodyRecord? parentBody) && parentBody.Motion == MotionKind.Dynamic)
            {
                throw new ValueException($"entity {entity.Id} is under the dynamic body {ancestor.Id}; a body's ancestors must not be dynamic bodies");
            }
        }

        Trs pose = _ecs.ComputeWorldTransform(entity);
        AjShape* native = shape.Native;
        AjShape* scaledShape = null;
        AjShape* centered = null;
        try
        {
            if (pose.Scale != Vector3.One)
            {
                if (!shape.IsValidScale(pose.Scale))
                {
                    throw new ValueException($"the entity's scale ({pose.Scale.X}, {pose.Scale.Y}, {pose.Scale.Z}) cannot be applied to a {shape.Kind}");
                }

                scaledShape = JoltApi.ShapeScaled(native, Convert.ToJolt(pose.Scale));
                native = scaledShape;
            }

            if (desc.CenterOfMass is { } wanted)
            {
                Vector3 natural = Convert.FromJolt(JoltApi.ShapeCenterOfMass(native));
                centered = JoltApi.ShapeOffsetCenterOfMass(native, Convert.ToJolt(wanted - natural));
                native = centered;
            }

            var settings = new BodySettings
            {
                Motion = desc.Motion,
                Mass = desc.Mass,
                Friction = desc.Friction,
                Restitution = desc.Restitution,
                LinearDamping = desc.LinearDamping,
                AngularDamping = desc.AngularDamping,
                GravityFactor = desc.GravityFactor,
                Layers = desc.Layers,
                Mask = desc.Mask,
                Sensor = desc.IsSensor,
                Events = desc.Events,
                AllowSleeping = desc.AllowSleeping,
                Bullet = desc.Bullet,
            };
            if (settings.Mass <= 0 && desc.Density != 1000)
            {
                settings.Mass = desc.Density * JoltApi.ShapeVolume(native); // the engine's default density is Jolt's; another one gives a mass
            }

            CreateBody(entity, settings, native, pose, desc.LinearVelocity, desc.AngularVelocity);
        }
        finally
        {
            JoltApi.ShapeRelease(centered); // the body holds its own reference
            JoltApi.ShapeRelease(scaledShape);
        }
    }

    // Makes the Jolt body, its record and the entity's Body component. `shape` is final (scaled, centered): the body takes its own reference.
    internal BodyRecord CreateBody(Entity entity, BodySettings settings, AjShape* shape, Trs pose, Vector3 linearVelocity, Vector3 angularVelocity)
    {
        int slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : _records.Count;
        if (slot == _records.Count)
        {
            _records.Add(null);
        }

        var record = new BodyRecord
        {
            Entity = entity,
            Generation = ++_generation & 0x7FFFFFFF,
            Motion = settings.Motion,
            Sensor = settings.Sensor,
            Events = settings.Events,
            Settings = settings,
            CurrentPosition = pose.Position,
            CurrentRotation = pose.Rotation,
            PreviousPosition = pose.Position,
            PreviousRotation = pose.Rotation,
        };

        var desc = new AjBodyDesc
        {
            Shape = shape,
            Position = Convert.ToJolt(pose.Position),
            Rotation = Convert.ToJolt(pose.Rotation),
            Motion = (AjMotionType)settings.Motion,
            Layers = settings.Layers,
            Mask = settings.Mask,
            UserData = ((ulong)record.Generation << 32) | (uint)slot,
            Friction = settings.Friction,
            Restitution = settings.Restitution,
            LinearDamping = settings.LinearDamping,
            AngularDamping = settings.AngularDamping,
            GravityFactor = settings.GravityFactor,
            Mass = settings.Mass,
            Sensor = settings.Sensor,
            Events = settings.Events,
            AllowSleeping = settings.AllowSleeping,
            Bullet = settings.Bullet,
            LinearVelocity = Convert.ToJolt(linearVelocity),
            AngularVelocity = Convert.ToJolt(angularVelocity),
        };
        uint id = JoltApi.BodyCreate(_native, &desc);
        if (id == unchecked((uint)JoltApi.InvalidBody))
        {
            _freeSlots.Push(slot);
            throw new PhysicsException("Jolt could not create the body (the world is full: PhysicsOptions.MaxBodies).");
        }

        record.BodyId = id;
        _records[slot] = record;
        _byBodyId[record.BodyId] = record;
        _byEntity[entity.Id] = record;
        _ecs.Set(entity, new Body { Id = record.BodyId, Slot = (uint)slot, Motion = (byte)settings.Motion });
        return record;
    }

    /// <summary>Takes the body off <paramref name="entity"/> (the entity stays, where the body left it).</summary>
    [Method]
    public void RemoveBody(Entity entity)
    {
        ThrowIfDisposed();
        if (_ecs.IsAlive(entity) && _ecs.Has<Body>(entity))
        {
            _ecs.Remove<Body>(entity);
        }
    }

    // Runs when the Body component goes: removed, or its entity deleted.
    private void OnBodyRemoved(Entity entity, Body body)
    {
        if (_disposed || !_byEntity.TryGetValue(entity.Id, out BodyRecord? record))
        {
            return;
        }

        // What holds the body (a hitch, a vehicle's constraint) goes before the body does, while the world still knows the body.
        foreach (Hitch hitch in _hitches.ToArray())
        {
            if (hitch.Involves(record))
            {
                hitch.Release(restoreBody: true);
            }
        }

        OnBodyGone?.Invoke(record);
        foreach (Vehicle vehicle in _vehicles.ToArray())
        {
            if (vehicle.UsesBody(record))
            {
                vehicle.Destroy();
            }
        }

        _byEntity.Remove(entity.Id);
        _byBodyId.Remove(record.BodyId);
        _lastActive.Remove(record.BodyId);
        _records[(int)body.Slot] = null;
        _freeSlots.Push((int)body.Slot);
        JoltApi.BodyDestroy(_native, record.BodyId);
    }

    /// <summary>Raised when a body is destroyed, before Jolt forgets it: the library's other parts (vehicles) drop what they hold for it.</summary>
    internal event Action<BodyRecord>? OnBodyGone;

    // ---- Moving bodies ----------------------------------------------------------------------------------------------------------

    /// <summary>The exact pose of the body in the world after the last step (the entity's own transform may show a blend between two steps, see <see cref="Interpolate"/>).</summary>
    [Method]
    public (Double3 Position, Quaternion Rotation) Pose(Entity entity)
    {
        BodyRecord record = Require(entity);
        return (record.CurrentPosition, record.CurrentRotation);
    }

    /// <summary>
    /// Puts the body at this world pose at once, keeping its velocity, and its entity too (no blend). For a static body this is how it moves at all.
    /// </summary>
    [Method]
    public void Teleport(Entity entity, Double3 position, Quaternion rotation)
    {
        ThrowIfDisposed();
        BodyRecord record = Require(entity);
        JoltApi.BodySetPose(_native, record.BodyId, Convert.ToJolt(position), Convert.ToJolt(rotation));
        record.CurrentPosition = record.PreviousPosition = position;
        record.CurrentRotation = record.PreviousRotation = rotation;
        WriteTransform(record.Entity, position, rotation);
    }

    [Method]
    public Vector3 GetLinearVelocity(Entity entity)
    {
        return Convert.FromJolt(JoltApi.BodyGetLinearVelocity(_native, Require(entity).BodyId));
    }

    [Method]
    public void SetLinearVelocity(Entity entity, Vector3 velocity)
    {
        JoltApi.BodySetLinearVelocity(_native, Require(entity).BodyId, Convert.ToJolt(velocity));
    }

    [Method]
    public Vector3 GetAngularVelocity(Entity entity)
    {
        return Convert.FromJolt(JoltApi.BodyGetAngularVelocity(_native, Require(entity).BodyId));
    }

    [Method]
    public void SetAngularVelocity(Entity entity, Vector3 velocity)
    {
        JoltApi.BodySetAngularVelocity(_native, Require(entity).BodyId, Convert.ToJolt(velocity));
    }

    /// <summary>A force in newtons through the center of mass, applied during the next step only (call it every step to push continuously).</summary>
    [Method]
    public void AddForce(Entity entity, Vector3 force)
    {
        JoltApi.BodyAddForce(_native, Require(entity).BodyId, Convert.ToJolt(force));
    }

    [Method]
    public void AddTorque(Entity entity, Vector3 torque)
    {
        JoltApi.BodyAddTorque(_native, Require(entity).BodyId, Convert.ToJolt(torque));
    }

    /// <summary>An instant change of momentum in newton seconds through the center of mass.</summary>
    [Method]
    public void AddImpulse(Entity entity, Vector3 impulse)
    {
        JoltApi.BodyAddImpulse(_native, Require(entity).BodyId, Convert.ToJolt(impulse));
    }

    [Method]
    public void Wake(Entity entity) => JoltApi.BodyActivate(_native, Require(entity).BodyId);

    [Method]
    public bool IsAwake(Entity entity) => JoltApi.BodyIsActive(_native, Require(entity).BodyId);

    // ---- Stepping ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Advances the simulation by <paramref name="deltaSeconds"/> of game time in fixed steps (at most <see cref="PhysicsOptions.MaxSubsteps"/> a call; a slower frame loses time), then
    /// writes the bodies' poses to their entities. Call it once a frame, before <c>World.Progress</c>.
    /// </summary>
    [Method]
    public void Update(float deltaSeconds)
    {
        ThrowIfDisposed();
        if (Paused)
        {
            return;
        }

        double maxTime = _step * _options.MaxSubsteps;
        _accumulator = Math.Min(_accumulator + Math.Max(deltaSeconds, 0), maxTime + (_step * 0.001));
        int steps = (int)(_accumulator / _step);
        _accumulator -= steps * (double)_step;

        if (steps > 0)
        {
            FollowKinematics(steps * _step);
            for (int i = 0; i < steps; i++)
            {
                Stepping.Emit(_step);
                if (i == steps - 1)
                {
                    CapturePrevious();
                }

                foreach (Hitch hitch in _attached.ToArray())
                {
                    hitch.Prepare(); // a mounted attachment follows its hitch
                }

                foreach (Vehicle vehicle in _vehicles.ToArray())
                {
                    vehicle.Prepare(_step); // the vehicle's own stepping signal, then its inputs reach Jolt
                }

                Vector3 gravity = Gravity;
                foreach (CharacterController character in _characters)
                {
                    character.Step(_step, gravity);
                }

                uint error = JoltApi.WorldUpdate(_native, _step);
                if (error != 0)
                {
                    Apogee.Core.Logging.Log.Warn("Physics", $"Jolt step {_steps}: error {error} (raise PhysicsOptions.MaxBodyPairs or MaxContactConstraints)");
                }

                _steps++;
                DrainEvents();
            }
        }

        WriteBack(steps > 0, (float)(_accumulator / _step));
    }

    // Kinematic bodies go to where their entity is by the end of the steps this frame covers.
    private void FollowKinematics(float time)
    {
        foreach (BodyRecord record in _byEntity.Values)
        {
            if (record.Motion != MotionKind.Kinematic)
            {
                continue;
            }

            Trs pose = _ecs.ComputeWorldTransform(record.Entity);
            JoltApi.BodyMoveKinematic(_native, record.BodyId, Convert.ToJolt(pose.Position), Convert.ToJolt(pose.Rotation), time);
        }
    }

    private ReadOnlySpan<uint> ActiveBodies()
    {
        uint count;
        fixed (uint* ids = _activeIds)
        {
            count = JoltApi.WorldGetActiveBodies(_native, ids, (uint)_activeIds.Length);
        }

        if (count > _activeIds.Length)
        {
            _activeIds = new uint[Math.Max((int)count, _activeIds.Length * 2)];
            fixed (uint* ids = _activeIds)
            {
                count = JoltApi.WorldGetActiveBodies(_native, ids, (uint)_activeIds.Length);
            }
        }

        return _activeIds.AsSpan(0, (int)count);
    }

    // The pose of every dynamic body that is moving now, before the last step of the frame: the other end of the interpolation.
    private void CapturePrevious()
    {
        foreach (uint id in ActiveBodies())
        {
            if (_byBodyId.TryGetValue(id, out BodyRecord? record) && record.Motion == MotionKind.Dynamic)
            {
                record.PreviousPosition = record.CurrentPosition;
                record.PreviousRotation = record.CurrentRotation;
            }
        }
    }

    private void WriteBack(bool stepped, float alpha)
    {
        var active = new HashSet<uint>();
        foreach (uint id in ActiveBodies())
        {
            if (!_byBodyId.TryGetValue(id, out BodyRecord? record) || record.Motion != MotionKind.Dynamic)
            {
                continue;
            }

            active.Add(id);
            if (stepped)
            {
                ReadPose(record);
            }

            if (Interpolate)
            {
                WriteTransform(record.Entity, Double3.Lerp(record.PreviousPosition, record.CurrentPosition, alpha), Quaternion.Slerp(record.PreviousRotation, record.CurrentRotation, alpha));
            }
            else
            {
                WriteTransform(record.Entity, record.CurrentPosition, record.CurrentRotation);
            }
        }

        // A body that stopped moving shows exactly where it came to rest, not a blend.
        foreach (uint id in _lastActive)
        {
            if (!active.Contains(id) && _byBodyId.TryGetValue(id, out BodyRecord? asleep))
            {
                ReadPose(asleep);
                asleep.PreviousPosition = asleep.CurrentPosition;
                asleep.PreviousRotation = asleep.CurrentRotation;
                WriteTransform(asleep.Entity, asleep.CurrentPosition, asleep.CurrentRotation);
            }
        }

        _lastActive = active;

        foreach (Vehicle vehicle in _vehicles)
        {
            vehicle.WriteWheels();
        }

        foreach (CharacterController character in _characters)
        {
            WriteTransform(
                character.Entity,
                Interpolate ? Double3.Lerp(character.PreviousPosition, character.CurrentPosition, alpha) : character.CurrentPosition,
                character.CurrentRotation);
        }
    }

    /// <summary>
    /// Makes a walking character for <paramref name="entity"/>: a capsule of this height (feet to top, at least twice the radius) and radius, standing with its feet at the entity's position.
    /// <paramref name="layers"/> and <paramref name="mask"/> are the collision layers it is in and collides with.
    /// </summary>
    [Method]
    public CharacterController CreateCharacter(Entity entity, float height, float radius, uint layers = PhysicsLayers.Default, uint mask = PhysicsLayers.All)
    {
        ThrowIfDisposed();
        if (!_ecs.IsAlive(entity))
        {
            throw new ValueException($"entity {entity.Id} does not exist");
        }

        if (!(radius > 0) || !(height >= 2 * radius))
        {
            throw new ValueException($"a character needs a radius above zero and a height of at least twice the radius, got {height} and {radius}");
        }

        if (!_ecs.Has<Transform>(entity))
        {
            _ecs.Set(entity, Transform.Identity);
        }

        Trs pose = _ecs.ComputeWorldTransform(entity);
        var desc = new AjCharacterDesc
        {
            Height = height,
            Radius = radius,
            MaxSlopeRadians = CharacterController.MaxSlopeDegreesValue * MathF.PI / 180,
            Mass = 70,
            MaxStrength = 100,
            Position = Convert.ToJolt(pose.Position),
            Rotation = Convert.ToJolt(pose.Rotation),
            Layers = layers,
            Mask = mask,
        };
        AjCharacter* native = JoltApi.CharacterCreate(_native, &desc);
        if (native == null)
        {
            throw new PhysicsException("Jolt could not create the character.");
        }

        var character = new CharacterController(this, entity, native, pose.Position, pose.Rotation);
        _characters.Add(character);
        return character;
    }

    internal void ForgetCharacter(CharacterController character) => _characters.Remove(character);

    internal void ForgetVehicle(Vehicle vehicle) => _vehicles.Remove(vehicle);

    internal bool IsVehicle(BodyRecord record) => _vehicles.Exists(v => v.UsesBody(record));

    internal void Register(Hitch hitch)
    {
        if (!_attached.Contains(hitch))
        {
            _attached.Add(hitch);
        }
    }

    internal void Unregister(Hitch hitch) => _attached.Remove(hitch);

    internal void ForgetHitch(Hitch hitch)
    {
        _attached.Remove(hitch);
        _hitches.Remove(hitch);
    }

    internal MountedBody MountedOf(BodyRecord body)
    {
        if (!_mounted.TryGetValue(body, out MountedBody? mounted))
        {
            _mounted[body] = mounted = new MountedBody(this, body);
        }

        return mounted;
    }

    internal void ForgetMounted(BodyRecord body) => _mounted.Remove(body);

    /// <summary>Makes two bodies pass through each other (the truck and its trailer) until <see cref="AllowCollisions"/>.</summary>
    internal void IgnoreCollisions(BodyRecord a, BodyRecord b) => JoltApi.IgnorePair(_native, a.BodyId, b.BodyId);

    internal void AllowCollisions(BodyRecord a, BodyRecord b) => JoltApi.AllowPair(_native, a.BodyId, b.BodyId);

    /// <summary>
    /// Makes a hitch of <paramref name="kind"/> on <paramref name="hitchEntity"/>, which must be under the entity of a dynamic body (the vehicle): its transform is the hitch's frame in the
    /// vehicle's space. Move the entity to lift what a mount hitch carries.
    /// </summary>
    [Method]
    public Hitch CreateHitch(Entity hitchEntity, HitchKind kind)
    {
        ThrowIfDisposed();
        if (!_ecs.IsAlive(hitchEntity))
        {
            throw new ValueException($"entity {hitchEntity.Id} does not exist");
        }

        BodyRecord? parent = null;
        for (Entity up = _ecs.GetParent(hitchEntity); up.Id != 0 && parent is null; up = _ecs.GetParent(up))
        {
            parent = RecordOf(up);
        }

        if (parent is null || parent.Motion != MotionKind.Dynamic)
        {
            throw new ValueException("a hitch goes under the entity of a dynamic body (the vehicle it is on)");
        }

        if (!_ecs.Has<Transform>(hitchEntity))
        {
            _ecs.Set(hitchEntity, Transform.Identity);
        }

        var hitch = new Hitch(this, hitchEntity, parent, kind);
        _hitches.Add(hitch);
        return hitch;
    }

    /// <summary>
    /// Makes a vehicle of <paramref name="desc"/> on the dynamic body of <paramref name="entity"/>. The vehicle's space is the entity's, +Z forward, +Y up, +X left. Set its inputs, <c>drive_ratio</c> and
    /// <c>clutch</c> in its <c>stepping</c> signal (or the world's); destroying the vehicle, its body or its entity ends it.
    /// </summary>
    [Method]
    public Vehicle CreateVehicle(Entity entity, VehicleDesc desc)
    {
        ThrowIfDisposed();
        BodyRecord record = Require(entity);
        if (record.Motion != MotionKind.Dynamic)
        {
            throw new ValueException("a vehicle needs a dynamic body");
        }

        foreach (Vehicle existing in _vehicles)
        {
            if (existing.UsesBody(record))
            {
                throw new ValueException($"entity {entity.Id} is a vehicle already");
            }
        }

        var vehicle = new Vehicle(this, record, desc);
        _vehicles.Add(vehicle);
        return vehicle;
    }

    /// <summary>The mass of the body of <paramref name="entity"/> in kilograms (0 for a static or kinematic one).</summary>
    [Method]
    public float GetMass(Entity entity)
    {
        BodyRecord record = Require(entity);
        if (record.Motion != MotionKind.Dynamic)
        {
            return 0;
        }

        return JoltApi.BodyGetMass(_native, record.BodyId);
    }

    private void ReadPose(BodyRecord record)
    {
        AjRVec3 position;
        AjQuat rotation;
        JoltApi.BodyGetPose(_native, record.BodyId, &position, &rotation);
        record.CurrentPosition = Convert.FromJolt(position);
        record.CurrentRotation = Convert.FromJolt(rotation);
    }

    // Sets the entity's local transform so that its world pose is the given one; its scale stays.
    internal void WriteTransform(Entity entity, Double3 position, Quaternion rotation)
    {
        Entity parent = _ecs.GetParent(entity);
        ref Transform local = ref _ecs.Ensure<Transform>(entity);
        if (parent.Id == 0)
        {
            local.Position = position;
            local.Rotation = rotation;
            return;
        }

        Trs parentWorld = _ecs.ComputeWorldTransform(parent);
        local.Position = parentWorld.InverseTransformPoint(position);
        local.Rotation = Quaternion.Concatenate(rotation, Quaternion.Inverse(parentWorld.Rotation));
    }

    // ---- Events -----------------------------------------------------------------------------------------------------------------

    private void DrainEvents()
    {
        _dropped += JoltApi.EventsDropped(_native);
        uint count;
        fixed (AjEvent* buffer = _eventBuffer)
        {
            do
            {
                count = JoltApi.EventsTake(_native, buffer, EventBatch);
                for (int i = 0; i < count; i++)
                {
                    Dispatch(in buffer[i]);
                }
            }
            while (count == EventBatch);
        }
    }

    private void Dispatch(in AjEvent e)
    {
        switch ((AjEventKind)e.Kind)
        {
            case AjEventKind.ContactAdded:
                if (_byBodyId.TryGetValue(e.Body1, out BodyRecord? a) && _byBodyId.TryGetValue(e.Body2, out BodyRecord? b))
                {
                    if (a.Sensor || b.Sensor)
                    {
                        if (a.Sensor)
                        {
                            SensorEntered.Emit(a.Entity, b.Entity);
                        }
                        else
                        {
                            SensorEntered.Emit(b.Entity, a.Entity);
                        }
                    }
                    else
                    {
                        ContactBegan.Emit(a.Entity, b.Entity, new Double3(e.Position.X, e.Position.Y, e.Position.Z), new Vector3(e.Normal.X, e.Normal.Y, e.Normal.Z));
                    }
                }

                break;
            case AjEventKind.ContactRemoved:
                if (_byBodyId.TryGetValue(e.Body1, out BodyRecord? c) && _byBodyId.TryGetValue(e.Body2, out BodyRecord? d))
                {
                    if (c.Sensor || d.Sensor)
                    {
                        if (c.Sensor)
                        {
                            SensorExited.Emit(c.Entity, d.Entity);
                        }
                        else
                        {
                            SensorExited.Emit(d.Entity, c.Entity);
                        }
                    }
                    else
                    {
                        ContactEnded.Emit(c.Entity, d.Entity);
                    }
                }

                break;
            case AjEventKind.BodyActivated:
                if (_byBodyId.TryGetValue(e.Body1, out BodyRecord? woke))
                {
                    BodyWoke.Emit(woke.Entity);
                }

                break;
            case AjEventKind.BodyDeactivated:
                if (_byBodyId.TryGetValue(e.Body1, out BodyRecord? slept))
                {
                    BodySlept.Emit(slept.Entity);
                }

                break;
        }
    }

    // ---- Lifetime ---------------------------------------------------------------------------------------------------------------

    public void Dispose() => Destroy();

    protected override void OnDestroyed()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // A script that drops both the ECS world and this one has them destroyed in either order: if the ECS world is gone its observer went with it.
        if (!_ecs.IsDestroyed && _ecs.IsAlive(_removeObserver))
        {
            _ecs.Destroy(_removeObserver);
        }

        foreach (CharacterController character in _characters.ToArray())
        {
            character.Destroy();
        }

        foreach (Hitch hitch in _hitches.ToArray())
        {
            hitch.Destroy();
        }

        foreach (Vehicle vehicle in _vehicles.ToArray())
        {
            vehicle.Destroy();
        }

        JoltApi.WorldDestroy(_native); // takes the bodies and the constraints with it
        _byEntity.Clear();
        _byBodyId.Clear();
    }
}
