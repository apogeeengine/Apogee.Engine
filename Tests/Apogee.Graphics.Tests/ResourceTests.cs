using Apogee.Graphics.Vulkan;
using Apogee.Core.Graphics;
using System;
using System.Linq;
using System.Threading.Tasks;
using Apogee.Native.Vulkan;
using Xunit;

namespace Apogee.Graphics.Tests;

public unsafe class ResourceTests(GpuFixture gpu) : GpuTest(gpu)
{
    private static byte[] Pattern(int length, int seed = 1) => [.. Enumerable.Range(0, length).Select(i => (byte)((i * 31 + seed) & 0xFF))];

    [Fact]
    public void ADeviceLocalBufferRoundTripsThroughStaging()
    {
        byte[] data = Pattern(4096);
        using GpuBuffer buffer = Device.CreateBuffer(data, BufferUsage.Storage, "round trip");

        Assert.True(buffer.Mapped == null);
        Assert.Equal(data, Device.ReadBuffer(buffer, 0, data.Length));
        Assert.Equal(Pattern(100)[10..20], Device.ReadBuffer(buffer, 10, 10));
    }

    [Fact]
    public void AnUploadBufferIsMappedAndWritableAtAnOffset()
    {
        using GpuBuffer buffer = Device.CreateBuffer(256, BufferUsage.Uniform, MemoryKind.Upload, "mapped");

        Assert.True(buffer.Mapped != null);
        buffer.Write(new byte[8], 0);          // new memory is not zeroed
        buffer.Write([1, 2, 3, 4], 8);
        Assert.Equal([0, 0, 1, 2, 3, 4], buffer.Read(6, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Write(new byte[16], 250));
    }

    [Fact]
    public void BuffersHaveDistinctDeviceAddresses()
    {
        using GpuBuffer a = Device.CreateBuffer(1024, BufferUsage.Storage);
        using GpuBuffer b = Device.CreateBuffer(1024, BufferUsage.Storage);

        Assert.NotEqual(0UL, a.Address);
        Assert.NotEqual(a.Address, b.Address);
        Assert.True(a.Address + 1024 <= b.Address || b.Address + 1024 <= a.Address);
    }

    [Fact]
    public void AnImageRoundTripsEveryMipLevel()
    {
        var description = new ImageDescription(PixelFormat.R8G8B8A8Unorm, 4, 4, ImageUsage.Sampled, MipLevels: ImageDescription.FullMipCount(4, 4));
        Assert.Equal(3u, description.MipLevels);
        using GpuImage image = Device.CreateImage(description, "mips");

        byte[] mip0 = Pattern(64, 1);
        byte[] mip1 = Pattern(16, 2);
        byte[] mip2 = Pattern(4, 3);
        Device.UploadImage(image, [.. mip0, .. mip1, .. mip2], [new ImageRegion(0, 0, 0, 4, 4), new ImageRegion(64, 1, 0, 2, 2), new ImageRegion(80, 2, 0, 1, 1)], ImageState.ShaderRead);

        Assert.Equal(mip0, Device.ReadImage(image, ImageState.ShaderRead, 0));
        Assert.Equal(mip1, Device.ReadImage(image, ImageState.ShaderRead, 1));
        Assert.Equal(mip2, Device.ReadImage(image, ImageState.ShaderRead, 2));
    }

    [Fact]
    public void ABlockCompressedImageKeepsItsBlocks()
    {
        if (!Device.SupportsBc)
        {
            Assert.Skip("This GPU does not sample BC textures.");
        }

        using GpuImage image = Device.CreateImage(new ImageDescription(PixelFormat.Bc7UnormBlock, 8, 4, ImageUsage.Sampled), "bc7");
        byte[] blocks = Pattern(32);

        Device.UploadImage(image, blocks, [new ImageRegion(0, 0, 0, 8, 4)], ImageState.ShaderRead);

        Assert.Equal(blocks, Device.ReadImage(image, ImageState.ShaderRead));
    }

    [Fact]
    public void ArrayLayersAndCubesAreCreatedWithTheRightViews()
    {
        RequireVulkan();
        using GpuImage array = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 8, 8, ImageUsage.Sampled, ArrayLayers: 3), "array");
        using GpuImage cube = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 8, 8, ImageUsage.Sampled, Cube: true), "cube");

        Assert.Equal(3u, array.Layers);
        Assert.Equal(6u, cube.Layers);
        Assert.True(((VulkanImageView)array.View).Handle.Value != 0 && ((VulkanImageView)cube.View).Handle.Value != 0);

        ImageView layer = array.CreateView(0, 1, 1, 1);
        Assert.True(((VulkanImageView)layer).Handle.Value != 0);
        array.DestroyView(layer);
    }

    [Fact]
    public void DepthAndColorAttachmentsCanBeCreated()
    {
        RequireVulkan();
        using GpuImage depth = Device.CreateImage(new ImageDescription(PixelFormat.D32Sfloat, 64, 64, ImageUsage.DepthStencilAttachment | ImageUsage.Sampled), "depth");
        using GpuImage color = Device.CreateImage(new ImageDescription(PixelFormat.R16G16B16A16Sfloat, 64, 64, ImageUsage.ColorAttachment | ImageUsage.Sampled), "hdr");

        Assert.Equal(VkImageAspectFlagBits.Depth, ((VulkanImage)depth).Aspect);
        Assert.Equal(VkImageAspectFlagBits.Color, ((VulkanImage)color).Aspect);
        Device.Immediate.Submit(recorder =>
        {
            recorder.Barriers([ImageBarrier.For(depth, ImageState.Undefined, ImageState.DepthAttachment), ImageBarrier.For(color, ImageState.Undefined, ImageState.ColorAttachment)]);
            recorder.Barriers([ImageBarrier.For(depth, ImageState.DepthAttachment, ImageState.DepthRead), ImageBarrier.For(color, ImageState.ColorAttachment, ImageState.ShaderRead)]);
        });
    }

    [Fact]
    public void SamplersAreCreatedAndShareTheirLimitsWithTheDevice()
    {
        RequireVulkan();
        using GpuSampler repeat = Device.CreateSampler(SamplerDescription.LinearRepeat, "repeat");
        using GpuSampler shadow = Device.CreateSampler(SamplerDescription.Shadow, "shadow");

        Assert.True(((VulkanSampler)repeat).Handle.Value != 0 && ((VulkanSampler)shadow).Handle.Value != 0);
    }

    [Fact]
    public void TheBindlessHeapHandsOutSlotsAndReusesFreedOnes()
    {
        using GpuImage image = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 2, 2, ImageUsage.Sampled));
        Device.UploadImage(image, Pattern(16), [new ImageRegion(0, 0, 0, 2, 2)], ImageState.ShaderRead);

        int before = Device.Bindless.SampledImageCount;
        uint first = Device.Bindless.AddSampledImage(image);
        uint second = Device.Bindless.AddSampledImage(image);

        Assert.True(first >= 1 && second >= 1 && first != second);
        Assert.Equal(before + 2, Device.Bindless.SampledImageCount);

        Device.Bindless.RemoveSampledImage(first);
        Assert.Equal(first, Device.Bindless.AddSampledImage(image));
        Device.Bindless.RemoveSampledImage(first);
        Device.Bindless.RemoveSampledImage(second);
        Assert.Equal(before, Device.Bindless.SampledImageCount);

        Assert.Throws<ArgumentException>(() => Device.Bindless.RemoveSampledImage(0));
        Assert.Throws<ArgumentException>(() => Device.Bindless.RemoveSampledImage(second));
    }

    [Fact]
    public void SeveralThreadsCanUseTheImmediateContextAtOnce()
    {
        Parallel.For(0, 4, thread =>
        {
            for (int i = 0; i < 10; i++)
            {
                byte[] data = Pattern(512, thread * 10 + i);
                using GpuBuffer buffer = Device.CreateBuffer(data, BufferUsage.Storage);
                Assert.Equal(data, Device.ReadBuffer(buffer, 0, data.Length));
            }
        });
    }
}

/// <summary>The tables that need no GPU.</summary>
public class FormatAndStateTests
{
    [Fact]
    public void LevelSizesFollowTheBlockLayout()
    {
        Assert.Equal(64UL, FormatInfo.LevelSize(PixelFormat.R8G8B8A8Unorm, 4, 4));
        Assert.Equal(64UL, FormatInfo.LevelSize(PixelFormat.Bc7UnormBlock, 7, 5));   // 2 x 2 blocks of 16 bytes
        Assert.Equal(8UL, FormatInfo.LevelSize(PixelFormat.Bc1RgbUnormBlock, 4, 4));
        Assert.Equal(8UL * 4 * 4 * 2, FormatInfo.LevelSize(PixelFormat.R16G16B16A16Sfloat, 4, 4, 2));
        Assert.Throws<NotSupportedException>(() => FormatInfo.Block(PixelFormat.Undefined));
    }

    [Fact]
    public void AspectsFollowTheFormat()
    {
        Assert.Equal(VkImageAspectFlagBits.Color, VulkanConvert.Aspect(PixelFormat.R8G8B8A8Srgb));
        Assert.Equal(VkImageAspectFlagBits.Depth, VulkanConvert.Aspect(PixelFormat.D32Sfloat));
        Assert.Equal(VkImageAspectFlagBits.Depth | VkImageAspectFlagBits.Stencil, VulkanConvert.Aspect(PixelFormat.D24UnormS8Uint));
        Assert.Equal(VkImageAspectFlagBits.Depth, VulkanConvert.SampledAspect(PixelFormat.D24UnormS8Uint));
    }

    [Fact]
    public void EveryStateHasALayoutAndWritesAreRecognized()
    {
        foreach (ImageState state in Enum.GetValues<ImageState>())
        {
            (_, _, VkImageLayout layout) = VulkanStates.Describe(state);
            Assert.Equal(state == ImageState.Undefined, layout == VkImageLayout.Undefined);
        }

        foreach (BufferState state in Enum.GetValues<BufferState>())
        {
            VulkanStates.Describe(state);
        }

        Assert.True(ResourceStates.Writes(ImageState.ColorAttachment));
        Assert.False(ResourceStates.Writes(ImageState.ShaderRead));
        Assert.True(ResourceStates.Writes(BufferState.ShaderWrite));
        Assert.False(ResourceStates.Writes(BufferState.TransferSource));
        Assert.Equal(VkImageLayout.ShaderReadOnlyOptimal, VulkanStates.Describe(ImageState.ShaderRead).Layout);
    }

    [Fact]
    public void FullMipCountGoesDownToOneTexel()
    {
        Assert.Equal(1u, ImageDescription.FullMipCount(1, 1));
        Assert.Equal(9u, ImageDescription.FullMipCount(256, 100));
        Assert.Equal(10u, ImageDescription.FullMipCount(1000, 3));
    }
}
