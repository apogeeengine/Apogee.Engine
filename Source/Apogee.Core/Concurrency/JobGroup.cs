using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Apogee.Core.Concurrency;

/// <summary>The jobs scheduled together on a <see cref="JobSystem"/>; <see cref="Wait"/> returns when all of them have run.</summary>
public sealed class JobGroup : IDisposable
{
    private readonly ManualResetEventSlim _done = new(initialState: true);
    private int _pending;
    private ExceptionDispatchInfo? _failure;

    internal void Add()
    {
        if (Interlocked.Increment(ref _pending) == 1)
        {
            _done.Reset();
        }
    }

    internal void Complete(Exception? failure)
    {
        if (failure is not null)
        {
            Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(failure), null);
        }

        if (Interlocked.Decrement(ref _pending) == 0)
        {
            _done.Set();
        }
    }

    /// <summary>Blocks until every job has run, then rethrows the first exception a job threw.</summary>
    public void Wait()
    {
        _done.Wait();
        _failure?.Throw();
    }

    public void Dispose() => _done.Dispose();
}
