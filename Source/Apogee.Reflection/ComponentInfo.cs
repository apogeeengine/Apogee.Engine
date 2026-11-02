using System;
using System.Collections.Generic;

namespace Apogee.Reflection;

/// <summary>How a component field is stored.</summary>
public enum FieldKind : byte
{
    Bool,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,

    /// <summary>Three <c>float</c>s: a <c>vector</c> in scripts.</summary>
    Vector3,

    /// <summary>Three <c>double</c>s: a world position. Scripts see it as a <c>vector</c> (narrowed to float; see the math chapter of the manual).</summary>
    Double3,

    /// <summary>Four <c>float</c>s: a rotation, the <c>Quat</c> of scripts.</summary>
    Quaternion,
    Entity,
}

public static class FieldKinds
{
    public static int SizeOf(this FieldKind kind) => kind switch
    {
        FieldKind.Bool or FieldKind.Int8 or FieldKind.UInt8 => 1,
        FieldKind.Int16 or FieldKind.UInt16 => 2,
        FieldKind.Int32 or FieldKind.UInt32 or FieldKind.Float32 => 4,
        FieldKind.Vector3 => 12,
        FieldKind.Double3 => 24,
        FieldKind.Quaternion => 16,
        _ => 8,
    };

    /// <summary>The alignment: a vector or a quaternion is made of floats, a double position of doubles.</summary>
    public static int AlignmentOf(this FieldKind kind) => kind switch
    {
        FieldKind.Vector3 or FieldKind.Quaternion => 4,
        FieldKind.Double3 => 8,
        _ => kind.SizeOf(),
    };
}

/// <summary>What an inspector needs to know about a property or a component field besides its type: a range for a slider, a step for a drag field, a heading, hidden, read-only. All optional.</summary>
public sealed record InspectorMeta(double Min = double.NaN, double Max = double.NaN, double Step = double.NaN, string? Category = null, bool Hidden = false, bool ReadOnly = false)
{
    /// <summary>Whether both ends of the range are given (a slider can be drawn).</summary>
    public bool HasRange => !double.IsNaN(Min) && !double.IsNaN(Max);

    /// <summary>The range as text for documentation: <c>0 to 10</c>, <c>from 0</c>, <c>up to 10</c>, or empty.</summary>
    public string RangeText => HasRange ? $"{Format(Min)} to {Format(Max)}" : !double.IsNaN(Min) ? $"from {Format(Min)}" : !double.IsNaN(Max) ? $"up to {Format(Max)}" : "";

    private static string Format(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record FieldDef(string Name, FieldKind Kind, int Offset, string? Doc = null, InspectorMeta? Meta = null);

/// <summary>An ECS component: its memory layout, so that the ECS can register it and scripts can read and write it.</summary>
public sealed class ComponentInfo
{
    public ComponentInfo(string name, Type? clrType, int size, int alignment, IReadOnlyList<FieldDef> fields, string? doc = null)
    {
        Name = name;
        ClrType = clrType;
        Size = size;
        Alignment = alignment;
        Fields = fields;
        Doc = doc;
    }

    /// <summary>The script name: the C# name, in PascalCase as ECS component names are.</summary>
    public string Name { get; }

    /// <summary>The struct, or null for a component a script defined.</summary>
    public Type? ClrType { get; }

    /// <summary>Zero for a tag.</summary>
    public int Size { get; }

    public int Alignment { get; }

    public IReadOnlyList<FieldDef> Fields { get; }

    public string? Doc { get; }

    public bool IsTag => Size == 0;

    public FieldDef? FindField(string name)
    {
        foreach (FieldDef field in Fields)
        {
            if (field.Name == name)
            {
                return field;
            }
        }

        return null;
    }
}
