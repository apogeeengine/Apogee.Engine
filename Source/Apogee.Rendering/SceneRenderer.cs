using Apogee.Core.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Apogee.Content;
using Apogee.Core.Numerics;
using Apogee.Graphics;
using Apogee.Graphics.Shaders;
using Apogee.Rendering.Graph;
using Apogee.Rendering.Shaders;

namespace Apogee.Rendering;

public enum DebugView
{
    None = 0,

    /// <summary>Shades each pixel by how many lights its cluster has: blue none, green a few, red many.</summary>
    ClusterLights = 1,
}

public sealed class RenderSettings
{
    /// <summary>Scales the scene's light before tone mapping.</summary>
    public float Exposure { get; set; } = 1f;

    public DebugView Debug { get; set; }

    /// <summary>What the HDR image is cleared to where nothing is drawn.</summary>
    public Vector3 SkyColor { get; set; } = new(0.35f, 0.5f, 0.75f);

    /// <summary>Lines and triangles to draw over the lit scene, depth-tested against it; null draws none. The renderer reads it and leaves it alone: whoever fills it clears it for the next frame.</summary>
    public Apogee.Core.Graphics.DebugDraw? DebugDraw { get; set; }

    /// <summary>The width of a debug line in pixels.</summary>
    public float DebugLineWidth { get; set; } = 1.5f;

    /// <summary>How visible the part of a debug line is that is behind something: 0 hides it, 1 draws it as strongly as the rest.</summary>
    public float DebugHiddenAlpha { get; set; } = 0.25f;

    /// <summary>Leave out the draws whose bounds are outside the camera's view (on the CPU, with SIMD). Turn it off to compare, in tools and tests.</summary>
    public bool CullDraws { get; set; } = true;
}

/// <summary>Where the final image goes: the window's image, an offscreen one that is read back, or one that is drawn as a texture afterwards (an editor's viewport).</summary>
public readonly struct RenderTarget
{
    private RenderTarget(Backbuffer? backbuffer, GpuImage? image, bool sampled = false, uint sampledIndex = 0)
    {
        Backbuffer = backbuffer;
        Image = image;
        Sampled = sampled;
        SampledIndex = sampledIndex;
    }

    /// <summary>The image is left ready to be sampled (<see cref="ImageState.ShaderRead"/>), not ready to be copied.</summary>
    internal bool Sampled { get; }

    /// <summary>The image's bindless index, for a target that is sampled afterwards.</summary>
    internal uint SampledIndex { get; }

    internal Backbuffer? Backbuffer { get; }

    internal GpuImage? Image { get; }

    public uint Width => Backbuffer?.Width ?? Image!.Width;

    public uint Height => Backbuffer?.Height ?? Image!.Height;

    public PixelFormat Format => Backbuffer?.Format ?? Image!.Format;

    public static RenderTarget Window(Backbuffer backbuffer) => new(backbuffer, null);

    /// <summary>An offscreen image with <see cref="ImageUsage.ColorAttachment"/>; it is left in <see cref="ImageState.TransferSource"/> for readback.</summary>
    public static RenderTarget Offscreen(GpuImage image) => new(null, image);

    /// <summary>
    /// An offscreen image with <see cref="ImageUsage.ColorAttachment"/> and <see cref="ImageUsage.Sampled"/>, drawn as a texture afterwards (the editor shows the world in a panel): it is left in
    /// <see cref="ImageState.ShaderRead"/>, so a later graph of the same frame, such as the ImGui pass, samples it through <paramref name="sampledIndex"/> (its bindless index) with no barrier of its own.
    /// </summary>
    public static RenderTarget Texture(GpuImage image, uint sampledIndex) => new(null, image, sampled: true, sampledIndex);
}

/// <summary>Something drawn over the finished image: the dev overlay. It adds its own pass to the frame's graph.</summary>
public interface IRenderOverlay
{
    /// <summary>Adds the passes that draw onto <paramref name="output"/>, which holds the tone-mapped image by now and is left as it is.</summary>
    void AddPasses(RenderGraph graph, ImageHandle output, Frame frame);
}

public sealed record RenderStats(int Draws, int Lights, int ClustersBuilt, int PassesRun, int Culled = 0);

/// <summary>
/// The Forward+ renderer. Every frame it builds a render graph: a depth prepass, a light-culling compute pass over the 16 x 9 x 24 cluster grid,
/// the PBR forward pass (opaque and alpha-masked, then blended draws far to near), and a tone-mapping pass into the target.
/// </summary>
public sealed unsafe class SceneRenderer : IDisposable
{
    private const int MaxLights = 1024;

    private readonly GraphicsDevice _device;
    private readonly SceneGather _gather = new();
    private readonly List<Draw> _allDraws = [];
    private readonly List<Draw> _opaqueDraws = [];
    private readonly List<Draw> _maskDraws = [];
    private readonly List<Draw> _blendDraws = [];
    private readonly TransientPool _pool;
    private readonly ShaderLibrary _shaders;
    private readonly ReloadablePipelines<ClusterPipelines> _clusters;
    private readonly ReloadablePipelines<DepthPipelines> _depth;
    private readonly ReloadablePipelines<ForwardPipelines> _forward;
    private readonly ReloadablePipelines<TonemapPipelines> _tonemap;
    private readonly ReloadablePipelines<DebugPipelines> _debugPipelines;
    private readonly DebugDrawRenderer _debugDraw;
    private readonly HashSet<PixelFormat> _targetFormats = [];
    private readonly GpuSampler _clampSampler;
    private readonly uint _clampSamplerIndex;
    private readonly GpuBuffer?[] _clusterBounds;
    private readonly GpuBuffer?[] _clusterLights;
    private readonly Vector4[] _clusterKeys;
    private readonly GpuBuffer?[] _instanceBuffers;
    private BoxSet _boxes = new(0);
    private byte[] _visible = [];

    // Each group is the pipelines of one shader module (the clusters of two), so a change to a shader rebuilds only the pipelines that use it. [Indexed by double sided: 0 culls back faces, 1 does not.]
    private sealed record ClusterPipelines(Pipeline Build, Pipeline Cull);

    private sealed record DepthPipelines(Pipeline[] Opaque, Pipeline[] Mask);

    private sealed record ForwardPipelines(Pipeline[] Opaque, Pipeline[] Blend);

    /// <summary>The debug lines (depth-tested, and a faint pass without the test for what is hidden) and triangles.</summary>
    private sealed record DebugPipelines(Pipeline Lines, Pipeline HiddenLines, Pipeline Triangles);

    /// <summary>The tone mapper: one pipeline per target format, made when the format is first rendered to (a rebuild makes them again for the formats seen).</summary>
    private sealed class TonemapPipelines(GraphicsDevice device, PipelineScope scope, ShaderCode fullscreen, ShaderCode fragment)
    {
        private readonly Dictionary<PixelFormat, Pipeline> _pipelines = [];

        public Pipeline For(PixelFormat format)
        {
            if (!_pipelines.TryGetValue(format, out Pipeline? pipeline))
            {
                pipeline = scope.Add(device.CreateGraphicsPipeline(new GraphicsPipelineDescription(fullscreen, fragment, [format], CullMode: CullMode.None), $"Tonemap {format}"));
                _pipelines[format] = pipeline;
            }

            return pipeline;
        }
    }

    public SceneRenderer(GraphicsDevice device, ShaderLibrary? shaders = null, string? cacheDirectory = null, int framesInFlight = 2)
    {
        _device = device;
        _pool = new TransientPool(device, framesInFlight);
        _clusterBounds = new GpuBuffer?[framesInFlight];
        _clusterLights = new GpuBuffer?[framesInFlight];
        _clusterKeys = new Vector4[framesInFlight];
        _instanceBuffers = new GpuBuffer?[framesInFlight];

        _shaders = shaders ??= ShaderLibrary.Standard(cacheDirectory);
        _clusters = new ReloadablePipelines<ClusterPipelines>(shaders, "Cluster pipelines", ["shaders://cluster_build.slang", "shaders://light_cull.slang"], scope =>
        {
            CompiledShader build = shaders.Load("shaders://cluster_build.slang", backend: device.ShaderBackend);
            CompiledShader cull = shaders.Load("shaders://light_cull.slang", backend: device.ShaderBackend);
            return new ClusterPipelines(
                scope.Add(device.CreateComputePipeline(build.Code("buildClusters"), "Build clusters")),
                scope.Add(device.CreateComputePipeline(cull.Code("cullLights"), "Cull lights")));
        });

        // glTF winds front faces counter-clockwise. The projection flips y instead of the viewport, which keeps the image (and so the winding) as it is.
        _depth = new ReloadablePipelines<DepthPipelines>(shaders, "Depth pipelines", ["shaders://depth.slang"], scope =>
        {
            CompiledShader depth = shaders.Load("shaders://depth.slang", backend: device.ShaderBackend);
            var vertex = depth.Code("depthVs");
            var fragment = depth.Code("depthMaskFs");
            var opaque = new Pipeline[2];
            var mask = new Pipeline[2];
            for (int doubleSided = 0; doubleSided < 2; doubleSided++)
            {
                CullMode cull = doubleSided == 1 ? CullMode.None : CullMode.Back;
                string suffix = doubleSided == 1 ? " (double sided)" : "";
                opaque[doubleSided] = scope.Add(device.CreateGraphicsPipeline(
                    new GraphicsPipelineDescription(vertex, null, [], PixelFormat.D32Sfloat, CullMode: cull, DepthTest: true, DepthWrite: true), "Depth" + suffix));
                mask[doubleSided] = scope.Add(device.CreateGraphicsPipeline(
                    new GraphicsPipelineDescription(vertex, fragment, [], PixelFormat.D32Sfloat, CullMode: cull, DepthTest: true, DepthWrite: true), "Depth mask" + suffix));
            }

            return new DepthPipelines(opaque, mask);
        });

        _forward = new ReloadablePipelines<ForwardPipelines>(shaders, "Forward pipelines", ["shaders://forward.slang"], scope =>
        {
            CompiledShader forward = shaders.Load("shaders://forward.slang", backend: device.ShaderBackend);
            var vertex = forward.Code("forwardVs");
            var fragment = forward.Code("forwardFs");
            var opaque = new Pipeline[2];
            var blend = new Pipeline[2];
            for (int doubleSided = 0; doubleSided < 2; doubleSided++)
            {
                CullMode cull = doubleSided == 1 ? CullMode.None : CullMode.Back;
                string suffix = doubleSided == 1 ? " (double sided)" : "";
                opaque[doubleSided] = scope.Add(device.CreateGraphicsPipeline(
                    new GraphicsPipelineDescription(vertex, fragment, [PixelFormat.R16G16B16A16Sfloat], PixelFormat.D32Sfloat, CullMode: cull, DepthTest: true), "Forward" + suffix));
                blend[doubleSided] = scope.Add(device.CreateGraphicsPipeline(
                    new GraphicsPipelineDescription(vertex, fragment, [PixelFormat.R16G16B16A16Sfloat], PixelFormat.D32Sfloat, CullMode: cull, DepthTest: true, Blend: BlendMode.Alpha), "Forward blend" + suffix));
            }

            return new ForwardPipelines(opaque, blend);
        });

        _tonemap = new ReloadablePipelines<TonemapPipelines>(shaders, "Tonemap pipelines", ["shaders://tonemap.slang"], scope =>
        {
            CompiledShader tonemap = shaders.Load("shaders://tonemap.slang", backend: device.ShaderBackend);
            var group = new TonemapPipelines(device, scope, tonemap.Code("fullscreenVs"), tonemap.Code("tonemapFs"));
            PixelFormat[] seen;
            lock (_targetFormats)
            {
                seen = [.. _targetFormats];
            }

            foreach (PixelFormat format in seen)
            {
                group.For(format); // made now, off the render thread, for the formats it has drawn to
            }

            return group;
        });

        _debugDraw = new DebugDrawRenderer(device, framesInFlight);
        _debugPipelines = new ReloadablePipelines<DebugPipelines>(shaders, "Debug draw pipelines", ["shaders://debug_draw.slang"], scope =>
        {
            CompiledShader debug = shaders.Load("shaders://debug_draw.slang", backend: device.ShaderBackend);
            var fragment = debug.Code("debugFs");
            PixelFormat[] color = [PixelFormat.R16G16B16A16Sfloat];
            Pipeline Make(string entry, string name, bool depthTest) => scope.Add(device.CreateGraphicsPipeline(
                new GraphicsPipelineDescription(debug.Code(entry), fragment, color, PixelFormat.D32Sfloat, CullMode: CullMode.None, DepthTest: depthTest, Blend: BlendMode.Alpha), name));
            return new DebugPipelines(Make("lineVs", "Debug lines", true), Make("lineVs", "Debug lines (hidden)", false), Make("triangleVs", "Debug triangles", true));
        });

        _clampSampler = device.CreateSampler(SamplerDescription.LinearClamp, "HDR sampler");
        _clampSamplerIndex = device.Bindless.AddSampler(_clampSampler);
    }

    /// <summary>The library the shaders come from: its <see cref="ShaderLibrary.Errors"/> are what the dev overlay shows when a shader edit does not compile.</summary>
    public ShaderLibrary Shaders => _shaders;

    /// <summary>How many times each group of pipelines has been rebuilt after a shader change.</summary>
    internal (int Clusters, int Depth, int Forward, int Tonemap) ShaderRebuilds => (_clusters.Rebuilds, _depth.Rebuilds, _forward.Rebuilds, _tonemap.Rebuilds);

    /// <summary>Waits for the rebuilds in progress (tests); the next <see cref="Render"/> then uses them.</summary>
    internal void WaitForShaderRebuilds()
    {
        _clusters.WaitForPending();
        _depth.WaitForPending();
        _forward.WaitForPending();
        _tonemap.WaitForPending();
        _debugPipelines.WaitForPending();
    }

    /// <summary>The graph of the most recent <see cref="Render"/>, for the dev overlay.</summary>
    public GraphPlan? LastPlan { get; private set; }

    public RenderStats? LastStats { get; private set; }

    /// <summary>The cluster light lists the GPU wrote for a frame slot: per cluster the light count, then the light indices. For tests and the dev overlay's statistics.</summary>
    internal uint[] ReadClusterLights(int slot)
    {
        GpuBuffer lists = _clusterLights[slot] ?? throw new InvalidOperationException("That frame slot has not rendered yet.");
        byte[] bytes = _device.ReadBuffer(lists, 0, (int)lists.Size);
        uint[] result = new uint[bytes.Length / sizeof(uint)];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    /// <summary>Puts a model on the GPU, with its origin at <paramref name="at"/> in the world (the world's origin by default). The scene owns the model.</summary>
    public RenderScene CreateScene(ModelData model, Double3 at = default) => new(new GpuModel(_device, model), at);

    /// <summary>Puts a model on the GPU to place it many times (see <see cref="ModelLibrary"/>, which does it for models by path).</summary>
    public GpuModel CreateModel(ModelData model) => new(_device, model);

    /// <summary>Records a frame of <paramref name="scene"/> seen by <paramref name="camera"/> into <paramref name="frame"/>, ending with the image in <paramref name="target"/>.</summary>
    public void Render(Frame frame, RenderScene scene, Camera camera, in RenderTarget target, RenderSettings? settings = null, IRenderOverlay? overlay = null) =>
        RenderSource(frame, scene, camera, target, settings, overlay);

    /// <summary>Records a frame of the entities of <paramref name="world"/> (their models and lights) seen by <paramref name="camera"/>.</summary>
    public void Render(Frame frame, RenderWorld world, Camera camera, in RenderTarget target, RenderSettings? settings = null, IRenderOverlay? overlay = null) =>
        RenderSource(frame, world, camera, target, settings, overlay);

    /// <summary>The instance buffer of a frame slot, big enough for <paramref name="count"/> instances. A buffer that is too small goes to <paramref name="frame"/> to be freed when the GPU is done with it.</summary>
    private GpuBuffer InstanceBuffer(Frame frame, int slot, int count)
    {
        ulong needed = (ulong)Math.Max(1, count) * (ulong)sizeof(GpuInstance);
        GpuBuffer? buffer = _instanceBuffers[slot];
        if (buffer is null || buffer.Size < needed)
        {
            if (buffer is not null)
            {
                frame.Defer(buffer);
            }

            ulong grown = buffer is null ? 0 : buffer.Size + (buffer.Size / 2);
            buffer = _instanceBuffers[slot] = _device.CreateBuffer(Math.Max(needed, Math.Max(grown, 256UL * (ulong)sizeof(GpuInstance))), BufferUsage.Storage, MemoryKind.Upload, $"Instances {slot}");
        }

        return buffer;
    }

    private void RenderSource(Frame frame, IRenderSource source, Camera camera, in RenderTarget target, RenderSettings? settings, IRenderOverlay? overlay)
    {
        settings ??= new RenderSettings();

        // Pipelines rebuilt since the last frame (a shader changed) replace the old ones here, never in the middle of a frame.
        _clusters.Apply(frame);
        _depth.Apply(frame);
        _forward.Apply(frame);
        _tonemap.Apply(frame);
        _debugPipelines.Apply(frame);

        uint width = target.Width;
        uint height = target.Height;
        PixelFormat targetFormat = target.Format;
        int slot = frame.SlotIndex;

        // The renderer works relative to the camera: the view has no translation, and the instances and lights below are offsets from it.
        Matrix4x4 view = camera.View;
        Matrix4x4 projection = camera.Projection(width / (float)height);
        float near = camera.Near;
        float far = camera.ClusterFar;

        // Per-frame-slot cluster buffers: a slot's bounds are rebuilt when the projection changes, and the GPU is done with the slot by now.
        GpuBuffer bounds = _clusterBounds[slot] ??= _device.CreateBuffer((ulong)(ClusterGrid.Total * 2 * sizeof(Vector4)), BufferUsage.Storage, MemoryKind.DeviceLocal, $"Cluster bounds {slot}");
        GpuBuffer lists = _clusterLights[slot] ??= _device.CreateBuffer((ulong)(ClusterGrid.Total * ClusterGrid.Stride * sizeof(uint)), BufferUsage.Storage, MemoryKind.DeviceLocal, $"Cluster lights {slot}");
        var key = new Vector4(projection.M11, projection.M22, near, far);
        bool rebuild = _clusterKeys[slot] != key;
        _clusterKeys[slot] = key;

        _gather.Clear();
        source.Gather(_gather, camera.Position);
        List<Placement> placements = _gather.Placements;

        // One instance per mesh instance of every placement, written with its translation relative to the camera (the difference of the double positions is taken first, so a model
        // 10 000 km from the world's origin is as precise as one at it). Their boxes, in the same camera-relative space, are what culling tests.
        int instanceCount = 0;
        foreach (Placement placement in placements)
        {
            instanceCount += placement.Model.Nodes.Length;
        }

        GpuBuffer instances = InstanceBuffer(frame, slot, instanceCount);
        if (_boxes is null || _boxes.Count < instanceCount)
        {
            _boxes = new BoxSet((int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(instanceCount, 64)));
            _visible = new byte[_boxes.ResultLength];
        }

        var destination = (GpuInstance*)instances.Mapped;
        List<Draw> all = _allDraws;
        all.Clear();
        foreach (Placement placement in placements)
        {
            Double3 fromCamera = placement.Position - camera.Position;
            bool upright = placement.IsUpright;
            Matrix4x4 orientation = default;
            Matrix4x4 normalOrientation = default;
            if (!upright)
            {
                // The model's own matrices come first, then the placement's scale and rotation: x' = x * node * (S * R). Normals take transpose(inverse(S * R)) = S^-1 * R.
                orientation = Matrix4x4.CreateScale(placement.Scale) * Matrix4x4.CreateFromQuaternion(placement.Rotation);
                Vector3 s = placement.Scale;
                normalOrientation = Matrix4x4.CreateScale(new Vector3(SafeInverse(s.X), SafeInverse(s.Y), SafeInverse(s.Z))) * Matrix4x4.CreateFromQuaternion(placement.Rotation);
            }

            foreach (ModelNode node in placement.Model.Nodes)
            {
                int index = all.Count;
                GpuInstance instance = default;
                Vector3 offset = node.Offset;
                Vector3 boxCenter = node.BoxCenter;
                Vector3 boxExtents = node.BoxExtents;
                if (upright)
                {
                    instance.World = node.World;
                    instance.NormalMatrix = node.Normal;
                }
                else
                {
                    instance.World = node.World * orientation;
                    instance.NormalMatrix = node.Normal * normalOrientation;
                    offset = Vector3.TransformNormal(offset, orientation);
                    boxCenter = Vector3.TransformNormal(boxCenter, orientation);
                    boxExtents = RenderScene.WorldExtents(boxExtents, orientation);
                }

                instance.World.Translation = (Vector3)(fromCamera + offset);
                instance.VertexOffset = node.VertexOffset;
                instance.Material = node.Material;
                destination[index] = instance;

                Vector3 center = (Vector3)fromCamera + boxCenter;
                _boxes.Set(index, center, boxExtents);
                all.Add(new Draw((uint)index, node.FirstIndex, node.IndexCount, node.Class, node.DoubleSided, center, placement.Model));
            }
        }

        // Lights: uploaded every frame so they can move.
        int lightCount = Math.Min(_gather.Lights.Count, MaxLights);
        FrameAllocation lightMemory = frame.Allocate(Math.Max(1, lightCount) * sizeof(GpuLight), 16);
        var gpuLights = (GpuLight*)lightMemory.Memory;
        for (int i = 0; i < lightCount; i++)
        {
            gpuLights[i] = _gather.Lights[i];
        }

        FrameAllocation dataMemory = frame.Allocate(sizeof(GpuFrameData), 16);
        *(GpuFrameData*)dataMemory.Memory = new GpuFrameData
        {
            View = view,
            Proj = projection,
            ViewProj = view * projection,
            CameraPosition = Vector4.Zero, // everything is camera-relative
            Screen = new Vector4(width, height, 1f / width, 1f / height),
            ClusterParams = new Vector4(near, far, 1f / MathF.Log(far / near), 0f),
            Counts = new UInt4((uint)lightCount, (uint)settings.Debug, 0, 0),
            SunDirection = new Vector4(Vector3.Normalize(_gather.SunDirection), 0f),
            SunColor = new Vector4(_gather.SunColor, 0f),
            AmbientSky = new Vector4(_gather.AmbientSky, 0f),
            AmbientGround = new Vector4(_gather.AmbientGround, 0f),
            ProjScale = new Vector4(projection.M11, projection.M22, 0f, 0f),
            Instances = instances.Address,
            Lights = lightMemory.Address,
            ClusterBounds = bounds.Address,
            ClusterLights = lists.Address,
        };
        ulong frameAddress = dataMemory.Address;

        // The draws inside the view, by pass. The lists are kept between frames: Render records a frame to its end before the next one starts.
        List<Draw> opaque = _opaqueDraws;
        List<Draw> mask = _maskDraws;
        List<Draw> blend = _blendDraws;
        opaque.Clear();
        mask.Clear();
        blend.Clear();
        int culled = 0;
        byte[] visible = [];
        if (settings.CullDraws && all.Count > 0)
        {
            // The camera's basis is in the rows of its view matrix; the boxes are camera-relative already, so the frustum needs no offset.
            var basisRight = new Vector3(view.M11, view.M21, view.M31);
            var basisUp = new Vector3(view.M12, view.M22, view.M32);
            var basisForward = new Vector3(-view.M13, -view.M23, -view.M33);
            Frustum frustum = Frustum.FromCamera(basisRight, basisUp, basisForward, MathF.Tan(camera.VerticalFov * 0.5f), width / (float)height, near);
            FrustumCulling.Cull(frustum, _boxes, _visible);
            visible = _visible;
        }

        for (int i = 0; i < all.Count; i++)
        {
            if (visible.Length > 0 && !FrustumCulling.IsVisible(visible, i))
            {
                culled++;
                continue;
            }

            Draw draw = all[i];
            (draw.Class switch { DrawClass.Mask => mask, DrawClass.Blend => blend, _ => opaque }).Add(draw);
        }

        // Blended surfaces are drawn far to near; the centers are offsets from the camera.
        blend.Sort((a, b) => b.Center.LengthSquared().CompareTo(a.Center.LengthSquared()));

        var graph = new RenderGraph(_device, _pool);
        ImageHandle output = target.Backbuffer is { } back
            ? graph.ImportBackbuffer(back)
            : graph.ImportImage("Output", target.Image!, ImageState.Undefined, target.Sampled ? ImageState.ShaderRead : ImageState.TransferSource, output: true, sampledIndex: target.SampledIndex);
        ImageHandle depth = graph.CreateImage("Depth", new ImageDescription(PixelFormat.D32Sfloat, width, height, ImageUsage.DepthStencilAttachment | ImageUsage.Sampled));
        ImageHandle hdr = graph.CreateImage("HDR color", new ImageDescription(PixelFormat.R16G16B16A16Sfloat, width, height, ImageUsage.ColorAttachment | ImageUsage.Sampled));
        BufferHandle clusterBounds = graph.ImportBuffer("Cluster bounds", bounds, BufferState.ShaderRead, BufferState.ShaderRead);
        BufferHandle clusterLists = graph.ImportBuffer("Cluster lights", lists, BufferState.ShaderRead, BufferState.ShaderRead);
        int draws = 0;

        graph.AddPass("Depth prepass", PassType.Graphics, pass =>
        {
            pass.WriteDepth(depth);
            pass.Execute(context =>
            {
                CommandRecorder recorder = context.Recorder;
                draws += DrawAll(recorder, _depth.Current.Opaque, opaque, frameAddress);
                draws += DrawAll(recorder, _depth.Current.Mask, mask, frameAddress);
            });
        });

        if (rebuild)
        {
            graph.AddPass("Build clusters", PassType.Compute, pass =>
            {
                pass.Write(clusterBounds);
                pass.Execute(context =>
                {
                    context.Recorder.BindPipeline(_clusters.Current.Build);
                    context.Recorder.PushConstants(new GpuFramePush { Frame = frameAddress });
                    context.Recorder.Dispatch((ClusterGrid.Total + 63) / 64);
                });
            });
        }

        graph.AddPass("Cull lights", PassType.Compute, pass =>
        {
            pass.Read(clusterBounds);
            pass.Write(clusterLists);
            pass.Execute(context =>
            {
                context.Recorder.BindPipeline(_clusters.Current.Cull);
                context.Recorder.PushConstants(new GpuFramePush { Frame = frameAddress });
                context.Recorder.Dispatch((ClusterGrid.Total + 63) / 64);
            });
        });

        graph.AddPass("Forward", PassType.Graphics, pass =>
        {
            pass.ReadDepth(depth);
            pass.WriteColor(hdr, new Vector4(settings.SkyColor, 1f));
            pass.Read(clusterLists);
            pass.Execute(context =>
            {
                CommandRecorder recorder = context.Recorder;
                draws += DrawAll(recorder, _forward.Current.Opaque, opaque, frameAddress);
                draws += DrawAll(recorder, _forward.Current.Opaque, mask, frameAddress);
                draws += DrawAll(recorder, _forward.Current.Blend, blend, frameAddress);
            });
        });

        if (settings.DebugDraw is { IsEmpty: false } debug)
        {
            (ulong lineAddress, int lineCount, ulong triangleAddress, int triangleCount) = _debugDraw.Upload(frame, debug, camera.Position);
            graph.AddPass("Debug draw", PassType.Graphics, pass =>
            {
                pass.ReadDepth(depth);
                pass.WriteColor(hdr); // over the lit scene
                pass.Execute(context =>
                {
                    CommandRecorder recorder = context.Recorder;
                    DebugPipelines pipelines = _debugPipelines.Current;
                    if (triangleCount > 0)
                    {
                        recorder.BindPipeline(pipelines.Triangles);
                        recorder.PushConstants(new GpuDebugPush { Frame = frameAddress, Data = triangleAddress, Thickness = settings.DebugLineWidth, Alpha = 1f });
                        recorder.Draw((uint)triangleCount * 3);
                    }

                    if (lineCount > 0)
                    {
                        if (settings.DebugHiddenAlpha > 0f)
                        {
                            recorder.BindPipeline(pipelines.HiddenLines);
                            recorder.PushConstants(new GpuDebugPush { Frame = frameAddress, Data = lineAddress, Thickness = settings.DebugLineWidth, Alpha = settings.DebugHiddenAlpha });
                            recorder.Draw((uint)lineCount * 6);
                        }

                        recorder.BindPipeline(pipelines.Lines);
                        recorder.PushConstants(new GpuDebugPush { Frame = frameAddress, Data = lineAddress, Thickness = settings.DebugLineWidth, Alpha = 1f });
                        recorder.Draw((uint)lineCount * 6);
                    }
                });
            });
        }

        graph.AddPass("Tonemap", PassType.Graphics, pass =>
        {
            pass.ReadSampled(hdr);
            pass.WriteColor(output, Vector4.Zero);
            pass.Execute(context =>
            {
                context.Recorder.BindPipeline(TonemapPipeline(targetFormat));
                context.Recorder.PushConstants(new GpuTonemapPush { Texture = context.Image(hdr).SampledIndex, Sampler = _clampSamplerIndex, Exposure = settings.Exposure });
                context.Recorder.Draw(3);
            });
        });

        overlay?.AddPasses(graph, output, frame);
        graph.Execute(frame);
        LastPlan = graph.Plan;
        LastStats = new RenderStats(draws, lightCount, rebuild ? ClusterGrid.Total : 0, graph.Plan!.LivePassCount, culled);
    }

    /// <summary>1 / <paramref name="scale"/>, kept finite for a scale of zero (a flattened object has no meaningful normal anyway).</summary>
    private static float SafeInverse(float scale) => 1f / (MathF.Abs(scale) < 1e-6f ? (scale < 0f ? -1e-6f : 1e-6f) : scale);

    private static int DrawAll(CommandRecorder recorder, Pipeline[] pipelinesByCull, List<Draw> draws, ulong frameAddress)
    {
        int bound = -1;
        GpuModel? model = null;
        foreach (Draw draw in draws)
        {
            int cull = draw.DoubleSided ? 1 : 0;
            if (cull != bound)
            {
                recorder.BindPipeline(pipelinesByCull[cull]);
                bound = cull;
            }

            if (!ReferenceEquals(draw.Model, model))
            {
                model = draw.Model;
                recorder.BindIndexBuffer(model.Indices);
            }

            recorder.PushConstants(new GpuDrawPush { Frame = frameAddress, Vertices = model!.Vertices.Address, Materials = model.Materials.Address, Instance = draw.Instance });
            recorder.DrawIndexed(draw.IndexCount, 1, draw.FirstIndex);
        }

        return draws.Count;
    }

    private Pipeline TonemapPipeline(PixelFormat format)
    {
        lock (_targetFormats)
        {
            _targetFormats.Add(format);
        }

        return _tonemap.Current.For(format);
    }

    /// <summary>How far a light reaches: its glTF range, or where it falls below a faint threshold when it has none.</summary>
    internal static float EffectiveRadius(LightData light) => EffectiveRadius(light.Color, light.Intensity, light.Range);

    internal static float EffectiveRadius(Vector3 color, float intensity, float range)
    {
        if (range > 0f)
        {
            return range;
        }

        Vector3 emitted = color * intensity;
        return Math.Clamp(MathF.Sqrt(MathF.Max(emitted.X, MathF.Max(emitted.Y, emitted.Z)) / 0.05f), 0.05f, 500f);
    }

    /// <summary>A light for the GPU: <paramref name="offset"/> moves it from the scene's coordinates to ones relative to the camera.</summary>
    internal static GpuLight ToGpu(LightData light, Vector3 offset = default) =>
        ToGpu(light.Kind, light.Position + offset, light.Direction, light.Color, light.Intensity, light.Range, light.InnerConeAngle, light.OuterConeAngle);

    /// <summary>A point or spot light for the GPU, at a position that is already relative to the camera.</summary>
    internal static GpuLight ToGpu(LightKind kind, Vector3 position, Vector3 direction, Vector3 color, float intensity, float range, float innerCone, float outerCone)
    {
        float cosOuter = MathF.Cos(outerCone);
        float cosInner = MathF.Cos(innerCone);
        float scale = 1f / MathF.Max(cosInner - cosOuter, 1e-3f);
        bool spot = kind == LightKind.Spot;
        return new GpuLight
        {
            PositionRadius = new Vector4(position, EffectiveRadius(color, intensity, range)),
            ColorType = new Vector4(color * intensity, spot ? 1f : 0f),
            DirectionCone = new Vector4(direction, cosOuter),
            Spot = new Vector4(scale, -cosOuter * scale, 0f, 0f),
        };
    }

    public void Dispose()
    {
        _device.WaitIdle();
        _pool.Dispose();
        _clusters.Dispose();
        _depth.Dispose();
        _forward.Dispose();
        _tonemap.Dispose();
        _debugPipelines.Dispose();
        _debugDraw.Dispose();

        _device.Bindless.RemoveSampler(_clampSamplerIndex);
        _clampSampler.Dispose();
        foreach (GpuBuffer? buffer in _clusterBounds.Concat(_clusterLights).Concat(_instanceBuffers))
        {
            buffer?.Dispose();
        }
    }
}
