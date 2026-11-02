using System;
using System.Collections.Generic;

namespace Apogee.Reflection;

/// <summary>One step down into a table: the field <see cref="Name"/>, or when that is null the element <see cref="Index"/> of a list (counted from 0).</summary>
public readonly record struct DataPathSegment(string? Name, int Index = -1)
{
    public static DataPathSegment Field(string name) => new(name);

    public static DataPathSegment Element(int index) => new(null, index);
}

/// <summary>What a place in a table holds, as a data model sees it.</summary>
public enum ScriptDataKind
{
    /// <summary>Nothing (nil or no such field).</summary>
    Missing,

    /// <summary>A boolean, number or string.</summary>
    Scalar,

    /// <summary>A table that is a list (or empty).</summary>
    Array,

    /// <summary>A table with named fields.</summary>
    Struct,

    /// <summary>A function.</summary>
    Function,
}

/// <summary>
/// A table a script handed to the engine, kept by reference and read when needed: what UI data models are made of. A place in it is a path of fields and list elements; reading the same path later
/// gives what the script has put there since.
/// </summary>
public abstract class ScriptTable : IDisposable
{
    public abstract ScriptDataKind KindAt(ReadOnlySpan<DataPathSegment> path);

    /// <summary>The boolean, number or string at the path, or nil.</summary>
    public abstract Value GetAt(ReadOnlySpan<DataPathSegment> path);

    /// <summary>Writes a scalar at the path (the field must exist). False when it cannot be.</summary>
    public abstract bool SetAt(ReadOnlySpan<DataPathSegment> path, Value value);

    /// <summary>The number of elements of the list at the path.</summary>
    public abstract int LengthAt(ReadOnlySpan<DataPathSegment> path);

    /// <summary>The names of the fields of the table at the path, sorted.</summary>
    public abstract IReadOnlyList<string> KeysAt(ReadOnlySpan<DataPathSegment> path);

    /// <summary>The function at the path, or null.</summary>
    public abstract ScriptFunction? FunctionAt(ReadOnlySpan<DataPathSegment> path);

    public abstract void Dispose();
}
