using System;
using System.IO;
using System.Numerics;
using Weft.Core.Graphics;
using Weft.Graphics;
using Weft.Graphics.Shaders;
using Weft.Graphics.Tests;
using Weft.Rendering.Graph;
using Xunit;

namespace Weft.Rendering.Tests;

/// <summary>Where the engine's shader sources are, for tests that compile one directly.</summary>
internal static class ShaderFiles
{
    public static string Directory()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "Weft.slnx")))
            {
                return Path.Combine(directory, "Source", "Weft.Rendering", "Shaders");
            }
        }

        throw new InvalidOperationException("Weft.slnx not found above the test output.");
    }

    public static CompiledShader Compile(GraphicsDevice device, string file) =>
        new ShaderCompiler(new ShaderCompilerOptions { Backend = device.ShaderBackend, SearchPaths = [Directory()] }).CompileFile(Path.Combine(Directory(), file));
}

/// <summary>The tone-mapping shader against its CPU twin: an image of gradients goes through <c>tonemap.slang</c> and the result is compared pixel by pixel with <see cref="Gt7ToneMapping"/>.</summary>
public class ToneMappingGpuTests(GpuFixture gpu) : GpuTest(gpu)
{
    private const int Width = 128;
    private const int Height = 8;

    /// <summary>The color of each row, multiplied by the gradient along it.</summary>
    private static readonly Vector3[] s_rows =
    [
        new(1f, 1f, 1f),
        new(1f, 0f, 0f),
        new(0f, 1f, 0f),
        new(0f, 0f, 1f),
        new(1f, 0.5f, 0.1f),
        new(0.1f, 0.6f, 1f),
        new(1f, 0.9f, 0f),
        new(0.5f, 0.5f, 0.5f),
    ];

    /// <summary>The value of column <paramref name="x"/>: logarithmic from 0.002 to 30 (the exposed range of a very dark pixel to a very bright one); the last column is overridden with infinities.</summary>
    private static float Gradient(int x) => 0.002f * MathF.Pow(30f / 0.002f, x / (float)(Width - 1));

    private byte[] Run(Tonemapper tonemapper, out Vector3[] input)
    {
        input = new Vector3[Width * Height];
        byte[] texels = new byte[Width * Height * 8];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                // Through half precision, as in the HDR image, so the twin sees what the shader does.
                Vector3 color = s_rows[y] * Gradient(x);
                if (x == Width - 1)
                {
                    // The last column overflowed the half floats: an infinity in the channels that have light.
                    color = new Vector3(s_rows[y].X > 0f ? float.PositiveInfinity : 0f, s_rows[y].Y > 0f ? float.PositiveInfinity : 0f, s_rows[y].Z > 0f ? float.PositiveInfinity : 0f);
                }

                var half = new Half[] { (Half)color.X, (Half)color.Y, (Half)color.Z, (Half)1f };
                input[(y * Width) + x] = new Vector3((float)half[0], (float)half[1], (float)half[2]);
                for (int c = 0; c < 4; c++)
                {
                    BitConverter.TryWriteBytes(texels.AsSpan((((y * Width) + x) * 8) + (c * 2)), half[c]);
                }
            }
        }

        using GpuImage hdr = Device.CreateImage(new ImageDescription(PixelFormat.R16G16B16A16Sfloat, Width, Height, ImageUsage.Sampled), "tone mapping input");
        Device.UploadImage(hdr, texels, [new ImageRegion(0, 0, 0, Width, Height)], ImageState.ShaderRead);
        using GpuImage target = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, Width, Height, ImageUsage.ColorAttachment), "tone mapping output");
        // Nearest, as the renderer's tone mapping pass: the image is read 1:1, and a linear filter would multiply the infinite last column by a zero weight at its neighbour (NaN on some GPUs).
        using GpuSampler sampler = Device.CreateSampler(SamplerDescription.NearestClamp, "tone mapping sampler");
        uint hdrIndex = Device.Bindless.AddSampledImage(hdr);
        uint samplerIndex = Device.Bindless.AddSampler(sampler);

        CompiledShader shader = ShaderFiles.Compile(Device, "tonemap.slang");
        using Pipeline pipeline = Device.CreateGraphicsPipeline(
            new GraphicsPipelineDescription(shader.Code("fullscreenVs"), shader.Code("tonemapFs"), [PixelFormat.R8G8B8A8Unorm], CullMode: CullMode.None), "tone mapping");
        using var pool = new TransientPool(Device);
        using var loop = Device.CreateFrameLoop();

        Frame frame = loop.BeginFrame()!;
        var graph = new RenderGraph(Device, pool);
        ImageHandle source = graph.ImportImage("HDR", hdr, ImageState.ShaderRead, ImageState.ShaderRead, sampledIndex: hdrIndex);
        ImageHandle output = graph.ImportImage("Output", target, ImageState.Undefined, ImageState.TransferSource, output: true);
        graph.AddPass("Tonemap", PassType.Graphics, pass =>
        {
            pass.ReadSampled(source);
            pass.WriteColor(output, Vector4.Zero);
            pass.Execute(context =>
            {
                context.Recorder.BindPipeline(pipeline);
                context.Recorder.PushConstants(new GpuTonemapPush { Texture = hdrIndex, Sampler = samplerIndex, Tonemapper = (uint)tonemapper });
                context.Recorder.Draw(3);
            });
        });
        graph.Execute(frame);
        loop.EndFrame(frame);
        loop.WaitIdle();

        byte[] pixels = Device.ReadImage(target, ImageState.TransferSource);
        Device.Bindless.RemoveSampler(samplerIndex);
        Device.Bindless.RemoveSampledImage(hdrIndex);
        return pixels;
    }

    private static float Srgb(float v) => (v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1f / 2.4f)) - 0.055f) * 255f;

    [Fact]
    public void TheShaderMatchesTheGt7TwinOnAGradientImage()
    {
        byte[] pixels = Run(Tonemapper.Gt7, out Vector3[] input);

        for (int i = 0; i < input.Length; i++)
        {
            Vector3 expected = Gt7ToneMapping.ToDisplay(input[i]);
            float[] want = [Srgb(expected.X), Srgb(expected.Y), Srgb(expected.Z)];
            for (int c = 0; c < 3; c++)
            {
                float difference = MathF.Abs(want[c] - pixels[(i * 4) + c]);
                Assert.True(difference <= 2.5f, $"pixel {i % Width},{i / Width} {input[i]}: channel {c} expected {want[c]}, got {pixels[(i * 4) + c]}");
            }
        }

        // The gradient is not trivial: the bright end of every row is at the display's white or near it, and the dark end is dark.
        Assert.True(pixels[((Width - 1) * 4)] > 240, "the bright end is white");
        Assert.True(pixels[0] < 10, "the dark end is dark");
    }

    [Fact]
    public void TheNeutralOptionStillRunsTheKhronosToneMapper()
    {
        byte[] pixels = Run(Tonemapper.PbrNeutral, out Vector3[] input);

        for (int i = 0; i < input.Length; i++)
        {
            Vector3 expected = ToneTwins.Neutral(Vector3.Clamp(input[i], Vector3.Zero, new Vector3(65504f)));
            for (int c = 0; c < 3; c++)
            {
                float want = Srgb(c == 0 ? expected.X : c == 1 ? expected.Y : expected.Z);
                Assert.True(MathF.Abs(want - pixels[(i * 4) + c]) <= 2.5f, $"pixel {i % Width},{i / Width} {input[i]}: channel {c} expected {want}, got {pixels[(i * 4) + c]}");
            }
        }
    }

    [Fact]
    public void TheTwoToneMappersLookDifferentAtTheSameInput()
    {
        byte[] gt7 = Run(Tonemapper.Gt7, out _);
        byte[] neutral = Run(Tonemapper.PbrNeutral, out _);

        // Over most of the gradient the two are not the same picture.
        int differing = 0;
        for (int i = 0; i < gt7.Length; i += 4)
        {
            if (Math.Abs(gt7[i] - neutral[i]) > 8)
            {
                differing++;
            }
        }

        Assert.True(differing > Width, $"only {differing} pixels differ");
    }
}

/// <summary>Tone mappers on the CPU, for tests that compare the shader with them.</summary>
internal static class ToneTwins
{
    /// <summary>The Khronos PBR Neutral tone mapper, saturated, in linear light (what tonemap.slang's neutralTonemap does).</summary>
    public static Vector3 Neutral(Vector3 c)
    {
        float x = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float offset = x < 0.08f ? x - (6.25f * x * x) : 0.04f;
        c -= new Vector3(offset);
        float peak = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        const float start = 0.8f - 0.04f;
        if (peak >= start)
        {
            const float d = 1f - start;
            float newPeak = 1f - (d * d / (peak + d - start));
            c *= newPeak / peak;
            float g = 1f - (1f / ((0.15f * (peak - newPeak)) + 1f));
            c = Vector3.Lerp(c, new Vector3(newPeak), g);
        }

        return Vector3.Clamp(c, Vector3.Zero, Vector3.One);
    }
}
