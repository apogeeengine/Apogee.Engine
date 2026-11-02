using System;
using System.Threading;
using Apogee.Core.Logging;

namespace Apogee.Reflection;

/// <summary>A connection to a signal; <c>default</c> is not connected.</summary>
public readonly struct Connection
{
    private readonly SignalBase? _signal;
    private readonly int _id;

    internal Connection(SignalBase signal, int id)
    {
        _signal = signal;
        _id = id;
    }

    public bool IsConnected => _signal?.Has(_id) ?? false;

    /// <summary>Stops the handler being called; does nothing when it is not connected any more.</summary>
    public void Disconnect() => _signal?.Remove(_id);
}

public abstract class SignalBase
{
    internal abstract bool Has(int id);

    internal abstract void Remove(int id);
}

/// <summary>
/// The handlers of a signal. Emitting calls a snapshot of them, so a handler may connect or disconnect (itself too) while it runs.
/// An exception from a handler is logged and the others still run: the emitter never sees it.
/// </summary>
public abstract class SignalBase<THandler> : SignalBase
    where THandler : Delegate
{
    protected readonly record struct Entry(int Id, THandler Handler);

    private readonly Lock _lock = new();
    private Entry[] _entries = [];
    private int _nextId;

    protected ReadOnlySpan<Entry> Entries => Volatile.Read(ref _entries);

    public int Count => Volatile.Read(ref _entries).Length;

    public Connection Connect(THandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            int id = ++_nextId;
            Entry[] entries = new Entry[_entries.Length + 1];
            _entries.CopyTo(entries, 0);
            entries[^1] = new Entry(id, handler);
            Volatile.Write(ref _entries, entries);
            return new Connection(this, id);
        }
    }

    internal override bool Has(int id)
    {
        foreach (Entry entry in Entries)
        {
            if (entry.Id == id)
            {
                return true;
            }
        }

        return false;
    }

    internal override void Remove(int id)
    {
        lock (_lock)
        {
            int index = Array.FindIndex(_entries, e => e.Id == id);
            if (index < 0)
            {
                return;
            }

            Entry[] entries = new Entry[_entries.Length - 1];
            Array.Copy(_entries, 0, entries, 0, index);
            Array.Copy(_entries, index + 1, entries, index, entries.Length - index);
            Volatile.Write(ref _entries, entries);
        }
    }

    protected static void Failed(Exception exception) => Log.Error("Signal", $"A signal handler failed: {exception}");
}

public sealed class Signal : SignalBase<Action>
{
    public void Emit()
    {
        foreach (Entry entry in Entries)
        {
            try
            {
                entry.Handler();
            }
            catch (Exception e)
            {
                Failed(e);
            }
        }
    }
}

public sealed class Signal<T1> : SignalBase<Action<T1>>
{
    public void Emit(T1 a)
    {
        foreach (Entry entry in Entries)
        {
            try
            {
                entry.Handler(a);
            }
            catch (Exception e)
            {
                Failed(e);
            }
        }
    }
}

public sealed class Signal<T1, T2> : SignalBase<Action<T1, T2>>
{
    public void Emit(T1 a, T2 b)
    {
        foreach (Entry entry in Entries)
        {
            try
            {
                entry.Handler(a, b);
            }
            catch (Exception e)
            {
                Failed(e);
            }
        }
    }
}

public sealed class Signal<T1, T2, T3> : SignalBase<Action<T1, T2, T3>>
{
    public void Emit(T1 a, T2 b, T3 c)
    {
        foreach (Entry entry in Entries)
        {
            try
            {
                entry.Handler(a, b, c);
            }
            catch (Exception e)
            {
                Failed(e);
            }
        }
    }
}

public sealed class Signal<T1, T2, T3, T4> : SignalBase<Action<T1, T2, T3, T4>>
{
    public void Emit(T1 a, T2 b, T3 c, T4 d)
    {
        foreach (Entry entry in Entries)
        {
            try
            {
                entry.Handler(a, b, c, d);
            }
            catch (Exception e)
            {
                Failed(e);
            }
        }
    }
}
