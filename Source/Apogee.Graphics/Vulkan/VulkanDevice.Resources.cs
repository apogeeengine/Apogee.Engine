using System;
using Apogee.Native.Vulkan;
using Apogee.Platform;

namespace Apogee.Graphics.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    public override GpuBuffer CreateBuffer(ulong size, BufferUsage usage, MemoryKind kind = MemoryKind.DeviceLocal, string? name = null) =>
        new VulkanBuffer(this, size, usage, kind, name);

    public override GpuImage CreateImage(in ImageDescription description, string? name = null) => new VulkanImage(this, description, name);

    public override GpuSampler CreateSampler(in SamplerDescription description, string? name = null) => new VulkanSampler(this, description, name);

    /// <summary>A swapchain for the window the device was created for.</summary>
    public override Swapchain CreateSwapchain(bool vsync = true)
    {
        if (_window is null || _surface.Value == 0)
        {
            throw new InvalidOperationException("The device was created without a window, so it cannot present.");
        }

        Window window = _window;
        return new VulkanSwapchain(this, _surface, () => window.PixelSize, vsync);
    }

    public override FrameLoop CreateFrameLoop(Swapchain? swapchain = null, int framesInFlight = 2, int arenaBytes = 4 * 1024 * 1024) =>
        new VulkanFrameLoop(this, swapchain, framesInFlight, arenaBytes);

    internal VkSemaphore CreateBinarySemaphore()
    {
        var info = new VkSemaphoreCreateInfo();
        VkSemaphore semaphore;
        VulkanException.Check(Commands.CreateSemaphore(Handle, &info, null, &semaphore), "vkCreateSemaphore");
        return semaphore;
    }

    internal VkSemaphore CreateTimelineSemaphore(ulong initialValue = 0)
    {
        var type = new VkSemaphoreTypeCreateInfo { SemaphoreType = VkSemaphoreType.Timeline, InitialValue = initialValue };
        var info = new VkSemaphoreCreateInfo { PNext = &type };
        VkSemaphore semaphore;
        VulkanException.Check(Commands.CreateSemaphore(Handle, &info, null, &semaphore), "vkCreateSemaphore");
        return semaphore;
    }
}
