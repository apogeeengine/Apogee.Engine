using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Apogee.Native.Sdl;
using Xunit;

namespace Apogee.Platform.Tests;

/// <summary>
/// A real drop needs a person and a display, so these feed the collector the events SDL would send (the strings in memory the test owns and frees, as SDL frees its own after the event) and check what the
/// platform makes of them. They share the platform's process-wide state with the other platform tests, hence the collection.
/// </summary>
[Collection("sdl")]
public sealed unsafe class FileDropTests
{
    private static Event Make(EventType type, uint window = 1, float x = 0, float y = 0, byte* data = null)
    {
        Event e = default;
        e.Drop.Type = type;
        e.Drop.WindowID = window;
        e.Drop.X = x;
        e.Drop.Y = y;
        e.Drop.Data = data;
        return e;
    }

    /// <summary>Feeds a file event with <paramref name="path"/> held in unmanaged memory that is freed right after, like SDL's.</summary>
    private static FileDrop? File(DropCollector collector, string path, uint window = 1, float x = 0, float y = 0)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(path + "\0");
        byte* memory = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(memory, bytes.Length));
        try
        {
            Event e = Make(EventType.DropFile, window, x, y, memory);
            return collector.Handle(in e);
        }
        finally
        {
            NativeMemory.Free(memory);
        }
    }

    private static FileDrop? Feed(DropCollector collector, EventType type, uint window = 1, float x = 0, float y = 0)
    {
        Event e = Make(type, window, x, y);
        return collector.Handle(in e);
    }

    [Fact]
    public void DropEventsBecomeOneFileDropWithCopiedPaths()
    {
        var collector = new DropCollector();
        Assert.Null(Feed(collector, EventType.DropBegin, window: 7));
        Assert.Null(Feed(collector, EventType.DropPosition, window: 7, x: 120, y: 80));
        Assert.Null(Feed(collector, EventType.DropPosition, window: 7, x: 130, y: 90));
        Assert.Equal((130f, 90f), collector.Hover);
        Assert.Null(File(collector, "/home/me/models/car.glb", 7, 130, 90));
        Assert.Null(File(collector, "/home/me/pictures/été 2.png", 7, 130, 90));

        FileDrop? drop = Feed(collector, EventType.DropComplete, window: 7);

        Assert.NotNull(drop);
        Assert.Equal(7u, drop.WindowId);
        Assert.Equal(["/home/me/models/car.glb", "/home/me/pictures/été 2.png"], drop.Paths); // copied: the memory was freed after each event
        Assert.True(drop.HasPosition);
        Assert.Equal((130f, 90f), (drop.X, drop.Y));
        Assert.Null(collector.Hover);                                                          // the drag is over
        Assert.Null(Feed(collector, EventType.DropComplete));                                  // nothing is left to deliver twice
    }

    [Fact]
    public void ABackendWithoutAPositionSaysSo()
    {
        var collector = new DropCollector();
        _ = Feed(collector, EventType.DropBegin);
        _ = File(collector, "/a/b.png"); // x and y are zero: the backend gave none
        FileDrop? drop = Feed(collector, EventType.DropComplete);

        Assert.NotNull(drop);
        Assert.False(drop.HasPosition);
    }

    [Fact]
    public void ALostCompleteIsClosedByTheNextBeginAndTextAndEmptyDragsDeliverNothing()
    {
        var collector = new DropCollector();
        _ = Feed(collector, EventType.DropBegin, window: 3);
        _ = File(collector, "/first.png", 3, 5, 5);
        FileDrop? closed = Feed(collector, EventType.DropBegin, window: 4); // the complete of the first never came

        Assert.NotNull(closed);
        Assert.Equal(3u, closed.WindowId);
        Assert.Equal(["/first.png"], closed.Paths);

        _ = File(collector, "/second.png", 4, 9, 9);
        FileDrop? second = Feed(collector, EventType.DropComplete, window: 4);
        Assert.Equal(["/second.png"], second!.Paths);                         // the first batch's files are not repeated

        _ = Feed(collector, EventType.DropBegin);
        Assert.Null(Feed(collector, EventType.DropComplete));                 // a drag that left the window, or dropped text
        Assert.Null(Feed(collector, EventType.DropText));                     // text is ignored
    }

    [Fact]
    public void ABatchWhoseCompleteNeverComesIsDeliveredWhenItIsStale()
    {
        var clock = new ManualClock();
        var collector = new DropCollector(clock);
        _ = Feed(collector, EventType.DropBegin);
        _ = File(collector, "/lonely.png", 1, 4, 4);

        Assert.Null(collector.CloseStale());
        clock.Advance(DropCollector.StaleAfter + TimeSpan.FromSeconds(1));
        FileDrop? drop = collector.CloseStale();

        Assert.Equal(["/lonely.png"], drop!.Paths);
        Assert.Null(collector.CloseStale());
    }

    [Fact]
    public void ThePlatformRaisesFilesDroppedAndSurvivesAHandlerThatThrows()
    {
        var seen = new List<FileDrop>();
        void Bad(FileDrop _) => throw new InvalidOperationException("a handler bug");
        void Good(FileDrop drop) => seen.Add(drop);
        PlatformHost.FilesDropped += Bad;
        PlatformHost.FilesDropped += Good;
        try
        {
            Event begin = Make(EventType.DropBegin);
            PlatformHost.HandleDropEvent(in begin);
            byte[] bytes = Encoding.UTF8.GetBytes("/x/y.glb\0");
            fixed (byte* path = bytes)
            {
                Event file = Make(EventType.DropFile, x: 3, y: 4, data: path);
                PlatformHost.HandleDropEvent(in file);
            }

            Event complete = Make(EventType.DropComplete);
            PlatformHost.HandleDropEvent(in complete);
        }
        finally
        {
            PlatformHost.FilesDropped -= Bad;
            PlatformHost.FilesDropped -= Good;
        }

        // The handler that throws is logged and the ones after it still get the batch.
        Assert.Single(seen);
        Assert.Equal(["/x/y.glb"], seen[0].Paths);
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
