using System;
using Apogee.Core.Graphics;
using Apogee.Core.Numerics;
using Apogee.Native.Jolt;
using Apogee.Reflection;
using JoltApi = Apogee.Native.Jolt.Jolt;

namespace Apogee.Physics;

/// <summary>What <see cref="PhysicsWorld.DrawDebug"/> puts in a <see cref="DebugDraw"/>.</summary>
public sealed record PhysicsDebugOptions
{
    /// <summary>Every body's shape.</summary>
    public bool Shapes { get; init; } = true;

    /// <summary>Shapes as lines instead of solid triangles; lines are what a debug view usually wants, since solid shapes hide what they overlap.</summary>
    public bool Wireframe { get; init; } = true;

    /// <summary>Static, kinematic and dynamic bodies in colors of their own, sleeping ones darker.</summary>
    public bool ColorByMotion { get; init; } = true;

    public bool BoundingBoxes { get; init; }

    public bool CentersOfMass { get; init; }

    public bool Velocities { get; init; }

    /// <summary>Joints, drawbars and vehicles (wheels, suspension and contact points).</summary>
    public bool Constraints { get; init; } = true;

    public bool ConstraintLimits { get; init; }

    /// <summary>Character controllers' capsules.</summary>
    public bool Characters { get; init; } = true;
}

public sealed unsafe partial class PhysicsWorld
{
    /// <summary>
    /// Draw the bodies, joints and characters over the world (wireframes, colored by what each body is): the host that renders the world calls <see cref="DrawDebug"/> when this is on. Scripts turn it on
    /// with <c>physics.debug_draw = true</c>; the dev overlay has a checkbox.
    /// </summary>
    [Property]
    public bool DebugDraw { get; set; }

    /// <summary>
    /// Adds the picture of the world (<paramref name="options"/> says what) to <paramref name="draw"/>, in world positions. <paramref name="camera"/> picks the level of detail of round shapes.
    /// The drawing is of the state the last <see cref="Update"/> left, not interpolated between steps.
    /// </summary>
    public void DrawDebug(DebugDraw draw, PhysicsDebugOptions? options = null, Double3 camera = default)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ThrowIfDisposed();
        options ??= new PhysicsDebugOptions();
        var settings = new AjDebugSettings
        {
            Shapes = options.Shapes,
            Wireframe = options.Wireframe,
            ColorByMotion = options.ColorByMotion,
            BoundingBoxes = options.BoundingBoxes,
            CenterOfMass = options.CentersOfMass,
            Velocities = options.Velocities,
            Constraints = options.Constraints,
            ConstraintLimits = options.ConstraintLimits,
        };

        JoltApi.DebugClear();
        JoltApi.WorldDrawDebug(Native, &settings, new AjRVec3 { X = camera.X, Y = camera.Y, Z = camera.Z });
        if (options.Characters)
        {
            foreach (CharacterController character in _characters)
            {
                JoltApi.CharacterDrawDebug(character.Native, options.Wireframe);
            }
        }

        // Jolt's lines and triangles have the layout of DebugLine and DebugTriangle (a test pins it), so they are copied in as they are.
        draw.AddLines(new ReadOnlySpan<DebugLine>(JoltApi.DebugLines(), (int)JoltApi.DebugLineCount()));
        draw.AddTriangles(new ReadOnlySpan<DebugTriangle>(JoltApi.DebugTriangles(), (int)JoltApi.DebugTriangleCount()));
        JoltApi.DebugClear();
    }
}
