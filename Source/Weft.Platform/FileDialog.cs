using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Weft.Core.Logging;
using Weft.Interop;
using Weft.Native.Sdl;

namespace Weft.Platform;

/// <summary>
/// What a native file dialog ended with. A chosen file or folder is in <see cref="Paths"/> (operating system paths, one or several); a dialog that was closed without a choice is
/// <see cref="Cancelled"/>; a dialog that could not be shown (no dialog backend on this machine, a failure) has an <see cref="Error"/> with SDL's reason.
/// </summary>
public sealed record FileDialogResult(IReadOnlyList<string> Paths, bool Cancelled, string? Error)
{
    /// <summary>The dialog was closed without choosing anything.</summary>
    public static FileDialogResult Cancel { get; } = new([], true, null);

    /// <summary>A dialog that failed with <paramref name="error"/>.</summary>
    public static FileDialogResult Failed(string error) => new([], false, error);

    /// <summary>The user chose at least one path.</summary>
    public bool HasPaths => Error is null && !Cancelled && Paths.Count > 0;
}

/// <summary>
/// SDL's native file dialogs (open file, save file, open folder), asynchronous. SDL shows the dialog and later reports the result on a callback that it may run on <b>another thread</b> (Windows, the
/// Linux portal and zenity backends) or before the call returns, so the result is never handed to the caller there: the callback only copies it into a queue, and <see cref="Dispatch"/> (called by
/// <see cref="PlatformHost.PumpEvents"/>, on the main thread) runs each request's continuation.
/// <para>
/// <b>What is shared between threads</b>: only <c>s_completed</c>, a <see cref="ConcurrentQueue{T}"/> that the callback enqueues into (any thread) and <see cref="Dispatch"/> drains (main thread).
/// Everything else (<c>s_pending</c>, <c>s_nextId</c>, the request memory) is touched by the main thread alone. The callback receives a request id as its userdata (never a pointer or a GCHandle),
/// so it cannot reach managed objects of the requests, and a result whose id is unknown (the platform was shut down meanwhile) is dropped.
/// </para>
/// <para>
/// <b>Memory</b>: SDL keeps the <c>filters</c> table, the filter strings and the default location, and requires them to stay valid until it has called back. Each request therefore gets one unmanaged block
/// (<see cref="FilterBlock"/>) that is freed only when its result is dispatched (a dialog still open at <see cref="PlatformHost.Shutdown"/> keeps its block: see <c>Reset</c>). The list of paths in the callback belongs to SDL and is freed when the callback
/// returns, so the callback copies every string into managed memory before it returns.
/// </para>
/// </summary>
public static unsafe class FileDialog
{
    private const string LogCategory = "Platform";

    private sealed class Request(Action<FileDialogResult> done, FilterBlock block)
    {
        public Action<FileDialogResult> Done { get; } = done;

        public FilterBlock Block { get; } = block;
    }

    // SHARED between threads: written by SDL's callback thread(s), drained by the main thread. The only thread-safe member of this class.
    private static readonly ConcurrentQueue<(int Id, FileDialogResult Result)> s_completed = new();

    // Main thread only.
    private static readonly Dictionary<int, Request> s_pending = [];
    private static int s_nextId;

    /// <summary>The number of dialogs that were opened and whose result was not dispatched yet.</summary>
    public static int PendingCount => s_pending.Count;

    /// <summary>
    /// Asks the user to choose a file to open. <paramref name="filters"/> are (name, pattern) pairs such as <c>("Scenes", "ascene")</c> (several extensions separated by <c>;</c>, or <c>*</c> for all files); the platform
    /// may let the user ignore them. <paramref name="defaultLocation"/> is the folder or file the dialog starts at. <paramref name="done"/> runs once, on the main thread, from <see cref="Dispatch"/>. Returns the request's id.
    /// </summary>
    public static int OpenFile(Window? window, IReadOnlyList<(string Name, string Pattern)> filters, string? defaultLocation, bool many, Action<FileDialogResult> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        FilterBlock block = FilterBlock.Create(filters, defaultLocation);
        int id = Begin(done, block);
        try
        {
            SDL.ShowOpenFileDialog(&OnResult, (void*)(nint)id, window is null ? null : window.NativeHandle, block.Table, block.Count, block.Location, many);
        }
        catch
        {
            Abandon(id);
            throw;
        }

        return id;
    }

    /// <summary>Asks the user where to save a file; the same arguments as <see cref="OpenFile"/> without choosing several. The result has one path.</summary>
    public static int SaveFile(Window? window, IReadOnlyList<(string Name, string Pattern)> filters, string? defaultLocation, Action<FileDialogResult> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        FilterBlock block = FilterBlock.Create(filters, defaultLocation);
        int id = Begin(done, block);
        try
        {
            SDL.ShowSaveFileDialog(&OnResult, (void*)(nint)id, window is null ? null : window.NativeHandle, block.Table, block.Count, block.Location);
        }
        catch
        {
            Abandon(id);
            throw;
        }

        return id;
    }

    /// <summary>Asks the user to choose a folder (several with <paramref name="many"/>).</summary>
    public static int OpenFolder(Window? window, string? defaultLocation, bool many, Action<FileDialogResult> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        FilterBlock block = FilterBlock.Create([], defaultLocation);
        int id = Begin(done, block);
        try
        {
            SDL.ShowOpenFolderDialog(&OnResult, (void*)(nint)id, window is null ? null : window.NativeHandle, block.Location, many);
        }
        catch
        {
            Abandon(id);
            throw;
        }

        return id;
    }

    /// <summary>
    /// Runs the continuations of the dialogs that have ended, on the calling thread (the main one): <see cref="PlatformHost.PumpEvents"/> calls it. A continuation that throws is logged and the others still run.
    /// </summary>
    public static void Dispatch()
    {
        while (s_completed.TryDequeue(out (int Id, FileDialogResult Result) item))
        {
            if (!s_pending.Remove(item.Id, out Request? request))
            {
                continue; // The platform was shut down while the dialog was open: nobody is waiting for it.
            }

            request.Block.Free();
            try
            {
                request.Done(item.Result);
            }
#pragma warning disable CA1031 // The continuation is application code; one that fails must not stop the next from running.
            catch (Exception e)
#pragma warning restore CA1031
            {
                Log.Error(LogCategory, $"A file dialog's continuation failed: {e}");
            }
        }
    }

    /// <summary>
    /// Forgets every open dialog: <see cref="PlatformHost.Shutdown"/> calls it. A callback that still arrives is dropped by <see cref="Dispatch"/> (ids are never reused). The memory of a dialog that is
    /// <b>still open</b> is deliberately <b>not</b> freed: SDL may not have read its filters yet (a dialog thread that has not started, say), and the rule is that they stay valid until the callback has run.
    /// What leaks is a few hundred bytes, once, at exit.
    /// </summary>
    internal static void Reset()
    {
        s_pending.Clear();
        while (s_completed.TryDequeue(out _))
        {
        }
    }

    /// <summary>Registers a request and returns its id. The id goes to SDL as the callback's userdata.</summary>
    internal static int Begin(Action<FileDialogResult> done, FilterBlock block)
    {
        int id = ++s_nextId;
        s_pending.Add(id, new Request(done, block));
        return id;
    }

    private static void Abandon(int id)
    {
        if (s_pending.Remove(id, out Request? request))
        {
            request.Block.Free();
        }
    }

    /// <summary>The function pointer SDL calls; tests call it directly to stand in for SDL.</summary>
    internal static delegate* unmanaged[Cdecl]<void*, byte**, int, void> CallbackPointer => &OnResult;

    /// <summary>
    /// SDL's callback, on whichever thread SDL chooses. It does nothing but turn the result into a <see cref="FileDialogResult"/> (copying the strings: SDL frees <paramref name="list"/> when we return) and
    /// enqueue it with the request id. No exception may leave an unmanaged callback (the process would die), so everything is caught and becomes an error result.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResult(void* userdata, byte** list, int filter)
    {
        _ = filter; // The index of the filter the user chose; nobody needs it.
        int id = (int)(nint)userdata;
#pragma warning disable CA1031 // Nothing may unwind through an unmanaged callback.
        try
        {
            FileDialogResult result;
            try
            {
                result = Read(list);
            }
            catch (Exception e)
            {
                result = FileDialogResult.Failed("The file dialog's result could not be read: " + e.Message);
            }

            s_completed.Enqueue((id, result));
        }
        catch (Exception)
        {
            // Out of memory while enqueueing: the request stays pending, which is no worse than a dialog that never ends.
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// The pure part of the callback: SDL's result list into a result. <c>null</c> is a failure (SDL's error text, set on the calling thread), a list whose first entry is null is a cancel, otherwise the list is
    /// null-terminated and every path is copied.
    /// </summary>
    internal static FileDialogResult Read(byte** list)
    {
        if (list is null)
        {
            string? error = Utf8.FromPointer(SDL.GetError());
            return FileDialogResult.Failed(string.IsNullOrEmpty(error) ? "The file dialog failed." : error);
        }

        if (list[0] is null)
        {
            return FileDialogResult.Cancel;
        }

        var paths = new List<string>();
        for (int i = 0; list[i] is not null; i++)
        {
            paths.Add(Utf8.FromPointer(list[i]) ?? "");
        }

        return new FileDialogResult(paths, false, null);
    }

    /// <summary>
    /// The unmanaged memory of one request: a table of <see cref="DialogFileFilter"/>, then the UTF-8 strings it points at (names, patterns), then the default location, all in one allocation that
    /// lives from the call to SDL until the result is dispatched.
    /// </summary>
    internal readonly struct FilterBlock
    {
        private readonly nint _memory;

        private FilterBlock(nint memory, DialogFileFilter* table, int count, byte* location)
        {
            _memory = memory;
            Table = table;
            Count = count;
            Location = location;
        }

        /// <summary>The filters as SDL wants them, or null with no filters.</summary>
        public DialogFileFilter* Table { get; }

        public int Count { get; }

        /// <summary>The zero-terminated default location, or null.</summary>
        public byte* Location { get; }

        public static FilterBlock Create(IReadOnlyList<(string Name, string Pattern)> filters, string? defaultLocation)
        {
            ArgumentNullException.ThrowIfNull(filters);
            int bytes = filters.Count * sizeof(DialogFileFilter);
            foreach ((string name, string pattern) in filters)
            {
                bytes += SizeOf(name) + SizeOf(pattern);
            }

            if (defaultLocation is not null)
            {
                bytes += SizeOf(defaultLocation);
            }

            // The table first, so that the pointers in it are aligned (the allocation is).
            byte* memory = (byte*)NativeMemory.Alloc((nuint)Math.Max(bytes, 1));
            var table = (DialogFileFilter*)memory;
            byte* next = memory + filters.Count * sizeof(DialogFileFilter);
            for (int i = 0; i < filters.Count; i++)
            {
                table[i].Name = Put(filters[i].Name, ref next);
                table[i].Pattern = Put(filters[i].Pattern, ref next);
            }

            byte* location = defaultLocation is null ? null : Put(defaultLocation, ref next);
            return new FilterBlock((nint)memory, filters.Count == 0 ? null : table, filters.Count, location);
        }

        public void Free()
        {
            if (_memory != 0)
            {
                NativeMemory.Free((void*)_memory);
            }
        }

        private static int SizeOf(string text)
        {
            if (text.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("A file dialog string cannot contain a NUL character.", nameof(text));
            }

            return Encoding.UTF8.GetByteCount(text) + 1;
        }

        private static byte* Put(string text, ref byte* next)
        {
            byte* start = next;
            int length = Encoding.UTF8.GetBytes(text, new Span<byte>(start, Encoding.UTF8.GetByteCount(text)));
            start[length] = 0;
            next = start + length + 1;
            return start;
        }
    }
}
