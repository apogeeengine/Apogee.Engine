using System;
using Apogee.Core.Logging;
using Apogee.Graphics.Shaders;
using Xunit;

namespace Apogee.Graphics.Tests;

/// <summary>
/// One device for all GPU tests (creating them takes a while). Tests skip when the machine has no usable GPU, unless
/// <c>APOGEE_REQUIRE_VULKAN=1</c> (CI with lavapipe) or, when the device is Metal's (the default on macOS), <c>APOGEE_REQUIRE_METAL=1</c>, where that is a failure.
/// <c>APOGEE_GPU=llvmpipe</c> runs them on the software rasterizer.
/// </summary>
public sealed class GpuFixture : IDisposable
{
    public GpuFixture()
    {
        if (GraphicsDevice.TryCreate(new GraphicsDeviceOptions { ApplicationName = "Apogee tests" }, out GraphicsDevice? device, out string? error))
        {
            Device = device;
        }
        else
        {
            SkipReason = $"{GraphicsDevice.DefaultBackend} is not available: {error}";
        }
    }

    public GraphicsDevice? Device { get; }

    public string? SkipReason { get; }

    public void Dispose() => Device?.Dispose();
}

[CollectionDefinition("gpu")]
public sealed class GpuTests : ICollectionFixture<GpuFixture>
{
}

/// <summary>Base of the GPU tests: gives the device, skips without one, and fails the test when the validation layer reported an error.</summary>
[Collection("gpu")]
public abstract class GpuTest : IDisposable
{
    private readonly GpuFixture _gpu;

    protected GpuTest(GpuFixture gpu)
    {
        _gpu = gpu;
        if (gpu.Device is null)
        {
            string variable = GraphicsDevice.DefaultBackend == GraphicsBackend.Metal ? "APOGEE_REQUIRE_METAL" : "APOGEE_REQUIRE_VULKAN";
            if (Environment.GetEnvironmentVariable(variable) == "1")
            {
                Assert.Fail($"A {GraphicsDevice.DefaultBackend} device is required here ({variable}=1): {gpu.SkipReason}");
            }

            Assert.Skip(gpu.SkipReason ?? $"No {GraphicsDevice.DefaultBackend} device.");
        }

        gpu.Device!.TakeValidationErrors();
    }

    protected GraphicsDevice Device => _gpu.Device!;

    /// <summary>A shader compiler for the code this device runs (SPIR-V for Vulkan, Metal shading language for Metal).</summary>
    protected ShaderCompiler Compiler() => new(new ShaderCompilerOptions { Backend = Device.ShaderBackend });

    /// <summary>Skips a test that is about Vulkan itself when the device is another API's.</summary>
    protected void RequireVulkan()
    {
        if (Device.Backend != GraphicsBackend.Vulkan)
        {
            Assert.Skip($"This test is about Vulkan; the device is {Device.Backend}.");
        }
    }

    /// <summary>
    /// How far a render may be from a stored reference image (the mean difference per channel out of 255, and the worst channel). The references come from Mesa's software rasterizer, which Vulkan GPUs match
    /// closely; another API's rasterizer differs at edges, so Metal gets a looser bound until it has been measured on real hardware (<c>APOGEE_IMAGE_TOLERANCE=mean</c> overrides it).
    /// </summary>
    protected (double Mean, int Worst) ReferenceTolerance
    {
        get
        {
            if (double.TryParse(Environment.GetEnvironmentVariable("APOGEE_IMAGE_TOLERANCE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mean))
            {
                return (mean, 255);
            }

            return Device.Backend == GraphicsBackend.Vulkan ? (0.5, 24) : (2.0, 64);
        }
    }

    public void Dispose()
    {
        string[] errors = Device.TakeValidationErrors();
        GC.SuppressFinalize(this);
        Assert.True(errors.Length == 0, "The validation layer reported errors:\n" + string.Join("\n", errors));
    }
}
