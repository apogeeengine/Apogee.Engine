using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.Vulkan;

namespace Apogee.Graphics.Vulkan;

public sealed class VulkanInstanceOptions
{
    public string ApplicationName { get; set; } = "Apogee";

    /// <summary>Instance extensions the window system needs (<c>Window.GetRequiredVulkanInstanceExtensions()</c>); empty for headless use.</summary>
    public IReadOnlyList<string> WindowSystemExtensions { get; set; } = [];

    /// <summary>
    /// Turns on the Khronos validation layer when it is installed. On by default; <c>APOGEE_VULKAN_VALIDATION=0</c> turns it off, and
    /// <c>=1</c> makes a missing layer an error.
    /// </summary>
    public bool Validation { get; set; } = true;
}

/// <summary>The loader's instance plus the debug messenger that forwards validation and driver messages to the log and counts errors.</summary>
public sealed unsafe class VulkanInstance : IDisposable
{
    private const string LogCategory = "Vulkan";
    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    private readonly VkGlobalCommands _global;
    private readonly object _messagesLock = new();
    private readonly List<string> _errors = [];
    private GCHandle _self;
    private VkDebugUtilsMessengerEXT _messenger;
    private int _errorCount;
    private int _warningCount;

    private VulkanInstance(VkGlobalCommands global, VkInstance handle, VkInstanceCommands commands, bool validationEnabled, bool debugUtils, bool portability)
    {
        _global = global;
        Handle = handle;
        Commands = commands;
        ValidationEnabled = validationEnabled;
        HasDebugUtils = debugUtils;
        PortabilityEnumeration = portability;
    }

    public VkInstance Handle { get; }

    public VkInstanceCommands Commands { get; }

    public bool ValidationEnabled { get; }

    public bool HasDebugUtils { get; }

    /// <summary>The loader enumerates portability (layered) implementations such as MoltenVK; devices then need <c>VK_KHR_portability_subset</c>.</summary>
    public bool PortabilityEnumeration { get; }

    /// <summary>Errors the validation layer or the driver reported since the instance was created (or since <see cref="TakeErrors"/>).</summary>
    public int ErrorCount => Volatile.Read(ref _errorCount);

    public int WarningCount => Volatile.Read(ref _warningCount);

    /// <summary>The instance version the loader supports.</summary>
    public static uint LoaderVersion
    {
        get
        {
            var global = new VkGlobalCommands();
            uint version;
            return global.EnumerateInstanceVersion(&version) == VkResult.Success ? version : Vk.ApiVersion10;
        }
    }

    /// <summary>Creates an instance, or returns false with the reason (no Vulkan loader, no driver, a missing layer that was demanded).</summary>
    public static bool TryCreate(VulkanInstanceOptions options, out VulkanInstance? instance, out string? error)
    {
        try
        {
            instance = Create(options);
            error = null;
            return true;
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException)
        {
            instance = null;
            error = e.Message;
            return false;
        }
    }

    public static VulkanInstance Create(VulkanInstanceOptions options)
    {
        VkGlobalCommands global = Build(options, out VkInstance handle, out VkInstanceCommands commands, out bool validation, out bool debugUtils, out bool portability);
        var instance = new VulkanInstance(global, handle, commands, validation, debugUtils, portability);
        if (debugUtils)
        {
            try
            {
                instance.CreateMessenger();
            }
            catch
            {
                instance.Dispose();
                throw;
            }
        }

        return instance;
    }

    private static VkGlobalCommands Build(VulkanInstanceOptions options, out VkInstance handle, out VkInstanceCommands commands, out bool validation, out bool debugUtils, out bool portability)
    {
        VkGlobalCommands global;
        try
        {
            global = new VkGlobalCommands();
        }
        catch (DllNotFoundException e)
        {
            throw new VulkanException($"No Vulkan loader is installed ({e.Message})");
        }

        if (global.EnumerateInstanceVersion is null || LoaderVersion < Vk.ApiVersion13)
        {
            throw new VulkanException("The Vulkan loader is older than 1.3.");
        }

        var extensions = new List<string>(options.WindowSystemExtensions);
        HashSet<string> availableExtensions = EnumerateExtensions(global);
        debugUtils = availableExtensions.Contains("VK_EXT_debug_utils");
        if (debugUtils)
        {
            extensions.Add("VK_EXT_debug_utils");
        }

        // MoltenVK (and any other layered implementation) is only listed when the application opts in.
        portability = availableExtensions.Contains("VK_KHR_portability_enumeration");
        if (portability)
        {
            extensions.Add("VK_KHR_portability_enumeration");
            if (availableExtensions.Contains("VK_KHR_get_physical_device_properties2"))
            {
                extensions.Add("VK_KHR_get_physical_device_properties2");
            }
        }

        foreach (string required in options.WindowSystemExtensions)
        {
            if (!availableExtensions.Contains(required))
            {
                throw new VulkanException($"The Vulkan loader lacks the instance extension {required}, which presenting to a window needs.");
            }
        }

        var layers = new List<string>();
        string? setting = Environment.GetEnvironmentVariable("APOGEE_VULKAN_VALIDATION");
        bool wanted = options.Validation && setting != "0";
        validation = false;
        if (wanted)
        {
            if (EnumerateLayers(global).Contains(ValidationLayer))
            {
                layers.Add(ValidationLayer);
                validation = true;
            }
            else if (setting == "1")
            {
                throw new VulkanException($"APOGEE_VULKAN_VALIDATION=1 but the layer {ValidationLayer} is not installed.");
            }
        }

        using var extensionNames = new Utf8Array(extensions);
        using var layerNames = new Utf8Array(layers);
        fixed (byte* applicationName = Utf8.ToZeroTerminated(options.ApplicationName))
        fixed (byte* engineName = "Apogee\0"u8)
        {
            var application = new VkApplicationInfo
            {
                PApplicationName = applicationName,
                PEngineName = engineName,
                ApiVersion = Vk.ApiVersion13,
            };
            var info = new VkInstanceCreateInfo
            {
                Flags = portability ? VkInstanceCreateFlagBits.EnumeratePortabilityBitKhr : 0,
                PApplicationInfo = &application,
                EnabledExtensionCount = (uint)extensionNames.Count,
                PpEnabledExtensionNames = extensionNames.Items,
                EnabledLayerCount = (uint)layerNames.Count,
                PpEnabledLayerNames = layerNames.Items,
            };

            VkInstance created;
            VulkanException.Check(global.CreateInstance(&info, null, &created), "vkCreateInstance");
            handle = created;
        }

        commands = new VkInstanceCommands(handle);
        return global;
    }

    private static HashSet<string> EnumerateExtensions(VkGlobalCommands global)
    {
        uint count;
        VulkanException.Check(global.EnumerateInstanceExtensionProperties(null, &count, null), "vkEnumerateInstanceExtensionProperties");
        var properties = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* data = properties)
        {
            VulkanException.Check(global.EnumerateInstanceExtensionProperties(null, &count, data), "vkEnumerateInstanceExtensionProperties");
            var names = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                names.Add(Utf8.FromPointer(data[i].ExtensionName)!);
            }

            return names;
        }
    }

    private static HashSet<string> EnumerateLayers(VkGlobalCommands global)
    {
        uint count;
        VulkanException.Check(global.EnumerateInstanceLayerProperties(&count, null), "vkEnumerateInstanceLayerProperties");
        var properties = new VkLayerProperties[count];
        fixed (VkLayerProperties* data = properties)
        {
            VulkanException.Check(global.EnumerateInstanceLayerProperties(&count, data), "vkEnumerateInstanceLayerProperties");
            var names = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                names.Add(Utf8.FromPointer(data[i].LayerName)!);
            }

            return names;
        }
    }

    private void CreateMessenger()
    {
        _self = GCHandle.Alloc(this);
        var info = new VkDebugUtilsMessengerCreateInfoEXT
        {
            MessageSeverity = VkDebugUtilsMessageSeverityFlagBitsEXT.Warning | VkDebugUtilsMessageSeverityFlagBitsEXT.Error,
            MessageType = VkDebugUtilsMessageTypeFlagBitsEXT.General | VkDebugUtilsMessageTypeFlagBitsEXT.Validation | VkDebugUtilsMessageTypeFlagBitsEXT.Performance,
            PfnUserCallback = &DebugCallback,
            PUserData = (void*)GCHandle.ToIntPtr(_self),
        };

        VkDebugUtilsMessengerEXT messenger;
        VulkanException.Check(Commands.CreateDebugUtilsMessengerEXT(Handle, &info, null, &messenger), "vkCreateDebugUtilsMessengerEXT");
        _messenger = messenger;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint DebugCallback(VkDebugUtilsMessageSeverityFlagBitsEXT severity, VkDebugUtilsMessageTypeFlagBitsEXT type, VkDebugUtilsMessengerCallbackDataEXT* data, void* userData)
    {
        // Nothing may throw back into the driver.
        try
        {
            var instance = (VulkanInstance?)GCHandle.FromIntPtr((IntPtr)userData).Target;
            instance?.OnMessage(severity, type, Utf8.FromPointer(data->PMessage) ?? "");
        }
        catch (Exception)
        {
        }

        return 0;
    }

    private void OnMessage(VkDebugUtilsMessageSeverityFlagBitsEXT severity, VkDebugUtilsMessageTypeFlagBitsEXT type, string message)
    {
        if (severity.HasFlag(VkDebugUtilsMessageSeverityFlagBitsEXT.Error))
        {
            Interlocked.Increment(ref _errorCount);
            lock (_messagesLock)
            {
                if (_errors.Count < 100)
                {
                    _errors.Add(message);
                }
            }

            Log.Error(LogCategory, $"[{type}] {message}");
        }
        else
        {
            Interlocked.Increment(ref _warningCount);
            Log.Warn(LogCategory, $"[{type}] {message}");
        }
    }

    /// <summary>Returns the error messages collected so far (at most 100) and clears the count.</summary>
    public string[] TakeErrors()
    {
        lock (_messagesLock)
        {
            string[] errors = [.. _errors];
            _errors.Clear();
            Interlocked.Exchange(ref _errorCount, 0);
            return errors;
        }
    }

    public void Dispose()
    {
        if (_messenger.Value != 0)
        {
            Commands.DestroyDebugUtilsMessengerEXT(Handle, _messenger, null);
            _messenger = default;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }

        Commands.DestroyInstance(Handle, null);
        _ = _global;
    }
}
