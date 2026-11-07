using Apogee.Core.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Apogee.Graphics;
using Apogee.Graphics.Shaders;
using Apogee.Native.RmlUi;
using Apogee.Rendering;
using Apogee.Rendering.Graph;
using Apogee.Rendering.Shaders;

namespace Apogee.UI;

/// <summary>
/// Draws what RmlUi hands the host onto the frame's output, as a pass of the render graph: use it as (part of) the <see cref="IRenderOverlay"/> of <c>SceneRenderer.Render</c>.
/// RmlUi compiles geometry once and renders it many times; the renderer keeps the compiled vertices and indices in memory and, when a frame is built, packs the geometry that frame draws into
/// per-frame upload buffers (as the ImGui backend does), so nothing on the GPU depends on when RmlUi releases or re-renders a piece of geometry. Textures are images in the bindless heap, and the
/// handle RmlUi holds for one is its bindless index (0 is the white texel that untextured geometry samples).
/// </summary>
public sealed unsafe class UiRenderer : IRenderOverlay, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UiPush
    {
        public ulong Vertices;
        public uint Texture;
        public uint Sampler;
        public Vector4 Transform0;
        public Vector4 Transform1;
        public Vector4 Transform2;
        public Vector4 Transform3;
        public Vector2 Translation;
        public Vector2 Viewport;
        public uint VertexOffset;
        public uint HasTransform;
        public uint ClipTexture;
        public uint ShaderKind;
        public ulong Gradient;
    }

    /// <summary>The push constants of the full-screen passes (fxFs): 120 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FxPush
    {
        public Vector4 M0;
        public Vector4 M1;
        public Vector4 M2;
        public Vector4 M3;
        public Vector4 M4;
        public uint Source;
        public uint Source2;
        public uint Sampler;
        public uint Clip;
        public uint Mode;
        public uint Unused;
        public Vector2 Viewport;
        public Vector2 Shift;
    }

    /// <summary>A gradient as the shader reads it: 16 stops (premultiplied colors and positions), the shape, and what kind it is. 336 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GradientData
    {
        public const int MaxStops = 16;

        public fixed float Colors[MaxStops * 4];
        public fixed float Positions[MaxStops];
        public Vector4 A;
        public uint Kind;
        public uint Repeating;
        public uint Count;
        public uint Unused;
    }

    private sealed class Geometry(ArVertex[] vertices, int[] indices)
    {
        public ArVertex[] Vertices { get; } = vertices;

        public int[] Indices { get; } = indices;
    }

    /// <summary>What was set when RmlUi drew: the scissor, the transform, the clip mask (the index of the mask operation that made it, or -1) and the layer drawn to (0 is the frame's output).</summary>
    private readonly record struct State(bool Scissor, int ScissorX, int ScissorY, int ScissorWidth, int ScissorHeight, bool HasTransform, Matrix4x4 Transform, int Clip, int Layer);

    /// <summary>One thing RmlUi asked the host to do this frame, in order.</summary>
    private abstract record Op(State State);

    private sealed record DrawOp(State State, Geometry Geometry, float X, float Y, uint Texture, UiShader? Shader) : Op(State);

    private sealed record MaskOp(State State, int Index, int Previous, int Operation, Geometry Geometry, float X, float Y) : Op(State);

    private sealed record CompositeOp(State State, int Source, int Destination, bool Replace, UiFilter[] Filters) : Op(State);

    private sealed record SaveOp(State State, int Layer, int X, int Y, TextureEntry Target) : Op(State);

    private readonly record struct Packed(uint VertexBase, uint IndexBase, uint IndexCount);


    private sealed record TextureEntry(GpuImage Image, uint BindlessIndex);

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>The pipelines, one set per target format: made when the format is first drawn to, and again, off the render thread, for the formats seen when the shader changes.</summary>
    private sealed class FormatPipelines(GraphicsDevice device, PipelineScope scope, CompiledShader shader)
    {
        private readonly Dictionary<PixelFormat, Pipeline> _ui = [];
        private readonly Dictionary<PixelFormat, Pipeline> _mask = [];
        private readonly Dictionary<PixelFormat, Pipeline> _over = [];
        private readonly Dictionary<PixelFormat, Pipeline> _replace = [];

        private Pipeline Make(Dictionary<PixelFormat, Pipeline> cache, PixelFormat format, string vertex, string fragment, BlendMode blend, string name)
        {
            if (!cache.TryGetValue(format, out Pipeline? pipeline))
            {
                pipeline = scope.Add(device.CreateGraphicsPipeline(new GraphicsPipelineDescription(shader.Code(vertex), shader.Code(fragment), [format], CullMode: CullMode.None, Blend: blend), $"{name} {format}"));
                cache[format] = pipeline;
            }

            return pipeline;
        }

        /// <summary>RmlUi's geometry, blended over what is there.</summary>
        public Pipeline Geometry(PixelFormat format) => Make(_ui, format, "uiVs", "uiFs", BlendMode.Premultiplied, "UI");

        /// <summary>The shape of a clip mask operation, replacing what is there.</summary>
        public Pipeline Mask(PixelFormat format) => Make(_mask, format, "uiVs", "maskFs", BlendMode.Opaque, "UI mask");

        /// <summary>A full-screen pass blended over its target.</summary>
        public Pipeline Over(PixelFormat format) => Make(_over, format, "fxVs", "fxFs", BlendMode.Premultiplied, "UI fx over");

        /// <summary>A full-screen pass that replaces its target.</summary>
        public Pipeline Replace(PixelFormat format) => Make(_replace, format, "fxVs", "fxFs", BlendMode.Opaque, "UI fx");

        public void ForFormat(PixelFormat format)
        {
            _ = Geometry(format);
            _ = Mask(format);
            _ = Over(format);
            _ = Replace(format);
        }
    }

    private const PixelFormat LayerFormat = PixelFormat.R8G8B8A8Unorm;

    private readonly GraphicsDevice _device;
    private readonly UiSystem _ui;
    private readonly ShaderLibrary _shaders;
    private readonly ReloadablePipelines<FormatPipelines> _pipelines;
    private readonly HashSet<PixelFormat> _targetFormats = [];
    private readonly Dictionary<ulong, Geometry> _geometries = [];
    private readonly Dictionary<uint, TextureEntry> _textures = [];
    private readonly List<TextureEntry> _released = [];
    private readonly List<Op> _ops = [];
    private readonly Dictionary<Geometry, Packed> _packed = [];
    private readonly List<int> _stack = [];
    private readonly GpuSampler _sampler;
    private readonly uint _samplerIndex;
    private readonly GpuBuffer?[] _vertexBuffers;
    private readonly GpuBuffer?[] _indexBuffers;
    private readonly GpuBuffer?[] _gradientBuffers;
    private ulong _nextGeometry = 1;
    private bool _scissor;
    private (int X, int Y, int Width, int Height) _scissorRegion;
    private bool _hasTransform;
    private Matrix4x4 _transform;
    private bool _clipEnabled;
    private int _clipVersion = -1;
    private int _maskCount;
    private int _nextLayer = 1;

    public UiRenderer(GraphicsDevice device, UiSystem ui, ShaderLibrary? shaders = null, string? cacheDirectory = null, int framesInFlight = 2)
    {
        _device = device;
        _ui = ui;
        _vertexBuffers = new GpuBuffer?[framesInFlight];
        _indexBuffers = new GpuBuffer?[framesInFlight];
        _gradientBuffers = new GpuBuffer?[framesInFlight];

        _shaders = shaders ??= ShaderLibrary.Standard(cacheDirectory);
        _pipelines = new ReloadablePipelines<FormatPipelines>(shaders, "UI pipelines", ["shaders://ui.slang"], scope =>
        {
            CompiledShader shader = shaders.Load("shaders://ui.slang", backend: device.ShaderBackend);
            var group = new FormatPipelines(device, scope, shader);
            PixelFormat[] seen;
            lock (_targetFormats)
            {
                seen = [.. _targetFormats];
            }

            foreach (PixelFormat format in seen)
            {
                group.ForFormat(format);
            }

            return group;
        });

        _sampler = device.CreateSampler(SamplerDescription.LinearClamp, "UI sampler");
        _samplerIndex = device.Bindless.AddSampler(_sampler);
        ui.Attach(this);
    }

    /// <summary>The library the shader comes from (its errors are what the dev overlay shows when an edit does not compile).</summary>
    public ShaderLibrary Shaders => _shaders;

    /// <summary>The textures on the GPU: images of documents and glyph atlases.</summary>
    public int TextureCount => _textures.Count;

    /// <summary>The geometry RmlUi has compiled and not released.</summary>
    public int GeometryCount => _geometries.Count;

    /// <summary>What the last frame drew: calls of RenderGeometry, vertices and triangles packed into the buffers.</summary>
    public (int Draws, int Vertices, int Triangles) LastFrame { get; private set; }

    /// <summary>What the last frame's effects were: clip mask operations, layers that were pushed, and composites (a filter chain counts once).</summary>
    public (int Masks, int Layers, int Composites) LastEffects { get; private set; }

    internal int ShaderRebuilds => _pipelines.Rebuilds;

    internal void WaitForShaderRebuilds() => _pipelines.WaitForPending();

    // ---- What RmlUi asks of the host (UiSystem forwards it here when a renderer is attached) ----

    internal ulong CompileGeometry(ReadOnlySpan<ArVertex> vertices, ReadOnlySpan<int> indices)
    {
        ulong handle = _nextGeometry++;
        _geometries[handle] = new Geometry(vertices.ToArray(), indices.ToArray());
        return handle;
    }

    internal void ReleaseGeometry(ulong handle) => _geometries.Remove(handle);

    private int TopLayer => _stack.Count == 0 ? 0 : _stack[^1];

    private State Current() => new(_scissor, _scissorRegion.X, _scissorRegion.Y, _scissorRegion.Width, _scissorRegion.Height, _hasTransform, _transform, _clipEnabled ? _clipVersion : -1, TopLayer);

    internal void RenderGeometry(ulong geometry, float x, float y, ulong texture)
    {
        if (_geometries.TryGetValue(geometry, out Geometry? found))
        {
            _ops.Add(new DrawOp(Current(), found, x, y, (uint)texture, null));
        }
    }

    internal void RenderShader(ulong shader, ulong geometry, float x, float y, ulong texture)
    {
        if (_geometries.TryGetValue(geometry, out Geometry? found) && _ui.ShaderOf(shader) is { } gradient)
        {
            _ops.Add(new DrawOp(Current(), found, x, y, (uint)texture, gradient));
        }
    }

    internal void EnableScissor(bool enable) => _scissor = enable;

    internal void SetScissor(int x, int y, int width, int height) => _scissorRegion = (x, y, width, height);

    internal void SetTransform(float* matrix)
    {
        _hasTransform = matrix != null;
        _transform = matrix == null
            ? Matrix4x4.Identity
            : new Matrix4x4(matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5], matrix[6], matrix[7], matrix[8], matrix[9], matrix[10], matrix[11], matrix[12], matrix[13], matrix[14], matrix[15]);
    }

    // ---- Clip masks, layers, filters ----

    internal void EnableClipMask(bool enable) => _clipEnabled = enable;

    /// <summary>
    /// Changes the clip mask: a texture the size of the context whose red channel is the coverage, that geometry drawn while it is enabled is multiplied by. Every operation makes a new one (an
    /// intersection reads the one before), which takes the place of the stencil buffer RmlUi's own backends use.
    /// </summary>
    internal void RenderToClipMask(int operation, ulong geometry, float x, float y)
    {
        if (!_geometries.TryGetValue(geometry, out Geometry? found))
        {
            return;
        }

        int previous = _clipVersion;
        if (operation == (int)ArClipOperation.Intersect && previous < 0)
        {
            operation = (int)ArClipOperation.Set; // nothing to intersect with
        }

        int index = _maskCount++;
        _ops.Add(new MaskOp(Current() with { Scissor = false }, index, previous, operation, found, x, y));
        _clipVersion = index;
    }

    internal ulong PushLayer()
    {
        int id = _nextLayer++;
        _stack.Add(id);
        return (ulong)id;
    }

    internal void PopLayer()
    {
        if (_stack.Count > 0)
        {
            _stack.RemoveAt(_stack.Count - 1);
        }
    }

    internal void CompositeLayers(ulong source, ulong destination, bool replace, ReadOnlySpan<ulong> filters)
    {
        var resolved = new List<UiFilter>(filters.Length);
        foreach (ulong handle in filters)
        {
            if (_ui.FilterOf(handle) is { } filter)
            {
                resolved.Add(filter);
            }
        }

        _ops.Add(new CompositeOp(Current(), (int)source, (int)destination, replace, [.. resolved]));
    }

    /// <summary>A copy of the scissor region of the top layer, as a texture that lives until RmlUi releases it (a box shadow keeps one).</summary>
    internal ulong SaveLayerAsTexture()
    {
        int width = _ui.Width;
        int height = _ui.Height;
        (int x, int y, int w, int h) = _scissor ? (_scissorRegion.X, _scissorRegion.Y, _scissorRegion.Width, _scissorRegion.Height) : (0, 0, width, height);
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        w = Math.Clamp(w, 1, width - x);
        h = Math.Clamp(h, 1, height - y);
        GpuImage image = _device.CreateImage(new ImageDescription(LayerFormat, (uint)w, (uint)h, ImageUsage.ColorAttachment | ImageUsage.Sampled), "UI saved layer");
        uint index = _device.Bindless.AddSampledImage(image);
        var entry = new TextureEntry(image, index);
        _textures[index] = entry;
        _ops.Add(new SaveOp(Current(), TopLayer, x, y, entry));
        return index;
    }

    /// <summary>A filter that multiplies by the alpha of what was drawn to the top layer: the layer itself is the mask, which a later composite reads.</summary>
    internal ulong SaveLayerAsMaskImage() => _ui.AddFilter(new UiFilter(0, 0f, default, default, [], MaskLayer: TopLayer));

    /// <summary>A texture from straight-alpha or premultiplied RGBA8 pixels (<paramref name="premultiplied"/> says which); the handle is its bindless index.</summary>
    internal ulong CreateTexture(ReadOnlySpan<byte> rgba, int width, int height, bool premultiplied)
    {
        byte[] pixels = rgba.ToArray();
        if (!premultiplied)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = pixels[i + 3];
                pixels[i] = (byte)((pixels[i] * alpha + 127) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * alpha + 127) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * alpha + 127) / 255);
            }
        }

        GpuImage image = _device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, (uint)width, (uint)height, ImageUsage.Sampled), "UI texture");
        _device.UploadImage(image, pixels, [new ImageRegion(0, 0, 0, (uint)width, (uint)height)], ImageState.ShaderRead);
        uint index = _device.Bindless.AddSampledImage(image);
        _textures[index] = new TextureEntry(image, index);
        return index;
    }

    internal void ReleaseTexture(ulong handle)
    {
        if (_textures.Remove((uint)handle, out TextureEntry? entry))
        {
            _released.Add(entry); // a frame in flight may still sample it: it is freed when the next frame is built, once that frame is done
        }
    }

    // ---- The passes ----

    public void AddPasses(RenderGraph graph, ImageHandle output, Frame frame)
    {
        _pipelines.Apply(frame);
        foreach (TextureEntry entry in _released)
        {
            frame.Defer(new Release(() => Free(entry)));
        }

        _released.Clear();

        // RmlUi hands the geometry over by calling back into the host: what it asks for is recorded in order, then turned into passes.
        _ops.Clear();
        _stack.Clear();
        _clipEnabled = false;
        _clipVersion = -1;
        _maskCount = 0;
        _nextLayer = 1;
        _ui.RenderContext();
        _stack.Clear();
        if (_ops.Count == 0)
        {
            LastFrame = default;
            LastEffects = default;
            return;
        }

        // Every geometry that is drawn goes into the frame's buffers once, however many times it is drawn; every gradient into one of its own.
        _packed.Clear();
        ulong vertexTotal = 0;
        ulong indexTotal = 0;
        var gradients = new Dictionary<UiShader, int>();
        int draws = 0;
        foreach (Op op in _ops)
        {
            Geometry? geometry = op switch { DrawOp d => d.Geometry, MaskOp m => m.Geometry, _ => null };
            if (geometry is not null && !_packed.ContainsKey(geometry))
            {
                _packed[geometry] = new Packed((uint)vertexTotal, (uint)indexTotal, (uint)geometry.Indices.Length);
                vertexTotal += (ulong)geometry.Vertices.Length;
                indexTotal += (ulong)geometry.Indices.Length;
            }

            if (op is DrawOp { Shader: { } shader })
            {
                gradients.TryAdd(shader, gradients.Count);
            }

            if (op is DrawOp)
            {
                draws++;
            }
        }

        int slot = frame.SlotIndex;
        GpuBuffer vertices = Ensure(ref _vertexBuffers[slot], vertexTotal * (ulong)sizeof(ArVertex), BufferUsage.Storage, frame);
        GpuBuffer indices = Ensure(ref _indexBuffers[slot], indexTotal * sizeof(uint), BufferUsage.Index, frame);
        foreach ((Geometry geometry, Packed where) in _packed)
        {
            geometry.Vertices.AsSpan().CopyTo(new Span<ArVertex>(vertices.Mapped + (ulong)where.VertexBase * (ulong)sizeof(ArVertex), geometry.Vertices.Length));
            geometry.Indices.AsSpan().CopyTo(new Span<int>(indices.Mapped + (ulong)where.IndexBase * sizeof(uint), geometry.Indices.Length));
        }

        ulong gradientAddress = 0;
        if (gradients.Count > 0)
        {
            GpuBuffer buffer = Ensure(ref _gradientBuffers[slot], (ulong)(gradients.Count * sizeof(GradientData)), BufferUsage.Storage, frame);
            foreach ((UiShader shader, int index) in gradients)
            {
                WriteGradient(shader, (GradientData*)(buffer.Mapped + ((ulong)index * (ulong)sizeof(GradientData))));
            }

            gradientAddress = buffer.Address;
        }

        LastFrame = (draws, (int)vertexTotal, (int)(indexTotal / 3));
        var builder = new Builder(this, graph, output, indices, vertices.Address, gradientAddress, gradients);
        builder.Run();
        LastEffects = (_maskCount, _nextLayer - 1, builder.Composites);
    }

    private static void WriteGradient(UiShader shader, GradientData* data)
    {
        *data = default;
        int count = Math.Min(shader.Stops.Length, GradientData.MaxStops);
        for (int i = 0; i < count; i++)
        {
            (Vector4 color, float position) = shader.Stops[i];
            data->Colors[(i * 4) + 0] = color.X;
            data->Colors[(i * 4) + 1] = color.Y;
            data->Colors[(i * 4) + 2] = color.Z;
            data->Colors[(i * 4) + 3] = color.W;
            data->Positions[i] = position;
        }

        data->Count = (uint)count;
        data->Repeating = shader.Repeating ? 1u : 0u;
        data->Kind = (uint)shader.Kind;
        data->A = shader.Kind switch
        {
            ArShaderKind.LinearGradient => new Vector4(shader.P0.X, shader.P0.Y, shader.P1.X, shader.P1.Y),
            ArShaderKind.RadialGradient => new Vector4(shader.Center.X, shader.Center.Y, shader.Radius.X, shader.Radius.Y),
            _ => new Vector4(shader.Center.X, shader.Center.Y, shader.Angle, 0f),
        };
    }

    /// <summary>Turns the frame's operations into passes of the render graph: runs of geometry into one layer under one clip mask, a pass for each clip mask operation, and the passes of each composite's filter chain.</summary>
    private sealed class Builder(UiRenderer owner, RenderGraph graph, ImageHandle output, GpuBuffer indices, ulong vertexAddress, ulong gradientAddress, Dictionary<UiShader, int> gradients)
    {
        private readonly Dictionary<int, (ImageHandle Image, bool Cleared)> _layers = [];
        private readonly Dictionary<int, ImageHandle> _masks = [];
        private readonly Dictionary<uint, ImageHandle> _saved = [];
        private readonly List<DrawOp> _run = [];
        private readonly Vector2 _viewport = new(owner._ui.Width, owner._ui.Height);
        private int _temporaries;

        public int Composites { get; private set; }

        public void Run()
        {
            foreach (Op op in owner._ops)
            {
                if (op is DrawOp draw)
                {
                    if (_run.Count > 0 && (_run[0].State.Layer != draw.State.Layer || _run[0].State.Clip != draw.State.Clip))
                    {
                        FlushRun();
                    }

                    _run.Add(draw);
                    continue;
                }

                FlushRun();
                switch (op)
                {
                    case MaskOp mask:
                        AddMask(mask);
                        break;
                    case CompositeOp composite:
                        AddComposite(composite);
                        break;
                    case SaveOp save:
                        AddSave(save);
                        break;
                }
            }

            FlushRun();
        }

        private ImageHandle Layer(int id, out bool first)
        {
            if (id == 0)
            {
                first = false;
                return output;
            }

            if (!_layers.TryGetValue(id, out (ImageHandle Image, bool Cleared) layer))
            {
                layer = (graph.CreateImage($"UI layer {id}", new ImageDescription(LayerFormat, (uint)_viewport.X, (uint)_viewport.Y, ImageUsage.ColorAttachment | ImageUsage.Sampled)), false);
            }

            first = !layer.Cleared;
            _layers[id] = (layer.Image, true);
            return layer.Image;
        }

        private ImageHandle Temporary() => graph.CreateImage($"UI temporary {_temporaries++}", new ImageDescription(LayerFormat, (uint)_viewport.X, (uint)_viewport.Y, ImageUsage.ColorAttachment | ImageUsage.Sampled));

        private static (int X, int Y, int Right, int Bottom) Region(State state, uint width, uint height)
        {
            int x = 0;
            int y = 0;
            int right = (int)width;
            int bottom = (int)height;
            if (state.Scissor)
            {
                x = Math.Clamp(state.ScissorX, 0, right);
                y = Math.Clamp(state.ScissorY, 0, bottom);
                right = Math.Clamp(state.ScissorX + state.ScissorWidth, x, right);
                bottom = Math.Clamp(state.ScissorY + state.ScissorHeight, y, bottom);
            }

            return (x, y, right, bottom);
        }

        private UiPush Push(DrawOp op, Packed packed, uint clipIndex, uint sampler)
        {
            Matrix4x4 m = op.State.Transform;
            return new UiPush
            {
                Vertices = vertexAddress,
                Texture = op.Texture,
                Sampler = sampler,
                Transform0 = new Vector4(m.M11, m.M12, m.M13, m.M14),
                Transform1 = new Vector4(m.M21, m.M22, m.M23, m.M24),
                Transform2 = new Vector4(m.M31, m.M32, m.M33, m.M34),
                Transform3 = new Vector4(m.M41, m.M42, m.M43, m.M44),
                Translation = new Vector2(op.X, op.Y),
                Viewport = _viewport,
                VertexOffset = packed.VertexBase,
                HasTransform = op.State.HasTransform ? 1u : 0u,
                ClipTexture = clipIndex,
            };
        }

        private void FlushRun()
        {
            if (_run.Count == 0)
            {
                return;
            }

            DrawOp[] ops = [.. _run];
            _run.Clear();
            State state = ops[0].State;
            ImageHandle target = Layer(state.Layer, out bool first);
            ImageHandle? mask = null;
            graph.AddPass("UI", PassType.Graphics, pass =>
            {
                pass.WriteColor(target, first ? Vector4.Zero : null);
                pass.NoCull();
                if (state.Clip >= 0)
                {
                    mask = pass.ReadSampled(_masks[state.Clip]);
                }

                var textures = new HashSet<ImageHandle>();
                foreach (DrawOp op in ops)
                {
                    if (_saved.TryGetValue(op.Texture, out ImageHandle saved) && textures.Add(saved))
                    {
                        pass.ReadSampled(saved);
                    }
                }

                pass.Execute(context =>
                {
                    ResolvedImage resolved = context.Image(target);
                    CommandRecorder recorder = context.Recorder;
                    FormatPipelines pipelines = owner.PipelinesFor(resolved.Format);
                    recorder.BindPipeline(pipelines.Geometry(resolved.Format));
                    recorder.BindIndexBuffer(indices, IndexFormat.Index32);
                    uint clipIndex = mask is { } m ? context.Image(m).SampledIndex : 0;
                    foreach (DrawOp op in ops)
                    {
                        (int x, int y, int right, int bottom) = Region(op.State, resolved.Width, resolved.Height);
                        if (right <= x || bottom <= y)
                        {
                            continue;
                        }

                        Packed packed = owner._packed[op.Geometry];
                        UiPush push = Push(op, packed, clipIndex, owner._samplerIndex);
                        if (op.Shader is { } shader)
                        {
                            push.ShaderKind = 1;
                            push.Gradient = gradientAddress + ((ulong)gradients[shader] * (ulong)sizeof(GradientData));
                        }

                        recorder.SetScissor(x, y, (uint)(right - x), (uint)(bottom - y));
                        recorder.PushConstants(push);
                        recorder.DrawIndexed(packed.IndexCount, 1, packed.IndexBase);
                    }
                });
            });
        }

        private void AddMask(MaskOp op)
        {
            ImageHandle image = Temporary();
            _masks[op.Index] = image;
            ImageHandle? previous = null;
            graph.AddPass("UI clip mask", PassType.Graphics, pass =>
            {
                // Set starts empty and draws 1 inside the shape; the inverse starts full and draws 0 inside it; an intersection starts empty and copies the old mask inside the shape.
                pass.WriteColor(image, op.Operation == (int)ArClipOperation.SetInverse ? Vector4.One : Vector4.Zero);
                pass.NoCull();
                if (op.Operation == (int)ArClipOperation.Intersect)
                {
                    previous = pass.ReadSampled(_masks[op.Previous]);
                }

                pass.Execute(context =>
                {
                    ResolvedImage resolved = context.Image(image);
                    CommandRecorder recorder = context.Recorder;
                    recorder.BindPipeline(owner.PipelinesFor(resolved.Format).Mask(resolved.Format));
                    recorder.BindIndexBuffer(indices, IndexFormat.Index32);
                    recorder.SetScissor(0, 0, resolved.Width, resolved.Height);
                    Packed packed = owner._packed[op.Geometry];
                    var draw = new DrawOp(op.State, op.Geometry, op.X, op.Y, 0, null);
                    UiPush push = Push(draw, packed, previous is { } p ? context.Image(p).SampledIndex : 0, owner._samplerIndex);
                    push.ShaderKind = (uint)op.Operation;
                    recorder.PushConstants(push);
                    recorder.DrawIndexed(packed.IndexCount, 1, packed.IndexBase);
                });
            });
        }

        /// <summary>A pass over the whole target (inside the region) that draws one full-screen triangle sampling one or two layers.</summary>
        private void AddEffect(ImageHandle target, bool clear, ImageHandle source, ImageHandle? source2, ImageHandle? clip, FxPush push, State region, bool over)
        {
            graph.AddPass("UI effect", PassType.Graphics, pass =>
            {
                pass.WriteColor(target, clear ? Vector4.Zero : null);
                pass.NoCull();
                ImageHandle readSource = pass.ReadSampled(source);
                ImageHandle? read2 = source2 is { } s2 ? pass.ReadSampled(s2) : null;
                ImageHandle? readClip = clip is { } c ? pass.ReadSampled(c) : null;
                pass.Execute(context =>
                {
                    ResolvedImage resolved = context.Image(target);
                    CommandRecorder recorder = context.Recorder;
                    FormatPipelines pipelines = owner.PipelinesFor(resolved.Format);
                    recorder.BindPipeline(over ? pipelines.Over(resolved.Format) : pipelines.Replace(resolved.Format));
                    (int x, int y, int right, int bottom) = Region(region, resolved.Width, resolved.Height);
                    if (right <= x || bottom <= y)
                    {
                        return;
                    }

                    push.Source = context.Image(readSource).SampledIndex;
                    push.Source2 = read2 is { } r2 ? context.Image(r2).SampledIndex : 0;
                    push.Clip = readClip is { } rc ? context.Image(rc).SampledIndex : 0;
                    push.Sampler = owner._samplerIndex;
                    recorder.SetScissor(x, y, (uint)(right - x), (uint)(bottom - y));
                    recorder.PushConstants(push);
                    recorder.Draw(3, 1, 0, 0);
                });
            });
        }

        private static State Full => new(false, 0, 0, 0, 0, false, Matrix4x4.Identity, -1, 0);

        private void AddComposite(CompositeOp op)
        {
            if (op.Source == 0)
            {
                return; // the frame's own image cannot be sampled (a backdrop filter): not supported
            }

            Composites++;
            ImageHandle current = Layer(op.Source, out _);
            State region = op.State;
            State passRegion = region with { Clip = -1 };
            foreach (UiFilter filter in op.Filters)
            {
                var push = new FxPush { Viewport = _viewport };
                if (filter.MaskLayer >= 0)
                {
                    ImageHandle mask = Layer(filter.MaskLayer, out _);
                    ImageHandle masked = Temporary();
                    push.Mode = 3;
                    AddEffect(masked, true, current, mask, null, push, passRegion, over: false);
                    current = masked;
                }
                else if (filter.Kind == ArFilterKind.ColorMatrix)
                {
                    float[] m = filter.Matrix;
                    push.Mode = 0;
                    push.M0 = new Vector4(m[0], m[1], m[2], m[3]);
                    push.M1 = new Vector4(m[5], m[6], m[7], m[8]);
                    push.M2 = new Vector4(m[10], m[11], m[12], m[13]);
                    push.M3 = new Vector4(m[15], m[16], m[17], m[18]);
                    push.M4 = new Vector4(m[4], m[9], m[14], m[19]);
                    ImageHandle result = Temporary();
                    AddEffect(result, true, current, null, null, push, passRegion, over: false);
                    current = result;
                }
                else if (filter.Kind == ArFilterKind.Blur)
                {
                    current = Blur(current, filter.Sigma, passRegion);
                }
                else if (filter.Kind == ArFilterKind.DropShadow)
                {
                    ImageHandle blurred = filter.Sigma > 0.01f ? Blur(current, filter.Sigma, passRegion) : current;
                    ImageHandle result = Temporary();
                    push.Mode = 2;
                    push.M0 = filter.Color;
                    push.M1 = new Vector4(filter.Offset, 0, 0);
                    AddEffect(result, true, blurred, current, null, push, passRegion, over: false);
                    current = result;
                }
            }

            ImageHandle destination = Layer(op.Destination, out bool first);
            ImageHandle? clip = region.Clip >= 0 ? _masks[region.Clip] : null;
            AddEffect(destination, first, current, null, clip, new FxPush { Mode = 4, Viewport = _viewport }, region, over: !op.Replace);
        }

        private ImageHandle Blur(ImageHandle source, float sigma, State region)
        {
            ImageHandle horizontal = Temporary();
            AddEffect(horizontal, true, source, null, null, new FxPush { Mode = 1, M0 = new Vector4(1, 0, sigma, 0), Viewport = _viewport }, region, over: false);
            ImageHandle vertical = Temporary();
            AddEffect(vertical, true, horizontal, null, null, new FxPush { Mode = 1, M0 = new Vector4(0, 1, sigma, 0), Viewport = _viewport }, region, over: false);
            return vertical;
        }

        /// <summary>Copies the region of a layer into the texture RmlUi was given: a pass that draws it, at the size of the texture.</summary>
        private void AddSave(SaveOp op)
        {
            ImageHandle source = Layer(op.Layer, out _);
            ImageHandle target = graph.ImportImage("UI saved layer", op.Target.Image, ImageState.Undefined, ImageState.ShaderRead, sampledIndex: op.Target.BindlessIndex);
            _saved[op.Target.BindlessIndex] = target;
            var push = new FxPush { Mode = 4, Viewport = _viewport, Shift = new Vector2(op.X, op.Y) };
            AddEffect(target, true, source, null, null, push, Full, over: false);
        }
    }

    private FormatPipelines PipelinesFor(PixelFormat format)
    {
        lock (_targetFormats)
        {
            _targetFormats.Add(format);
        }

        return _pipelines.Current;
    }

    private GpuBuffer Ensure(ref GpuBuffer? buffer, ulong needed, BufferUsage usage, Frame frame)
    {
        if (buffer is null || buffer.Size < needed)
        {
            if (buffer is not null)
            {
                frame.Defer(buffer);
            }

            buffer = _device.CreateBuffer(Math.Max(needed + needed / 2, 64 * 1024), usage, MemoryKind.Upload, "UI geometry");
        }

        return buffer;
    }

    private void Free(TextureEntry entry)
    {
        _device.Bindless.RemoveSampledImage(entry.BindlessIndex);
        entry.Image.Dispose();
    }

    public void Dispose()
    {
        _ui.Detach(this);
        _device.WaitIdle();
        foreach (TextureEntry entry in _textures.Values)
        {
            Free(entry);
        }

        foreach (TextureEntry entry in _released)
        {
            Free(entry);
        }

        _textures.Clear();
        _released.Clear();
        _geometries.Clear();
        _pipelines.Dispose();
        foreach (GpuBuffer? buffer in _vertexBuffers.Concat(_indexBuffers).Concat(_gradientBuffers))
        {
            buffer?.Dispose();
        }

        _device.Bindless.RemoveSampler(_samplerIndex);
        _sampler.Dispose();
    }
}
