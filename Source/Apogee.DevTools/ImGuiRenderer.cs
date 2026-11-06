using Apogee.Core.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Apogee.Graphics;
using Apogee.Graphics.Shaders;
using Apogee.Native.CImGui;
using Apogee.Rendering;
using Apogee.Rendering.Graph;
using Apogee.Rendering.Shaders;

namespace Apogee.DevTools;

/// <summary>
/// Draws Dear ImGui's draw data onto the frame's output as a render-graph pass: its textures (the font atlas) become images in the bindless heap
/// as ImGui asks for them, and its vertices and indices go through per-frame buffers that the shader pulls from. Use it as the
/// <see cref="IRenderOverlay"/> of <see cref="SceneRenderer.Render"/>, or call <see cref="AddPasses"/> on your own graph.
/// </summary>
public sealed unsafe class ImGuiRenderer : IRenderOverlay, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct OverlayPush
    {
        public ulong Vertices;
        public Vector2 Scale;
        public Vector2 Translate;
        public uint Texture;
        public uint Sampler;
        public uint VertexOffset;
        public uint Unused;
    }

    private readonly record struct DrawCommand(int ScissorX, int ScissorY, uint ScissorWidth, uint ScissorHeight, uint Texture, uint VertexOffset, uint FirstIndex, uint IndexCount);

    private sealed record TextureEntry(GpuImage Image, uint BindlessIndex);

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>The overlay's pipelines, one per target format: made when the format is first drawn to, and again, off the render thread, for the formats seen when the shader changes.</summary>
    private sealed class FormatPipelines(GraphicsDevice device, PipelineScope scope, ShaderCode vertex, ShaderCode fragment)
    {
        private readonly Dictionary<PixelFormat, Pipeline> _byFormat = [];

        public Pipeline For(PixelFormat format)
        {
            if (!_byFormat.TryGetValue(format, out Pipeline? pipeline))
            {
                pipeline = scope.Add(device.CreateGraphicsPipeline(
                    new GraphicsPipelineDescription(vertex, fragment, [format], CullMode: CullMode.None, Blend: BlendMode.Alpha), $"ImGui {format}"));
                _byFormat[format] = pipeline;
            }

            return pipeline;
        }
    }

    private readonly GraphicsDevice _device;
    private readonly ShaderLibrary _shaders;
    private readonly ReloadablePipelines<FormatPipelines> _pipelines;
    private readonly HashSet<PixelFormat> _targetFormats = [];
    private readonly Dictionary<int, TextureEntry> _textures = [];
    private readonly GpuSampler _sampler;
    private readonly uint _samplerIndex;
    private readonly GpuBuffer?[] _vertexBuffers;
    private readonly GpuBuffer?[] _indexBuffers;
    private readonly int _framesInFlight;

    public ImGuiRenderer(GraphicsDevice device, ShaderLibrary? shaders = null, string? cacheDirectory = null, int framesInFlight = 2)
    {
        _device = device;
        _framesInFlight = framesInFlight;
        _vertexBuffers = new GpuBuffer?[framesInFlight];
        _indexBuffers = new GpuBuffer?[framesInFlight];

        _shaders = shaders ??= ShaderLibrary.Standard(cacheDirectory);
        _pipelines = new ReloadablePipelines<FormatPipelines>(shaders, "ImGui pipelines", ["shaders://imgui.slang"], scope =>
        {
            CompiledShader shader = shaders.Load("shaders://imgui.slang", backend: device.ShaderBackend);
            var group = new FormatPipelines(device, scope, shader.Code("imguiVs"), shader.Code("imguiFs"));
            PixelFormat[] seen;
            lock (_targetFormats)
            {
                seen = [.. _targetFormats];
            }

            foreach (PixelFormat format in seen)
            {
                group.For(format);
            }

            return group;
        });

        _sampler = device.CreateSampler(SamplerDescription.LinearClamp, "ImGui sampler");
        _samplerIndex = device.Bindless.AddSampler(_sampler);
    }

    /// <summary>The number of ImGui textures on the GPU (the font atlas, and any a game registers).</summary>
    public int TextureCount => _textures.Count;

    /// <summary>The library the shader comes from (its errors are what the dev overlay shows when an edit does not compile).</summary>
    public ShaderLibrary Shaders => _shaders;

    /// <summary>How many times the pipelines have been rebuilt after a shader change.</summary>
    internal int ShaderRebuilds => _pipelines.Rebuilds;

    /// <summary>Waits for the rebuild in progress (tests); the next frame then uses it.</summary>
    internal void WaitForShaderRebuilds() => _pipelines.WaitForPending();

    public void AddPasses(RenderGraph graph, ImageHandle output, Frame frame)
    {
        _pipelines.Apply(frame); // pipelines rebuilt since the last frame replace the old ones here

        ImDrawData* data = ImGui.GetDrawData();
        if (data == null || !data->Valid)
        {
            return;
        }

        ProcessTextures(data, frame);
        if (data->TotalVtxCount == 0 || data->TotalIdxCount == 0)
        {
            return;
        }

        int slot = frame.SlotIndex;
        GpuBuffer vertices = Ensure(ref _vertexBuffers[slot], (ulong)data->TotalVtxCount * (ulong)sizeof(ImDrawVert), BufferUsage.Storage, frame);
        GpuBuffer indices = Ensure(ref _indexBuffers[slot], (ulong)data->TotalIdxCount * sizeof(ushort), BufferUsage.Index, frame);

        // One buffer for every draw list: copy them in, and note where each command's vertices and indices ended up.
        var commands = new List<DrawCommand>();
        ImVec2 clipOffset = data->DisplayPos;
        ImVec2 scale = data->FramebufferScale;
        var vertexCursor = 0u;
        var indexCursor = 0u;
        for (int l = 0; l < data->CmdLists.Size; l++)
        {
            ImDrawList* list = data->CmdLists.Data[l];
            Buffer.MemoryCopy(list->VtxBuffer.Data, vertices.Mapped + vertexCursor * (uint)sizeof(ImDrawVert), (long)vertices.Size - vertexCursor * sizeof(ImDrawVert), list->VtxBuffer.Size * sizeof(ImDrawVert));
            Buffer.MemoryCopy(list->IdxBuffer.Data, indices.Mapped + indexCursor * sizeof(ushort), (long)indices.Size - indexCursor * sizeof(ushort), list->IdxBuffer.Size * sizeof(ushort));

            for (int c = 0; c < list->CmdBuffer.Size; c++)
            {
                ImDrawCmd* command = &list->CmdBuffer.Data[c];
                if (command->UserCallback != null)
                {
                    continue;       // ImGui's reset-render-state callback and any a game adds: nothing to do
                }

                int x0 = Math.Max(0, (int)((command->ClipRect.X - clipOffset.X) * scale.X));
                int y0 = Math.Max(0, (int)((command->ClipRect.Y - clipOffset.Y) * scale.Y));
                int x1 = (int)((command->ClipRect.Z - clipOffset.X) * scale.X);
                int y1 = (int)((command->ClipRect.W - clipOffset.Y) * scale.Y);
                if (x1 <= x0 || y1 <= y0)
                {
                    continue;
                }

                ulong texture = ImGui.ImTextureRefGetTexID(&command->TexRef);
                commands.Add(new DrawCommand(x0, y0, (uint)(x1 - x0), (uint)(y1 - y0), (uint)texture, vertexCursor + command->VtxOffset, indexCursor + command->IdxOffset, command->ElemCount));
            }

            vertexCursor += (uint)list->VtxBuffer.Size;
            indexCursor += (uint)list->IdxBuffer.Size;
        }

        float scaleX = 2f / data->DisplaySize.X;
        float scaleY = 2f / data->DisplaySize.Y;
        var projectionScale = new Vector2(scaleX, scaleY);
        var projectionTranslate = new Vector2(-1f - clipOffset.X * scaleX, -1f - clipOffset.Y * scaleY);
        ulong vertexAddress = vertices.Address;

        graph.AddPass("Dev overlay", PassType.Graphics, pass =>
        {
            pass.WriteColor(output);
            pass.NoCull();
            pass.Execute(context =>
            {
                ResolvedImage target = context.Image(output);
                CommandRecorder recorder = context.Recorder;
                recorder.BindPipeline(PipelineFor(target.Format));
                recorder.BindIndexBuffer(indices, IndexFormat.Index16);
                foreach (DrawCommand command in commands)
                {
                    uint width = Math.Min(command.ScissorWidth, target.Width - (uint)Math.Min(command.ScissorX, (int)target.Width));
                    uint height = Math.Min(command.ScissorHeight, target.Height - (uint)Math.Min(command.ScissorY, (int)target.Height));
                    if (width == 0 || height == 0)
                    {
                        continue;
                    }

                    recorder.SetScissor(command.ScissorX, command.ScissorY, width, height);
                    recorder.PushConstants(new OverlayPush
                    {
                        Vertices = vertexAddress,
                        Scale = projectionScale,
                        Translate = projectionTranslate,
                        Texture = command.Texture,
                        Sampler = _samplerIndex,
                        VertexOffset = command.VertexOffset,
                    });
                    recorder.DrawIndexed(command.IndexCount, 1, command.FirstIndex);
                }
            });
        });
    }

    private Pipeline PipelineFor(PixelFormat format)
    {
        lock (_targetFormats)
        {
            _targetFormats.Add(format);
        }

        return _pipelines.Current.For(format);
    }

    private GpuBuffer Ensure(ref GpuBuffer? buffer, ulong needed, BufferUsage usage, Frame frame)
    {
        if (buffer is null || buffer.Size < needed)
        {
            if (buffer is not null)
            {
                frame.Defer(buffer);
            }

            buffer = _device.CreateBuffer(Math.Max(needed + needed / 2, 64 * 1024), usage, MemoryKind.Upload, "ImGui geometry");
        }

        return buffer;
    }

    /// <summary>The texture protocol of Dear ImGui 1.92+: it says which textures to create, update and destroy, and the backend answers with an id per texture.</summary>
    private void ProcessTextures(ImDrawData* data, Frame frame)
    {
        if (data->Textures == null)
        {
            return;
        }

        for (int i = 0; i < data->Textures->Size; i++)
        {
            ImTextureData* texture = data->Textures->Data[i];
            switch (texture->Status)
            {
                case ImTextureStatus.WantCreate:
                    _textures[texture->UniqueID] = Upload(texture);
                    break;
                case ImTextureStatus.WantUpdates:
                    // A new image rather than writing into the old: a frame still in flight may be sampling it.
                    if (_textures.TryGetValue(texture->UniqueID, out TextureEntry? old))
                    {
                        frame.Defer(new Release(() => Free(old)));
                    }

                    _textures[texture->UniqueID] = Upload(texture);
                    break;
                case ImTextureStatus.WantDestroy when texture->UnusedFrames >= _framesInFlight:
                    if (_textures.Remove(texture->UniqueID, out TextureEntry? entry))
                    {
                        frame.Defer(new Release(() => Free(entry)));
                    }

                    texture->TexID = 0;
                    ImGui.ImTextureDataSetStatus(texture, ImTextureStatus.Destroyed);
                    break;
            }
        }
    }

    private TextureEntry Upload(ImTextureData* texture)
    {
        int width = texture->Width;
        int height = texture->Height;
        byte[] rgba = new byte[width * height * 4];
        byte* pixels = texture->Pixels;
        if (texture->Format == ImTextureFormat.Alpha8)
        {
            // Glyph coverage becomes white with that alpha, so text and shapes use the same shader.
            for (int i = 0; i < width * height; i++)
            {
                rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255;
                rgba[i * 4 + 3] = pixels[i];
            }
        }
        else
        {
            new ReadOnlySpan<byte>(pixels, rgba.Length).CopyTo(rgba);
        }

        GpuImage image = _device.CreateImage(new ImageDescription(PixelFormat.R8G8B8A8Unorm, (uint)width, (uint)height, ImageUsage.Sampled), "ImGui texture");
        _device.UploadImage(image, rgba, [new ImageRegion(0, 0, 0, (uint)width, (uint)height)], ImageState.ShaderRead);
        uint index = _device.Bindless.AddSampledImage(image);
        texture->TexID = index;
        ImGui.ImTextureDataSetStatus(texture, ImTextureStatus.Ok);
        return new TextureEntry(image, index);
    }

    private void Free(TextureEntry entry)
    {
        _device.Bindless.RemoveSampledImage(entry.BindlessIndex);
        entry.Image.Dispose();
    }

    public void Dispose()
    {
        _device.WaitIdle();
        foreach (TextureEntry entry in _textures.Values)
        {
            Free(entry);
        }

        _textures.Clear();
        _pipelines.Dispose();

        foreach (GpuBuffer? buffer in _vertexBuffers)
        {
            buffer?.Dispose();
        }

        foreach (GpuBuffer? buffer in _indexBuffers)
        {
            buffer?.Dispose();
        }

        _device.Bindless.RemoveSampler(_samplerIndex);
        _sampler.Dispose();
    }
}
