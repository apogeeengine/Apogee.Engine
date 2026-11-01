using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Apogee.Native.Vulkan;

public static unsafe partial class Vk
{
    /// <summary>VK_MAKE_API_VERSION</summary>
    public static uint MakeApiVersion(uint variant, uint major, uint minor, uint patch) =>
        (variant << 29) | (major << 22) | (minor << 12) | patch;

    public static uint ApiVersion10 => MakeApiVersion(0, 1, 0, 0);

    public static uint ApiVersion11 => MakeApiVersion(0, 1, 1, 0);

    public static uint ApiVersion12 => MakeApiVersion(0, 1, 2, 0);

    public static uint ApiVersion13 => MakeApiVersion(0, 1, 3, 0);

    public static uint ApiVersion14 => MakeApiVersion(0, 1, 4, 0);

    // The generated command tables pass u8 literals: the compiler stores those in static memory, null terminated, so the
    // pointer to the first byte is a valid, stable C string.
    internal static nint LoadInstanceProc(VkInstance instance, ReadOnlySpan<byte> name) =>
        (nint)GetInstanceProcAddr(instance, (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(name)));

    internal static nint LoadDeviceProc(VkInstanceCommands instance, VkDevice device, ReadOnlySpan<byte> name) =>
        (nint)instance.GetDeviceProcAddr(device, (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(name)));
}
