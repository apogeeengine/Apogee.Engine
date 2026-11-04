using System;
using System.Collections.Generic;
using Apogee.Native.Metal;

namespace Apogee.Graphics.Metal;

/// <summary>
/// The Metal frame loop: a few frames in flight, each with its own upload arena and command buffer, and one shared event as the timeline (its value is the number of frames the GPU has finished).
/// With a swapchain it takes the next drawable and presents it; without one it renders headless. GPU timings are not measured yet, so <see cref="Timings"/> stays empty.
/// </summary>
public sealed unsafe class MetalFrameLoop : FrameLoop
{
    private readonly MetalDevice _device;
    private readonly Slot[] _slots;
    private readonly Event* _timeline;
    private ulong _submitted;
    private ulong _frameNumber;
    private bool _frameOpen;
    private Drawable* _drawable;
    private MetalImage? _backImage;

    internal MetalFrameLoop(MetalDevice device, Swapchain? swapchain, int framesInFlight, int arenaBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(framesInFlight, 1);
        _device = device;
        Swapchain = swapchain;
        _timeline = Mtl.EventCreate(device.Handle);
        _slots = new Slot[framesInFlight];
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new Slot(device, i, arenaBytes);
        }
    }

    public override Swapchain? Swapchain { get; set; }

    public override int FramesInFlight => _slots.Length;

    public override ulong FrameNumber => _frameNumber;

    public override IReadOnlyList<GpuTiming> Timings => [];

    public override Frame? BeginFrame()
    {
        if (_frameOpen)
        {
            throw new InvalidOperationException("The previous frame was not ended.");
        }

        Slot slot = _slots[_frameNumber % (ulong)_slots.Length];
        WaitFor(slot.SubmittedValue);
        slot.Complete();

        Backbuffer? backbuffer = null;
        if (Swapchain is MetalSwapchain swapchain)
        {
            if (swapchain.IsZeroSized)
            {
                return null;
            }

            if (swapchain.IsStale)
            {
                swapchain.Recreate();
            }

            if (!swapchain.TryAcquire(out _drawable, out _backImage))
            {
                return null;
            }

            backbuffer = new Backbuffer(_backImage!, _backImage!.View, swapchain.Format, swapchain.Width, swapchain.Height);
        }

        slot.Begin();
        _frameOpen = true;
        return new Frame(this, slot, _frameNumber++, backbuffer, 0);
    }

    public override void EndFrame(Frame frame)
    {
        if (!_frameOpen)
        {
            throw new InvalidOperationException("No frame is open.");
        }

        _frameOpen = false;
        Slot slot = _slots[frame.SlotIndex];
        _submitted++;
        slot.SubmittedValue = _submitted;
        slot.Recorder!.CommitFrame(_timeline, _submitted, _drawable);

        // The command buffer holds what it needs now: give back the references to the drawable and its texture.
        if (_drawable is not null)
        {
            Mtl.Release(_drawable);
            _drawable = null;
        }

        _backImage?.Dispose();
        _backImage = null;
    }

    public override void WaitIdle() => WaitFor(_submitted);

    private void WaitFor(ulong value)
    {
        if (value != 0 && Mtl.EventWait(_timeline, value, ulong.MaxValue) != 0)
        {
            throw new MetalException("The GPU did not finish a frame.");
        }
    }

    public override void Dispose()
    {
        WaitIdle();
        foreach (Slot slot in _slots)
        {
            slot.Dispose();
        }

        Mtl.Release(_timeline);
    }

    private sealed class Slot : IFrameSlot, IDisposable
    {
        private readonly MetalDevice _device;
        private readonly MetalBuffer _arena;
        private int _arenaOffset;

        public Slot(MetalDevice device, int index, int arenaBytes)
        {
            _device = device;
            Index = index;
            _arena = new MetalBuffer(device, (ulong)arenaBytes, BufferUsage.Storage | BufferUsage.Uniform, MemoryKind.Upload, $"Frame {index} arena");
        }

        public int Index { get; }

        public MetalCommandRecorder? Recorder { get; private set; }

        CommandRecorder IFrameSlot.Recorder => Recorder!;

        /// <summary>The timeline value that says the GPU finished the frame this slot last recorded.</summary>
        public ulong SubmittedValue { get; set; }

        public List<IDisposable> Deferred { get; } = [];

        public FrameAllocation Allocate(int size, int alignment)
        {
            int start = (_arenaOffset + alignment - 1) & ~(alignment - 1);
            if (start + size > (int)_arena.Size)
            {
                throw new MetalException($"The frame's upload arena ({_arena.Size} bytes) is full.");
            }

            _arenaOffset = start + size;
            return new FrameAllocation(_arena.Address + (ulong)start, _arena.Mapped + start, size);
        }

        public int WriteTimestamp(CommandRecorder recorder, string name, bool begin) => -1;

        public void Begin()
        {
            Recorder = new MetalCommandRecorder(_device);
            _arenaOffset = 0;
        }

        /// <summary>The GPU has finished the slot's last frame: frees what the frame deferred.</summary>
        public void Complete()
        {
            foreach (IDisposable resource in Deferred)
            {
                resource.Dispose();
            }

            Deferred.Clear();
        }

        public void Dispose()
        {
            Complete();
            _arena.Dispose();
        }
    }
}
