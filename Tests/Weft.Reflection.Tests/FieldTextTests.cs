using System;
using System.Globalization;
using System.Numerics;
using Weft.Core.Numerics;
using Weft.Reflection;
using Xunit;

namespace Weft.Reflection.Tests;

public unsafe class FieldTextTests
{
    // Writes the text into a field at an odd offset (script-defined components are packed by alignment), formats it back and returns that text.
    private static string Through(FieldKind kind, string text, int offset = 1)
    {
        byte* data = stackalloc byte[64];
        new Span<byte>(data, 64).Clear();
        var field = new FieldDef("f", kind, offset);
        FieldText.Parse(field, text, data + offset);
        return FieldText.Format(field, data + offset);
    }

    [Theory]
    [InlineData(FieldKind.Bool, "true")]
    [InlineData(FieldKind.Bool, "false")]
    [InlineData(FieldKind.Int8, "-128")]
    [InlineData(FieldKind.UInt8, "255")]
    [InlineData(FieldKind.Int16, "-32768")]
    [InlineData(FieldKind.UInt16, "65535")]
    [InlineData(FieldKind.Int32, "-2147483648")]
    [InlineData(FieldKind.UInt32, "4294967295")]
    [InlineData(FieldKind.Int64, "-9223372036854775808")]
    [InlineData(FieldKind.UInt64, "18446744073709551615")]
    [InlineData(FieldKind.Float32, "0.1")]
    [InlineData(FieldKind.Float32, "1E-45")]
    [InlineData(FieldKind.Float32, "-Infinity")]
    [InlineData(FieldKind.Float32, "NaN")]
    [InlineData(FieldKind.Float64, "3.141592653589793")]
    [InlineData(FieldKind.Float64, "1.7976931348623157E+308")]
    [InlineData(FieldKind.Float64, "Infinity")]
    [InlineData(FieldKind.Vector3, "1 -2.5 3E-10")]
    [InlineData(FieldKind.Double3, "1000000000000.5 -2 3")]
    [InlineData(FieldKind.Quaternion, "0.1 0.2 0.3 0.9")]
    public void EveryKindRoundTripsAsTheSameText(FieldKind kind, string text) => Assert.Equal(text, Through(kind, text));

    [Fact]
    public void ValuesRoundTripExactly()
    {
        byte* data = stackalloc byte[64];
        var f32 = new FieldDef("f", FieldKind.Float32, 0);
        var f64 = new FieldDef("d", FieldKind.Float64, 8);
        foreach (float value in new[] { 0.1f, 1f / 3f, float.Epsilon, float.MaxValue, -0f })
        {
            *(float*)data = value;
            string text = FieldText.Format(f32, data);
            *(float*)data = 123f;
            FieldText.Parse(f32, text, data);
            Assert.Equal(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits(*(float*)data));
        }

        foreach (double value in new[] { 0.1, 1.0 / 3.0, double.Epsilon, double.MaxValue, Math.PI * 1e300 })
        {
            *(double*)(data + 8) = value;
            string text = FieldText.Format(f64, data + 8);
            *(double*)(data + 8) = 123;
            FieldText.Parse(f64, text, data + 8);
            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(*(double*)(data + 8)));
        }
    }

    [Fact]
    public void TextIsTheSameInEveryCulture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // a decimal comma, and a different minus on some systems
            Assert.Equal("-1234.5", Through(FieldKind.Float64, "-1234.5"));
            Assert.Equal("1.5 -2.25 3", Through(FieldKind.Vector3, "1.5 -2.25 3"));
            Assert.Equal("1234567", Through(FieldKind.Int32, "1234567"));
            Assert.Throws<FormatException>(() => Through(FieldKind.Float64, "1,5"));   // a comma is not a decimal point in the file, whatever the machine's language
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void ABadTextIsAFormatExceptionAndLeavesTheFieldAlone()
    {
        byte* data = stackalloc byte[16];
        new Span<byte>(data, 16).Clear();
        *(int*)data = 77;
        var field = new FieldDef("f", FieldKind.Int32, 0);

        Assert.Throws<FormatException>(() => FieldText.Parse(field, "seven", data));
        Assert.Throws<FormatException>(() => FieldText.Parse(field, "99999999999", data));   // too big: still a FormatException, not an OverflowException
        Assert.Throws<FormatException>(() => FieldText.Parse(field, "", data));
        Assert.Equal(77, *(int*)data);

        Assert.Throws<FormatException>(() => Through(FieldKind.UInt8, "256"));
        Assert.Throws<FormatException>(() => Through(FieldKind.UInt32, "-1"));
        Assert.Throws<FormatException>(() => Through(FieldKind.Bool, "yes"));
        Assert.Throws<FormatException>(() => Through(FieldKind.Vector3, "1 2"));
        Assert.Throws<FormatException>(() => Through(FieldKind.Vector3, "1 2 3 4"));
        Assert.Throws<FormatException>(() => Through(FieldKind.Quaternion, "1 2 3"));
        Assert.Throws<FormatException>(() => Through(FieldKind.Double3, "1 two 3"));
    }

    [Fact]
    public void ExtraSpacesAndOneAndZeroAreAccepted()
    {
        Assert.Equal("1 2 3", Through(FieldKind.Vector3, "  1   2\t 3 ".Replace('\t', ' ')));
        Assert.Equal("true", Through(FieldKind.Bool, "1"));
        Assert.Equal("false", Through(FieldKind.Bool, "0"));
    }

    [Fact]
    public void EntityAndAssetRefNeedAWorldAndAreRefused()
    {
        byte* data = stackalloc byte[16];
        foreach (FieldKind kind in new[] { FieldKind.Entity, FieldKind.AssetRef })
        {
            var field = new FieldDef("f", kind, 0);
            Assert.False(FieldText.Handles(kind));
            Assert.Throws<ArgumentException>(() => FieldText.Format(field, data));
            Assert.Throws<ArgumentException>(() => FieldText.Parse(field, "1", data));
        }

        Assert.True(FieldText.Handles(FieldKind.Quaternion));
    }
}
