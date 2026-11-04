using System;
using System.Linq;
using Apogee.Core.Graphics;
using Apogee.Core.Logging;
using Apogee.Native.Vulkan;
using Apogee.Platform;

namespace Apogee.Graphics.Vulkan;

/// <summary>The outcome of asking the swapchain for the next image.</summary>
public enum AcquireStatus
{
    Ok,
    /// <summary>The swapchain no longer matches the surface (resized, or the display changed): recreate it and try again.</summary>
    OutOfDate,
}

/// <summary>
/// The images presented to a window. Their format is a UNORM one, B8G8R8A8 or R8G8B8A8: shaders write sRGB-encoded values themselves, which
/// also lets the dev overlay draw straight onto them. Created for a size from <paramref name="sizeSource"/> and recreated whenever that changes.
/// </summary>
public sealed unsafe class VulkanSwapchain : Swapchain
{
    private const string LogCategory = "Graphics";

    private readonly VulkanDevice _device;
    private readonly VkSurfaceKHR _surface;
    private readonly Func<PixelSize> _sizeSource;
    private VkSwapchainKHR _handle;
    private VkImage[] _images = [];
    private VkImageView[] _views = [];
    private VulkanImage[] _wrapped = [];
    private uint _width;
    private uint _height;
    private PixelFormat _format;
    private VkSemaphore[] _presentSemaphores = [];

    internal VulkanSwapchain(VulkanDevice device, VkSurfaceKHR surface, Func<PixelSize> sizeSource, bool vsync = true)
    {
        if (!device.SupportsSwapchain)
        {
            throw new VulkanException("The device was created without a surface, so it cannot present.");
        }

        _device = device;
        _surface = surface;
        _sizeSource = sizeSource;
        VSync = vsync;
        Create(default);
    }

    public override bool VSync { get; }

    public override PixelFormat Format => _format;

    public override uint Width { get => _width; }

    public override uint Height { get => _height; }

    public override int ImageCount => _images.Length;

    public VkImage Image(uint index) => _images[index];

    public VkImageView View(uint index) => _views[index];

    /// <summary>The image as the engine's API sees it (owned by the swapchain; do not dispose it).</summary>
    public GpuImage ImageAt(uint index) => _wrapped[index];

    /// <summary>The semaphore the present of image <paramref name="index"/> waits on: one per image, so a semaphore is never signalled while an earlier present may still be waiting on it.</summary>
    public VkSemaphore PresentSemaphore(uint index) => _presentSemaphores[index];

    public override bool IsStale
    {
        get
        {
            PixelSize size = _sizeSource();
            return !size.IsEmpty && (size.Width != Width || size.Height != Height);
        }
    }

    public override bool IsZeroSized => _sizeSource().IsEmpty;

    public AcquireStatus Acquire(VkSemaphore signal, out uint imageIndex)
    {
        uint index;
        VkResult result = _device.Commands.AcquireNextImageKHR(_device.Handle, _handle, ulong.MaxValue, signal, default, &index);
        imageIndex = index;
        switch (result)
        {
            case VkResult.Success:
            case VkResult.SuboptimalKhr:
                return AcquireStatus.Ok;
            case VkResult.ErrorOutOfDateKhr:
                return AcquireStatus.OutOfDate;
            default:
                throw new VulkanException("vkAcquireNextImageKHR", result);
        }
    }

    /// <summary>Presents an image after the work that renders it. Returns false when the swapchain is out of date (the frame is still valid).</summary>
    public bool Present(uint imageIndex)
    {
        VkSwapchainKHR swapchain = _handle;
        VkSemaphore wait = _presentSemaphores[imageIndex];
        uint index = imageIndex;
        var info = new VkPresentInfoKHR { WaitSemaphoreCount = 1, PWaitSemaphores = &wait, SwapchainCount = 1, PSwapchains = &swapchain, PImageIndices = &index };

        VkResult result;
        lock (_device.QueueLock)
        {
            result = _device.Commands.QueuePresentKHR(_device.Queue, &info);
        }

        return result switch
        {
            VkResult.Success => true,
            VkResult.SuboptimalKhr or VkResult.ErrorOutOfDateKhr => false,
            _ => throw new VulkanException("vkQueuePresentKHR", result),
        };
    }

    public override void Recreate()
    {
        _device.WaitIdle();
        VkSwapchainKHR old = _handle;
        DestroyImages();
        Create(old);
        if (old.Value != 0)
        {
            _device.Commands.DestroySwapchainKHR(_device.Handle, old, null);
        }
    }

    private void Create(VkSwapchainKHR old)
    {
        VkInstanceCommands instance = _device.Instance.Commands;
        VkPhysicalDevice physical = _device.Physical.Handle;

        VkSurfaceCapabilitiesKHR capabilities;
        VulkanException.Check(instance.GetPhysicalDeviceSurfaceCapabilitiesKHR(physical, _surface, &capabilities), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

        uint formatCount;
        VulkanException.Check(instance.GetPhysicalDeviceSurfaceFormatsKHR(physical, _surface, &formatCount, null), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        var formats = new VkSurfaceFormatKHR[formatCount];
        fixed (VkSurfaceFormatKHR* formatData = formats)
        {
            VulkanException.Check(instance.GetPhysicalDeviceSurfaceFormatsKHR(physical, _surface, &formatCount, formatData), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        }

        VkSurfaceFormatKHR format = formats.FirstOrDefault(f => f.Format == VkFormat.B8G8R8A8Unorm && f.ColorSpace == VkColorSpaceKHR.SrgbNonlinear);
        if (format.Format == VkFormat.Undefined)
        {
            format = formats.FirstOrDefault(f => f.Format == VkFormat.R8G8B8A8Unorm && f.ColorSpace == VkColorSpaceKHR.SrgbNonlinear);
        }

        if (format.Format == VkFormat.Undefined)
        {
            throw new VulkanException($"The surface offers no UNORM format the engine can present ({string.Join(", ", formats.Select(f => f.Format))}).");
        }

        uint modeCount;
        VulkanException.Check(instance.GetPhysicalDeviceSurfacePresentModesKHR(physical, _surface, &modeCount, null), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        var modes = new VkPresentModeKHR[modeCount];
        fixed (VkPresentModeKHR* modeData = modes)
        {
            VulkanException.Check(instance.GetPhysicalDeviceSurfacePresentModesKHR(physical, _surface, &modeCount, modeData), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        }

        // FIFO is the one mode every implementation has (and waits for vertical blank); without vsync prefer mailbox, then immediate.
        VkPresentModeKHR mode = VkPresentModeKHR.Fifo;
        if (!VSync)
        {
            mode = modes.Contains(VkPresentModeKHR.Mailbox) ? VkPresentModeKHR.Mailbox : modes.Contains(VkPresentModeKHR.Immediate) ? VkPresentModeKHR.Immediate : mode;
        }

        // Some platforms report a fixed extent, others (Wayland) let the application choose within limits.
        PixelSize wanted = _sizeSource();
        uint width = capabilities.CurrentExtent.Width != uint.MaxValue ? capabilities.CurrentExtent.Width : Math.Clamp((uint)wanted.Width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width);
        uint height = capabilities.CurrentExtent.Height != uint.MaxValue ? capabilities.CurrentExtent.Height : Math.Clamp((uint)wanted.Height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height);
        if (width == 0 || height == 0)
        {
            throw new VulkanException("The surface has no area to present to.");
        }

        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount != 0)
        {
            imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
        }

        // Rendering, clears and blits into it, and screenshots out of it.
        VkImageUsageFlagBits usage = VkImageUsageFlagBits.ColorAttachment | VkImageUsageFlagBits.TransferDst | VkImageUsageFlagBits.TransferSrc;
        usage &= capabilities.SupportedUsageFlags | VkImageUsageFlagBits.ColorAttachment;

        var info = new VkSwapchainCreateInfoKHR
        {
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = format.Format,
            ImageColorSpace = format.ColorSpace,
            ImageExtent = new VkExtent2D { Width = width, Height = height },
            ImageArrayLayers = 1,
            ImageUsage = usage,
            ImageSharingMode = VkSharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = (capabilities.SupportedCompositeAlpha & VkCompositeAlphaFlagBitsKHR.Opaque) != 0 ? VkCompositeAlphaFlagBitsKHR.Opaque : VkCompositeAlphaFlagBitsKHR.Inherit,
            PresentMode = mode,
            Clipped = 1,
            OldSwapchain = old,
        };

        VkSwapchainKHR swapchain;
        VulkanException.Check(_device.Commands.CreateSwapchainKHR(_device.Handle, &info, null, &swapchain), "vkCreateSwapchainKHR");
        _handle = swapchain;
        _format = format.Format.ToEngine();
        _width = width;
        _height = height;

        uint count;
        VulkanException.Check(_device.Commands.GetSwapchainImagesKHR(_device.Handle, swapchain, &count, null), "vkGetSwapchainImagesKHR");
        _images = new VkImage[count];
        fixed (VkImage* imageData = _images)
        {
            VulkanException.Check(_device.Commands.GetSwapchainImagesKHR(_device.Handle, swapchain, &count, imageData), "vkGetSwapchainImagesKHR");
        }

        _views = new VkImageView[count];
        _wrapped = new VulkanImage[count];
        _presentSemaphores = new VkSemaphore[count];
        for (int i = 0; i < count; i++)
        {
            var viewInfo = new VkImageViewCreateInfo
            {
                Image = _images[i],
                ViewType = VkImageViewType._2D,
                Format = format.Format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlagBits.Color, LevelCount = 1, LayerCount = 1 },
            };
            VkImageView view;
            VulkanException.Check(_device.Commands.CreateImageView(_device.Handle, &viewInfo, null, &view), "vkCreateImageView");
            _views[i] = view;
            _wrapped[i] = new VulkanImage(_device, _images[i], view, new ImageDescription(_format, width, height, ImageUsage.ColorAttachment | ImageUsage.TransferSource | ImageUsage.TransferDestination));
            _presentSemaphores[i] = _device.CreateBinarySemaphore();
            _device.SetDebugName(VkObjectType.Image, _images[i].Value, $"Swapchain image {i}");
        }

        Log.Info(LogCategory, $"Swapchain {width}x{height} {format.Format}, {count} images, {mode}");
    }

    private void DestroyImages()
    {
        foreach (VkImageView view in _views)
        {
            _device.Commands.DestroyImageView(_device.Handle, view, null);
        }

        foreach (VkSemaphore semaphore in _presentSemaphores)
        {
            _device.Commands.DestroySemaphore(_device.Handle, semaphore, null);
        }

        _views = [];
        _wrapped = [];
        _presentSemaphores = [];
        _images = [];
    }

    public override void Dispose()
    {
        if (_handle.Value == 0)
        {
            return;
        }

        _device.WaitIdle();
        DestroyImages();
        _device.Commands.DestroySwapchainKHR(_device.Handle, _handle, null);
        _handle = default;
    }
}
