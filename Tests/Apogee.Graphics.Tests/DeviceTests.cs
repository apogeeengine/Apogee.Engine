using System.Linq;
using Apogee.Graphics.Vulkan;
using Apogee.Native.Vulkan;
using Xunit;

namespace Apogee.Graphics.Tests;

public unsafe class DeviceTests(GpuFixture gpu) : GpuTest(gpu)
{
    private VulkanDevice Vulkan
    {
        get
        {
            RequireVulkan();
            return (VulkanDevice)Device;
        }
    }

    [Fact]
    public void TheInstanceIsVersion13OrNewer()
    {
        Assert.True(VulkanInstance.LoaderVersion >= Vk.ApiVersion13);
        Assert.True(Vulkan.Instance.HasDebugUtils);
    }

    [Fact]
    public void EveryGpuIsDescribedAndTheChosenOneIsUsable()
    {
        PhysicalDeviceInfo[] devices = VulkanDevice.EnumeratePhysicalDevices(Vulkan.Instance);

        Assert.NotEmpty(devices);
        Assert.All(devices, d => Assert.False(string.IsNullOrEmpty(d.Name)));
        PhysicalDeviceInfo chosen = Assert.Single(devices, d => d.Handle.Value == Vulkan.Physical.Handle.Value);
        Assert.True(chosen.Suitable, string.Join("; ", chosen.Reasons));
        Assert.True(chosen.ApiVersion >= Vk.ApiVersion13);
    }

    [Fact]
    public void ThePortableApiNamesTheDeviceAndTheGpusThatMeetTheBaseline()
    {
        GpuInfo[] gpus = GraphicsDevice.EnumerateDevices();

        Assert.Equal(GraphicsBackend.Vulkan, Device.Backend);
        Assert.Equal(Apogee.Graphics.Shaders.ShaderBackend.Spirv, Device.ShaderBackend);
        Assert.Contains(gpus, g => g.Name == Device.Name && g.Suitable);
    }

    [Fact]
    public void TheDeviceReportsTheLimitsTheRendererRelaysOn()
    {
        Assert.True(Vulkan.MaxPushConstantsSize >= 128);
        Assert.True(Device.MinUniformBufferOffsetAlignment > 0);
        Assert.True(Vulkan.Allocator != null);
        Assert.True(Vulkan.Queue.Value != 0);
        if (Device.SupportsTimestamps)
        {
            Assert.True(Device.TimestampPeriod > 0);
        }
    }

    [Fact]
    public void TheGpuEnvironmentVariableChoosesTheDeviceByName()
    {
        string? wanted = System.Environment.GetEnvironmentVariable("APOGEE_GPU");
        if (wanted is null)
        {
            Assert.Skip("APOGEE_GPU is not set.");
        }

        PhysicalDeviceInfo[] usable = [.. VulkanDevice.EnumeratePhysicalDevices(Vulkan.Instance).Where(d => d.Suitable)];
        if (!usable.Any(d => d.Name.Contains(wanted, System.StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Skip($"No usable device matches APOGEE_GPU={wanted}.");
        }

        Assert.Contains(wanted, Device.Name, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ObjectsCanBeNamedWithoutValidationErrors()
    {
        // The queue is a real object: naming it must be accepted by the validation layer.
        Vulkan.SetDebugName(VkObjectType.Queue, (ulong)Vulkan.Queue.Value, "Apogee queue");
    }

    [Fact]
    public void AGpuThatLacksTheBaselineIsReportedWithItsReason()
    {
        // The software and hardware devices both meet it, so an unusable one is built by hand to pin the message format.
        var info = new PhysicalDeviceInfo(default, "Old GPU", VkPhysicalDeviceType.IntegratedGpu, Vk.ApiVersion12, 0, ["Vulkan 1.2 (1.3 needed)", "dynamicRendering"]);

        Assert.False(info.Suitable);
        Assert.Contains("Old GPU", info.ToString());
        Assert.Contains("dynamicRendering", info.ToString());
        Assert.Equal(2, info.Reasons.Length);
        Assert.Contains(info.Reasons, r => r.Contains("1.3", System.StringComparison.Ordinal));
    }
}
