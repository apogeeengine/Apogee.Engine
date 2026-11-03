using System;
using Apogee.Native.Flecs;
using Apogee.Reflection;
using Flecs = Apogee.Native.Flecs.Ecs;
using NativeQuery = Apogee.Native.Flecs.Query;

namespace Apogee.Ecs;

public enum QueryOperator
{
    /// <summary>The entity must have the component.</summary>
    And = 0,

    /// <summary>The entity must not have it.</summary>
    Not = 2,

    /// <summary>The field is null in tables that do not have it.</summary>
    Optional = 3,
}

/// <summary>Where a term's component is taken from.</summary>
public enum QuerySource
{
    /// <summary>The matched entity itself.</summary>
    Self,

    /// <summary>The entity's parent (the nearest ancestor that has it); entities are visited parents first. The field is a single element shared by the table.</summary>
    ParentCascade,
}

/// <summary>One term of a query: a component (or a pair), how it must match, and where it is read from.</summary>
public readonly record struct QueryTerm(Entity Id, QueryOperator Operator = QueryOperator.And, QuerySource Source = QuerySource.Self);

/// <summary>
/// Entities that match a list of terms, cached by Flecs. Iterating defers structural changes (adding or removing components,
/// deleting entities) until the iteration is done. Dispose it when it is not needed; the world disposes the rest.
/// </summary>
public unsafe class Query : IDisposable
{
    private readonly World _world;
    private NativeQuery* _query;

    internal Query(World world, ReadOnlySpan<QueryTerm> terms)
    {
        _world = world;
        var desc = new QueryDesc();
        World.FillTerms(ref desc, terms);
        _query = Flecs.QueryInit(world.Native, &desc);
        if (_query is null)
        {
            throw new InvalidOperationException("Flecs could not create the query (is every term a component or a pair?).");
        }

        TermCount = terms.Length;
        world.Track(this);
    }

    public int TermCount { get; }

    public bool IsDisposed => _query is null;

    /// <summary>Calls <paramref name="action"/> for each table that matches.</summary>
    public void ForEachTable(TableAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeQuery* query = _query is null ? throw new ObjectDisposedException(nameof(Query)) : _query;
        Apogee.Native.Flecs.World* world = _world.Native;

        Iter it = Flecs.QueryIter(world, query);
        bool finished = false;
        _ = Flecs.DeferBegin(world);
        try
        {
            while (Flecs.QueryNext(&it))
            {
                var view = new TableView(&it);
                action(in view);
            }

            finished = true; // the iterator cleans up after itself when it runs out
        }
        finally
        {
            if (!finished)
            {
                Flecs.IterFini(&it);
            }

            _ = Flecs.DeferEnd(world);
        }
    }

    /// <summary>The number of matching entities.</summary>
    public int Count()
    {
        int count = 0;
        ForEachTable((in TableView view) => count += view.Count);
        return count;
    }

    public void Dispose()
    {
        if (_query is null)
        {
            return;
        }

        Flecs.QueryFini(_query);
        _query = null;
        _world.Untrack(this);
        GC.SuppressFinalize(this);
    }
}

public sealed class Query<T1> : Query
    where T1 : unmanaged
{
    internal Query(World world, Entity a)
        : base(world, [new QueryTerm(a)])
    {
    }

    public void Each(EachAction<T1> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ForEachTable((in TableView view) =>
        {
            Span<T1> a = view.Field<T1>(0);
            ReadOnlySpan<Entity> entities = view.Entities;
            for (int i = 0; i < entities.Length; i++)
            {
                action(entities[i], ref a[i]);
            }
        });
    }
}

public sealed class Query<T1, T2> : Query
    where T1 : unmanaged
    where T2 : unmanaged
{
    internal Query(World world, Entity a, Entity b)
        : base(world, [new QueryTerm(a), new QueryTerm(b)])
    {
    }

    public void Each(EachAction<T1, T2> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ForEachTable((in TableView view) =>
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
}

public sealed class Query<T1, T2, T3> : Query
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
{
    internal Query(World world, Entity a, Entity b, Entity c)
        : base(world, [new QueryTerm(a), new QueryTerm(b), new QueryTerm(c)])
    {
    }

    public void Each(EachAction<T1, T2, T3> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ForEachTable((in TableView view) =>
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
}

public sealed class Query<T1, T2, T3, T4> : Query
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
    where T4 : unmanaged
{
    internal Query(World world, Entity a, Entity b, Entity c, Entity d)
        : base(world, [new QueryTerm(a), new QueryTerm(b), new QueryTerm(c), new QueryTerm(d)])
    {
    }

    public void Each(EachAction<T1, T2, T3, T4> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ForEachTable((in TableView view) =>
        {
            Span<T1> a = view.Field<T1>(0);
            Span<T2> b = view.Field<T2>(1);
            Span<T3> c = view.Field<T3>(2);
            Span<T4> d = view.Field<T4>(3);
            ReadOnlySpan<Entity> entities = view.Entities;
            for (int i = 0; i < entities.Length; i++)
            {
                action(entities[i], ref a[i], ref b[i], ref c[i], ref d[i]);
            }
        });
    }
}
