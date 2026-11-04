using System;
using System.Collections.Generic;

namespace Apogee.Graphics;

/// <summary>A pool of integer slots with a free list: the indices of a bindless heap. Slot 0 is reserved for the default entry and never freed.</summary>
internal sealed class SlotAllocator(uint capacity)
{
    private readonly Stack<uint> _free = [];
    private readonly HashSet<uint> _live = [];
    private uint _next;

    public int Used => _live.Count;

    public uint Reserve(uint index)
    {
        if (index != _next)
        {
            throw new InvalidOperationException("Slots are reserved in order.");
        }

        _next++;
        _live.Add(index);
        return index;
    }

    public uint Allocate()
    {
        uint index;
        if (_free.Count > 0)
        {
            index = _free.Pop();
        }
        else if (_next < capacity)
        {
            index = _next++;
        }
        else
        {
            throw new GraphicsException($"The bindless heap is full ({capacity} entries).");
        }

        _live.Add(index);
        return index;
    }

    public void Free(uint index)
    {
        if (index == 0 || !_live.Remove(index))
        {
            throw new ArgumentException($"Slot {index} is not allocated (slot 0 is the default and stays).", nameof(index));
        }

        _free.Push(index);
    }
}
