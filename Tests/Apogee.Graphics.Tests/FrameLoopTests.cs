using System;
using System.Linq;
using System.Runtime.InteropServices;
using Apogee.Graphics.Shaders;
using Apogee.Native.Vulkan;
using Xunit;

namespace Apogee.Graphics.Tests;

public unsafe class FrameLoopTests(GpuFixture gpu) : GpuTest(gpu)
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CopyPush
    {
        public ulong Source;
        public ulong Destination;
        public uint Count;
    }

    private const string CopyShader = """
        struct Push { uint* source; uint* destination; uint count; };
        [[vk::push_constant]] ConstantBuffer<Push> push;

        [shader("compute")] [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            if (id.x < push.count)
                push.destination[id.x] = push.source[id.x] + 1;
        }
        """;

    private sealed class Counter : IDisposable
    {
        public int Disposed { get; private set; }

        public void Dispose() => Disposed++;
    }

    [Fact]
    public void HeadlessFramesAdvanceThroughTheSlots()
    {
        using var loop = Device.CreateFrameLoop(framesInFlight: 2);
        var slots = new System.Collections.Generic.List<int>();

        for (int i = 0; i < 10; i++)
        {
            Frame frame = loop.BeginFrame()!;
            Assert.Equal((ulong)i, frame.Number);
            Assert.Null(frame.Backbuffer);
            slots.Add(frame.SlotIndex);
            loop.EndFrame(frame);
        }

        Assert.Equal(10UL, loop.FrameNumber);
        Assert.Equal([0, 1, 0, 1, 0, 1, 0, 1, 0, 1], slots);
        loop.WaitIdle();
    }

    [Fact]
    public void ABeginMustBeEndedAndAnEndNeedsABegin()
    {
        using var loop = Device.CreateFrameLoop();
        Frame frame = loop.BeginFrame()!;

        Assert.Throws<InvalidOperationException>(() => loop.BeginFrame());
        loop.EndFrame(frame);
        Assert.Throws<InvalidOperationException>(() => loop.EndFrame(frame));
    }

    [Fact]
    public void TheFrameArenaIsAlignedAndTheGpuReadsWhatTheCpuWrote()
    {
        CompiledShader shader = Compiler().CompileSource("increment", CopyShader);
        using Pipeline pipeline = Device.CreateComputePipeline(shader.Code("main"), "increment");
        using GpuBuffer destination = Device.CreateBuffer(256 * sizeof(uint), BufferUsage.Storage, MemoryKind.DeviceLocal, "destination");
        using var loop = Device.CreateFrameLoop();

        Frame frame = loop.BeginFrame()!;
        FrameAllocation first = frame.Allocate(10);
        FrameAllocation source = frame.Allocate(256 * sizeof(uint));
        Assert.Equal(0UL, first.Address % 256);
        Assert.Equal(0UL, source.Address % 256);
        Assert.True(source.Address >= first.Address + 10);

        Span<uint> input = MemoryMarshal.Cast<byte, uint>(source.Span);
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (uint)(i * 3);
        }

        frame.Recorder.BindPipeline(pipeline);
        frame.Recorder.PushConstants(new CopyPush { Source = source.Address, Destination = destination.Address, Count = 256 });
        frame.Recorder.Dispatch(4);
        loop.EndFrame(frame);
        loop.WaitIdle();

        uint[] result = new uint[256];
        Buffer.BlockCopy(Device.ReadBuffer(destination, 0, 256 * sizeof(uint)), 0, result, 0, 256 * sizeof(uint));
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (uint)(i * 3 + 1)), result);

        Frame next = loop.BeginFrame()!;
        Assert.ThrowsAny<GraphicsException>(() => next.Allocate(8 * 1024 * 1024));
        loop.EndFrame(next);
    }

    [Fact]
    public void DeferredDisposalWaitsUntilTheSlotIsReused()
    {
        using var loop = Device.CreateFrameLoop(framesInFlight: 2);
        var counter = new Counter();

        Frame frame0 = loop.BeginFrame()!;
        frame0.Defer(counter);
        loop.EndFrame(frame0);
        Assert.Equal(0, counter.Disposed);

        loop.EndFrame(loop.BeginFrame()!);      // frame 1 uses the other slot
        Assert.Equal(0, counter.Disposed);

        loop.EndFrame(loop.BeginFrame()!);      // frame 2 reuses slot 0, after the GPU finished frame 0
        Assert.Equal(1, counter.Disposed);

        loop.EndFrame(loop.BeginFrame()!);
        loop.EndFrame(loop.BeginFrame()!);
        Assert.Equal(1, counter.Disposed);
    }

    [Fact]
    public void ResourcesStillDeferredAreFreedWhenTheLoopIsDisposed()
    {
        var counter = new Counter();
        using (var loop = Device.CreateFrameLoop())
        {
            Frame frame = loop.BeginFrame()!;
            frame.Defer(counter);
            loop.EndFrame(frame);
        }

        Assert.Equal(1, counter.Disposed);
    }

    [Fact]
    public void GpuScopesAreTimedAndLabelled()
    {
        if (!Device.SupportsTimestamps)
        {
            Assert.Skip("This GPU has no timestamps.");
        }

        CompiledShader shader = Compiler().CompileSource("increment", CopyShader);
        using Pipeline pipeline = Device.CreateComputePipeline(shader.Code("main"), "increment");
        using GpuBuffer buffer = Device.CreateBuffer(1024 * sizeof(uint), BufferUsage.Storage, MemoryKind.DeviceLocal, "buffer");
        using var loop = Device.CreateFrameLoop();

        for (int i = 0; i < 4; i++)
        {
            Frame frame = loop.BeginFrame()!;
            using (frame.Scope("Outer"))
            {
                using (frame.Scope("Inner"))
                {
                    frame.Recorder.BindPipeline(pipeline);
                    frame.Recorder.PushConstants(new CopyPush { Source = buffer.Address, Destination = buffer.Address, Count = 1024 });
                    frame.Recorder.Dispatch(16);
                }
            }

            loop.EndFrame(frame);
        }

        loop.WaitIdle();
        loop.EndFrame(loop.BeginFrame()!);      // collects the finished frame's timers

        Assert.Equal(["Outer", "Inner"], loop.Timings.Select(t => t.Name));
        Assert.All(loop.Timings, t => Assert.True(t.Milliseconds >= 0 && t.Milliseconds < 5000));
        Assert.True(loop.Timings[0].Milliseconds >= loop.Timings[1].Milliseconds);
    }
}
