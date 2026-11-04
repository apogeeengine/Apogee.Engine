using System;
using System.Linq;
using Apogee.Core.Graphics;
using Apogee.Graphics.Shaders;
using Apogee.Interop;
using Apogee.Native.Vulkan;

namespace Apogee.Graphics.Vulkan;

/// <summary>A Vulkan pipeline (it uses the bindless heap's pipeline layout).</summary>
public sealed unsafe class VulkanPipeline : Pipeline
{
    private readonly VulkanDevice _device;

    internal VulkanPipeline(VulkanDevice device, VkPipeline handle, VkPipelineBindPoint bindPoint)
    {
        _device = device;
        Handle = handle;
        BindPoint = bindPoint;
    }

    public VkPipeline Handle { get; private set; }

    public VkPipelineBindPoint BindPoint { get; }

    public override void Dispose()
    {
        if (Handle.Value != 0)
        {
            _device.Commands.DestroyPipeline(_device.Handle, Handle, null);
            Handle = default;
        }
    }
}

public sealed unsafe partial class VulkanDevice
{
    public override Pipeline CreateComputePipeline(in ShaderCode shader, string? name = null)
    {
        string entryPoint = shader.EntryPoint;
        VkShaderModule module = CreateShaderModule(shader);
        try
        {
            fixed (byte* entry = Utf8.ToZeroTerminated(entryPoint))
            {
                var info = new VkComputePipelineCreateInfo
                {
                    Stage = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlagBits.Compute, Module = module, PName = entry },
                    Layout = VulkanBindless.PipelineLayout,
                    BasePipelineIndex = -1,
                };
                VkPipeline pipeline;
                VulkanException.Check(Commands.CreateComputePipelines(Handle, default, 1, &info, null, &pipeline), $"vkCreateComputePipelines ({name ?? entryPoint})");
                NameObject(VkObjectType.Pipeline, pipeline.Value, name ?? entryPoint);
                return new VulkanPipeline(this, pipeline, VkPipelineBindPoint.Compute);
            }
        }
        finally
        {
            Commands.DestroyShaderModule(Handle, module, null);
        }
    }

    public override Pipeline CreateGraphicsPipeline(GraphicsPipelineDescription description, string? name = null)
    {
        VkShaderModule vertexModule = CreateShaderModule(description.Vertex);
        VkShaderModule fragmentModule = description.Fragment is { } fragmentStage ? CreateShaderModule(fragmentStage) : default;
        VkFormat[] colorFormatArray = [.. description.ColorFormats.Select(f => f.ToVulkan())];
        try
        {
            fixed (byte* vertexEntry = Utf8.ToZeroTerminated(description.Vertex.EntryPoint))
            fixed (byte* fragmentEntry = Utf8.ToZeroTerminated(description.Fragment?.EntryPoint ?? ""))
            fixed (VkFormat* colorFormats = colorFormatArray)
            {
                var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
                stages[0] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlagBits.Vertex, Module = vertexModule, PName = vertexEntry };
                uint stageCount = 1;
                if (description.Fragment is not null)
                {
                    stages[1] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlagBits.Fragment, Module = fragmentModule, PName = fragmentEntry };
                    stageCount = 2;
                }

                var vertexInput = new VkPipelineVertexInputStateCreateInfo();
                var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = description.Topology.ToVulkan() };
                var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
                var rasterization = new VkPipelineRasterizationStateCreateInfo
                {
                    PolygonMode = VkPolygonMode.Fill,
                    CullMode = description.CullMode.ToVulkan(),
                    FrontFace = description.FrontFace.ToVulkan(),
                    LineWidth = 1f,
                };
                var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlagBits._1 };
                var depthStencil = new VkPipelineDepthStencilStateCreateInfo
                {
                    DepthTestEnable = description.DepthTest ? 1u : 0u,
                    DepthWriteEnable = description.DepthWrite ? 1u : 0u,
                    DepthCompareOp = description.DepthCompare.ToVulkan(),
                };

                var blend = BlendState(description.Blend);
                var blendAttachments = stackalloc VkPipelineColorBlendAttachmentState[description.ColorFormats.Length];
                for (int i = 0; i < description.ColorFormats.Length; i++)
                {
                    blendAttachments[i] = blend;
                }

                var colorBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = (uint)description.ColorFormats.Length, PAttachments = blendAttachments };
                var dynamicStates = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
                var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, PDynamicStates = dynamicStates };
                var rendering = new VkPipelineRenderingCreateInfo
                {
                    ColorAttachmentCount = (uint)description.ColorFormats.Length,
                    PColorAttachmentFormats = colorFormats,
                    DepthAttachmentFormat = description.DepthFormat.ToVulkan(),
                    StencilAttachmentFormat = FormatInfo.HasStencil(description.DepthFormat) ? description.DepthFormat.ToVulkan() : VkFormat.Undefined,
                };

                var info = new VkGraphicsPipelineCreateInfo
                {
                    PNext = &rendering,
                    StageCount = stageCount,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewport,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &colorBlend,
                    PDynamicState = &dynamic,
                    Layout = VulkanBindless.PipelineLayout,
                    BasePipelineIndex = -1,
                };

                VkPipeline pipeline;
                VulkanException.Check(Commands.CreateGraphicsPipelines(Handle, default, 1, &info, null, &pipeline), $"vkCreateGraphicsPipelines ({name})");
                NameObject(VkObjectType.Pipeline, pipeline.Value, name);
                return new VulkanPipeline(this, pipeline, VkPipelineBindPoint.Graphics);
            }
        }
        finally
        {
            Commands.DestroyShaderModule(Handle, vertexModule, null);
            if (fragmentModule.Value != 0)
            {
                Commands.DestroyShaderModule(Handle, fragmentModule, null);
            }
        }
    }

    private static VkPipelineColorBlendAttachmentState BlendState(BlendMode mode)
    {
        const VkColorComponentFlagBits all = VkColorComponentFlagBits.R | VkColorComponentFlagBits.G | VkColorComponentFlagBits.B | VkColorComponentFlagBits.A;
        return mode switch
        {
            BlendMode.Opaque => new VkPipelineColorBlendAttachmentState { ColorWriteMask = all },
            BlendMode.Alpha => new VkPipelineColorBlendAttachmentState
            {
                BlendEnable = 1,
                SrcColorBlendFactor = VkBlendFactor.SrcAlpha,
                DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = VkBlendOp.Add,
                SrcAlphaBlendFactor = VkBlendFactor.One,
                DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = VkBlendOp.Add,
                ColorWriteMask = all,
            },
            BlendMode.Premultiplied => new VkPipelineColorBlendAttachmentState
            {
                BlendEnable = 1,
                SrcColorBlendFactor = VkBlendFactor.One,
                DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = VkBlendOp.Add,
                SrcAlphaBlendFactor = VkBlendFactor.One,
                DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = VkBlendOp.Add,
                ColorWriteMask = all,
            },
            BlendMode.Additive => new VkPipelineColorBlendAttachmentState
            {
                BlendEnable = 1,
                SrcColorBlendFactor = VkBlendFactor.One,
                DstColorBlendFactor = VkBlendFactor.One,
                ColorBlendOp = VkBlendOp.Add,
                SrcAlphaBlendFactor = VkBlendFactor.One,
                DstAlphaBlendFactor = VkBlendFactor.One,
                AlphaBlendOp = VkBlendOp.Add,
                ColorWriteMask = all,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private VkShaderModule CreateShaderModule(in ShaderCode shader)
    {
        if (shader.Backend != ShaderBackend.Spirv)
        {
            throw new ArgumentException($"Vulkan runs SPIR-V, not {shader.Backend} code.", nameof(shader));
        }

        ReadOnlySpan<byte> spirv = shader.Code;
        if (spirv.Length == 0 || spirv.Length % 4 != 0)
        {
            throw new ArgumentException("SPIR-V is a whole number of 32-bit words.", nameof(shader));
        }

        uint[] words = new uint[spirv.Length / 4];
        spirv.CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));
        fixed (uint* code = words)
        {
            var info = new VkShaderModuleCreateInfo { CodeSize = (nuint)spirv.Length, PCode = code };
            VkShaderModule module;
            VulkanException.Check(Commands.CreateShaderModule(Handle, &info, null, &module), "vkCreateShaderModule");
            return module;
        }
    }

    private void NameObject(VkObjectType type, ulong handle, string? name)
    {
        if (name is not null)
        {
            SetDebugName(type, handle, name);
        }
    }
}
