# Core

`Weft.Core`: the small things every other library uses.

## Logging

`Log.Info("Category", "message")` (also `Trace`, `Debug`, `Warn`, `Error`) writes a `LogEntry` to every registered
`ILogSink`. Sinks can be called from any thread. `Log.MinimumLevel` filters, `Log.IsEnabled(level)` avoids building a
message that would be dropped. The sinks that ship:

- `ConsoleSink`: stderr for warnings and errors, stdout for the rest.
- `FileLogSink`: one file per run in a folder that keeps the newest ten, flushed line by line.
- `RecentLogSink`: the last N lines in memory, which a crash report quotes.

`LogEntry.ToLine()` is the one line format they share: `HH:mm:ss.fff LVL [Category] message`.

```csharp
Log.AddSink(new ConsoleSink());
Log.Info("Runtime", "started");
```

The runtime and the editor write each run's log and, when an exception ends the program, a crash report: see [Logs and crash reports](crashes.md).

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
