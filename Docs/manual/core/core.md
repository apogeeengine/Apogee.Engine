# Core

`Apogee.Core`: the small things every other library uses.

## Logging

`Log.Info("Category", "message")` (also `Trace`, `Debug`, `Warn`, `Error`) writes a `LogEntry` to every registered
`ILogSink`. Sinks can be called from any thread. `Log.MinimumLevel` filters, `Log.IsEnabled(level)` avoids building a
message that would be dropped. `ConsoleSink` is the stock sink.

```csharp
Log.AddSink(new ConsoleSink());
Log.Info("Runtime", "started");
```

## Arena

`Arena` is a fixed-size bump allocator over unmanaged memory. `Allocate<T>(count)` returns a zeroed `Span<T>` that stays
valid (the memory never moves) until `Reset(mark)`; take `Mark` before, reset to it after, to free a scope. It is not
thread safe: use one per thread or per job.

## NativeList

`NativeList<T>` is a growable list in unmanaged memory for `unmanaged` elements: `Add`, `RemoveAtSwapBack`, a `ref`
indexer and `AsSpan()`. Dispose it.

## Jobs

`JobSystem` is a worker pool: `Schedule(group, job)` queues a job on a `JobGroup`, `group.Wait()` returns when all of its
jobs ran and rethrows the first failure. `ParallelFor(count, batchSize, body)` splits a range across the workers and
waits.
