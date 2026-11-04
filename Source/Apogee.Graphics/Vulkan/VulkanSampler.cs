using System;
using Apogee.Native.Vulkan;

namespace Apogee.Graphics.Vulkan;

public sealed unsafe class VulkanSampler : GpuSampler
{
    private readonly VulkanDevice _device;

    internal VulkanSampler(VulkanDevice device, in SamplerDescription description, string? name)
    {
        _device = device;
        bool anisotropy = device.SupportsAnisotropy && description.MaxAnisotropy > 1f;
        var info = new VkSamplerCreateInfo
        {
            MagFilter = description.Filter.ToVulkan(),
            MinFilter = description.Filter.ToVulkan(),
            MipmapMode = description.MipmapMode.ToVulkan(),
            AddressModeU = description.AddressMode.ToVulkan(),
            AddressModeV = description.AddressMode.ToVulkan(),
            AddressModeW = description.AddressMode.ToVulkan(),
            AnisotropyEnable = anisotropy ? 1u : 0u,
            MaxAnisotropy = anisotropy ? Math.Min(description.MaxAnisotropy, device.MaxSamplerAnisotropy) : 1f,
            CompareEnable = description.Compare ? 1u : 0u,
            CompareOp = VkCompareOp.GreaterOrEqual,
            MinLod = 0f,
            MaxLod = 1000f,
            BorderColor = VkBorderColor.FloatOpaqueBlack,
        };

        VkSampler sampler;
        VulkanException.Check(device.Commands.CreateSampler(device.Handle, &info, null, &sampler), "vkCreateSampler");
        Handle = sampler;
        if (name is not null)
        {
            device.SetDebugName(VkObjectType.Sampler, sampler.Value, name);
        }
    }

    public VkSampler Handle { get; private set; }

    public override void Dispose()
    {
        if (Handle.Value != 0)
        {
            _device.Commands.DestroySampler(_device.Handle, Handle, null);
            Handle = default;
        }
    }
}
