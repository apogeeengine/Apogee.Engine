using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Apogee.Content;
using Apogee.Graphics;

namespace Apogee.Rendering;

/// <summary>How a draw is sorted into passes.</summary>
internal enum DrawClass
{
    Opaque,
    Mask,
    Blend,
}

/// <summary>
/// One mesh instance of a model (a glTF node that has a mesh), ready to draw. Everything is in the model's own space: the world matrix keeps its rotation and scale but not its
/// translation, which is <see cref="Offset"/>, because the renderer adds the offset from the camera to it every frame in double precision.
/// </summary>
internal readonly record struct ModelNode(
    Matrix4x4 World,
    Matrix4x4 Normal,
    Vector3 Offset,
    uint VertexOffset,
    uint Material,
    Vector3 BoxCenter,
    Vector3 BoxExtents,
    uint FirstIndex,
    uint IndexCount,
    DrawClass Class,
    bool DoubleSided);

/// <summary>
/// A <see cref="ModelData"/> on the GPU: its vertices, indices, materials and textures in buffers and in the bindless heap. It is placed in the world any number of times (by a
/// <see cref="RenderScene"/>, or by entities with a <see cref="ModelRenderer"/>); each placement costs one instance record per mesh instance, never another copy of the data.
/// </summary>
public sealed unsafe class GpuModel : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly List<GpuImage> _images = [];
    private readonly List<GpuSampler> _samplers = [];
    private readonly List<uint> _imageSlots = [];
    private readonly List<uint> _samplerSlots = [];

    internal GpuModel(GraphicsDevice device, ModelData model)
    {
        _device = device;
        Name = model.Name;
        BoundsMin = model.BoundsMin;
        BoundsMax = model.BoundsMax;
        Lights = [.. model.Lights.Where(l => l.Kind != LightKind.Directional)];
        LightData? sun = model.Lights.FirstOrDefault(l => l.Kind == LightKind.Directional);
        if (sun is not null)
        {
            SunDirection = -sun.Direction;
            SunColor = sun.Color * sun.Intensity;
        }

        (uint[] imageIndex, uint[] samplerIndex) = UploadTextures(model);
        Materials = CreateMaterials(model, imageIndex, samplerIndex);
        (Vertices, Indices, Nodes) = UploadGeometry(model);
    }

    public string Name { get; }

    /// <summary>The box around the model, in its own space.</summary>
    public Vector3 BoundsMin { get; }

    public Vector3 BoundsMax { get; }

    /// <summary>Point and spot lights the model came with, in its own space.</summary>
    public IReadOnlyList<LightData> Lights { get; }

    /// <summary>The direction toward the model's sun (when it has a directional light), and its color times illuminance (zero when it has none).</summary>
    public Vector3? SunDirection { get; }

    public Vector3 SunColor { get; }

    /// <summary>The number of mesh instances: the draws one placement of the model makes.</summary>
    public int MeshCount => Nodes.Length;

    internal GpuBuffer Vertices { get; }

    internal GpuBuffer Indices { get; }

    internal GpuBuffer Materials { get; }

    internal ModelNode[] Nodes { get; }

    private (uint[] Images, uint[] Samplers) UploadTextures(ModelData model)
    {
        uint[] imageIndex = new uint[model.Textures.Count];
        uint[] samplerIndex = new uint[model.Textures.Count];
        var samplerBySettings = new Dictionary<SamplerData, uint>();
        for (int i = 0; i < model.Textures.Count; i++)
        {
            TextureData texture = model.Textures[i];
            var description = new ImageDescription(texture.Format, (uint)texture.Width, (uint)texture.Height, ImageUsage.Sampled, MipLevels: (uint)texture.Mips.Count);
            GpuImage image = _device.CreateImage(description, texture.Name);
            _images.Add(image);

            var regions = new ImageRegion[texture.Mips.Count];
            byte[] blob = new byte[texture.Mips.Sum(m => m.Length)];
            ulong offset = 0;
            for (int level = 0; level < texture.Mips.Count; level++)
            {
                texture.Mips[level].CopyTo(blob, (int)offset);
                regions[level] = new ImageRegion(offset, (uint)level, 0, (uint)Math.Max(1, texture.Width >> level), (uint)Math.Max(1, texture.Height >> level));
                offset += (ulong)texture.Mips[level].Length;
            }

            _device.UploadImage(image, blob, regions, ImageState.ShaderRead);
            imageIndex[i] = _device.Bindless.AddSampledImage(image);
            _imageSlots.Add(imageIndex[i]);

            if (!samplerBySettings.TryGetValue(texture.Sampler, out uint slot))
            {
                var wrap = (TextureWrap w) => w switch
                {
                    TextureWrap.ClampToEdge => AddressMode.ClampToEdge,
                    TextureWrap.MirroredRepeat => AddressMode.MirroredRepeat,
                    _ => AddressMode.Repeat,
                };
                var gpuSampler = _device.CreateSampler(
                    new SamplerDescription(
                        texture.Sampler.Filter == TextureFilter.Nearest ? Filter.Nearest : Filter.Linear,
                        texture.Sampler.Mipmaps && texture.Sampler.Filter == TextureFilter.Linear ? MipmapMode.Linear : MipmapMode.Nearest,
                        wrap(texture.Sampler.WrapU),
                        texture.Sampler.Filter == TextureFilter.Linear ? 8f : 1f),
                    "glTF sampler");
                _samplers.Add(gpuSampler);
                slot = _device.Bindless.AddSampler(gpuSampler);
                _samplerSlots.Add(slot);
                samplerBySettings[texture.Sampler] = slot;
            }

            samplerIndex[i] = slot;
        }

        return (imageIndex, samplerIndex);
    }

    private GpuBuffer CreateMaterials(ModelData model, uint[] imageIndex, uint[] samplerIndex)
    {
        var materials = new GpuMaterial[Math.Max(1, model.Materials.Count)];
        for (int m = 0; m < model.Materials.Count; m++)
        {
            MaterialData source = model.Materials[m];
            uint flags = 0;
            flags |= source.AlphaMode == AlphaMode.Mask ? 1u : 0;
            flags |= source.AlphaMode == AlphaMode.Blend ? 2u : 0;
            flags |= source.DoubleSided ? 4u : 0;
            flags |= source.Unlit ? 8u : 0;
            flags |= source.NormalTexture is not null ? 16u : 0;

            var gpu = new GpuMaterial
            {
                BaseColor = source.BaseColorFactor,
                EmissiveMetallic = new Vector4(source.EmissiveFactor, source.MetallicFactor),
                Shading = new Vector4(source.RoughnessFactor, source.NormalScale, source.OcclusionStrength, source.AlphaCutoff),
            };

            // Slot 0 of the bindless arrays is a white texel and a default sampler, which leaves a factor unchanged.
            uint Image(TextureRef? r) => r is { } t ? imageIndex[t.Texture] : 0;
            uint Sampler(TextureRef? r) => r is { } t ? samplerIndex[t.Texture] : 0;
            gpu.Textures0 = new UInt4(Image(source.BaseColorTexture), Image(source.MetallicRoughnessTexture), Image(source.NormalTexture), Image(source.OcclusionTexture));
            gpu.Textures1 = new UInt4(Image(source.EmissiveTexture), flags, 0, 0);
            gpu.Samplers0 = new UInt4(Sampler(source.BaseColorTexture), Sampler(source.MetallicRoughnessTexture), Sampler(source.NormalTexture), Sampler(source.OcclusionTexture));
            gpu.Samplers1 = new UInt4(Sampler(source.EmissiveTexture), 0, 0, 0);

            TextureRef?[] slots = [source.BaseColorTexture, source.MetallicRoughnessTexture, source.NormalTexture, source.OcclusionTexture, source.EmissiveTexture];
            for (int s = 0; s < slots.Length; s++)
            {
                (Vector4 row0, Vector4 row1) = RenderScene.TransformRows(slots[s]);
                gpu.Transforms[s * 2] = row0;
                gpu.Transforms[s * 2 + 1] = row1;
            }

            materials[m] = gpu;
        }

        return _device.CreateBuffer(MemoryMarshal.AsBytes<GpuMaterial>(materials), BufferUsage.Storage, "Materials");
    }

    private (GpuBuffer Vertices, GpuBuffer Indices, ModelNode[] Nodes) UploadGeometry(ModelData model)
    {
        int vertexCount = model.Meshes.Sum(m => m.Vertices.Length);
        int indexCount = model.Meshes.Sum(m => m.Indices.Length);
        var vertices = new Vertex[Math.Max(1, vertexCount)];
        var indices = new uint[Math.Max(1, indexCount)];
        var vertexOffsets = new uint[model.Meshes.Count];
        var firstIndices = new uint[model.Meshes.Count];
        int vertexCursor = 0;
        int indexCursor = 0;
        for (int i = 0; i < model.Meshes.Count; i++)
        {
            MeshData mesh = model.Meshes[i];
            vertexOffsets[i] = (uint)vertexCursor;
            firstIndices[i] = (uint)indexCursor;
            mesh.Vertices.CopyTo(vertices, vertexCursor);
            mesh.Indices.CopyTo(indices, indexCursor);
            vertexCursor += mesh.Vertices.Length;
            indexCursor += mesh.Indices.Length;
        }

        int defaultMaterial = 0;
        var nodes = new ModelNode[model.Instances.Count];
        for (int i = 0; i < model.Instances.Count; i++)
        {
            MeshInstance instance = model.Instances[i];
            MeshData mesh = model.Meshes[instance.Mesh];
            int materialIndex = mesh.MaterialIndex >= 0 ? mesh.MaterialIndex : defaultMaterial;
            Matrix4x4.Invert(instance.World, out Matrix4x4 inverse);
            MaterialData material = model.Materials[materialIndex];
            Vector3 center = Vector3.Transform((mesh.BoundsMin + mesh.BoundsMax) * 0.5f, instance.World);
            nodes[i] = new ModelNode(
                instance.World with { Translation = Vector3.Zero }, // the translation is added every frame, relative to the camera
                Matrix4x4.Transpose(inverse),
                instance.World.Translation,
                vertexOffsets[instance.Mesh],
                (uint)materialIndex,
                center,
                RenderScene.WorldExtents((mesh.BoundsMax - mesh.BoundsMin) * 0.5f, instance.World),
                firstIndices[instance.Mesh],
                (uint)mesh.Indices.Length,
                material.AlphaMode switch { AlphaMode.Mask => DrawClass.Mask, AlphaMode.Blend => DrawClass.Blend, _ => DrawClass.Opaque },
                material.DoubleSided);
        }

        return (
            _device.CreateBuffer(MemoryMarshal.AsBytes<Vertex>(vertices), BufferUsage.Storage, "Model vertices"),
            _device.CreateBuffer(MemoryMarshal.AsBytes<uint>(indices), BufferUsage.Index, "Model indices"),
            nodes);
    }

    /// <summary>Waits for the GPU to finish with the model, then frees it. Draw frames that use a model before disposing it.</summary>
    public void Dispose()
    {
        _device.WaitIdle();
        foreach (uint slot in _imageSlots)
        {
            _device.Bindless.RemoveSampledImage(slot);
        }

        foreach (uint slot in _samplerSlots)
        {
            _device.Bindless.RemoveSampler(slot);
        }

        _images.ForEach(i => i.Dispose());
        _samplers.ForEach(s => s.Dispose());
        Vertices.Dispose();
        Indices.Dispose();
        Materials.Dispose();
    }
}
