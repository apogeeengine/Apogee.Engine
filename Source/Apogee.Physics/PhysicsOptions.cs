using System;
using System.Numerics;

namespace Apogee.Physics;

/// <summary>How a <see cref="PhysicsWorld"/> is made. The defaults suit heavy vehicles on a large map.</summary>
public sealed class PhysicsOptions
{
    /// <summary>The most bodies the world holds (Jolt allocates for them up front).</summary>
    public int MaxBodies { get; init; } = 10240;

    public int MaxBodyPairs { get; init; } = 65536;

    public int MaxContactConstraints { get; init; } = 20480;

    /// <summary>Memory Jolt takes for the working data of one update.</summary>
    public int TempBufferBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Worker threads of the job system; 0 uses one less than the machine has (at least one). The results do not depend on it.</summary>
    public int ThreadCount { get; init; }

    public Vector3 Gravity { get; init; } = new(0, -9.81f, 0);

    /// <summary>Fixed steps per second. Results depend on it, so a game picks one and keeps it.</summary>
    public int StepRate { get; init; } = 60;

    /// <summary>The most steps one <see cref="PhysicsWorld.Update"/> runs: a slow frame slows the simulation down instead of spiralling.</summary>
    public int MaxSubsteps { get; init; } = 4;

    /// <summary>Velocity solver iterations per step (Jolt's default is 10).</summary>
    public int VelocitySteps { get; init; } = 10;

    /// <summary>Position solver iterations per step (Jolt's default is 2).</summary>
    public int PositionSteps { get; init; } = 2;

    /// <summary>How many contact and activation events one step can queue before the rest is dropped (counted in <see cref="PhysicsWorld.DroppedEvents"/>).</summary>
    public int EventCapacity { get; init; } = 4096;

    /// <summary>Show the bodies between the last two steps instead of at the last one, so motion is smooth at any frame rate (see <see cref="PhysicsWorld.Interpolate"/>).</summary>
    public bool Interpolate { get; init; } = true;

    internal int ResolvedThreads => ThreadCount > 0 ? ThreadCount : Math.Max(1, Environment.ProcessorCount - 1);
}
