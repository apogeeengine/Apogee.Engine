using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Apogee.Content.FileSystem;
using Apogee.Core.Logging;
using Apogee.Graphics;

namespace Apogee.Rendering.Shaders;

/// <summary>The pipelines one build made. Disposing destroys them all; they go through <see cref="Frame.Defer"/> when a rebuild replaces them.</summary>
public sealed class PipelineScope : IDisposable
{
    private readonly List<Pipeline> _pipelines = [];

    /// <summary>Takes ownership of <paramref name="pipeline"/> and returns it.</summary>
    public Pipeline Add(Pipeline pipeline)
    {
        lock (_pipelines)
        {
            _pipelines.Add(pipeline);
        }

        return pipeline;
    }

    public void Dispose()
    {
        lock (_pipelines)
        {
            foreach (Pipeline pipeline in _pipelines)
            {
                pipeline.Dispose();
            }

            _pipelines.Clear();
        }
    }
}

/// <summary>
/// The pipelines a renderer builds from some shader modules, rebuilt when one of them changes (or a module it imports) without stalling the frame. A change starts a build on another thread: the shaders are
/// compiled through the <see cref="ShaderLibrary"/> and the pipelines made, while the renderer carries on with <see cref="Current"/>. The renderer calls <see cref="Apply"/> at the start of a frame; if a
/// build has finished, the new pipelines become current there, and the old ones are destroyed through <see cref="Frame.Defer"/> once the GPU is done with the frames that used them. A build that fails
/// (a shader that does not compile) leaves <see cref="Current"/> as it was, is logged, and shows in <see cref="ShaderLibrary.Errors"/> until a later build works. Changes that arrive during a build
/// start another one right after it.
/// </summary>
/// <typeparam name="T">What the renderer uses: the pipelines, however it arranges them.</typeparam>
public sealed class ReloadablePipelines<T> : IDisposable
    where T : class
{
    private readonly ShaderLibrary _library;
    private readonly string _name;
    private readonly HashSet<string> _modules;
    private readonly Func<PipelineScope, T> _build;
    private readonly object _lock = new();
    private PipelineScope _scope;
    private Task? _task;
    private bool _running;
    private bool _again;
    private (T Value, PipelineScope Scope)? _ready;
    private bool _failed;
    private string? _failure;
    private bool _disposed;

    /// <param name="library">Where the modules are loaded from, and which says when they change.</param>
    /// <param name="name">Names the group in logs and in <see cref="ShaderLibrary.Errors"/>.</param>
    /// <param name="modules">The modules this group is built from (<c>shaders://forward.slang</c>); a change to one of them, or to a file it imports, rebuilds the group.</param>
    /// <param name="build">Makes the pipelines, adding each to the scope it is given as soon as it is made. It runs on the calling thread now, and on another one for rebuilds.</param>
    public ReloadablePipelines(ShaderLibrary library, string name, IReadOnlyList<string> modules, Func<PipelineScope, T> build)
    {
        _library = library;
        _name = name;
        _modules = [.. modules.Select(VfsPath.Normalize)];
        _build = build;

        _scope = new PipelineScope();
        try
        {
            Current = build(_scope);
        }
        catch
        {
            _scope.Dispose();
            throw;
        }

        library.ModulesChanged += OnModulesChanged;
    }

    /// <summary>What to render with now.</summary>
    public T Current { get; private set; }

    /// <summary>How many rebuilds have replaced the first build.</summary>
    public int Rebuilds { get; private set; }

    /// <summary>Starts a rebuild without waiting for a change (tests, or a renderer whose options changed).</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_running)
            {
                _again = true; // the build in progress is followed by another
                return;
            }

            _running = true;
            _task = Task.Run(Rebuild);
        }
    }

    /// <summary>Call at the start of a frame, on the render thread: if a rebuild has finished, makes it current and defers destroying the old pipelines to <paramref name="frame"/>. Returns true if it did.</summary>
    public bool Apply(Frame frame)
    {
        (T Value, PipelineScope Scope)? ready;
        string? failure;
        bool failed;
        lock (_lock)
        {
            ready = _ready;
            failure = _failure;
            failed = _failed;
            _ready = null;
            _failure = null;
            _failed = false;
        }

        if (failed)
        {
            _library.SetError(_name, failure);
            Log.Error("Shaders", $"{_name}: not rebuilt, the old pipelines stay: {failure}");
        }

        if (ready is not { } built)
        {
            return false;
        }

        frame.Defer(_scope);
        _scope = built.Scope;
        Current = built.Value;
        Rebuilds++;
        _library.SetError(_name, null);
        Log.Info("Shaders", $"{_name}: pipelines rebuilt");
        return true;
    }

    /// <summary>Waits until the build in progress, and the ones that followed it, are done (tests).</summary>
    public void WaitForPending()
    {
        Task? task;
        lock (_lock)
        {
            task = _task;
        }

        task?.Wait();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        _library.ModulesChanged -= OnModulesChanged;
        _task?.Wait();
        _ready?.Scope.Dispose();
        _scope.Dispose();
    }

    private void OnModulesChanged(IReadOnlyCollection<string> modules)
    {
        if (modules.Any(_modules.Contains))
        {
            Invalidate();
        }
    }

    private void Rebuild()
    {
        while (true)
        {
            var scope = new PipelineScope();
            T? value = null;
            string? failure = null;
            try
            {
                value = _build(scope);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Whatever went wrong on this thread is reported on the render thread by Apply; nothing may be thrown into the void of a task.
                scope.Dispose();
                failure = e.Message;
            }

            lock (_lock)
            {
                if (value is not null)
                {
                    _ready?.Scope.Dispose(); // an earlier build that was never applied
                    _ready = (value, scope);
                    _failed = false;
                    _failure = null;
                }
                else
                {
                    _failed = true;
                    _failure = failure;
                }

                if (!_again)
                {
                    _running = false;
                    return;
                }

                _again = false;
            }
        }
    }
}
