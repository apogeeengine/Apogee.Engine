using System.Numerics;
using System.Runtime.InteropServices;

namespace Apogee.Core.Numerics;

/// <summary>
/// A placement in the double-precision world: a <see cref="Double3"/> position, a rotation and a scale (not a matrix, so a position far
/// from the origin keeps its precision). A point is scaled, then rotated, then moved. Non-uniform scale is allowed, but there is no
/// shear: the scale of a child under a rotated, non-uniformly scaled parent is the product of the two scales, which is exact only when
/// the parent's scale is uniform or its rotation lines up with the child's axes (the usual rule of game engines).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Trs(Double3 position, Quaternion rotation, Vector3 scale)
{
    public static readonly Trs Identity = new(Double3.Zero, Quaternion.Identity, Vector3.One);

    public Double3 Position = position;

    public Quaternion Rotation = rotation;

    public Vector3 Scale = scale;

    /// <summary>This placement seen from <paramref name="parent"/>'s space, as a world placement: the child's own transform inside the parent's.</summary>
    public readonly Trs InWorldOf(in Trs parent) => new(
        parent.Position + Vector3.Transform(parent.Scale * (Vector3)Position, parent.Rotation),
        Quaternion.Concatenate(Rotation, parent.Rotation),
        parent.Scale * Scale);

    /// <summary>
    /// The inverse of <see cref="InWorldOf"/>: this world placement as a local placement inside <paramref name="parent"/> (the transform an entity needs under a new parent to stay where it is). The
    /// position is subtracted in double, so a far-away placement stays exact; it is exact under the conditions <see cref="InWorldOf"/> is.
    /// </summary>
    public readonly Trs InLocalOf(in Trs parent) => new(
        (Double3)parent.InverseTransformPoint(Position),
        Quaternion.Concatenate(Rotation, Quaternion.Inverse(parent.Rotation)),
        Scale / parent.Scale);

    /// <summary>A point of this placement's local space in the space around it.</summary>
    public readonly Double3 TransformPoint(Vector3 local) => Position + Vector3.Transform(Scale * local, Rotation);

    /// <summary>A point of the outer space in this placement's local space. The subtraction is done in double, so a far-away placement stays exact.</summary>
    public readonly Vector3 InverseTransformPoint(Double3 point)
    {
        Vector3 offset = (Vector3)(point - Position);
        return Vector3.Transform(offset, Quaternion.Inverse(Rotation)) / Scale;
    }

    /// <summary>A vector (a difference, not a position) scaled and rotated, without the translation.</summary>
    public readonly Vector3 TransformVector(Vector3 local) => Vector3.Transform(Scale * local, Rotation);

    /// <summary>
    /// The float matrix for the GPU, with the translation taken relative to <paramref name="origin"/> (the camera): row-vector, scale
    /// first, then rotation, then translation, as <see cref="Matrix4x4"/> composes.
    /// </summary>
    public readonly Matrix4x4 ToMatrix(Double3 origin) =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position.RelativeTo(origin));
}
