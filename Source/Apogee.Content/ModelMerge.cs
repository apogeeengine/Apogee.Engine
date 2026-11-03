using System;
using System.Collections.Generic;
using System.Numerics;

namespace Apogee.Content;

/// <summary>Combines models into one scene: the way a sample or a level puts several glTF files and generated shapes together.</summary>
public static class ModelMerge
{
    /// <summary>Puts every model in one, each placed by its transform. Meshes, materials and textures keep their order, one model after the other.</summary>
    public static ModelData Combine(string name, IReadOnlyList<(ModelData Model, Matrix4x4 Transform)> parts)
    {
        var meshes = new List<MeshData>();
        var materials = new List<MaterialData>();
        var textures = new List<TextureData>();
        var instances = new List<MeshInstance>();
        var lights = new List<LightData>();

        foreach ((ModelData model, Matrix4x4 transform) in parts)
        {
            int meshBase = meshes.Count;
            int materialBase = materials.Count;
            int textureBase = textures.Count;

            textures.AddRange(model.Textures);
            foreach (MaterialData material in model.Materials)
            {
                materials.Add(ShiftTextures(material, textureBase));
            }

            foreach (MeshData mesh in model.Meshes)
            {
                meshes.Add(new MeshData(mesh.Name, mesh.Vertices, mesh.Indices, mesh.MaterialIndex >= 0 ? mesh.MaterialIndex + materialBase : -1, mesh.BoundsMin, mesh.BoundsMax));
            }

            foreach (MeshInstance instance in model.Instances)
            {
                instances.Add(new MeshInstance(instance.Mesh + meshBase, instance.World * transform));
            }

            foreach (LightData light in model.Lights)
            {
                Vector3 direction = Vector3.TransformNormal(light.Direction, transform);
                lights.Add(light with
                {
                    Position = Vector3.Transform(light.Position, transform),
                    Direction = direction.LengthSquared() > 0f ? Vector3.Normalize(direction) : light.Direction,
                });
            }
        }

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (MeshInstance instance in instances)
        {
            MeshData mesh = meshes[instance.Mesh];
            for (int corner = 0; corner < 8; corner++)
            {
                var point = new Vector3((corner & 1) != 0 ? mesh.BoundsMax.X : mesh.BoundsMin.X, (corner & 2) != 0 ? mesh.BoundsMax.Y : mesh.BoundsMin.Y, (corner & 4) != 0 ? mesh.BoundsMax.Z : mesh.BoundsMin.Z);
                Vector3 world = Vector3.Transform(point, instance.World);
                min = Vector3.Min(min, world);
                max = Vector3.Max(max, world);
            }
        }

        if (instances.Count == 0)
        {
            min = max = Vector3.Zero;
        }

        return new ModelData
        {
            Name = name,
            Meshes = meshes,
            Instances = instances,
            Materials = materials,
            Textures = textures,
            Lights = lights,
            BoundsMin = min,
            BoundsMax = max,
        };
    }

    /// <summary>
    /// The transform that scales <paramref name="model"/> uniformly to <paramref name="height"/> meters tall and stands it on the ground at
    /// (<paramref name="x"/>, 0, <paramref name="z"/>), centered over that point: for dropping a model into a scene without knowing its units.
    /// </summary>
    public static Matrix4x4 PlaceOnGround(ModelData model, float x, float z, float height, float yawRadians = 0f)
    {
        Vector3 size = model.BoundsMax - model.BoundsMin;
        float scale = size.Y > 0f ? height / size.Y : 1f;
        Vector3 bottomCenter = new((model.BoundsMin.X + model.BoundsMax.X) * 0.5f, model.BoundsMin.Y, (model.BoundsMin.Z + model.BoundsMax.Z) * 0.5f);
        return Matrix4x4.CreateTranslation(-bottomCenter) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(yawRadians) * Matrix4x4.CreateTranslation(x, 0f, z);
    }

    private static MaterialData ShiftTextures(MaterialData material, int textureBase)
    {
        TextureRef? Shift(TextureRef? reference) => reference is { } r ? r with { Texture = r.Texture + textureBase } : null;
        return new MaterialData
        {
            Name = material.Name,
            BaseColorFactor = material.BaseColorFactor,
            BaseColorTexture = Shift(material.BaseColorTexture),
            MetallicFactor = material.MetallicFactor,
            RoughnessFactor = material.RoughnessFactor,
            MetallicRoughnessTexture = Shift(material.MetallicRoughnessTexture),
            NormalTexture = Shift(material.NormalTexture),
            NormalScale = material.NormalScale,
            OcclusionTexture = Shift(material.OcclusionTexture),
            OcclusionStrength = material.OcclusionStrength,
            EmissiveFactor = material.EmissiveFactor,
            EmissiveTexture = Shift(material.EmissiveTexture),
            AlphaMode = material.AlphaMode,
            AlphaCutoff = material.AlphaCutoff,
            DoubleSided = material.DoubleSided,
            Unlit = material.Unlit,
        };
    }
}
