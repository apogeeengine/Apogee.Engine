using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Apogee.Core.Memory;

/// <summary>
/// A fixed-size bump allocator over unmanaged memory. Allocations are zeroed and live until <see cref="Reset"/>; the
/// memory never moves, so pointers and spans into it stay valid. Not thread safe.
/// </summary>
public sealed unsafe class Arena : IDisposable
{
    private const int MaxAlignment = 16;

    private byte* _start;
    private readonly nuint _capacity;
    private nuint _offset;

    public Arena(nuint capacity)
    {
        _capacity = capacity;
        _start = (byte*)NativeMemory.AlignedAlloc(capacity, MaxAlignment);
    }

    public nuint Capacity => _capacity;

    public nuint Used => _offset;

    /// <summary>A position to <see cref="Reset"/> back to, freeing everything allocated after it.</summary>
    public nuint Mark => _offset;

    public Span<T> Allocate<T>(int count)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ObjectDisposedException.ThrowIf(_start is null, this);

        nuint alignment = (nuint)Math.Min(MaxAlignment, (int)BitOperations.RoundUpToPowerOf2((uint)sizeof(T)));
        nuint start = (_offset + alignment - 1) & ~(alignment - 1);
        nuint size = checked((nuint)count * (nuint)sizeof(T));
        if (start > _capacity || size > _capacity - start)
        {
            throw new InvalidOperationException($"The arena is full: {_capacity - _offset} bytes left, {size} requested.");
        }

        _offset = start + size;
        byte* pointer = _start + start;
        NativeMemory.Clear(pointer, size);
        return new Span<T>(pointer, count);
    }

    public void Reset(nuint mark = 0)
    {
        if (mark > _offset)
        {
            throw new ArgumentOutOfRangeException(nameof(mark), "The mark is beyond what has been allocated.");
        }

        _offset = mark;
    }

    public void Dispose()
    {
        NativeMemory.AlignedFree(_start);
        _start = null;
        GC.SuppressFinalize(this);
    }

    ~Arena() => NativeMemory.AlignedFree(_start);
}
