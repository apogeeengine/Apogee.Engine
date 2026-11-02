using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Apogee.Reflection;

/// <summary>
/// A value that crosses between the engine and scripts: no boxing for numbers, vectors, entities and enums. The <c>To…</c>
/// methods check the kind and throw a <see cref="ValueException"/> whose message a script can show.
/// </summary>
public readonly struct Value : IEquatable<Value>
{
    private readonly object? _reference; // string, EnumInfo, EngineObject, ScriptFunction, ScriptTable, IReadOnlyList<Value>
    private readonly long _bits;
    private readonly float _extra; // z of a vector or a quaternion
    private readonly float _extra2; // w of a quaternion
    private readonly ValueKind _kind;

    private Value(ValueKind kind, long bits = 0, object? reference = null, float extra = 0, float extra2 = 0)
    {
        _kind = kind;
        _bits = bits;
        _reference = reference;
        _extra = extra;
        _extra2 = extra2;
    }

    public ValueKind Kind => _kind;

    public bool IsNil => _kind == ValueKind.Nil;

    // ---- Creating ----------------------------------------------------------------------------------------------

    public static Value Bool(bool value) => new(ValueKind.Bool, value ? 1 : 0);

    public static Value Int(long value) => new(ValueKind.Int, value);

    public static Value Float(double value) => new(ValueKind.Float, BitConverter.DoubleToInt64Bits(value));

    /// <summary>A string; null is nil.</summary>
    public static Value String(string? value) => value is null ? default : new(ValueKind.String, 0, value);

    public static Value Vector(Vector3 value) =>
        new(ValueKind.Vector3, (uint)BitConverter.SingleToInt32Bits(value.X) | ((long)BitConverter.SingleToInt32Bits(value.Y) << 32), null, value.Z);

    public static Value Quat(Quaternion value) =>
        new(ValueKind.Quaternion, (uint)BitConverter.SingleToInt32Bits(value.X) | ((long)BitConverter.SingleToInt32Bits(value.Y) << 32), null, value.Z, value.W);

    public static Value Enum(EnumInfo type, long value) => new(ValueKind.Enum, value, type);

    /// <summary>An object; null is nil.</summary>
    public static Value Object(EngineObject? value) => value is null ? default : new(ValueKind.Object, 0, value);

    public static Value Entity(Entity value) => new(ValueKind.Entity, unchecked((long)value.Id));

    public static Value Function(ScriptFunction? value) => value is null ? default : new(ValueKind.Function, 0, value);

    /// <summary>A table kept by reference; null is nil.</summary>
    public static Value Table(ScriptTable? value) => value is null ? default : new(ValueKind.Table, 0, value);

    public static Value List(IReadOnlyList<Value>? items) => items is null ? default : new(ValueKind.List, 0, items);

    // ---- Reading -----------------------------------------------------------------------------------------------

    public bool ToBool() => _kind == ValueKind.Bool ? _bits != 0 : throw Mismatch("boolean");

    public double ToDouble() => _kind switch
    {
        ValueKind.Float => BitConverter.Int64BitsToDouble(_bits),
        ValueKind.Int => _bits,
        _ => throw Mismatch("number"),
    };

    public float ToSingle() => (float)ToDouble();

    /// <summary>An integer; a float with no fraction is accepted (scripts only have doubles).</summary>
    public long ToInt64()
    {
        switch (_kind)
        {
            case ValueKind.Int:
                return _bits;
            case ValueKind.Float:
                double value = BitConverter.Int64BitsToDouble(_bits);
                return value == Math.Floor(value) && Math.Abs(value) < 9.2e18 ? (long)value : throw new ValueException($"expected an integer, got {value.ToString(CultureInfo.InvariantCulture)}");
            default:
                throw Mismatch("number");
        }
    }

    public int ToInt32() => (int)Narrow(ToInt64(), int.MinValue, int.MaxValue, "int");

    public uint ToUInt32() => (uint)Narrow(ToInt64(), uint.MinValue, uint.MaxValue, "uint");

    public short ToInt16() => (short)Narrow(ToInt64(), short.MinValue, short.MaxValue, "short");

    public ushort ToUInt16() => (ushort)Narrow(ToInt64(), ushort.MinValue, ushort.MaxValue, "ushort");

    public sbyte ToSByte() => (sbyte)Narrow(ToInt64(), sbyte.MinValue, sbyte.MaxValue, "sbyte");

    public byte ToByte() => (byte)Narrow(ToInt64(), byte.MinValue, byte.MaxValue, "byte");

    public ulong ToUInt64() => _kind == ValueKind.Entity ? unchecked((ulong)_bits) : (ulong)Narrow(ToInt64(), 0, long.MaxValue, "ulong");

    public string ToStringValue() => _kind == ValueKind.String ? (string)_reference! : throw Mismatch("string");

    public Vector3 ToVector3() => _kind == ValueKind.Vector3
        ? new Vector3(BitConverter.Int32BitsToSingle((int)_bits), BitConverter.Int32BitsToSingle((int)(_bits >> 32)), _extra)
        : throw Mismatch("vector");

    public Quaternion ToQuaternion() => _kind == ValueKind.Quaternion
        ? new Quaternion(BitConverter.Int32BitsToSingle((int)_bits), BitConverter.Int32BitsToSingle((int)(_bits >> 32)), _extra, _extra2)
        : throw Mismatch("Quat");

    public Entity ToEntity() => _kind == ValueKind.Entity ? new Entity(unchecked((ulong)_bits)) : throw Mismatch("entity");

    /// <summary>The numeric value of an enum member.</summary>
    public long ToEnum() => _kind == ValueKind.Enum ? _bits : throw Mismatch("enum");

    /// <summary>The enum a <see cref="ValueKind.Enum"/> value is a member of.</summary>
    public EnumInfo EnumType => _kind == ValueKind.Enum ? (EnumInfo)_reference! : throw Mismatch("enum");

    /// <summary>The object, which must be a <typeparamref name="T"/> and alive.</summary>
    public T ToObject<T>()
        where T : EngineObject
    {
        if (_kind != ValueKind.Object)
        {
            throw Mismatch(typeof(T).Name);
        }

        var value = (EngineObject)_reference!;
        if (value.IsDestroyed)
        {
            throw new ValueException("attempt to use a destroyed object");
        }

        return value as T ?? throw new ValueException($"expected {typeof(T).Name}, got {value.Class.Name}");
    }

    public ScriptFunction ToFunction() => _kind == ValueKind.Function ? (ScriptFunction)_reference! : throw Mismatch("function");

    public ScriptTable ToTable() => _kind == ValueKind.Table ? (ScriptTable)_reference! : throw Mismatch("table");

    public IReadOnlyList<Value> ToList() => _kind == ValueKind.List ? (IReadOnlyList<Value>)_reference! : throw Mismatch("table");

    // ---- Lists of typed values (used by generated code) -----------------------------------------------------------------

    public static Value ListOf<T>(IEnumerable<T>? items, Func<T, Value> convert)
    {
        if (items is null)
        {
            return default;
        }

        var values = new List<Value>();
        foreach (T item in items)
        {
            values.Add(convert(item));
        }

        return List(values);
    }

    public List<T> ToListOf<T>(Func<Value, T> convert)
    {
        IReadOnlyList<Value> items = ToList();
        var result = new List<T>(items.Count);
        foreach (Value item in items)
        {
            result.Add(convert(item));
        }

        return result;
    }

    // ---- Everything else -----------------------------------------------------------------------------------------

    public bool Equals(Value other) =>
        _kind == other._kind && _bits == other._bits && _extra.Equals(other._extra) && _extra2.Equals(other._extra2) && Equals(_reference, other._reference);

    public override bool Equals(object? obj) => obj is Value other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_kind, _bits, _extra, _extra2, _reference);

    public static bool operator ==(Value left, Value right) => left.Equals(right);

    public static bool operator !=(Value left, Value right) => !left.Equals(right);

    public override string ToString() => _kind switch
    {
        ValueKind.Nil => "nil",
        ValueKind.Bool => ToBool() ? "true" : "false",
        ValueKind.Int => _bits.ToString(CultureInfo.InvariantCulture),
        ValueKind.Float => ToDouble().ToString("R", CultureInfo.InvariantCulture),
        ValueKind.String => (string)_reference!,
        ValueKind.Vector3 => ToVector3().ToString(),
        ValueKind.Quaternion => ToQuaternion().ToString(),
        ValueKind.Enum => ((EnumInfo)_reference!).GetName(_bits),
        ValueKind.Entity => ToEntity().ToString(),
        ValueKind.Object => ((EngineObject)_reference!).Class.Name,
        ValueKind.Function => "function",
        ValueKind.Table => "table",
        _ => $"list({((IReadOnlyList<Value>)_reference!).Count})",
    };

    private static long Narrow(long value, long min, long max, string type) =>
        value >= min && value <= max ? value : throw new ValueException($"{value.ToString(CultureInfo.InvariantCulture)} is out of range for {type}");

    private ValueException Mismatch(string expected) => new($"expected {expected}, got {KindName(_kind)}");

    internal static string KindName(ValueKind kind) => kind switch
    {
        ValueKind.Nil => "nil",
        ValueKind.Bool => "boolean",
        ValueKind.Int or ValueKind.Float => "number",
        ValueKind.String => "string",
        ValueKind.Vector3 => "vector",
        ValueKind.Quaternion => "Quat",
        ValueKind.Enum => "enum",
        ValueKind.Object => "object",
        ValueKind.Entity => "entity",
        ValueKind.Function => "function",
        _ => "table",
    };
}
