using Apogee.Native.Vma;
using Apogee.Native.Vulkan;
using VmaApi = Apogee.Native.Vma.Vma;
using VmaAllocation = Apogee.Native.Vma.Allocation;

namespace Apogee.Graphics.Vulkan;

/// <summary>A Vulkan buffer with its VMA allocation.</summary>
public sealed unsafe class VulkanBuffer : GpuBuffer
{
    private readonly VulkanDevice _device;
    private VmaAllocation* _allocation;

    internal VulkanBuffer(VulkanDevice device, ulong size, BufferUsage usage, MemoryKind kind, string? name)
        : base(size, usage, kind)
    {
        _device = device;

        var bufferInfo = new VkBufferCreateInfo
        {
            Size = size,
            Usage = Usage.ToVulkan() | VkBufferUsageFlagBits.ShaderDeviceAddress,
            SharingMode = VkSharingMode.Exclusive,
        };
        var allocationInfo = new AllocationCreateInfo
        {
            Usage = kind == MemoryKind.DeviceLocal ? MemoryUsage.AutoPreferDevice : MemoryUsage.Auto,
            Flags = kind switch
            {
                MemoryKind.Upload => AllocationCreateFlagBits.HostAccessSequentialWrite | AllocationCreateFlagBits.Mapped,
                MemoryKind.Readback => AllocationCreateFlagBits.HostAccessRandom | AllocationCreateFlagBits.Mapped,
                _ => 0,
            },
        };

        VkBuffer buffer;
        VmaAllocation* allocation;
        AllocationInfo info;
        VulkanException.Check(VmaApi.CreateBuffer(device.Allocator, &bufferInfo, &allocationInfo, &buffer, &allocation, &info), "vmaCreateBuffer");
        Handle = buffer;
        _allocation = allocation;
        Mapped = (byte*)info.PMappedData;

        var addressInfo = new VkBufferDeviceAddressInfo { Buffer = buffer };
        Address = device.Commands.GetBufferDeviceAddress(device.Handle, &addressInfo);

        if (name is not null)
        {
            device.SetDebugName(VkObjectType.Buffer, buffer.Value, name);
        }
    }

    public VkBuffer Handle { get; }

    /// <inheritdoc/>
    public override ulong Address { get; }

    protected override void Flush(ulong offset, ulong length) =>
        VulkanException.Check(VmaApi.FlushAllocation(_device.Allocator, _allocation, offset, length), "vmaFlushAllocation");

    protected override void Invalidate(ulong offset, ulong length) =>
        VulkanException.Check(VmaApi.InvalidateAllocation(_device.Allocator, _allocation, offset, length), "vmaInvalidateAllocation");

    public override void Dispose()
    {
        if (_allocation is null)
        {
            return;
        }

        VmaApi.DestroyBuffer(_device.Allocator, Handle, _allocation);
        _allocation = null;
    }
}
