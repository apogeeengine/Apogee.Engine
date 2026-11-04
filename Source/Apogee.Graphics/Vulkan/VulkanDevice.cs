using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Apogee.Core.Graphics;
using Apogee.Core.Logging;
using Apogee.Graphics.Shaders;
using Apogee.Interop;
using Apogee.Native.Vma;
using Apogee.Native.Vulkan;
using Apogee.Platform;
using SDL = Apogee.Native.Sdl.SDL;
using VmaAllocator = Apogee.Native.Vma.Allocator;
using VmaApi = Apogee.Native.Vma.Vma;

namespace Apogee.Graphics.Vulkan;

/// <summary>
/// The Vulkan implementation of <see cref="GraphicsDevice"/>: the instance, the logical device and everything created with it (the queue, the VMA allocator, the device commands). It
/// owns the instance and, when it was created for a window, the window's surface. The engine's baseline is Vulkan 1.3 with dynamic rendering, synchronization2, buffer device
/// addresses and descriptor indexing; a GPU without them is rejected with the reason.
/// </summary>
public sealed unsafe partial class VulkanDevice : GraphicsDevice
{
    private const string LogCategory = "Graphics";

    private VmaAllocator* _allocator;
    private readonly bool _ownsInstance;
    private readonly Window? _window;
    private VkSurfaceKHR _surface;
    private float _timestampPeriod;
    private bool _supportsTimestamps;
    private ulong _minUniformBufferOffsetAlignment;
    private float _maxSamplerAnisotropy;
    private bool _supportsBc;
    private bool _supportsAstc;
    private bool _supportsAnisotropy;

    private VulkanDevice(VulkanInstance instance, bool ownsInstance, PhysicalDeviceInfo physical, VkDevice handle, VkDeviceCommands commands, VkQueue queue, VkSurfaceKHR surface, Window? window)
    {
        Instance = instance;
        _ownsInstance = ownsInstance;
        Physical = physical;
        Handle = handle;
        Commands = commands;
        Queue = queue;
        QueueFamily = physical.QueueFamily;
        _surface = surface;
        _window = window;
        SupportsSwapchain = surface.Value != 0;
    }

    public VulkanInstance Instance { get; }

    public PhysicalDeviceInfo Physical { get; }

    public VkDevice Handle { get; }

    public VkDeviceCommands Commands { get; }

    /// <summary>The one queue the engine submits to: graphics, compute and transfer, and presentation when a surface was given.</summary>
    public VkQueue Queue { get; }

    public uint QueueFamily { get; }

    public bool SupportsSwapchain { get; }

    public VmaAllocator* Allocator => _allocator;

    /// <summary>Held while submitting to <see cref="Queue"/>: Vulkan queues need external synchronization.</summary>
    public object QueueLock { get; } = new();

    /// <summary>The bindless heap as a Vulkan one: the descriptor set and the pipeline layout.</summary>
    public VulkanBindlessHeap VulkanBindless => (VulkanBindlessHeap)Bindless;

    public override GraphicsBackend Backend => GraphicsBackend.Vulkan;

    public override ShaderBackend ShaderBackend => ShaderBackend.Spirv;

    public override string Name => Physical.Name;

    public override GpuKind Kind => Physical.Type switch
    {
        VkPhysicalDeviceType.DiscreteGpu => GpuKind.Discrete,
        VkPhysicalDeviceType.IntegratedGpu => GpuKind.Integrated,
        VkPhysicalDeviceType.VirtualGpu => GpuKind.Virtual,
        VkPhysicalDeviceType.Cpu => GpuKind.Software,
        _ => GpuKind.Other,
    };

    public override float TimestampPeriod => _timestampPeriod;

    public override bool SupportsTimestamps => _supportsTimestamps;

    public uint MaxPushConstantsSize { get; private set; }

    public override ulong MinUniformBufferOffsetAlignment => _minUniformBufferOffsetAlignment;

    public override float MaxSamplerAnisotropy => _maxSamplerAnisotropy;

    public override bool SupportsBc => _supportsBc;

    public override bool SupportsAstc => _supportsAstc;

    public override bool SupportsAnisotropy => _supportsAnisotropy;

    public override int ValidationErrorCount => Instance.ErrorCount;

    public override string[] TakeValidationErrors() => Instance.TakeErrors();

    /// <summary>The GPUs of a new instance, for <see cref="GraphicsDevice.EnumerateDevices"/>.</summary>
    internal static GpuInfo[] EnumerateGpus(GraphicsDeviceOptions options)
    {
        using VulkanInstance instance = VulkanInstance.Create(InstanceOptions(options));
        VkSurfaceKHR surface = CreateSurface(instance, options.Window);
        try
        {
            return [.. EnumeratePhysicalDevices(instance, surface).Select(d => new GpuInfo(d.Name, KindOf(d.Type), $"Vulkan {d.ApiVersion >> 22}.{(d.ApiVersion >> 12) & 0x3ff}", d.Reasons))];
        }
        finally
        {
            DestroySurface(instance, surface);
        }
    }

    private static GpuKind KindOf(VkPhysicalDeviceType type) => type switch
    {
        VkPhysicalDeviceType.DiscreteGpu => GpuKind.Discrete,
        VkPhysicalDeviceType.IntegratedGpu => GpuKind.Integrated,
        VkPhysicalDeviceType.VirtualGpu => GpuKind.Virtual,
        VkPhysicalDeviceType.Cpu => GpuKind.Software,
        _ => GpuKind.Other,
    };

    private static VulkanInstanceOptions InstanceOptions(GraphicsDeviceOptions options) => new()
    {
        ApplicationName = options.ApplicationName,
        WindowSystemExtensions = options.Window is null ? [] : WindowSystemExtensions(),
    };

    /// <summary>The instance extensions Vulkan needs to present to SDL windows on this platform (<c>VK_KHR_surface</c> and the window system's).</summary>
    private static string[] WindowSystemExtensions()
    {
        uint count;
        byte** names = SDL.VulkanGetInstanceExtensions(&count);
        if (names is null)
        {
            throw new VulkanException($"SDL_Vulkan_GetInstanceExtensions failed: {Utf8.FromPointer(SDL.GetError())}");
        }

        string[] extensions = new string[count];
        for (int i = 0; i < extensions.Length; i++)
        {
            extensions[i] = Utf8.FromPointer(names[i])!;
        }

        return extensions;
    }

    private static VkSurfaceKHR CreateSurface(VulkanInstance instance, Window? window)
    {
        if (window is null)
        {
            return default;
        }

        VkSurfaceKHR surface;
        if (!SDL.VulkanCreateSurface(window.NativeHandle, instance.Handle, null, &surface))
        {
            throw new VulkanException($"SDL_Vulkan_CreateSurface failed (was the window created with WindowOptions.Vulkan?): {Utf8.FromPointer(SDL.GetError())}");
        }

        return surface;
    }

    private static void DestroySurface(VulkanInstance instance, VkSurfaceKHR surface)
    {
        if (surface.Value != 0)
        {
            SDL.VulkanDestroySurface(instance.Handle, surface, null);
        }
    }

    /// <summary>Lists the GPUs the loader reports, with whether each meets the baseline.</summary>
    public static PhysicalDeviceInfo[] EnumeratePhysicalDevices(VulkanInstance instance, VkSurfaceKHR surface = default)
    {
        VkInstanceCommands commands = instance.Commands;
        uint count;
        VulkanException.Check(commands.EnumeratePhysicalDevices(instance.Handle, &count, null), "vkEnumeratePhysicalDevices");
        var handles = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* data = handles)
        {
            VulkanException.Check(commands.EnumeratePhysicalDevices(instance.Handle, &count, data), "vkEnumeratePhysicalDevices");
        }

        return [.. handles.Select(h => Describe(instance, h, surface))];
    }

    private static PhysicalDeviceInfo Describe(VulkanInstance instance, VkPhysicalDevice handle, VkSurfaceKHR surface)
    {
        VkInstanceCommands commands = instance.Commands;
        VkPhysicalDeviceProperties properties;
        commands.GetPhysicalDeviceProperties(handle, &properties);

        var reasons = new List<string>();
        if (properties.ApiVersion < Vk.ApiVersion13)
        {
            reasons.Add($"Vulkan {properties.ApiVersion >> 22}.{(properties.ApiVersion >> 12) & 0x3ff} (1.3 needed)");
        }

        // The first family that can do graphics and compute (and present, when a surface was given).
        uint familyCount;
        commands.GetPhysicalDeviceQueueFamilyProperties(handle, &familyCount, null);
        var families = new VkQueueFamilyProperties[familyCount];
        uint family = uint.MaxValue;
        fixed (VkQueueFamilyProperties* data = families)
        {
            commands.GetPhysicalDeviceQueueFamilyProperties(handle, &familyCount, data);
            for (uint i = 0; i < familyCount && family == uint.MaxValue; i++)
            {
                const VkQueueFlagBits needed = VkQueueFlagBits.Graphics | VkQueueFlagBits.Compute;
                if ((data[i].QueueFlags & needed) != needed)
                {
                    continue;
                }

                uint present = 1;
                if (surface.Value != 0)
                {
                    VulkanException.Check(commands.GetPhysicalDeviceSurfaceSupportKHR(handle, i, surface, &present), "vkGetPhysicalDeviceSurfaceSupportKHR");
                }

                if (present != 0)
                {
                    family = i;
                }
            }
        }

        if (family == uint.MaxValue)
        {
            reasons.Add(surface.Value != 0 ? "no graphics and compute queue that can present to the window" : "no graphics and compute queue");
        }

        if (properties.ApiVersion >= Vk.ApiVersion13)
        {
            reasons.AddRange(MissingFeatures(commands, handle));
        }

        return new PhysicalDeviceInfo(handle, Utf8.FromPointer(properties.DeviceName)!, properties.DeviceType, properties.ApiVersion, family, [.. reasons]);
    }

    private static List<string> MissingFeatures(VkInstanceCommands commands, VkPhysicalDevice handle)
    {
        var vulkan11 = new VkPhysicalDeviceVulkan11Features();
        var vulkan12 = new VkPhysicalDeviceVulkan12Features { PNext = &vulkan11 };
        var vulkan13 = new VkPhysicalDeviceVulkan13Features { PNext = &vulkan12 };
        var features = new VkPhysicalDeviceFeatures2 { PNext = &vulkan13 };
        commands.GetPhysicalDeviceFeatures2(handle, &features);

        var missing = new List<string>();
        void Require(uint supported, string name)
        {
            if (supported == 0)
            {
                missing.Add(name);
            }
        }

        Require(vulkan11.ShaderDrawParameters, "shaderDrawParameters");
        Require(vulkan13.ShaderDemoteToHelperInvocation, "shaderDemoteToHelperInvocation");
        Require(vulkan13.DynamicRendering, "dynamicRendering");
        Require(vulkan13.Synchronization2, "synchronization2");
        Require(vulkan13.Maintenance4, "maintenance4");
        Require(vulkan12.BufferDeviceAddress, "bufferDeviceAddress");
        Require(vulkan12.TimelineSemaphore, "timelineSemaphore");
        Require(vulkan12.DescriptorIndexing, "descriptorIndexing");
        Require(vulkan12.RuntimeDescriptorArray, "runtimeDescriptorArray");
        Require(vulkan12.DescriptorBindingPartiallyBound, "descriptorBindingPartiallyBound");
        Require(vulkan12.DescriptorBindingSampledImageUpdateAfterBind, "descriptorBindingSampledImageUpdateAfterBind");
        Require(vulkan12.DescriptorBindingStorageImageUpdateAfterBind, "descriptorBindingStorageImageUpdateAfterBind");
        Require(vulkan12.ShaderSampledImageArrayNonUniformIndexing, "shaderSampledImageArrayNonUniformIndexing");
        Require(vulkan12.ScalarBlockLayout, "scalarBlockLayout");
        Require(vulkan12.HostQueryReset, "hostQueryReset");
        return missing;
    }

    /// <summary>Like <see cref="CreateFor"/>, for a machine that may have no Vulkan loader or driver: says why instead of throwing.</summary>
    internal static bool TryCreateFor(GraphicsDeviceOptions options, out GraphicsDevice? device, out string? error)
    {
        try
        {
            device = CreateFor(options);
            error = null;
            return true;
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException)
        {
            device = null;
            error = e.Message;
            return false;
        }
    }

    /// <summary>Creates the instance, the window's surface and the device on the best usable GPU. Throws with every GPU's reason when none qualifies.</summary>
    internal static VulkanDevice CreateFor(GraphicsDeviceOptions options)
    {
        VulkanInstance instance = VulkanInstance.Create(InstanceOptions(options));
        try
        {
            return Create(instance, ownsInstance: true, options);
        }
        catch
        {
            instance.Dispose();
            throw;
        }
    }

    /// <summary>Creates the device on an instance the caller keeps (the caller disposes the instance after the device). A surface is made for the window, if there is one.</summary>
    public static VulkanDevice Create(VulkanInstance instance, GraphicsDeviceOptions? options = null) => Create(instance, ownsInstance: false, options ?? new GraphicsDeviceOptions());

    private static VulkanDevice Create(VulkanInstance instance, bool ownsInstance, GraphicsDeviceOptions options)
    {
        string? preferred = options.PreferredDeviceName ?? Environment.GetEnvironmentVariable("APOGEE_GPU");

        VkSurfaceKHR surface = CreateSurface(instance, options.Window);
        PhysicalDeviceInfo[] devices = EnumeratePhysicalDevices(instance, surface);
        foreach (PhysicalDeviceInfo device in devices)
        {
            Log.Info(LogCategory, $"GPU: {device}");
        }

        PhysicalDeviceInfo? chosen = devices
            .Where(d => d.Suitable)
            .OrderByDescending(d => preferred is not null && d.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d.Type switch
            {
                VkPhysicalDeviceType.DiscreteGpu => 0,
                VkPhysicalDeviceType.IntegratedGpu => 1,
                VkPhysicalDeviceType.VirtualGpu => 2,
                VkPhysicalDeviceType.Cpu => 3,
                _ => 4,
            })
            .FirstOrDefault();
        if (chosen is null)
        {
            DestroySurface(instance, surface);
            throw new VulkanException(devices.Length == 0
                ? "No Vulkan GPU found."
                : "No GPU meets the engine's baseline (Vulkan 1.3, dynamic rendering, synchronization2, buffer device address, descriptor indexing): "
                    + string.Join(" | ", devices.Select(d => d.ToString())));
        }

        Log.Info(LogCategory, $"Using {chosen.Name}");
        try
        {
            return CreateOn(instance, ownsInstance, chosen, surface, options.Window);
        }
        catch
        {
            DestroySurface(instance, surface);
            throw;
        }
    }

    private static VulkanDevice CreateOn(VulkanInstance instance, bool ownsInstance, PhysicalDeviceInfo physical, VkSurfaceKHR surface, Window? window)
    {
        VkInstanceCommands commands = instance.Commands;

        // Everything optional that the renderer can use, switched on when the GPU has it.
        var supported = new VkPhysicalDeviceFeatures2();
        commands.GetPhysicalDeviceFeatures2(physical.Handle, &supported);

        // shaderDrawParameters: Slang's SV_VertexID is zero-based, which SPIR-V expresses with the DrawParameters capability.
        var vulkan11 = new VkPhysicalDeviceVulkan11Features { ShaderDrawParameters = 1 };
        var vulkan12 = new VkPhysicalDeviceVulkan12Features
        {
            PNext = &vulkan11,
            BufferDeviceAddress = 1,
            TimelineSemaphore = 1,
            DescriptorIndexing = 1,
            RuntimeDescriptorArray = 1,
            DescriptorBindingPartiallyBound = 1,
            DescriptorBindingSampledImageUpdateAfterBind = 1,
            DescriptorBindingStorageImageUpdateAfterBind = 1,
            DescriptorBindingUpdateUnusedWhilePending = 1,
            ShaderSampledImageArrayNonUniformIndexing = 1,
            ScalarBlockLayout = 1,
            HostQueryReset = 1,
        };
        var vulkan13 = new VkPhysicalDeviceVulkan13Features
        {
            PNext = &vulkan12,
            ShaderDemoteToHelperInvocation = 1,      // Slang's discard
            DynamicRendering = 1,
            Synchronization2 = 1,
            Maintenance4 = 1,
        };
        var features = new VkPhysicalDeviceFeatures2
        {
            PNext = &vulkan13,
            Features =
            {
                SamplerAnisotropy = supported.Features.SamplerAnisotropy,
                TextureCompressionBC = supported.Features.TextureCompressionBC,
                TextureCompressionASTC_LDR = supported.Features.TextureCompressionASTC_LDR,
                DepthClamp = supported.Features.DepthClamp,
                FillModeNonSolid = supported.Features.FillModeNonSolid,
                MultiDrawIndirect = supported.Features.MultiDrawIndirect,
                ShaderInt64 = supported.Features.ShaderInt64,
                ShaderStorageImageReadWithoutFormat = supported.Features.ShaderStorageImageReadWithoutFormat,
                ShaderStorageImageWriteWithoutFormat = supported.Features.ShaderStorageImageWriteWithoutFormat,
            },
        };

        var extensions = new List<string>();
        if (surface.Value != 0)
        {
            extensions.Add("VK_KHR_swapchain");
        }

        // A layered implementation (MoltenVK) lists this extension and requires it to be enabled.
        if (DeviceExtensions(instance, physical.Handle).Contains("VK_KHR_portability_subset"))
        {
            extensions.Add("VK_KHR_portability_subset");
        }

        float priority = 1f;
        var queue = new VkDeviceQueueCreateInfo { QueueFamilyIndex = physical.QueueFamily, QueueCount = 1, PQueuePriorities = &priority };
        using var extensionNames = new Utf8Array(extensions);
        var create = new VkDeviceCreateInfo
        {
            PNext = &features,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queue,
            EnabledExtensionCount = (uint)extensionNames.Count,
            PpEnabledExtensionNames = extensionNames.Items,
        };

        VkDevice device;
        VulkanException.Check(commands.CreateDevice(physical.Handle, &create, null, &device), "vkCreateDevice");
        var deviceCommands = new VkDeviceCommands(commands, device);
        VkQueue vkQueue;
        deviceCommands.GetDeviceQueue(device, physical.QueueFamily, 0, &vkQueue);

        var result = new VulkanDevice(instance, ownsInstance, physical, device, deviceCommands, vkQueue, surface, window);
        try
        {
            result.Initialize(supported.Features, features.Features);
        }
        catch
        {
            result.Dispose();
            throw;
        }

        return result;
    }

    private static HashSet<string> DeviceExtensions(VulkanInstance instance, VkPhysicalDevice handle)
    {
        uint count;
        VulkanException.Check(instance.Commands.EnumerateDeviceExtensionProperties(handle, null, &count, null), "vkEnumerateDeviceExtensionProperties");
        var properties = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* data = properties)
        {
            VulkanException.Check(instance.Commands.EnumerateDeviceExtensionProperties(handle, null, &count, data), "vkEnumerateDeviceExtensionProperties");
            var names = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                names.Add(Utf8.FromPointer(data[i].ExtensionName)!);
            }

            return names;
        }
    }

    private void Initialize(VkPhysicalDeviceFeatures supported, VkPhysicalDeviceFeatures enabled)
    {
        VkPhysicalDeviceProperties properties;
        Instance.Commands.GetPhysicalDeviceProperties(Physical.Handle, &properties);
        _timestampPeriod = properties.Limits.TimestampPeriod;
        _supportsTimestamps = properties.Limits.TimestampComputeAndGraphics != 0;
        MaxPushConstantsSize = properties.Limits.MaxPushConstantsSize;
        _minUniformBufferOffsetAlignment = properties.Limits.MinUniformBufferOffsetAlignment;
        _maxSamplerAnisotropy = properties.Limits.MaxSamplerAnisotropy;
        _supportsAnisotropy = enabled.SamplerAnisotropy != 0;
        _supportsBc = enabled.TextureCompressionBC != 0;
        _supportsAstc = enabled.TextureCompressionASTC_LDR != 0;

        CreateAllocator();
        Immediate = new VulkanImmediateContext(this);
        var bindless = new VulkanBindlessHeap(this);
        Bindless = bindless;
        bindless.CreateDefaults();
    }

    private void CreateAllocator()
    {
        // VMA loads the rest of Vulkan through these two entry points (it is built without static functions).
        if (!NativeLibraries.TryLoad(typeof(Vk).Assembly, "vulkan", out IntPtr loader))
        {
            throw new VulkanException("The Vulkan loader disappeared.");
        }

        var functions = new VulkanFunctions
        {
            VkGetInstanceProcAddr = (delegate* unmanaged[Cdecl]<VkInstance, byte*, delegate* unmanaged[Cdecl]<void>>)NativeLibrary.GetExport(loader, "vkGetInstanceProcAddr"),
            VkGetDeviceProcAddr = Instance.Commands.GetDeviceProcAddr,
        };
        var info = new AllocatorCreateInfo
        {
            Flags = AllocatorCreateFlagBits.BufferDeviceAddress,
            PhysicalDevice = Physical.Handle,
            Device = Handle,
            Instance = Instance.Handle,
            VulkanApiVersion = Vk.ApiVersion13,
            PVulkanFunctions = &functions,
        };

        VmaAllocator* allocator;
        VulkanException.Check(VmaApi.CreateAllocator(&info, &allocator), "vmaCreateAllocator");
        _allocator = allocator;
    }

    public override void WaitIdle() => VulkanException.Check(Commands.DeviceWaitIdle(Handle), "vkDeviceWaitIdle");

    /// <summary>Names an object for the validation layer and capture tools (RenderDoc, Nsight). Does nothing without <c>VK_EXT_debug_utils</c>.</summary>
    public void SetDebugName(VkObjectType type, ulong handle, string name)
    {
        if (!Instance.HasDebugUtils)
        {
            return;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(name))
        {
            var info = new VkDebugUtilsObjectNameInfoEXT { ObjectType = type, ObjectHandle = handle, PObjectName = text };
            Commands.SetDebugUtilsObjectNameEXT(Handle, &info);
        }
    }

    public override void Dispose()
    {
        if (Handle.Value != 0 && Immediate is not null)
        {
            WaitIdle();
        }

        Bindless?.Dispose();
        Immediate?.Dispose();
        if (_allocator is not null)
        {
            VmaApi.DestroyAllocator(_allocator);
            _allocator = null;
        }

        if (Handle.Value != 0)
        {
            Commands.DestroyDevice(Handle, null);
        }

        DestroySurface(Instance, _surface);
        _surface = default;
        if (_ownsInstance)
        {
            Instance.Dispose();
        }
    }
}
