using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Physics.Tests;

/// <summary>Helpers to build vehicles: a chassis body and wheels.</summary>
public abstract class VehicleFixture : PhysicsFixture
{
    protected Entity Chassis(Double3 at, float mass, Vector3 half, float shapeHeight, Vector3? centerOfMass = null)
    {
        Entity entity = Spawn(at);
        PhysicsShape shape = PhysicsShape.Offset(PhysicsShape.Box(half), new Vector3(0, shapeHeight, 0), Quaternion.Identity);
        Physics.AddBody(entity, new BodyDesc { Shape = shape, Mass = mass, CenterOfMass = centerOfMass, LinearDamping = 0.02f });
        return entity;
    }

    protected static WheelDesc Wheel(float x, float y, float z, bool powered, float steer = 0, float radius = 0.5f, float share = 1) =>
        new() { Position = new Vector3(x, y, z), Radius = radius, Powered = powered, MaxSteerAngle = steer, TorqueShare = share };

    protected Vehicle FourWheeler(bool powered, out Entity entity, Double3? at = null, float steer = 0)
    {
        AddGround();
        entity = Chassis(at ?? new Double3(0, 0.3, 0), 4000, new Vector3(1, 0.5f, 2), 1.2f, new Vector3(0, 0.8f, 0));
        var wheels = new List<WheelDesc>();
        foreach (float z in new[] { 1.4f, -1.4f })
        {
            foreach (float x in new[] { 0.9f, -0.9f })
            {
                wheels.Add(Wheel(x, 0.5f, z, powered, z > 0 ? steer : 0));
            }
        }

        Vehicle vehicle = Physics.CreateVehicle(entity, new VehicleDesc { Wheels = [.. wheels], EngineMaxTorque = 1500 });
        Run(0.5); // settled on its springs
        return vehicle;
    }
}

/// <summary>Vehicle tests on a 4 t vehicle on four wheels.</summary>
public class VehicleTests : VehicleFixture
{
    [Fact]
    public void ItDrivesThroughTheRatioItIsGiven()
    {
        Vehicle vehicle = FourWheeler(powered: true, out _);
        Assert.True(vehicle.IsReady);

        (vehicle.DriveRatio, vehicle.Clutch, vehicle.Forward) = (6, 1, 1);
        Run(4);
        Assert.True(vehicle.SpeedKmh > 8, $"{vehicle.SpeedKmh} km/h");
        Assert.Equal(6f, vehicle.GearRatio, 1e-3f);

        // Backward is a negative ratio.
        (vehicle.Forward, vehicle.Brake) = (0, 1);
        Run(3);
        (vehicle.Brake, vehicle.Forward, vehicle.DriveRatio) = (0, 1, -6);
        Run(4);
        Assert.True(vehicle.SpeedKmh < -3, $"{vehicle.SpeedKmh} km/h");
    }

    [Fact]
    public void ItDrivesNothingInNeutralOrWithTheClutchOpen()
    {
        Vehicle vehicle = FourWheeler(powered: true, out _);

        (vehicle.DriveRatio, vehicle.Clutch, vehicle.Forward) = (0, 1, 1);
        Run(2);
        Assert.True(Math.Abs(vehicle.SpeedKmh) < 0.5f);

        (vehicle.DriveRatio, vehicle.Clutch) = (6, 0);
        Run(2);
        Assert.True(Math.Abs(vehicle.SpeedKmh) < 0.5f);
    }

    [Fact]
    public void SteppingRunsEveryStepBeforeTheInputsReachJolt()
    {
        Vehicle vehicle = FourWheeler(powered: true, out _);
        int calls = 0;
        float delta = 0;
        vehicle.Stepping.Connect(dt =>
        {
            calls++;
            delta = dt;
            (vehicle.Forward, vehicle.DriveRatio, vehicle.Clutch) = (1, 6, 1);
        });

        Run(2);

        Assert.InRange(calls, 120, 130); // 2 s at 64 steps a second
        Assert.Equal(Step, delta);
        Assert.True(vehicle.SpeedKmh > 3, $"{vehicle.SpeedKmh} km/h"); // what the step set drove it
    }

    [Fact]
    public void EachWheelsBrakeIsScaledByItsFactor()
    {
        Vehicle vehicle = FourWheeler(powered: true, out _);
        (vehicle.DriveRatio, vehicle.Clutch, vehicle.Forward) = (6, 1, 1);
        Run(5);
        float fast = vehicle.SpeedKmh;
        Assert.True(fast > 10, $"{fast} km/h");

        // Braking with every wheel's brakes released leaves it rolling.
        (vehicle.Forward, vehicle.DriveRatio, vehicle.Brake) = (0, 0, 1);
        for (int i = 0; i < vehicle.WheelCount; i++)
        {
            Assert.Equal(1, vehicle.GetBrakeFactor(i));
            vehicle.SetBrakeFactor(i, 0);
        }

        Run(1);
        Assert.True(vehicle.SpeedKmh > fast * 0.9f);

        for (int i = 0; i < vehicle.WheelCount; i++)
        {
            vehicle.SetBrakeFactor(i, 1);
        }

        Run(4);
        Assert.True(Math.Abs(vehicle.SpeedKmh) < 1, $"{vehicle.SpeedKmh} km/h");
    }

    [Fact]
    public void ItReportsWhatTheAssistsNeed()
    {
        Vehicle vehicle = FourWheeler(powered: true, out _);
        Run(1.5);

        float[] readings = vehicle.GetWheelReadings();

        Assert.Equal(16, readings.Length); // four readings for each of four wheels
        float carried = 0;
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(1, readings[i * 4]); // in contact
            carried += readings[(i * 4) + 2];
            Assert.True(readings[(i * 4) + 3] > 0.5f); // the tire's grip
            Assert.True(vehicle.GetWheelContact(i));
            Assert.InRange(vehicle.GetWheelCompression(i), 0.05f, 0.95f);
        }

        Assert.Equal(4000 * 9.81f, carried, 4000 * 9.81f * 0.05f); // its weight, on its wheels
        Assert.Equal(4 * 1500f, vehicle.BrakeCapacity);
        Assert.True(vehicle.EnginePeakPowerRpm > 800);
        Assert.Equal(4000f, Physics.GetMass(vehicle.Entity), 1f);
    }

    [Fact]
    public void TheTorqueIsSharedByTheWheelsShares()
    {
        AddGround();
        Entity entity = Chassis(new Double3(0, 0.3, 0), 4000, new Vector3(1, 0.5f, 2), 1.2f, new Vector3(0, 0.8f, 0));
        Vehicle vehicle = Physics.CreateVehicle(entity, new VehicleDesc
        {
            EngineMaxTorque = 1500,
            Wheels =
            [
                Wheel(0.9f, 0.5f, 1.4f, true, share: 0), Wheel(-0.9f, 0.5f, 1.4f, true, share: 0),
                Wheel(0.9f, 0.5f, -1.4f, true, share: 1), Wheel(-0.9f, 0.5f, -1.4f, true, share: 1),
            ],
        });
        Run(0.5);

        (vehicle.DriveRatio, vehicle.Clutch, vehicle.Forward) = (6, 1, 1);
        Run(4);

        Assert.True(vehicle.SpeedKmh > 5, $"{vehicle.SpeedKmh} km/h"); // only the rear axle gets torque, and it drives
        // The front wheels are not driven, so they spin slower than the rear ones that slip a little.
        Assert.True(Math.Abs(vehicle.GetWheelRpm(2)) >= Math.Abs(vehicle.GetWheelRpm(0)) * 0.9f);
    }

    [Fact]
    public void AVehicleWithNoPoweredWheelsStaysInNeutral()
    {
        Vehicle trailer = FourWheeler(powered: false, out _);

        (trailer.DriveRatio, trailer.Clutch, trailer.Forward) = (6, 1, 1);
        Run(2);

        Assert.True(Math.Abs(trailer.SpeedKmh) < 0.5f);
        Assert.Equal(0f, trailer.GearRatio);
    }

    [Fact]
    public void SteeredWheelsTurnAboutOneCenter()
    {
        Vehicle vehicle = FourWheeler(powered: true, out _, steer: 35);
        vehicle.Steering = 1;
        (vehicle.DriveRatio, vehicle.Clutch, vehicle.Forward) = (6, 1, 0.3f);
        Run(0.5);

        // The front wheels sit at x = ±0.9, and positive steering turns toward +X, so the wheel at +X is the inner one and turns more.
        float inner = vehicle.GetWheelSteerDegrees(0); // x = +0.9, z = +1.4
        float outer = vehicle.GetWheelSteerDegrees(1); // x = -0.9, z = +1.4
        Assert.True(Math.Abs(inner) > Math.Abs(outer) + 1, $"inner {inner}°, outer {outer}°");
        Assert.Equal(Math.Sign(inner), Math.Sign(outer));
        // Rear wheels do not steer.
        Assert.Equal(0, vehicle.GetWheelSteerDegrees(2), 0.01f);
        Assert.Equal(0, vehicle.GetWheelSteerDegrees(3), 0.01f);
        // Turning center on the rear axle: tan(angle) = wheelbase / (radius ± half track), so the two angles satisfy cot(outer) - cot(inner) = track / wheelbase.
        double cotInner = 1 / Math.Tan(Math.Abs(inner) * Math.PI / 180);
        double cotOuter = 1 / Math.Tan(Math.Abs(outer) * Math.PI / 180);
        Assert.Equal(1.8 / 2.8, cotOuter - cotInner, 0.03);
    }

    [Fact]
    public void RearSteeringAndCrabSteeringUseTheSteerRatio()
    {
        AddGround();
        Entity entity = Chassis(new Double3(0, 0.3, 0), 4000, new Vector3(1, 0.5f, 2), 1.2f, new Vector3(0, 0.8f, 0));
        var front = Wheel(0.9f, 0.5f, 1.4f, true, 30);
        var rear = Wheel(0.9f, 0.5f, -1.4f, true, 30);
        rear.SteerRatio = -1; // the rear wheels turn the other way
        var frontRight = Wheel(-0.9f, 0.5f, 1.4f, true, 30);
        var rearRight = Wheel(-0.9f, 0.5f, -1.4f, true, 30);
        rearRight.SteerRatio = -1;
        Vehicle vehicle = Physics.CreateVehicle(entity, new VehicleDesc { Wheels = [front, frontRight, rear, rearRight], EngineMaxTorque = 1500 });
        Run(0.3);

        vehicle.Steering = 0.5f;
        Run(0.2);

        Assert.True(vehicle.GetWheelSteerDegrees(0) * vehicle.GetWheelSteerDegrees(2) < 0, "opposite directions");
    }

    [Fact]
    public void ADestroyedBodyTakesTheVehicleWithIt()
    {
        Vehicle vehicle = FourWheeler(powered: true, out Entity entity);

        Ecs.Destroy(entity);
        Run(0.2);

        Assert.True(vehicle.IsDestroyed);
        Assert.Equal(1, Physics.BodyCount); // the ground
    }

    [Fact]
    public void AVehicleNeedsADynamicBodyAndWheels()
    {
        AddGround();
        Entity staticBody = AddBox(new Double3(5, 1, 0), motion: MotionKind.Static);
        Assert.Throws<ValueException>(() => Physics.CreateVehicle(staticBody, new VehicleDesc { Wheels = [Wheel(1, 0, 0, true)] }));
        Entity noBody = Spawn(Double3.Zero);
        Assert.Throws<ValueException>(() => Physics.CreateVehicle(noBody, new VehicleDesc { Wheels = [Wheel(1, 0, 0, true)] }));
        Entity box = AddBox(new Double3(0, 2, 0));
        Assert.Throws<ValueException>(() => Physics.CreateVehicle(box, new VehicleDesc()));
        Physics.CreateVehicle(box, new VehicleDesc { Wheels = [Wheel(1, 0, 0, true)] });
        Assert.Throws<ValueException>(() => Physics.CreateVehicle(box, new VehicleDesc { Wheels = [Wheel(1, 0, 0, true)] }));
    }

    [Fact]
    public void WheelEntitiesFollowTheSuspensionAndTheSteering()
    {
        AddGround();
        Entity chassis = Chassis(new Double3(0, 0.3, 0), 4000, new Vector3(1, 0.5f, 2), 1.2f, new Vector3(0, 0.8f, 0));
        Entity wheelEntity = Spawn(new Double3(5, 5, 5), parent: chassis);
        WheelDesc wheel = Wheel(0.9f, 0.5f, 1.4f, true, 30);
        wheel.Entity = wheelEntity;
        Vehicle vehicle = Physics.CreateVehicle(chassis, new VehicleDesc
        {
            Wheels = [wheel, Wheel(-0.9f, 0.5f, 1.4f, true, 30), Wheel(0.9f, 0.5f, -1.4f, true), Wheel(-0.9f, 0.5f, -1.4f, true)],
        });
        Run(1);

        Vector3 local = (Vector3)Ecs.Get<Transform>(wheelEntity).Position;
        Assert.Equal(0.9f, local.X, 0.01f);
        Assert.InRange(local.Y, 0.2f, 0.9f); // somewhere in its travel
        Assert.Equal(1.4f, local.Z, 0.01f);

        vehicle.Steering = 1;
        Run(0.3);
        Quaternion rotation = Ecs.Get<Transform>(wheelEntity).Rotation;
        float yaw = MathF.Abs(MathF.Atan2(2 * ((rotation.W * rotation.Y) + (rotation.X * rotation.Z)), 1 - (2 * ((rotation.Y * rotation.Y) + (rotation.X * rotation.X)))));
        Assert.True(yaw > 0.3f, $"steered {yaw} rad");
    }
}

public class TrackedVehicleTests : VehicleFixture
{
    [Fact]
    public void ATrackedVehicleDrivesAndTurnsInPlace()
    {
        AddGround();
        Entity entity = Chassis(new Double3(0, 0.3, 0), 8000, new Vector3(1.4f, 0.5f, 2.5f), 1.0f, new Vector3(0, 0.7f, 0));
        var wheels = new List<WheelDesc>();
        foreach (float z in new[] { 2f, 1f, 0f, -1f, -2f })
        {
            foreach (float x in new[] { 1.2f, -1.2f })
            {
                wheels.Add(Wheel(x, 0.5f, z, powered: z == -2f, radius: 0.5f));
            }
        }

        Vehicle tank = Physics.CreateVehicle(entity, new VehicleDesc { Drive = DriveKind.Tracks, Wheels = [.. wheels], EngineMaxTorque = 3000, EngineMaxRpm = 2500, DifferentialRatio = 8 });
        Run(0.5);

        (tank.DriveRatio, tank.Clutch, tank.Forward) = (4, 1, 1);
        Run(4);
        Assert.True(tank.SpeedKmh > 3, $"{tank.SpeedKmh} km/h");

        // Stop, then steer without throttle: the sides run opposite ways and it turns on the spot.
        (tank.Forward, tank.Brake) = (0, 1);
        Run(4);
        (tank.Brake, tank.Steering) = (0, 1);
        Run(3);
        Vector3 spin = Physics.GetAngularVelocity(entity);
        Assert.True(MathF.Abs(spin.Y) > 0.2f, $"yaw rate {spin.Y} rad/s");
        Assert.True(MathF.Abs(tank.SpeedKmh) < 4);
    }

    [Fact]
    public void ATrackNeedsWheelsOnBothSides()
    {
        AddGround();
        Entity entity = Chassis(new Double3(0, 0.3, 0), 8000, new Vector3(1.4f, 0.5f, 2.5f), 1.0f);
        Assert.Throws<ValueException>(() => Physics.CreateVehicle(entity, new VehicleDesc { Drive = DriveKind.Tracks, Wheels = [Wheel(1.2f, 0.5f, 0, true)] }));
    }
}
