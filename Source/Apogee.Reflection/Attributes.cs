using System;

namespace Apogee.Reflection;

/// <summary>
/// Describes a <c>partial</c> class derived from <see cref="EngineObject"/> to the registry: a source generator writes its
/// <see cref="ClassInfo"/> from the members marked <see cref="PropertyAttribute"/>, <see cref="MethodAttribute"/> and <see cref="SignalAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ApogeeClassAttribute : Attribute
{
    /// <summary>The script name of the class; the C# name by default.</summary>
    public string? Name { get; set; }

    /// <summary>False keeps the class out of scripts (its objects are nil there) and out of the generated definitions.</summary>
    public bool Scriptable { get; set; } = true;

    /// <summary>True refuses objects of the class to mods: they only exist for the base game.</summary>
    public bool ModsBlocked { get; set; }

    /// <summary>True gives the class a <c>new()</c> in scripts; it needs a public parameterless constructor.</summary>
    public bool Creatable { get; set; }
}

/// <summary>A property or field that scripts, serialization and inspectors see. Read-only when it has no public setter.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class PropertyAttribute : Attribute
{
    /// <summary>The script name; the C# name in snake_case by default.</summary>
    public string? Name { get; set; }

    /// <summary>The smallest value an inspector's slider or drag field gives (NaN: no limit). It does not stop a script or a file from setting a smaller one.</summary>
    public double Min { get; set; } = double.NaN;

    /// <summary>The largest value an inspector's slider gives (NaN: no limit).</summary>
    public double Max { get; set; } = double.NaN;

    /// <summary>How much one step of an inspector's drag field changes the value (NaN: the inspector's choice).</summary>
    public double Step { get; set; } = double.NaN;

    /// <summary>The heading an inspector groups the property or component field under.</summary>
    public string? Category { get; set; }

    /// <summary>An inspector does not show it (scripts and files still see it).</summary>
    public bool Hidden { get; set; }

    /// <summary>An inspector shows it but does not let it be edited.</summary>
    public bool ReadOnly { get; set; }
}

/// <summary>A method that scripts can call. A <c>ValueTuple</c> return gives a script several results.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class MethodAttribute : Attribute
{
    public string? Name { get; set; }
}

/// <summary>A <see cref="Signal"/> or <see cref="Signal{T1}"/> property or field that scripts can connect to.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
public sealed class SignalAttribute : Attribute
{
    public string? Name { get; set; }
}

/// <summary>
/// An unmanaged struct that is an ECS component. Every public instance field is a field of the component (bool, integers,
/// <c>float</c>, <c>double</c>, <c>Vector3</c>, <see cref="Entity"/>); a struct with no fields is a tag. A field may carry <see cref="PropertyAttribute"/> for what an inspector shows
/// (a range, a category, hidden, read-only).
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class ComponentAttribute : Attribute
{
    public string? Name { get; set; }
}

/// <summary>A static <c>partial</c> class whose <see cref="MethodAttribute"/> methods are a global table in scripts (<c>imgui.text(…)</c>).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ScriptLibraryAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
