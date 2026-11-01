using System;
using System.Diagnostics.CodeAnalysis;

namespace Apogee.Interop;

/// <summary>A C <c>bool</c> (one byte). Unlike <see cref="bool"/> it is blittable, so calls need no marshalling.</summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Named after C's bool; VB is not a target.")]
public readonly struct CBool : IEquatable<CBool>
{
    private readonly byte _value;

    public CBool(bool value) => _value = value ? (byte)1 : (byte)0;

    public static implicit operator bool(CBool value) => value._value != 0;

    public static implicit operator CBool(bool value) => new(value);

    public bool Equals(CBool other) => (_value != 0) == (other._value != 0);

    public override bool Equals(object? obj) => obj is CBool other && Equals(other);

    public override int GetHashCode() => _value != 0 ? 1 : 0;

    public static bool operator ==(CBool left, CBool right) => left.Equals(right);

    public static bool operator !=(CBool left, CBool right) => !left.Equals(right);

    public override string ToString() => _value != 0 ? "true" : "false";
}
