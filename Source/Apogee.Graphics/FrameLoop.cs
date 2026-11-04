using System;
using System.Collections.Generic;

namespace Apogee.Graphics;

/// <summary>
/// Keeps frames in flight (two by default): each has its own command buffer, upload arena and timer queries, and <see cref="BeginFrame"/> waits only for the frame that used its slot
/// two frames ago. Without a swapchain it runs <b>headless</b>: the same frames with no window image, for screenshots, tests and servers. Made by <see cref="GraphicsDevice.CreateFrameLoop"/>.
/// </summary>
public abstract class FrameLoop : IDisposable
{
    public abstract Swapchain? Swapchain { get; set; }

    public abstract int FramesInFlight { get; }

    /// <summary>The number of frames that were begun.</summary>
    public abstract ulong FrameNumber { get; }

    /// <summary>The GPU time of the named sections of the most recent finished frame, in order.</summary>
    public abstract IReadOnlyList<GpuTiming> Timings { get; }

    /// <summary>Starts a frame; null when there is nothing to draw to (the window is minimized, or the swapchain was just recreated): try again next time.</summary>
    public abstract Frame? BeginFrame();

    /// <summary>Submits the frame, then presents it.</summary>
    public abstract void EndFrame(Frame frame);

    public abstract void WaitIdle();

    public abstract void Dispose();
}
