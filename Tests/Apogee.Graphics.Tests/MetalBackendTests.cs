using System;
using System.Linq;
using Apogee.Core.Graphics;
using Apogee.Graphics.Metal;
using Apogee.Graphics.Shaders;
using Apogee.Native.Metal;
using Xunit;

namespace Apogee.Graphics.Tests;

/// <summary>
/// What is particular to the Metal backend. They run on a Mac with a Metal GPU that meets the baseline, with <c>APOGEE_GRAPHICS=metal</c> (the default on macOS); everywhere else they skip.
/// The rest of the GPU tests run on whichever backend the device is, so most of Metal is covered by them.
/// </summary>
public sealed unsafe class MetalBackendTests(GpuFixture gpu) : GpuTest(gpu)
{
    private MetalDevice Metal
    {
        get
        {
            if (Device is not MetalDevice metal)
            {
                Assert.Skip($"This test is about Metal; the device is {Device.Backend}.");
            }

            return (MetalDevice)Device;
        }
    }

    [Fact]
    public void TheDeviceIsAMetal3GpuThatMeetsTheBaseline()
    {
        MetalDevice device = Metal;

        Assert.Equal(GraphicsBackend.Metal, device.Backend);
        Assert.Equal(ShaderBackend.Msl, device.ShaderBackend);
        Assert.False(string.IsNullOrEmpty(device.Name));
        Assert.Contains(GraphicsDevice.EnumerateDevices(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal }), g => g.Name == device.Name && g.Suitable);
    }

    [Fact]
    public void EveryFormatTheEngineUsesHasAMetalPixelFormat()
    {
        _ = Metal;
        PixelFormat[] used =
        [
            PixelFormat.R8Unorm, PixelFormat.R8G8Unorm, PixelFormat.R8G8B8A8Unorm, PixelFormat.R8G8B8A8Srgb, PixelFormat.B8G8R8A8Unorm, PixelFormat.B8G8R8A8Srgb,
            PixelFormat.R16G16B16A16Sfloat, PixelFormat.R32Sfloat, PixelFormat.R32G32B32A32Sfloat, PixelFormat.D32Sfloat,
            PixelFormat.Bc7UnormBlock, PixelFormat.Bc7SrgbBlock, PixelFormat.Astc4X4UnormBlock, PixelFormat.Astc4X4SrgbBlock,
        ];

        Assert.All(used, format => Assert.NotEqual(0u, Mtl.PixelFormat((int)format)));
        Assert.Equal(0u, Mtl.PixelFormat((int)PixelFormat.Undefined));
        Assert.Equal(used.Length, used.Select(f => Mtl.PixelFormat((int)f)).Distinct().Count()); // no two of them collapse into one
    }

    [Fact]
    public void ADeviceRefusesCodeOfAnotherBackend()
    {
        _ = Metal;
        CompiledShader spirv = new ShaderCompiler().CompileSource(
            "refused", "[shader(\"compute\")] [numthreads(1, 1, 1)] void main(uint3 id : SV_DispatchThreadID) { }");

        Assert.Throws<ArgumentException>(() => Device.CreateComputePipeline(spirv.Code("main"), "refused"));
    }

    [Fact]
    public void AShaderThatDoesNotCompileIsAnErrorWithMetalsMessage()
    {
        _ = Metal;
        var bad = new ShaderCode(ShaderBackend.Msl, "this is not metal shading language"u8.ToArray(), "main", new ThreadGroupSize(1, 1, 1));

        var error = Assert.Throws<MetalException>(() => Device.CreateComputePipeline(bad, "bad"));

        Assert.Contains("could not compile", error.Message);
    }

    [Fact]
    public void TheBindlessArgumentBufferHoldsTheDefaultTextureAndSamplerInSlotZero()
    {
        MetalDevice device = Metal;
        using GpuImage image = device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 2, 2, ImageUsage.Sampled), "slot test");
        device.UploadImage(image, new byte[16], [new ImageRegion(0, 0, 0, 2, 2)], ImageState.ShaderRead);

        uint index = device.Bindless.AddSampledImage(image);

        byte* arguments = device.MetalBindless.Arguments.Mapped;
        Assert.NotEqual(0UL, *(ulong*)arguments);                                 // the white texel's id, slot 0
        Assert.NotEqual(0UL, *(ulong*)(arguments + (index * 8)));                 // the new texture's
        Assert.NotEqual(*(ulong*)arguments, *(ulong*)(arguments + (index * 8)));
        Assert.NotEqual(0UL, *(ulong*)(arguments + (BindlessHeap.MaxSampledImages * 8))); // the default sampler, the first after the textures
        device.Bindless.RemoveSampledImage(index);
    }
}
