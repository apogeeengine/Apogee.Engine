using System;
using System.Collections.Generic;
using Apogee.Core.Graphics;
using Apogee.Native.Vulkan;

namespace Apogee.Graphics.Vulkan;

/// <summary>
/// The one descriptor set every pipeline uses: arrays of sampled images, samplers and storage images that shaders index with plain integers
/// (carried in push constants or in buffers). Entries are written once and stay valid until freed; slot 0 of the images and the samplers
/// is a white texel and a linear repeat sampler, so an unset index still reads something sane.
/// </summary>
public sealed unsafe class VulkanBindlessHeap : BindlessHeap
{
    private readonly VulkanDevice _device;
    private readonly object _lock = new();
    private readonly SlotAllocator _sampledImages = new(MaxSampledImages);
    private readonly SlotAllocator _samplers = new(MaxSamplers);
    private readonly SlotAllocator _storageImages = new(MaxStorageImages);
    private readonly VkDescriptorPool _pool;
    private GpuImage? _whiteTexel;
    private VulkanSampler? _defaultSampler;

    internal VulkanBindlessHeap(VulkanDevice device)
    {
        _device = device;
        VkDeviceCommands commands = device.Commands;

        var bindings = stackalloc VkDescriptorSetLayoutBinding[3];
        bindings[0] = new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.SampledImage, DescriptorCount = MaxSampledImages, StageFlags = VkShaderStageFlagBits.All };
        bindings[1] = new VkDescriptorSetLayoutBinding { Binding = 1, DescriptorType = VkDescriptorType.Sampler, DescriptorCount = MaxSamplers, StageFlags = VkShaderStageFlagBits.All };
        bindings[2] = new VkDescriptorSetLayoutBinding { Binding = 2, DescriptorType = VkDescriptorType.StorageImage, DescriptorCount = MaxStorageImages, StageFlags = VkShaderStageFlagBits.All };

        const VkDescriptorBindingFlagBits flag = VkDescriptorBindingFlagBits.PartiallyBound | VkDescriptorBindingFlagBits.UpdateAfterBind | VkDescriptorBindingFlagBits.UpdateUnusedWhilePending;
        var flags = stackalloc VkDescriptorBindingFlagBits[3] { flag, flag, flag };
        var flagsInfo = new VkDescriptorSetLayoutBindingFlagsCreateInfo { BindingCount = 3, PBindingFlags = flags };
        var layoutInfo = new VkDescriptorSetLayoutCreateInfo
        {
            PNext = &flagsInfo,
            Flags = VkDescriptorSetLayoutCreateFlagBits.UpdateAfterBindPool,
            BindingCount = 3,
            PBindings = bindings,
        };
        VkDescriptorSetLayout layout;
        VulkanException.Check(commands.CreateDescriptorSetLayout(device.Handle, &layoutInfo, null, &layout), "vkCreateDescriptorSetLayout");
        Layout = layout;

        var sizes = stackalloc VkDescriptorPoolSize[3];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.SampledImage, DescriptorCount = MaxSampledImages };
        sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.Sampler, DescriptorCount = MaxSamplers };
        sizes[2] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = MaxStorageImages };
        var poolInfo = new VkDescriptorPoolCreateInfo { Flags = VkDescriptorPoolCreateFlagBits.UpdateAfterBind, MaxSets = 1, PoolSizeCount = 3, PPoolSizes = sizes };
        VkDescriptorPool pool;
        VulkanException.Check(commands.CreateDescriptorPool(device.Handle, &poolInfo, null, &pool), "vkCreateDescriptorPool");
        _pool = pool;

        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &layout };
        VkDescriptorSet set;
        VulkanException.Check(commands.AllocateDescriptorSets(device.Handle, &allocate, &set), "vkAllocateDescriptorSets");
        Set = set;

        var push = new VkPushConstantRange { StageFlags = VkShaderStageFlagBits.All, Offset = 0, Size = PushConstantBytes };
        var pipelineLayoutInfo = new VkPipelineLayoutCreateInfo { SetLayoutCount = 1, PSetLayouts = &layout, PushConstantRangeCount = 1, PPushConstantRanges = &push };
        VkPipelineLayout pipelineLayout;
        VulkanException.Check(commands.CreatePipelineLayout(device.Handle, &pipelineLayoutInfo, null, &pipelineLayout), "vkCreatePipelineLayout");
        PipelineLayout = pipelineLayout;

        device.SetDebugName(VkObjectType.DescriptorSetLayout, layout.Value, "Bindless layout");
        device.SetDebugName(VkObjectType.DescriptorSet, set.Value, "Bindless set");
        device.SetDebugName(VkObjectType.PipelineLayout, pipelineLayout.Value, "Engine pipeline layout");
    }

    public VkDescriptorSetLayout Layout { get; }

    public VkDescriptorSet Set { get; }

    /// <summary>The layout of every pipeline: the bindless set at 0 and <see cref="PushConstantBytes"/> of push constants for all stages.</summary>
    public VkPipelineLayout PipelineLayout { get; }

    /// <summary>Fills slot 0 of the images and samplers. Called by the device once uploads work.</summary>
    internal void CreateDefaults()
    {
        _whiteTexel = _device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, 1, 1, ImageUsage.Sampled), "White texel");
        _device.UploadImage(_whiteTexel, [0xFF, 0xFF, 0xFF, 0xFF], [new ImageRegion(0, 0, 0, 1, 1)], ImageState.ShaderRead);
        _defaultSampler = (VulkanSampler)_device.CreateSampler(SamplerDescription.LinearRepeat, "Default sampler");
        WriteSampledImage(_sampledImages.Reserve(0), ((VulkanImageView)_whiteTexel.View).Handle);
        WriteSampler(_samplers.Reserve(0), _defaultSampler.Handle);
    }

    public override uint AddSampledImage(ImageView view)
    {
        lock (_lock)
        {
            uint index = _sampledImages.Allocate();
            WriteSampledImage(index, ((VulkanImageView)view).Handle);
            return index;
        }
    }

    public override void RemoveSampledImage(uint index)
    {
        lock (_lock)
        {
            _sampledImages.Free(index);
        }
    }

    public override uint AddSampler(GpuSampler sampler)
    {
        lock (_lock)
        {
            uint index = _samplers.Allocate();
            WriteSampler(index, ((VulkanSampler)sampler).Handle);
            return index;
        }
    }

    public override void RemoveSampler(uint index)
    {
        lock (_lock)
        {
            _samplers.Free(index);
        }
    }

    public override uint AddStorageImage(ImageView view)
    {
        lock (_lock)
        {
            uint index = _storageImages.Allocate();
            WriteImage(2, index, VkDescriptorType.StorageImage, default, ((VulkanImageView)view).Handle, VkImageLayout.General);
            return index;
        }
    }

    public override void RemoveStorageImage(uint index)
    {
        lock (_lock)
        {
            _storageImages.Free(index);
        }
    }

    public override int SampledImageCount { get { lock (_lock) { return _sampledImages.Used; } } }

    private void WriteSampledImage(uint index, VkImageView view) => WriteImage(0, index, VkDescriptorType.SampledImage, default, view, VkImageLayout.ShaderReadOnlyOptimal);

    private void WriteSampler(uint index, VkSampler sampler) => WriteImage(1, index, VkDescriptorType.Sampler, sampler, default, VkImageLayout.Undefined);

    private void WriteImage(uint binding, uint index, VkDescriptorType type, VkSampler sampler, VkImageView view, VkImageLayout layout)
    {
        var image = new VkDescriptorImageInfo { Sampler = sampler, ImageView = view, ImageLayout = layout };
        var write = new VkWriteDescriptorSet { DstSet = Set, DstBinding = binding, DstArrayElement = index, DescriptorCount = 1, DescriptorType = type, PImageInfo = &image };
        _device.Commands.UpdateDescriptorSets(_device.Handle, 1, &write, 0, null);
    }

    public override void Dispose()
    {
        _whiteTexel?.Dispose();
        _defaultSampler?.Dispose();
        VkDeviceCommands commands = _device.Commands;
        commands.DestroyPipelineLayout(_device.Handle, PipelineLayout, null);
        commands.DestroyDescriptorPool(_device.Handle, _pool, null);
        commands.DestroyDescriptorSetLayout(_device.Handle, Layout, null);
    }
}
