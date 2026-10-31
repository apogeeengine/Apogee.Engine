using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Apogee.Build.Bindings;
using ClangSharp.Interop;
using Xunit;

namespace Apogee.Build.Tests;

/// <summary>Runs the generator on fixtures and on the real Vulkan registry. Needs libclang and an installed clang.</summary>
public class BindingsGeneratorTests
{
    private static readonly string s_root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "Apogee.slnx")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException("Apogee.slnx not found above the test output.");
    }

    /// <summary>Skips without clang and libclang; fails instead when <c>APOGEE_REQUIRE_CLANG=1</c> (the Linux CI job), so the tests cannot silently stop running.</summary>
    private static void RequireClang()
    {
        string? problem = null;
        try
        {
            using Process process = Process.Start(new ProcessStartInfo("clang", "-print-resource-dir") { RedirectStandardOutput = true })!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                problem = $"clang -print-resource-dir exited with {process.ExitCode}.";
            }
            else
            {
                CXIndex.Create().Dispose();
            }
        }
        catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            problem = $"clang or libclang is not usable: {e.GetType().Name}: {e.Message}";
        }

        if (problem is not null)
        {
            if (Environment.GetEnvironmentVariable("APOGEE_REQUIRE_CLANG") == "1")
            {
                Assert.Fail(problem);
            }

            Assert.Skip(problem);
        }
    }

    private static BindingConfig FixtureConfig() => new()
    {
        Name = "fixture",
        Project = "Bin/bindings/fixture",
        Namespace = "Fixture",
        ClassName = "Fixture",
        Library = new LibraryConfig { Name = "fixture", Linux = ["libfixture.so"], Windows = ["fixture.dll"], Macos = ["libfixture.dylib"] },
        IncludeDirs = ["Tests/Apogee.Build.Tests/Fixtures/other", "Tests/Apogee.Build.Tests/Fixtures/own"],
        Includes = ["own.h"],
        OwnDirs = ["Tests/Apogee.Build.Tests/Fixtures/own"],
        StripPrefixes = ["own_"],
        ForeignTypes = new() { ["^Other"] = "Other.Binding" },
        TypeMap = new() { ["OtherFlags"] = "uint" },
    };

    [Fact]
    public void ForeignTypesAreReferencedNotGenerated()
    {
        RequireClang();

        BindingConfig config = FixtureConfig();
        ApiModel model = new HeaderParser(config, s_root).Parse();

        Assert.Equal(["Other.Binding"], model.Usings);
        Assert.DoesNotContain(model.Skipped, line => !line.StartsWith("variable ", StringComparison.Ordinal)); // the fixture's array variable is reported

        // Handles, structs and enums by value and behind pointers resolve to the other assembly's types; typeMap still wins for a typedef.
        StructModel createInfo = Assert.Single(model.Structs, s => s.Name == "OwnCreateInfo");
        Assert.Equal(
            [("Owner", "OtherHandle"), ("Info", "OtherInfo*"), ("Flags", "uint"), ("Kind", "OtherKind"), ("Usage", "OwnUsageFlagBits")],
            createInfo.Fields.Select(f => (f.Name, f.Type)));
        Assert.Equal(["OwnUsageFlagBits"], model.Enums.Select(e => e.Name));

        FunctionModel create = Assert.Single(model.Functions, f => f.CName == "own_create");
        Assert.Equal("OtherHandle", create.ReturnType);
        Assert.Equal(["OwnCreateInfo*", "OtherInfo*"], create.Parameters.Select(p => p.Type));

        FunctionModel use = Assert.Single(model.Functions, f => f.CName == "own_use");
        Assert.Equal(["OtherKind", "OtherInfo", "OtherInfo**"], use.Parameters.Select(p => p.Type));

        string types = CSharpEmitter.Emit(config, model)["Fixture.Types.g.cs"];
        Assert.Contains("using Other.Binding;", types);
        Assert.DoesNotContain("struct OtherInfo", types);
    }

    [Fact]
    public void ABannerOfAsterisksIsNotADescription()
    {
        RequireClang();

        ApiModel model = new HeaderParser(FixtureConfig(), s_root).Parse();

        Assert.Null(Assert.Single(model.Functions, f => f.CName == "own_banner").Doc);
        Assert.Equal("Says hello.", Assert.Single(model.Functions, f => f.CName == "own_hello").Doc);
    }

    [Fact]
    public void FlagBitsEnumsAreFlagsAndTheirFlagsTypedefIsTheEnum()
    {
        RequireClang();

        ApiModel model = new HeaderParser(FixtureConfig(), s_root).Parse();

        // OwnUsageFlags → OwnUsageFlagBits (see the Usage field above); no _BIT suffix, no _MAX_ENUM sentinel.
        EnumModel usage = Assert.Single(model.Enums);
        Assert.True(usage.IsFlags);
        Assert.Equal([("Read", "1"), ("Write", "2")], usage.Members.Select(m => (m.Name, m.Value)));
    }

    [Fact]
    public void StructsWithALongHaveTheirOwnSizeTable()
    {
        RequireClang();

        BindingConfig config = FixtureConfig();
        string layout = CSharpEmitter.Emit(config, new HeaderParser(config, s_root).Parse())["Fixture.Layout.g.cs"];

        // OwnStream has a long; OwnHolder contains an OwnStream, so it depends on it too. OwnCreateInfo does not.
        string sizes = layout[..layout.IndexOf("LongDependentSizes", StringComparison.Ordinal)];
        string longDependent = layout[layout.IndexOf("LongDependentSizes", StringComparison.Ordinal)..];
        Assert.Contains("typeof(OwnCreateInfo)", sizes);
        Assert.DoesNotContain("typeof(OwnStream)", sizes);
        Assert.Contains("typeof(OwnStream), 16", longDependent);
        Assert.Contains("typeof(OwnHolder), 24", longDependent);
        Assert.DoesNotContain("typeof(OwnCreateInfo)", longDependent);
    }

    [Fact]
    public void ExportedVariablesAreRefPropertiesFoundByName()
    {
        RequireClang();

        BindingConfig config = FixtureConfig();
        config.GlobalRenames = new() { ["^FLECS_ID(.+)ID_$"] = "$1_id" };
        ApiModel model = new HeaderParser(config, s_root).Parse();

        // Arrays are skipped (and reported); the id macro's symbol is renamed before the usual prefix stripping.
        Assert.Equal(
            [("own_limit", "Limit", "ulong", true), ("own_counter", "Counter", "int", false), ("FLECS_IDown_valueID_", "ValueId", "ulong", true)],
            model.Globals.Select(g => (g.CName, g.Name, g.Type, g.IsConst)));
        Assert.Equal(["variable own_table: arrays are not supported"], model.Skipped);

        Dictionary<string, string> files = CSharpEmitter.Emit(config, model);
        string globals = files["Fixture.Globals.g.cs"];
        Assert.Contains("public static ref readonly ulong Limit =>", globals);
        Assert.Contains("public static ref int Counter =>", globals);
        Assert.Contains("GlobalAddress(\"FLECS_IDown_valueID_\")", globals);
        Assert.Contains("\"own_limit\",", files["Fixture.Layout.g.cs"]);
    }

    [Fact]
    public void AnArrayParameterIsAPointerToItsFirstElement()
    {
        RequireClang();

        ApiModel model = new HeaderParser(FixtureConfig(), s_root).Parse();

        // `float color[4]` is `float*` in C; passing an inline array by value would hand the function the bytes instead of an address.
        FunctionModel function = Assert.Single(model.Functions, f => f.CName == "own_set_color");
        Assert.Equal(["byte*", "float*", "InlineArray2_int*", "int*"], function.Parameters.Select(p => p.Type));
    }

    [Fact]
    public void ExcludedVariablesAreLeftOut()
    {
        RequireClang();

        BindingConfig config = FixtureConfig();
        config.ExcludeGlobals = ["^own_(limit|counter)$", "^own_table$"];
        config.GlobalRenames = new() { ["^FLECS_ID(.+)ID_$"] = "$1_id" };
        ApiModel model = new HeaderParser(config, s_root).Parse();

        Assert.Equal(["FLECS_IDown_valueID_"], model.Globals.Select(g => g.CName));
        Assert.Empty(model.Skipped);
    }

    private static (BindingConfig Config, ApiModel Model) ParseVulkan()
    {
        BindingConfig config = BindingConfig.Load(Path.Combine(s_root, "Tools/Apogee.Build/Bindings/Configs/vulkan.json"));
        return (config, new VulkanRegistryParser(config, s_root).Parse());
    }

    [Fact]
    public void VulkanStructsSetTheirStructureType()
    {
        RequireClang();

        (BindingConfig config, ApiModel model) = ParseVulkan();
        Assert.Equal("RenderingInfo", model.Structs.Single(s => s.Name == "VkRenderingInfo").StructureType);
        Assert.Equal("ApplicationInfo", model.Structs.Single(s => s.Name == "VkApplicationInfo").StructureType);
        Assert.Null(model.Structs.Single(s => s.Name == "VkBaseInStructure").StructureType);
        Assert.Null(model.Structs.Single(s => s.Name == "VkExtent2D").StructureType);

        string types = CSharpEmitter.Emit(config, model)["Vk.Types.g.cs"];
        Assert.Contains("    public VkRenderingInfo()\n    {\n        SType = VkStructureType.RenderingInfo;\n    }", types.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("public VkBaseInStructure()", types);
        Assert.DoesNotContain("public VkExtent2D()", types);
    }

    [Fact]
    public void CommittedVulkanBindingsAreCurrent()
    {
        RequireClang();

        (BindingConfig config, ApiModel model) = ParseVulkan();
        foreach ((string file, string contents) in CSharpEmitter.Emit(config, model))
        {
            string committed = File.ReadAllText(Path.Combine(s_root, config.Project, "Generated", file));
            Assert.True(
                committed.ReplaceLineEndings("\n") == contents.ReplaceLineEndings("\n"),
                $"{file} differs from what the generator writes: run ./build.sh bindings vulkan.");
        }
    }
}
