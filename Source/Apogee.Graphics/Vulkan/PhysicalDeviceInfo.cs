using Apogee.Native.Vulkan;

namespace Apogee.Graphics.Vulkan;

/// <summary>A GPU the loader found, and whether the engine can use it.</summary>
/// <param name="Reasons">Why it cannot be used (missing features, no queue that can present); empty when it can.</param>
public sealed record PhysicalDeviceInfo(
    VkPhysicalDevice Handle,
    string Name,
    VkPhysicalDeviceType Type,
    uint ApiVersion,
    uint QueueFamily,
    string[] Reasons)
{
    public bool Suitable => Reasons.Length == 0;

    public override string ToString() => $"{Name} ({Type}, Vulkan {ApiVersion >> 22}.{(ApiVersion >> 12) & 0x3ff}){(Suitable ? "" : " unusable: " + string.Join("; ", Reasons))}";
}
