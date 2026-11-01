using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Apogee.Core.Numerics;

/// <summary>
/// A position in the double-precision world: three <c>double</c>s (24 bytes). Float has 24 bits of precision, so at 5 km from
/// the origin a <c>float</c> position moves in 0.5 mm steps; a <c>double</c> keeps sub-micrometre steps for thousands of kilometres.
/// Only positions are double. Directions, rotations, scales and everything the GPU sees stay <c>float</c>: positions are turned into
/// float offsets from a nearby origin (the camera) with <see cref="RelativeTo"/> before they are used. Plain scalar code on purpose:
/// positions are added and subtracted, not crunched in bulk.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Double3(double x, double y, double z) : IEquatable<Double3>
{
    public static Double3 Zero => default;

    public static readonly Double3 One = new(1, 1, 1);

    public double X { get; } = x;

    public double Y { get; } = y;

    public double Z { get; } = z;

    public Double3(double value)
        : this(value, value, value)
    {
    }

    public static implicit operator Double3(Vector3 value) => new(value.X, value.Y, value.Z);

    /// <summary>Narrows to float; positions far from the origin lose precision here, which is what <see cref="RelativeTo"/> avoids.</summary>
    public static explicit operator Vector3(Double3 value) => new((float)value.X, (float)value.Y, (float)value.Z);

    public static Double3 operator +(Double3 a, Double3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Double3 operator +(Double3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Double3 operator -(Double3 a, Double3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Double3 operator -(Double3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Double3 operator -(Double3 a) => new(-a.X, -a.Y, -a.Z);

    public static Double3 operator *(Double3 a, double b) => new(a.X * b, a.Y * b, a.Z * b);

    public static Double3 operator *(double a, Double3 b) => b * a;

    public static Double3 operator /(Double3 a, double b) => new(a.X / b, a.Y / b, a.Z / b);

    public static bool operator ==(Double3 a, Double3 b) => a.Equals(b);

    public static bool operator !=(Double3 a, Double3 b) => !a.Equals(b);

    public double LengthSquared() => (X * X) + (Y * Y) + (Z * Z);

    public double Length() => Math.Sqrt(LengthSquared());

    public static double Dot(Double3 a, Double3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static double DistanceSquared(Double3 a, Double3 b) => (a - b).LengthSquared();

    public static double Distance(Double3 a, Double3 b) => (a - b).Length();

    public static Double3 Lerp(Double3 a, Double3 b, double t) => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));

    public static Double3 Min(Double3 a, Double3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    public static Double3 Max(Double3 a, Double3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>
    /// The offset from <paramref name="origin"/> to this position as floats. The subtraction is done in double, so the result is
    /// exact to float precision however far both are from the world's origin: this is how positions reach the GPU.
    /// </summary>
    public Vector3 RelativeTo(Double3 origin) => new((float)(X - origin.X), (float)(Y - origin.Y), (float)(Z - origin.Z));

    public bool Equals(Double3 other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is Double3 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public override string ToString() => FormattableString.Invariant($"({X}, {Y}, {Z})");
}
