using System;
using Apogee.Core.Graphics;
using Apogee.Native.Vma;
using Apogee.Native.Vulkan;
using VmaApi = Apogee.Native.Vma.Vma;
using VmaAllocation = Apogee.Native.Vma.Allocation;

namespace Apogee.Graphics.Vulkan;

/// <summary>A view of a Vulkan image.</summary>
public sealed class VulkanImageView(VkImageView handle) : ImageView
{
    public VkImageView Handle { get; } = handle;
}

/// <summary>A Vulkan image with its VMA allocation and a view of the whole of it, or one that belongs to someone else (a swapchain's) and is only described here.</summary>
public sealed unsafe class VulkanImage : GpuImage
{
    private readonly VulkanDevice _device;
    private readonly bool _owned;
    private VmaAllocation* _allocation;
    private VulkanImageView _view = null!;

    internal VulkanImage(VulkanDevice device, in ImageDescription description, string? name)
        : base(description)
    {
        _device = device;
        _owned = true;

        VkImageCreateFlagBits flags = description.Cube ? VkImageCreateFlagBits.CubeCompatible : 0;
        var imageInfo = new VkImageCreateInfo
        {
            Flags = flags,
            ImageType = description.Depth > 1 ? VkImageType._3D : VkImageType._2D,
            Format = description.Format.ToVulkan(),
            Extent = new VkExtent3D { Width = description.Width, Height = description.Height, Depth = description.Depth },
            MipLevels = description.MipLevels,
            ArrayLayers = description.Cube ? 6 * description.ArrayLayers : description.ArrayLayers,
            Samples = description.Samples.ToVulkan(),
            Tiling = VkImageTiling.Optimal,
            Usage = description.Usage.ToVulkan() | VkImageUsageFlagBits.TransferSrc | VkImageUsageFlagBits.TransferDst,
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined,
        };
        var allocationInfo = new AllocationCreateInfo { Usage = MemoryUsage.AutoPreferDevice };

        VkImage image;
        VmaAllocation* allocation;
        VulkanException.Check(VmaApi.CreateImage(device.Allocator, &imageInfo, &allocationInfo, &image, &allocation, null), "vmaCreateImage");
        Handle = image;
        _allocation = allocation;
        Layers = imageInfo.ArrayLayers;

        try
        {
            _view = (VulkanImageView)CreateView(0, description.MipLevels, 0, Layers);
        }
        catch
        {
            Dispose();
            throw;
        }

        if (name is not null)
        {
            device.SetDebugName(VkObjectType.Image, image.Value, name);
            device.SetDebugName(VkObjectType.ImageView, _view.Handle.Value, name + " view");
        }
    }

    /// <summary>An image someone else owns (a swapchain's) with the view that goes with it; disposing this destroys neither.</summary>
    internal VulkanImage(VulkanDevice device, VkImage image, VkImageView view, in ImageDescription description)
        : base(description)
    {
        _device = device;
        Handle = image;
        Layers = description.ArrayLayers;
        _view = new VulkanImageView(view);
    }

    public VkImage Handle { get; }

    public override uint Layers { get; }

    public override ImageView View => _view;

    public VkImageView VulkanView => _view.Handle;

    /// <summary>All the aspects of the image, for a barrier.</summary>
    public VkImageAspectFlagBits Aspect => VulkanConvert.Aspect(Format);

    public override ImageView CreateView(uint baseMip, uint mipCount, uint baseLayer, uint layerCount) =>
        new VulkanImageView(CreateView(baseMip, mipCount, baseLayer, layerCount, VulkanConvert.SampledAspect(Format)));

    /// <summary>Creates an extra view with the given aspect. The caller destroys it with <see cref="DestroyView(ImageView)"/>.</summary>
    public VkImageView CreateView(uint baseMip, uint mipCount, uint baseLayer, uint layerCount, VkImageAspectFlagBits aspect)
    {
        VkImageViewType type = Description.Depth > 1 ? VkImageViewType._3D
            : Description.Cube ? (Layers > 6 && layerCount > 6 ? VkImageViewType.CubeArray : VkImageViewType.Cube)
            : Layers > 1 && layerCount > 1 ? VkImageViewType._2DArray
            : VkImageViewType._2D;
        var info = new VkImageViewCreateInfo
        {
            Image = Handle,
            ViewType = type,
            Format = Format.ToVulkan(),
            SubresourceRange = new VkImageSubresourceRange { AspectMask = aspect, BaseMipLevel = baseMip, LevelCount = mipCount, BaseArrayLayer = baseLayer, LayerCount = layerCount },
        };

        VkImageView view;
        VulkanException.Check(_device.Commands.CreateImageView(_device.Handle, &info, null, &view), "vkCreateImageView");
        return view;
    }

    public override void DestroyView(ImageView view) => _device.Commands.DestroyImageView(_device.Handle, ((VulkanImageView)view).Handle, null);

    public override void Dispose()
    {
        if (!_owned)
        {
            return;
        }

        if (_view is not null && _view.Handle.Value != 0)
        {
            _device.Commands.DestroyImageView(_device.Handle, _view.Handle, null);
            _view = new VulkanImageView(default);
        }

        if (_allocation is null)
        {
            return;
        }

        VmaApi.DestroyImage(_device.Allocator, Handle, _allocation);
        _allocation = null;
    }
}
