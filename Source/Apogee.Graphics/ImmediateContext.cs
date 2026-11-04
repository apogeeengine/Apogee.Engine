using System;

namespace Apogee.Graphics;

/// <summary>
/// Records a command buffer, submits it and waits for it: for loading and tools, where stalling is fine. A frame never uses this.
/// Safe to call from several threads (the calls are serialized).
/// </summary>
public abstract class ImmediateContext : IDisposable
{
    /// <summary>Runs <paramref name="record"/> on a command buffer and returns when the GPU has finished it.</summary>
    public abstract void Submit(Action<CommandRecorder> record);

    public abstract void Dispose();
}
