using System;
using System.Collections.Generic;
using System.Linq;
using Weft.Graphics;

namespace Weft.Rendering.Graph;

/// <summary>
/// The images and buffers graphs allocate, kept between frames so that a steady-state frame allocates nothing. There is one set per frame in
/// flight (a resource is only reused by the frame slot that used it before, once the GPU has finished with it), and one that goes unused for a
/// while is freed.
/// </summary>
public sealed class TransientPool : IDisposable
{
    private const int KeepFrames = 8;

    private readonly GraphicsDevice _device;
    private readonly List<PooledImage>[] _images;
    private readonly List<PooledBuffer>[] _buffers;
    private readonly int[] _executions;
    private readonly List<(GpuImage Image, ViewEntry View)>[] _temporaryViews;

    public TransientPool(GraphicsDevice device, int framesInFlight = 2)
    {
        _device = device;
        _images = Create(framesInFlight, () => new List<PooledImage>());
        _buffers = Create(framesInFlight, () => new List<PooledBuffer>());
        _executions = new int[framesInFlight];
        _temporaryViews = Create(framesInFlight, () => new List<(GpuImage, ViewEntry)>());
    }

    /// <summary>A view of part of an image, with the bindless indices that read or write just that part (0 where the image has no such usage).</summary>
    internal readonly record struct ViewEntry(ImageView View, uint SampledIndex, uint StorageIndex);

    /// <summary>
    /// A view of part of an image the graph imported (a transient's views are kept with the transient). It, and its bindless entries, are destroyed the next time this frame slot starts a graph, which is
    /// after the GPU has finished the frame that used it.
    /// </summary>
    internal ViewEntry AcquireTemporaryView(int slot, GpuImage image, SubresourceRange range)
    {
        ViewEntry view = CreateView(_device, image, range);
        _temporaryViews[slot].Add((image, view));
        return view;
    }

    /// <summary>Called when a graph starts executing in a frame slot: the slot's frame from before is finished, so the views it needed can go.</summary>
    internal void BeginExecution(int slot)
    {
        foreach ((GpuImage image, ViewEntry view) in _temporaryViews[slot])
        {
            DestroyView(_device, image, view);
        }

        _temporaryViews[slot].Clear();
    }

    private static ViewEntry CreateView(GraphicsDevice device, GpuImage image, SubresourceRange range)
    {
        ImageView view = image.CreateView(range.BaseMip, range.MipCount, range.BaseLayer, range.LayerCount);
        uint sampled = image.Description.Usage.HasFlag(ImageUsage.Sampled) ? device.Bindless.AddSampledImage(view) : 0;
        uint storage = image.Description.Usage.HasFlag(ImageUsage.Storage) ? device.Bindless.AddStorageImage(view) : 0;
        return new ViewEntry(view, sampled, storage);
    }

    private static void DestroyView(GraphicsDevice device, GpuImage image, ViewEntry entry)
    {
        if (entry.SampledIndex != 0)
        {
            device.Bindless.RemoveSampledImage(entry.SampledIndex);
        }

        if (entry.StorageIndex != 0)
        {
            device.Bindless.RemoveStorageImage(entry.StorageIndex);
        }

        image.DestroyView(entry.View);
    }

    /// <summary>Images and buffers currently alive across all frame slots.</summary>
    public int ImageCount => _images.Sum(static l => l.Count);

    public int BufferCount => _buffers.Sum(static l => l.Count);

    internal PooledImage AcquireImage(int slot, in ImageDescription description)
    {
        foreach (PooledImage pooled in _images[slot])
        {
            if (!pooled.InUse && pooled.Description.Equals(description))
            {
                pooled.InUse = true;
                pooled.LastUsed = _executions[slot];
                return pooled;
            }
        }

        var created = new PooledImage(_device, description) { InUse = true, LastUsed = _executions[slot] };
        _images[slot].Add(created);
        return created;
    }

    internal GpuBuffer AcquireBuffer(int slot, ulong size, BufferUsage usage)
    {
        foreach (PooledBuffer pooled in _buffers[slot])
        {
            if (!pooled.InUse && pooled.Size == size && pooled.Usage == usage)
            {
                pooled.InUse = true;
                pooled.LastUsed = _executions[slot];
                return pooled.Buffer;
            }
        }

        var created = new PooledBuffer(_device.CreateBuffer(size, usage, MemoryKind.DeviceLocal, "Graph buffer"), size, usage) { InUse = true, LastUsed = _executions[slot] };
        _buffers[slot].Add(created);
        return created.Buffer;
    }

    /// <summary>Marks everything of this slot free for the next graph, and frees what has not been used for a while.</summary>
    internal void EndExecution(int slot)
    {
        _executions[slot]++;
        for (int i = _images[slot].Count - 1; i >= 0; i--)
        {
            PooledImage pooled = _images[slot][i];
            pooled.InUse = false;
            if (_executions[slot] - pooled.LastUsed > KeepFrames)
            {
                pooled.Dispose();
                _images[slot].RemoveAt(i);
            }
        }

        for (int i = _buffers[slot].Count - 1; i >= 0; i--)
        {
            PooledBuffer pooled = _buffers[slot][i];
            pooled.InUse = false;
            if (_executions[slot] - pooled.LastUsed > KeepFrames)
            {
                pooled.Buffer.Dispose();
                _buffers[slot].RemoveAt(i);
            }
        }
    }

    public void Dispose()
    {
        for (int slot = 0; slot < _temporaryViews.Length; slot++)
        {
            BeginExecution(slot);
        }

        foreach (List<PooledImage> list in _images)
        {
            list.ForEach(i => i.Dispose());
            list.Clear();
        }

        foreach (List<PooledBuffer> list in _buffers)
        {
            list.ForEach(b => b.Buffer.Dispose());
            list.Clear();
        }
    }

    private static T[] Create<T>(int count, Func<T> create)
    {
        var array = new T[count];
        for (int i = 0; i < count; i++)
        {
            array[i] = create();
        }

        return array;
    }

    internal sealed class PooledImage : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly Dictionary<SubresourceRange, ViewEntry> _views = [];

        public PooledImage(GraphicsDevice device, in ImageDescription description)
        {
            _device = device;
            Description = description;
            Image = device.CreateImage(description, "Graph image");

            // A transient that shaders read or write by index gets its bindless slot with the image, and keeps it as long as it lives.
            if (description.Usage.HasFlag(ImageUsage.Sampled))
            {
                SampledIndex = device.Bindless.AddSampledImage(Image);
            }

            if (description.Usage.HasFlag(ImageUsage.Storage))
            {
                StorageIndex = device.Bindless.AddStorageImage(Image.View);
            }
        }

        public ImageDescription Description { get; }

        public GpuImage Image { get; }

        public uint SampledIndex { get; }

        public uint StorageIndex { get; }

        public bool InUse { get; set; }

        public int LastUsed { get; set; }

        /// <summary>The view of a part of this image, made when first asked for and kept as long as the image lives.</summary>
        public ViewEntry GetView(SubresourceRange range)
        {
            if (!_views.TryGetValue(range, out ViewEntry entry))
            {
                entry = CreateView(_device, Image, range);
                _views.Add(range, entry);
            }

            return entry;
        }

        public void Dispose()
        {
            foreach (ViewEntry view in _views.Values)
            {
                DestroyView(_device, Image, view);
            }

            _views.Clear();

            if (SampledIndex != 0)
            {
                _device.Bindless.RemoveSampledImage(SampledIndex);
            }

            if (StorageIndex != 0)
            {
                _device.Bindless.RemoveStorageImage(StorageIndex);
            }

            Image.Dispose();
        }
    }

    internal sealed class PooledBuffer(GpuBuffer buffer, ulong size, BufferUsage usage)
    {
        public GpuBuffer Buffer { get; } = buffer;

        public ulong Size { get; } = size;

        public BufferUsage Usage { get; } = usage;

        public bool InUse { get; set; }

        public int LastUsed { get; set; }
    }
}
