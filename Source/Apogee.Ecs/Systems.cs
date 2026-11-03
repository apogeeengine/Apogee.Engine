using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apogee.Interop;
using Apogee.Native.Flecs;
using Apogee.Reflection;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Ecs;

/// <summary>Where in a frame a system runs; phases run in this order, and systems within one in the order they were added.</summary>
public enum Phase
{
    OnLoad,
    PostLoad,
    PreUpdate,
    OnUpdate,
    OnValidate,
    PostUpdate,
    PreStore,
    OnStore,
}

public enum ObserverEvent
{
    /// <summary>The component was added to an entity.</summary>
    OnAdd,

    /// <summary>The component was set or marked modified.</summary>
    OnSet,

    /// <summary>The component was removed, or its entity deleted.</summary>
    OnRemove,
}

public sealed unsafe partial class World
{
    /// <summary>What a system or observer callback needs, kept alive by a GCHandle that Flecs frees with the system.</summary>
    private sealed class CallbackData(World world, TableAction action)
    {
        public World World { get; } = world;

        public TableAction Action { get; } = action;
    }

    internal static void FillTerms(ref QueryDesc desc, ReadOnlySpan<QueryTerm> terms)
    {
        if (terms.Length is 0 or > 32)
        {
            throw new ArgumentException("A query has 1 to 32 terms.", nameof(terms));
        }

        for (int i = 0; i < terms.Length; i++)
        {
            desc.Terms[i].Id = terms[i].Id.Id;
            desc.Terms[i].Oper = (short)terms[i].Operator;
            if (terms[i].Source == QuerySource.ParentCascade)
            {
                // Up + Cascade on the source, along ChildOf: the parent's component, with parents ordered before their children.
                desc.Terms[i].Src.Id = unchecked((ulong)(Flecs.Up | Flecs.Cascade));
                desc.Terms[i].Trav = Flecs.ChildOf;
            }
        }
    }

    /// <summary>
    /// Adds a system: <paramref name="action"/> runs once per matching table in every <see cref="Progress"/>, in its phase. An exception
    /// it throws is held until the frame is done (see <see cref="Progress"/>). Destroy the returned entity to remove the system.
    /// </summary>
    public Entity AddSystem(string name, ReadOnlySpan<QueryTerm> terms, Phase phase, TableAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        GCHandle handle = GCHandle.Alloc(new CallbackData(this, action));
        var desc = new SystemDesc
        {
            Phase = PhaseId(phase),
            Callback = &SystemCallback,
            Ctx = (void*)GCHandle.ToIntPtr(handle),
            CtxFree = &FreeContext,
        };
        FillTerms(ref desc.Query, terms);

        // On failure Flecs may or may not have called the free callback already, so the handle is left alone: a leak beats a double free.
        Entity entity = CreateNamedEntity(name);
        desc.Entity = entity.Id;
        if (Flecs.SystemInit(Native, &desc) == 0)
        {
            throw new InvalidOperationException($"Flecs could not create the system {name}.");
        }

        return entity;
    }

    public Entity AddSystem<T1>(string name, Phase phase, EachAction<T1> action)
        where T1 : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        return AddSystem(name, [new QueryTerm(Component<T1>())], phase, (in TableView view) =>
        {
            Span<T1> a = view.Field<T1>(0);
            ReadOnlySpan<Entity> entities = view.Entities;
            for (int i = 0; i < entities.Length; i++)
            {
                action(entities[i], ref a[i]);
            }
        });
    }

    public Entity AddSystem<T1, T2>(string name, Phase phase, EachAction<T1, T2> action)
        where T1 : unmanaged
        where T2 : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        return AddSystem(name, [new QueryTerm(Component<T1>()), new QueryTerm(Component<T2>())], phase, (in TableView view) =>
        {
            Span<T1> a = view.Field<T1>(0);
            Span<T2> b = view.Field<T2>(1);
            ReadOnlySpan<Entity> entities = view.Entities;
            for (int i = 0; i < entities.Length; i++)
            {
                action(entities[i], ref a[i], ref b[i]);
            }
        });
    }

    public Entity AddSystem<T1, T2, T3>(string name, Phase phase, EachAction<T1, T2, T3> action)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        return AddSystem(name, [new QueryTerm(Component<T1>()), new QueryTerm(Component<T2>()), new QueryTerm(Component<T3>())], phase, (in TableView view) =>
        {
            Span<T1> a = view.Field<T1>(0);
            Span<T2> b = view.Field<T2>(1);
            Span<T3> c = view.Field<T3>(2);
            ReadOnlySpan<Entity> entities = view.Entities;
            for (int i = 0; i < entities.Length; i++)
            {
                action(entities[i], ref a[i], ref b[i], ref c[i]);
            }
        });
    }

    /// <summary>
    /// Adds an observer: <paramref name="action"/> runs when a matching entity gets the event (a component added, set or removed),
    /// at once, not in a phase. Destroy the returned entity to remove it.
    /// </summary>
    public Entity AddObserver(string name, ReadOnlySpan<QueryTerm> terms, ObserverEvent trigger, TableAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        GCHandle handle = GCHandle.Alloc(new CallbackData(this, action));
        var desc = new ObserverDesc
        {
            Callback = &SystemCallback,
            Ctx = (void*)GCHandle.ToIntPtr(handle),
            CtxFree = &FreeContext,
        };
        desc.Events[0] = trigger switch
        {
            ObserverEvent.OnAdd => Flecs.OnAdd,
            ObserverEvent.OnSet => Flecs.OnSet,
            _ => Flecs.OnRemove,
        };
        FillTerms(ref desc.Query, terms);

        Entity entity = CreateNamedEntity(name);
        desc.Entity = entity.Id;
        if (Flecs.ObserverInit(Native, &desc) == 0)
        {
            throw new InvalidOperationException($"Flecs could not create the observer {name}.");
        }

        return entity;
    }

    public Entity AddObserver<T1>(string name, ObserverEvent trigger, EachAction<T1> action)
        where T1 : unmanaged
    {
        ArgumentNullException.ThrowIfNull(action);
        return AddObserver(name, [new QueryTerm(Component<T1>())], trigger, (in TableView view) =>
        {
            Span<T1> a = view.Field<T1>(0);
            ReadOnlySpan<Entity> entities = view.Entities;
            for (int i = 0; i < entities.Length; i++)
            {
                action(entities[i], ref a[i]);
            }
        });
    }

    private static ulong PhaseId(Phase phase) => phase switch
    {
        Phase.OnLoad => Flecs.OnLoad,
        Phase.PostLoad => Flecs.PostLoad,
        Phase.PreUpdate => Flecs.PreUpdate,
        Phase.OnUpdate => Flecs.OnUpdate,
        Phase.OnValidate => Flecs.OnValidate,
        Phase.PostUpdate => Flecs.PostUpdate,
        Phase.PreStore => Flecs.PreStore,
        _ => Flecs.OnStore,
    };

    // The callbacks are called by Flecs, so an exception must never leave them: it is held on the world and thrown by Progress.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SystemCallback(Iter* it)
    {
        var data = (CallbackData)GCHandle.FromIntPtr((IntPtr)it->Ctx).Target!;
        try
        {
            var view = new TableView(it);
            data.Action(in view);
        }
#pragma warning disable CA1031 // See above: nothing may unwind through Flecs.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            data.World.Record(failure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FreeContext(void* context) => GCHandle.FromIntPtr((IntPtr)context).Free();
}
