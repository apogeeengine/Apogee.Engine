using System;
using System.IO;
using System.Linq;
using Apogee.Graphics.Shaders;
using Apogee.Native.Tests;
using Xunit;

namespace Apogee.Graphics.Tests;

/// <summary>Compiling needs the Slang library but no GPU.</summary>
public sealed class ShaderCompilerTests : IDisposable
{
    private const string Compute = """
        RWStructuredBuffer<float> output;
        [shader("compute")] [numthreads(8, 1, 1)]
        void first(uint3 id : SV_DispatchThreadID) { output[id.x] = 1.0; }
        [shader("compute")] [numthreads(4, 1, 1)]
        void second(uint3 id : SV_DispatchThreadID) { output[id.x] = 2.0; }
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("apogee-shaders").FullName;

    public ShaderCompilerTests()
    {
        NativeRequirements.Require(typeof(Apogee.Native.Slang.Slang).Assembly, "slang_c");
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Cache => Path.Combine(_directory, "cache");

    [Fact]
    public void EveryEntryPointOfAModuleGetsItsOwnSpirv()
    {
        CompiledShader shader = new ShaderCompiler().CompileSource("pair", Compute);

        Assert.Equal(["first", "second"], shader.EntryPoints.Order());
        foreach (string entry in shader.EntryPoints)
        {
            byte[] spirv = shader.Spirv(entry);
            Assert.Equal(0x07230203u, BitConverter.ToUInt32(spirv, 0));
            Assert.Equal(0, spirv.Length % 4);
        }

        Assert.NotEqual(shader.Spirv("first"), shader.Spirv("second"));
        Assert.Contains("first", shader.ReflectionJson);
        Assert.False(shader.FromCache);
        Assert.Throws<ArgumentException>(() => shader.Spirv("missing"));
    }

    private const string Pointers = """
        struct Push { float* output; uint count; };
        [[vk::push_constant]] ConstantBuffer<Push> push;
        [shader("compute")] [numthreads(8, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
        #ifdef APOGEE_METAL
            if (id.x < push.count) push.output[id.x] = 2.5;
        #else
            if (id.x < push.count) push.output[id.x] = 1.5;
        #endif
        }
        """;

    [Fact]
    public void AModuleCompilesToMetalShadingLanguageWithDevicePointersAndTheMetalDefine()
    {
        CompiledShader metal = new ShaderCompiler(new ShaderCompilerOptions { Backend = ShaderBackend.Msl }).CompileSource("pointers", Pointers);
        CompiledShader vulkan = new ShaderCompiler().CompileSource("pointers", Pointers);

        string source = metal.Msl("main");
        Assert.Equal(ShaderBackend.Msl, metal.Backend);
        Assert.Contains("#include <metal_stdlib>", source);
        Assert.Contains("[[kernel]]", source);
        Assert.Contains("float device*", source);          // a pointer in the push constants is a device pointer
        Assert.Contains("constant*", source);              // the push constants are a constant buffer
        Assert.Contains("2.5", source);                    // APOGEE_METAL is defined for the Metal build...
        Assert.DoesNotContain("1.5", source);
        Assert.Equal(ShaderBackend.Spirv, vulkan.Backend); // ...and only for it
        Assert.Equal(0x07230203u, BitConverter.ToUInt32(vulkan.Spirv("main"), 0));
        Assert.Throws<InvalidOperationException>(() => metal.Spirv("main"));
    }

    [Fact]
    public void TheMetalBuildIsNotConfusedWithTheVulkanOneInTheCache()
    {
        _ = new ShaderCompiler(new ShaderCompilerOptions { CacheDirectory = Cache }).CompileSource("pointers", Pointers);

        CompiledShader metal = new ShaderCompiler(new ShaderCompilerOptions { CacheDirectory = Cache, Backend = ShaderBackend.Msl }).CompileSource("pointers", Pointers);

        Assert.Equal(ShaderBackend.Msl, metal.Backend);
        Assert.Contains("[[kernel]]", metal.Msl("main"));
    }

    [Fact]
    public void AComputeEntryPointSaysItsThreadGroupSize()
    {
        CompiledShader shader = new ShaderCompiler().CompileSource("pair", Compute);

        Assert.Equal(new ThreadGroupSize(8, 1, 1), shader.Code("first").Group);
        Assert.Equal(new ThreadGroupSize(4, 1, 1), shader.Code("second").Group);
        Assert.Equal(ShaderBackend.Spirv, shader.Code("first").Backend);
    }

    [Fact]
    public void ACompileErrorCarriesTheFileAndLine()
    {
        var error = Assert.Throws<ShaderCompileException>(() => new ShaderCompiler().CompileSource("broken", "[shader(\"compute\")] [numthreads(1,1,1)]\nvoid main() { undefinedThing(); }", "broken.slang"));

        Assert.Contains("broken.slang:2", error.Diagnostics);
        Assert.Contains("undefinedThing", error.Diagnostics);
        Assert.Equal("broken", error.ModuleName);
    }

    [Fact]
    public void AFileImportsFromItsOwnDirectoryAndFromTheSearchPaths()
    {
        string library = Path.Combine(_directory, "lib");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "math.slang"), "public float triple(float x) { return x * 3.0; }");
        File.WriteAllText(Path.Combine(_directory, "sibling.slang"), "public float plusOne(float x) { return x + 1.0; }");
        string main = Path.Combine(_directory, "main.slang");
        File.WriteAllText(main, """
            import math;
            import sibling;
            RWStructuredBuffer<float> output;
            [shader("compute")] [numthreads(1, 1, 1)]
            void run(uint3 id : SV_DispatchThreadID) { output[0] = plusOne(triple(2.0)); }
            """);

        CompiledShader shader = new ShaderCompiler(new ShaderCompilerOptions { SearchPaths = [library] }).CompileFile(main);

        Assert.Equal(["run"], shader.EntryPoints);
        Assert.Contains(shader.Dependencies, d => d.EndsWith("math.slang", StringComparison.Ordinal));
        Assert.Contains(shader.Dependencies, d => d.EndsWith("sibling.slang", StringComparison.Ordinal));
    }

    [Fact]
    public void DefinesReachTheShader()
    {
        const string source = """
            RWStructuredBuffer<float> output;
            [shader("compute")] [numthreads(1, 1, 1)]
            void run(uint3 id : SV_DispatchThreadID) { output[0] = SCALE; }
            """;

        var compiler = new ShaderCompiler(new ShaderCompilerOptions { Defines = new System.Collections.Generic.Dictionary<string, string> { ["SCALE"] = "4.0" } });
        Assert.Equal(["run"], compiler.CompileSource("defined", source).EntryPoints);
        Assert.Throws<ShaderCompileException>(() => new ShaderCompiler().CompileSource("undefined", source));
    }

    [Fact]
    public void AnUnchangedShaderComesFromTheCacheAndIsTheSameSpirv()
    {
        var compiler = new ShaderCompiler(new ShaderCompilerOptions { CacheDirectory = Cache });

        CompiledShader first = compiler.CompileSource("cached", Compute);
        CompiledShader second = compiler.CompileSource("cached", Compute);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(first.EntryPoints.Order(), second.EntryPoints.Order());
        Assert.Equal(first.Spirv("first"), second.Spirv("first"));
        Assert.Equal(first.ReflectionJson, second.ReflectionJson);
    }

    [Fact]
    public void ChangingTheSourceOrTheOptionsMissesTheCache()
    {
        var compiler = new ShaderCompiler(new ShaderCompilerOptions { CacheDirectory = Cache });
        compiler.CompileSource("variant", Compute);

        Assert.False(compiler.CompileSource("variant", Compute.Replace("2.0", "3.0", StringComparison.Ordinal)).FromCache);
        Assert.False(new ShaderCompiler(new ShaderCompilerOptions { CacheDirectory = Cache, Optimization = 3 }).CompileSource("variant", Compute).FromCache);
        Assert.True(compiler.CompileSource("variant", Compute).FromCache);
    }

    [Fact]
    public void EditingAnImportedFileInvalidatesTheCache()
    {
        string import = Path.Combine(_directory, "constants.slang");
        File.WriteAllText(import, "public float value() { return 1.0; }");
        string main = Path.Combine(_directory, "uses.slang");
        File.WriteAllText(main, """
            import constants;
            RWStructuredBuffer<float> output;
            [shader("compute")] [numthreads(1, 1, 1)]
            void run(uint3 id : SV_DispatchThreadID) { output[0] = value(); }
            """);
        var compiler = new ShaderCompiler(new ShaderCompilerOptions { CacheDirectory = Cache });

        Assert.False(compiler.CompileFile(main).FromCache);
        Assert.True(compiler.CompileFile(main).FromCache);

        File.WriteAllText(import, "public float value() { return 2.0; }");
        CompiledShader recompiled = compiler.CompileFile(main);
        Assert.False(recompiled.FromCache);
        Assert.True(compiler.CompileFile(main).FromCache);
    }

    [Fact]
    public void ACorruptCacheFileIsJustAMiss()
    {
        var compiler = new ShaderCompiler(new ShaderCompilerOptions { CacheDirectory = Cache });
        compiler.CompileSource("corrupt", Compute);
        foreach (string file in Directory.GetFiles(Cache, "*.shader"))
        {
            File.WriteAllBytes(file, [1, 2, 3, 4, 5]);
        }

        Assert.False(compiler.CompileSource("corrupt", Compute).FromCache);
        Assert.True(compiler.CompileSource("corrupt", Compute).FromCache);
    }
}
