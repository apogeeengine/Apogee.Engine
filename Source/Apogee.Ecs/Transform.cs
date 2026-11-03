using System.Numerics;
using Apogee.Core.Numerics;
using Apogee.Reflection;

namespace Apogee.Ecs;

#pragma warning disable CA1051 // The fields of a component are its memory layout.

/// <summary>
/// Where an entity is, relative to its parent (the <c>ChildOf</c> relationship); for an entity with no parent, in the world. The position
/// is a <see cref="Double3"/>: the world is double precision, so a position stays exact for thousands of kilometres. Set the
/// <see cref="Scale"/> to one: a zeroed transform (from <c>Add</c>) has no size. the world's transform system keeps
/// <see cref="WorldTransform"/> up to date from it.
/// </summary>
[Component]
public struct Transform
{
    public static Transform Identity => new(Double3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>Relative to the parent; scripts see it as a <c>vector</c>.</summary>
    [Property(Step = 0.05)]
    public Double3 Position;

    public Quaternion Rotation;

    /// <summary>Per axis; there is no shear, see <see cref="Trs"/>.</summary>
    [Property(Step = 0.01)]
    public Vector3 Scale;

    public Transform(Double3 position, Quaternion rotation, Vector3 scale)
    {
        Position = position;
        Rotation = rotation;
        Scale = scale;
    }

    public static Transform At(Double3 position) => new(position, Quaternion.Identity, Vector3.One);

    public readonly Trs ToTrs() => new(Position, Rotation, Scale);

    public static Transform From(in Trs value) => new(value.Position, value.Rotation, value.Scale);
}

/// <summary>
/// The same placement in world space, computed from <see cref="Transform"/> and the parents' by the world's transform system
/// once a frame. Read it; changes to it are overwritten.
/// </summary>
[Component]
public struct WorldTransform
{
    [Property(ReadOnly = true)]
    public Double3 Position;

    [Property(ReadOnly = true)]
    public Quaternion Rotation;

    [Property(ReadOnly = true)]
    public Vector3 Scale;

    public readonly Trs ToTrs() => new(Position, Rotation, Scale);

    public static WorldTransform From(in Trs value) => new() { Position = value.Position, Rotation = value.Rotation, Scale = value.Scale };
}

#pragma warning restore CA1051
