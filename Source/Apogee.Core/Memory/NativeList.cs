using System;
using System.Runtime.InteropServices;

namespace Apogee.Core.Memory;

/// <summary>A growable list of unmanaged values in unmanaged memory. Must be disposed. Not thread safe.</summary>
public sealed unsafe class NativeList<T> : IDisposable
    where T : unmanaged
{
    private T* _items;
    private int _capacity;

    public NativeList(int capacity = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
        _items = capacity == 0 ? null : (T*)NativeMemory.Alloc((nuint)capacity, (nuint)sizeof(T));
    }

    public int Count { get; private set; }

    public int Capacity => _capacity;

    /// <summary>The items. The span is invalidated by anything that adds to the list.</summary>
    public Span<T> AsSpan() => new(_items, Count);

    public ref T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count);
            return ref _items[index];
        }
    }

    public void Add(T item)
    {
        if (Count == _capacity)
        {
            Reserve(Count + 1);
        }

        _items[Count++] = item;
    }

    /// <summary>Removes the item at <paramref name="index"/> by moving the last item into its place (order is not kept).</summary>
    public void RemoveAtSwapBack(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count);
        _items[index] = _items[--Count];
    }

    public void Clear() => Count = 0;

    public void Reserve(int capacity)
    {
        if (capacity <= _capacity)
        {
            return;
        }

        int grown = Math.Max(capacity, Math.Max(4, _capacity * 2));
        _items = (T*)NativeMemory.Realloc(_items, (nuint)grown * (nuint)sizeof(T));
        _capacity = grown;
    }

    public void Dispose()
    {
        NativeMemory.Free(_items);
        _items = null;
        Count = 0;
        _capacity = 0;
        GC.SuppressFinalize(this);
    }

    ~NativeList() => NativeMemory.Free(_items);
}
