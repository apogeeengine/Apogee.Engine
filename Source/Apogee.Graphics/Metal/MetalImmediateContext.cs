using System;

namespace Apogee.Graphics.Metal;

/// <summary>Records a command buffer, commits it and waits for it: for loading and tools. The calls are serialized.</summary>
public sealed class MetalImmediateContext(MetalDevice device) : ImmediateContext
{
    private readonly object _lock = new();

    public override void Submit(Action<CommandRecorder> record)
    {
        lock (_lock)
        {
            var recorder = new MetalCommandRecorder(device);
            record(recorder);
            recorder.Commit(wait: true);
        }
    }

    public override void Dispose()
    {
    }
}
