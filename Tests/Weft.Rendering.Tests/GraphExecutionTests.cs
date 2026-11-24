using Weft.Core.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Weft.Graphics;
using Weft.Graphics.Shaders;
using Weft.Graphics.Tests;
using Weft.Native.Vulkan;
using Weft.Rendering.Graph;
using Xunit;

namespace Weft.Rendering.Tests;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MipPush
    {
        public uint Source;
        public uint Destination;
        public uint Size;
        public float Value;
        public ulong Output;
    }

    private const string MipShader = """
        [[vk::binding(0, 0)]] Texture2D<float4> gTextures[];
        [[vk::binding(0, 0)]] Texture2DArray<float4> gArrays[];
        [[vk::binding(2, 0)]] RWTexture2D<float4> gStorage[];

        struct Push { uint source; uint destination; uint size; float value; float* output; };
        [[vk::push_constant]] ConstantBuffer<Push> push;

        // The first level: each texel is x + y.
        [shader("compute")] [numthreads(8, 8, 1)]
        void patternMain(uint3 id : SV_DispatchThreadID)
        {
            if (id.x < push.size && id.y < push.size)
                gStorage[push.destination][id.xy] = float4(id.x + id.y, 0.0, 0.0, 1.0);
        }

        // A level is the average of 2x2 texels of the one before, read through a view of that level alone.
        [shader("compute")] [numthreads(8, 8, 1)]
        void downMain(uint3 id : SV_DispatchThreadID)
        {
            if (id.x < push.size && id.y < push.size)
            {
                int2 p = int2(id.xy) * 2;
                float sum = gTextures[push.source].Load(int3(p, 0)).x + gTextures[push.source].Load(int3(p + int2(1, 0), 0)).x
                          + gTextures[push.source].Load(int3(p + int2(0, 1), 0)).x + gTextures[push.source].Load(int3(p + int2(1, 1), 0)).x;
                gStorage[push.destination][id.xy] = float4(sum * 0.25, 0.0, 0.0, 1.0);
            }
        }

        [shader("compute")] [numthreads(8, 8, 1)]
        void constMain(uint3 id : SV_DispatchThreadID)
        {
            if (id.x < push.size && id.y < push.size)
                gStorage[push.destination][id.xy] = float4(push.value, 0.0, 0.0, 1.0);
        }

        [shader("compute")] [numthreads(1, 1, 1)]
        void readMain()
        {
            push.output[0] = gTextures[push.source].Load(int3(0, 0, 0)).x;
        }

        [shader("compute")] [numthreads(1, 1, 1)]
        void readLayersMain()
        {
            push.output[0] = gArrays[push.source].Load(int4(0, 0, 0, 0)).x;
            push.output[1] = gArrays[push.source].Load(int4(0, 0, 1, 0)).x;
        }
        """;

    /// <summary>Builds a mip chain of an 8x8 R32F image in four passes, each reading the level before through a view of that level and writing its own through another, and checks every level against the same maths on the CPU.</summary>
    private static void BuildChain(GraphicsDevice device, TransientPool pool, FrameLoop loop, GpuImage image, Pipeline pattern, Pipeline down)
    {
        RunFrame(device, loop, frame =>
        {
            var graph = new RenderGraph(device, pool);
            ImageHandle chain = graph.ImportImage("Chain", image, ImageState.Undefined, ImageState.ShaderRead, output: true);

            graph.AddPass("Level 0", PassType.Compute, pass =>
            {
                pass.WriteStorage(chain, SubresourceRange.Mip(0));
                pass.Execute(context =>
                {
                    context.Recorder.BindPipeline(pattern);
                    context.Recorder.PushConstants(new MipPush { Destination = context.Image(chain, SubresourceRange.Mip(0)).StorageIndex, Size = 8 });
                    context.Recorder.Dispatch(1, 1);
                });
            });
            for (uint level = 1; level < 4; level++)
            {
                uint mip = level;
                graph.AddPass("Level " + mip, PassType.Compute, pass =>
                {
                    pass.ReadSampled(chain, SubresourceRange.Mip(mip - 1));
                    pass.WriteStorage(chain, SubresourceRange.Mip(mip));
                    pass.Execute(context =>
                    {
                        ResolvedImage source = context.Image(chain, SubresourceRange.Mip(mip - 1));
                        ResolvedImage destination = context.Image(chain, SubresourceRange.Mip(mip));
                        Assert.Equal(8u >> (int)mip, destination.Width);
                        context.Recorder.BindPipeline(down);
                        context.Recorder.PushConstants(new MipPush { Source = source.SampledIndex, Destination = destination.StorageIndex, Size = destination.Width });
                        context.Recorder.Dispatch(1, 1);
                    });
                });
            }

            graph.Execute(frame);
            Assert.Equal(4, graph.Plan!.LivePassCount);
        });
    }

    [Fact]
    public void AMipChainIsWrittenALevelAtATimeThroughViewsOfOneImportedImage()
    {
        CompiledShader shader = Compiler().CompileSource("mips", MipShader);
        using Pipeline pattern = Device.CreateComputePipeline(shader.Code("patternMain"), "pattern");
        using Pipeline down = Device.CreateComputePipeline(shader.Code("downMain"), "down");
        using GpuImage image = Device.CreateImage(new ImageDescription(PixelFormat.R32Sfloat, 8, 8, ImageUsage.Storage | ImageUsage.Sampled, MipLevels: 4), "chain");
        int sampledBaseline = Device.Bindless.SampledImageCount;
        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();

        // Two frames, so that the views of the first are made again and the ones of the frame slot before are destroyed.
        BuildChain(Device, pool, loop, image, pattern, down);
        BuildChain(Device, pool, loop, image, pattern, down);

        float[,] expected = new float[8, 8];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                expected[y, x] = x + y;
            }
        }

        for (uint mip = 0; mip < 4; mip++)
        {
            int size = 8 >> (int)mip;
            float[] texels = new float[size * size];
            Buffer.BlockCopy(Device.ReadImage(image, ImageState.ShaderRead, mip), 0, texels, 0, texels.Length * 4);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Assert.Equal(expected[y, x], texels[(y * size) + x], 0.0001f);
                }
            }

            // The next level: the average of 2x2 texels.
            float[,] next = new float[Math.Max(1, size / 2), Math.Max(1, size / 2)];
            for (int y = 0; y < size / 2; y++)
            {
                for (int x = 0; x < size / 2; x++)
                {
                    next[y, x] = (expected[2 * y, 2 * x] + expected[2 * y, (2 * x) + 1] + expected[(2 * y) + 1, 2 * x] + expected[(2 * y) + 1, (2 * x) + 1]) * 0.25f;
                }
            }

            expected = next;
        }

        pool.Dispose();
        Assert.Equal(sampledBaseline, Device.Bindless.SampledImageCount);   // every view's bindless entry went with it
    }

    [Fact]
    public void ATransientWithMipsAndLayersHasViewsPerPartAndIsUsedByPart()
    {
        CompiledShader shader = Compiler().CompileSource("mips", MipShader);
        using Pipeline pattern = Device.CreateComputePipeline(shader.Code("patternMain"), "pattern");
        using Pipeline down = Device.CreateComputePipeline(shader.Code("downMain"), "down");
        using Pipeline read = Device.CreateComputePipeline(shader.Code("readMain"), "read");
        using GpuBuffer output = Device.CreateBuffer(16, BufferUsage.Storage, MemoryKind.DeviceLocal, "output");
        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();

        RunFrame(Device, loop, frame =>
        {
            var graph = new RenderGraph(Device, pool);
            ImageHandle chain = graph.CreateImage("Chain", new ImageDescription(PixelFormat.R32Sfloat, 8, 8, ImageUsage.Storage | ImageUsage.Sampled, MipLevels: 4));
            BufferHandle result = graph.ImportBuffer("Output", output, BufferState.Undefined, BufferState.TransferSource, output: true);

            graph.AddPass("Level 0", PassType.Compute, pass =>
            {
                pass.WriteStorage(chain, SubresourceRange.Mip(0));
                pass.Execute(context =>
                {
                    context.Recorder.BindPipeline(pattern);
                    context.Recorder.PushConstants(new MipPush { Destination = context.Image(chain, SubresourceRange.Mip(0)).StorageIndex, Size = 8 });
                    context.Recorder.Dispatch(1, 1);
                });
            });
            for (uint level = 1; level < 4; level++)
            {
                uint mip = level;
                graph.AddPass("Level " + mip, PassType.Compute, pass =>
                {
                    pass.ReadSampled(chain, SubresourceRange.Mip(mip - 1));
                    pass.WriteStorage(chain, SubresourceRange.Mip(mip));
                    pass.Execute(context =>
                    {
                        ResolvedImage source = context.Image(chain, SubresourceRange.Mip(mip - 1));
                        ResolvedImage destination = context.Image(chain, SubresourceRange.Mip(mip));
                        Assert.NotEqual(0u, source.SampledIndex);
                        Assert.NotEqual(0u, destination.StorageIndex);
                        context.Recorder.BindPipeline(down);
                        context.Recorder.PushConstants(new MipPush { Source = source.SampledIndex, Destination = destination.StorageIndex, Size = destination.Width });
                        context.Recorder.Dispatch(1, 1);
                    });
                });
            }

            graph.AddPass("Read the top", PassType.Compute, pass =>
            {
                pass.ReadSampled(chain, SubresourceRange.Mip(3));
                pass.Write(result);
                pass.Execute(context =>
                {
                    context.Recorder.BindPipeline(read);
                    context.Recorder.PushConstants(new MipPush { Source = context.Image(chain, SubresourceRange.Mip(3)).SampledIndex, Output = context.Buffer(result).Address });
                    context.Recorder.Dispatch(1);
                });
            });
            graph.Execute(frame);
        });

        // Every level keeps the mean of the first, which is 7.
        Assert.Equal(7f, BitConverter.ToSingle(Device.ReadBuffer(output, 0, 4)), 0.0001f);
    }

    [Fact]
    public void LayersOfATransientArrayAreWrittenByDifferentPassesAndSampledTogether()
    {
        CompiledShader shader = Compiler().CompileSource("mips", MipShader);
        using Pipeline constant = Device.CreateComputePipeline(shader.Code("constMain"), "const");
        using Pipeline readLayers = Device.CreateComputePipeline(shader.Code("readLayersMain"), "layers");
        using GpuBuffer output = Device.CreateBuffer(16, BufferUsage.Storage, MemoryKind.DeviceLocal, "output");
        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();

        RunFrame(Device, loop, frame =>
        {
            var graph = new RenderGraph(Device, pool);
            ImageHandle array = graph.CreateImage("Array", new ImageDescription(PixelFormat.R32Sfloat, 4, 4, ImageUsage.Storage | ImageUsage.Sampled, ArrayLayers: 2));
            BufferHandle result = graph.ImportBuffer("Output", output, BufferState.Undefined, BufferState.TransferSource, output: true);

            for (uint layer = 0; layer < 2; layer++)
            {
                uint index = layer;
                graph.AddPass("Layer " + index, PassType.Compute, pass =>
                {
                    pass.WriteStorage(array, SubresourceRange.Layer(index));
                    pass.Execute(context =>
                    {
                        context.Recorder.BindPipeline(constant);
                        context.Recorder.PushConstants(new MipPush { Destination = context.Image(array, SubresourceRange.Layer(index)).StorageIndex, Size = 4, Value = index + 1 });
                        context.Recorder.Dispatch(1, 1);
                    });
                });
            }

            graph.AddPass("Read both", PassType.Compute, pass =>
            {
                pass.ReadSampled(array);
                pass.Write(result);
                pass.Execute(context =>
                {
                    context.Recorder.BindPipeline(readLayers);
                    context.Recorder.PushConstants(new MipPush { Source = context.Image(array).SampledIndex, Output = context.Buffer(result).Address });
                    context.Recorder.Dispatch(1);
                });
            });
            graph.Execute(frame);
        });

        float[] values = new float[2];
        Buffer.BlockCopy(Device.ReadBuffer(output, 0, 8), 0, values, 0, 8);
        Assert.Equal([1f, 2f], values);
    }

    [Fact]
    public void AnAttachmentCanBeOneMipOfAnImportedImage()
    {
        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();
        using GpuImage target = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 4, 4, ImageUsage.ColorAttachment | ImageUsage.Sampled, MipLevels: 3), "mips");

        RunFrame(Device, loop, frame =>
        {
            var graph = new RenderGraph(Device, pool);
            ImageHandle output = graph.ImportImage("Output", target, ImageState.Undefined, ImageState.ShaderRead, output: true);
            graph.AddPass("Mip 1", PassType.Graphics, pass =>
            {
                pass.WriteColor(output, new Vector4(0f, 0f, 1f, 1f), SubresourceRange.Mip(1));
                pass.Execute(context => Assert.Equal(2u, context.Image(output, SubresourceRange.Mip(1)).Width));
            });
            graph.Execute(frame);
        });

        byte[] pixels = Device.ReadImage(target, ImageState.ShaderRead, mipLevel: 1);
        Assert.Equal(16, pixels.Length);                      // 2x2 texels: the attachment was the size of the level
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);
    }

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
