using System;
using System.IO;
using System.Linq;
using Weft.Graphics.Shaders;
using Weft.Native.Tests;
using Xunit;

namespace Weft.Rendering.Tests;

/// <summary>Every shipped shader compiles. Needs the Slang library but no GPU.</summary>
public class ShaderCompilationTests
{
    private static readonly string s_shaders = Path.Combine(AppContext.BaseDirectory, "Shaders");

    public ShaderCompilationTests()
    {
        NativeRequirements.Require(typeof(Weft.Native.Slang.Slang).Assembly, "slang_c");
    }

    [Theory]
    [InlineData("depth.slang", new[] { "depthVs", "depthMaskFs" })]
    [InlineData("cluster_build.slang", new[] { "buildClusters" })]
    [InlineData("light_cull.slang", new[] { "cullLights" })]
    [InlineData("forward.slang", new[] { "forwardVs", "forwardFs" })]
    [InlineData("tonemap.slang", new[] { "fullscreenVs", "tonemapFs" })]
    [InlineData("exposure.slang", new[] { "buildHistogram", "resolveExposure" })]
    public void AShippedShaderCompilesWithItsEntryPoints(string file, string[] entryPoints)
    {
        CompiledShader shader = new ShaderCompiler(new ShaderCompilerOptions { SearchPaths = [s_shaders] }).CompileFile(Path.Combine(s_shaders, file));

        Assert.Equal(entryPoints.Order(), shader.EntryPoints.Order());
        Assert.Contains(shader.Dependencies, d => d.EndsWith("common.slang", StringComparison.Ordinal));
    }
}
