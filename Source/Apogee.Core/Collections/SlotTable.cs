using System;

namespace Apogee.Core.Collections;

/// <summary>
/// Names a slot of a <see cref="SlotTable{T}"/> and the generation it had when the item was added. Once the slot is reused the
/// generation differs, so a stale handle is recognised instead of reaching the new item. <c>default</c> is never valid.
/// </summary>
public readonly record struct SlotHandle(uint Index, uint Generation)
{
    public bool IsNull => Generation == 0;

    public override string ToString() => IsNull ? "null" : $"{Index}:{Generation}";
}

/// <summary>
/// Items that are looked up by <see cref="SlotHandle"/>: O(1) add, remove and lookup, freed slots are reused and a handle to a
/// removed item stays invalid for good. Not thread safe.
/// </summary>
public sealed class SlotTable<T>
    where T : class
{
    private struct Slot
    {
        public T? Item;
        public uint Generation;
        public int NextFree;
    }

    private Slot[] _slots = new Slot[16];
    private int _used;
    private int _freeHead = -1;

    public int Count { get; private set; }

    public SlotHandle Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        int index;
        if (_freeHead >= 0)
        {
            index = _freeHead;
            _freeHead = _slots[index].NextFree;
        }
        else
        {
            if (_used == _slots.Length)
            {
                Array.Resize(ref _slots, _slots.Length * 2);
            }

            index = _used++;
        }

        ref Slot slot = ref _slots[index];
        slot.Item = item;
        slot.Generation++; // starts at 1: generation 0 is the null handle
        Count++;
        return new SlotHandle((uint)index, slot.Generation);
    }

    public bool TryGet(SlotHandle handle, out T item)
    {
        if (handle.Index < (uint)_used && _slots[handle.Index] is { Item: not null } slot && slot.Generation == handle.Generation)
        {
            item = slot.Item;
            return true;
        }

        item = null!;
        return false;
    }

    public bool IsAlive(SlotHandle handle) => TryGet(handle, out _);

    /// <summary>Removes the item; false when the handle was already stale.</summary>
    public bool Remove(SlotHandle handle)
    {
        if (!IsAlive(handle))
        {
            return false;
        }

        ref Slot slot = ref _slots[handle.Index];
        slot.Item = null;
        slot.NextFree = _freeHead;
        _freeHead = (int)handle.Index;
        Count--;
        return true;
    }
}
