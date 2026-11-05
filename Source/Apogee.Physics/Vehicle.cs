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
/// A vehicle on a dynamic body: wheels with suspension and tires, or tracks, an engine and a clutch (Jolt's <c>VehicleConstraint</c>). The game owns the gearbox: it sets
/// <see cref="DriveRatio"/> (the signed overall ratio it wants, 0 for neutral) and <see cref="Clutch"/> in the <see cref="Stepping"/> signal, which runs before each physics step, together with the
/// driver's inputs, and the vehicle gives them to Jolt. Make one with <see cref="PhysicsWorld.CreateVehicle"/>.
/// </summary>
/// <remarks>
/// A road or tracked vehicle on Jolt's wheeled and tracked <c>VehicleConstraint</c>, tuned for heavy vehicles. Differences from Jolt's own: its transmission stays in manual mode with one forward and one
/// reverse gear whose ratio is rewritten each step; wheels at one axle share a differential whose ratio goes with the wheel size; the wheels of a vehicle with no powered wheel (a trailer)
/// stay in neutral; and the steered wheels turn about one center (Ackermann).
/// </remarks>
[ApogeeClass]
public sealed unsafe partial class Vehicle : EngineObject
{
    private readonly PhysicsWorld _world;
    private readonly BodyRecord _body;
    private readonly WheelState[] _wheels;
    private readonly DriveKind _drive;
    private readonly bool _trailer;
    private readonly float _brakeCapacity;
    private readonly float _peakPowerRpm;
    private AjVehicle* _native;
    private float _applied;

    private sealed class WheelState
    {
        public Vector3 Rest;                       // the position in the vehicle's space
        public float SteerRatio;
        public float MaxSteerRadians;
        public float BrakeTorque;
        public float BrakeFactor = 1;
        public bool IsTire;
        public Entity Entity;
        public float Travel;
    }

    internal Vehicle(PhysicsWorld world, BodyRecord body, VehicleDesc desc)
    {
        _world = world;
        _body = body;
        _drive = desc.Drive;
        Adopt();
        Entity = body.Entity;

        WheelDesc[] wheels = desc.Wheels;
        if (wheels.Length == 0)
        {
            throw new ValueException("a vehicle needs at least one wheel");
        }

        _wheels = new WheelState[wheels.Length];
        var native = new AjWheelDesc[wheels.Length];
        for (int i = 0; i < wheels.Length; i++)
        {
            WheelDesc wheel = wheels[i];
            float travel = Math.Max(0.01f, wheel.SuspensionLength);
            _wheels[i] = new WheelState
            {
                Rest = wheel.Position,
                // A wheel that does not steer is the fixed axle the others pivot about.
                SteerRatio = wheel.MaxSteerAngle == 0 ? 0 : wheel.SteerRatio,
                MaxSteerRadians = wheel.MaxSteerAngle * MathF.PI / 180,
                BrakeTorque = wheel.MaxBrakeTorque,
                BrakeFactor = wheel.BrakeFactor,
                IsTire = _drive == DriveKind.Wheels,
                Entity = wheel.Entity,
                Travel = travel,
            };

            // The wheel was placed with its suspension fully extended, so the attachment point is the travel above it.
            native[i] = new AjWheelDesc
            {
                Position = Convert.ToJolt(wheel.Position + new Vector3(0, travel, 0)),
                Radius = wheel.Radius,
                Width = wheel.Width,
                SuspensionMaxLength = travel,
                SpringFrequency = wheel.SuspensionFrequency,
                SpringDamping = wheel.SuspensionDamping,
                MaxSteer = _wheels[i].MaxSteerRadians,
                MaxBrakeTorque = wheel.MaxBrakeTorque * wheel.BrakeFactor,
                MaxHandbrakeTorque = wheel.MaxHandbrakeTorque,
                // A heavier wheel than Jolt's car default: 0.5 · m · r² for a wheel of about 250 kg per meter of radius (a truck's, rim and tire). A light one is stopped by the brakes in one step.
                Inertia = 0.5f * 250f * wheel.Radius * wheel.Radius * wheel.Radius,
                Friction = wheel.Friction,
            };
        }

        var vehicle = new AjVehicleDesc
        {
            Tracked = _drive == DriveKind.Tracks,
            Wheels = null,
            WheelCount = (uint)wheels.Length,
            MaxPitchRollRadians = desc.MaxPitchRollAngle * MathF.PI / 180,
            EngineMaxTorque = desc.EngineMaxTorque,
            EngineMinRpm = desc.EngineMinRpm,
            EngineMaxRpm = Math.Max(desc.EngineMaxRpm, desc.EngineMinRpm + 1),
            EngineInertia = Math.Max(0.01f, desc.EngineInertia),
            // The clutch's strength: the torque it carries for each rad/s it slips.
            ClutchStrength = Math.Max(1f, desc.EngineMaxTorque / (Math.Max(1f, desc.ClutchSlipRpm) * MathF.Tau / 60f)),
        };
        _peakPowerRpm = FillTorque(desc, ref vehicle);
        _trailer = false;

        var differentials = new List<AjDifferentialDesc>();
        var trackWheels = new uint[2][];
        if (_drive == DriveKind.Tracks)
        {
            trackWheels = MakeTracks(desc, wheels, ref vehicle);
            _brakeCapacity = 2 * desc.TrackBrakeTorque;
        }
        else
        {
            _trailer = MakeDifferentials(desc, wheels, differentials, ref vehicle);
            _brakeCapacity = SumBrakes(wheels);
        }

        AjDifferentialDesc[] differentialArray = [.. differentials];
        fixed (AjWheelDesc* wheelData = native)
        fixed (AjDifferentialDesc* differentialData = differentialArray)
        fixed (uint* left = trackWheels[0])
        fixed (uint* right = trackWheels[1])
        {
            vehicle.Wheels = wheelData;
            vehicle.Differentials = differentialData;
            vehicle.DifferentialCount = (uint)differentialArray.Length;
            vehicle.Tracks[0].Wheels = left;
            vehicle.Tracks[1].Wheels = right;
            _native = JoltApi.VehicleCreate(world.Native, body.BodyId, &vehicle);
        }

        if (_native == null)
        {
            throw new PhysicsException("Jolt could not make the vehicle.");
        }
    }

    /// <summary>The entity of the vehicle's body.</summary>
    [Property]
    public Entity Entity { get; }

    // ---- Building ---------------------------------------------------------------------------------------------------------------

    private static float SumBrakes(WheelDesc[] wheels)
    {
        float total = 0;
        foreach (WheelDesc wheel in wheels)
        {
            total += wheel.MaxBrakeTorque;
        }

        return total;
    }

    // The engine's torque curve, sampled at 17 points of rpm / max rpm (no curve: the same torque at every rev); returns the rpm where torque times rpm peaks, found over 65 points.
    private static float FillTorque(VehicleDesc desc, ref AjVehicleDesc vehicle)
    {
        float Share(float x)
        {
            float[] curve = desc.EngineTorqueCurve;
            if (curve.Length < 2)
            {
                return 1;
            }

            float at = Math.Clamp(x, 0, 1) * (curve.Length - 1);
            int i = Math.Min((int)at, curve.Length - 2);
            return Math.Max(0, curve[i] + ((curve[i + 1] - curve[i]) * (at - i)));
        }

        for (int i = 0; i <= 16; i++)
        {
            vehicle.TorqueCurve[i] = Share(i / 16f);
        }

        float peak = vehicle.EngineMaxRpm;
        float best = 0;
        for (int i = 0; i <= 64; i++)
        {
            float rpm = vehicle.EngineMinRpm + ((vehicle.EngineMaxRpm - vehicle.EngineMinRpm) * (i / 64f));
            float power = rpm * Share(rpm / vehicle.EngineMaxRpm);
            if (power > best)
            {
                best = power;
                peak = rpm;
            }
        }

        return peak;
    }

    // A differential per axle of powered wheels: wheels at about the same Z, the one with the larger X (+X is left) as its left wheel. Its ratio goes with the size of its wheels, so axles
    // of smaller wheels turn them faster and every wheel covers the same ground: the final drive is that of the largest wheels. Returns whether the vehicle is a trailer (no powered wheel).
    private static bool MakeDifferentials(VehicleDesc desc, WheelDesc[] wheels, List<AjDifferentialDesc> differentials, ref AjVehicleDesc vehicle)
    {
        var powered = new List<int>();
        float largest = 0;
        for (int i = 0; i < wheels.Length; i++)
        {
            if (wheels[i].Powered)
            {
                powered.Add(i);
                largest = Math.Max(largest, wheels[i].Radius);
            }
        }

        var paired = new bool[wheels.Length];
        var leftWheels = new List<int>();
        foreach (int a in powered)
        {
            if (paired[a])
            {
                continue;
            }

            paired[a] = true;
            int b = -1;
            foreach (int c in powered)
            {
                if (!paired[c] && Math.Abs(wheels[c].Position.Z - wheels[a].Position.Z) < 0.25f)
                {
                    b = c;
                    break;
                }
            }

            float radius = b >= 0 ? (wheels[a].Radius + wheels[b].Radius) * 0.5f : wheels[a].Radius;
            var differential = new AjDifferentialDesc { Ratio = desc.DifferentialRatio * (largest > 0 ? radius / largest : 1f), LeftWheel = a, RightWheel = -1 };
            if (b >= 0)
            {
                paired[b] = true;
                bool aLeft = wheels[a].Position.X >= wheels[b].Position.X;
                differential.LeftWheel = aLeft ? a : b;
                differential.RightWheel = aLeft ? b : a;
            }

            leftWheels.Add(differential.LeftWheel);
            differentials.Add(differential);
        }

        if (differentials.Count == 0)
        {
            // A trailer: Jolt wants the engine's torque shared between differentials that have wheels, so the first wheel gets one, and the gearbox stays in neutral with the clutch open:
            // no engine torque ever reaches it.
            differentials.Add(new AjDifferentialDesc { LeftWheel = 0, RightWheel = -1, EngineTorqueRatio = 1 });
            vehicle.EngineMaxTorque = 0;
            return true;
        }

        // The engine's torque goes to the axles by the share the game gave their wheels (an even split by default).
        float total = 0;
        foreach (int left in leftWheels)
        {
            total += wheels[left].TorqueShare;
        }

        for (int i = 0; i < differentials.Count; i++)
        {
            AjDifferentialDesc d = differentials[i];
            d.EngineTorqueRatio = total > 0 ? wheels[leftWheels[i]].TorqueShare / total : 1f / differentials.Count;
            differentials[i] = d;
        }

        return false;
    }

    // A track per side (+X is left), driven by its powered wheel (the sprocket), or else its rearmost one.
    private static uint[][] MakeTracks(VehicleDesc desc, WheelDesc[] wheels, ref AjVehicleDesc vehicle)
    {
        var result = new uint[2][];
        for (int side = 0; side < 2; side++)
        {
            var onSide = new List<uint>();
            int driven = -1;
            for (int i = 0; i < wheels.Length; i++)
            {
                bool left = wheels[i].Position.X >= 0;
                if (left != (side == 0))
                {
                    continue;
                }

                onSide.Add((uint)i);
                if (driven < 0
                    || (wheels[i].Powered && !wheels[driven].Powered)
                    || (wheels[i].Powered == wheels[driven].Powered && wheels[i].Position.Z < wheels[driven].Position.Z))
                {
                    driven = i;
                }
            }

            if (driven < 0)
            {
                throw new ValueException("a tracked vehicle needs wheels on both sides (+X is left, -X is right)");
            }

            result[side] = [.. onSide];
            vehicle.Tracks[side].WheelCount = (uint)onSide.Count;
            vehicle.Tracks[side].DrivenWheel = (uint)driven;
            vehicle.Tracks[side].Ratio = desc.DifferentialRatio;
            vehicle.Tracks[side].MaxBrakeTorque = desc.TrackBrakeTorque;
        }

        return result;
    }

    // ---- The driver's inputs ----------------------------------------------------------------------------------------------------

    /// <summary>Throttle: -1 to 1 (negative backs up in a gearbox that reverses by throttle; the usual gearbox uses <see cref="DriveRatio"/>'s sign).</summary>
    [Property]
    public float Forward { get; set; }

    /// <summary>-1 to 1: positive turns toward +X (the vehicle's left, in its +Z-forward space), negative toward -X.</summary>
    [Property]
    public float Steering { get; set; }

    [Property]
    public float Brake { get; set; }

    [Property]
    public float Handbrake { get; set; }

    /// <summary>The overall ratio between engine and wheels the game drives through, signed: negative reverses; 0 is neutral. Set it in <see cref="Stepping"/>.</summary>
    [Property]
    public float DriveRatio { get; set; }

    /// <summary>How far the clutch is engaged: 0 drives nothing, 1 is fully engaged. Set it in <see cref="Stepping"/>.</summary>
    [Property]
    public float Clutch { get; set; }

    /// <summary>Emitted before each fixed step with its length, before the inputs reach Jolt: the game's driving logic (its gearbox, assists, ABS) belongs here.</summary>
    [Signal]
    public Signal<float> Stepping { get; } = new();

    // ---- Readings ---------------------------------------------------------------------------------------------------------------

    /// <summary>Whether the vehicle is working (its body exists and the wheels were built).</summary>
    [Property]
    public bool IsReady => _native != null;

    /// <summary>The gear ratio the transmission is in now.</summary>
    [Property]
    public float GearRatio => IsReady ? JoltApi.VehicleGetGearRatio(_native) : 0;

    [Property]
    public float EngineRpm => IsReady ? JoltApi.VehicleGetEngineRpm(_native) : 0;

    /// <summary>The torque the engine gives at the throttle that reached it, in N·m.</summary>
    [Property]
    public float EngineTorque => IsReady ? JoltApi.VehicleGetEngineTorque(_native, Math.Abs(_applied)) : 0;

    /// <summary>The throttle that reached the engine, 0 to 1.</summary>
    [Property]
    public float EngineLoad => IsReady ? Math.Abs(_applied) : 0;

    /// <summary>The rpm where the engine's power (torque times rpm) peaks, from its curve.</summary>
    [Property]
    public float EnginePeakPowerRpm => _peakPowerRpm;

    /// <summary>What all the brakes give together, in N·m.</summary>
    [Property]
    public float BrakeCapacity => _brakeCapacity;

    /// <summary>How fast it moves along its forward axis, in km/h (negative backing up).</summary>
    [Property]
    public float SpeedKmh
    {
        get
        {
            Vector3 velocity = _world.GetLinearVelocity(Entity);
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, _body.CurrentRotation);
            return Vector3.Dot(velocity, forward) * 3.6f;
        }
    }

    /// <summary>How many wheels it has.</summary>
    [Property]
    public int WheelCount => _wheels.Length;

    private AjWheelState StateOf(int index)
    {
        if (!IsReady || (uint)index >= (uint)_wheels.Length)
        {
            throw new ValueException($"wheel {index} does not exist (the vehicle has {_wheels.Length})");
        }

        AjWheelState state;
        JoltApi.VehicleGetWheelState(_native, (uint)index, &state);
        return state;
    }

    [Method]
    public bool GetWheelContact(int index) => StateOf(index).Contact;

    /// <summary>How far the suspension is compressed, 0 (extended) to 1.</summary>
    [Method]
    public float GetWheelCompression(int index)
    {
        float travel = _wheels[index].Travel;
        return Math.Clamp((travel - StateOf(index).SuspensionLength) / travel, 0, 1);
    }

    /// <summary>How far the wheel is turned about its steering axis, in degrees (positive toward +X, like <see cref="Steering"/>).</summary>
    [Method]
    public float GetWheelSteerDegrees(int index) => StateOf(index).SteerAngle * 180f / MathF.PI;

    /// <summary>The wheel's rotation speed in revolutions per minute.</summary>
    [Method]
    public float GetWheelRpm(int index) => StateOf(index).AngularVelocity * 60f / MathF.Tau;

    /// <summary>The point where the wheel touches the ground (the world's origin if it does not).</summary>
    [Method]
    public Double3 GetWheelContactPoint(int index)
    {
        AjWheelState state = StateOf(index);
        return state.Contact ? Convert.FromJolt(state.ContactPoint) : Double3.Zero;
    }

    [Method]
    public Vector3 GetWheelContactNormal(int index)
    {
        AjWheelState state = StateOf(index);
        return state.Contact ? Convert.FromJolt(state.ContactNormal) : Vector3.UnitY;
    }

    /// <summary>The entity of the body the wheel stands on, or 0.</summary>
    [Method]
    public Entity GetWheelContactEntity(int index)
    {
        AjWheelState state = StateOf(index);
        return state.Contact && _world.RecordOfBody(state.ContactBody) is { } record ? record.Entity : default;
    }

    /// <summary>
    /// Four numbers for each wheel, the ones the driving assists need: 1 if it touches the ground (else 0), its angular velocity in rad/s, the load it carries in newtons (its suspension's
    /// force) and the grip of its tire (0 for a track's wheel).
    /// </summary>
    [Method]
    public float[] GetWheelReadings()
    {
        if (!IsReady)
        {
            return [];
        }

        var readings = new float[_wheels.Length * 4];
        float dt = _world.StepSeconds;
        for (int i = 0; i < _wheels.Length; i++)
        {
            AjWheelState state = StateOf(i);
            readings[(i * 4) + 0] = state.Contact ? 1 : 0;
            readings[(i * 4) + 1] = state.AngularVelocity;
            readings[(i * 4) + 2] = dt > 0 && _wheels[i].IsTire ? state.SuspensionLambda / dt : 0;
            readings[(i * 4) + 3] = state.Grip;
        }

        return readings;
    }

    /// <summary>Scales one wheel's brake torque for the next steps: 0 releases it, 1 is full. An ABS sets it every step.</summary>
    [Method]
    public void SetBrakeFactor(int index, float factor)
    {
        if ((uint)index >= (uint)_wheels.Length)
        {
            throw new ValueException($"wheel {index} does not exist (the vehicle has {_wheels.Length})");
        }

        _wheels[index].BrakeFactor = Math.Max(0, factor);
    }

    [Method]
    public float GetBrakeFactor(int index) => (uint)index < (uint)_wheels.Length ? _wheels[index].BrakeFactor : throw new ValueException($"wheel {index} does not exist (the vehicle has {_wheels.Length})");

    // ---- Each step --------------------------------------------------------------------------------------------------------------

    // Runs before each fixed step: the game's logic first, then the inputs go to Jolt.
    internal void Prepare(float dt)
    {
        if (!IsReady)
        {
            return;
        }

        Stepping.Emit(dt);
        if (!IsReady)
        {
            return; // the game destroyed it in its step
        }

        // The gearbox: the game's ratio and clutch in Jolt's one forward and one reverse gear. A trailer stays in neutral.
        if (_trailer)
        {
            if (JoltApi.VehicleGetGear(_native) != 0)
            {
                JoltApi.VehicleSetGearbox(_native, 0, 0);
            }
        }
        else
        {
            JoltApi.VehicleSetGearbox(_native, DriveRatio, Clutch);
        }

        if (_drive == DriveKind.Wheels)
        {
            for (int i = 0; i < _wheels.Length; i++)
            {
                JoltApi.VehicleSetWheelBrake(_native, (uint)i, _wheels[i].BrakeTorque * _wheels[i].BrakeFactor);
            }

            Steer();
            _applied = Forward;
            JoltApi.VehicleDriveWheeled(_native, Forward, Brake, Handbrake); // the steering is in the wheels' angles (Steer)
        }
        else
        {
            _applied = DriveTracks();
        }

        if (Forward != 0 || Steering != 0 || Brake != 0 || Handbrake != 0)
        {
            _world.Wake(Entity);
        }
    }

    // Ackermann steering. Each wheel's own angle is the axle's times its steer ratio; the steered wheels then turn about one turning center, the inner wheel of an axle more than the outer,
    // rather than both by the same angle (which has them fight at full lock and lose their grip). A wheel's steer ratio is how far it is from the fixed pivot (where the ratio is 0) over
    // the wheelbase, so two wheels with different ratios give the pivot and the wheelbase.
    private void Steer()
    {
        int n = _wheels.Length;
        float wheelbase = 0, pivot = 0;
        for (int i = 0; i < n && wheelbase == 0; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (Math.Abs(_wheels[i].SteerRatio - _wheels[j].SteerRatio) > 0.01f)
                {
                    wheelbase = (_wheels[i].Rest.Z - _wheels[j].Rest.Z) / (_wheels[i].SteerRatio - _wheels[j].SteerRatio);
                    pivot = _wheels[i].Rest.Z - (_wheels[i].SteerRatio * wheelbase);
                    break;
                }
            }
        }

        float limit = 80 * MathF.PI / 180;
        float steering = Math.Clamp(Steering, -1, 1);
        for (int i = 0; i < n; i++)
        {
            WheelState wheel = _wheels[i];
            float angle = steering * wheel.MaxSteerRadians * wheel.SteerRatio;
            float along = wheel.Rest.Z - pivot;
            if (wheelbase != 0 && Math.Abs(angle) >= 1e-4f && Math.Abs(along) >= 0.05f)
            {
                // The turning center's X, on the pivot's line, from the axle's angle at the axle's middle.
                float center = along / MathF.Tan(angle);
                angle = Math.Clamp(MathF.Atan(along / (center - wheel.Rest.X)), -limit, limit);
            }

            JoltApi.VehicleSetWheelSteer(_native, (uint)i, angle);
        }
    }

    // Tracks steer by slowing one side (reversing it at full lock). Turning in place drives both sides opposite ways. Jolt wants neither ratio at 0.
    private float DriveTracks()
    {
        float throttle = Forward, steering = Steering, left = 1, right = 1;
        if (throttle == 0 && steering != 0)
        {
            throttle = Math.Abs(steering);
            left = steering > 0 ? 1f : -1f;
            right = -left;
        }
        else if (steering > 0)
        {
            right = 1f - (2f * steering);
        }
        else if (steering < 0)
        {
            left = 1f + (2f * steering);
        }

        static float NonZero(float ratio) => Math.Abs(ratio) < 0.01f ? 0.01f : ratio;
        JoltApi.VehicleDriveTracked(_native, throttle, NonZero(left), NonZero(right), Math.Max(Brake, Handbrake));
        return throttle;
    }

    // After the steps: each wheel's entity gets its place on the suspension, in the vehicle's space (the entities are children of the vehicle's).
    internal void WriteWheels()
    {
        if (!IsReady)
        {
            return;
        }

        for (int i = 0; i < _wheels.Length; i++)
        {
            Entity wheelEntity = _wheels[i].Entity;
            if (wheelEntity.Id == 0 || !_world.Ecs.IsAlive(wheelEntity))
            {
                continue;
            }

            AjWheelState state = StateOf(i);
            ref Transform transform = ref _world.Ecs.Ensure<Transform>(wheelEntity);
            transform.Position = (Double3)Convert.FromJolt(state.LocalPosition);
            transform.Rotation = Convert.FromJolt(state.LocalRotation);
        }
    }

    internal bool UsesBody(BodyRecord record) => ReferenceEquals(record, _body);

    protected override void OnDestroyed()
    {
        if (_native == null)
        {
            return;
        }

        _world.ForgetVehicle(this);
        JoltApi.VehicleDestroy(_native);
        _native = null;
    }
}
