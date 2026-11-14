using System;
using Apogee.Core.Graphics;
using Apogee.Interop;
using Apogee.Native.Vulkan;

namespace Apogee.Graphics.Vulkan;

/// <summary>A Vulkan command buffer being recorded.</summary>
public sealed unsafe class VulkanCommandRecorder : CommandRecorder
{
    private readonly VulkanDevice _device;

    internal VulkanCommandRecorder(VulkanDevice device, VkCommandBuffer handle)
        : base(device)
    {
        _device = device;
        Handle = handle;
    }

    public VkCommandBuffer Handle { get; }

    private VkDeviceCommands Commands => _device.Commands;

    public override void Barriers(ReadOnlySpan<ImageBarrier> images, ReadOnlySpan<BufferBarrier> buffers = default)
    {
        if (images.Length == 0 && buffers.Length == 0)
        {
            return;
        }

        var imageBarriers = new VkImageMemoryBarrier2[images.Length];
        for (int i = 0; i < images.Length; i++)
        {
            ImageBarrier b = images[i];
            var image = (VulkanImage)b.Image;
            (VkPipelineStageFlagBits2 srcStages, VkAccessFlagBits2 srcAccess, VkImageLayout oldLayout) = VulkanStates.Describe(b.Before);
            (VkPipelineStageFlagBits2 dstStages, VkAccessFlagBits2 dstAccess, VkImageLayout newLayout) = VulkanStates.Describe(b.After);
            imageBarriers[i] = new VkImageMemoryBarrier2
            {
                SrcStageMask = b.Before == ImageState.Undefined ? VkPipelineStageFlagBits2.AllCommands : srcStages,
                SrcAccessMask = srcAccess,
                DstStageMask = b.After == ImageState.Present ? VkPipelineStageFlagBits2.AllCommands : dstStages,
                DstAccessMask = dstAccess,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = uint.MaxValue,
                DstQueueFamilyIndex = uint.MaxValue,
                Image = image.Handle,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = image.Aspect,
                    BaseMipLevel = b.BaseMip,
                    LevelCount = b.MipCount,
                    BaseArrayLayer = b.BaseLayer,
                    LayerCount = b.LayerCount,
                },
            };
        }

        var bufferBarriers = new VkBufferMemoryBarrier2[buffers.Length];
        for (int i = 0; i < buffers.Length; i++)
        {
            BufferBarrier b = buffers[i];
            (VkPipelineStageFlagBits2 srcStages, VkAccessFlagBits2 srcAccess) = VulkanStates.Describe(b.Before);
            (VkPipelineStageFlagBits2 dstStages, VkAccessFlagBits2 dstAccess) = VulkanStates.Describe(b.After);
            bufferBarriers[i] = new VkBufferMemoryBarrier2
            {
                SrcStageMask = b.Before == BufferState.Undefined ? VkPipelineStageFlagBits2.AllCommands : srcStages,
                SrcAccessMask = srcAccess,
                DstStageMask = dstStages,
                DstAccessMask = dstAccess,
                SrcQueueFamilyIndex = uint.MaxValue,
                DstQueueFamilyIndex = uint.MaxValue,
                Buffer = ((VulkanBuffer)b.Buffer).Handle,
                Offset = 0,
                Size = b.Buffer.Size,
            };
        }

        fixed (VkImageMemoryBarrier2* imageData = imageBarriers)
        fixed (VkBufferMemoryBarrier2* bufferData = bufferBarriers)
        {
            var dependency = new VkDependencyInfo
            {
                ImageMemoryBarrierCount = (uint)imageBarriers.Length,
                PImageMemoryBarriers = imageData,
                BufferMemoryBarrierCount = (uint)bufferBarriers.Length,
                PBufferMemoryBarriers = bufferData,
            };
            Commands.CmdPipelineBarrier2(Handle, &dependency);
        }
    }

    public override void CopyBuffer(GpuBuffer source, GpuBuffer destination, ulong size, ulong sourceOffset = 0, ulong destinationOffset = 0)
    {
        var region = new VkBufferCopy { SrcOffset = sourceOffset, DstOffset = destinationOffset, Size = size };
        Commands.CmdCopyBuffer(Handle, ((VulkanBuffer)source).Handle, ((VulkanBuffer)destination).Handle, 1, &region);
    }

    public override void CopyBufferToImage(GpuBuffer source, GpuImage destination, ReadOnlySpan<ImageRegion> regions)
    {
        VkBufferImageCopy[] copies = ToCopies(destination, regions);
        fixed (VkBufferImageCopy* data = copies)
        {
            Commands.CmdCopyBufferToImage(Handle, ((VulkanBuffer)source).Handle, ((VulkanImage)destination).Handle, VkImageLayout.TransferDstOptimal, (uint)copies.Length, data);
        }
    }

    public override void CopyImageToBuffer(GpuImage source, GpuBuffer destination, ReadOnlySpan<ImageRegion> regions)
    {
        VkBufferImageCopy[] copies = ToCopies(source, regions);
        fixed (VkBufferImageCopy* data = copies)
        {
            Commands.CmdCopyImageToBuffer(Handle, ((VulkanImage)source).Handle, VkImageLayout.TransferSrcOptimal, ((VulkanBuffer)destination).Handle, (uint)copies.Length, data);
        }
    }

    private static VkBufferImageCopy[] ToCopies(GpuImage image, ReadOnlySpan<ImageRegion> regions)
    {
        var copies = new VkBufferImageCopy[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            ImageRegion r = regions[i];
            copies[i] = new VkBufferImageCopy
            {
                BufferOffset = r.BufferOffset,
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VulkanConvert.SampledAspect(image.Format), MipLevel = r.MipLevel, BaseArrayLayer = r.ArrayLayer, LayerCount = 1 },
                ImageExtent = new VkExtent3D { Width = r.Width, Height = r.Height, Depth = r.Depth },
            };
        }

        return copies;
    }

    public override void FillBuffer(GpuBuffer buffer, uint value = 0, ulong offset = 0, ulong size = WholeSize)
    {
        if ((offset & 3) != 0 || (size != WholeSize && (size & 3) != 0))
        {
            throw new ArgumentException("The offset and size of a buffer fill are multiples of four.");
        }

        Commands.CmdFillBuffer(Handle, ((VulkanBuffer)buffer).Handle, offset, size, value);
    }

    public override void ClearImage(GpuImage image, System.Numerics.Vector4 color, uint baseMip = 0, uint mipCount = uint.MaxValue, uint baseLayer = 0, uint layerCount = uint.MaxValue)
    {
        var vulkan = (VulkanImage)image;
        var value = new VkClearColorValue();
        value.Float32[0] = color.X;
        value.Float32[1] = color.Y;
        value.Float32[2] = color.Z;
        value.Float32[3] = color.W;
        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlagBits.Color, BaseMipLevel = baseMip, LevelCount = mipCount, BaseArrayLayer = baseLayer, LayerCount = layerCount };
        Commands.CmdClearColorImage(Handle, vulkan.Handle, VkImageLayout.TransferDstOptimal, &value, 1, &range);
    }

    public override void ClearDepthImage(GpuImage image, float depth = 0f, uint baseMip = 0, uint mipCount = uint.MaxValue, uint baseLayer = 0, uint layerCount = uint.MaxValue)
    {
        var vulkan = (VulkanImage)image;
        var value = new VkClearDepthStencilValue { Depth = depth, Stencil = 0 };
        var range = new VkImageSubresourceRange { AspectMask = vulkan.Aspect, BaseMipLevel = baseMip, LevelCount = mipCount, BaseArrayLayer = baseLayer, LayerCount = layerCount };
        Commands.CmdClearDepthStencilImage(Handle, vulkan.Handle, VkImageLayout.TransferDstOptimal, &value, 1, &range);
    }

    public override void BindPipeline(Pipeline pipeline)
    {
        var vulkan = (VulkanPipeline)pipeline;
        Commands.CmdBindPipeline(Handle, vulkan.BindPoint, vulkan.Handle);
        VkDescriptorSet set = _device.VulkanBindless.Set;
        Commands.CmdBindDescriptorSets(Handle, vulkan.BindPoint, _device.VulkanBindless.PipelineLayout, 0, 1, &set, 0, null);
    }

    protected override void PushBytes(ReadOnlySpan<byte> data)
    {
        fixed (byte* pointer = data)
        {
            Commands.CmdPushConstants(Handle, _device.VulkanBindless.PipelineLayout, VkShaderStageFlagBits.All, 0, (uint)data.Length, pointer);
        }
    }

    public override void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1) => Commands.CmdDispatch(Handle, groupsX, groupsY, groupsZ);

    public override void DispatchIndirect(GpuBuffer arguments, ulong offset = 0) => Commands.CmdDispatchIndirect(Handle, ((VulkanBuffer)arguments).Handle, offset);

    public override void BeginRendering(uint width, uint height, ReadOnlySpan<ColorTarget> colors, DepthTarget? depth = null)
    {
        var colorInfos = stackalloc VkRenderingAttachmentInfo[Math.Max(1, colors.Length)];
        for (int i = 0; i < colors.Length; i++)
        {
            ColorTarget c = colors[i];
            colorInfos[i] = new VkRenderingAttachmentInfo
            {
                ImageView = ((VulkanImageView)c.View).Handle,
                ImageLayout = VkImageLayout.ColorAttachmentOptimal,
                LoadOp = c.Clear ? VkAttachmentLoadOp.Clear : VkAttachmentLoadOp.Load,
                StoreOp = c.Store ? VkAttachmentStoreOp.Store : VkAttachmentStoreOp.DontCare,
            };
            colorInfos[i].ClearValue.Color.Float32[0] = c.ClearColor.X;
            colorInfos[i].ClearValue.Color.Float32[1] = c.ClearColor.Y;
            colorInfos[i].ClearValue.Color.Float32[2] = c.ClearColor.Z;
            colorInfos[i].ClearValue.Color.Float32[3] = c.ClearColor.W;
        }

        VkRenderingAttachmentInfo depthInfo = default;
        if (depth is { } d)
        {
            depthInfo = new VkRenderingAttachmentInfo
            {
                ImageView = ((VulkanImageView)d.View).Handle,
                ImageLayout = d.ReadOnly ? VkImageLayout.DepthReadOnlyOptimal : VkImageLayout.DepthAttachmentOptimal,
                LoadOp = d.Clear ? VkAttachmentLoadOp.Clear : VkAttachmentLoadOp.Load,
                StoreOp = d.Store ? VkAttachmentStoreOp.Store : VkAttachmentStoreOp.DontCare,
            };
            depthInfo.ClearValue.DepthStencil = new VkClearDepthStencilValue { Depth = d.ClearDepth };
        }

        var info = new VkRenderingInfo
        {
            RenderArea = new VkRect2D { Extent = new VkExtent2D { Width = width, Height = height } },
            LayerCount = 1,
            ColorAttachmentCount = (uint)colors.Length,
            PColorAttachments = colorInfos,
            PDepthAttachment = depth is null ? null : &depthInfo,
        };
        Commands.CmdBeginRendering(Handle, &info);
        SetViewport(width, height);
    }

    public override void EndRendering() => Commands.CmdEndRendering(Handle);

    public override void SetViewport(uint width, uint height)
    {
        var viewport = new VkViewport { Width = width, Height = height, MinDepth = 0f, MaxDepth = 1f };
        var scissor = new VkRect2D { Extent = new VkExtent2D { Width = width, Height = height } };
        Commands.CmdSetViewport(Handle, 0, 1, &viewport);
        Commands.CmdSetScissor(Handle, 0, 1, &scissor);
    }

    public override void SetScissor(int x, int y, uint width, uint height)
    {
        var scissor = new VkRect2D { Offset = new VkOffset2D { X = x, Y = y }, Extent = new VkExtent2D { Width = width, Height = height } };
        Commands.CmdSetScissor(Handle, 0, 1, &scissor);
    }

    public override void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0) =>
        Commands.CmdDraw(Handle, vertexCount, instanceCount, firstVertex, firstInstance);

    public override void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0) =>
        Commands.CmdDrawIndexed(Handle, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);

    public override void DrawIndexedIndirect(GpuBuffer commands, ulong offset, uint drawCount, uint stride = DrawIndexedIndirectCommand.Size)
    {
        VkBuffer buffer = ((VulkanBuffer)commands).Handle;
        if (drawCount <= 1 || _device.SupportsMultiDrawIndirect)
        {
            Commands.CmdDrawIndexedIndirect(Handle, buffer, offset, drawCount, stride);
            return;
        }

        // Without multiDrawIndirect a call may make one draw at most: one call each.
        for (uint i = 0; i < drawCount; i++)
        {
            Commands.CmdDrawIndexedIndirect(Handle, buffer, offset + (i * stride), 1, stride);
        }
    }

    public override void DrawIndexedIndirectCount(GpuBuffer commands, ulong offset, GpuBuffer count, ulong countOffset, uint maxDrawCount, uint stride = DrawIndexedIndirectCommand.Size)
    {
        if (!_device.SupportsDrawIndirectCount)
        {
            throw new NotSupportedException("This GPU has no drawIndirectCount: check GraphicsDevice.SupportsDrawIndirectCount, or use DrawIndexedIndirect with a maximum count.");
        }

        Commands.CmdDrawIndexedIndirectCount(Handle, ((VulkanBuffer)commands).Handle, offset, ((VulkanBuffer)count).Handle, countOffset, maxDrawCount, stride);
    }

    public override void BindIndexBuffer(GpuBuffer buffer, IndexFormat format = IndexFormat.Index32, ulong offset = 0) =>
        Commands.CmdBindIndexBuffer(Handle, ((VulkanBuffer)buffer).Handle, offset, format.ToVulkan());

    protected override void BeginLabel(string name, float r, float g, float b)
    {
        if (_device.Instance.HasDebugUtils)
        {
            fixed (byte* text = Utf8.ToZeroTerminated(name))
            {
                var label = new VkDebugUtilsLabelEXT { PLabelName = text };
                label.Color[0] = r;
                label.Color[1] = g;
                label.Color[2] = b;
                label.Color[3] = 1f;
                Commands.CmdBeginDebugUtilsLabelEXT(Handle, &label);
            }
        }
    }

    internal override void EndLabel()
    {
        if (_device.Instance.HasDebugUtils)
        {
            Commands.CmdEndDebugUtilsLabelEXT(Handle);
        }
    }
}
