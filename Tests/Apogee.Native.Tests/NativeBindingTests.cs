using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Apogee.Interop;
using Xunit;
using Ecs = Apogee.Native.Flecs.Ecs;
using ImGui = Apogee.Native.CImGui.ImGui;
using Lua = Apogee.Native.Luau.Lua;
using SDL = Apogee.Native.Sdl.SDL;
using Vk = Apogee.Native.Vulkan.Vk;
using AudioDecodeApi = Apogee.Native.AudioDecode.AudioDecode;
using NetApi = Apogee.Native.Net.Net;
using OpenALApi = Apogee.Native.OpenAL.OpenAL;
using CgltfApi = Apogee.Native.Cgltf.Cgltf;
using FtApi = Apogee.Native.FreeType.FT;
using GizmoApi = Apogee.Native.ImGuizmo.Gizmo;
using JoltApi = Apogee.Native.Jolt.Jolt;
using NodesApi = Apogee.Native.ImNodes.Nodes;
using PlotApi = Apogee.Native.ImPlot.Plot;
using RmlApi = Apogee.Native.RmlUi.Rml;
using KtxApi = Apogee.Native.Ktx.Ktx;
using MeshoptApi = Apogee.Native.Meshopt.Meshopt;
using MtlApi = Apogee.Native.Metal.Mtl;
using SlangApi = Apogee.Native.Slang.Slang;
using Stbi = Apogee.Native.Stb.Stbi;
using SteamApi = Apogee.Native.Steam.Steam;
using VmaApi = Apogee.Native.Vma.Vma;
using ZstdApi = Apogee.Native.Zstd.Zstd;

namespace Apogee.Native.Tests;

/// <summary>Checks every generated binding assembly against the native library it was generated from.</summary>
public class NativeBindingTests
{
    public static TheoryData<string> Bindings => new(BindingAssemblies.Keys);

    private static readonly Dictionary<string, Assembly> BindingAssemblies = new()
    {
        ["Sdl"] = typeof(SDL).Assembly,
        ["Flecs"] = typeof(Ecs).Assembly,
        ["Luau"] = typeof(Lua).Assembly,
        ["CImGui"] = typeof(ImGui).Assembly,
        ["Vulkan"] = typeof(Vk).Assembly,
        ["Vma"] = typeof(VmaApi).Assembly,
        ["Meshopt"] = typeof(MeshoptApi).Assembly,
        ["Cgltf"] = typeof(CgltfApi).Assembly,
        ["Stb"] = typeof(Stbi).Assembly,
        ["AudioDecode"] = typeof(AudioDecodeApi).Assembly,
        ["OpenAL"] = typeof(OpenALApi).Assembly,
        ["Net"] = typeof(NetApi).Assembly,
        ["Ktx"] = typeof(KtxApi).Assembly,
        ["Jolt"] = typeof(JoltApi).Assembly,
        ["Steam"] = typeof(SteamApi).Assembly,
        ["Slang"] = typeof(SlangApi).Assembly,
        ["Zstd"] = typeof(ZstdApi).Assembly,
        ["FreeType"] = typeof(FtApi).Assembly,
        ["Metal"] = typeof(MtlApi).Assembly,
        ["RmlUi"] = typeof(RmlApi).Assembly,
        ["ImGuizmo"] = typeof(GizmoApi).Assembly,
        ["ImPlot"] = typeof(PlotApi).Assembly,
        ["ImNodes"] = typeof(NodesApi).Assembly,
    };

    /// <summary>The bindings whose C API has no struct of a known size: ImGuizmo takes plain floats, imnodes and OpenAL only hand out opaque contexts and devices.</summary>
    private static readonly string[] NoSizedStructs = ["ImGuizmo", "ImNodes", "OpenAL"];

    [Theory]
    [MemberData(nameof(Bindings))]
    public void EveryEntryPointResolves(string binding)
    {
        Assembly assembly = BindingAssemblies[binding];
        var imports = assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Import: m.GetCustomAttribute<DllImportAttribute>()))
            .Where(x => x.Import is not null)
            .ToList();
        Assert.NotEmpty(imports);

        string library = imports[0].Import!.Value;
        IntPtr handle = NativeRequirements.Require(assembly, library);

        List<string> missing = imports
            .Select(x => x.Import!.EntryPoint ?? x.Method.Name)
            .Where(entry => !NativeLibrary.TryGetExport(handle, entry, out _))
            .ToList();
        Assert.True(missing.Count == 0, $"{missing.Count} entry points are not exported by {library}: {string.Join(", ", missing.Take(20))}");

        // Exported variables are properties over the symbol's address, so the layout class lists their symbols.
        if (assembly.GetTypes().Select(t => t.GetField("ExportedGlobals", BindingFlags.Public | BindingFlags.Static)).FirstOrDefault(f => f is not null) is { } globals)
        {
            List<string> missingGlobals = ((string[])globals.GetValue(null)!).Where(symbol => !NativeLibrary.TryGetExport(handle, symbol, out _)).ToList();
            Assert.True(missingGlobals.Count == 0, $"{missingGlobals.Count} variables are not exported by {library}: {string.Join(", ", missingGlobals.Take(20))}");
        }
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public void StructSizesMatchC(string binding)
    {
        Assembly assembly = BindingAssemblies[binding];
        Type layout = assembly.GetTypes().First(t => t.GetField("Sizes", BindingFlags.Public | BindingFlags.Static)?.FieldType == typeof((Type, int)[]));
        var expected = ((Type Type, int Size)[])layout.GetField("Sizes")!.GetValue(null)!;

        // A binding whose structs are all opaque handles (or that has none) has no layout to check.
        if (expected.Length == 0)
        {
            Assert.Contains(binding, NoSizedStructs);
            return;
        }

        // Structs with a C long have the size the generator saw (long = 8 bytes) everywhere but on Windows.
        if (!OperatingSystem.IsWindows() && layout.GetField("LongDependentSizes") is { } longDependent)
        {
            expected = [.. expected, .. (IEnumerable<(Type, int)>)longDependent.GetValue(null)!];
        }

        // Steam's callback structs are packed to 4 bytes except on Windows, where the packing is 8.
        if (layout.GetField("CallbackPackSizes") is { } packed)
        {
            bool windows = OperatingSystem.IsWindows();
            expected = [.. expected, .. ((IEnumerable<(Type Type, int Pack4, int Pack8)>)packed.GetValue(null)!).Select(e => (e.Type, windows ? e.Pack8 : e.Pack4))];
        }

        MethodInfo sizeOf = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!;
        List<string> wrong = expected
            .Select(e => (e.Type, e.Size, Actual: (int)sizeOf.MakeGenericMethod(e.Type).Invoke(null, null)!))
            .Where(x => x.Size != x.Actual)
            .Select(x => $"{x.Type.Name}: C {x.Size}, C# {x.Actual}")
            .ToList();
        Assert.True(wrong.Count == 0, $"{wrong.Count} struct layouts differ: {string.Join("; ", wrong.Take(20))}");
    }
}
