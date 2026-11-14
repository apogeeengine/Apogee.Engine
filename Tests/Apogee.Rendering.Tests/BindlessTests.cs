using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Apogee.Core.Graphics;
using Apogee.Graphics;
using Apogee.Graphics.Metal;
using Apogee.Graphics.Shaders;
using Apogee.Graphics.Tests;
using Xunit;

namespace Apogee.Rendering.Tests;

/// <summary>The bindless arrays of common.slang: textures of every dimension and storage images, read through the engine's own declarations.</summary>
public unsafe class BindlessTests(GpuFixture gpu) : GpuTest(gpu)
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public uint Texture2D;
        public uint Cube;
        public uint Array;
        public uint Volume;
        public uint UintTexture;
        public uint Storage;
        public uint Sampler;
        public uint Unused;
        public ulong Output;
    }

    private const string Shader = """
        #include "common.slang"

        struct Push { uint texture2D; uint cube; uint array; uint volume; uint uintTexture; uint storage; uint sampler; uint unused; float4* output; };
        [[vk::push_constant]] ConstantBuffer<Push> push PUSH_SLOT;

        [shader("compute")] [numthreads(1, 1, 1)]
        void sampleMain()
        {
            SamplerState s = gSamplers[push.sampler];
            push.output[0] = gTextures[push.texture2D].SampleLevel(s, float2(0.5, 0.5), 0);
            push.output[1] = gCubes[push.cube].SampleLevel(s, float3(0.0, 0.0, 1.0), 0);
            push.output[2] = gArrays[push.array].SampleLevel(s, float3(0.5, 0.5, 1.0), 0);
            push.output[3] = gVolumes[push.volume].SampleLevel(s, float3(0.5, 0.5, 0.75), 0);
            push.output[4] = float4(gUintTextures[push.uintTexture].Load(int3(0, 0, 0)), 0, 0, 0);
        }

        [shader("compute")] [numthreads(1, 1, 1)]
        void storeMain()
        {
            gStorage[push.storage][uint2(1, 1)] = float4(1.0, 0.0, 0.0, 1.0);
        }
        """;

    private static string ShaderDirectory()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "Apogee.slnx")))
            {
                return Path.Combine(directory, "Source", "Apogee.Rendering", "Shaders");
            }
        }

        throw new InvalidOperationException("Apogee.slnx not found above the test output.");
    }

    private GpuImage Upload(ImageDescription description, byte[] texels, ImageRegion[] regions)
    {
        GpuImage image = Device.CreateImage(description, "bindless test");
        Device.UploadImage(image, texels, regions, ImageState.ShaderRead);
        return image;
    }

    [Fact]
    public void EveryDimensionOfTextureIsReadByItsOwnArrayAndStorageImagesAreWritten()
    {
        var compiler = new ShaderCompiler(new ShaderCompilerOptions { Backend = Device.ShaderBackend, SearchPaths = [ShaderDirectory()] });
        CompiledShader shader = compiler.CompileSource("bindless", Shader);
        using Pipeline sample = Device.CreateComputePipeline(shader.Code("sampleMain"), "sample");
        using Pipeline store = Device.CreateComputePipeline(shader.Code("storeMain"), "store");

        using GpuImage flat = Upload(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 1, 1, ImageUsage.Sampled), [255, 0, 0, 255], [new ImageRegion(0, 0, 0, 1, 1)]);

        // A cube with a green +Z face (face 4) and black others.
        byte[] faces = new byte[24];
        faces[16 + 1] = 255;
        for (int face = 0; face < 6; face++)
        {
            faces[(face * 4) + 3] = 255;
        }

        using GpuImage cube = Upload(
            new ImageDescription(PixelFormat.R8G8B8A8Unorm, 1, 1, ImageUsage.Sampled, Cube: true), faces,
            [.. Enumerable.Range(0, 6).Select(face => new ImageRegion((ulong)(face * 4), 0, (uint)face, 1, 1))]);

        // Two layers: black, then blue.
        using GpuImage array = Upload(
            new ImageDescription(PixelFormat.R8G8B8A8Unorm, 1, 1, ImageUsage.Sampled, ArrayLayers: 2), [0, 0, 0, 255, 0, 0, 255, 255],
            [new ImageRegion(0, 0, 0, 1, 1), new ImageRegion(4, 0, 1, 1, 1)]);

        // Two depth slices: black, then yellow.
        using GpuImage volume = Upload(
            new ImageDescription(PixelFormat.R8G8B8A8Unorm, 1, 1, ImageUsage.Sampled, Depth: 2), [0, 0, 0, 255, 255, 255, 0, 255],
            [new ImageRegion(0, 0, 0, 1, 1, 2)]);

        using GpuImage integers = Upload(new ImageDescription(PixelFormat.R32Uint, 1, 1, ImageUsage.Sampled), BitConverter.GetBytes(42u), [new ImageRegion(0, 0, 0, 1, 1)]);

        using GpuImage storage = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 2, 2, ImageUsage.Storage), "storage");
        using GpuSampler sampler = Device.CreateSampler(SamplerDescription.NearestClamp, "nearest");
        using GpuBuffer output = Device.CreateBuffer(5 * 16, BufferUsage.Storage, MemoryKind.DeviceLocal, "output");

        var heap = Device.Bindless;
        var push = new Push
        {
            Texture2D = heap.AddSampledImage(flat),
            Cube = heap.AddSampledImage(cube),
            Array = heap.AddSampledImage(array),
            Volume = heap.AddSampledImage(volume),
            UintTexture = heap.AddSampledImage(integers),
            Storage = heap.AddStorageImage(storage.View),
            Sampler = heap.AddSampler(sampler),
            Output = output.Address,
        };

        Device.Immediate.Submit(recorder =>
        {
            recorder.Barrier(ImageBarrier.For(storage, ImageState.Undefined, ImageState.StorageReadWrite));
            recorder.Barriers([], [new BufferBarrier(output, BufferState.Undefined, BufferState.ShaderWrite)]);
            recorder.BindPipeline(sample);
            recorder.PushConstants(push);
            recorder.Dispatch(1);
            recorder.BindPipeline(store);
            recorder.PushConstants(push);
            recorder.Dispatch(1);
            recorder.Barrier(ImageBarrier.For(storage, ImageState.StorageReadWrite, ImageState.StorageReadWrite));
        });

        float[] values = new float[20];
        Buffer.BlockCopy(Device.ReadBuffer(output, 0, 80), 0, values, 0, 80);
        Assert.Equal([1f, 0f, 0f, 1f], values[0..4]);        // 2D
        Assert.Equal([0f, 1f, 0f, 1f], values[4..8]);        // cube, +Z
        Assert.Equal([0f, 0f, 1f, 1f], values[8..12]);       // 2D array, layer 1
        Assert.Equal([1f, 1f, 0f, 1f], values[12..16]);      // 3D, slice 1
        Assert.Equal(42f, values[16]);                       // unsigned integer texture

        byte[] pixels = Device.ReadImage(storage, ImageState.StorageReadWrite);
        Assert.Equal([255, 0, 0, 255], pixels[12..16]);      // the texel (1, 1) the shader wrote

        heap.RemoveSampledImage(push.Texture2D);
        heap.RemoveSampledImage(push.Cube);
        heap.RemoveSampledImage(push.Array);
        heap.RemoveSampledImage(push.Volume);
        heap.RemoveSampledImage(push.UintTexture);
        heap.RemoveStorageImage(push.Storage);
        heap.RemoveSampler(push.Sampler);
    }

    [Fact]
    public void ACubeViewOfOneFaceIsA2DViewAndAFullCubeIsACubeView()
    {
        using GpuImage cube = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 4, 4, ImageUsage.Sampled | ImageUsage.Storage, Cube: true), "cube");
        ImageView face = cube.CreateView(0, 1, 2, 1);
        ImageView all = cube.CreateView(0, 1, 0, 6);
        try
        {
            // Both must be usable as descriptors; the validation layer checks the view types against the image.
            uint a = Device.Bindless.AddSampledImage(face);
            uint b = Device.Bindless.AddSampledImage(all);
            uint c = Device.Bindless.AddStorageImage(face);
            Device.Bindless.RemoveSampledImage(a);
            Device.Bindless.RemoveSampledImage(b);
            Device.Bindless.RemoveStorageImage(c);
        }
        finally
        {
            cube.DestroyView(face);
            cube.DestroyView(all);
        }
    }

    [Fact]
    public void TheMetalArgumentBufferHasTheMembersOfTheShaderBlockInOrder()
    {
        // What Slang writes for Metal: the members of BindlessResources, in order, with their sizes.
        var compiler = new ShaderCompiler(new ShaderCompilerOptions { Backend = ShaderBackend.Msl, SearchPaths = [ShaderDirectory()] });
        string msl = compiler.CompileSource("bindless", Shader).Msl("storeMain");

        int start = msl.IndexOf("struct BindlessResources", StringComparison.Ordinal);
        Assert.True(start >= 0, "the MSL has the bindless block");
        string block = msl[start..msl.IndexOf('}', start)];
        string[] members = [.. block.Split('\n').Select(line => line.Trim()).Where(line => line.EndsWith("_0;", StringComparison.Ordinal)).Select(line => line.Split(' ')[1][..^3])];

        Assert.Equal(MetalBindlessHeap.Members, members);

        // Each run is an array of that many resources.
        foreach (string member in members)
        {
            Assert.True(MetalBindlessHeap.CountOf(member) is 16384 or 256);
        }

        Assert.Contains("int(16384)", msl);
        Assert.Contains("int(256)", msl);
        Assert.Equal(0UL, MetalBindlessHeap.OffsetOf("textures"));
        Assert.Equal(16384UL * 8, MetalBindlessHeap.OffsetOf("samplers"));
        Assert.Equal(((16384UL + 256) * 8), MetalBindlessHeap.OffsetOf("cubes"));
        Assert.Equal((16384UL * 5 + 256) * 8, MetalBindlessHeap.OffsetOf("storage"));
        Assert.Equal((16384UL * 5 + 512) * 8, MetalBindlessHeap.ArgumentBufferBytes);
    }
}
