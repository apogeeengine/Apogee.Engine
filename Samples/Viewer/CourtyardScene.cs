using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Apogee.Content;

namespace Apogee.Samples.Viewer;

/// <summary>A colonnaded courtyard generated in code, optionally with the CC0 sample models standing on pedestals: a scene for the lights to play in.</summary>
internal static class CourtyardScene
{
    private const float HalfLength = 22f;
    private const float HalfWidth = 11f;

    /// <summary>The sample models, left to right along the middle of the courtyard: file, height in meters, and facing in degrees.</summary>
    private static readonly (string Folder, float Height, float Yaw)[] s_models =
    [
        ("WaterBottle", 1.3f, 0f),
        ("Lantern", 2.0f, 0f),
        ("BoomBox", 0.9f, 20f),
        ("ToyCar", 1.0f, -30f),
        ("SheenChair", 1.5f, 160f),
        ("NormalTangentTest", 1.6f, 0f),
    ];

    public static ModelData Build(string? assetsDirectory, ContentOptions options)
    {
        var shell = new List<MaterialData>();
        int Material(Vector4 color, float roughness, float metallic = 0f, bool doubleSided = false)
        {
            shell.Add(new MaterialData { BaseColorFactor = color, RoughnessFactor = roughness, MetallicFactor = metallic, DoubleSided = doubleSided });
            return shell.Count - 1;
        }

        int stone = Material(new Vector4(0.62f, 0.57f, 0.5f, 1), 0.85f);
        int floor = Material(new Vector4(0.34f, 0.31f, 0.28f, 1), 0.55f);
        int plaster = Material(new Vector4(0.74f, 0.68f, 0.58f, 1), 0.95f);
        int beam = Material(new Vector4(0.28f, 0.2f, 0.15f, 1), 0.7f);
        int pedestal = Material(new Vector4(0.18f, 0.18f, 0.2f, 1), 0.3f, metallic: 0.8f);
        Vector4[] clothColors = [new(0.7f, 0.1f, 0.1f, 1), new(0.1f, 0.4f, 0.15f, 1), new(0.12f, 0.2f, 0.6f, 1), new(0.7f, 0.55f, 0.1f, 1)];
        int[] cloth = [.. Array.ConvertAll(clothColors, c => Material(c, 0.95f, doubleSided: true))];

        var meshes = new List<MeshData>();
        var instances = new List<MeshInstance>();
        int Mesh(MeshData mesh)
        {
            meshes.Add(mesh);
            return meshes.Count - 1;
        }

        void Place(int mesh, Vector3 at, float yaw = 0f) => instances.Add(new MeshInstance(mesh, Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(at)));

        Place(Mesh(ProceduralMeshes.Plane("floor", HalfLength * 2, HalfWidth * 2, floor, uvScale: 0.5f)), Vector3.Zero);

        // Two colonnades, a beam along the top of each, and the walls behind them.
        int column = Mesh(ProceduralMeshes.Cylinder("column", 0.45f, 6f, 20, stone));
        int plinth = Mesh(ProceduralMeshes.Box("plinth", new Vector3(1.2f, 0.45f, 1.2f), stone));
        int capital = Mesh(ProceduralMeshes.Box("capital", new Vector3(1.1f, 0.35f, 1.1f), stone));
        int architrave = Mesh(ProceduralMeshes.Box("architrave", new Vector3(HalfLength * 2, 0.7f, 0.9f), beam));
        for (int side = -1; side <= 1; side += 2)
        {
            float z = side * 6f;
            for (int i = 0; i <= 10; i++)
            {
                float x = -HalfLength + 2f + i * 4.2f;
                Place(plinth, new Vector3(x, 0.225f, z));
                Place(column, new Vector3(x, 0.45f, z));
                Place(capital, new Vector3(x, 6.45f + 0.175f, z));
            }

            Place(architrave, new Vector3(0, 7.3f, z));
            Place(Mesh(ProceduralMeshes.Box("wall", new Vector3(HalfLength * 2, 9f, 0.6f), plaster)), new Vector3(0, 4.5f, side * (HalfWidth + 0.3f)));
        }

        Place(Mesh(ProceduralMeshes.Box("end wall", new Vector3(0.6f, 9f, HalfWidth * 2 + 1.2f), plaster)), new Vector3(-HalfLength - 0.3f, 4.5f, 0));
        Place(Mesh(ProceduralMeshes.Box("end wall", new Vector3(0.6f, 9f, HalfWidth * 2 + 1.2f), plaster)), new Vector3(HalfLength + 0.3f, 4.5f, 0));

        // Banners hang between the columns of both rows.
        for (int side = -1; side <= 1; side += 2)
        {
            for (int i = 0; i < 10; i++)
            {
                float x = -HalfLength + 4.1f + i * 4.2f;
                int banner = Mesh(ProceduralMeshes.Box($"banner{i}", new Vector3(1.6f, 3.4f, 0.04f), cloth[(i + (side + 1) / 2) % cloth.Length]));
                Place(banner, new Vector3(x, 4.2f, side * 5.2f));
            }
        }

        ModelData shellModel = new()
        {
            Name = "courtyard",
            Meshes = meshes,
            Instances = instances,
            Materials = shell,
            Textures = [],
            Lights = [],
            BoundsMin = new Vector3(-HalfLength, 0, -HalfWidth),
            BoundsMax = new Vector3(HalfLength, 9f, HalfWidth),
        };

        var parts = new List<(ModelData, Matrix4x4)> { (shellModel, Matrix4x4.Identity) };
        if (assetsDirectory is not null)
        {
            // Pedestals along the middle, a model on each.
            var pedestalMesh = ProceduralMeshes.Box("pedestal", new Vector3(1.6f, 0.9f, 1.6f), 0);
            var pedestals = new ModelData
            {
                Name = "pedestals",
                Meshes = [pedestalMesh],
                Instances = [],
                Materials = [shell[pedestal]],
                Textures = [],
                Lights = [],
                BoundsMin = new Vector3(0),
                BoundsMax = new Vector3(0),
            };
            var pedestalInstances = new List<MeshInstance>();
            for (int i = 0; i < s_models.Length; i++)
            {
                float x = -15f + i * 6f;
                pedestalInstances.Add(new MeshInstance(0, Matrix4x4.CreateTranslation(x, 0.45f, 0)));
                string file = Path.Combine(assetsDirectory, s_models[i].Folder, s_models[i].Folder + ".glb");
                if (File.Exists(file))
                {
                    ModelData model = GltfLoader.Load(file, options);
                    parts.Add((model, ModelMerge.PlaceOnGround(model, x, 0f, s_models[i].Height, s_models[i].Yaw * MathF.PI / 180f) * Matrix4x4.CreateTranslation(0, 0.9f, 0)));
                }
            }

            parts.Add((new ModelData
            {
                Name = pedestals.Name,
                Meshes = pedestals.Meshes,
                Instances = pedestalInstances,
                Materials = pedestals.Materials,
                Textures = [],
                Lights = [],
                BoundsMin = new Vector3(-16, 0, -1),
                BoundsMax = new Vector3(16, 0.9f, 1),
            }, Matrix4x4.Identity));
        }

        ModelData combined = ModelMerge.Combine("courtyard", parts);
        return combined;
    }

    /// <summary>Finds the Samples/Viewer/Assets folder by walking up from the program, or null when there is none.</summary>
    public static string? FindAssets(string? explicitDirectory)
    {
        if (explicitDirectory is not null)
        {
            return Directory.Exists(explicitDirectory) ? explicitDirectory : null;
        }

        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string candidate = Path.Combine(directory, "Samples", "Viewer", "Assets");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
