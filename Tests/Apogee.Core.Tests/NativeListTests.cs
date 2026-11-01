using System;
using Apogee.Core.Memory;
using Xunit;

namespace Apogee.Core.Tests;

public class NativeListTests
{
    [Fact]
    public void GrowsPastItsCapacityAndKeepsItems()
    {
        using var list = new NativeList<int>(2);

        for (int i = 0; i < 1000; i++)
        {
            list.Add(i);
        }

        Assert.Equal(1000, list.Count);
        Assert.Equal(999, list[999]);
        Assert.Equal(499500, Sum(list.AsSpan()));
    }

    [Fact]
    public void RemoveAtSwapBackMovesTheLastItemIn()
    {
        using var list = new NativeList<int>();
        foreach (int i in new[] { 10, 20, 30, 40 })
        {
            list.Add(i);
        }

        list.RemoveAtSwapBack(1);

        Assert.Equal([10, 40, 30], list.AsSpan().ToArray());
    }

    [Fact]
    public void IndexerRejectsOutOfRange()
    {
        using var list = new NativeList<int>();
        list.Add(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => list[1]);
    }

    private static long Sum(ReadOnlySpan<int> values)
    {
        long sum = 0;
        foreach (int v in values)
        {
            sum += v;
        }

        return sum;
    }
}
