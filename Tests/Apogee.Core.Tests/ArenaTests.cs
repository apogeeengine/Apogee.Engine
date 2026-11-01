using System;
using System.Runtime.CompilerServices;
using Apogee.Core.Memory;
using Xunit;

namespace Apogee.Core.Tests;

public class ArenaTests
{
    [Fact]
    public void AllocationsAreZeroedAndDoNotOverlap()
    {
        using var arena = new Arena(256);

        Span<int> a = arena.Allocate<int>(4);
        Span<long> b = arena.Allocate<long>(2);
        a.Fill(-1);
        b.Fill(-1);

        Assert.Equal(4, a.Length);
        Assert.Equal(2, b.Length);
        Assert.All(a.ToArray(), v => Assert.Equal(-1, v));
        Assert.True(arena.Used >= 4 * sizeof(int) + 2 * sizeof(long));
    }

    [Fact]
    public unsafe void AlignsToTheTypeSize()
    {
        using var arena = new Arena(256);

        arena.Allocate<byte>(1);
        Span<long> aligned = arena.Allocate<long>(1);

        Assert.Equal(0, (long)Unsafe.AsPointer(ref aligned[0]) % sizeof(long));
    }

    [Fact]
    public void ResetFreesEverythingAfterTheMarkAndZeroesOnReuse()
    {
        using var arena = new Arena(64);
        arena.Allocate<int>(2);
        nuint mark = arena.Mark;
        arena.Allocate<int>(4).Fill(7);

        arena.Reset(mark);

        Assert.Equal(mark, arena.Used);
        Assert.All(arena.Allocate<int>(4).ToArray(), v => Assert.Equal(0, v));
    }

    [Fact]
    public void ThrowsWhenFull()
    {
        using var arena = new Arena(16);

        Assert.Throws<InvalidOperationException>(() => arena.Allocate<long>(3));
    }
}
