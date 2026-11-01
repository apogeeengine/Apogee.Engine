using Apogee.Core.Collections;
using Xunit;

namespace Apogee.Core.Tests;

public class SlotTableTests
{
    [Fact]
    public void AnAddedItemIsFoundByItsHandle()
    {
        var table = new SlotTable<string>();
        SlotHandle a = table.Add("a");
        SlotHandle b = table.Add("b");

        Assert.True(table.TryGet(a, out string? foundA));
        Assert.Equal("a", foundA);
        Assert.True(table.TryGet(b, out string? foundB));
        Assert.Equal("b", foundB);
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void AStaleHandleStaysInvalidAfterItsSlotIsReused()
    {
        var table = new SlotTable<string>();
        SlotHandle old = table.Add("old");
        Assert.True(table.Remove(old));

        SlotHandle reused = table.Add("new");

        Assert.Equal(old.Index, reused.Index);
        Assert.NotEqual(old.Generation, reused.Generation);
        Assert.False(table.IsAlive(old));
        Assert.False(table.Remove(old));
        Assert.True(table.TryGet(reused, out string? item));
        Assert.Equal("new", item);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void TheDefaultHandleIsNeverValid()
    {
        var table = new SlotTable<string>();
        table.Add("x");

        Assert.True(default(SlotHandle).IsNull);
        Assert.False(table.IsAlive(default));
        Assert.False(table.TryGet(new SlotHandle(99, 1), out _));
    }

    [Fact]
    public void FreedSlotsAreReusedBeforeTheTableGrows()
    {
        var table = new SlotTable<object>();
        SlotHandle[] handles = new SlotHandle[100];
        for (int i = 0; i < handles.Length; i++)
        {
            handles[i] = table.Add(new object());
        }

        foreach (SlotHandle handle in handles)
        {
            Assert.True(table.Remove(handle));
        }

        Assert.Equal(0, table.Count);
        uint highest = 0;
        for (int i = 0; i < handles.Length; i++)
        {
            highest = System.Math.Max(highest, table.Add(new object()).Index);
        }

        Assert.Equal(99u, highest); // no new slots
    }
}
