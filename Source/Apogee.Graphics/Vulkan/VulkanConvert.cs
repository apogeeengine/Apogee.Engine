using Apogee.Core.Graphics;
using Apogee.Native.Vulkan;

namespace Apogee.Graphics.Vulkan;

/// <summary>The engine's enumerations as Vulkan's. The numbers are the same on purpose (see <see cref="PixelFormat"/> and <c>Enums.cs</c>), so these are casts.</summary>
public static class VulkanConvert
{
    public static VkFormat ToVulkan(this PixelFormat format) => (VkFormat)(int)format;

    public static PixelFormat ToEngine(this VkFormat format) => (PixelFormat)(int)format;

    public static VkBufferUsageFlagBits ToVulkan(this BufferUsage usage) => (VkBufferUsageFlagBits)(uint)usage;

    public static VkImageUsageFlagBits ToVulkan(this ImageUsage usage) => (VkImageUsageFlagBits)(uint)usage;

    public static VkSampleCountFlagBits ToVulkan(this SampleCount samples) => (VkSampleCountFlagBits)(uint)samples;

    public static VkCullModeFlagBits ToVulkan(this CullMode mode) => (VkCullModeFlagBits)(uint)mode;

    public static VkFrontFace ToVulkan(this FrontFace face) => (VkFrontFace)(int)face;

    public static VkCompareOp ToVulkan(this CompareOp op) => (VkCompareOp)(int)op;

    public static VkPrimitiveTopology ToVulkan(this PrimitiveTopology topology) => (VkPrimitiveTopology)(int)topology;

    public static VkFilter ToVulkan(this Filter filter) => (VkFilter)(int)filter;

    public static VkSamplerMipmapMode ToVulkan(this MipmapMode mode) => (VkSamplerMipmapMode)(int)mode;

    public static VkSamplerAddressMode ToVulkan(this AddressMode mode) => (VkSamplerAddressMode)(int)mode;

    public static VkIndexType ToVulkan(this IndexFormat format) => (VkIndexType)(int)format;

    /// <summary>All the aspects of an image of this format (depth and stencil for a depth-stencil format).</summary>
    public static VkImageAspectFlagBits Aspect(PixelFormat format) =>
        !FormatInfo.IsDepth(format) ? VkImageAspectFlagBits.Color
        : FormatInfo.HasStencil(format) ? VkImageAspectFlagBits.Depth | VkImageAspectFlagBits.Stencil
        : VkImageAspectFlagBits.Depth;

    /// <summary>The aspect a sampled view reads: depth only, for a depth-stencil format.</summary>
    public static VkImageAspectFlagBits SampledAspect(PixelFormat format) => FormatInfo.IsDepth(format) ? VkImageAspectFlagBits.Depth : VkImageAspectFlagBits.Color;
}
