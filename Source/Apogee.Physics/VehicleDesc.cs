using System.Numerics;
using Apogee.Reflection;

namespace Apogee.Physics;

/// <summary>What drives a vehicle's wheels.</summary>
public enum DriveKind
{
    /// <summary>Wheels with tires, steering and a differential on each powered axle.</summary>
    Wheels,

    /// <summary>A track on each side (+X is the left), steered by the difference of their speeds; the wheels are the track's road wheels.</summary>
    Tracks,
}

/// <summary>
/// One wheel of a vehicle. The vehicle's space has +Z forward, +Y up and +X to its left (glTF's). Its <see cref="Position"/> is where the wheel's center sits with its suspension fully extended:
/// the suspension reaches <see cref="SuspensionLength"/> above that, and the wheel rests somewhere in that travel under the load.
/// </summary>
[ApogeeClass(Creatable = true)]
public sealed partial class WheelDesc : EngineObject
{
    /// <summary>Where the wheel's center is with the suspension extended, in the vehicle's space.</summary>
    [Property]
    public Vector3 Position { get; set; }

    /// <summary>An entity (usually a child of the vehicle's entity) whose local transform follows the wheel each frame: its place on the suspension, steered and spinning. 0 for none.</summary>
    [Property]
    public Entity Entity { get; set; }

    [Property]
    public float Radius { get; set; } = 0.5f;

    [Property]
    public float Width { get; set; } = 0.4f;

    /// <summary>How far the wheel can move up from its extended position.</summary>
    [Property]
    public float SuspensionLength { get; set; } = 0.3f;

    /// <summary>The suspension's natural frequency in Hz: higher is stiffer.</summary>
    [Property]
    public float SuspensionFrequency { get; set; } = 1.5f;

    /// <summary>1 is critically damped.</summary>
    [Property]
    public float SuspensionDamping { get; set; } = 0.5f;

    /// <summary>How far the wheel turns at full steering, in degrees (0: it does not steer).</summary>
    [Property]
    public float MaxSteerAngle { get; set; }

    /// <summary>How much of the steering a wheel takes, and which way: 1 for front wheels, -1 for rear steering, 1 on every axle for crab steering. A wheel with no steer angle counts as ratio 0, the fixed axle. With two wheels of different ratios the steered wheels turn about one turning center (Ackermann).</summary>
    [Property]
    public float SteerRatio { get; set; } = 1;

    /// <summary>Whether the engine drives it (wheeled vehicles) or it is the track's sprocket (tracked).</summary>
    [Property]
    public bool Powered { get; set; }

    /// <summary>Brake torque in N·m at full brake.</summary>
    [Property]
    public float MaxBrakeTorque { get; set; } = 1500;

    [Property]
    public float MaxHandbrakeTorque { get; set; }

    /// <summary>Scales the tire's grip (1 is Jolt's default tire).</summary>
    [Property]
    public float Friction { get; set; } = 1;

    /// <summary>The share of the engine's torque this wheel's axle gets, among the powered axles. By the load on the axles, a lightly loaded front axle would spin.</summary>
    [Property]
    public float TorqueShare { get; set; } = 1;

    /// <summary>Scales the brake torque while driving: an ABS or a one-sided brake sets it per step (see <see cref="Vehicle.SetBrakeFactor"/>).</summary>
    [Property]
    public float BrakeFactor { get; set; } = 1;
}

/// <summary>A vehicle: its engine, drivetrain and wheels. Give it to <see cref="PhysicsWorld.CreateVehicle"/> with the entity of a dynamic body.</summary>
[ApogeeClass(Creatable = true)]
public sealed partial class VehicleDesc : EngineObject
{
    [Property]
    public DriveKind Drive { get; set; } = DriveKind.Wheels;

    [Property]
    public WheelDesc[] Wheels { get; set; } = [];

    /// <summary>Torque at the best rev, in N·m.</summary>
    [Property]
    public float EngineMaxTorque { get; set; } = 500;

    [Property]
    public float EngineMinRpm { get; set; } = 800;

    [Property]
    public float EngineMaxRpm { get; set; } = 2400;

    /// <summary>The torque at each rev as a share of the maximum, sampled evenly from 0 to the maximum rpm (at least two values); empty is the same torque at every rev.</summary>
    [Property]
    public float[] EngineTorqueCurve { get; set; } = [];

    /// <summary>The engine's flywheel inertia in kg·m².</summary>
    [Property]
    public float EngineInertia { get; set; } = 1;

    /// <summary>The final drive: engine revs per wheel rev, before the gear the game drives through.</summary>
    [Property]
    public float DifferentialRatio { get; set; } = 3.42f;

    /// <summary>How many rpm the clutch slips at full engine torque: lower is a stiffer clutch.</summary>
    [Property]
    public float ClutchSlipRpm { get; set; } = 50;

    /// <summary>Brake torque of a track's sprocket (tracked vehicles).</summary>
    [Property]
    public float TrackBrakeTorque { get; set; } = 15000;

    /// <summary>The vehicle's body is helped upright beyond this pitch or roll, in degrees.</summary>
    [Property]
    public float MaxPitchRollAngle { get; set; } = 60;
}
