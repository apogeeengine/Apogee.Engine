using System;
using System.Collections.Generic;

namespace Apogee.Reflection;

/// <summary>
/// A table the engine makes for scripts: named fields holding values (numbers, strings, booleans, lists). A method that returns one gives the script a new table with those fields; nil fields are left out. It is
/// read-only and lives in C#, so it is not a table a script gave the engine (that is any other <see cref="ScriptTable"/>).
/// </summary>
public sealed class ScriptRecord : ScriptTable
{
    private readonly SortedDictionary<string, Value> _fields = new(StringComparer.Ordinal);

    /// <summary>The fields in name order.</summary>
    public IEnumerable<KeyValuePair<string, Value>> Fields => _fields;

    /// <summary>Sets a field; a nil value removes it. Returns the record, so that fields chain.</summary>
    public ScriptRecord With(string name, Value value)
    {
        if (value.IsNil)
        {
            _ = _fields.Remove(name);
        }
        else
        {
            _fields[name] = value;
        }

        return this;
    }

    public override ScriptDataKind KindAt(ReadOnlySpan<DataPathSegment> path)
    {
        if (path.IsEmpty)
        {
            return ScriptDataKind.Struct;
        }

        if (path.Length > 1 || path[0].Name is not { } name || !_fields.TryGetValue(name, out Value value))
        {
            return ScriptDataKind.Missing;
        }

        return value.Kind switch
        {
            ValueKind.List => ScriptDataKind.Array,
            ValueKind.Function => ScriptDataKind.Function,
            ValueKind.Table => ScriptDataKind.Struct,
            _ => ScriptDataKind.Scalar,
        };
    }

    public override Value GetAt(ReadOnlySpan<DataPathSegment> path) =>
        path.Length == 1 && path[0].Name is { } name && _fields.TryGetValue(name, out Value value) && KindAt(path) == ScriptDataKind.Scalar ? value : default;

    public override bool SetAt(ReadOnlySpan<DataPathSegment> path, Value value) => false;

    public override int LengthAt(ReadOnlySpan<DataPathSegment> path) =>
        path.Length == 1 && path[0].Name is { } name && _fields.TryGetValue(name, out Value value) && value.Kind == ValueKind.List ? value.ToList().Count : 0;

    public override IReadOnlyList<string> KeysAt(ReadOnlySpan<DataPathSegment> path) => path.IsEmpty ? [.. _fields.Keys] : [];

    public override ScriptFunction? FunctionAt(ReadOnlySpan<DataPathSegment> path) =>
        path.Length == 1 && path[0].Name is { } name && _fields.TryGetValue(name, out Value value) && value.Kind == ValueKind.Function ? value.ToFunction() : null;

    public override void Dispose()
    {
    }
}
