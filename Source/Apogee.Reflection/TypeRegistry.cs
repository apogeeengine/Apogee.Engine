using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Apogee.Reflection;

/// <summary>
/// Every class, enum, component and script library that the source generator described. Each assembly with reflected types
/// registers its own from a module initializer, so the registry is complete before the first lookup.
/// </summary>
public static class TypeRegistry
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, ClassInfo> s_classes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, EnumInfo> s_enums = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ComponentInfo> s_components = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LibraryInfo> s_libraries = new(StringComparer.Ordinal);

    static TypeRegistry()
    {
        // The root of every class: the base of one that has no described base, and the type of "any object".
        s_classes["EngineObject"] = new ClassInfo("EngineObject", null, typeof(EngineObject), [], [], [], doc: "The base of everything scripts can hold.");
    }

    public static IReadOnlyList<ClassInfo> Classes => Snapshot(s_classes);

    public static IReadOnlyList<EnumInfo> Enums => Snapshot(s_enums);

    public static IReadOnlyList<ComponentInfo> Components => Snapshot(s_components);

    public static IReadOnlyList<LibraryInfo> Libraries => Snapshot(s_libraries);

    public static void Register(ClassInfo info) => Add(s_classes, info.Name, info, "class");

    public static void Register(EnumInfo info) => Add(s_enums, info.Name, info, "enum");

    /// <summary>Registers the enum unless one of that name is there: every assembly registers the enums its classes use, including ones another assembly declares.</summary>
    public static void RegisterIfMissing(EnumInfo info)
    {
        lock (s_lock)
        {
            s_enums.TryAdd(info.Name, info);
        }
    }

    public static void Register(LibraryInfo info) => Add(s_libraries, info.Name, info, "library");

    public static void Register<T>(ComponentInfo info)
        where T : unmanaged
    {
        Add(s_components, info.Name, info, "component");
        ComponentOf<T>.Info = info;
    }

    public static ClassInfo? FindClass(string name) => Find(s_classes, name);

    public static ClassInfo GetClass(string name) => FindClass(name) ?? throw new KeyNotFoundException($"No class named {name} is registered.");

    public static EnumInfo? FindEnum(string name) => Find(s_enums, name);

    public static EnumInfo GetEnum(string name) => FindEnum(name) ?? throw new KeyNotFoundException($"No enum named {name} is registered.");

    public static ComponentInfo? FindComponent(string name) => Find(s_components, name);

    public static LibraryInfo? FindLibrary(string name) => Find(s_libraries, name);

    /// <summary>The component described by the struct <typeparamref name="T"/> (marked <see cref="ComponentAttribute"/>).</summary>
    public static ComponentInfo GetComponent<T>()
        where T : unmanaged =>
        ComponentOf<T>.Info ?? throw new KeyNotFoundException($"{typeof(T).Name} is not a registered component.");

    private static T? Find<T>(Dictionary<string, T> table, string name)
        where T : class
    {
        lock (s_lock)
        {
            return table.GetValueOrDefault(name);
        }
    }

    private static void Add<T>(Dictionary<string, T> table, string name, T info, string what)
    {
        lock (s_lock)
        {
            if (!table.TryAdd(name, info))
            {
                throw new InvalidOperationException($"A {what} named {name} is already registered.");
            }
        }
    }

    /// <summary>The entries sorted by name, so generated output (definitions, documentation) does not depend on assembly load order.</summary>
    private static T[] Snapshot<T>(Dictionary<string, T> table)
    {
        lock (s_lock)
        {
            return [.. table.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value)];
        }
    }

    private static class ComponentOf<T>
        where T : unmanaged
    {
        public static ComponentInfo? Info;
    }
}
