using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Apogee.Ecs;
using Apogee.Reflection;
using Apogee.Rendering;

namespace Apogee.Host.Editing;

/// <summary>
/// What a component starts as when an editor adds it, and what <i>Reset to default</i> puts back: a table from the component's name to its bytes. The engine's own components have a usable value
/// (<see cref="RegisterEngine"/>: a <see cref="Transform"/> with a scale of zero has no size); a component nobody registered starts as zeros, as one made in a script does. A game or an editor
/// extension adds its own with <see cref="Register(string, byte[])"/>.
/// </summary>
public static class ComponentDefaults
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, byte[]> s_values = new(StringComparer.Ordinal);

    /// <summary>Sets the default of the component called <paramref name="name"/> to a copy of <paramref name="bytes"/> (its whole value, as many bytes as the component's size), replacing an earlier one.</summary>
    public static void Register(string name, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(bytes);
        lock (s_lock)
        {
            s_values[name] = (byte[])bytes.Clone();
        }
    }

    /// <summary>Sets the default of the component <typeparamref name="T"/> (registered under its type's name).</summary>
    public static void Register<T>(in T value)
        where T : unmanaged =>
        Register(typeof(T).Name, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray());

    /// <summary>The registered default of the component called <paramref name="name"/>; false when there is none (a component with no default is zero).</summary>
    public static bool TryGet(string name, out ReadOnlySpan<byte> bytes)
    {
        lock (s_lock)
        {
            if (s_values.TryGetValue(name, out byte[]? value))
            {
                bytes = value;
                return true;
            }
        }

        bytes = default;
        return false;
    }

    /// <summary>Forgets the default of <paramref name="name"/>, so that component starts as zeros again. False when it had none.</summary>
    public static bool Remove(string name)
    {
        lock (s_lock)
        {
            return s_values.Remove(name);
        }
    }

    /// <summary>
    /// The bytes a new <paramref name="info"/> starts as: its registered default, or zeros when it has none or the default does not match the component's size (a layout that changed since);
    /// null for a tag, which has no bytes.
    /// </summary>
    public static byte[]? ValueFor(ComponentInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.IsTag)
        {
            return null;
        }

        if (TryGet(info.Name, out ReadOnlySpan<byte> bytes) && bytes.Length == info.Size)
        {
            return bytes.ToArray();
        }

        return new byte[info.Size];
    }

    /// <summary>
    /// Registers the defaults of the engine's components: the identity <see cref="Transform"/>, <see cref="CameraComponent.Default"/> and a white point <see cref="Light"/>. An editor calls it once at
    /// start; calling it again keeps what has been registered since (a game's own choice for one of these wins).
    /// </summary>
    public static void RegisterEngine()
    {
        RegisterIfMissing(Transform.Identity);
        RegisterIfMissing(CameraComponent.Default);
        RegisterIfMissing(Light.Point(Vector3.One, 80f, 12f));
    }

    private static void RegisterIfMissing<T>(in T value)
        where T : unmanaged
    {
        lock (s_lock)
        {
            s_values.TryAdd(typeof(T).Name, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray());
        }
    }
}
