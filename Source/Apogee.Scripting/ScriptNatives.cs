using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Apogee.Native.Luau;

namespace Apogee.Scripting;

/// <summary>
/// Methods of a class that are written by hand against the Luau stack because they take tables, functions or variable shapes that the
/// registry cannot describe (the ECS calls on <c>World</c>). A native sees the stack as a method does: the object at index 1, then the
/// arguments; it returns the number of results it pushed, and throws a <see cref="Apogee.Reflection.ValueException"/> for a script error.
/// Each comes with its <c>.d.luau</c> declaration, so the definitions cannot lack one.
/// </summary>
public static unsafe class ScriptNatives
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, List<NativeMethod>> s_byClass = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<NativeFunction>> s_byLibrary = new(StringComparer.Ordinal);

    /// <param name="Body">A <c>delegate*&lt;State*, int&gt;</c>.</param>
    /// <param name="Declaration">The member as the definitions declare it, such as <c>function set(self, entity: number): ()</c>.</param>
    internal sealed record NativeMethod(string Class, string Name, nint Body, string Declaration);

    /// <summary>Adds a native method to the class registered under <paramref name="className"/>. Does nothing when it is there already (a VM registers them each time).</summary>
    public static void Register(string className, string name, delegate*<State*, int> body, string declaration)
    {
        lock (s_lock)
        {
            if (!s_byClass.TryGetValue(className, out List<NativeMethod>? list))
            {
                s_byClass[className] = list = [];
            }

            if (list.Exists(m => m.Name == name))
            {
                return;
            }

            list.Add(new NativeMethod(className, name, (nint)body, declaration));
        }
    }

    /// <param name="Body">A <c>delegate*&lt;State*, int&gt;</c>.</param>
    /// <param name="Type">The function's type as the definitions declare it, such as <c>(text: string) -&gt; XmlElement</c>.</param>
    /// <param name="Doc">One line for hover, or null.</param>
    internal sealed record NativeFunction(string Library, string Name, nint Body, string Type, string? Doc);

    /// <summary>
    /// Adds a function to the global table <paramref name="library"/> (<c>xml.decode</c>), which scripts see in every VM. Like the native methods it reads the stack itself and
    /// comes with its declaration. Does nothing when it is there already.
    /// </summary>
    public static void RegisterLibrary(string library, string name, delegate*<State*, int> body, string type, string? doc = null)
    {
        lock (s_lock)
        {
            if (!s_byLibrary.TryGetValue(library, out List<NativeFunction>? list))
            {
                s_byLibrary[library] = list = [];
            }

            if (list.Exists(f => f.Name == name))
            {
                return;
            }

            list.Add(new NativeFunction(library, name, (nint)body, type, doc));
        }
    }

    /// <summary>The libraries made of native functions, by name, with their functions in the order they were registered.</summary>
    internal static IReadOnlyList<(string Library, IReadOnlyList<NativeFunction> Functions)> Libraries()
    {
        lock (s_lock)
        {
            return [.. s_byLibrary.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, (IReadOnlyList<NativeFunction>)[.. p.Value]))];
        }
    }

    internal static IReadOnlyList<NativeMethod> Of(string className)
    {
        lock (s_lock)
        {
            return s_byClass.TryGetValue(className, out List<NativeMethod>? list) ? [.. list] : [];
        }
    }
}
