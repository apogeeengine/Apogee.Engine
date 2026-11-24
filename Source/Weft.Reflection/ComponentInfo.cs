using System;
using System.Collections.Generic;

namespace Weft.Reflection;

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

    /// <summary>
    /// A <c>uint</c> handle that names an asset (a model, a texture…): four bytes in memory, like <see cref="UInt32"/>, which is also what scripts see.
    /// <see cref="FieldDef.AssetType"/> says what kind of asset. Scene files and the editor turn the handle into an <c>AssetRef</c> (path and UID).
    /// Appended after <see cref="Entity"/> so that the byte values of the kinds above stay what they were.
    /// </summary>
    AssetRef,
}

public static class FieldKinds
{
    public static int SizeOf(this FieldKind kind) => kind switch
    {
        FieldKind.Bool or FieldKind.Int8 or FieldKind.UInt8 => 1,
        FieldKind.Int16 or FieldKind.UInt16 => 2,
        FieldKind.Int32 or FieldKind.UInt32 or FieldKind.Float32 or FieldKind.AssetRef => 4,
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
/// <param name="Min">The smallest value of a slider or drag field (NaN: no limit).</param>
/// <param name="Max">The largest value of a slider (NaN: no limit).</param>
/// <param name="Step">How much one step of a drag field changes the value (NaN: the inspector's choice).</param>
/// <param name="Category">The heading the field is grouped under, or null.</param>
/// <param name="Hidden">An inspector does not show it.</param>
/// <param name="ReadOnly">An inspector shows it but does not let it be edited.</param>
/// <param name="Color">The <c>Vector3</c> is a linear RGB colour: an inspector draws a colour picker (above 1 is allowed: a light can be brighter than white). Added last so that existing positional calls keep their meaning.</param>
public sealed record InspectorMeta(double Min = double.NaN, double Max = double.NaN, double Step = double.NaN, string? Category = null, bool Hidden = false, bool ReadOnly = false, bool Color = false)
{
    /// <summary>Whether both ends of the range are given (a slider can be drawn).</summary>
    public bool HasRange => !double.IsNaN(Min) && !double.IsNaN(Max);

    /// <summary>The range as text for documentation: <c>0 to 10</c>, <c>from 0</c>, <c>up to 10</c>, or empty.</summary>
    public string RangeText => HasRange ? $"{Format(Min)} to {Format(Max)}" : !double.IsNaN(Min) ? $"from {Format(Min)}" : !double.IsNaN(Max) ? $"up to {Format(Max)}" : "";

    private static string Format(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One field of a component: where it is, how it is stored, and what an inspector needs to show it.</summary>
/// <param name="Name">The field name.</param>
/// <param name="Kind">How the field is stored.</param>
/// <param name="Offset">The byte offset from the start of the component.</param>
/// <param name="Doc">The <c>///</c> summary of the field, or null.</param>
/// <param name="Meta">Hints for an inspector, or null.</param>
/// <param name="AssetType">For <see cref="FieldKind.AssetRef"/>: the kind of asset the handle names (<c>model</c>), as the host registered it; null for every other kind.</param>
/// <param name="Enum">For an integer field that holds an enum's values: the name of its <see cref="EnumInfo"/> (<see cref="TypeRegistry.FindEnum"/>), so that an inspector can list the members. The field is
/// still an integer of its <see cref="Kind"/> in memory, in scripts and in scenes; null for every other field. Added last so that existing positional calls keep their meaning.</param>
public sealed record FieldDef(string Name, FieldKind Kind, int Offset, string? Doc = null, InspectorMeta? Meta = null, string? AssetType = null, string? Enum = null);

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
