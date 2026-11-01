using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Apogee.Core.Concurrency;

/// <summary>A fixed pool of worker threads that runs jobs in the order they were scheduled.</summary>
public sealed class JobSystem : IDisposable
{
    private readonly BlockingCollection<(Action Job, JobGroup Group)> _queue = [];
    private readonly Thread[] _workers;

    /// <param name="workerCount">Number of threads; by default one fewer than the logical processors (the caller's thread is the other).</param>
    public JobSystem(int workerCount = 0)
    {
        if (workerCount <= 0)
        {
            workerCount = Math.Max(1, Environment.ProcessorCount - 1);
        }

        _workers = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _workers[i] = new Thread(Work) { Name = $"Apogee job worker {i}", IsBackground = true };
            _workers[i].Start();
        }
    }

    public int WorkerCount => _workers.Length;

    public void Schedule(JobGroup group, Action job)
    {
        group.Add();
        _queue.Add((job, group));
    }

    /// <summary>Runs <paramref name="body"/> over [0, <paramref name="count"/>) in batches, and returns when all of it has run.</summary>
    /// <param name="body">Receives the start (inclusive) and end (exclusive) of one batch.</param>
    public void ParallelFor(int count, int batchSize, Action<int, int> body)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        using var group = new JobGroup();
        for (int start = 0; start < count; start += batchSize)
        {
            int begin = start;
            int end = Math.Min(count, start + batchSize);
            Schedule(group, () => body(begin, end));
        }

        group.Wait();
    }

    private void Work()
    {
        foreach ((Action job, JobGroup group) in _queue.GetConsumingEnumerable())
        {
            Exception? failure = null;
            try
            {
                job();
            }
            catch (Exception e)
            {
                failure = e;
            }

            group.Complete(failure);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        foreach (Thread worker in _workers)
        {
            worker.Join();
        }

        _queue.Dispose();
    }
}
