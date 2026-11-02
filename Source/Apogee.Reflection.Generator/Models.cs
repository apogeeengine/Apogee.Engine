using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Apogee.Reflection.Generator;

/// <summary>What a member's type is to the registry.</summary>
internal enum Shape
{
    Bool,
    Int,
    Float,
    String,
    Vector3,
    Quaternion,
    Enum,
    Object,
    Entity,
    Function,
    Table,
    List,
    Any,
}

internal enum ListKind
{
    Array,
    List,
    ReadOnlyList,
}

/// <summary>A C# type that the registry supports, with what the generated code needs to convert it to and from a <c>Value</c>.</summary>
internal sealed class TypeModel
{
    public Shape Shape { get; set; }

    /// <summary>The fully qualified C# name, without a nullable annotation.</summary>
    public string CsType { get; set; } = "";

    public bool Nullable { get; set; }

    public bool IsValueType { get; set; }

    /// <summary>For <see cref="Shape.Int"/>: the suffix of the <c>Value.ToXxx</c> method (Int32, UInt64…).</summary>
    public string IntegerName { get; set; } = "";

    public bool IsUnsigned64 { get; set; }

    /// <summary>For <see cref="Shape.Float"/>: true for <c>double</c>.</summary>
    public bool IsDouble { get; set; }

    /// <summary>For <see cref="Shape.Vector3"/>: true for <c>Double3</c>, a world position that scripts see as a float vector.</summary>
    public bool IsDouble3 { get; set; }

    /// <summary>For <see cref="Shape.Enum"/> and <see cref="Shape.Object"/>: the registry name.</summary>
    public string RegistryName { get; set; } = "";

    /// <summary>For <see cref="Shape.Enum"/>: the key into the generated enum cache.</summary>
    public string EnumKey { get; set; } = "";

    public TypeModel? Element { get; set; }

    public ListKind ListKind { get; set; }
}

internal sealed class EnumModel
{
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public bool IsFlags { get; set; }

    public string? Doc { get; set; }

    public List<KeyValuePair<string, long>> Members { get; } = [];
}

internal sealed class ParameterModel
{
    public string Name { get; set; } = "";

    public TypeModel Type { get; set; } = null!;

    /// <summary>The source text of the default value, or null when the parameter is required.</summary>
    public string? Default { get; set; }
}

internal sealed class PropertyModel
{
    public string Name { get; set; } = "";

    public string ScriptName { get; set; } = "";

    public TypeModel Type { get; set; } = null!;

    public bool CanWrite { get; set; }

    public string? Doc { get; set; }

    /// <summary>The C# expression of its <c>InspectorMeta</c>, or null when the property carries no hints.</summary>
    public string? Meta { get; set; }
}

internal sealed class MethodModel
{
    public string Name { get; set; } = "";

    public string ScriptName { get; set; } = "";

    public bool IsStatic { get; set; }

    public List<ParameterModel> Parameters { get; } = [];

    /// <summary>Empty for <c>void</c>; one per item for a tuple.</summary>
    public List<TypeModel> Results { get; } = [];

    public bool ReturnsTuple { get; set; }

    public string? Doc { get; set; }
}

internal sealed class SignalModel
{
    public string Name { get; set; } = "";

    public string ScriptName { get; set; } = "";

    public List<TypeModel> Parameters { get; } = [];

    public string? Doc { get; set; }
}

internal sealed class FieldModel
{
    public string Name { get; set; } = "";

    /// <summary>The <c>FieldKind</c> member.</summary>
    public string Kind { get; set; } = "";

    public string CsType { get; set; } = "";

    public int Alignment { get; set; }

    public string? Doc { get; set; }

    /// <summary>The C# expression of its <c>InspectorMeta</c>, or null when the field carries no hints.</summary>
    public string? Meta { get; set; }
}

internal enum DescribedKind
{
    Class,
    Component,
    Library,
}

/// <summary>A type with one of our attributes, as plain data.</summary>
internal sealed class Described
{
    public DescribedKind Kind { get; set; }

    public string Namespace { get; set; } = "";

    /// <summary>The containing types, outermost first, as declaration keywords: <c>class Outer</c>.</summary>
    public List<string> Containers { get; } = [];

    public string TypeName { get; set; } = "";

    /// <summary>The declaration keyword and name: <c>class Foo</c>, <c>struct Bar</c>.</summary>
    public string Declaration { get; set; } = "";

    public string FullName { get; set; } = "";

    public string RegistryName { get; set; } = "";

    public string BaseName { get; set; } = "EngineObject";

    public bool Scriptable { get; set; } = true;

    public bool ModsBlocked { get; set; }

    public bool Creatable { get; set; }

    public string? Doc { get; set; }

    public List<PropertyModel> Properties { get; } = [];

    public List<MethodModel> Methods { get; } = [];

    public List<SignalModel> Signals { get; } = [];

    public List<FieldModel> Fields { get; } = [];

    public Dictionary<string, EnumModel> Enums { get; } = [];

    public List<Diagnostic> Diagnostics { get; } = [];

    public bool HasErrors { get; set; }
}
