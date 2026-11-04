using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apogee.Graphics.Shaders;
using Xunit;

namespace Apogee.Graphics.Tests;

/// <summary>The compiled-shader artifact. It is plain data, so these need neither the compiler nor a GPU.</summary>
public class ShaderBundleTests
{
    private static CompiledShader Shader(byte fill, string reflection = "{}") =>
        new("module", new Dictionary<string, byte[]> { ["vs"] = [fill, 1, 2, 3], ["fs"] = [fill, 9, 9, 9, 9] }, reflection, "careful", [], fromCache: false);

    [Fact]
    public void VariantsComeBackWithTheirEntryPointsAndReflection()
    {
        byte[] bundle = ShaderBundle.Write("module", [("", Shader(1, "{\"a\":1}")), ("FAST=1", Shader(2, "{\"b\":2}"))]);

        CompiledShader plain = ShaderBundle.Read(bundle);
        CompiledShader fast = ShaderBundle.Read(bundle, "FAST=1", ["x.slang"]);

        Assert.Equal(["", "FAST=1"], ShaderBundle.Variants(bundle));
        Assert.Equal(["fs", "vs"], plain.EntryPoints.Order());
        Assert.Equal(new byte[] { 1, 1, 2, 3 }, plain.Spirv("vs"));
        Assert.Equal(new byte[] { 2, 9, 9, 9, 9 }, fast.Spirv("fs"));
        Assert.Equal("{\"a\":1}", plain.ReflectionJson);
        Assert.Equal("{\"b\":2}", fast.ReflectionJson);
        Assert.Equal("careful", fast.Warnings);
        Assert.Equal(["x.slang"], fast.Dependencies);
        Assert.Equal("module", plain.ModuleName);
    }

    [Fact]
    public void AVariantIsNamedByItsSortedDefines()
    {
        var defines = new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" };

        Assert.Equal("A=1;B=2", ShaderBundle.VariantKey(defines));
        Assert.Equal("", ShaderBundle.VariantKey(new Dictionary<string, string>()));
    }

    [Fact]
    public void AMissingVariantNamesTheOnesThereAre()
    {
        byte[] bundle = ShaderBundle.Write("module", [("", Shader(1))]);

        var error = Assert.Throws<InvalidDataException>(() => ShaderBundle.Read(bundle, "NOPE=1"));

        Assert.Contains("NOPE=1", error.Message);
        Assert.Contains("(default)", error.Message);
    }

    [Fact]
    public void ABundleOfAnotherAbiIsRefusedWithAMessageToImportAgain()
    {
        byte[] bundle = ShaderBundle.Write("module", [("", Shader(1))]);
        BitConverter.GetBytes(ShaderAbi.Version + 1).CopyTo(bundle, 8); // magic (4), format version (4), then the ABI

        var error = Assert.Throws<ShaderCompileException>(() => ShaderBundle.Read(bundle));

        Assert.Contains($"ABI {ShaderAbi.Version + 1}", error.Message);
        Assert.Contains("--import", error.Message);
    }

    private static CompiledShader MetalShader(string fill) =>
        new("module", new Dictionary<string, byte[]> { ["vs"] = System.Text.Encoding.UTF8.GetBytes("// vs " + fill), ["fs"] = System.Text.Encoding.UTF8.GetBytes("// fs " + fill) }, "{\"metal\":1}", "", [], fromCache: false, ShaderBackend.Msl);

    [Fact]
    public void ABundleHoldsEveryBackendAndEachReaderTakesItsOwn()
    {
        byte[] bundle = ShaderBundle.Write("module", [("", (IReadOnlyList<CompiledShader>)[Shader(1, "{\"spirv\":1}"), MetalShader("plain")]), ("FAST=1", [Shader(2), MetalShader("fast")])]);

        CompiledShader vulkan = ShaderBundle.Read(bundle);
        CompiledShader metal = ShaderBundle.Read(bundle, backend: ShaderBackend.Msl);
        CompiledShader fastMetal = ShaderBundle.Read(bundle, "FAST=1", backend: ShaderBackend.Msl);

        Assert.Equal(["", "FAST=1"], ShaderBundle.Variants(bundle));
        Assert.Equal(ShaderBackend.Spirv, vulkan.Backend);
        Assert.Equal(new byte[] { 1, 1, 2, 3 }, vulkan.Spirv("vs"));
        Assert.Equal(ShaderBackend.Msl, metal.Backend);
        Assert.Equal("// vs plain", metal.Msl("vs"));
        Assert.Equal("// fs fast", fastMetal.Msl("fs"));
        Assert.Equal(["fs", "vs"], metal.EntryPoints.Order());
        Assert.Equal("{\"spirv\":1}", metal.ReflectionJson); // the first backend's
        Assert.Equal(ShaderBackend.Msl, metal.Code("vs").Backend);
        Assert.Throws<InvalidOperationException>(() => metal.Spirv("vs"));
        Assert.Throws<InvalidOperationException>(() => vulkan.Msl("vs"));
    }

    [Fact]
    public void AReaderOfABackendTheBundleLacksIsToldToImportAgain()
    {
        byte[] bundle = ShaderBundle.Write("module", [("", Shader(1))]);

        var error = Assert.Throws<ShaderCompileException>(() => ShaderBundle.Read(bundle, backend: ShaderBackend.Msl));

        Assert.Contains("no Msl code", error.Message);
        Assert.Contains("--import", error.Message);
    }

    [Fact]
    public void CodeOfBackendsThatAreNotMadeYetIsSkippedNotMisread()
    {
        byte[] bundle = ShaderBundle.Write("module", [("", Shader(1))]);
        // The first blob's backend byte follows magic, format, ABI, the module name ("module": 1 + 6), the variant count, the variant name ("": 1),
        // the reflection ("{}": 1 + 2), the warnings ("careful": 1 + 7) and the blob count.
        int backendOffset = 4 + 4 + 4 + 7 + 4 + 1 + 3 + 8 + 4;
        Assert.Equal((byte)ShaderBackend.Spirv, bundle[backendOffset]);
        bundle[backendOffset] = (byte)ShaderBackend.Dxil;

        CompiledShader shader = ShaderBundle.Read(bundle);

        Assert.Single(shader.EntryPoints);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(40)]
    public void ADamagedOrForeignFileIsInvalidData(int keep)
    {
        byte[] bundle = ShaderBundle.Write("module", [("", Shader(1))]);

        Assert.Throws<InvalidDataException>(() => ShaderBundle.Read(bundle[..keep]));
        Assert.Throws<InvalidDataException>(() => ShaderBundle.Read([.. "not a bundle at all, just text"u8]));
    }
}
