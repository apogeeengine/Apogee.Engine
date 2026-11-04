using System;
using System.Collections.Generic;
using Apogee.Core.Graphics;

namespace Apogee.Graphics;

/// <summary>Space in a frame's upload arena: an address for shaders and memory for the CPU to fill.</summary>
public readonly unsafe struct FrameAllocation
{
    internal FrameAllocation(ulong address, byte* memory, int size)
    {
        Address = address;
        Memory = memory;
        Size = size;
    }

    /// <summary>The GPU address of the first byte: put it in push constants.</summary>
    public ulong Address { get; }

    public byte* Memory { get; }

    public int Size { get; }

    public Span<byte> Span => new(Memory, Size);
}

/// <summary>How long a named part of a frame took on the GPU.</summary>
public readonly record struct GpuTiming(string Name, double Milliseconds);

/// <summary>The image of the window the frame renders to. It starts in <see cref="ImageState.Undefined"/> and must end in <see cref="ImageState.Present"/>.</summary>
public readonly record struct Backbuffer(GpuImage Image, ImageView View, PixelFormat Format, uint Width, uint Height);

/// <summary>One frame in flight: its command buffer, the window's image, per-frame upload memory, and deferred destruction.</summary>
public sealed unsafe class Frame
{
    private readonly IFrameSlot _slot;

    internal Frame(FrameLoop loop, IFrameSlot slot, ulong number, Backbuffer? backbuffer, uint imageIndex)
    {
        Loop = loop;
        _slot = slot;
        Number = number;
        Backbuffer = backbuffer;
        ImageIndex = imageIndex;
        Recorder = slot.Recorder;
    }

    internal FrameLoop Loop { get; }

    internal IFrameSlot Slot => _slot;

    internal uint ImageIndex { get; }

    /// <summary>Counts up from 0 with every frame that was begun.</summary>
    public ulong Number { get; }

    /// <summary>Which of the frames in flight this is (0 or 1): per-frame resources are indexed by it.</summary>
    public int SlotIndex => _slot.Index;

    /// <summary>Records into this frame's command buffer.</summary>
    public CommandRecorder Recorder { get; }

    /// <summary>The window's image, or null when rendering headless.</summary>
    public Backbuffer? Backbuffer { get; }

    /// <summary>Gives out <paramref name="size"/> bytes of host-visible memory that stays valid until the GPU is done with this frame. The memory is not zeroed.</summary>
    public FrameAllocation Allocate(int size, int alignment = 256) => _slot.Allocate(size, alignment);

    /// <summary>Disposes <paramref name="resource"/> once the GPU has finished this frame: the way to free something the frame's commands still use.</summary>
    public void Defer(IDisposable resource) => _slot.Deferred.Add(resource);

    /// <summary>Opens a named section: a debug label for RenderDoc and a GPU timer reported in <see cref="FrameLoop.Timings"/>.</summary>
    public FrameScope Scope(string name) => new(this, name);

    internal int WriteTimestamp(string name, bool begin) => _slot.WriteTimestamp(Recorder, name, begin);
}

/// <summary>What a frame needs from the frame in flight it runs in: its recorder, its upload arena, its deferred disposals and its timer queries. A backend's frame loop implements it.</summary>
internal interface IFrameSlot
{
    int Index { get; }

    CommandRecorder Recorder { get; }

    List<IDisposable> Deferred { get; }

    FrameAllocation Allocate(int size, int alignment);

    int WriteTimestamp(CommandRecorder recorder, string name, bool begin);
}

/// <summary>A debug label with a GPU timer; dispose it to end the section.</summary>
public readonly struct FrameScope : IDisposable
{
    private readonly Frame _frame;
    private readonly DebugLabelScope _label;
    private readonly string _name;

    internal FrameScope(Frame frame, string name)
    {
        _frame = frame;
        _name = name;
        _label = frame.Recorder.Label(name);
        frame.WriteTimestamp(name, begin: true);
    }

    public void Dispose()
    {
        _frame.WriteTimestamp(_name, begin: false);
        _label.Dispose();
    }
}
