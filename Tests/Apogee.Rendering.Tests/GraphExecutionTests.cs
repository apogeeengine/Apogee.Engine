using Apogee.Core.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Apogee.Graphics;
using Apogee.Graphics.Shaders;
using Apogee.Graphics.Tests;
using Apogee.Native.Vulkan;
using Apogee.Rendering.Graph;
using Xunit;

namespace Apogee.Rendering.Tests;

public unsafe class GraphExecutionTests(GpuFixture gpu) : GpuTest(gpu)
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SamplePush
    {
        public uint Texture;
        public uint Sampler;
        public ulong Output;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePush
    {
        public uint Image;
        public uint Width;
        public uint Height;
        public float Value;
    }

    private const string SampleShader = """
        [[vk::binding(0, 0)]] Texture2D<float4> gTextures[];
        [[vk::binding(1, 0)]] SamplerState gSamplers[];

        struct Push { uint texture; uint sampler; float4* output; };
        [[vk::push_constant]] ConstantBuffer<Push> push;

        [shader("compute")] [numthreads(1, 1, 1)]
        void main()
        {
            push.output[0] = gTextures[NonUniformResourceIndex(push.texture)].SampleLevel(gSamplers[push.sampler], float2(0.5, 0.5), 0);
        }
        """;

    private const string FillShader = """
        [[vk::binding(2, 0)]] RWTexture2D<float4> gStorage[];

        struct Push { uint image; uint width; uint height; float value; };
        [[vk::push_constant]] ConstantBuffer<Push> push;

        [shader("compute")] [numthreads(8, 8, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            if (id.x < push.width && id.y < push.height)
                gStorage[push.image][id.xy] = float4(0.0, push.value, 0.0, 1.0);
        }
        """;

    private static ImageDescription ColorTarget(uint size = 8) =>
        new(PixelFormat.R8G8B8A8Unorm, size, size, ImageUsage.ColorAttachment | ImageUsage.Sampled);

    private static void RunFrame(GraphicsDevice device, FrameLoop loop, Action<Frame> record)
    {
        Frame frame = loop.BeginFrame()!;
        record(frame);
        loop.EndFrame(frame);
        loop.WaitIdle();
    }

    [Fact]
    public void ARenderedTextureIsSampledThroughTheBindlessHeapInTheNextPass()
    {
        CompiledShader shader = Compiler().CompileSource("sample", SampleShader);
        using Pipeline pipeline = Device.CreateComputePipeline(shader.Code("main"), "sample");
        using GpuBuffer output = Device.CreateBuffer(16, BufferUsage.Storage, MemoryKind.DeviceLocal, "output");
        using GpuSampler sampler = Device.CreateSampler(SamplerDescription.NearestClamp, "nearest");
        uint samplerIndex = Device.Bindless.AddSampler(sampler);
        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();

        RunFrame(Device, loop, frame =>
        {
            var graph = new RenderGraph(Device, pool);
            ImageHandle texture = graph.CreateImage("Rendered", ColorTarget());
            BufferHandle result = graph.ImportBuffer("Output", output, BufferState.Undefined, BufferState.TransferSource, output: true);

            graph.AddPass("Render", PassType.Graphics, pass => pass.WriteColor(texture, new Vector4(1f, 0f, 0f, 1f)));
            graph.AddPass("Sample", PassType.Compute, pass =>
            {
                pass.ReadSampled(texture);
                pass.Write(result);
                pass.Execute(context =>
                {
                    ResolvedImage image = context.Image(texture);
                    Assert.NotEqual(0u, image.SampledIndex);
                    context.Recorder.BindPipeline(pipeline);
                    context.Recorder.PushConstants(new SamplePush { Texture = image.SampledIndex, Sampler = samplerIndex, Output = context.Buffer(result).Address });
                    context.Recorder.Dispatch(1);
                });
            });
            graph.Execute(frame);

            Assert.Equal(2, graph.Plan!.LivePassCount);
        });

        float[] rgba = new float[4];
        Buffer.BlockCopy(Device.ReadBuffer(output, 0, 16), 0, rgba, 0, 16);
        Assert.Equal([1f, 0f, 0f, 1f], rgba);
        Device.Bindless.RemoveSampler(samplerIndex);
    }

    [Fact]
    public void ATransientStorageImageIsWrittenByIndexAndSampledAfterwards()
    {
        CompiledShader fill = Compiler().CompileSource("fill", FillShader);
        CompiledShader sample = Compiler().CompileSource("sample", SampleShader);
        using Pipeline fillPipeline = Device.CreateComputePipeline(fill.Code("main"), "fill");
        using Pipeline samplePipeline = Device.CreateComputePipeline(sample.Code("main"), "sample");
        using GpuBuffer output = Device.CreateBuffer(16, BufferUsage.Storage, MemoryKind.DeviceLocal, "output");
        using GpuSampler sampler = Device.CreateSampler(SamplerDescription.NearestClamp, "nearest");
        uint samplerIndex = Device.Bindless.AddSampler(sampler);
        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();

        RunFrame(Device, loop, frame =>
        {
            var graph = new RenderGraph(Device, pool);
            ImageHandle image = graph.CreateImage("Storage", new ImageDescription(PixelFormat.R8G8B8A8Unorm, 8, 8, ImageUsage.Storage | ImageUsage.Sampled));
            BufferHandle result = graph.ImportBuffer("Output", output, BufferState.Undefined, BufferState.TransferSource, output: true);

            graph.AddPass("Fill", PassType.Compute, pass =>
            {
                pass.WriteStorage(image);
                pass.Execute(context =>
                {
                    ResolvedImage resolved = context.Image(image);
                    context.Recorder.BindPipeline(fillPipeline);
                    context.Recorder.PushConstants(new StoragePush { Image = resolved.StorageIndex, Width = 8, Height = 8, Value = 1f });
                    context.Recorder.Dispatch(1, 1);
                });
            });
            graph.AddPass("Sample", PassType.Compute, pass =>
            {
                pass.ReadSampled(image);
                pass.Write(result);
                pass.Execute(context =>
                {
                    context.Recorder.BindPipeline(samplePipeline);
                    context.Recorder.PushConstants(new SamplePush { Texture = context.Image(image).SampledIndex, Sampler = samplerIndex, Output = context.Buffer(result).Address });
                    context.Recorder.Dispatch(1);
                });
            });
            graph.Execute(frame);
        });

        float[] rgba = new float[4];
        Buffer.BlockCopy(Device.ReadBuffer(output, 0, 16), 0, rgba, 0, 16);
        Assert.Equal([0f, 1f, 0f, 1f], rgba);
        Device.Bindless.RemoveSampler(samplerIndex);
    }

    [Fact]
    public void CulledPassesDoNotRunAndTheOthersRunInOrder()
    {
        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();
        using GpuImage target = Device.CreateImage(ColorTarget(), "target");
        var ran = new List<string>();

        RunFrame(Device, loop, frame =>
        {
            var graph = new RenderGraph(Device, pool);
            ImageHandle output = graph.ImportImage("Output", target, ImageState.Undefined, ImageState.TransferSource, output: true);
            ImageHandle scratch = graph.CreateImage("Scratch", ColorTarget());
            ImageHandle unused = graph.CreateImage("Unused", ColorTarget());

            graph.AddPass("Wasted", PassType.Graphics, pass => { pass.WriteColor(unused, Vector4.Zero); pass.Execute(_ => ran.Add("Wasted")); });
            graph.AddPass("A", PassType.Graphics, pass => { pass.WriteColor(scratch, Vector4.One); pass.Execute(_ => ran.Add("A")); });
            graph.AddPass("B", PassType.Graphics, pass => { pass.ReadSampled(scratch); pass.WriteColor(output, Vector4.Zero); pass.Execute(_ => ran.Add("B")); });
            graph.Execute(frame);
        });

        Assert.Equal(["A", "B"], ran);
    }

    [Fact]
    public void ImportedImagesEndInTheRequestedState()
    {
        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();
        using GpuImage target = Device.CreateImage(ColorTarget(4), "target");

        RunFrame(Device, loop, frame =>
        {
            var graph = new RenderGraph(Device, pool);
            ImageHandle output = graph.ImportImage("Output", target, ImageState.Undefined, ImageState.ShaderRead, output: true);
            graph.AddPass("Clear", PassType.Graphics, pass => pass.WriteColor(output, new Vector4(0f, 0f, 1f, 1f)));
            graph.Execute(frame);
        });

        byte[] pixels = Device.ReadImage(target, ImageState.ShaderRead);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);
    }

    [Fact]
    public void DisjointTransientsShareOneImageAtRunTime()
    {
        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();
        using GpuImage target = Device.CreateImage(ColorTarget(), "target");
        var handles = new List<GpuImage>();

        RunFrame(Device, loop, frame =>
        {
            var graph = new RenderGraph(Device, pool);
            ImageHandle output = graph.ImportImage("Output", target, ImageState.Undefined, ImageState.TransferSource, output: true);
            ImageHandle a = graph.CreateImage("a", ColorTarget());
            ImageHandle b = graph.CreateImage("b", ColorTarget());

            graph.AddPass("Make a", PassType.Graphics, pass => pass.WriteColor(a, Vector4.One));
            graph.AddPass("Use a", PassType.Graphics, pass => { pass.ReadSampled(a); pass.WriteColor(output, Vector4.Zero); pass.Execute(c => handles.Add(c.Image(a).Image)); });
            graph.AddPass("Make b", PassType.Graphics, pass => pass.WriteColor(b, Vector4.One));
            graph.AddPass("Use b", PassType.Graphics, pass => { pass.ReadSampled(b); pass.WriteColor(output); pass.Execute(c => handles.Add(c.Image(b).Image)); });
            graph.Execute(frame);
        });

        Assert.Equal(2, handles.Count);
        Assert.Same(handles[0], handles[1]);
        Assert.Equal(1, pool.ImageCount);
    }

    [Fact]
    public void ASteadyStateGraphAllocatesNothingAfterTheFirstFrames()
    {
        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();
        using GpuImage target = Device.CreateImage(ColorTarget(), "target");
        int baseline = Device.Bindless.SampledImageCount;
        var counts = new List<int>();

        for (int i = 0; i < 8; i++)
        {
            RunFrame(Device, loop, frame =>
            {
                var graph = new RenderGraph(Device, pool);
                ImageHandle output = graph.ImportImage("Output", target, i == 0 ? ImageState.Undefined : ImageState.TransferSource, ImageState.TransferSource, output: true);
                ImageHandle scratch = graph.CreateImage("Scratch", ColorTarget());
                BufferHandle buffer = graph.CreateBuffer("List", 256, BufferUsage.Storage);
                graph.AddPass("Scratch", PassType.Graphics, pass => pass.WriteColor(scratch, Vector4.One));
                graph.AddPass("Buffer", PassType.Compute, pass => { pass.Write(buffer); pass.NoCull(); });
                graph.AddPass("Output", PassType.Graphics, pass => { pass.ReadSampled(scratch); pass.Read(buffer); pass.WriteColor(output, Vector4.Zero); });
                graph.Execute(frame);
            });
            counts.Add(pool.ImageCount + pool.BufferCount);
        }

        // One image and one buffer per frame slot, found again every frame.
        Assert.Equal(4, counts[^1]);
        Assert.Equal(counts[3], counts[^1]);
        Assert.Equal(baseline + 2, Device.Bindless.SampledImageCount);   // each slot's scratch image has its bindless entry

        pool.Dispose();
        Assert.Equal(baseline, Device.Bindless.SampledImageCount);
    }

    [Fact]
    public void PassesAreTimedByName()
    {
        if (!Device.SupportsTimestamps)
        {
            Assert.Skip("This GPU has no timestamps.");
        }

        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();
        using GpuImage target = Device.CreateImage(ColorTarget(), "target");

        for (int i = 0; i < 4; i++)
        {
            Frame frame = loop.BeginFrame()!;
            var graph = new RenderGraph(Device, pool);
            ImageHandle output = graph.ImportImage("Output", target, i == 0 ? ImageState.Undefined : ImageState.TransferSource, ImageState.TransferSource, output: true);
            graph.AddPass("Clear target", PassType.Graphics, pass => pass.WriteColor(output, Vector4.One));
            graph.Execute(frame);
            loop.EndFrame(frame);
        }

        loop.WaitIdle();
        loop.EndFrame(loop.BeginFrame()!);
        Assert.Contains("Clear target", loop.Timings.Select(t => t.Name));
    }

    [Fact]
    public void ADepthPrepassAndADepthReadingPassRunWithoutValidationErrors()
    {
        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();
        using GpuImage target = Device.CreateImage(ColorTarget(16), "target");

        RunFrame(Device, loop, frame =>
        {
            var graph = new RenderGraph(Device, pool);
            ImageHandle output = graph.ImportImage("Output", target, ImageState.Undefined, ImageState.TransferSource, output: true);
            ImageHandle depth = graph.CreateImage("Depth", new ImageDescription(PixelFormat.D32Sfloat, 16, 16, ImageUsage.DepthStencilAttachment | ImageUsage.Sampled));
            graph.AddPass("Depth prepass", PassType.Graphics, pass => pass.WriteDepth(depth));
            graph.AddPass("Opaque", PassType.Graphics, pass => { pass.ReadDepth(depth); pass.WriteColor(output, Vector4.Zero); });
            graph.Execute(frame);
        });
    }

    [Fact]
    public void AGraphWithoutADeviceCannotExecute()
    {
        using var loop = Device.CreateFrameLoop();
        Frame frame = loop.BeginFrame()!;
        try
        {
            Assert.Throws<InvalidOperationException>(() => new RenderGraph().Execute(frame));
        }
        finally
        {
            loop.EndFrame(frame);
        }
    }
}
