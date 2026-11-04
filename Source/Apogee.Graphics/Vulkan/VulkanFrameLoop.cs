using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Apogee.Core.Logging;
using Apogee.Native.Vulkan;

namespace Apogee.Graphics.Vulkan;

/// <summary>
/// The Vulkan frame loop: a single timeline semaphore says which of the frames in flight the GPU has finished. With a swapchain it acquires and presents; without one it renders headless.
/// </summary>
public sealed unsafe class VulkanFrameLoop : FrameLoop
{
    private const int MaxTimestamps = 128;

    private readonly VulkanDevice _device;
    private readonly Slot[] _slots;
    private readonly VkSemaphore _timeline;
    private ulong _submitted;
    private ulong _frameNumber;
    private bool _frameOpen;

    internal VulkanFrameLoop(VulkanDevice device, Swapchain? swapchain = null, int framesInFlight = 2, int arenaBytes = 4 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(framesInFlight, 1);

        _device = device;
        _swapchain = swapchain;
        _timeline = device.CreateTimelineSemaphore();
        _slots = new Slot[framesInFlight];
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new Slot(device, i, arenaBytes);
        }
    }

    private Swapchain? _swapchain;

    /// <summary>Where frames are presented; null renders headless. Set it again after replacing the swapchain.</summary>
    public override Swapchain? Swapchain { get => _swapchain; set => _swapchain = value; }

    public override int FramesInFlight => _slots.Length;

    public override ulong FrameNumber => _frameNumber;

    private IReadOnlyList<GpuTiming> _timings = [];

    /// <summary>The GPU timings of the most recent frame the GPU finished (one or two frames behind), in the order the sections began.</summary>
    public override IReadOnlyList<GpuTiming> Timings => _timings;

    /// <summary>
    /// Starts a frame, waiting for the GPU if it is still busy with the frame that used this slot before. Returns null when there is nothing to render to
    /// this time (the window is minimized, or the swapchain had to be recreated): skip the frame and call it again.
    /// </summary>
    public override Frame? BeginFrame()
    {
        if (_frameOpen)
        {
            throw new InvalidOperationException("The previous frame was not ended.");
        }

        Slot slot = _slots[_frameNumber % (ulong)_slots.Length];
        WaitFor(slot.SubmittedValue);
        _timings = slot.Complete(this) ?? _timings;

        Backbuffer? backbuffer = null;
        uint imageIndex = 0;
        if (Swapchain is VulkanSwapchain swapchain)
        {
            if (swapchain.IsZeroSized)
            {
                return null;
            }

            if (swapchain.IsStale)
            {
                swapchain.Recreate();
            }

            if (swapchain.Acquire(slot.AcquireSemaphore, out imageIndex) == AcquireStatus.OutOfDate)
            {
                swapchain.Recreate();
                return null;
            }

            GpuImage image = swapchain.ImageAt(imageIndex);
            backbuffer = new Backbuffer(image, image.View, swapchain.Format, swapchain.Width, swapchain.Height);
            slot.WaitsForAcquire = true;
        }
        else
        {
            slot.WaitsForAcquire = false;
        }

        slot.Begin();
        _frameOpen = true;
        return new Frame(this, slot, _frameNumber++, backbuffer, imageIndex);
    }

    /// <summary>Submits the frame and presents it. A frame's window image must be in <see cref="ImageState.Present"/> by now.</summary>
    public override void EndFrame(Frame frame)
    {
        if (!_frameOpen)
        {
            throw new InvalidOperationException("No frame is open.");
        }

        _frameOpen = false;
        Slot slot = _slots[frame.SlotIndex];
        slot.End();

        VkDeviceCommands commands = _device.Commands;
        _submitted++;
        slot.SubmittedValue = _submitted;

        var commandInfo = new VkCommandBufferSubmitInfo { CommandBuffer = slot.CommandBuffer };
        var waits = stackalloc VkSemaphoreSubmitInfo[1];
        var signals = stackalloc VkSemaphoreSubmitInfo[2];
        uint waitCount = 0;
        uint signalCount = 0;
        if (slot.WaitsForAcquire)
        {
            waits[waitCount++] = new VkSemaphoreSubmitInfo { Semaphore = slot.AcquireSemaphore, StageMask = VkPipelineStageFlagBits2.AllCommands };
            signals[signalCount++] = new VkSemaphoreSubmitInfo { Semaphore = ((VulkanSwapchain)Swapchain!).PresentSemaphore(frame.ImageIndex), StageMask = VkPipelineStageFlagBits2.AllCommands };
        }

        signals[signalCount++] = new VkSemaphoreSubmitInfo { Semaphore = _timeline, Value = _submitted, StageMask = VkPipelineStageFlagBits2.AllCommands };

        var submit = new VkSubmitInfo2
        {
            WaitSemaphoreInfoCount = waitCount,
            PWaitSemaphoreInfos = waits,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &commandInfo,
            SignalSemaphoreInfoCount = signalCount,
            PSignalSemaphoreInfos = signals,
        };
        lock (_device.QueueLock)
        {
            VulkanException.Check(commands.QueueSubmit2(_device.Queue, 1, &submit, default), "vkQueueSubmit2");
        }

        if (slot.WaitsForAcquire && !((VulkanSwapchain)Swapchain!).Present(frame.ImageIndex))
        {
            Swapchain!.Recreate();
        }
    }

    public override void WaitIdle() => WaitFor(_submitted);

    private void WaitFor(ulong value)
    {
        if (value == 0)
        {
            return;
        }

        VkSemaphore semaphore = _timeline;
        ulong target = value;
        var info = new VkSemaphoreWaitInfo { SemaphoreCount = 1, PSemaphores = &semaphore, PValues = &target };
        VulkanException.Check(_device.Commands.WaitSemaphores(_device.Handle, &info, ulong.MaxValue), "vkWaitSemaphores");
    }

    public override void Dispose()
    {
        WaitIdle();
        foreach (Slot slot in _slots)
        {
            slot.Dispose();
        }

        _device.Commands.DestroySemaphore(_device.Handle, _timeline, null);
    }

    internal sealed class Slot : IDisposable, IFrameSlot
    {
        private readonly VulkanDevice _device;
        private readonly VkCommandPool _pool;
        private readonly GpuBuffer _arena;
        private readonly VkQueryPool _queries;
        private readonly List<(string Name, bool Begin)> _stamps = [];
        private int _arenaOffset;
        private bool _used;

        public Slot(VulkanDevice device, int index, int arenaBytes)
        {
            _device = device;
            Index = index;
            VkDeviceCommands commands = device.Commands;

            var poolInfo = new VkCommandPoolCreateInfo { Flags = VkCommandPoolCreateFlagBits.Transient, QueueFamilyIndex = device.QueueFamily };
            VkCommandPool pool;
            VulkanException.Check(commands.CreateCommandPool(device.Handle, &poolInfo, null, &pool), "vkCreateCommandPool");
            _pool = pool;

            var allocate = new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary, CommandBufferCount = 1 };
            VkCommandBuffer commandBuffer;
            VulkanException.Check(commands.AllocateCommandBuffers(device.Handle, &allocate, &commandBuffer), "vkAllocateCommandBuffers");
            CommandBuffer = commandBuffer;
            Recorder = new VulkanCommandRecorder(device, commandBuffer);

            AcquireSemaphore = device.CreateBinarySemaphore();
            _arena = device.CreateBuffer((ulong)arenaBytes, BufferUsage.Storage | BufferUsage.Uniform, MemoryKind.Upload, $"Frame {index} arena");

            if (device.SupportsTimestamps)
            {
                var queryInfo = new VkQueryPoolCreateInfo { QueryType = VkQueryType.Timestamp, QueryCount = MaxTimestamps };
                VkQueryPool queries;
                VulkanException.Check(commands.CreateQueryPool(device.Handle, &queryInfo, null, &queries), "vkCreateQueryPool");
                _queries = queries;
            }
        }

        public int Index { get; }

        public VkCommandBuffer CommandBuffer { get; }

        public VulkanCommandRecorder Recorder { get; }

        CommandRecorder IFrameSlot.Recorder => Recorder;

        public VkSemaphore AcquireSemaphore { get; }

        public bool WaitsForAcquire { get; set; }

        /// <summary>The timeline value that says the GPU finished the frame this slot last recorded.</summary>
        public ulong SubmittedValue { get; set; }

        public List<IDisposable> Deferred { get; } = [];

        public FrameAllocation Allocate(int size, int alignment)
        {
            int start = (_arenaOffset + alignment - 1) & ~(alignment - 1);
            if (start + size > (int)_arena.Size)
            {
                throw new VulkanException($"The frame's upload arena ({_arena.Size} bytes) is full.");
            }

            _arenaOffset = start + size;
            return new FrameAllocation(_arena.Address + (ulong)start, _arena.Mapped + start, size);
        }

        public void Begin()
        {
            VkDeviceCommands commands = _device.Commands;
            VulkanException.Check(commands.ResetCommandPool(_device.Handle, _pool, 0), "vkResetCommandPool");
            var begin = new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlagBits.OneTimeSubmit };
            VulkanException.Check(commands.BeginCommandBuffer(CommandBuffer, &begin), "vkBeginCommandBuffer");
            if (_queries.Value != 0)
            {
                commands.ResetQueryPool(_device.Handle, _queries, 0, MaxTimestamps);
            }

            _stamps.Clear();
            _arenaOffset = 0;
            _used = true;
        }

        public void End() => VulkanException.Check(_device.Commands.EndCommandBuffer(CommandBuffer), "vkEndCommandBuffer");

        public int WriteTimestamp(CommandRecorder recorder, string name, bool begin)
        {
            if (_queries.Value == 0 || _stamps.Count >= MaxTimestamps)
            {
                return -1;
            }

            _device.Commands.CmdWriteTimestamp2(((VulkanCommandRecorder)recorder).Handle, VkPipelineStageFlagBits2.AllCommands, _queries, (uint)_stamps.Count);
            _stamps.Add((name, begin));
            return _stamps.Count - 1;
        }

        /// <summary>Called when the GPU has finished the slot's last frame: frees what the frame deferred and returns its timings.</summary>
        public IReadOnlyList<GpuTiming>? Complete(FrameLoop loop)
        {
            foreach (IDisposable resource in Deferred)
            {
                resource.Dispose();
            }

            Deferred.Clear();
            if (!_used || _queries.Value == 0 || _stamps.Count == 0)
            {
                return null;
            }

            ulong[] ticks = new ulong[_stamps.Count];
            fixed (ulong* data = ticks)
            {
                VkResult result = _device.Commands.GetQueryPoolResults(_device.Handle, _queries, 0, (uint)ticks.Length, (nuint)(ticks.Length * sizeof(ulong)), data, sizeof(ulong), VkQueryResultFlagBits._64);
                if (result != VkResult.Success)
                {
                    return null;
                }
            }

            // A section is the span between its begin stamp and the next end stamp with the same name; sections nest, so match with a stack.
            var starts = new Stack<(string Name, int Index)>();
            var timings = new List<(int Start, GpuTiming Timing)>();
            for (int i = 0; i < _stamps.Count; i++)
            {
                if (_stamps[i].Begin)
                {
                    starts.Push((_stamps[i].Name, i));
                }
                else if (starts.Count > 0)
                {
                    (string name, int begin) = starts.Pop();
                    timings.Add((begin, new GpuTiming(name, (ticks[i] - ticks[begin]) * (double)_device.TimestampPeriod / 1_000_000.0)));
                }
            }

            timings.Sort((a, b) => a.Start.CompareTo(b.Start));
            return timings.ConvertAll(t => t.Timing);
        }

        public void Dispose()
        {
            foreach (IDisposable resource in Deferred)
            {
                resource.Dispose();
            }

            VkDeviceCommands commands = _device.Commands;
            if (_queries.Value != 0)
            {
                commands.DestroyQueryPool(_device.Handle, _queries, null);
            }

            _arena.Dispose();
            commands.DestroySemaphore(_device.Handle, AcquireSemaphore, null);
            commands.DestroyCommandPool(_device.Handle, _pool, null);
        }
    }
}
