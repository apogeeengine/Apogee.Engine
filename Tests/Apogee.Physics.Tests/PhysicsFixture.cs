using System;
using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Native.Tests;
using Apogee.Reflection;

namespace Apogee.Physics.Tests;

/// <summary>An ECS world with a physics world, one worker thread (the results do not depend on it) and a step of 1/64 s, which is exact in binary so that frame times add up.</summary>
public abstract class PhysicsFixture : IDisposable
{
    protected const float Step = 1f / 64;

    protected PhysicsFixture(bool interpolate = false, int threads = 1)
    {
        NativeRequirements.Require(typeof(Apogee.Native.Flecs.Ecs).Assembly, "flecs");
        NativeRequirements.Require(typeof(Apogee.Native.Jolt.Jolt).Assembly, "jolt");
        Ecs = new World();
        Physics = new PhysicsWorld(Ecs, new PhysicsOptions { ThreadCount = threads, StepRate = 64, Interpolate = interpolate, TempBufferBytes = 4 * 1024 * 1024, MaxBodies = 2048 });
    }

    protected World Ecs { get; }

    protected PhysicsWorld Physics { get; }

    public void Dispose()
    {
        Physics.Dispose();
        Ecs.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Runs whole frames of <paramref name="seconds"/> in total: physics, then the ECS (which composes the world transforms).</summary>
    protected void Run(double seconds, float frame = Step)
    {
        for (double t = 0; t < seconds - 1e-9; t += frame)
        {
            Physics.Update(frame);
            Ecs.Progress(frame);
        }
    }

    protected Entity Spawn(Double3 position, Quaternion? rotation = null, Vector3? scale = null, Entity parent = default)
    {
        Entity entity = Ecs.CreateEntity(parent: parent);
        Ecs.Set(entity, new Transform(position, rotation ?? Quaternion.Identity, scale ?? Vector3.One));
        return entity;
    }

    protected Entity AddGround(double top = 0, double x = 0, double z = 0)
    {
        Entity ground = Spawn(new Double3(x, top - 1, z));
        Physics.AddBody(ground, new BodyDesc { Shape = PhysicsShape.Box(new Vector3(200, 1, 200)), Motion = MotionKind.Static });
        return ground;
    }

    protected Entity AddBox(Double3 position, Vector3? half = null, float mass = 0, MotionKind motion = MotionKind.Dynamic, bool events = false, Entity parent = default, Vector3? scale = null)
    {
        Entity box = Spawn(position, parent: parent, scale: scale);
        Physics.AddBody(box, new BodyDesc { Shape = PhysicsShape.Box(half ?? new Vector3(0.5f)), Motion = motion, Mass = mass, Events = events });
        return box;
    }

    protected Entity AddBall(Double3 position, float radius = 0.5f, bool events = false)
    {
        Entity ball = Spawn(position);
        Physics.AddBody(ball, new BodyDesc { Shape = PhysicsShape.Sphere(radius), Events = events });
        return ball;
    }

    protected WorldTransform WorldOf(Entity entity) => Ecs.Get<WorldTransform>(entity);
}
