using System;
using Apogee.Native.Flecs;
using Apogee.Reflection;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Ecs;

/// <summary>Called for each table (group of entities with the same components) a query or system matches.</summary>
public delegate void TableAction(in TableView view);

/// <summary>Called for each matching entity with references to its components.</summary>
public delegate void EachAction<T1>(Entity entity, ref T1 a)
    where T1 : unmanaged;

public delegate void EachAction<T1, T2>(Entity entity, ref T1 a, ref T2 b)
    where T1 : unmanaged
    where T2 : unmanaged;

public delegate void EachAction<T1, T2, T3>(Entity entity, ref T1 a, ref T2 b, ref T3 c)
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged;

public delegate void EachAction<T1, T2, T3, T4>(Entity entity, ref T1 a, ref T2 b, ref T3 c, ref T4 d)
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
    where T4 : unmanaged;

/// <summary>
/// The entities of one table and their components, valid only inside the call that got it. A field is the component of the term with
/// that index; components are arrays, one element per entity (a field a term gets from elsewhere, such as a singleton, is a single element).
/// </summary>
public readonly unsafe ref struct TableView
{
    private readonly Iter* _it;

    internal TableView(Iter* it) => _it = it;

    public int Count => _it->Count;

    /// <summary>The time step of the frame for a system or observer; zero in a query.</summary>
    public float DeltaTime => _it->DeltaTime;

    public ReadOnlySpan<Entity> Entities => new((Entity*)_it->Entities, _it->Count);

    /// <summary>The memory of the component of the term with this index, or null when the table has none (a <c>Not</c> or absent optional term).</summary>
    public byte* FieldPointer(int index, int size) => (byte*)Flecs.FieldWSize(_it, (nuint)size, (sbyte)index);

    public Span<T> Field<T>(int index)
        where T : unmanaged
    {
        byte* data = FieldPointer(index, sizeof(T));
        return data is null ? default : new Span<T>(data, _it->Count);
    }
}
