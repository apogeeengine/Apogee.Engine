using System;
using System.Collections.Generic;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Physics.Tests;

/// <summary>The hitch scenarios: a truck with a mounted attachment (lifted, driven, lowered, detached) and one pulling a trailer on a drawbar through a turn.</summary>
public class HitchTests : VehicleFixture
{
    private Vehicle Truck(Double3 at, out Entity entity)
    {
        entity = Chassis(at, 6000, new Vector3(1, 0.6f, 2), 1.3f, new Vector3(0, 0.9f, 0.5f));
        var wheels = new List<WheelDesc>();
        foreach ((float z, float r, float steer, float share) in new[] { (2.2f, 0.55f, 35f, 0.513f), (-0.2f, 0.8f, 0f, 1.579f) })
        {
            foreach (float x in new[] { 0.95f, -0.95f })
            {
                wheels.Add(new WheelDesc { Position = new Vector3(x, r, z), Radius = r, Powered = true, MaxSteerAngle = steer, TorqueShare = share });
            }
        }

        Vehicle vehicle = Physics.CreateVehicle(entity, new VehicleDesc { Wheels = [.. wheels], EngineMaxTorque = 2500 });
        (vehicle.DriveRatio, vehicle.Clutch) = (12, 1);
        return vehicle;
    }

    private Entity Attachment(Double3 at, float mass, Vector3 size)
    {
        Entity entity = Spawn(at);
        PhysicsShape shape = PhysicsShape.Offset(PhysicsShape.Box(size / 2), new Vector3(0, size.Y / 2, 0), Quaternion.Identity);
        Physics.AddBody(entity, new BodyDesc { Shape = shape, Mass = mass });
        return entity;
    }

    private Entity HitchEntity(Entity truck, Vector3 at) => Spawn((Double3)at, parent: truck);

    [Fact]
    public void AMountedAttachmentIsLiftedRidesOnTheHitchAndGoesFreeAgain()
    {
        AddGround();
        Vehicle truck = Truck(new Double3(-20, 0.2, 0), out Entity truckEntity);
        Entity hitchEntity = HitchEntity(truckEntity, new Vector3(0, 0.6f, -1.4f));
        Hitch rear = Physics.CreateHitch(hitchEntity, HitchKind.Mount);
        Entity attachment = Attachment(new Double3(-20, 0.5, -3), 1200, new Vector3(3, 0.6f, 1.2f));
        Run(0.1);

        float mass = Physics.GetMass(truckEntity);
        Assert.Equal(6000f, mass, 1f);
        Assert.True(rear.Attach(attachment, new Vector3(0, 0.3f, 0.6f), Quaternion.Identity), "the attachment mounts");
        Assert.Equal(mass + 1200, Physics.GetMass(truckEntity), 1f); // its mass goes on the truck
        Assert.False(Physics.HasBody(attachment)); // its shape is the truck's now
        Assert.Equal(truckEntity, Ecs.GetParent(hitchEntity));
        Assert.Equal(hitchEntity, Ecs.GetParent(attachment));
        Assert.True(rear.IsAttached);
        Entity other = Attachment(new Double3(-30, 0.5, 0), 100, new Vector3(1, 0.5f, 1));
        Assert.False(rear.Attach(other, Vector3.Zero, Quaternion.Identity), "a hitch holds one body");
        Run(1.5);

        // Lift: moving the hitch entity moves the attachment.
        double before = WorldOf(attachment).Position.Y;
        Ecs.Ensure<Transform>(hitchEntity).Position = new Double3(0, 1.0, -1.4);
        Run(0.1);
        double rise = WorldOf(attachment).Position.Y - before;
        Assert.True(rise > 0.35, $"lifting the hitch lifts the attachment ({rise} m)");

        // Drive with it raised: it rides on the hitch, and the truck still drives.
        truck.Forward = 0.7f;
        Run(4);
        Trs hitchWorld = Ecs.ComputeWorldTransform(hitchEntity);
        Trs attachmentWorld = Ecs.ComputeWorldTransform(attachment);
        Double3 attachPoint = attachmentWorld.TransformPoint(new Vector3(0, 0.3f, 0.6f));
        Assert.True(Double3.Distance(attachPoint, hitchWorld.Position) < 0.01, "the attachment rides on the hitch");
        Assert.True(truck.SpeedKmh > 5, $"the truck drives with it ({truck.SpeedKmh} km/h)");

        // Stop, lower and detach: the attachment stands on the ground on its own.
        (truck.Forward, truck.Brake) = (0, 1);
        Run(2);
        Ecs.Ensure<Transform>(hitchEntity).Position = new Double3(0, 0.3, -1.4);
        Run(0.1);
        rear.Detach();
        Assert.False(rear.IsAttached);
        Assert.Equal(mass, Physics.GetMass(truckEntity), 1f); // the mass is the truck's again
        Assert.True(Physics.HasBody(attachment));
        Assert.Equal(0ul, Ecs.GetParent(attachment).Id); // a root again
        Run(2.5);
        double y = WorldOf(attachment).Position.Y;
        Assert.InRange(y, -0.1, 0.1);
        Assert.True(Physics.GetLinearVelocity(attachment).Length() < 0.1f, "and stays there");
    }

    [Fact]
    public void ATruckWithAnAttachmentKeepsItsShapeAndGetsItBack()
    {
        AddGround();
        Truck(new Double3(0, 0.2, 0), out Entity truckEntity);
        Entity hitchEntity = HitchEntity(truckEntity, new Vector3(0, 0.6f, -1.4f));
        Hitch hitch = Physics.CreateHitch(hitchEntity, HitchKind.Mount);
        Entity attachment = Attachment(new Double3(0, 0.5, -3), 500, new Vector3(2, 0.5f, 1));
        Run(0.2);

        Assert.True(hitch.Attach(attachment, new Vector3(0, 0.25f, 0.5f), Quaternion.Identity));
        Run(0.5);
        hitch.Detach();
        Run(0.5);

        // Attached and detached twice: nothing leaks and the bodies behave.
        Assert.True(hitch.Attach(attachment, new Vector3(0, 0.25f, 0.5f), Quaternion.Identity));
        Assert.Equal(6500f, Physics.GetMass(truckEntity), 1f);
        hitch.Detach();
        Assert.Equal(6000f, Physics.GetMass(truckEntity), 1f);
        Assert.Equal(3, Physics.BodyCount); // ground, truck and the attachment, which has its body again
    }

    [Fact]
    public void ADestroyedAttachmentOrTruckLetsGo()
    {
        AddGround();
        Truck(new Double3(0, 0.2, 0), out Entity truckEntity);
        Entity hitchEntity = HitchEntity(truckEntity, new Vector3(0, 0.6f, -1.4f));
        Hitch hitch = Physics.CreateHitch(hitchEntity, HitchKind.Mount);
        Entity first = Attachment(new Double3(0, 0.5, -3), 300, new Vector3(1, 0.5f, 1));
        Assert.True(hitch.Attach(first, Vector3.Zero, Quaternion.Identity));
        Run(0.3);

        Ecs.Destroy(first); // gone while mounted
        Run(0.2);

        Assert.False(hitch.IsAttached);
        Assert.Equal(6000f, Physics.GetMass(truckEntity), 1f);

        Entity second = Attachment(new Double3(0, 0.5, -3), 300, new Vector3(1, 0.5f, 1));
        Assert.True(hitch.Attach(second, Vector3.Zero, Quaternion.Identity));
        Physics.RemoveBody(truckEntity); // the truck loses its body: the attachment gets its own back
        Run(0.2);
        Assert.False(hitch.IsAttached);
        Assert.True(Physics.HasBody(second));
        Assert.Equal(0ul, Ecs.GetParent(second).Id);

        // Destroying the truck's entity destroys what hangs under it, the hitched attachment's entity too (it is a child of the hitch entity).
        Truck(new Double3(30, 0.2, 0), out Entity other);
        Hitch otherHitch = Physics.CreateHitch(HitchEntity(other, new Vector3(0, 0.6f, -1.4f)), HitchKind.Mount);
        Entity third = Attachment(new Double3(30, 0.5, -3), 300, new Vector3(1, 0.5f, 1));
        Assert.True(otherHitch.Attach(third, Vector3.Zero, Quaternion.Identity));
        Ecs.Destroy(other);
        Run(0.2);
        Assert.False(otherHitch.IsAttached);
        Assert.False(Ecs.IsAlive(third));
    }

    [Fact]
    public void ATrailerFollowsThroughATurnOnItsDrawbar()
    {
        AddGround();
        Vehicle truck = Truck(new Double3(20, 0.2, 0), out Entity truckEntity);
        Entity drawbarEntity = HitchEntity(truckEntity, new Vector3(0, 0.6f, -1.5f));
        Hitch drawbar = Physics.CreateHitch(drawbarEntity, HitchKind.Trailer);

        Entity trailerEntity = Chassis(new Double3(20, 0.2, -6.5), 4000, new Vector3(1.15f, 0.6f, 2.5f), 1.5f);
        Vehicle trailer = Physics.CreateVehicle(trailerEntity, new VehicleDesc { Wheels = [Wheel(1, 0.55f, 0, false, radius: 0.55f), Wheel(-1, 0.55f, 0, false, radius: 0.55f)] });
        Run(0.1);

        Assert.True(drawbar.Attach(trailerEntity, new Vector3(0, 0.4f, 2.8f), Quaternion.Identity), "the trailer hitches");
        Run(1.5);
        truck.Forward = 0.7f;
        Run(4);

        // The trailer follows through a turn.
        truck.Steering = 0.5f;
        double worst = 0;
        float lowest = 1;
        for (double t = 0; t < 5; t += Step)
        {
            Physics.Update(Step);
            Ecs.Progress(Step);
            (Double3 truckPosition, Quaternion truckRotation) = Physics.Pose(truckEntity);
            (Double3 trailerPosition, Quaternion trailerRotation) = Physics.Pose(trailerEntity);
            Double3 hitchPoint = truckPosition + Vector3.Transform(new Vector3(0, 0.6f, -1.5f), truckRotation);
            Double3 trailerPoint = trailerPosition + Vector3.Transform(new Vector3(0, 0.4f, 2.8f), trailerRotation);
            worst = Math.Max(worst, Double3.Distance(hitchPoint, trailerPoint));
            lowest = Math.Min(lowest, Vector3.Transform(Vector3.UnitY, trailerRotation).Y);
        }

        Assert.True(worst < 0.05, $"the drawbar holds through the turn ({worst} m apart at most)");
        Assert.True(lowest > 0.95f, $"the trailer stays upright ({lowest})");
        Assert.True(truck.SpeedKmh > 3, $"{truck.SpeedKmh} km/h");

        // Detached, the joint is gone and the trailer is free: pushed, it rolls away from where the truck stands.
        (truck.Forward, truck.Steering, truck.Brake) = (0, 0, 1);
        drawbar.Detach();
        Assert.False(drawbar.IsAttached);
        Physics.SetLinearVelocity(trailerEntity, Vector3.Transform(new Vector3(0, 0, -3), Physics.Pose(trailerEntity).Rotation));
        Run(1);
        Assert.True(Double3.Distance(Physics.Pose(trailerEntity).Position, Physics.Pose(truckEntity).Position) > 8, "the trailer left");
        Assert.True(trailer.IsReady);
    }

    [Fact]
    public void AHitchChecksWhatItIsGiven()
    {
        AddGround();
        Truck(new Double3(0, 0.2, 0), out Entity truckEntity);
        Entity loose = Spawn(Double3.Zero);
        Assert.Throws<ValueException>(() => Physics.CreateHitch(loose, HitchKind.Mount)); // not under a body
        Entity hitchEntity = HitchEntity(truckEntity, new Vector3(0, 0.6f, -1.4f));
        Hitch hitch = Physics.CreateHitch(hitchEntity, HitchKind.Mount);
        Assert.Throws<ValueException>(() => hitch.Attach(Spawn(Double3.Zero), Vector3.Zero, Quaternion.Identity)); // no body
        Assert.Throws<ValueException>(() => hitch.Attach(truckEntity, Vector3.Zero, Quaternion.Identity));       // itself
        Entity wall = AddBox(new Double3(10, 1, 10), motion: MotionKind.Static);
        Assert.Throws<ValueException>(() => hitch.Attach(wall, Vector3.Zero, Quaternion.Identity));                 // static
        hitch.Detach(); // nothing attached: nothing happens
        Assert.False(hitch.IsAttached);
    }
}
