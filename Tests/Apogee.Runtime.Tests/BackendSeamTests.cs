using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Apogee.Content;
using Apogee.DevTools;
using Apogee.Graphics;
using Apogee.Platform;
using Apogee.Rendering;
using Xunit;

namespace Apogee.Runtime.Tests;

/// <summary>
/// The graphics seam: what the renderer, the dev tools, the content pipeline and the platform layer see of the GPU is the portable API of <c>Apogee.Graphics</c>, never a Vulkan type. A second
/// backend can only plug in if nothing outside <c>Apogee.Graphics.Vulkan</c> names Vulkan; this reads the public surface of those assemblies and fails on any signature that does.
/// </summary>
public class BackendSeamTests
{
    private const string VulkanBinding = "Apogee.Native.Vulkan";
    private const string VulkanBackend = "Apogee.Graphics.Vulkan";

    private static readonly Assembly[] s_assemblies =
    [
        typeof(GraphicsDevice).Assembly,
        typeof(SceneRenderer).Assembly,
        typeof(DevOverlay).Assembly,
        typeof(ContentException).Assembly,
        typeof(Window).Assembly,
    ];

    private static bool IsVulkan(Type? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type.HasElementType)
        {
            return IsVulkan(type.GetElementType());
        }

        if (type.Namespace is { } ns && (ns == VulkanBinding || ns.StartsWith(VulkanBinding + ".", StringComparison.Ordinal)))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(IsVulkan);
    }

    private static bool InBackend(Type type) => type.Namespace is { } ns && (ns == VulkanBackend || ns.StartsWith(VulkanBackend + ".", StringComparison.Ordinal));

    private static bool Visible(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static IEnumerable<string> Leaks(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        if (IsVulkan(type.BaseType) || type.GetInterfaces().Any(IsVulkan))
        {
            yield return $"{type} derives from a Vulkan type";
        }

        foreach (FieldInfo field in type.GetFields(all).Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly))
        {
            if (IsVulkan(field.FieldType))
            {
                yield return $"{type}.{field.Name} is a {field.FieldType}";
            }
        }

        foreach (PropertyInfo property in type.GetProperties(all))
        {
            MethodInfo? accessor = property.GetMethod ?? property.SetMethod;
            if (accessor is not null && Visible(accessor) && (IsVulkan(property.PropertyType) || property.GetIndexParameters().Any(p => IsVulkan(p.ParameterType))))
            {
                yield return $"{type}.{property.Name} is a {property.PropertyType}";
            }
        }

        foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)).Where(Visible))
        {
            if (method is MethodInfo { IsSpecialName: true })
            {
                continue; // property accessors are covered above
            }

            foreach (ParameterInfo parameter in method.GetParameters().Where(p => IsVulkan(p.ParameterType)))
            {
                yield return $"{type}.{method.Name}({parameter.Name}: {parameter.ParameterType})";
            }

            if (method is MethodInfo { } info && IsVulkan(info.ReturnType))
            {
                yield return $"{type}.{method.Name} returns {info.ReturnType}";
            }
        }
    }

    [Fact]
    public void NothingOutsideTheVulkanBackendNamesAVulkanTypeInItsPublicSurface()
    {
        string[] leaks =
        [
            .. s_assemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => (t.IsPublic || t.IsNestedPublic || t.IsNestedFamily) && !InBackend(t) && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
                .SelectMany(Leaks)
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(leaks.Length == 0, "Vulkan types in the portable API:\n" + string.Join("\n", leaks));
    }

    [Fact]
    public void TheDetectorSeesVulkanTypesWhereThereAreSome()
    {
        // The Vulkan classes themselves are full of them (handles, commands, formats): if this finds none, the test above would never find a leak either.
        Assert.True(Leaks(typeof(Apogee.Graphics.Vulkan.VulkanDevice)).Count() >= 4);
        Assert.NotEmpty(Leaks(typeof(Apogee.Graphics.Vulkan.VulkanCommandRecorder)));
        Assert.True(IsVulkan(typeof(Apogee.Native.Vulkan.VkImage[])));
    }

    [Fact]
    public void TheVulkanBackendIsWhereTheDeviceAndItsObjectsComeFrom()
    {
        // The portable classes are abstract; the sealed Vulkan ones are in the backend's namespace.
        Type[] portable = [typeof(GraphicsDevice), typeof(GpuBuffer), typeof(GpuImage), typeof(ImageView), typeof(GpuSampler), typeof(Pipeline), typeof(CommandRecorder), typeof(BindlessHeap), typeof(ImmediateContext), typeof(Swapchain), typeof(FrameLoop)];
        Assert.All(portable, t => Assert.True(t.IsAbstract, $"{t.Name} is the seam: it must be abstract"));

        var backend = typeof(GraphicsDevice).Assembly.GetTypes().Where(InBackend).Where(t => t.IsPublic).ToList();
        foreach (Type type in portable)
        {
            Assert.Contains(backend, t => type.IsAssignableFrom(t) && t.IsSealed);
        }
    }
}
