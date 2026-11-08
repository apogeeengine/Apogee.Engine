using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Physics.Tests;

public class PhysicsStartupTests
{
    [Fact]
    public void PhysicsWorldsCanBeMadeOnManyThreadsAtOnce()
    {
        // Jolt registers its allocator and types once; a thread that arrived while another was registering used to build a world without them (a crash on macOS).
        NativeRequirements.Require(typeof(Apogee.Native.Flecs.Ecs).Assembly, "flecs");
        NativeRequirements.Require(typeof(Apogee.Native.Jolt.Jolt).Assembly, "jolt");
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var threads = new System.Threading.Thread[8];
        using var start = new System.Threading.Barrier(threads.Length);
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new System.Threading.Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    using var ecs = new World();
                    using var physics = new PhysicsWorld(ecs, new PhysicsOptions { ThreadCount = 1, TempBufferBytes = 1024 * 1024, MaxBodies = 64 });
                    Assert.Equal(0, physics.BodyCount);
                }
                catch (Exception e)
                {
                    failures.Add(e);
                }
            });
            threads[i].Start();
        }

        foreach (System.Threading.Thread thread in threads)
        {
            thread.Join();
        }

        Assert.Empty(failures);
    }
}

public class BodyTests : PhysicsFixture
{
    [Fact]
    public void ABoxLandsRestsAndGoesToSleep()
    {
        AddGround();
        Entity box = AddBox(new Double3(0, 3, 0));

        Run(4);

        Assert.Equal(0.5, WorldOf(box).Position.Y, 0.03); // resting on the ground, less the penetration slop
        Assert.Equal(WorldOf(box).Position.Y, Ecs.Get<Transform>(box).Position.Y); // a root: local is world
        Assert.False(Physics.IsAwake(box));
        Assert.Equal((byte)MotionKind.Dynamic, Ecs.Get<Body>(box).Motion); // the Body component says what it is
    }

    [Fact]
    public void PositionsKeepTheirPrecisionTenThousandKilometresFromTheOrigin()
    {
        // The world is double precision (Docs/manual/core/math.md): the same drop, 10 000 km out, rests to the millimeter.
        const double Far = 1.0e7;
        AddGround(top: 0, x: Far, z: -Far);
        Entity box = AddBox(new Double3(Far + 3.25, 2, -Far + 1.5));

        Run(4);

        WorldTransform world = WorldOf(box);
        Assert.Equal(Far + 3.25, world.Position.X, 0.001);
        Assert.Equal(-Far + 1.5, world.Position.Z, 0.001);
        Assert.Equal(0.5, world.Position.Y, 0.03);
    }

    [Fact]
    public void ABodyUnderAParentIsWrittenInTheParentsSpace()
    {
        // The parent sits 1000 m away, turned a quarter around Y, and is no body. The child falls inside its space (its "down" is still the world's down).
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        Entity parent = Spawn(new Double3(1000, 0, 0), turn);
        AddGround(top: 0, x: 1000);
        Entity box = AddBox(new Double3(0, 4, 0), parent: parent); // local (0,4,0): world (1000,4,0)

        Run(5);

        Vector3 local = (Vector3)Ecs.Get<Transform>(box).Position;
        WorldTransform world = WorldOf(box);
        Assert.Equal(1000, world.Position.X, 0.01);
        Assert.Equal(0.5, world.Position.Y, 0.03);
        Assert.Equal(0.5, local.Y, 0.03); // y is unchanged by a turn about y
        Assert.Equal(0, local.X, 0.01);
        // The composed world pose is the physics pose.
        (Double3 position, Quaternion rotation) = Physics.Pose(box);
        Assert.True((position - world.Position).Length() < 1e-3);
        Assert.True(Quaternion.Dot(rotation, world.Rotation) is > 0.9999f or < -0.9999f);
    }

    [Fact]
    public void ARotatedParentTurnsTheChildsLocalPose()
    {
        // A parent turned a quarter about Z: its local +Y is the world's -X. A box at local (0, 2, 0) is at world (10 - 2, 0, 0); with no gravity it stays there, and the local
        // transform the physics writes must still compose to the body's world pose.
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        Entity parent = Spawn(new Double3(10, 0, 0), turn);
        Entity box = AddBox(new Double3(0, 2, 0), parent: parent);
        Physics.SetLinearVelocity(box, Vector3.Zero);
        Physics.Gravity = Vector3.Zero;

        Run(0.5);

        Trs expected = Ecs.ComputeWorldTransform(box);
        Assert.True((Physics.Pose(box).Position - expected.Position).Length() < 1e-3);
        Assert.Equal(8, WorldOf(box).Position.X, 0.01); // 10 - 2
    }

    [Fact]
    public void AKinematicBodyFollowsItsEntityAndCarriesWhatStandsOnIt()
    {
        AddGround(top: -5);
        Entity platform = AddBox(new Double3(0, 0, 0), new Vector3(3, 0.25f, 3), motion: MotionKind.Kinematic);
        Entity crate = AddBox(new Double3(0, 0.75, 0), new Vector3(0.5f));

        for (int frame = 0; frame < 4 * 64; frame++)
        {
            ref Transform t = ref Ecs.Ensure<Transform>(platform);
            t.Position = new Double3(frame * Step * 2, 0, 0); // 2 m/s along x
            Physics.Update(Step);
            Ecs.Progress(Step);
        }

        Assert.Equal(8.0, WorldOf(platform).Position.X, 0.05);
        Assert.Equal(WorldOf(platform).Position.X, WorldOf(crate).Position.X, 0.6); // carried along by friction, a little behind after the jerk of the start
        Assert.Equal(0.75, WorldOf(crate).Position.Y, 0.05);
    }

    [Fact]
    public void ScaleOfTheEntityScalesTheShape()
    {
        AddGround();
        Entity big = AddBox(new Double3(0, 3, 0), scale: new Vector3(2, 2, 2)); // a 1 m box doubled: half extent 1

        Run(4);

        Assert.Equal(1.0, WorldOf(big).Position.Y, 0.04);
        Assert.Equal(new Vector3(2, 2, 2), Ecs.Get<Transform>(big).Scale); // the transform's scale is left alone
    }

    [Fact]
    public void TeleportingMovesTheBodyAndItsEntityAtOnce()
    {
        AddGround();
        Entity box = AddBox(new Double3(0, 3, 0));

        Physics.Teleport(box, new Double3(40, 7, -2), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f));

        Assert.Equal(new Double3(40, 7, -2), Ecs.Get<Transform>(box).Position);
        Assert.Equal(new Double3(40, 7, -2), Physics.Pose(box).Position);
        Run(5);
        Assert.Equal(40, WorldOf(box).Position.X, 0.05);
        Assert.True(WorldOf(box).Position.Y < 1);
    }

    [Fact]
    public void AnImpulseChangesTheVelocityByImpulseOverMass()
    {
        Physics.Gravity = Vector3.Zero;
        Entity box = AddBox(new Double3(0, 5, 0), mass: 50);

        Physics.AddImpulse(box, new Vector3(100, 0, 0));

        Assert.Equal(2f, Physics.GetLinearVelocity(box).X, 0.001f);
        Physics.SetAngularVelocity(box, new Vector3(0, 1, 0));
        Assert.Equal(1f, Physics.GetAngularVelocity(box).Y, 0.001f);
    }

    [Fact]
    public void DensityAndShapeGiveTheMass()
    {
        Physics.Gravity = Vector3.Zero;
        Entity box = Spawn(new Double3(0, 5, 0));
        Physics.AddBody(box, new BodyDesc { Shape = PhysicsShape.Box(new Vector3(0.5f)), Density = 200 }); // 1 m³ of 200 kg/m³

        Physics.AddImpulse(box, new Vector3(100, 0, 0));

        Assert.Equal(0.5f, Physics.GetLinearVelocity(box).X, 0.001f);
    }

    [Fact]
    public void ADestroyedEntityTakesItsBodyAndRemoveBodyKeepsTheEntity()
    {
        Entity a = AddBox(new Double3(0, 5, 0));
        Entity b = AddBox(new Double3(5, 5, 0));
        Assert.Equal(2, Physics.BodyCount);

        Ecs.Destroy(a);
        Physics.RemoveBody(b);

        Assert.Equal(0, Physics.BodyCount);
        Assert.True(Ecs.IsAlive(b));
        Assert.False(Ecs.Has<Body>(b));
        Assert.False(Physics.HasBody(b));
        Run(0.5); // nothing left to step
    }

    [Fact]
    public void AddBodyRefusesWhatCannotWork()
    {
        Entity e = Spawn(Double3.Zero);
        Assert.Throws<ValueException>(() => Physics.AddBody(e, new BodyDesc()));                                                            // no shape
        Assert.Throws<ValueException>(() => Physics.AddBody(Ecs.CreateEntity(), new BodyDesc { Shape = PhysicsShape.Sphere(1), Mass = -1 })); // negative mass
        Assert.Throws<ValueException>(() => Physics.AddBody(new Entity(123456), new BodyDesc { Shape = PhysicsShape.Sphere(1) }));        // no such entity

        PhysicsShape mesh = PhysicsShape.Mesh([new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(0, 0, 1)], [0, 1, 2]);
        Assert.Throws<ValueException>(() => Physics.AddBody(e, new BodyDesc { Shape = mesh, Motion = MotionKind.Dynamic }));            // a mesh cannot move by itself
        Physics.AddBody(e, new BodyDesc { Shape = mesh, Motion = MotionKind.Static });
        Assert.Throws<ValueException>(() => Physics.AddBody(e, new BodyDesc { Shape = mesh, Motion = MotionKind.Static }));            // twice

        Entity sphere = Spawn(Double3.Zero);
        Physics.AddBody(sphere, new BodyDesc { Shape = PhysicsShape.Sphere(1) });
        Entity child = Spawn(Double3.Zero, parent: sphere);
        Assert.Throws<ValueException>(() => Physics.AddBody(child, new BodyDesc { Shape = PhysicsShape.Sphere(1) }));                  // under a dynamic body

        Entity stretched = Spawn(Double3.Zero, scale: new Vector3(1, 2, 1));
        Assert.Throws<ValueException>(() => Physics.AddBody(stretched, new BodyDesc { Shape = PhysicsShape.Sphere(1) }));             // a sphere cannot be stretched
    }
}

public class SteppingTests : PhysicsFixture
{
    public SteppingTests()
        : base(interpolate: false)
    {
    }

    [Fact]
    public void StepsAreFixedAndTimeCarriesOver()
    {
        Physics.Update(Step * 0.5f);
        Assert.Equal(0, Physics.Steps);
        Physics.Update(Step * 0.5f);
        Assert.Equal(1, Physics.Steps);
        Physics.Update(Step * 2.5f);
        Assert.Equal(3, Physics.Steps);
        Physics.Update(Step * 0.5f);
        Assert.Equal(4, Physics.Steps);
    }

    [Fact]
    public void ASlowFrameLosesTimeInsteadOfSpiralling()
    {
        Physics.Update(10); // far more than four steps
        Assert.Equal(4, Physics.Steps);
        Physics.Update(0);
        Assert.Equal(4, Physics.Steps);
    }

    [Fact]
    public void SteppingIsAnnouncedBeforeEachStep()
    {
        var seen = new List<float>();
        Physics.Stepping.Connect(seen.Add);

        Physics.Update(Step * 3);

        Assert.Equal([Step, Step, Step], seen);
    }

    [Fact]
    public void TheResultOnlyDependsOnTheNumberOfSteps()
    {
        AddGround();
        Entity box = AddBox(new Double3(0, 3, 0));
        Run(1, frame: Step); // 64 steps, one per frame
        Double3 oneByOne = Physics.Pose(box).Position;

        Physics.Teleport(box, new Double3(0, 3, 0), Quaternion.Identity);
        Physics.SetLinearVelocity(box, Vector3.Zero);
        Run(1, frame: Step * 4); // the same 64 steps, four to a frame

        Assert.Equal(oneByOne, Physics.Pose(box).Position);
    }
}

public class InterpolationTests : PhysicsFixture
{
    public InterpolationTests()
        : base(interpolate: true)
    {
    }

    [Fact]
    public void BetweenTwoStepsTheEntityShowsAnInBetweenPose()
    {
        Entity ball = AddBall(new Double3(0, 100, 0));
        Run(0.5);
        double exactBefore = Physics.Pose(ball).Position.Y;
        double shownBefore = WorldOf(ball).Position.Y;

        Physics.Update(Step * 1.5f); // one step and half of the next
        Ecs.Progress(Step);

        double exactAfter = Physics.Pose(ball).Position.Y;
        double shown = WorldOf(ball).Position.Y;
        Assert.True(exactAfter < exactBefore);
        Assert.True(shown > exactAfter, "the blend is behind the last step");
        Assert.True(shown < shownBefore, "but it keeps falling");
        Assert.Equal(1, Physics.Steps - (long)(0.5 / Step));
    }

    [Fact]
    public void ABodyThatCameToRestShowsWhereItIsNotABlend()
    {
        AddGround();
        Entity box = AddBox(new Double3(0, 2, 0));

        Run(6);

        Assert.False(Physics.IsAwake(box));
        Assert.Equal(Physics.Pose(box).Position.Y, WorldOf(box).Position.Y);
    }
}

public class QueryTests : PhysicsFixture
{
    [Fact]
    public void ARayHitsTheNearestBodyAndReportsTheSurface()
    {
        Entity near = AddBox(new Double3(0, 0.5, 5), motion: MotionKind.Static);
        AddBox(new Double3(0, 0.5, 10), motion: MotionKind.Static);

        var (hit, entity, point, normal, distance) = Physics.Raycast(new Double3(0, 0.5, 0), Vector3.UnitZ, 50);

        Assert.True(hit);
        Assert.Equal(near, entity);
        Assert.Equal(4.5f, distance, 0.001f);
        Assert.Equal(4.5, point.Z, 0.001);
        Assert.Equal(-1f, normal.Z, 0.01f);
    }

    [Fact]
    public void ARayHonoursTheLayerMaskTheIgnoredEntityAndTheDistance()
    {
        Entity wall = Spawn(new Double3(0, 0.5, 5));
        Physics.AddBody(wall, new BodyDesc { Shape = PhysicsShape.Box(new Vector3(0.5f)), Motion = MotionKind.Static, Layers = 1 << 3 });

        Assert.False(Physics.Raycast(new Double3(0, 0.5, 0), Vector3.UnitZ, 50, mask: 1 << 2).Hit); // not a layer asked for
        Assert.True(Physics.Raycast(new Double3(0, 0.5, 0), Vector3.UnitZ, 50, mask: 1 << 3).Hit);
        Assert.False(Physics.Raycast(new Double3(0, 0.5, 0), Vector3.UnitZ, 50, ignore: wall).Hit);
        Assert.False(Physics.Raycast(new Double3(0, 0.5, 0), Vector3.UnitZ, 3).Hit); // too short
        Assert.False(Physics.Raycast(new Double3(0, 0.5, 0), Vector3.Zero, 3).Hit);
    }

    [Fact]
    public void RaysWorkTenThousandKilometresOut()
    {
        const double Far = 1.0e7;
        Entity wall = AddBox(new Double3(Far, 0.5, Far + 5), motion: MotionKind.Static);

        var (hit, entity, point, _, distance) = Physics.Raycast(new Double3(Far, 0.5, Far), Vector3.UnitZ, 50);

        Assert.True(hit);
        Assert.Equal(wall, entity);
        Assert.Equal(Far + 4.5, point.Z, 0.001);
        Assert.Equal(4.5f, distance, 0.001f);
    }

    [Fact]
    public void SensorsAreNotHitByRays()
    {
        Entity sensor = Spawn(new Double3(0, 0.5, 5));
        Physics.AddBody(sensor, new BodyDesc { Shape = PhysicsShape.Box(new Vector3(0.5f)), Motion = MotionKind.Static, IsSensor = true });

        Assert.False(Physics.Raycast(new Double3(0, 0.5, 0), Vector3.UnitZ, 50).Hit);
    }

    [Fact]
    public void OverlapsFindBodiesInASphereAndABox()
    {
        Entity a = AddBox(new Double3(0, 0.5, 0), motion: MotionKind.Static);
        Entity b = AddBox(new Double3(3, 0.5, 0), motion: MotionKind.Static);
        AddBox(new Double3(20, 0.5, 0), motion: MotionKind.Static);

        Entity[] sphere = Physics.OverlapSphere(new Double3(1.5, 0.5, 0), 1.5f);
        Entity[] box = Physics.OverlapBox(new Double3(3, 0.5, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f), new Vector3(0.2f));

        Assert.Equal(new[] { a.Id, b.Id }, sphere.Select(e => e.Id).Order());
        Assert.Equal(new[] { b.Id }, box.Select(e => e.Id));
        Assert.Empty(Physics.OverlapSphere(new Double3(50, 0, 0), 1));
    }

    [Fact]
    public void ASphereCastStopsAtTheFirstBodyAndSaysHowFarItGot()
    {
        Entity wall = AddBox(new Double3(0, 1, 6), new Vector3(2, 1, 0.5f), motion: MotionKind.Static);

        var (hit, entity, point, normal, distance) = Physics.CastSphere(new Double3(0, 1, 0), Vector3.UnitZ, 0.5f, 20);

        Assert.True(hit);
        Assert.Equal(wall, entity);
        Assert.Equal(5f, distance, 0.02f); // wall face at z = 5.5, sphere radius 0.5
        Assert.Equal(5.5, point.Z, 0.02);
        Assert.True(normal.Z < -0.9f);
        Assert.False(Physics.CastSphere(new Double3(0, 1, 0), -Vector3.UnitZ, 0.5f, 20).Hit);
    }
}

public class EventTests : PhysicsFixture
{
    [Fact]
    public void BodiesThatAskForEventsReportWhenTheyHitSomething()
    {
        Entity ground = AddGround();
        Entity ball = AddBall(new Double3(0, 2, 0), events: true);
        Entity quiet = AddBall(new Double3(10, 2, 0));
        var began = new List<(Entity A, Entity B, Double3 Point, Vector3 Normal)>();
        Physics.ContactBegan.Connect((a, b, point, normal) => began.Add((a, b, point, normal)));

        Run(3);

        var contact = Assert.Single(began);
        Assert.True((contact.A == ball && contact.B == ground) || (contact.A == ground && contact.B == ball));
        Assert.InRange(contact.Point.Y, -0.1, 0.1);
        Assert.Equal(1f, Math.Abs(contact.Normal.Y), 0.05f);
        Assert.DoesNotContain(began, c => c.A == quiet || c.B == quiet);
    }

    [Fact]
    public void ABodyWithEventsReportsWakingAndSleeping()
    {
        AddGround();
        Entity ball = AddBall(new Double3(0, 1.5, 0), events: true);
        var woke = new List<Entity>();
        var slept = new List<Entity>();
        Physics.BodyWoke.Connect(woke.Add);
        Physics.BodySlept.Connect(slept.Add);

        Run(5);

        Assert.Contains(ball, slept);
        Physics.Wake(ball);
        Run(0.1);
        Assert.Contains(ball, woke);
    }

    [Fact]
    public void ASensorReportsWhatEntersAndLeaves()
    {
        Physics.Gravity = Vector3.Zero;
        Entity sensor = Spawn(new Double3(0, 0, 0));
        Physics.AddBody(sensor, new BodyDesc { Shape = PhysicsShape.Box(new Vector3(2)), Motion = MotionKind.Static, IsSensor = true });
        Entity ball = AddBall(new Double3(-6, 0, 0));
        Physics.SetLinearVelocity(ball, new Vector3(4, 0, 0)); // through the box in about 3 seconds
        var entered = new List<(Entity, Entity)>();
        var exited = new List<(Entity, Entity)>();
        Physics.SensorEntered.Connect((s, o) => entered.Add((s, o)));
        Physics.SensorExited.Connect((s, o) => exited.Add((s, o)));
        var contacts = new List<Entity>();
        Physics.ContactBegan.Connect((a, _, _, _) => contacts.Add(a));

        Run(4);

        Assert.Equal([(sensor, ball)], entered);
        Assert.Equal([(sensor, ball)], exited);
        Assert.Empty(contacts); // a sensor is not a collision
        Assert.True(WorldOf(ball).Position.X > 5, "the ball went straight through");
    }

    [Fact]
    public void LayersDecideWhoCollides()
    {
        // Two balls fall on a floor that is only in layer 2: the one whose mask lacks layer 2 falls through.
        Entity floor = Spawn(new Double3(0, -1, 0));
        Physics.AddBody(floor, new BodyDesc { Shape = PhysicsShape.Box(new Vector3(20, 1, 20)), Motion = MotionKind.Static, Layers = 1 << 2 });
        Entity solid = Spawn(new Double3(0, 2, 0));
        Physics.AddBody(solid, new BodyDesc { Shape = PhysicsShape.Sphere(0.5f), Layers = 1 << 0, Mask = 1 << 2 });
        Entity ghost = Spawn(new Double3(5, 2, 0));
        Physics.AddBody(ghost, new BodyDesc { Shape = PhysicsShape.Sphere(0.5f), Layers = 1 << 0, Mask = 1 << 3 });
        Entity unseen = Spawn(new Double3(-5, 2, 0));
        Physics.AddBody(unseen, new BodyDesc { Shape = PhysicsShape.Sphere(0.5f), Layers = 1 << 1, Mask = 1 << 2 });

        Run(3);

        Assert.Equal(0.5, WorldOf(solid).Position.Y, 0.03);
        Assert.True(WorldOf(ghost).Position.Y < -5, "its mask does not hold the floor's layer");
        Assert.Equal(0.5, WorldOf(unseen).Position.Y, 0.03);
    }
}

public class ShapeTests : PhysicsFixture
{
    [Fact]
    public void ShapesKnowTheirVolume()
    {
        Assert.Equal(1f, PhysicsShape.Box(new Vector3(0.5f)).Volume, 0.001f);
        Assert.Equal(4f / 3f * MathF.PI, PhysicsShape.Sphere(1).Volume, 0.01f);
        Assert.Equal(MathF.PI * 4 * 2, PhysicsShape.Cylinder(1, 2).Volume, 0.05f);
        Assert.Equal((4f / 3f * MathF.PI) + (MathF.PI * 2 * 1), PhysicsShape.Capsule(1, 1).Volume, 0.05f);
        PhysicsShape cube = PhysicsShape.ConvexHull([new(-1, -1, -1), new(1, -1, -1), new(-1, 1, -1), new(1, 1, -1), new(-1, -1, 1), new(1, -1, 1), new(-1, 1, 1), new(1, 1, 1)]);
        Assert.Equal(8f, cube.Volume, 0.05f);
        Assert.Equal("convex_hull", cube.Kind);
    }

    [Fact]
    public void ACompoundHasTheCenterOfMassOfItsParts()
    {
        PhysicsShape ball = PhysicsShape.Sphere(0.5f);
        PhysicsShape both = PhysicsShape.Compound([ball, ball], [new Vector3(-2, 0, 0), new Vector3(2, 0, 0)], []);

        Assert.Equal(0, both.CenterOfMass.X, 0.01f);
        Assert.Equal(2 * ball.Volume, both.Volume, 0.01f);
    }

    [Fact]
    public void AnOffsetShapeMovesTheBodiesOriginRelativeToIt()
    {
        AddGround();
        // A box whose origin is at its bottom: the entity rests at y = 0.
        PhysicsShape standing = PhysicsShape.Offset(PhysicsShape.Box(new Vector3(0.5f)), new Vector3(0, 0.5f, 0), Quaternion.Identity);
        Entity post = Spawn(new Double3(0, 3, 0));
        Physics.AddBody(post, new BodyDesc { Shape = standing });

        Run(4);

        Assert.Equal(0.0, WorldOf(post).Position.Y, 0.03);
    }

    [Fact]
    public void AHeightFieldAndAMeshCarryBalls()
    {
        int size = 9;
        var heights = new float[size * size];
        Array.Fill(heights, 2f); // a flat plateau two meters up
        Entity field = Spawn(new Double3(0, 0, 0));
        Physics.AddBody(field, new BodyDesc { Shape = PhysicsShape.HeightField(heights, size, new Vector3(-4, 0, -4), Vector3.One), Motion = MotionKind.Static });
        Entity onField = AddBall(new Double3(0, 6, 0));

        PhysicsShape floor = PhysicsShape.Mesh([new(-5, 0, -5), new(5, 0, -5), new(5, 0, 5), new(-5, 0, 5)], [0, 2, 1, 0, 3, 2]);
        Entity mesh = Spawn(new Double3(100, 10, 0));
        Physics.AddBody(mesh, new BodyDesc { Shape = floor, Motion = MotionKind.Static });
        Entity onMesh = AddBall(new Double3(100, 14, 0));

        Run(4);

        Assert.Equal(2.5, WorldOf(onField).Position.Y, 0.05);
        Assert.Equal(10.5, WorldOf(onMesh).Position.Y, 0.05);
    }

    [Fact]
    public void ShapeFunctionsCheckTheirArguments()
    {
        Assert.Throws<ValueException>(() => PhysicsShape.Sphere(0));
        Assert.Throws<ValueException>(() => PhysicsShape.Box(new Vector3(1, -1, 1)));
        Assert.Throws<ValueException>(() => PhysicsShape.Capsule(float.NaN, 1));
        Assert.Throws<ValueException>(() => PhysicsShape.ConvexHull([Vector3.Zero, Vector3.One]));
        Assert.Throws<ValueException>(() => PhysicsShape.Mesh([Vector3.Zero, Vector3.One, Vector3.UnitX], [0, 1]));
        Assert.Throws<ValueException>(() => PhysicsShape.Mesh([Vector3.Zero, Vector3.One, Vector3.UnitX], [0, 1, 7]));
        Assert.Throws<ValueException>(() => PhysicsShape.HeightField(new float[5], 2, Vector3.Zero, Vector3.One));
        Assert.Throws<ValueException>(() => PhysicsShape.Compound([], [], []));
        Assert.Throws<ValueException>(() => PhysicsShape.Scaled(PhysicsShape.Sphere(1), new Vector3(1, 2, 1)));
        Assert.Equal(8f, PhysicsShape.Scaled(PhysicsShape.Box(new Vector3(0.5f)), new Vector3(2)).Volume, 0.01f);
    }

    [Fact]
    public void ADestroyedShapeLeavesTheBodiesThatUseItAlone()
    {
        AddGround();
        PhysicsShape shape = PhysicsShape.Sphere(0.5f);
        Entity ball = Spawn(new Double3(0, 2, 0));
        Physics.AddBody(ball, new BodyDesc { Shape = shape });

        shape.Destroy();
        Run(3);

        Assert.Equal(0.5, WorldOf(ball).Position.Y, 0.03);
        Assert.Throws<ObjectDisposedException>(() => _ = shape.Volume);
    }
}

public class DeterminismTests
{
    private sealed class Scene : PhysicsFixture
    {
        public Scene(int threads)
            : base(threads: threads)
        {
        }

        public ulong Simulate()
        {
            AddGround();
            var entities = new List<Entity>();
            for (int i = 0; i < 12; i++)
            {
                Entity e = Spawn(new Double3((i % 4 * 0.7) - 1, 1 + (i * 0.9), (i % 3) * 0.6));
                Physics.AddBody(e, new BodyDesc
                {
                    Shape = i % 2 == 0 ? PhysicsShape.Sphere(0.4f) : PhysicsShape.Box(new Vector3(0.4f, 0.3f, 0.5f)),
                    Restitution = 0.4f,
                    LinearVelocity = new Vector3((i % 5) - 2, 0, 1 - (i % 3)),
                });
                entities.Add(e);
            }

            Run(5);

            ulong hash = 14695981039346656037UL;
            foreach (Entity e in entities)
            {
                (Double3 p, Quaternion q) = Physics.Pose(e);
                foreach (double v in new[] { p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W })
                {
                    hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(v)) * 1099511628211UL;
                }
            }

            return hash;
        }
    }

    [Fact]
    public void ASimulationGivesTheSameBitsWhateverTheNumberOfThreads()
    {
        ulong one, four;
        using (var scene = new Scene(1))
        {
            one = scene.Simulate();
        }

        using (var scene = new Scene(4))
        {
            four = scene.Simulate();
        }

        Assert.Equal(one, four);
    }
}

public class CharacterTests : PhysicsFixture
{
    public CharacterTests()
        : base(interpolate: true)
    {
    }

    private CharacterController Walker(Double3 feet)
    {
        Entity entity = Spawn(feet);
        return Physics.CreateCharacter(entity, 1.8f, 0.4f);
    }

    [Fact]
    public void ACharacterWalksOnTheGroundAtTheVelocityAskedFor()
    {
        AddGround();
        CharacterController walker = Walker(new Double3(0, 0.2, 0));
        walker.DesiredVelocity = new Vector3(3, 0, 0);

        Run(2);

        WorldTransform world = WorldOf(walker.Entity);
        Assert.Equal(6, world.Position.X, 0.25);
        Assert.Equal(0, world.Position.Y, 0.05);
        Assert.True(walker.IsOnGround);
        Assert.Equal(3f, walker.Velocity.X, 0.2f);
    }

    [Fact]
    public void ACharacterFallsAndLands()
    {
        AddGround();
        CharacterController walker = Walker(new Double3(0, 5, 0));
        Assert.False(walker.IsOnGround);

        Run(2);

        Assert.Equal(0, WorldOf(walker.Entity).Position.Y, 0.05);
        Assert.True(walker.IsOnGround);
    }

    [Fact]
    public void AWallStopsACharacterAndItSlidesAlongIt()
    {
        AddGround();
        AddBox(new Double3(5, 1, 0), new Vector3(0.5f, 1, 20), motion: MotionKind.Static); // a wall at x = 4.5..5.5
        CharacterController walker = Walker(new Double3(0, 0.1, 0));
        walker.DesiredVelocity = new Vector3(3, 0, 3); // into the wall and along it

        Run(2);

        Double3 position = WorldOf(walker.Entity).Position;
        Assert.InRange(position.X, 3.9, 4.15); // 4.5 minus the radius
        Assert.True(position.Z > 4, "slid along the wall");
    }

    [Fact]
    public void ACharacterJumpsOnlyFromTheGround()
    {
        AddGround();
        CharacterController walker = Walker(new Double3(0, 0.1, 0));
        Run(0.5);
        double peak = 0;

        walker.Jump(5);
        for (int i = 0; i < 64; i++)
        {
            Physics.Update(Step);
            Ecs.Progress(Step);
            peak = Math.Max(peak, WorldOf(walker.Entity).Position.Y);
        }

        Assert.InRange(peak, 1.0, 1.4); // v²/2g = 1.27
        Run(0.2); // the jump takes 2v/g = 1.02 s
        Assert.Equal(0, WorldOf(walker.Entity).Position.Y, 0.05);
    }

    [Fact]
    public void AJumpRequestedInTheAirIsLost()
    {
        AddGround();
        CharacterController walker = Walker(new Double3(0, 4, 0));
        Run(0.1);

        walker.Jump(8); // falling: nothing to push off
        Run(0.2);

        Assert.True(walker.Velocity.Y < 0, "still falling");
    }

    [Fact]
    public void ACharacterClimbsAStairStep()
    {
        AddGround();
        AddBox(new Double3(4, 0.15, 0), new Vector3(0.5f, 0.15f, 20), motion: MotionKind.Static); // a 0.3 m step at x = 3.5..4.5
        CharacterController walker = Walker(new Double3(0, 0.1, 0));
        walker.DesiredVelocity = new Vector3(2, 0, 0);
        double highest = 0;

        for (int i = 0; i < 3 * 64; i++)
        {
            Physics.Update(Step);
            Ecs.Progress(Step);
            highest = Math.Max(highest, WorldOf(walker.Entity).Position.Y);
        }

        Assert.True(WorldOf(walker.Entity).Position.X > 5, "got over the step and down the other side");
        Assert.Equal(0.3, highest, 0.05); // stood on top of it
    }

    [Fact]
    public void TeleportingMovesTheCharacterAtOnce()
    {
        AddGround();
        CharacterController walker = Walker(new Double3(0, 0.1, 0));

        walker.Teleport(new Double3(30, 0.1, -4));

        Assert.Equal(new Double3(30, 0.1, -4), Ecs.Get<Transform>(walker.Entity).Position);
        Run(1);
        Assert.Equal(30, WorldOf(walker.Entity).Position.X, 0.01);
    }

    [Fact]
    public void ACharacterCanWalkTenThousandKilometresOut()
    {
        const double Far = 1.0e7;
        AddGround(top: 0, x: Far);
        CharacterController walker = Walker(new Double3(Far, 0.1, 0));
        walker.DesiredVelocity = new Vector3(2, 0, 0);

        Run(2);

        Assert.Equal(Far + 4, WorldOf(walker.Entity).Position.X, 0.25);
        Assert.Equal(0, WorldOf(walker.Entity).Position.Y, 0.05);
    }

    [Fact]
    public void ADestroyedCharacterStopsFollowing()
    {
        AddGround();
        CharacterController walker = Walker(new Double3(0, 0.1, 0));
        walker.DesiredVelocity = new Vector3(3, 0, 0);
        Run(0.5);

        walker.Destroy();
        double x = WorldOf(walker.Entity).Position.X;
        Run(0.5);

        Assert.Equal(x, WorldOf(walker.Entity).Position.X);
        Assert.Throws<ValueException>(() => Physics.CreateCharacter(walker.Entity, 0.5f, 0.4f));
    }
}

public class PauseTests : PhysicsFixture
{
    [Fact]
    public void APausedWorldDoesNotStepAndResumesWhereItStopped()
    {
        AddGround();
        Entity box = AddBox(new Double3(0, 5, 0));
        Run(0.5);
        double fell = WorldOf(box).Position.Y;
        Assert.True(fell < 5);

        Physics.Paused = true;
        long steps = Physics.Steps;
        Run(1);
        Assert.Equal(steps, Physics.Steps);
        Assert.Equal(fell, WorldOf(box).Position.Y);

        Physics.Paused = false;
        Run(0.25);
        Assert.True(WorldOf(box).Position.Y < fell);
    }
}
