using System;
using System.Collections.Generic;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Core.Logging;
using Apogee.Reflection;

namespace Apogee.Rendering;

/// <summary>
/// The models that entities draw (<see cref="ModelRenderer.Model"/> is an id from here; scripts see this as <c>models</c>). A model is loaded once per path whatever the number of entities
/// that show it, and reloaded in place when its file changes, so entities keep their id. Ids are never reused.
/// </summary>
/// <remarks>
/// Ids are handed out as soon as a path names a readable file, even before there is a GPU: a game's scripts run first and the renderer comes later (or never, in a headless run). The model is
/// read and put on the GPU the first time something asks for it after <see cref="Attach"/>; a file that does not load is logged once and draws nothing until it is reloaded.
/// Everything runs on the thread that renders.
/// </remarks>
[ApogeeClass]
public sealed partial class ModelLibrary : EngineObject, IDisposable
{
    private readonly VirtualFileSystem _files;
    private readonly ContentLoader _content;
    private readonly Dictionary<string, uint> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, Entry> _models = [];
    private SceneRenderer? _renderer;
    private ContentOptions _options = new();
    private uint _next = 1;

    private sealed class Entry(string? path)
    {
        public string? Path { get; } = path;

        public GpuModel? Model { get; set; }

        /// <summary>The model's box, measured from the file when it is first asked for (a picking ray needs it with no GPU).</summary>
        public (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)? Bounds { get; set; }

        /// <summary>The file did not load; not tried again until it is reloaded.</summary>
        public bool Failed { get; set; }
    }

    /// <param name="files">Where models are read from.</param>
    /// <param name="content">Reads them (imported ones when there is an artifact).</param>
    public ModelLibrary(VirtualFileSystem files, ContentLoader content)
    {
        _files = files;
        _content = content;
    }

    /// <summary>True once models can be put on the GPU.</summary>
    public bool IsAttached => _renderer is not null;

    /// <summary>How many models have an id.</summary>
    public int Count => _models.Count;

    /// <summary>The virtual paths of the models loaded by path.</summary>
    public IReadOnlyCollection<string> Paths => _byPath.Keys;

    /// <summary>Lets models go on the GPU of <paramref name="renderer"/>; <paramref name="caps"/> are the compressed texture formats it samples.</summary>
    public void Attach(SceneRenderer renderer, TextureCaps caps)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        Detach();
        _renderer = renderer;
        _options = new ContentOptions { TextureCaps = caps };
    }

    /// <summary>Frees the GPU side (before the device goes away). The ids stay, and the models come back on the next <see cref="Attach"/>.</summary>
    public void Detach()
    {
        foreach (Entry entry in _models.Values)
        {
            entry.Model?.Dispose();
            entry.Model = null;
            entry.Failed = false;
        }

        _renderer = null;
    }

    /// <summary>The id of the model at <paramref name="path"/>, which is the same for every call with that path. The file has to exist; whether it is a model shows when it is first drawn.</summary>
    [Method]
    public uint Load(string path)
    {
        string canonical = VfsPath.Normalize(path);
        if (_byPath.TryGetValue(canonical, out uint id))
        {
            return id;
        }

        if (!_files.FileExists(canonical))
        {
            throw new System.IO.FileNotFoundException($"There is no model file at {path}.");
        }

        id = _next++;
        _models[id] = new Entry(canonical);
        _byPath[canonical] = id;
        return id;
    }

    /// <summary>Puts a model made in code on the GPU and returns its id. There has to be a renderer (<see cref="Attach"/>).</summary>
    public uint Add(ModelData model)
    {
        SceneRenderer renderer = _renderer ?? throw new InvalidOperationException("Attach a renderer first.");
        uint id = _next++;
        _models[id] = new Entry(null) { Model = renderer.CreateModel(model) };
        return id;
    }

    /// <summary>The model with that id on the GPU, or null: there is none (0, or one that was removed), there is no renderer, or its file does not load.</summary>
    public GpuModel? Find(uint id)
    {
        if (!_models.TryGetValue(id, out Entry? entry))
        {
            return null;
        }

        if (entry.Model is null && !entry.Failed && entry.Path is not null && _renderer is { } renderer)
        {
            try
            {
                entry.Model = renderer.CreateModel(_content.LoadModel(entry.Path, _options));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                entry.Failed = true;
                Log.Error("Models", $"{entry.Path} does not load: {exception.Message}");
            }
        }

        return entry.Model;
    }

    /// <summary>
    /// The box of the model in its own space, for picking and framing: from the model on the GPU when it is there, otherwise measured from the file once (the file is read, but nothing is put on the
    /// GPU). Null for an id the library does not know or a file that does not load.
    /// </summary>
    public (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)? BoundsOf(uint id)
    {
        if (!_models.TryGetValue(id, out Entry? entry))
        {
            return null;
        }

        if (entry.Model is { } onGpu)
        {
            return (onGpu.BoundsMin, onGpu.BoundsMax);
        }

        if (entry.Bounds is null && !entry.Failed && entry.Path is not null)
        {
            try
            {
                ModelData data = _content.LoadModel(entry.Path, _options);
                entry.Bounds = (data.BoundsMin, data.BoundsMax);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                entry.Failed = true;
                Log.Error("Models", $"{entry.Path} does not load: {exception.Message}");
            }
        }

        return entry.Bounds;
    }

    /// <summary>The path the model <paramref name="id"/> was loaded from, or null for an id the library does not know: what a scene saves instead of the number.</summary>
    public string? PathOf(uint id)
    {
        foreach ((string path, uint known) in _byPath)
        {
            if (known == id)
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>The id of the model loaded from <paramref name="path"/>, or 0.</summary>
    public uint IdOf(string path) => _byPath.TryGetValue(VfsPath.Normalize(path), out uint id) ? id : 0;

    /// <summary>
    /// Reads the model at <paramref name="path"/> again, if it has an id, and swaps it in under the same id. The old one is freed once the new one is ready; when the file no longer
    /// loads the exception propagates and the old model stays. Returns false when the path has no id.
    /// </summary>
    public bool Reload(string path)
    {
        string canonical = VfsPath.Normalize(path);
        if (!_byPath.TryGetValue(canonical, out uint id))
        {
            return false;
        }

        Entry entry = _models[id];
        entry.Failed = false;
        if (entry.Model is null || _renderer is not { } renderer)
        {
            return true; // not on the GPU: the next draw reads the new file
        }

        GpuModel fresh = renderer.CreateModel(_content.LoadModel(canonical, _options));
        GpuModel old = entry.Model;
        entry.Model = fresh;
        old.Dispose();
        return true;
    }

    /// <summary>Frees a model; entities that still name its id draw nothing. Returns false when there was none.</summary>
    public bool Remove(uint id)
    {
        if (!_models.Remove(id, out Entry? entry))
        {
            return false;
        }

        if (entry.Path is not null)
        {
            _byPath.Remove(entry.Path);
        }

        entry.Model?.Dispose();
        return true;
    }

    public void Dispose() => Destroy();

    protected override void OnDestroyed()
    {
        Detach();
        _models.Clear();
        _byPath.Clear();
    }
}
