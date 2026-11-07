using System;
using System.Numerics;
using Apogee.Reflection;

namespace Apogee.Editor.Editing;

/// <summary>What kind of widget edits a field.</summary>
public enum FieldWidget
{
    /// <summary>A checkbox.</summary>
    Toggle,

    /// <summary>A whole number (an integer field).</summary>
    WholeNumber,

    /// <summary>One number (a <c>float</c> or a <c>double</c>).</summary>
    Number,

    /// <summary>Three numbers (a vector or a position).</summary>
    Vector,

    /// <summary>Three angles in degrees (a rotation is edited as pitch, yaw and roll).</summary>
    Rotation,

    /// <summary>Another entity: picked by name or dropped on.</summary>
    EntityReference,
}

/// <summary>
/// Reads the memory of a component field as the numbers a widget edits and writes them back, for every <see cref="FieldKind"/>: a rotation is shown as angles, a position keeps its double
/// precision, an integer is clamped to its type. No ImGui in it, so the round trip of each kind is tested on its own.
/// </summary>
public static unsafe class FieldCodec
{
    public static FieldWidget WidgetOf(FieldKind kind) => kind switch
    {
        FieldKind.Bool => FieldWidget.Toggle,
        FieldKind.Float32 or FieldKind.Float64 => FieldWidget.Number,
        FieldKind.Vector3 or FieldKind.Double3 => FieldWidget.Vector,
        FieldKind.Quaternion => FieldWidget.Rotation,
        FieldKind.Entity => FieldWidget.EntityReference,
        _ => FieldWidget.WholeNumber,
    };

    /// <summary>How many numbers the widget edits.</summary>
    public static int CountOf(FieldKind kind) => WidgetOf(kind) is FieldWidget.Vector or FieldWidget.Rotation ? 3 : 1;

    /// <summary>The numbers of the field at <paramref name="at"/>: a bool is 0 or 1, an entity its id, a rotation pitch, yaw and roll in degrees.</summary>
    public static double[] Read(byte* at, FieldKind kind)
    {
        switch (kind)
        {
            case FieldKind.Bool: return [*at != 0 ? 1 : 0];
            case FieldKind.Int8: return [*(sbyte*)at];
            case FieldKind.UInt8: return [*at];
            case FieldKind.Int16: return [*(short*)at];
            case FieldKind.UInt16: return [*(ushort*)at];
            case FieldKind.Int32: return [*(int*)at];
            case FieldKind.UInt32: return [*(uint*)at];
            case FieldKind.Int64: return [*(long*)at];
            case FieldKind.UInt64: return [*(ulong*)at];
            case FieldKind.Float32: return [*(float*)at];
            case FieldKind.Float64: return [*(double*)at];
            case FieldKind.Vector3: return [((float*)at)[0], ((float*)at)[1], ((float*)at)[2]];
            case FieldKind.Double3: return [((double*)at)[0], ((double*)at)[1], ((double*)at)[2]];
            case FieldKind.Entity: return [*(ulong*)at];
            case FieldKind.Quaternion:
                var q = new Quaternion(((float*)at)[0], ((float*)at)[1], ((float*)at)[2], ((float*)at)[3]);
                (float pitch, float yaw, float roll) = ToEuler(q);
                return [pitch * Degrees, yaw * Degrees, roll * Degrees];
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>Writes the numbers into the field at <paramref name="at"/>; an integer is rounded and clamped to what its type holds.</summary>
    public static void Write(byte* at, FieldKind kind, ReadOnlySpan<double> values)
    {
        if (values.Length < CountOf(kind))
        {
            throw new ArgumentException($"a {kind} takes {CountOf(kind)} number(s)", nameof(values));
        }

        double v = values[0];
        switch (kind)
        {
            case FieldKind.Bool: *at = (byte)(v != 0 ? 1 : 0); break;
            case FieldKind.Int8: *(sbyte*)at = (sbyte)Clamp(v, sbyte.MinValue, sbyte.MaxValue); break;
            case FieldKind.UInt8: *at = (byte)Clamp(v, byte.MinValue, byte.MaxValue); break;
            case FieldKind.Int16: *(short*)at = (short)Clamp(v, short.MinValue, short.MaxValue); break;
            case FieldKind.UInt16: *(ushort*)at = (ushort)Clamp(v, ushort.MinValue, ushort.MaxValue); break;
            case FieldKind.Int32: *(int*)at = (int)Clamp(v, int.MinValue, int.MaxValue); break;
            case FieldKind.UInt32: *(uint*)at = (uint)Clamp(v, uint.MinValue, uint.MaxValue); break;
            case FieldKind.Int64: *(long*)at = (long)Clamp(v, long.MinValue, long.MaxValue); break;
            case FieldKind.UInt64: *(ulong*)at = (ulong)Clamp(v, 0, ulong.MaxValue); break;
            case FieldKind.Float32: *(float*)at = (float)v; break;
            case FieldKind.Float64: *(double*)at = v; break;
            case FieldKind.Vector3:
                ((float*)at)[0] = (float)values[0];
                ((float*)at)[1] = (float)values[1];
                ((float*)at)[2] = (float)values[2];
                break;
            case FieldKind.Double3:
                ((double*)at)[0] = values[0];
                ((double*)at)[1] = values[1];
                ((double*)at)[2] = values[2];
                break;
            case FieldKind.Entity: *(ulong*)at = (ulong)Clamp(v, 0, ulong.MaxValue); break;
            case FieldKind.Quaternion:
                Quaternion q = FromEuler((float)(values[0] / Degrees), (float)(values[1] / Degrees), (float)(values[2] / Degrees));
                ((float*)at)[0] = q.X;
                ((float*)at)[1] = q.Y;
                ((float*)at)[2] = q.Z;
                ((float*)at)[3] = q.W;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private const double Degrees = 180.0 / Math.PI;

    private static double Clamp(double value, double min, double max) => double.IsNaN(value) ? 0 : Math.Round(Math.Clamp(value, min, max));

    /// <summary>A rotation from pitch (about X), yaw (about Y) and roll (about Z) in radians: yaw first, then pitch, then roll, as <see cref="Quaternion.CreateFromYawPitchRoll"/> does.</summary>
    public static Quaternion FromEuler(float pitch, float yaw, float roll) => Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);

    /// <summary>The pitch, yaw and roll (radians) that <see cref="FromEuler"/> turns into <paramref name="q"/>.</summary>
    public static (float Pitch, float Yaw, float Roll) ToEuler(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        // The rotation matrix of Y (yaw), X (pitch), Z (roll) composition, read back.
        float sinPitch = 2f * ((q.W * q.X) - (q.Y * q.Z));
        float pitch;
        float yaw;
        float roll;
        if (MathF.Abs(sinPitch) >= 0.99999f)
        {
            // Looking straight up or down: yaw and roll turn about the same axis; give it all to yaw.
            pitch = MathF.CopySign(MathF.PI / 2f, sinPitch);
            yaw = 2f * MathF.Atan2(q.Y, q.W);
            roll = 0f;
        }
        else
        {
            pitch = MathF.Asin(sinPitch);
            yaw = MathF.Atan2(2f * ((q.X * q.Z) + (q.W * q.Y)), 1f - (2f * ((q.X * q.X) + (q.Y * q.Y))));
            roll = MathF.Atan2(2f * ((q.X * q.Y) + (q.W * q.Z)), 1f - (2f * ((q.X * q.X) + (q.Z * q.Z))));
        }

        return (pitch, yaw, roll);
    }
}
