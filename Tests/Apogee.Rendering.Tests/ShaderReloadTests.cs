using Apogee.Core.Graphics;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Graphics;
using Apogee.Graphics.Shaders;
using Apogee.Graphics.Tests;
using Apogee.Native.Tests;
using Apogee.Native.Vulkan;
using Apogee.Rendering.Shaders;
using Xunit;

namespace Apogee.Rendering.Tests;

/// <summary>
/// Editing a shader while a renderer runs: the pipelines that use it are rebuilt on another thread and swapped in at a frame boundary, and the validation layer (checked when the test ends) sees
/// nothing destroyed while the GPU still used it. The renderer works on a copy of the engine's shaders in a temporary folder, which the tests edit.
/// </summary>
public sealed class ShaderReloadTests : GpuTest, IDisposable
{
    private const uint Width = 192;
    private const uint Height = 108;

    private readonly string _folder = Directory.CreateTempSubdirectory("apogee-shader-reload").FullName;

    public ShaderReloadTests(GpuFixture gpu)
        : base(gpu)
    {
        NativeRequirements.Require(typeof(Apogee.Native.Slang.Slang).Assembly, "slang_c");
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Shaders"), "*.slang"))
        {
            File.Copy(file, Path.Combine(_folder, Path.GetFileName(file)));
        }
    }

    void IDisposable.Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        finally
        {
            Dispose(); // checks the validation layer
            GC.SuppressFinalize(this);
        }
    }

    private ShaderLibrary NewLibrary()
    {
        var files = new VirtualFileSystem();
        files.Mount("shaders://", new DirectorySource(_folder));
        return new ShaderLibrary(files);
    }

    private void Edit(string file, Func<string, string> change)
    {
        string path = Path.Combine(_folder, file);
        string before = File.ReadAllText(path);
        string after = change(before);
        Assert.NotEqual(before, after);
        File.WriteAllText(path, after);
    }

    private sealed class Rig : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly GpuImage _target;
        private readonly FrameLoop _loop;
        private readonly RenderScene _scene;
        private readonly Camera _camera;

        public Rig(GraphicsDevice device, ShaderLibrary library)
        {
            _device = device;
            Renderer = new SceneRenderer(device, library);
            _target = device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, Width, Height, ImageUsage.ColorAttachment), "Reload target");
            _loop = device.CreateFrameLoop();
            _scene = Renderer.CreateScene(GoldenScene.Build());
            _camera = new Camera { Position = new Vector3(-0.5f, 4.2f, 9f) };
            _camera.LookAt(new Vector3(0f, 0.8f, 0f));
        }

        public SceneRenderer Renderer { get; }

        /// <summary>Records and submits a frame; with <paramref name="wait"/> false the GPU may still be running it when the call returns.</summary>
        public void Render(bool wait = true)
        {
            Frame frame = _loop.BeginFrame()!;
            Renderer.Render(frame, _scene, _camera, RenderTarget.Offscreen(_target), new RenderSettings { SkyColor = new Vector3(0.3f, 0.42f, 0.65f) });
            _loop.EndFrame(frame);
            if (wait)
            {
                _loop.WaitIdle();
            }
        }

        public byte[] Pixels()
        {
            _loop.WaitIdle();
            return _device.ReadImage(_target, ImageState.TransferSource);
        }

        public void Dispose()
        {
            _loop.Dispose();
            _scene.Dispose();
            _target.Dispose();
            Renderer.Dispose();
        }
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (i % 4 != 3)
            {
                sum += Math.Abs(a[i] - b[i]);
            }
        }

        return sum / (a.Length / 4 * 3);
    }

    private const string TonemapReturn = "return float4(linearToSrgb(saturate(neutralTonemap(hdr))), 1.0);";

    [Fact]
    public void AnEditToTheToneMapperChangesTheNextImageAndRebuildsOnlyItsPipelines()
    {
        ShaderLibrary library = NewLibrary();
        using var rig = new Rig(Device, library);
        rig.Render();
        byte[] before = rig.Pixels();

        Edit("tonemap.slang", text => text.Replace(TonemapReturn, "return float4(1.0 - linearToSrgb(saturate(neutralTonemap(hdr))), 1.0);", StringComparison.Ordinal));
        library.NotifyChanged(["shaders://tonemap.slang"]);
        rig.Renderer.WaitForShaderRebuilds();
        rig.Render();
        byte[] after = rig.Pixels();

        // The two images, for looking at (the golden test does the same).
        File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "shader-reload-before.png"), Png.Encode((int)Width, (int)Height, before));
        File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "shader-reload-after.png"), Png.Encode((int)Width, (int)Height, after));

        Assert.Equal((0, 0, 0, 1), rig.Renderer.ShaderRebuilds);
        Assert.True(MeanDifference(before, after) > 60, "the image changed");
        for (int i = 0; i < before.Length; i += 4)
        {
            Assert.InRange(before[i] + after[i], 253, 257); // the new shader inverts the colors
        }

        Assert.Empty(library.Errors);
    }

    [Fact]
    public void AnEditThatDoesNotCompileKeepsTheOldPipelinesAndIsReportedUntilItIsFixed()
    {
        ShaderLibrary library = NewLibrary();
        using var rig = new Rig(Device, library);
        rig.Render();
        byte[] before = rig.Pixels();

        Edit("tonemap.slang", text => text.Replace(TonemapReturn, "return float4(undefinedThing, 1.0);", StringComparison.Ordinal));
        library.NotifyChanged(["shaders://tonemap.slang"]);
        rig.Renderer.WaitForShaderRebuilds();
        rig.Render();

        Assert.Equal(0, rig.Renderer.ShaderRebuilds.Tonemap);
        Assert.Equal(before, rig.Pixels());
        string error = Assert.Single(library.Errors);
        Assert.Contains("undefinedThing", error);

        Edit("tonemap.slang", text => text.Replace("undefinedThing", "float3(0.0, 1.0, 0.0)", StringComparison.Ordinal));
        library.NotifyChanged(["shaders://tonemap.slang"]);
        rig.Renderer.WaitForShaderRebuilds();
        rig.Render();

        Assert.Equal(1, rig.Renderer.ShaderRebuilds.Tonemap);
        Assert.Empty(library.Errors);
        byte[] green = rig.Pixels();
        Assert.All(Enumerable.Range(0, green.Length / 4), i => Assert.Equal((0, 255, 0), (green[i * 4], green[(i * 4) + 1], green[(i * 4) + 2])));
    }

    [Fact]
    public void AnEditToTheSharedModuleRebuildsEverythingThatImportsItAndAnotherFileNothing()
    {
        ShaderLibrary library = NewLibrary();
        using var rig = new Rig(Device, library);
        rig.Render();
        byte[] before = rig.Pixels();

        library.NotifyChanged(["shaders://something_else.slang", "res://imgui.slang"]);
        rig.Renderer.WaitForShaderRebuilds();
        Assert.Equal((0, 0, 0, 0), rig.Renderer.ShaderRebuilds);

        Edit("common.slang", text => text + "\n// an edit that changes nothing the GPU runs\n");
        library.NotifyChanged(["shaders://common.slang"]);
        rig.Renderer.WaitForShaderRebuilds();
        rig.Render();

        Assert.Equal((1, 1, 1, 1), rig.Renderer.ShaderRebuilds);
        Assert.Equal(before, rig.Pixels());
    }

    [Fact]
    public void ARebuildInTheMiddleOfFramesInFlightDestroysNothingTheGpuStillUses()
    {
        ShaderLibrary library = NewLibrary();
        using var rig = new Rig(Device, library);
        for (int i = 0; i < 3; i++)
        {
            rig.Render(wait: false);
        }

        Edit("forward.slang", text => text + "\n// touched\n");
        library.NotifyChanged(["shaders://forward.slang"]);
        rig.Renderer.WaitForShaderRebuilds();
        for (int i = 0; i < 6; i++)
        {
            rig.Render(wait: false); // the first of these swaps the pipelines while earlier frames may still be running on the old ones
        }

        rig.Render();
        Assert.Equal((0, 0, 1, 0), rig.Renderer.ShaderRebuilds);
        // The validation layer is checked when the test ends: a pipeline destroyed too early is an error there.
    }

    [Fact]
    public void ChangesWhileABuildIsRunningStartAnotherAfterIt()
    {
        ShaderLibrary library = NewLibrary();
        using var rig = new Rig(Device, library);
        rig.Render();

        for (int i = 0; i < 5; i++)
        {
            Edit("forward.slang", text => text + $"\n// edit {i}\n");
            library.NotifyChanged(["shaders://forward.slang"]);
        }

        rig.Renderer.WaitForShaderRebuilds();
        rig.Render();

        Assert.Equal(1, rig.Renderer.ShaderRebuilds.Forward); // the builds that were not applied before a newer one finished are dropped
        Assert.Empty(library.Errors);
    }
}
