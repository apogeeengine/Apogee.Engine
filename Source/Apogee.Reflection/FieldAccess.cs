using System.Numerics;
using Apogee.Core.Numerics;
using System.Runtime.CompilerServices;

namespace Apogee.Reflection;

/// <summary>Reads and writes a component's fields in its memory as <see cref="Value"/>s. <c>data</c> is the start of the component.</summary>
public static unsafe class FieldAccess
{
    public static Value Read(byte* data, FieldDef field)
    {
        byte* p = data + field.Offset;
        return field.Kind switch
        {
            FieldKind.Bool => Value.Bool(*p != 0),
            FieldKind.Int8 => Value.Int(*(sbyte*)p),
            FieldKind.UInt8 => Value.Int(*p),
            FieldKind.Int16 => Value.Int(Unsafe.ReadUnaligned<short>(p)),
            FieldKind.UInt16 => Value.Int(Unsafe.ReadUnaligned<ushort>(p)),
            FieldKind.Int32 => Value.Int(Unsafe.ReadUnaligned<int>(p)),
            FieldKind.UInt32 => Value.Int(Unsafe.ReadUnaligned<uint>(p)),
            FieldKind.Int64 => Value.Int(Unsafe.ReadUnaligned<long>(p)),
            FieldKind.UInt64 => Value.Int(unchecked((long)Unsafe.ReadUnaligned<ulong>(p))),
            FieldKind.Float32 => Value.Float(Unsafe.ReadUnaligned<float>(p)),
            FieldKind.Float64 => Value.Float(Unsafe.ReadUnaligned<double>(p)),
            FieldKind.Vector3 => Value.Vector(Unsafe.ReadUnaligned<Vector3>(p)),
            FieldKind.Double3 => Value.Vector((Vector3)Unsafe.ReadUnaligned<Double3>(p)),
            FieldKind.Quaternion => Value.Quat(Unsafe.ReadUnaligned<Quaternion>(p)),
            _ => Value.Entity(new Entity(Unsafe.ReadUnaligned<ulong>(p))),
        };
    }

    /// <summary>Writes the value after checking its kind and range (a <see cref="ValueException"/> otherwise); an entity field also takes a plain number.</summary>
    public static void Write(byte* data, FieldDef field, in Value value)
    {
        byte* p = data + field.Offset;
        switch (field.Kind)
        {
            case FieldKind.Bool: *p = value.ToBool() ? (byte)1 : (byte)0; break;
            case FieldKind.Int8: *(sbyte*)p = value.ToSByte(); break;
            case FieldKind.UInt8: *p = value.ToByte(); break;
            case FieldKind.Int16: Unsafe.WriteUnaligned(p, value.ToInt16()); break;
            case FieldKind.UInt16: Unsafe.WriteUnaligned(p, value.ToUInt16()); break;
            case FieldKind.Int32: Unsafe.WriteUnaligned(p, value.ToInt32()); break;
            case FieldKind.UInt32: Unsafe.WriteUnaligned(p, value.ToUInt32()); break;
            case FieldKind.Int64: Unsafe.WriteUnaligned(p, value.ToInt64()); break;
            case FieldKind.UInt64: Unsafe.WriteUnaligned(p, value.ToUInt64()); break;
            case FieldKind.Float32: Unsafe.WriteUnaligned(p, value.ToSingle()); break;
            case FieldKind.Float64: Unsafe.WriteUnaligned(p, value.ToDouble()); break;
            case FieldKind.Vector3: Unsafe.WriteUnaligned(p, value.ToVector3()); break;
            case FieldKind.Double3: Unsafe.WriteUnaligned(p, (Double3)value.ToVector3()); break;
            case FieldKind.Quaternion: Unsafe.WriteUnaligned(p, value.ToQuaternion()); break;
            default: Unsafe.WriteUnaligned(p, value.Kind == ValueKind.Entity ? value.ToEntity().Id : value.ToUInt64()); break;
        }
    }
}
