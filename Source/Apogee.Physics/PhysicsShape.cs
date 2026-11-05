using System;
using System.Numerics;
using Apogee.Native.Jolt;
using Apogee.Reflection;
using JoltApi = Apogee.Native.Jolt.Jolt;

namespace Apogee.Physics;

/// <summary>
/// A collision shape: an immutable thing a body (or another shape) is made of, in the body's local space. Made with the static functions, given to <see cref="BodyDesc.Shape"/>,
/// and shared freely: many bodies can use one. Jolt counts references, so a shape that a script lets go of lives on in the bodies that use it.
/// </summary>
[ApogeeClass]
public sealed unsafe partial class PhysicsShape : EngineObject
{
    private AjShape* _shape;

    private PhysicsShape(AjShape* shape, string kind)
    {
        _shape = shape;
        Kind = kind;
    }

    /// <summary>What it is: <c>box</c>, <c>sphere</c>, <c>capsule</c>, <c>cylinder</c>, <c>convex_hull</c>, <c>mesh</c>, <c>height_field</c>, <c>compound</c>, <c>offset</c> or <c>scaled</c>.</summary>
    [Property]
    public string Kind { get; }

    /// <summary>The volume in cubic meters, which with the density gives a dynamic body its mass.</summary>
    [Property]
    public float Volume => JoltApi.ShapeVolume(Native);

    /// <summary>The shape's center of mass, in its local space.</summary>
    [Property]
    public Vector3 CenterOfMass
    {
        get => Convert.FromJolt(JoltApi.ShapeCenterOfMass(Native));
    }

    internal AjShape* Native => _shape != null ? _shape : throw new ObjectDisposedException(nameof(PhysicsShape));

    /// <summary>Whether the shape can be scaled by this much (a sphere only evenly, a mesh by anything).</summary>
    [Method]
    public bool IsValidScale(Vector3 scale)
    {
        return JoltApi.ShapeIsValidScale(Native, Convert.ToJolt(scale));
    }

    protected override void OnDestroyed()
    {
        if (_shape != null)
        {
            JoltApi.ShapeRelease(_shape);
            _shape = null;
        }
    }

    /// <summary>Takes over a shape Jolt made (with one reference, ours).</summary>
    internal static PhysicsShape Own(AjShape* shape, string kind) => shape != null ? new PhysicsShape(shape, kind) : throw new PhysicsException($"Jolt could not make the {kind} shape.");

    private static void Check(float value, string name)
    {
        if (!(value > 0) || !float.IsFinite(value))
        {
            throw new ValueException($"{name} must be a positive number, got {value}");
        }
    }

    // ---- Primitives -------------------------------------------------------------------------------------------------------------

    /// <summary>A box of these half extents, centered on the origin. The edges are rounded by <paramref name="convexRadius"/> (Jolt's default, 5 cm, is shrunk for small boxes).</summary>
    [Method]
    public static PhysicsShape Box(Vector3 halfExtents, float convexRadius = 0.05f)
    {
        PhysicsLibrary.EnsureInitialized();
        Check(halfExtents.X, "half extent x");
        Check(halfExtents.Y, "half extent y");
        Check(halfExtents.Z, "half extent z");
        return Own(JoltApi.ShapeBox(Convert.ToJolt(halfExtents), convexRadius), "box");
    }

    [Method]
    public static PhysicsShape Sphere(float radius)
    {
        PhysicsLibrary.EnsureInitialized();
        Check(radius, "radius");
        return Own(JoltApi.ShapeSphere(radius), "sphere");
    }

    /// <summary>A capsule along Y: a cylinder of <paramref name="halfHeight"/> (without the caps) with a hemisphere of <paramref name="radius"/> on each end.</summary>
    [Method]
    public static PhysicsShape Capsule(float halfHeight, float radius)
    {
        PhysicsLibrary.EnsureInitialized();
        Check(halfHeight, "half height");
        Check(radius, "radius");
        return Own(JoltApi.ShapeCapsule(halfHeight, radius), "capsule");
    }

    /// <summary>A cylinder along Y (wheels, barrels, posts).</summary>
    [Method]
    public static PhysicsShape Cylinder(float halfHeight, float radius)
    {
        PhysicsLibrary.EnsureInitialized();
        Check(halfHeight, "half height");
        Check(radius, "radius");
        return Own(JoltApi.ShapeCylinder(halfHeight, radius), "cylinder");
    }

    // ---- Meshes -----------------------------------------------------------------------------------------------------------------

    /// <summary>The convex hull of these points (at least four, not in one plane).</summary>
    [Method]
    public static PhysicsShape ConvexHull(Vector3[] points)
    {
        PhysicsLibrary.EnsureInitialized();
        if (points.Length < 4)
        {
            throw new ValueException("a convex hull needs at least 4 points");
        }

        var jolt = new AjVec3[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            jolt[i] = Convert.ToJolt(points[i]);
        }

        fixed (AjVec3* data = jolt)
        {
            return Own(JoltApi.ShapeConvexHull(data, (uint)jolt.Length), "convex_hull");
        }
    }

    /// <summary>A static triangle mesh (terrain, buildings): <paramref name="indices"/> are three per triangle. Meshes are only for static and kinematic bodies.</summary>
    [Method]
    public static PhysicsShape Mesh(Vector3[] vertices, int[] indices)
    {
        PhysicsLibrary.EnsureInitialized();
        if (indices.Length == 0 || indices.Length % 3 != 0)
        {
            throw new ValueException($"a mesh needs a multiple of 3 indices, got {indices.Length}");
        }

        var jolt = new AjVec3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            jolt[i] = Convert.ToJolt(vertices[i]);
        }

        foreach (int index in indices)
        {
            if ((uint)index >= (uint)vertices.Length)
            {
                throw new ValueException($"a triangle uses a vertex that does not exist ({index} of {vertices.Length})");
            }
        }

        var unsigned = new uint[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            unsigned[i] = (uint)indices[i];
        }

        fixed (AjVec3* vertexData = jolt)
        fixed (uint* indexData = unsigned)
        {
            return Own(JoltApi.ShapeMesh(vertexData, (uint)jolt.Length, indexData, (uint)(unsigned.Length / 3)), "mesh");
        }
    }

    /// <summary>
    /// A grid of heights (terrain): <paramref name="heights"/> is <paramref name="size"/> by <paramref name="size"/> samples row by row (a power of two plus one works best, at least 2), at
    /// <paramref name="offset"/> from the origin with the grid spaced by <paramref name="scale"/> in x and z and heights multiplied by its y. A height of at least 1e30 is a hole.
    /// </summary>
    [Method]
    public static PhysicsShape HeightField(float[] heights, int size, Vector3 offset, Vector3 scale)
    {
        PhysicsLibrary.EnsureInitialized();
        if (size < 2 || heights.Length != size * size)
        {
            throw new ValueException($"a height field of size {size} needs {size}x{size} heights, got {heights.Length}");
        }

        fixed (float* data = heights)
        {
            return Own(JoltApi.ShapeHeightField(data, (uint)size, Convert.ToJolt(offset), Convert.ToJolt(scale)), "height_field");
        }
    }

    // ---- Combining --------------------------------------------------------------------------------------------------------------

    /// <summary>Several shapes at fixed places in one: the child <c>i</c> at <paramref name="positions"/>[i] turned by <paramref name="rotations"/>[i] (identity when the list is shorter).</summary>
    [Method]
    public static PhysicsShape Compound(PhysicsShape[] children, Vector3[] positions, Quaternion[] rotations)
    {
        PhysicsLibrary.EnsureInitialized();
        if (children.Length == 0)
        {
            throw new ValueException("a compound shape needs at least one child");
        }

        if (positions.Length != children.Length)
        {
            throw new ValueException($"a compound shape needs a position for each of its {children.Length} children, got {positions.Length}");
        }

        var handles = new AjShape*[children.Length];
        var positionData = new AjVec3[children.Length];
        var rotationData = new AjQuat[children.Length];
        for (int i = 0; i < children.Length; i++)
        {
            handles[i] = children[i].Native;
            positionData[i] = Convert.ToJolt(positions[i]);
            rotationData[i] = Convert.ToJolt(i < rotations.Length ? rotations[i] : Quaternion.Identity);
        }

        fixed (AjShape** childData = handles)
        fixed (AjVec3* positionPointer = positionData)
        fixed (AjQuat* rotationPointer = rotationData)
        {
            return Own(JoltApi.ShapeStaticCompound(childData, positionPointer, rotationPointer, (uint)children.Length), "compound");
        }
    }

    /// <summary>The shape moved and turned inside its own space, which moves the body's origin relative to it (a box standing on the origin instead of centered on it).</summary>
    [Method]
    public static PhysicsShape Offset(PhysicsShape shape, Vector3 position, Quaternion rotation)
    {
        PhysicsLibrary.EnsureInitialized();
        return Own(JoltApi.ShapeOffset(shape.Native, Convert.ToJolt(position), Convert.ToJolt(rotation)), "offset");
    }

    /// <summary>The shape stretched by <paramref name="scale"/>; fails when the shape cannot be scaled so (<see cref="IsValidScale"/>).</summary>
    [Method]
    public static PhysicsShape Scaled(PhysicsShape shape, Vector3 scale)
    {
        PhysicsLibrary.EnsureInitialized();
        if (!shape.IsValidScale(scale))
        {
            throw new ValueException($"a {shape.Kind} cannot be scaled by ({scale.X}, {scale.Y}, {scale.Z})");
        }

        return Own(JoltApi.ShapeScaled(shape.Native, Convert.ToJolt(scale)), "scaled");
    }
}
