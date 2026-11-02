using System.Threading;
using Apogee.Core.Collections;

namespace Apogee.Reflection;

/// <summary>
/// The base class of everything scripts can hold. Every object has a <see cref="SlotHandle"/> (index and generation) in one
/// table: scripts keep the handle, never the object, so a destroyed object is recognised and cannot be used by accident.
/// Derived classes are <c>partial</c> and marked <see cref="ApogeeClassAttribute"/>; the source generator writes <see cref="Class"/>.
/// </summary>
public abstract class EngineObject
{
    private static readonly SlotTable<EngineObject> s_objects = new();
    private static readonly Lock s_lock = new();

    protected EngineObject()
    {
        lock (s_lock)
        {
            Handle = s_objects.Add(this);
        }
    }

    /// <summary>
    /// True once the engine has taken the object over (put it in a world, a scene, a list it owns). An object a script creates is destroyed
    /// when the script no longer holds it, unless the engine adopted it.
    /// </summary>
    public bool IsAdopted { get; private set; }

    /// <summary>Null once the object is destroyed.</summary>
    public SlotHandle Handle { get; private set; }

    public bool IsDestroyed => Handle.IsNull;

    /// <summary>What the registry knows about this object's class.</summary>
    public abstract ClassInfo Class { get; }

    /// <summary>Takes the object over from the script that created it: it now lives until <see cref="Destroy"/>.</summary>
    public void Adopt() => IsAdopted = true;

    /// <summary>Finds a live object by the handle a script holds.</summary>
    public static bool TryGet(SlotHandle handle, out EngineObject item)
    {
        lock (s_lock)
        {
            return s_objects.TryGet(handle, out item);
        }
    }

    /// <summary>Ends the object's life: its handle stops resolving, in scripts too. Does nothing when it is already destroyed.</summary>
    public void Destroy()
    {
        lock (s_lock)
        {
            if (IsDestroyed)
            {
                return;
            }

            s_objects.Remove(Handle);
            Handle = default;
        }

        OnDestroyed();
    }

    protected virtual void OnDestroyed()
    {
    }
}
