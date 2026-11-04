using Apogee.Core.Graphics;
using System.Numerics;
using System.Runtime.InteropServices;
using Apogee.Graphics.Shaders;
using Apogee.Native.Vulkan;
using Xunit;

namespace Apogee.Graphics.Tests;

public unsafe class PipelineTests(GpuFixture gpu) : GpuTest(gpu)
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ComputePush
    {
        public ulong Output;
        public uint Count;
        public float Scale;
    }

    private const string Compute = """
        struct Push { float* output; uint count; float scale; };
        [[vk::push_constant]] ConstantBuffer<Push> push;

        [shader("compute")] [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            if (id.x < push.count)
                push.output[id.x] = id.x * push.scale;
        }
        """;

    private const string Fullscreen = """
        struct Push { float4 color; };
        [[vk::push_constant]] ConstantBuffer<Push> push;

        [shader("vertex")]
        float4 vsMain(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * 2.0 - 1.0, 0.0, 1.0);
        }

        [shader("fragment")]
        float4 fsMain() : SV_Target { return push.color; }
        """;

    [Fact]
    public void AComputeShaderWritesThroughABufferAddressInPushConstants()
    {
        CompiledShader shader = Compiler().CompileSource("scale", Compute);
        using Pipeline pipeline = Device.CreateComputePipeline(shader.Code("main"), "scale");
        const int count = 1000;
        using GpuBuffer output = Device.CreateBuffer(count * sizeof(float), BufferUsage.Storage, MemoryKind.DeviceLocal, "output");

        Device.Immediate.Submit(recorder =>
        {
            recorder.BindPipeline(pipeline);
            recorder.PushConstants(new ComputePush { Output = output.Address, Count = count, Scale = 0.5f });
            recorder.Dispatch((count + 63) / 64);
        });

        float[] values = new float[count];
        System.Buffer.BlockCopy(Device.ReadBuffer(output, 0, count * sizeof(float)), 0, values, 0, count * sizeof(float));
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i * 0.5f, values[i]);
        }
    }

    [Fact]
    public void AFullscreenTriangleFillsAnOffscreenTarget()
    {
        CompiledShader shader = Compiler().CompileSource("fill", Fullscreen);
        using Pipeline pipeline = Device.CreateGraphicsPipeline(
            new GraphicsPipelineDescription(shader.Code("vsMain"), shader.Code("fsMain"), [PixelFormat.R8G8B8A8Unorm], CullMode: CullMode.None),
            "fill");
        using GpuImage target = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 4, 4, ImageUsage.ColorAttachment), "target");

        Device.Immediate.Submit(recorder =>
        {
            using (recorder.Label("Fill"))
            {
                recorder.Barrier(ImageBarrier.For(target, ImageState.Undefined, ImageState.ColorAttachment));
                recorder.BeginRendering(4, 4, [new ColorTarget(target.View, Clear: true, ClearColor: new Vector4(0, 0, 0, 1))]);
                recorder.BindPipeline(pipeline);
                recorder.PushConstants(new Vector4(1f, 0f, 1f, 1f));
                recorder.Draw(3);
                recorder.EndRendering();
            }
        });

        byte[] pixels = Device.ReadImage(target, ImageState.ColorAttachment);
        Assert.Equal(64, pixels.Length);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(new byte[] { 255, 0, 255, 255 }, pixels[(i * 4)..(i * 4 + 4)]);
        }
    }

    [Fact]
    public void ClearingWithoutDrawingLeavesTheClearColor()
    {
        using GpuImage target = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 2, 2, ImageUsage.ColorAttachment), "cleared");

        Device.Immediate.Submit(recorder =>
        {
            recorder.Barrier(ImageBarrier.For(target, ImageState.Undefined, ImageState.ColorAttachment));
            recorder.BeginRendering(2, 2, [new ColorTarget(target.View, Clear: true, ClearColor: new Vector4(0f, 1f, 0f, 1f))]);
            recorder.EndRendering();
        });

        byte[] pixels = Device.ReadImage(target, ImageState.ColorAttachment);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels[..4]);
    }

    [Fact]
    public void DepthTestingKeepsTheNearerTriangle()
    {
        const string source = """
            struct Push { float4 color; float depth; };
            [[vk::push_constant]] ConstantBuffer<Push> push;

            [shader("vertex")]
            float4 vsMain(uint id : SV_VertexID) : SV_Position
            {
                float2 uv = float2((id << 1) & 2, id & 2);
                return float4(uv * 2.0 - 1.0, push.depth, 1.0);
            }

            [shader("fragment")]
            float4 fsMain() : SV_Target { return push.color; }
            """;
        CompiledShader shader = Compiler().CompileSource("depth", source);
        using Pipeline pipeline = Device.CreateGraphicsPipeline(
            new GraphicsPipelineDescription(
                shader.Code("vsMain"),
                shader.Code("fsMain"),
                [PixelFormat.R8G8B8A8Unorm],
                PixelFormat.D32Sfloat,
                CullMode: CullMode.None,
                DepthTest: true,
                DepthWrite: true),
            "depth");
        using GpuImage color = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 2, 2, ImageUsage.ColorAttachment), "color");
        using GpuImage depth = Device.CreateImage(new ImageDescription(PixelFormat.D32Sfloat, 2, 2, ImageUsage.DepthStencilAttachment), "depth");

        Device.Immediate.Submit(recorder =>
        {
            recorder.Barriers([ImageBarrier.For(color, ImageState.Undefined, ImageState.ColorAttachment), ImageBarrier.For(depth, ImageState.Undefined, ImageState.DepthAttachment)]);
            recorder.BeginRendering(2, 2, [new ColorTarget(color.View, Clear: true)], new DepthTarget(depth.View, Clear: true, ClearDepth: 0f));
            recorder.BindPipeline(pipeline);

            // Reverse-Z: larger is nearer. Draw the near (0.8, red) triangle first, then the far (0.2, blue) one, which must lose.
            recorder.PushConstants(new Vector4(1f, 0f, 0f, 1f).WithDepth(0.8f));
            recorder.Draw(3);
            recorder.PushConstants(new Vector4(0f, 0f, 1f, 1f).WithDepth(0.2f));
            recorder.Draw(3);
            recorder.EndRendering();
        });

        byte[] pixels = Device.ReadImage(color, ImageState.ColorAttachment);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[..4]);
    }
}

internal static class PushExtensions
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ColorAndDepth
    {
        public Vector4 Color;
        public float Depth;
    }

    public static ColorAndDepth WithDepth(this Vector4 color, float depth) => new() { Color = color, Depth = depth };
}
