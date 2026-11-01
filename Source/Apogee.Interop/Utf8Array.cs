using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Apogee.Interop;

/// <summary>A <c>const char* const*</c> for native calls (Vulkan's layer and extension name lists): the strings live in native memory until disposed.</summary>
public sealed unsafe class Utf8Array : IDisposable
{
    private byte** _items;
    private readonly int _count;

    public Utf8Array(IReadOnlyList<string> strings)
    {
        ArgumentNullException.ThrowIfNull(strings);

        _count = strings.Count;
        _items = (byte**)NativeMemory.AllocZeroed((nuint)Math.Max(1, _count), (nuint)sizeof(byte*));
        for (int i = 0; i < _count; i++)
        {
            _items[i] = (byte*)Marshal.StringToCoTaskMemUTF8(strings[i]);
        }
    }

    public int Count => _count;

    /// <summary>The array of C strings, for example a <c>PpEnabledLayerNames</c>; valid until <see cref="Dispose"/>.</summary>
    public byte** Items => _items is null ? throw new ObjectDisposedException(nameof(Utf8Array)) : _items;

    public void Dispose()
    {
        if (_items is null)
        {
            return;
        }

        for (int i = 0; i < _count; i++)
        {
            Marshal.FreeCoTaskMem((IntPtr)_items[i]);
        }

        NativeMemory.Free(_items);
        _items = null;
    }
}
