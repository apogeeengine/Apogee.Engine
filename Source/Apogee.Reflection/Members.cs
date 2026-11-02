using System;

namespace Apogee.Reflection;

public delegate Value PropertyGetter(object target);

public delegate void PropertySetter(object target, in Value value);

/// <summary>Calls a method with arguments already counted; writes its results (none for <c>void</c>, one per tuple item) to <paramref name="results"/>.</summary>
public delegate void MethodInvoker(object? target, ReadOnlySpan<Value> arguments, Span<Value> results);

/// <summary>Receives the arguments of an emitted signal as values.</summary>
public delegate void ScriptHandler(ReadOnlySpan<Value> arguments);

public delegate Connection SignalConnector(object target, ScriptHandler handler);

public sealed record ParameterDef(string Name, TypeRef Type, bool Optional = false);

/// <summary>A property: the setter is null for a read-only one.</summary>
public sealed class PropertyDef(string name, string scriptName, TypeRef type, PropertyGetter getter, PropertySetter? setter, string? doc = null, InspectorMeta? meta = null)
{
    public string Name { get; } = name;

    /// <summary>The name in scripts: snake_case.</summary>
    public string ScriptName { get; } = scriptName;

    public TypeRef Type { get; } = type;

    public PropertyGetter Getter { get; } = getter;

    public PropertySetter? Setter { get; } = setter;

    public string? Doc { get; } = doc;

    /// <summary>What an inspector shows it with (a range, a category...), or null.</summary>
    public InspectorMeta? Meta { get; } = meta;
}

public sealed class MethodDef
{
    private readonly MethodInvoker _invoker;
    private readonly int _required;

    public MethodDef(string name, string scriptName, bool isStatic, ParameterDef[] parameters, TypeRef[] results, MethodInvoker invoker, string? doc = null)
    {
        Name = name;
        ScriptName = scriptName;
        IsStatic = isStatic;
        Parameters = parameters;
        Results = results;
        Doc = doc;
        _invoker = invoker;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (!parameters[i].Optional)
            {
                _required = i + 1;
            }
        }
    }

    public string Name { get; }

    public string ScriptName { get; }

    public bool IsStatic { get; }

    public ParameterDef[] Parameters { get; }

    /// <summary>The types of the results: empty for <c>void</c>, one per item of a tuple.</summary>
    public TypeRef[] Results { get; }

    public string? Doc { get; }

    /// <summary>Calls the method after checking the argument count; <paramref name="results"/> must hold <see cref="Results"/>.Length values.</summary>
    public void Invoke(object? target, ReadOnlySpan<Value> arguments, Span<Value> results)
    {
        if (arguments.Length < _required || arguments.Length > Parameters.Length)
        {
            string expected = _required == Parameters.Length ? $"{_required}" : $"{_required} to {Parameters.Length}";
            throw new ValueException($"{ScriptName} expects {expected} arguments, got {arguments.Length}");
        }

        _invoker(target, arguments, results);
    }
}

public sealed class SignalDef(string name, string scriptName, ParameterDef[] parameters, SignalConnector connector, string? doc = null)
{
    public string Name { get; } = name;

    public string ScriptName { get; } = scriptName;

    public ParameterDef[] Parameters { get; } = parameters;

    public string? Doc { get; } = doc;

    /// <summary>Connects <paramref name="handler"/> to the signal of <paramref name="target"/>.</summary>
    public Connection Connect(object target, ScriptHandler handler) => connector(target, handler);
}

/// <summary>A static class whose methods are a global table in scripts.</summary>
public sealed class LibraryInfo(string name, MethodDef[] methods, string? doc = null)
{
    public string Name { get; } = name;

    public MethodDef[] Methods { get; } = methods;

    public string? Doc { get; } = doc;
}
