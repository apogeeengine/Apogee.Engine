using System;
using System.Numerics;
using Apogee.Reflection;
using Xunit;

namespace Apogee.Reflection.Tests;

public unsafe class FieldAccessTests
{
    private static void RoundTrip(FieldKind kind, Value value, Value expected)
    {
        // Odd offsets too: the layout of a script-defined component is packed by alignment, not by the CPU's liking.
        byte* data = stackalloc byte[64];
        foreach (int offset in new[] { 0, 1, 4 })
        {
            var field = new FieldDef("f", kind, offset);
            new Span<byte>(data, 64).Clear();
            FieldAccess.Write(data, field, value);
            Assert.Equal(expected, FieldAccess.Read(data, field));
        }
    }

    [Fact]
    public void EveryKindRoundTrips()
    {
        RoundTrip(FieldKind.Bool, Value.Bool(true), Value.Bool(true));
        RoundTrip(FieldKind.Int8, Value.Int(-5), Value.Int(-5));
        RoundTrip(FieldKind.UInt8, Value.Int(200), Value.Int(200));
        RoundTrip(FieldKind.Int16, Value.Int(-30000), Value.Int(-30000));
        RoundTrip(FieldKind.UInt16, Value.Int(60000), Value.Int(60000));
        RoundTrip(FieldKind.Int32, Value.Int(-2_000_000_000), Value.Int(-2_000_000_000));
        RoundTrip(FieldKind.UInt32, Value.Int(4_000_000_000), Value.Int(4_000_000_000));
        RoundTrip(FieldKind.Int64, Value.Int(long.MinValue + 1), Value.Int(long.MinValue + 1));
        RoundTrip(FieldKind.UInt64, Value.Int(123456789012), Value.Int(123456789012));
        RoundTrip(FieldKind.Float32, Value.Float(1.5), Value.Float(1.5));
        RoundTrip(FieldKind.Float64, Value.Float(Math.PI), Value.Float(Math.PI));
        RoundTrip(FieldKind.Vector3, Value.Vector(new Vector3(1, -2, 3.5f)), Value.Vector(new Vector3(1, -2, 3.5f)));
        RoundTrip(FieldKind.Double3, Value.Vector(new Vector3(1, -2, 3.5f)), Value.Vector(new Vector3(1, -2, 3.5f)));
        RoundTrip(FieldKind.Quaternion, Value.Quat(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)), Value.Quat(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)));
        RoundTrip(FieldKind.Entity, Value.Entity(new Entity(1UL << 63 | 7)), Value.Entity(new Entity(1UL << 63 | 7)));
    }

    [Fact]
    public void AnEntityFieldTakesAPlainNumberToo()
    {
        byte* data = stackalloc byte[8];
        var field = new FieldDef("target", FieldKind.Entity, 0);
        FieldAccess.Write(data, field, Value.Int(99));
        Assert.Equal(new Entity(99), FieldAccess.Read(data, field).ToEntity());
    }

    [Fact]
    public void AWrongKindOrARangeErrorIsAValueExceptionAndWritesNothing()
    {
        byte* data = stackalloc byte[8];
        new Span<byte>(data, 8).Clear();
        var field = new FieldDef("level", FieldKind.UInt8, 0);

        Assert.Contains("out of range", Assert.Throws<ValueException>(() => FieldAccess.Write(data, field, Value.Int(300))).Message);
        Assert.Contains("expected number, got string", Assert.Throws<ValueException>(() => FieldAccess.Write(data, field, Value.String("x"))).Message);
        Assert.Equal(0, FieldAccess.Read(data, field).ToInt32());
    }

    [Fact]
    public void FieldKindsKnowTheirSizeAndAlignment()
    {
        Assert.Equal(12, FieldKind.Vector3.SizeOf());
        Assert.Equal(4, FieldKind.Vector3.AlignmentOf());
        Assert.Equal(24, FieldKind.Double3.SizeOf());
        Assert.Equal(8, FieldKind.Double3.AlignmentOf());
        Assert.Equal(16, FieldKind.Quaternion.SizeOf());
        Assert.Equal(4, FieldKind.Quaternion.AlignmentOf());
        Assert.Equal(8, FieldKind.Entity.AlignmentOf());
        Assert.Equal(1, FieldKind.Bool.SizeOf());
    }
}
