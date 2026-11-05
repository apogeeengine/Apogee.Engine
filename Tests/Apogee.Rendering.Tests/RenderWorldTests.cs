using Apogee.Core.Graphics;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Content;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Graphics;
using Apogee.Graphics.Tests;
using Apogee.Native.Tests;
using Apogee.Reflection;
using Xunit;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Rendering.Tests;

/// <summary>Entities drawn through <see cref="RenderWorld"/>: the same pictures as a <see cref="RenderScene"/> where they should be the same.</summary>
public class RenderWorldTests(GpuFixture gpu) : GpuTest(gpu)
{
    private const uint Width = 192;
    private const uint Height = 108;

    private static readonly string s_shaderCache = Path.Combine(Path.GetTempPath(), "apogee-test-shader-cache");

    private static string RepositoryRoot
    {
        get
        {
            string? directory = AppContext.BaseDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "Apogee.slnx")))
            {
                directory = Path.GetDirectoryName(directory);
            }

            return directory ?? ".";
        }
    }

    /// <summary>A renderer, a world and a model library on the test device, with an offscreen target to draw into.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly GpuImage _target;
        private readonly FrameLoop _loop;

        public Rig(GraphicsDevice device)
        {
            NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
            _device = device;
            Renderer = new SceneRenderer(device, cacheDirectory: s_shaderCache);
            Files = new VirtualFileSystem();
            Models = new ModelLibrary(Files, new ContentLoader(Files));
            Models.Attach(Renderer, new TextureCaps(device.SupportsBc, device.SupportsAstc));
            Ecs = new World();
            View = new RenderWorld(Ecs, Models);
            _target = device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, Width, Height, ImageUsage.ColorAttachment), "RenderWorld target");
            _loop = device.CreateFrameLoop();
        }

        public SceneRenderer Renderer { get; }

        public VirtualFileSystem Files { get; }

        public ModelLibrary Models { get; }

        public World Ecs { get; }

        public RenderWorld View { get; }

        public Entity Spawn(Transform transform)
        {
            Entity entity = Ecs.CreateEntity();
            Ecs.Set(entity, transform);
            return entity;
        }

        public Entity SpawnModel(uint model, Transform transform)
        {
            Entity entity = Spawn(transform);
            Ecs.Set(entity, new ModelRenderer { Model = model });
            return entity;
        }

        /// <summary>Lets the transform system place the entities, then draws the world.</summary>
        public byte[] Render(Camera camera, RenderSettings? settings = null)
        {
            Ecs.Progress(1f / 60f);
            Frame frame = _loop.BeginFrame()!;
            Renderer.Render(frame, View, camera, RenderTarget.Offscreen(_target), settings);
            _loop.EndFrame(frame);
            _loop.WaitIdle();
            return _device.ReadImage(_target, ImageState.TransferSource);
        }

        public void Dispose()
        {
            _loop.WaitIdle();
            View.Dispose();
            Models.Dispose();
            Ecs.Dispose();
            _loop.Dispose();
            _target.Dispose();
            Renderer.Dispose();
        }
    }

    private static Camera LookingAtTheOrigin(float distance = 9f)
    {
        var camera = new Camera { Position = new Double3(-0.5, 4.2, distance) };
        camera.LookAt(new Double3(0, 0.8, 0));
        return camera;
    }

    private static int PixelsDifferent(byte[] a, byte[] b)
    {
        int count = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2])
            {
                count++;
            }
        }

        return count;
    }

    private static long Brightness(byte[] image)
    {
        long sum = 0;
        for (int i = 0; i < image.Length; i += 4)
        {
            sum += image[i] + image[i + 1] + image[i + 2];
        }

        return sum;
    }

    [Fact]
    public void AnEntityDrawsWhatASceneOfTheSameModelDraws()
    {
        using var rig = new Rig(Device);
        using RenderScene scene = rig.Renderer.CreateScene(GoldenScene.Build());
        rig.Spawn(Transform.Identity); // an entity without a model draws nothing
        rig.SpawnModel(rig.Models.Add(GoldenScene.Build()), Transform.Identity);
        Camera camera = LookingAtTheOrigin();

        byte[] fromEntities = rig.Render(camera);

        using var loop = Device.CreateFrameLoop();
        using GpuImage target = Device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, Width, Height, ImageUsage.ColorAttachment), "scene target");
        Frame frame = loop.BeginFrame()!;
        rig.Renderer.Render(frame, scene, camera, RenderTarget.Offscreen(target), new RenderSettings());
        loop.EndFrame(frame);
        loop.WaitIdle();
        byte[] fromScene = Device.ReadImage(target, ImageState.TransferSource);

        // The scene carries its own sun and lights as defaults; the world has none, so compare the shapes: the same pixels are sky and the same are not.
        Assert.True(fromEntities.Distinct().Count() > 20, "the picture is not blank");
        using var empty = new Rig(Device);
        byte[] sky = empty.Render(camera);
        int coveredByEntities = PixelsDifferent(fromEntities, sky);
        int coveredByScene = PixelsDifferent(fromScene, sky);
        Assert.True(coveredByEntities > 1000);
        Assert.InRange(coveredByEntities, coveredByScene * 0.9, coveredByScene * 1.1);
    }

    [Fact]
    public void ManyEntitiesShareOneModelAndEachDrawsItsMeshes()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        int white = model.AddMaterial(new MaterialData());
        model.AddBox(Vector3.Zero, new Vector3(1, 1, 1), white).AddBox(new Vector3(0, 1, 0), new Vector3(0.5f), white);
        uint id = rig.Models.Add(model.ToModel());
        Assert.Equal(2, rig.Models.Find(id)!.MeshCount);

        for (int i = 0; i < 5; i++)
        {
            rig.SpawnModel(id, Transform.At(new Double3((i - 2) * 1.5, 0, 0)));
        }

        var camera = new Camera { Position = new Double3(0, 1, 8) };
        camera.LookAt(new Double3(0, 0.5, 0));
        rig.Render(camera, new RenderSettings { CullDraws = false });

        Assert.Equal(5 * 2 * 2, rig.Renderer.LastStats!.Draws); // two passes (depth and forward) of ten meshes
        Assert.Equal(1, rig.Models.Count);
    }

    [Fact]
    public void RotationAndScaleOfTheEntityPlaceTheModel()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        int white = model.AddMaterial(new MaterialData());
        model.AddWall(Vector3.Zero, 1f, 1f, white); // faces +Z; a single-sided face is not drawn from behind
        uint id = rig.Models.Add(model.ToModel());
        var camera = new Camera { Position = new Double3(0, 0, 6) };
        camera.LookAt(Double3.Zero);

        using var emptyRig = new Rig(Device);
        byte[] sky = emptyRig.Render(camera);

        Entity wall = rig.SpawnModel(id, Transform.Identity);
        int normal = PixelsDifferent(rig.Render(camera), sky);
        Assert.True(normal > 500, $"the wall covers {normal} pixels");

        rig.Ecs.Set(wall, new Transform(Double3.Zero, Quaternion.Identity, new Vector3(2f)));
        int doubled = PixelsDifferent(rig.Render(camera), sky);
        Assert.InRange(doubled, normal * 3.2, normal * 4.8); // twice as wide and as high: about four times the pixels

        rig.Ecs.Set(wall, new Transform(Double3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), Vector3.One));
        int turnedAway = PixelsDifferent(rig.Render(camera), sky);
        Assert.Equal(0, turnedAway); // its back is culled

        rig.Ecs.Set(wall, new Transform(new Double3(-1000, 0, 0), Quaternion.Identity, Vector3.One));
        Assert.Equal(0, PixelsDifferent(rig.Render(camera), sky)); // moved out of view
    }

    [Fact]
    public void ALightEntityLightsTheWorld()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        int white = model.AddMaterial(new MaterialData());
        model.AddGround(10, white);
        rig.SpawnModel(rig.Models.Add(model.ToModel()), Transform.Identity);
        var camera = new Camera { Position = new Double3(0, 4, 8) };
        camera.LookAt(Double3.Zero);

        long dark = Brightness(rig.Render(camera));

        Entity lamp = rig.Spawn(Transform.At(new Double3(0, 2, 0)));
        rig.Ecs.Set(lamp, Light.Point(Vector3.One, 80f, range: 12f));
        long lit = Brightness(rig.Render(camera));
        Assert.Equal(1, rig.Renderer.LastStats!.Lights);
        Assert.True(lit > dark * 1.05, $"{lit} lit against {dark} dark");

        // A directional light is the sun: it is not one of the cluster lights.
        Entity sun = rig.Spawn(new Transform(Double3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 3f), Vector3.One));
        rig.Ecs.Set(sun, Light.Sun(Vector3.One, 3f));
        long sunlit = Brightness(rig.Render(camera));
        Assert.Equal(1, rig.Renderer.LastStats.Lights);
        Assert.True(sunlit > lit, $"{sunlit} in the sun against {lit}");
    }

    [Fact]
    public void ANeverUsedIdAndARemovedModelDrawNothing()
    {
        using var rig = new Rig(Device);
        var model = new TestScene();
        int white = model.AddMaterial(new MaterialData());
        model.AddBox(Vector3.Zero, Vector3.One, white);
        uint id = rig.Models.Add(model.ToModel());
        rig.SpawnModel(id, Transform.Identity);
        rig.SpawnModel(9999, Transform.Identity);
        rig.SpawnModel(0, Transform.Identity);
        var camera = new Camera { Position = new Double3(0, 0, 5) };
        camera.LookAt(Double3.Zero);

        rig.Render(camera);
        Assert.Equal(2, rig.Renderer.LastStats!.Draws); // the box, in two passes

        Assert.True(rig.Models.Remove(id));
        Assert.False(rig.Models.Remove(id));
        Assert.Null(rig.Models.Find(id));
        rig.Render(camera);
        Assert.Equal(0, rig.Renderer.LastStats.Draws);
    }

    [Fact]
    public void AModelFarFromTheOriginLooksTheSameAsAtIt()
    {
        using var rig = new Rig(Device);
        uint id = rig.Models.Add(GoldenScene.Build());
        Entity entity = rig.SpawnModel(id, Transform.Identity);
        byte[] near = rig.Render(LookingAtTheOrigin());

        var far = new Double3(2_000_000, 40, -3_000_000);
        rig.Ecs.Set(entity, Transform.At(far));
        var camera = new Camera { Position = far + new Double3(-0.5, 4.2, 9) };
        camera.LookAt(far + new Double3(0, 0.8, 0));
        byte[] shifted = rig.Render(camera);

        int different = PixelsDifferent(near, shifted);
        Assert.True(different < Width * Height * 0.01, $"{different} pixels differ");
    }

    [Fact]
    public void TheMainCameraEntityDrivesTheCamera()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        using var rig = new Rig(Device);
        var camera = new Camera();
        Assert.False(rig.View.TryGetMainCamera(camera));

        Entity eye = rig.Spawn(new Transform(new Double3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), Vector3.One));
        rig.Ecs.Set(eye, new CameraComponent { FovDegrees = 90f, Near = 0.5f, ClusterFar = 80f });
        rig.Ecs.Progress(1f / 60f);
        Assert.False(rig.View.TryGetMainCamera(camera)); // a lens alone is not the main camera

        rig.Ecs.Add<MainCamera>(eye);
        Assert.True(rig.View.TryGetMainCamera(camera));
        Assert.Equal(new Double3(1, 2, 3), camera.Position);
        Assert.Equal(MathF.PI / 2f, camera.Yaw, 1e-4f);
        Assert.Equal(0f, camera.Pitch, 1e-4f);
        Assert.Equal(MathF.PI / 2f, camera.VerticalFov, 1e-4f);
        Assert.Equal(0.5f, camera.Near);
        Assert.Equal(80f, camera.ClusterFar);
    }

    private static string FixtureModel => Path.Combine(RepositoryRoot, "Tests", "Apogee.Rendering.Tests", "Fixtures", "models", "NormalTangentTest.glb");

    /// <summary>A glTF with one triangle, as text: a second, different model for the reload test.</summary>
    private static string Triangle()
    {
        byte[] buffer = new byte[36];
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
        Buffer.BlockCopy(positions, 0, buffer, 0, 36);
        return "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}],"
            + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],"
            + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}],"
            + "\"bufferViews\":[{\"buffer\":0,\"byteLength\":36}],"
            + "\"buffers\":[{\"byteLength\":36,\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(buffer) + "\"}]}";
    }

    [Fact]
    public void ModelsLoadOncePerPathAndReloadInPlace()
    {
        string folder = Directory.CreateTempSubdirectory("apogee-models").FullName;
        try
        {
            File.Copy(FixtureModel, Path.Combine(folder, "thing.glb"));
            using var rig = new Rig(Device);
            rig.Files.Mount("res://", new DirectorySource(folder));

            uint id = rig.Models.Load("res://thing.glb");
            Assert.NotEqual(0u, id);
            Assert.Equal(id, rig.Models.Load("res://thing.glb"));
            Assert.Equal(id, rig.Models.IdOf("res://./thing.glb"));
            Assert.Equal(1, rig.Models.Count);
            int meshes = rig.Models.Find(id)!.MeshCount;
            Assert.True(meshes >= 1);

            Entity entity = rig.SpawnModel(id, Transform.Identity);
            Assert.False(rig.Models.Reload("res://missing.glb"));

            // The file changes on disk: the same id shows the new model.
            File.Delete(Path.Combine(folder, "thing.glb"));
            File.WriteAllText(Path.Combine(folder, "thing.gltf"), Triangle());
            Assert.Throws<System.IO.FileNotFoundException>(() => rig.Models.Load("res://thing.gltf2")); // a path with no file has no id
            uint other = rig.Models.Load("res://thing.gltf");
            Assert.Equal(1, rig.Models.Find(other)!.MeshCount);
            File.WriteAllText(Path.Combine(folder, "thing.gltf"), Triangle().Replace("\"nodes\":[{\"mesh\":0}]", "\"nodes\":[{\"mesh\":0},{\"mesh\":0}]").Replace("\"nodes\":[0]", "\"nodes\":[0,1]"));
            Assert.True(rig.Models.Reload("res://thing.gltf"));
            Assert.Equal(2, rig.Models.Find(other)!.MeshCount); // the same id, the new content
            Assert.Equal(id, rig.Ecs.Get<ModelRenderer>(entity).Model);

            // A file that no longer loads keeps the model that works.
            File.WriteAllText(Path.Combine(folder, "thing.gltf"), "not a gltf");
            Assert.ThrowsAny<Exception>(() => rig.Models.Reload("res://thing.gltf"));
            Assert.Equal(2, rig.Models.Find(other)!.MeshCount);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void IdsComeBeforeTheGpuAndTheModelFollowsOnceARendererIsAttached()
    {
        string small = FixtureModel;

        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(Path.GetDirectoryName(small)!));
        using var library = new ModelLibrary(files, new ContentLoader(files));
        Assert.False(library.IsAttached);

        // A script runs before there is a renderer: it gets its id, and a path that names no file is an error it can see.
        uint id = library.Load("res://NormalTangentTest.glb");
        Assert.NotEqual(0u, id);
        Assert.Throws<FileNotFoundException>(() => library.Load("res://nothing.glb"));
        Assert.Null(library.Find(id));
        Assert.Throws<InvalidOperationException>(() => library.Add(GoldenScene.Build()));

        using var rig = new Rig(Device);
        library.Attach(rig.Renderer, new TextureCaps(Device.SupportsBc, Device.SupportsAstc));
        GpuModel model = library.Find(id)!;
        Assert.True(model.MeshCount > 0);
        Assert.Same(model, library.Find(id));

        // The device going away frees the models; the ids survive and the models come back.
        library.Detach();
        Assert.Null(library.Find(id));
        library.Attach(rig.Renderer, new TextureCaps(Device.SupportsBc, Device.SupportsAstc));
        Assert.NotNull(library.Find(id));
    }
}
