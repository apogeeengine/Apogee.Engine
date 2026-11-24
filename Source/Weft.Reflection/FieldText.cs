using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Weft.Core.Numerics;

namespace Weft.Reflection;

/// <summary>
/// A component field as text and back: what a scene file holds in an attribute, and what prefab overrides and the clipboard will hold. The text is the same in every culture
/// (<see cref="CultureInfo.InvariantCulture"/>): booleans are <c>true</c> and <c>false</c>, integers decimal, floats the shortest text that reads back as the same number (with
/// <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c>), and a <see cref="FieldKind.Vector3"/>, <see cref="FieldKind.Double3"/> or <see cref="FieldKind.Quaternion"/> is its numbers
/// separated by single spaces (<c>1 2 3</c>, <c>0 0 0 1</c>).
/// <para>
/// <see cref="FieldKind.Entity"/> and <see cref="FieldKind.AssetRef"/> are not here: what they hold only means something in a world (an entity's id, an asset handle), so
/// whoever has that context writes and reads them.
/// </para>
/// </summary>
public static unsafe class FieldText
{
    /// <summary>Whether <see cref="Format"/> and <see cref="Parse"/> handle the kind (every kind except <see cref="FieldKind.Entity"/> and <see cref="FieldKind.AssetRef"/>).</summary>
    public static bool Handles(FieldKind kind) => kind is not (FieldKind.Entity or FieldKind.AssetRef);

    /// <summary>The text of the field whose value is at <paramref name="at"/> (the field's own address, not the component's start).</summary>
    /// <exception cref="ArgumentException">The kind is <see cref="FieldKind.Entity"/> or <see cref="FieldKind.AssetRef"/>.</exception>
    public static string Format(FieldDef field, byte* at)
    {
        // ReadUnaligned everywhere: the fields of a script-defined component are packed by alignment, and the caller may hand over any address.
        CultureInfo c = CultureInfo.InvariantCulture;
        switch (field.Kind)
        {
            case FieldKind.Bool: return *at != 0 ? "true" : "false";
            case FieldKind.Int8: return ((sbyte)*at).ToString(c);
            case FieldKind.UInt8: return (*at).ToString(c);
            case FieldKind.Int16: return Unsafe.ReadUnaligned<short>(at).ToString(c);
            case FieldKind.UInt16: return Unsafe.ReadUnaligned<ushort>(at).ToString(c);
            case FieldKind.Int32: return Unsafe.ReadUnaligned<int>(at).ToString(c);
            case FieldKind.UInt32: return Unsafe.ReadUnaligned<uint>(at).ToString(c);
            case FieldKind.Int64: return Unsafe.ReadUnaligned<long>(at).ToString(c);
            case FieldKind.UInt64: return Unsafe.ReadUnaligned<ulong>(at).ToString(c);

            // The default ToString of a float is the shortest text that parses back to the same bits; invariant culture spells the special values NaN, Infinity and -Infinity.
            case FieldKind.Float32: return Unsafe.ReadUnaligned<float>(at).ToString(c);
            case FieldKind.Float64: return Unsafe.ReadUnaligned<double>(at).ToString(c);
            case FieldKind.Vector3:
            {
                Vector3 v = Unsafe.ReadUnaligned<Vector3>(at);
                return $"{v.X.ToString(c)} {v.Y.ToString(c)} {v.Z.ToString(c)}";
            }

            case FieldKind.Quaternion:
            {
                Quaternion q = Unsafe.ReadUnaligned<Quaternion>(at);
                return $"{q.X.ToString(c)} {q.Y.ToString(c)} {q.Z.ToString(c)} {q.W.ToString(c)}";
            }

            case FieldKind.Double3:
            {
                Double3 d = Unsafe.ReadUnaligned<Double3>(at);
                return $"{d.X.ToString(c)} {d.Y.ToString(c)} {d.Z.ToString(c)}";
            }

            default:
                throw new ArgumentException($"A field of kind {field.Kind} has no text of its own: it needs a world to be written.", nameof(field));
        }
    }

    /// <summary>Reads <paramref name="text"/> into the field whose value is at <paramref name="at"/> (the field's own address).</summary>
    /// <exception cref="FormatException">The text is not a value of the kind, or is out of its range.</exception>
    /// <exception cref="ArgumentException">The kind is <see cref="FieldKind.Entity"/> or <see cref="FieldKind.AssetRef"/>.</exception>
    public static void Parse(FieldDef field, string text, byte* at)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            ParseCore(field, text, at);
        }
        catch (OverflowException e)
        {
            // Number parsing says "too big" with its own exception type; callers handle one kind of failure.
            throw new FormatException($"'{text}' does not fit in a {field.Kind}", e);
        }
    }

    private static void ParseCore(FieldDef field, string text, byte* at)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        switch (field.Kind)
        {
            case FieldKind.Bool:
                *at = text switch
                {
                    "true" or "1" => 1,
                    "false" or "0" => 0,
                    _ => throw new FormatException($"'{text}' is not true or false"),
                };
                break;
            case FieldKind.Int8: *(sbyte*)at = sbyte.Parse(text, NumberStyles.Integer, c); break;
            case FieldKind.UInt8: *at = byte.Parse(text, NumberStyles.Integer, c); break;
            case FieldKind.Int16: Unsafe.WriteUnaligned(at, short.Parse(text, NumberStyles.Integer, c)); break;
            case FieldKind.UInt16: Unsafe.WriteUnaligned(at, ushort.Parse(text, NumberStyles.Integer, c)); break;
            case FieldKind.Int32: Unsafe.WriteUnaligned(at, int.Parse(text, NumberStyles.Integer, c)); break;
            case FieldKind.UInt32: Unsafe.WriteUnaligned(at, uint.Parse(text, NumberStyles.Integer, c)); break;
            case FieldKind.Int64: Unsafe.WriteUnaligned(at, long.Parse(text, NumberStyles.Integer, c)); break;
            case FieldKind.UInt64: Unsafe.WriteUnaligned(at, ulong.Parse(text, NumberStyles.Integer, c)); break;
            case FieldKind.Float32: Unsafe.WriteUnaligned(at, float.Parse(text, NumberStyles.Float, c)); break;
            case FieldKind.Float64: Unsafe.WriteUnaligned(at, double.Parse(text, NumberStyles.Float, c)); break;
            case FieldKind.Vector3:
            {
                float* v = stackalloc float[3];
                ReadFloats(text, v, 3);
                Unsafe.WriteUnaligned(at, new Vector3(v[0], v[1], v[2]));
                break;
            }

            case FieldKind.Quaternion:
            {
                float* v = stackalloc float[4];
                ReadFloats(text, v, 4);
                Unsafe.WriteUnaligned(at, new Quaternion(v[0], v[1], v[2], v[3]));
                break;
            }

            case FieldKind.Double3:
            {
                string[] parts = Split(text, 3);
                Unsafe.WriteUnaligned(at, new Double3(double.Parse(parts[0], NumberStyles.Float, c), double.Parse(parts[1], NumberStyles.Float, c), double.Parse(parts[2], NumberStyles.Float, c)));
                break;
            }

            default:
                throw new ArgumentException($"A field of kind {field.Kind} has no text of its own: it needs a world to be read.", nameof(field));
        }
    }

    private static void ReadFloats(string text, float* into, int count)
    {
        string[] parts = Split(text, count);
        for (int i = 0; i < count; i++)
        {
            into[i] = float.Parse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }

    private static string[] Split(string text, int count)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != count)
        {
            throw new FormatException($"expected {count} numbers separated by spaces, got '{text}'");
        }

        return parts;
    }
}
