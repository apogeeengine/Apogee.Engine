using System;
using System.Collections.Generic;
using Weft.Interop;
using Weft.Native.Sdl;

namespace Weft.Platform;

/// <summary>
/// Files (and folders) the user dragged from the operating system onto a window and let go: one drag-and-drop, however many files it carried. Raised by <see cref="PlatformHost.FilesDropped"/> on the
/// main thread; the strings are copies, so they stay valid after the event.
/// </summary>
/// <param name="WindowId">The window they were dropped on (<see cref="Window.Id"/>).</param>
/// <param name="Paths">Operating system paths, in the order the system gave them; at least one.</param>
/// <param name="X">Where in the window the drop happened, in window points (the unit ImGui and mouse events use). Meaningless when <paramref name="HasPosition"/> is false.</param>
/// <param name="Y">See <paramref name="X"/>.</param>
/// <param name="HasPosition">The system told where the drop happened. Some backends (Wayland, depending on the compositor) do not: the position is then not a guess to act on.</param>
public sealed record FileDrop(uint WindowId, IReadOnlyList<string> Paths, float X, float Y, bool HasPosition);

/// <summary>
/// Turns SDL's drop events into <see cref="FileDrop"/>s. A drop arrives as <c>DropBegin</c>, any number of <c>DropPosition</c> (while dragging over the window), one <c>DropFile</c> per file, then
/// <c>DropComplete</c>; only the complete event ends a batch, but a complete that never comes is closed by the next begin, or by <see cref="CloseStale"/>. <c>DropText</c> is ignored. No SDL calls here:
/// the main thread feeds it events (the data pointer of an event belongs to SDL and is copied here), so tests feed it synthetic ones.
/// </summary>
internal sealed unsafe class DropCollector(TimeProvider? clock = null)
{
    /// <summary>How long a batch may stay open with no complete event before <see cref="CloseStale"/> delivers it.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly List<string> _paths = [];
    private bool _open;
    private uint _window;
    private long _started;
    private (float X, float Y)? _fileAt;

    /// <summary>Where the pointer is while something is dragged over the window, or null. Cleared when the drag ends.</summary>
    public (float X, float Y)? Hover { get; private set; }

    /// <summary>Takes one event. Returns the batch it ended (a complete event, or a begin that found the previous batch still open), or null.</summary>
    public FileDrop? Handle(in Event sdlEvent)
    {
        switch ((EventType)sdlEvent.Type)
        {
            case EventType.DropBegin:
                FileDrop? previous = Close();
                Hover = null;
                Open(sdlEvent.Drop.WindowID);
                return previous;

            case EventType.DropPosition:
                Hover = (sdlEvent.Drop.X, sdlEvent.Drop.Y);
                return null;

            case EventType.DropFile:
                if (!_open)
                {
                    Open(sdlEvent.Drop.WindowID); // a backend that sends no begin: this file starts the batch
                }

                if (Utf8.FromPointer(sdlEvent.Drop.Data) is { Length: > 0 } path)
                {
                    if (_paths.Count == 0)
                    {
                        _started = _clock.GetTimestamp(); // SDL3 begins a drop when the drag enters the window: how long the hover lasted is not how long a batch has been waiting
                    }

                    _paths.Add(path);
                    if (sdlEvent.Drop.X != 0 || sdlEvent.Drop.Y != 0)
                    {
                        _fileAt = (sdlEvent.Drop.X, sdlEvent.Drop.Y);
                    }
                }

                return null;

            case EventType.DropComplete:
                FileDrop? done = Close();
                Hover = null;
                return done;

            default:
                return null;
        }
    }

    /// <summary>Delivers a batch that has files but has been open longer than <see cref="StaleAfter"/> (its complete event was lost), or returns null.</summary>
    public FileDrop? CloseStale() => _open && _paths.Count > 0 && _clock.GetElapsedTime(_started) > StaleAfter ? Close() : null;

    /// <summary>Forgets a batch in progress and the hover (the platform shut down).</summary>
    public void Reset()
    {
        _open = false;
        _paths.Clear();
        Hover = null;
    }

    private void Open(uint window)
    {
        _open = true;
        _window = window;
        _started = _clock.GetTimestamp();
        _paths.Clear();
        _fileAt = null;
    }

    private FileDrop? Close()
    {
        if (!_open)
        {
            return null;
        }

        _open = false;
        if (_paths.Count == 0)
        {
            return null; // a drag that left the window, or dropped text
        }

        (float X, float Y)? at = Hover ?? _fileAt;
        var drop = new FileDrop(_window, [.. _paths], at?.X ?? 0f, at?.Y ?? 0f, at is not null);
        _paths.Clear();
        return drop;
    }
}
