using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Apogee.Interop;
using Xunit;
using Apogee.Native.Vma;
using Apogee.Native.Vulkan;
using Ecs = Apogee.Native.Flecs.Ecs;
using ImGui = Apogee.Native.CImGui.ImGui;
using SDL = Apogee.Native.Sdl.SDL;
using Vk = Apogee.Native.Vulkan.Vk;
using MeshoptApi = Apogee.Native.Meshopt.Meshopt;
using VmaApi = Apogee.Native.Vma.Vma;
using World = Apogee.Native.Flecs.World;
using Apogee.Native.Flecs;

namespace Apogee.Native.Tests;

/// <summary>Calls into each library through the generated bindings, so that a wrong signature fails here and not in a game.</summary>
public unsafe class NativeSmokeTests
{
    private static void RequireLibrary(Assembly assembly, string name) => NativeRequirements.Require(assembly, name);

    [Fact]
    public void SdlReportsItsVersion()
    {
        RequireLibrary(typeof(SDL).Assembly, "SDL3");

        // SDL_VERSIONNUM(3, 0, 0)
        Assert.True(SDL.GetVersion() >= 3_000_000);
    }

    [Fact]
    public void FlecsCreatesAndDeletesAnEntity()
    {
        RequireLibrary(typeof(Ecs).Assembly, "flecs");

        World* world = Ecs.Init();
        try
        {
            ulong entity = Ecs.New(world);
            Assert.True(Ecs.IsAlive(world, entity));

            Ecs.Delete(world, entity);
            Assert.False(Ecs.IsAlive(world, entity));
        }
        finally
        {
            Assert.Equal(0, Ecs.Fini(world));
        }
    }

    [Fact]
    public void FlecsBuiltinIdsAreReadFromTheLibrary()
    {
        RequireLibrary(typeof(Ecs).Assembly, "flecs");

        World* world = Ecs.Init();
        try
        {
            // Exported variables (Ecs.Globals.g.cs), filled in by ecs_init: tags, the component of components, a primitive's type.
            Assert.True(Ecs.IsAlive(world, Ecs.OnUpdate));
            Assert.True(Ecs.IsAlive(world, Ecs.ChildOf));
            Assert.True(Ecs.IsAlive(world, Ecs.ComponentId));
            Assert.True(Ecs.IsAlive(world, Ecs.F32Id));
            Assert.NotEqual(Ecs.OnUpdate, Ecs.OnStore);
        }
        finally
        {
            Assert.Equal(0, Ecs.Fini(world));
        }
    }

    [Fact]
    public void FlecsRefsHaveTheLayoutOfTheCompiledLibrary()
    {
        RequireLibrary(typeof(Ecs).Assembly, "flecs");

        // The bindings were once generated from Flecs' debug headers (ecs_ref_t 40 bytes), the library is a release build (32).
        // ecs_ref_init_id returns the struct by value, so a wrong size garbles the result instead of failing loudly.
        Assert.Equal(32, sizeof(Ref));

        World* world = Ecs.Init();
        try
        {
            ulong component;
            fixed (byte* name = "Pair\0"u8)
            {
                var entityDesc = new EntityDesc { Name = name };
                var componentDesc = new ComponentDesc { Entity = Ecs.EntityInit(world, &entityDesc), Type = new Apogee.Native.Flecs.TypeInfo { Size = 8, Alignment = 4 } };
                component = Ecs.ComponentInit(world, &componentDesc);
            }

            ulong entity = Ecs.New(world);
            int* value = stackalloc int[2] { 7, 11 };
            Ecs.SetId(world, entity, component, 8, value);

            Ref reference = Ecs.RefInitId(world, entity, component);
            Assert.Equal(entity, reference.Entity);
            int* read = (int*)Ecs.RefGetId(world, &reference, component);
            Assert.True(read != null);
            Assert.Equal(7, read[0]);
            Assert.Equal(11, read[1]);
        }
        finally
        {
            Assert.Equal(0, Ecs.Fini(world));
        }
    }

    [Fact]
    public void VmaPlacesAndFreesVirtualAllocations()
    {
        RequireLibrary(typeof(VmaApi).Assembly, "vma");

        // A virtual block is VMA's allocator without a GPU: it hands out offsets in a range, which proves the library and the structs.
        var blockInfo = new VirtualBlockCreateInfo { Size = 1024 };
        VirtualBlock* block;
        Assert.Equal(VkResult.Success, VmaApi.CreateVirtualBlock(&blockInfo, &block));
        try
        {
            var allocationInfo = new VirtualAllocationCreateInfo { Size = 256, Alignment = 256 };
            VirtualAllocation* first;
            VirtualAllocation* second;
            ulong firstOffset;
            ulong secondOffset;
            Assert.Equal(VkResult.Success, VmaApi.VirtualAllocate(block, &allocationInfo, &first, &firstOffset));
            Assert.Equal(VkResult.Success, VmaApi.VirtualAllocate(block, &allocationInfo, &second, &secondOffset));
            Assert.Equal(0UL, firstOffset);
            Assert.Equal(256UL, secondOffset);

            Statistics statistics;
            VmaApi.GetVirtualBlockStatistics(block, &statistics);
            Assert.Equal(2u, statistics.AllocationCount);
            Assert.Equal(512UL, statistics.AllocationBytes);
            Assert.Equal(0u, VmaApi.IsVirtualBlockEmpty(block));

            VmaApi.VirtualFree(block, first);
            VmaApi.VirtualFree(block, second);
            Assert.NotEqual(0u, VmaApi.IsVirtualBlockEmpty(block));
        }
        finally
        {
            VmaApi.DestroyVirtualBlock(block);
        }
    }

    [Fact]
    public void MeshoptDeduplicatesVerticesAndKeepsTheTriangles()
    {
        RequireLibrary(typeof(MeshoptApi).Assembly, "meshoptimizer");

        // A quad as two triangles with the shared corners duplicated: 6 vertices, 4 unique.
        float[] vertices = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1, 0, 0, 1, 0];
        uint[] indices = [0, 1, 2, 3, 4, 5];
        uint[] remap = new uint[6];
        uint[] unique = new uint[6];
        uint[] optimized = new uint[6];

        fixed (float* vertexData = vertices)
        fixed (uint* indexData = indices)
        fixed (uint* remapData = remap)
        fixed (uint* uniqueData = unique)
        fixed (uint* optimizedData = optimized)
        {
            nuint count = MeshoptApi.GenerateVertexRemap(remapData, indexData, 6, vertexData, 6, 3 * sizeof(float));
            Assert.Equal(4u, (uint)count);

            MeshoptApi.RemapIndexBuffer(uniqueData, indexData, 6, remapData);
            Assert.Equal([0u, 1u, 2u, 0u, 2u, 3u], unique);

            MeshoptApi.OptimizeVertexCache(optimizedData, uniqueData, 6, count);
        }

        // Optimizing reorders triangles but never changes them.
        static string[] Triangles(uint[] list) => [.. Enumerable.Range(0, list.Length / 3).Select(i => string.Join(',', list[(i * 3)..(i * 3 + 3)].Order())).Order()];
        Assert.Equal(Triangles(unique), Triangles(optimized));
    }

    [Fact]
    public void ImGuiCreatesAContext()
    {
        RequireLibrary(typeof(ImGui).Assembly, "cimgui");

        Assert.Equal("1.93.0", Marshal.PtrToStringUTF8((IntPtr)ImGui.GetVersion()));

        var context = ImGui.CreateContext(null);
        try
        {
            Assert.True(context != null);
            Assert.True(ImGui.GetCurrentContext() == context);
        }
        finally
        {
            ImGui.DestroyContext(context);
        }
    }

    [Fact]
    public void VulkanCreatesAnInstanceThroughTheLoader()
    {
        RequireLibrary(typeof(Vk).Assembly, "vulkan");

        var global = new VkGlobalCommands();
        uint version;
        Assert.Equal(VkResult.Success, global.EnumerateInstanceVersion(&version));
        Assert.True(version >= Vk.ApiVersion11);

        var application = new VkApplicationInfo { SType = VkStructureType.ApplicationInfo, ApiVersion = Vk.ApiVersion11 };
        var createInfo = new VkInstanceCreateInfo { SType = VkStructureType.InstanceCreateInfo, PApplicationInfo = &application };
        VkInstance instance;
        VkResult created = global.CreateInstance(&createInfo, null, &instance);
        if (created != VkResult.Success)
        {
            Assert.Skip($"This machine cannot create a Vulkan instance ({created}).");
        }

        var commands = new VkInstanceCommands(instance);
        try
        {
            uint count;
            Assert.Equal(VkResult.Success, commands.EnumeratePhysicalDevices(instance, &count, null));
        }
        finally
        {
            commands.DestroyInstance(instance, null);
        }
    }
}
