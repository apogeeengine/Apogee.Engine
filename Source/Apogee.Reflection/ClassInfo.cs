using System;
using System.Collections.Generic;

namespace Apogee.Reflection;

/// <summary>What the registry knows about an <see cref="EngineObject"/> class. Members are looked up by script name, inherited ones included.</summary>
public sealed class ClassInfo
{
    private readonly Func<EngineObject>? _factory;
    private Dictionary<string, PropertyDef>? _properties;
    private Dictionary<string, MethodDef>? _methods;
    private Dictionary<string, SignalDef>? _signals;

    public ClassInfo(
        string name,
        string? baseName,
        Type clrType,
        PropertyDef[] properties,
        MethodDef[] methods,
        SignalDef[] signals,
        Func<EngineObject>? factory = null,
        bool scriptable = true,
        bool modsBlocked = false,
        string? doc = null)
    {
        Name = name;
        BaseName = baseName;
        ClrType = clrType;
        Properties = properties;
        Methods = methods;
        Signals = signals;
        _factory = factory;
        Scriptable = scriptable;
        ModsBlocked = modsBlocked;
        Doc = doc;
    }

    public string Name { get; }

    /// <summary>The name of the closest reflected base class, or null.</summary>
    public string? BaseName { get; }

    public Type ClrType { get; }

    /// <summary>The members declared by this class itself.</summary>
    public PropertyDef[] Properties { get; }

    public MethodDef[] Methods { get; }

    public SignalDef[] Signals { get; }

    public bool Scriptable { get; }

    /// <summary>Objects of this class are refused to mods.</summary>
    public bool ModsBlocked { get; }

    public string? Doc { get; }

    /// <summary>True for a class scripts can create with <c>new()</c>.</summary>
    public bool Creatable => _factory is not null;

    public ClassInfo? Base => BaseName is null ? null : TypeRegistry.FindClass(BaseName);

    /// <summary>Creates an object of this class; the class must be <see cref="Creatable"/>.</summary>
    public EngineObject Create() => (_factory ?? throw new InvalidOperationException($"{Name} cannot be created from scripts")).Invoke();

    public bool IsSubclassOf(ClassInfo other)
    {
        for (ClassInfo? current = this; current is not null; current = current.Base)
        {
            if (current == other)
            {
                return true;
            }
        }

        return false;
    }

    public PropertyDef? FindProperty(string scriptName) => (_properties ??= Flatten(c => c.Properties, p => p.ScriptName)).GetValueOrDefault(scriptName);

    public MethodDef? FindMethod(string scriptName) => (_methods ??= Flatten(c => c.Methods, m => m.ScriptName)).GetValueOrDefault(scriptName);

    public SignalDef? FindSignal(string scriptName) => (_signals ??= Flatten(c => c.Signals, s => s.ScriptName)).GetValueOrDefault(scriptName);

    /// <summary>Every member of the chain, a derived class's hiding the base's of the same name.</summary>
    private Dictionary<string, T> Flatten<T>(Func<ClassInfo, T[]> members, Func<T, string> name)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        for (ClassInfo? current = this; current is not null; current = current.Base)
        {
            foreach (T member in members(current))
            {
                result.TryAdd(name(member), member);
            }
        }

        return result;
    }
}
