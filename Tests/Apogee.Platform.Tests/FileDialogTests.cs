using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apogee.Interop;
using Apogee.Native.Tests;
using Xunit;
using SDL = Apogee.Native.Sdl.SDL;

namespace Apogee.Platform.Tests;

/// <summary>
/// The native dialogs themselves need a display and a person, so these test everything around them: reading SDL's result list, the callback that stands in for SDL (called from another thread), the
/// queue that only <see cref="FileDialog.Dispatch"/> drains, and the request memory. They share SDL's process-wide state with <see cref="PlatformTests"/>, hence the collection.
/// </summary>
[Collection("sdl")]
public sealed unsafe class FileDialogTests
{
    public FileDialogTests() => NativeRequirements.Require(typeof(SDL).Assembly, "SDL3");

    /// <summary>A null-terminated list of C strings as SDL builds one; free with <see cref="Free"/>.</summary>
    private static byte** MakeList(params string[] paths)
    {
        byte** list = (byte**)NativeMemory.Alloc((nuint)((paths.Length + 1) * sizeof(byte*)));
        for (int i = 0; i < paths.Length; i++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(paths[i] + "\0");
            list[i] = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>(list[i], bytes.Length));
        }

        list[paths.Length] = null;
        return list;
    }

    private static void Free(byte** list)
    {
        for (int i = 0; list[i] is not null; i++)
        {
            NativeMemory.Free(list[i]);
        }

        NativeMemory.Free(list);
    }

    [Fact]
    public void AResultListIsCopied()
    {
        byte** list = MakeList("/home/me/scenes/level.ascene", "/home/me/scénes/niveau 2.ascene");
        FileDialogResult result = FileDialog.Read(list);
        Free(list); // SDL frees its list when the callback returns: the copy must not depend on it.

        Assert.True(result.HasPaths);
        Assert.False(result.Cancelled);
        Assert.Null(result.Error);
        Assert.Equal(["/home/me/scenes/level.ascene", "/home/me/scénes/niveau 2.ascene"], result.Paths);
    }

    [Fact]
    public void ANullListIsAnErrorAndAnEmptyOneACancel()
    {
        SDL.ClearError();
        Assert.Equal(0u, SDL.GetWindowID(null)); // a call that fails, so that SDL has an error text
        string? sdlError = Utf8.FromPointer(SDL.GetError());
        Assert.False(string.IsNullOrEmpty(sdlError));

        FileDialogResult failed = FileDialog.Read(null);
        Assert.Equal(sdlError, failed.Error);
        Assert.False(failed.Cancelled);
        Assert.False(failed.HasPaths);

        SDL.ClearError(); // no text at all: still an error, with a message
        Assert.False(string.IsNullOrEmpty(FileDialog.Read(null).Error));

        byte** empty = MakeList();
        FileDialogResult cancelled = FileDialog.Read(empty);
        Free(empty);
        Assert.True(cancelled.Cancelled);
        Assert.Null(cancelled.Error);
        Assert.Empty(cancelled.Paths);
        Assert.False(cancelled.HasPaths);
    }

    [Fact]
    public void ResultsAreDispatchedOnlyByDispatch()
    {
        FileDialog.Dispatch(); // nothing is left from other tests
        var seen = new List<(string[] Paths, string? Error, int Thread)>();
        int first = FileDialog.Begin(r => seen.Add(([.. r.Paths], r.Error, Environment.CurrentManagedThreadId)), FileDialog.FilterBlock.Create([], null));
        int second = FileDialog.Begin(r => seen.Add(([.. r.Paths], r.Error, Environment.CurrentManagedThreadId)), FileDialog.FilterBlock.Create([], null));
        Assert.Equal(2, FileDialog.PendingCount);

        // SDL calls back on a thread of its own, and the list is freed as soon as the callback returns.
        int callbackThread = 0;
        var thread = new Thread(() =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            byte** one = MakeList("/a/one");
            FileDialog.CallbackPointer((void*)(nint)first, one, 0);
            Free(one);
            FileDialog.CallbackPointer((void*)(nint)second, null, -1);
            byte** stale = MakeList("/stale");
            FileDialog.CallbackPointer((void*)(nint)9999, stale, 0); // an id nobody waits for
            Free(stale);
        });
        thread.Start();
        thread.Join();

        Assert.Empty(seen); // the callback alone runs nobody's code
        Assert.Equal(2, FileDialog.PendingCount);

        FileDialog.Dispatch();
        Assert.Equal(2, seen.Count);
        Assert.Equal(["/a/one"], seen[0].Paths);
        Assert.Empty(seen[1].Paths); // the null list: an error
        Assert.NotNull(seen[1].Error);
        Assert.All(seen, s => Assert.Equal(Environment.CurrentManagedThreadId, s.Thread));
        Assert.NotEqual(callbackThread, Environment.CurrentManagedThreadId);
        Assert.Equal(0, FileDialog.PendingCount);

        FileDialog.Dispatch();
        Assert.Equal(2, seen.Count); // each result runs once, and the stale id ran nothing
    }

    [Fact]
    public void TwoCallbacksForTheSameRequestRunItOnce()
    {
        FileDialog.Dispatch();
        int runs = 0;
        string[]? paths = null;
        int id = FileDialog.Begin(r =>
        {
            runs++;
            paths = [.. r.Paths];
        }, FileDialog.FilterBlock.Create([], null));

        byte** first = MakeList("/first");
        byte** second = MakeList("/second");
        FileDialog.CallbackPointer((void*)(nint)id, first, 0);
        FileDialog.CallbackPointer((void*)(nint)id, second, 0); // a backend that reports twice
        Free(first);
        Free(second);

        FileDialog.Dispatch();
        Assert.Equal(1, runs);
        Assert.Equal(["/first"], paths!); // the first answer won, the second was dropped
        Assert.Equal(0, FileDialog.PendingCount);
    }

    [Fact]
    public void AFailingContinuationDoesNotStopTheOthers()
    {
        FileDialog.Dispatch();
        bool ran = false;
        int bad = FileDialog.Begin(_ => throw new InvalidOperationException("boom"), FileDialog.FilterBlock.Create([], null));
        int good = FileDialog.Begin(_ => ran = true, FileDialog.FilterBlock.Create([], null));
        byte** empty = MakeList();
        FileDialog.CallbackPointer((void*)(nint)bad, empty, 0);
        FileDialog.CallbackPointer((void*)(nint)good, empty, 0);
        Free(empty);

        FileDialog.Dispatch();
        Assert.True(ran);
        Assert.Equal(0, FileDialog.PendingCount);
    }

    [Fact]
    public void ManyCallbacksFromManyThreadsAreAllQueued()
    {
        FileDialog.Dispatch();
        int done = 0;
        var ids = new int[200];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = FileDialog.Begin(_ => Interlocked.Increment(ref done), FileDialog.FilterBlock.Create([], null));
        }

        Parallel.For(0, ids.Length, i =>
        {
            byte** list = MakeList("/p/" + i);
            FileDialog.CallbackPointer((void*)(nint)ids[i], list, 0);
            Free(list);
        });

        FileDialog.Dispatch();
        Assert.Equal(ids.Length, done);
    }

    [Fact]
    public void TheRequestMemoryHoldsTheFiltersAndTheLocation()
    {
        var filters = new List<(string, string)> { ("Scenes", "ascene"), ("Modèles", "glb;gltf"), ("All files", "*") };
        FileDialog.FilterBlock block = FileDialog.FilterBlock.Create(filters, "/home/me/jeu éphémère/scenes/");
        try
        {
            Assert.Equal(3, block.Count);
            Assert.Equal("Scenes", Utf8.FromPointer(block.Table[0].Name));
            Assert.Equal("ascene", Utf8.FromPointer(block.Table[0].Pattern));
            Assert.Equal("Modèles", Utf8.FromPointer(block.Table[1].Name));
            Assert.Equal("glb;gltf", Utf8.FromPointer(block.Table[1].Pattern));
            Assert.Equal("*", Utf8.FromPointer(block.Table[2].Pattern));
            Assert.Equal("/home/me/jeu éphémère/scenes/", Utf8.FromPointer(block.Location));
        }
        finally
        {
            block.Free();
        }

        FileDialog.FilterBlock none = FileDialog.FilterBlock.Create([], null);
        Assert.Equal(0, none.Count);
        Assert.True(none.Table is null);
        Assert.True(none.Location is null);
        none.Free();

        Assert.Throws<ArgumentException>(() => FileDialog.FilterBlock.Create([("Bad\0name", "x")], null));
    }

    [Fact]
    public void ShuttingDownForgetsOpenDialogs()
    {
        PlatformHost.Initialize("dummy");
        try
        {
            bool ran = false;
            int id = FileDialog.Begin(_ => ran = true, FileDialog.FilterBlock.Create([("A", "a")], "/x"));
            PlatformHost.Shutdown();
            Assert.Equal(0, FileDialog.PendingCount);

            byte** empty = MakeList();
            FileDialog.CallbackPointer((void*)(nint)id, empty, 0); // a late callback
            Free(empty);
            FileDialog.Dispatch();
            Assert.False(ran);
        }
        finally
        {
            PlatformHost.Shutdown();
        }
    }
}
