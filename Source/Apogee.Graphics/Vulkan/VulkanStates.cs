using Apogee.Native.Vulkan;

namespace Apogee.Graphics.Vulkan;

/// <summary>The table from a state to the stage, access and layout a barrier uses.</summary>
public static class VulkanStates
{
    public static (VkPipelineStageFlagBits2 Stages, VkAccessFlagBits2 Access, VkImageLayout Layout) Describe(ImageState state) => state switch
    {
        ImageState.Undefined => (VkPipelineStageFlagBits2.None, VkAccessFlagBits2.None, VkImageLayout.Undefined),
        ImageState.TransferSource => (VkPipelineStageFlagBits2.AllTransfer, VkAccessFlagBits2.TransferRead, VkImageLayout.TransferSrcOptimal),
        ImageState.TransferDestination => (VkPipelineStageFlagBits2.AllTransfer, VkAccessFlagBits2.TransferWrite, VkImageLayout.TransferDstOptimal),
        ImageState.ShaderRead => (VkPipelineStageFlagBits2.FragmentShader | VkPipelineStageFlagBits2.ComputeShader, VkAccessFlagBits2.ShaderSampledRead, VkImageLayout.ShaderReadOnlyOptimal),
        ImageState.StorageReadWrite => (
            VkPipelineStageFlagBits2.FragmentShader | VkPipelineStageFlagBits2.ComputeShader,
            VkAccessFlagBits2.ShaderStorageRead | VkAccessFlagBits2.ShaderStorageWrite,
            VkImageLayout.General),
        ImageState.ColorAttachment => (
            VkPipelineStageFlagBits2.ColorAttachmentOutput,
            VkAccessFlagBits2.ColorAttachmentRead | VkAccessFlagBits2.ColorAttachmentWrite,
            VkImageLayout.ColorAttachmentOptimal),
        ImageState.DepthAttachment => (
            VkPipelineStageFlagBits2.EarlyFragmentTests | VkPipelineStageFlagBits2.LateFragmentTests,
            VkAccessFlagBits2.DepthStencilAttachmentRead | VkAccessFlagBits2.DepthStencilAttachmentWrite,
            VkImageLayout.DepthAttachmentOptimal),
        ImageState.DepthRead => (
            VkPipelineStageFlagBits2.EarlyFragmentTests | VkPipelineStageFlagBits2.LateFragmentTests | VkPipelineStageFlagBits2.FragmentShader | VkPipelineStageFlagBits2.ComputeShader,
            VkAccessFlagBits2.DepthStencilAttachmentRead | VkAccessFlagBits2.ShaderSampledRead,
            VkImageLayout.DepthReadOnlyOptimal),
        ImageState.Present => (VkPipelineStageFlagBits2.None, VkAccessFlagBits2.None, VkImageLayout.PresentSrcKhr),
        _ => throw new System.ArgumentOutOfRangeException(nameof(state)),
    };

    public static (VkPipelineStageFlagBits2 Stages, VkAccessFlagBits2 Access) Describe(BufferState state) => state switch
    {
        BufferState.Undefined => (VkPipelineStageFlagBits2.None, VkAccessFlagBits2.None),
        BufferState.TransferSource => (VkPipelineStageFlagBits2.AllTransfer, VkAccessFlagBits2.TransferRead),
        BufferState.TransferDestination => (VkPipelineStageFlagBits2.AllTransfer, VkAccessFlagBits2.TransferWrite),
        BufferState.ShaderRead => (
            VkPipelineStageFlagBits2.VertexShader | VkPipelineStageFlagBits2.FragmentShader | VkPipelineStageFlagBits2.ComputeShader,
            VkAccessFlagBits2.ShaderRead | VkAccessFlagBits2.UniformRead),
        BufferState.ShaderWrite => (
            VkPipelineStageFlagBits2.VertexShader | VkPipelineStageFlagBits2.FragmentShader | VkPipelineStageFlagBits2.ComputeShader,
            VkAccessFlagBits2.ShaderRead | VkAccessFlagBits2.ShaderWrite),
        BufferState.VertexIndexRead => (VkPipelineStageFlagBits2.IndexInput | VkPipelineStageFlagBits2.VertexAttributeInput, VkAccessFlagBits2.IndexRead),
        BufferState.IndirectRead => (VkPipelineStageFlagBits2.DrawIndirect, VkAccessFlagBits2.IndirectCommandRead),
        BufferState.HostRead => (VkPipelineStageFlagBits2.Host, VkAccessFlagBits2.HostRead),
        _ => throw new System.ArgumentOutOfRangeException(nameof(state)),
    };
}
