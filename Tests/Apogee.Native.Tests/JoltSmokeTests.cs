using System;
using System.Collections.Generic;
using Apogee.Native.Jolt;
using Xunit;
using JoltApi = Apogee.Native.Jolt.Jolt;

namespace Apogee.Native.Tests;

/// <summary>Our C API over Jolt, as the engine drives it: one world, bodies in doubles, and the event queue.</summary>
public unsafe class JoltSmokeTests : IDisposable
{
    private const float Step = 1f / 60f;

    private readonly AjWorld* _world;
    private readonly List<nint> _shapes = [];

    public JoltSmokeTests()
    {
        NativeRequirements.Require(typeof(JoltApi).Assembly, "jolt");
        Assert.True(JoltApi.Init());
        _world = NewWorld(threads: 2);
    }

    private static AjWorld* NewWorld(int threads, uint eventCapacity = 64)
    {
        var desc = new AjWorldDesc
        {
            MaxBodies = 1024,
            MaxBodyPairs = 4096,
            MaxContactConstraints = 2048,
            TempBufferBytes = 16 * 1024 * 1024,
            Threads = threads,
            EventCapacity = eventCapacity,
            VelocitySteps = 10,
            PositionSteps = 2,
            Gravity = new AjVec3 { Y = -9.81f },
        };
        AjWorld* world = JoltApi.WorldCreate(&desc);
        Assert.True(world != null);
        return world;
    }

    public void Dispose()
    {
        JoltApi.WorldDestroy(_world);
        foreach (nint shape in _shapes)
        {
            JoltApi.ShapeRelease((AjShape*)shape);
        }

        JoltApi.Shutdown();
        GC.SuppressFinalize(this);
    }

    private AjShape* Own(AjShape* shape)
    {
        Assert.True(shape != null);
        _shapes.Add((nint)shape);
        return shape;
    }

    private AjShape* Sphere(float radius) => Own(JoltApi.ShapeSphere(radius));

    private AjShape* Box(float x, float y, float z) => Own(JoltApi.ShapeBox(new AjVec3 { X = x, Y = y, Z = z }, 0.05f));

    private uint AddBody(AjShape* shape, double x, double y, double z, AjMotionType motion = AjMotionType.Dynamic, bool events = false, float restitution = 0, AjVec3 velocity = default)
    {
        var desc = new AjBodyDesc
        {
            Shape = shape,
            Position = new AjRVec3 { X = x, Y = y, Z = z },
            Rotation = new AjQuat { W = 1 },
            Motion = motion,
            Layers = 1,
            Mask = (uint)JoltApi.LayerBits,
            Friction = 0.2f,
            Restitution = restitution,
            LinearDamping = 0.05f,
            AngularDamping = 0.05f,
            GravityFactor = 1,
            Events = events,
            AllowSleeping = true,
            LinearVelocity = velocity,
        };
        uint id = JoltApi.BodyCreate(_world, &desc);
        Assert.NotEqual(unchecked((uint)JoltApi.InvalidBody), id);
        return id;
    }

    private uint AddGround() => AddBody(Box(100, 1, 100), 0, -1, 0, AjMotionType.Static);

    private void Simulate(int steps, AjWorld* world = null)
    {
        for (int i = 0; i < steps; i++)
        {
            Assert.Equal(0u, JoltApi.WorldUpdate(world == null ? _world : world, Step));
        }
    }

    private AjRVec3 PositionOf(uint body)
    {
        AjRVec3 position;
        AjQuat rotation;
        JoltApi.BodyGetPose(_world, body, &position, &rotation);
        return position;
    }

    [Fact]
    public void PositionsAreDoublesWhateverThePrecisionOfTheWorldsFloats()
    {
        Assert.Equal(24, sizeof(AjRVec3));

        // A billion metres, where a float cannot tell one metre from the next. The ball falls a second under gravity (9.81 m/s², about 4.9 m) and the drop is
        // still seen, so the library is built with JPH_DOUBLE_PRECISION.
        const double Far = 1.0e9;
        uint ball = AddBody(Sphere(0.5f), Far, Far, -Far);

        Simulate(60);

        AjRVec3 position = PositionOf(ball);
        Assert.Equal(Far, position.X);
        Assert.Equal(-Far, position.Z);
        Assert.InRange(Far - position.Y, 4.80, 5.0);
    }

    [Fact]
    public void ABodyComesToRestOnTheGround()
    {
        AddGround();
        uint ball = AddBody(Sphere(0.5f), 0, 3, 0);

        Simulate(240);

        Assert.Equal(0.5, PositionOf(ball).Y, 0.03); // minus the penetration slop
        Assert.False(JoltApi.BodyIsActive(_world, ball)); // it went to sleep
    }

    [Fact]
    public void ContactsOfBodiesThatAskForThemReachTheQueueAfterTheStep()
    {
        uint ground = AddGround();
        uint watched = AddBody(Sphere(0.5f), 0, 2, 0, events: true);
        uint ignored = AddBody(Sphere(0.5f), 10, 2, 0);

        Simulate(120);

        var taken = new List<AjEvent>();
        var buffer = new AjEvent[16];
        fixed (AjEvent* output = buffer)
        {
            uint count;
            while ((count = JoltApi.EventsTake(_world, output, (uint)buffer.Length)) > 0)
            {
                taken.AddRange(buffer.AsSpan(0, (int)count));
            }
        }

        Assert.Equal(0u, JoltApi.EventsDropped(_world));
        Assert.DoesNotContain(taken, e => e.Body1 == ignored || e.Body2 == ignored);

        // A watched body is activated when it is added and deactivated when it comes to rest, and Jolt ends the contacts of a body that goes to sleep, so the removal is the last
        // event of the pair: Activated, ContactAdded, BodyDeactivated, ContactRemoved.
        Assert.Equal([AjEventKind.BodyActivated, AjEventKind.ContactAdded, AjEventKind.BodyDeactivated, AjEventKind.ContactRemoved], taken.ConvertAll(e => (AjEventKind)e.Kind));
        Assert.Equal(watched, taken[0].Body1);
        AjEvent added = taken[1];
        Assert.True((added.Body1 == watched && added.Body2 == ground) || (added.Body1 == ground && added.Body2 == watched));
        Assert.Equal(1.0, Math.Abs(added.Normal.Y), 0.05);
        Assert.InRange(added.Position.Y, -0.1, 0.1); // the point where the ball touches the ground, in doubles
    }

    [Fact]
    public void ALimitedQueueCountsWhatDidNotFit()
    {
        AjWorld* small = NewWorld(threads: 1, eventCapacity: 1);
        try
        {
            AjShape* box = Box(100, 1, 100);
            AjShape* ball = Sphere(0.5f);
            var ground = new AjBodyDesc { Shape = box, Position = new AjRVec3 { Y = -1 }, Rotation = new AjQuat { W = 1 }, Motion = AjMotionType.Static, Layers = 1, Mask = (uint)JoltApi.LayerBits, Friction = 0.2f, GravityFactor = 1, AllowSleeping = true };
            Assert.NotEqual(unchecked((uint)JoltApi.InvalidBody), JoltApi.BodyCreate(small, &ground));
            for (int i = 0; i < 4; i++)
            {
                var desc = new AjBodyDesc { Shape = ball, Position = new AjRVec3 { X = i * 2, Y = 1 }, Rotation = new AjQuat { W = 1 }, Motion = AjMotionType.Dynamic, Layers = 1, Mask = (uint)JoltApi.LayerBits, Friction = 0.2f, GravityFactor = 1, Events = true, AllowSleeping = true };
                Assert.NotEqual(unchecked((uint)JoltApi.InvalidBody), JoltApi.BodyCreate(small, &desc));
            }

            Simulate(60, small);

            Assert.True(JoltApi.EventsDropped(small) > 0);
            Assert.Equal(0u, JoltApi.EventsDropped(small)); // read and cleared
        }
        finally
        {
            JoltApi.WorldDestroy(small);
        }
    }

    [Fact]
    public void BodiesWhosePairIsIgnoredPassThroughEachOtherAndNothingElse()
    {
        AddGround();
        uint lower = AddBody(Sphere(0.5f), 0, 1, 0);
        uint upper = AddBody(Sphere(0.5f), 0, 3, 0);
        uint other = AddBody(Sphere(0.5f), 5, 1, 0);
        uint stacked = AddBody(Sphere(0.5f), 5, 3, 0);
        JoltApi.IgnorePair(_world, lower, upper);

        Simulate(240);

        Assert.Equal(0.5, PositionOf(upper).Y, 0.05);   // fell through its partner and rests on the ground
        Assert.Equal(1.5, PositionOf(stacked).Y, 0.1);   // the pair that is not ignored stacks
        Assert.Equal(0.5, PositionOf(other).Y, 0.05);

        JoltApi.AllowPair(_world, lower, upper);
        JoltApi.BodySetPose(_world, upper, new AjRVec3 { X = 0, Y = 3, Z = 0 }, new AjQuat { W = 1 });
        JoltApi.BodySetLinearVelocity(_world, upper, default);
        Simulate(240);
        Assert.Equal(1.5, PositionOf(upper).Y, 0.1);     // allowed again: it lands on the other body
    }

    [Fact]
    public void LayersDecideWhoCollidesAndSensorsAreNotHit()
    {
        var floor = new AjBodyDesc { Shape = Box(20, 1, 20), Position = new AjRVec3 { Y = -1 }, Rotation = new AjQuat { W = 1 }, Motion = AjMotionType.Static, Layers = 1 << 2, Mask = (uint)JoltApi.LayerBits, Friction = 0.2f, GravityFactor = 1, AllowSleeping = true };
        Assert.NotEqual(unchecked((uint)JoltApi.InvalidBody), JoltApi.BodyCreate(_world, &floor));
        AjShape* ball = Sphere(0.5f);
        uint Add(double x, uint layers, uint mask)
        {
            var desc = new AjBodyDesc { Shape = ball, Position = new AjRVec3 { X = x, Y = 2 }, Rotation = new AjQuat { W = 1 }, Motion = AjMotionType.Dynamic, Layers = layers, Mask = mask, Friction = 0.2f, GravityFactor = 1, AllowSleeping = true };
            return JoltApi.BodyCreate(_world, &desc);
        }

        uint solid = Add(0, 1 << 0, 1 << 2);
        uint ghost = Add(5, 1 << 0, 1 << 3);

        Simulate(180);

        Assert.Equal(0.5, PositionOf(solid).Y, 0.03);
        Assert.True(PositionOf(ghost).Y < -5, "its mask does not hold the floor's layer");

        AjRayHit hit;
        Assert.True(JoltApi.Raycast(_world, new AjRVec3 { Y = 5 }, new AjVec3 { Y = -10 }, 1 << 2, unchecked((uint)JoltApi.InvalidBody), &hit));
        Assert.Equal(0.5f, hit.Fraction, 0.02f); // the floor's top is at y = 0, halfway along the ray
        Assert.Equal(1f, hit.Normal.Y, 0.01f);
        Assert.False(JoltApi.Raycast(_world, new AjRVec3 { Y = 5 }, new AjVec3 { Y = -10 }, 1 << 3, unchecked((uint)JoltApi.InvalidBody), &hit));
    }

    [Fact]
    public void ASimulationIsTheSameEveryTimeAndOnEveryPlatform()
    {
        // JPH_CROSS_PLATFORM_DETERMINISTIC: bodies that bounce, slide and collide with each other give bit-identical results on every OS, compiler and instruction
        // set, so this hash is a constant. If it changes, the physics changed: check why before updating it.
        AddGround();
        for (int i = 0; i < 12; i++)
        {
            AjShape* shape = i % 2 == 0 ? Sphere(0.4f) : Box(0.4f, 0.3f, 0.5f);
            AddBody(shape, (i % 4 * 0.7) - 1, 1 + (i * 0.9), (i % 3) * 0.6, restitution: 0.4f, velocity: new AjVec3 { X = (i % 5) - 2, Z = 1 - (i % 3) });
        }

        Simulate(300);

        ulong hash = 14695981039346656037UL;
        for (uint id = 0; id < 13; id++)
        {
            AjRVec3 position = PositionOf(id);
            foreach (double v in new[] { position.X, position.Y, position.Z })
            {
                hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(v)) * 1099511628211UL;
            }
        }

        Assert.Equal(ExpectedHash, hash);
    }

    private const ulong ExpectedHash = 4701856666363133479UL; // recorded on Linux (GCC) with SSE 4.2 and unchanged by the move from JoltC to our own C API; the same on AVX2 and with 1, 2 or 7 threads
}
