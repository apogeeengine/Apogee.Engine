using System;
using Apogee.Interop;
using Xunit;

namespace Apogee.Interop.Tests;

public unsafe class Utf8Tests
{
    [Fact]
    public void ZeroTerminatedBytesEndInZeroAndRoundTrip()
    {
        byte[] bytes = Utf8.ToZeroTerminated("Größe ✓");

        Assert.Equal(0, bytes[^1]);
        Assert.Equal(1, bytes.Length - System.Text.Encoding.UTF8.GetByteCount("Größe ✓"));
        fixed (byte* text = bytes)
        {
            Assert.Equal("Größe ✓", Utf8.FromPointer(text));
        }
    }

    [Fact]
    public void EmptyStringIsJustTheTerminator()
    {
        Assert.Equal([(byte)0], Utf8.ToZeroTerminated(""));
    }

    [Fact]
    public void NullPointerIsNullString()
    {
        Assert.Null(Utf8.FromPointer(null));
    }
}

public unsafe class Utf8ArrayTests
{
    [Fact]
    public void HoldsTheStringsAsCStrings()
    {
        using var array = new Utf8Array(["VK_KHR_surface", "VK_EXT_debug_utils"]);

        Assert.Equal(2, array.Count);
        Assert.Equal("VK_KHR_surface", Utf8.FromPointer(array.Items[0]));
        Assert.Equal("VK_EXT_debug_utils", Utf8.FromPointer(array.Items[1]));
    }

    [Fact]
    public void AnEmptyListGivesAValidPointer()
    {
        using var array = new Utf8Array([]);

        Assert.Equal(0, array.Count);
        Assert.True(array.Items != null);
    }

    [Fact]
    public void RefusesUseAfterDispose()
    {
        var array = new Utf8Array(["x"]);
        array.Dispose();
        array.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = array.Items[0]);
    }
}
