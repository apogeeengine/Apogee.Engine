using System;
using Apogee.Native.Vulkan;

namespace Apogee.Graphics.Vulkan;

/// <summary>The immediate context of a Vulkan device: one command buffer and a fence.</summary>
public sealed unsafe class VulkanImmediateContext : ImmediateContext
{
    private readonly VulkanDevice _device;
    private readonly object _lock = new();
    private readonly VkCommandPool _pool;
    private readonly VkCommandBuffer _commandBuffer;
    private readonly VkFence _fence;
    private readonly VulkanCommandRecorder _recorder;

    internal VulkanImmediateContext(VulkanDevice device)
    {
        _device = device;
        VkDeviceCommands commands = device.Commands;

        var poolInfo = new VkCommandPoolCreateInfo { Flags = VkCommandPoolCreateFlagBits.ResetCommandBuffer | VkCommandPoolCreateFlagBits.Transient, QueueFamilyIndex = device.QueueFamily };
        VkCommandPool pool;
        VulkanException.Check(commands.CreateCommandPool(device.Handle, &poolInfo, null, &pool), "vkCreateCommandPool");
        _pool = pool;

        var allocate = new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary, CommandBufferCount = 1 };
        VkCommandBuffer commandBuffer;
        VulkanException.Check(commands.AllocateCommandBuffers(device.Handle, &allocate, &commandBuffer), "vkAllocateCommandBuffers");
        _commandBuffer = commandBuffer;

        var fenceInfo = new VkFenceCreateInfo();
        VkFence fence;
        VulkanException.Check(commands.CreateFence(device.Handle, &fenceInfo, null, &fence), "vkCreateFence");
        _fence = fence;

        _recorder = new VulkanCommandRecorder(device, commandBuffer);
    }

    public override void Submit(Action<CommandRecorder> record)
    {
        lock (_lock)
        {
            VkDeviceCommands commands = _device.Commands;
            VulkanException.Check(commands.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");

            var begin = new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlagBits.OneTimeSubmit };
            VulkanException.Check(commands.BeginCommandBuffer(_commandBuffer, &begin), "vkBeginCommandBuffer");
            record(_recorder);
            VulkanException.Check(commands.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

            var commandInfo = new VkCommandBufferSubmitInfo { CommandBuffer = _commandBuffer };
            var submit = new VkSubmitInfo2 { CommandBufferInfoCount = 1, PCommandBufferInfos = &commandInfo };
            lock (_device.QueueLock)
            {
                VulkanException.Check(commands.QueueSubmit2(_device.Queue, 1, &submit, _fence), "vkQueueSubmit2");
            }

            VkFence fence = _fence;
            VulkanException.Check(commands.WaitForFences(_device.Handle, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
            VulkanException.Check(commands.ResetFences(_device.Handle, 1, &fence), "vkResetFences");
        }
    }

    public override void Dispose()
    {
        VkDeviceCommands commands = _device.Commands;
        commands.DestroyFence(_device.Handle, _fence, null);
        commands.DestroyCommandPool(_device.Handle, _pool, null);
    }
}
