using System;
using Apogee.Native.Vulkan;

namespace Apogee.Graphics.Vulkan;

/// <summary>A Vulkan call failed or the machine cannot do what the engine needs; the message says which.</summary>
public sealed class VulkanException : GraphicsException
{
    public VulkanException(string message)
        : base(message)
    {
    }

    public VulkanException(string message, VkResult result)
        : base($"{message}: {result}")
    {
        Result = result;
    }

    public VkResult Result { get; }

    internal static void Check(VkResult result, string what)
    {
        if (result < 0)
        {
            throw new VulkanException(what, result);
        }
    }
}
