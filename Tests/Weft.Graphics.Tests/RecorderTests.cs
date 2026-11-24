using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Weft.Core.Graphics;
using Weft.Graphics.Shaders;
using Xunit;

namespace Weft.Graphics.Tests;

/// <summary>The recorder's buffer fill, image clears and indirect calls: what GPU-driven passes are made of.</summary>
public unsafe class RecorderTests(GpuFixture gpu) : GpuTest(gpu)
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Pointers
    {
        public ulong Output;
        public ulong Arguments;
    }

    private const string WriteArguments = """
        struct Push { uint* output; uint* args; };
        [[vk::push_constant]] ConstantBuffer<Push> push;

        [shader("compute")] [numthreads(1, 1, 1)]
        void main()
        {
            push.args[0] = 3;       // groups of the dispatch that follows
            push.args[1] = 1;
            push.args[2] = 1;
        }
        """;

    private const string Count = """
        struct Push { uint* output; uint* args; };
        [[vk::push_constant]] ConstantBuffer<Push> push;

        [shader("compute")] [numthreads(4, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            push.output[id.x] = id.x + 1;
        }
        """;

    private const string Triangles = """
        struct Push { float4 color; };
        [[vk::push_constant]] ConstantBuffer<Push> push;

        // Indices 0..2: the half of the target above the diagonal (the top left); 3..5: the other half.
        static const float2 positions[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(1, 1), float2(-1, 1), float2(1, -1) };

        [shader("vertex")]
        float4 vsMain(uint id : SV_VertexID) : SV_Position { return float4(positions[id], 0.0, 1.0); }

        [shader("fragment")]
        float4 fsMain() : SV_Target { return push.color; }
        """;

    private static uint[] ToWords(byte[] bytes)
    {
        uint[] words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        return words;
    }

    private uint[] FilledBuffer(ulong size, Action<CommandRecorder, GpuBuffer> record)
    {
        using GpuBuffer buffer = Device.CreateBuffer(size, BufferUsage.Storage, MemoryKind.DeviceLocal, "filled");
        Device.Immediate.Submit(recorder =>
        {
            recorder.Barriers([], [new BufferBarrier(buffer, BufferState.Undefined, BufferState.TransferDestination)]);
            record(recorder, buffer);
            recorder.Barriers([], [new BufferBarrier(buffer, BufferState.TransferDestination, BufferState.ShaderWrite)]);
        });
        return ToWords(Device.ReadBuffer(buffer, 0, (int)size));
    }

    [Fact]
    public void FillBufferSetsAWholeBufferToAByteRepeatingPattern()
    {
        uint[] zeros = FilledBuffer(64, (recorder, buffer) => recorder.FillBuffer(buffer));
        uint[] ones = FilledBuffer(64, (recorder, buffer) => recorder.FillBuffer(buffer, 0xFFFFFFFF));

        Assert.All(zeros, word => Assert.Equal(0u, word));
        Assert.All(ones, word => Assert.Equal(0xFFFFFFFFu, word));
    }

    [Fact]
    public void FillBufferTouchesOnlyTheRangeItIsGiven()
    {
        uint[] words = FilledBuffer(64, (recorder, buffer) =>
        {
            recorder.FillBuffer(buffer, 0x01010101);
            recorder.Barriers([], [new BufferBarrier(buffer, BufferState.TransferDestination, BufferState.TransferDestination)]);
            recorder.FillBuffer(buffer, 0x02020202, offset: 16, size: 8);
        });

        for (int i = 0; i < words.Length; i++)
        {
            Assert.Equal(i is 4 or 5 ? 0x02020202u : 0x01010101u, words[i]);
        }
    }

    [Fact]
    public void FillBufferWritesAnyWordOnVulkan()
    {
        RequireVulkan();

        uint[] words = FilledBuffer(32, (recorder, buffer) => recorder.FillBuffer(buffer, 0xDEADBEEF));

        Assert.All(words, word => Assert.Equal(0xDEADBEEFu, word));
    }

    [Fact]
    public void FillBufferRejectsAnUnalignedRange()
    {
        using GpuBuffer buffer = Device.CreateBuffer(64, BufferUsage.Storage, MemoryKind.DeviceLocal, "aligned");

        Device.Immediate.Submit(recorder =>
        {
            Assert.Throws<ArgumentException>(() => recorder.FillBuffer(buffer, 0, offset: 2));
            Assert.Throws<ArgumentException>(() => recorder.FillBuffer(buffer, 0, size: 6));
        });
    }

    private static ImageDescription Chain(PixelFormat format = PixelFormat.R8G8B8A8Unorm, ImageUsage usage = ImageUsage.ColorAttachment | ImageUsage.Sampled) =>
        new(format, 4, 4, usage, MipLevels: 3, ArrayLayers: 2);

    [Fact]
    public void ClearImageSetsEverySubresourceOrTheRangeItIsGiven()
    {
        using GpuImage image = Device.CreateImage(Chain(), "cleared");

        Device.Immediate.Submit(recorder =>
        {
            recorder.Barrier(ImageBarrier.For(image, ImageState.Undefined, ImageState.TransferDestination));
            recorder.ClearImage(image, new Vector4(1f, 0f, 0f, 1f));
            recorder.Barrier(ImageBarrier.For(image, ImageState.TransferDestination, ImageState.TransferDestination, baseMip: 1, mipCount: 1, baseLayer: 1, layerCount: 1));
            recorder.ClearImage(image, new Vector4(0f, 0f, 1f, 1f), baseMip: 1, mipCount: 1, baseLayer: 1, layerCount: 1);
            recorder.Barrier(ImageBarrier.For(image, ImageState.TransferDestination, ImageState.ShaderRead));
        });

        byte[] red = [255, 0, 0, 255];
        byte[] blue = [0, 0, 255, 255];
        Assert.Equal(red, Device.ReadImage(image, ImageState.ShaderRead, mipLevel: 0, layer: 0)[..4]);
        Assert.Equal(red, Device.ReadImage(image, ImageState.ShaderRead, mipLevel: 0, layer: 1)[..4]);
        Assert.Equal(red, Device.ReadImage(image, ImageState.ShaderRead, mipLevel: 1, layer: 0)[..4]);
        Assert.Equal(blue, Device.ReadImage(image, ImageState.ShaderRead, mipLevel: 1, layer: 1)[..4]);
        Assert.Equal(red, Device.ReadImage(image, ImageState.ShaderRead, mipLevel: 2, layer: 1)[..4]);
    }

    [Fact]
    public void ClearDepthImageSetsTheDepth()
    {
        using GpuImage image = Device.CreateImage(
            new ImageDescription(PixelFormat.D32Sfloat, 4, 4, ImageUsage.DepthStencilAttachment | ImageUsage.Sampled), "depth");

        Device.Immediate.Submit(recorder =>
        {
            recorder.Barrier(ImageBarrier.For(image, ImageState.Undefined, ImageState.TransferDestination));
            recorder.ClearDepthImage(image, 0.25f);
            recorder.Barrier(ImageBarrier.For(image, ImageState.TransferDestination, ImageState.DepthRead));
        });

        float[] texels = new float[16];
        Buffer.BlockCopy(Device.ReadImage(image, ImageState.DepthRead), 0, texels, 0, 64);
        Assert.All(texels, texel => Assert.Equal(0.25f, texel));
    }

    [Fact]
    public void DispatchIndirectRunsTheGroupsAPassWroteIntoABuffer()
    {
        CompiledShader write = Compiler().CompileSource("write_args", WriteArguments);
        CompiledShader count = Compiler().CompileSource("count", Count);
        using Pipeline writePipeline = Device.CreateComputePipeline(write.Code("main"), "write args");
        using Pipeline countPipeline = Device.CreateComputePipeline(count.Code("main"), "count");
        using GpuBuffer output = Device.CreateBuffer(64 * sizeof(uint), BufferUsage.Storage, MemoryKind.DeviceLocal, "output");
        using GpuBuffer args = Device.CreateBuffer(DispatchIndirectCommand.Size, BufferUsage.Storage | BufferUsage.Indirect, MemoryKind.DeviceLocal, "args");

        Device.Immediate.Submit(recorder =>
        {
            recorder.Barriers([], [new BufferBarrier(output, BufferState.Undefined, BufferState.TransferDestination), new BufferBarrier(args, BufferState.Undefined, BufferState.ShaderWrite)]);
            recorder.FillBuffer(output);
            recorder.BindPipeline(writePipeline);
            recorder.PushConstants(new Pointers { Output = output.Address, Arguments = args.Address });
            recorder.Dispatch(1);
            recorder.Barriers([], [new BufferBarrier(output, BufferState.TransferDestination, BufferState.ShaderWrite), new BufferBarrier(args, BufferState.ShaderWrite, BufferState.IndirectRead)]);
            recorder.BindPipeline(countPipeline);
            recorder.PushConstants(new Pointers { Output = output.Address, Arguments = args.Address });
            recorder.DispatchIndirect(args);
        });

        uint[] words = ToWords(Device.ReadBuffer(output, 0, 64 * sizeof(uint)));
        for (int i = 0; i < words.Length; i++)
        {
            Assert.Equal(i < 12 ? (uint)(i + 1) : 0u, words[i]);     // three groups of four threads
        }
    }

    /// <summary>Draws the given indirect commands into a 4x4 target and says which of its corners turned red: the top left one is the first command's triangle, the bottom right one the second's.</summary>
    private (bool TopLeft, bool BottomRight) Draw(DrawIndexedIndirectCommand[] commands, uint count, bool useCountBuffer, uint maxDrawCount)
    {
        CompiledShader shader = Compiler().CompileSource("triangles", Triangles);
        using Pipeline pipeline = Device.CreateGraphicsPipeline(
            new GraphicsPipelineDescription(shader.Code("vsMain"), shader.Code("fsMain"), [PixelFormat.R8G8B8A8Unorm], CullMode: CullMode.None),
            "triangles");
        using GpuImage target = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 4, 4, ImageUsage.ColorAttachment), "target");
        using GpuBuffer indices = Device.CreateBuffer(MemoryMarshal.AsBytes<uint>([0, 1, 2, 3, 4, 5]), BufferUsage.Index, "indices");
        using GpuBuffer indirect = Device.CreateBuffer(MemoryMarshal.AsBytes<DrawIndexedIndirectCommand>(commands), BufferUsage.Indirect, "commands");
        using GpuBuffer counter = Device.CreateBuffer(BitConverter.GetBytes(count), BufferUsage.Indirect, "count");

        Device.Immediate.Submit(recorder =>
        {
            recorder.Barrier(ImageBarrier.For(target, ImageState.Undefined, ImageState.ColorAttachment));
            recorder.Barriers([], [
                new BufferBarrier(indirect, BufferState.ShaderRead, BufferState.IndirectRead),
                new BufferBarrier(counter, BufferState.ShaderRead, BufferState.IndirectRead),
                new BufferBarrier(indices, BufferState.ShaderRead, BufferState.VertexIndexRead),
            ]);
            recorder.BeginRendering(4, 4, [new ColorTarget(target.View, Clear: true, ClearColor: new Vector4(0, 0, 0, 1))]);
            recorder.BindPipeline(pipeline);
            recorder.PushConstants(new Vector4(1f, 0f, 0f, 1f));
            recorder.BindIndexBuffer(indices);
            if (useCountBuffer)
            {
                recorder.DrawIndexedIndirectCount(indirect, 0, counter, 0, maxDrawCount);
            }
            else
            {
                recorder.DrawIndexedIndirect(indirect, 0, maxDrawCount);
            }

            recorder.EndRendering();
        });

        byte[] pixels = Device.ReadImage(target, ImageState.ColorAttachment);
        return (pixels[0] == 255, pixels[(15 * 4)] == 255);   // the red channel of pixel (0, 0) and of pixel (3, 3)
    }

    private static readonly DrawIndexedIndirectCommand[] s_twoTriangles =
    [
        new(IndexCount: 3, InstanceCount: 1, FirstIndex: 0, VertexOffset: 0, FirstInstance: 0),
        new(IndexCount: 3, InstanceCount: 1, FirstIndex: 3, VertexOffset: 0, FirstInstance: 0),
    ];

    [Fact]
    public void DrawIndexedIndirectMakesEveryDrawOfTheBuffer()
    {
        Assert.Equal((true, true), Draw(s_twoTriangles, 2, useCountBuffer: false, maxDrawCount: 2));
    }

    [Fact]
    public void ACommandWithNoInstancesDrawsNothing()
    {
        DrawIndexedIndirectCommand[] commands = [s_twoTriangles[0], s_twoTriangles[1] with { InstanceCount = 0 }];

        Assert.Equal((true, false), Draw(commands, 2, useCountBuffer: false, maxDrawCount: 2));
    }

    [Fact]
    public void DrawIndexedIndirectCountStopsAtTheCountInTheBuffer()
    {
        if (!Device.SupportsDrawIndirectCount)
        {
            Assert.Skip("This device has no drawIndirectCount.");
        }

        Assert.Equal((true, false), Draw(s_twoTriangles, 1, useCountBuffer: true, maxDrawCount: 2));
        Assert.Equal((true, true), Draw(s_twoTriangles, 2, useCountBuffer: true, maxDrawCount: 2));
        Assert.Equal((true, false), Draw(s_twoTriangles, 9, useCountBuffer: true, maxDrawCount: 1));     // capped by the maximum
    }

    [Fact]
    public void WithoutDrawIndirectCountTheCallSaysSo()
    {
        if (Device.SupportsDrawIndirectCount)
        {
            Assert.Skip("This device has drawIndirectCount.");
        }

        Assert.Throws<NotSupportedException>(() => Draw(s_twoTriangles, 1, useCountBuffer: true, maxDrawCount: 2));
    }

    [Fact]
    public void AMinMaxSamplerReturnsTheSmallestOrLargestTexelOfItsFootprint()
    {
        if (!Device.SupportsSamplerFilterMinMax)
        {
            Assert.Throws<GraphicsException>(() => Device.CreateSampler(SamplerDescription.MinMax(SamplerReduction.Max)));
            Assert.Skip("This device has no min/max samplers.");
        }

        const string source = """
            [[vk::binding(0, 0)]] Texture2D<float4> gTextures[];
            [[vk::binding(1, 0)]] SamplerState gSamplers[];

            struct Push { uint texture; uint sampler; float* output; };
            [[vk::push_constant]] ConstantBuffer<Push> push;

            [shader("compute")] [numthreads(1, 1, 1)]
            void main()
            {
                push.output[0] = gTextures[push.texture].SampleLevel(gSamplers[push.sampler], float2(0.5, 0.5), 0).r;
            }
            """;
        CompiledShader shader = Compiler().CompileSource("minmax", source);
        using Pipeline pipeline = Device.CreateComputePipeline(shader.Code("main"), "minmax");
        using GpuImage texture = Device.CreateImage(new ImageDescription(PixelFormat.R32Sfloat, 2, 2, ImageUsage.Sampled), "texels");
        Device.UploadImage(texture, MemoryMarshal.AsBytes<float>([0.1f, 0.9f, 0.4f, 0.2f]), [new ImageRegion(0, 0, 0, 2, 2)], ImageState.ShaderRead);
        uint textureIndex = Device.Bindless.AddSampledImage(texture);
        using GpuBuffer output = Device.CreateBuffer(4, BufferUsage.Storage, MemoryKind.DeviceLocal, "output");

        float Sample(SamplerReduction reduction)
        {
            using GpuSampler sampler = Device.CreateSampler(new SamplerDescription(Filter.Linear, MipmapMode.Nearest, AddressMode.ClampToEdge, Reduction: reduction));
            uint samplerIndex = Device.Bindless.AddSampler(sampler);
            Device.Immediate.Submit(recorder =>
            {
                recorder.Barriers([], [new BufferBarrier(output, BufferState.Undefined, BufferState.ShaderWrite)]);
                recorder.BindPipeline(pipeline);
                recorder.PushConstants(new SamplePush { Texture = textureIndex, Sampler = samplerIndex, Output = output.Address });
                recorder.Dispatch(1);
            });
            Device.Bindless.RemoveSampler(samplerIndex);
            return BitConverter.ToSingle(Device.ReadBuffer(output, 0, 4));
        }

        // Sampling the middle of four texels with linear filtering reads all of them.
        Assert.Equal(0.1f, Sample(SamplerReduction.Min));
        Assert.Equal(0.9f, Sample(SamplerReduction.Max));
        Assert.Equal(0.4f, Sample(SamplerReduction.WeightedAverage), 0.001f);
        Device.Bindless.RemoveSampledImage(textureIndex);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SamplePush
    {
        public uint Texture;
        public uint Sampler;
        public ulong Output;
    }
}
