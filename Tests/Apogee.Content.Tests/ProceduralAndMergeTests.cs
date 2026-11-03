using Apogee.Core.Graphics;
using System;
using System.Linq;
using System.Numerics;
using Xunit;

namespace Apogee.Content.Tests;

public class ProceduralAndMergeTests
{
    /// <summary>Every triangle's winding agrees with its vertex normals: counter-clockwise seen from outside.</summary>
    private static void AssertOutwardWinding(MeshData mesh)
    {
        Assert.Equal(0, mesh.Indices.Length % 3);
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            Vertex a = mesh.Vertices[mesh.Indices[i]];
            Vertex b = mesh.Vertices[mesh.Indices[i + 1]];
            Vertex c = mesh.Vertices[mesh.Indices[i + 2]];
            Vector3 face = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            if (face.LengthSquared() < 1e-12f)
            {
                continue;       // a degenerate pole triangle
            }

            Assert.True(Vector3.Dot(face, a.Normal + b.Normal + c.Normal) > 0f, $"triangle {i / 3} of {mesh.Name} faces inward");
        }
    }

    [Fact]
    public void ShapesHaveOutwardWindingUnitNormalsAndBounds()
    {
        MeshData[] shapes =
        [
            ProceduralMeshes.Box("box", new Vector3(2, 3, 4), 0),
            ProceduralMeshes.Plane("plane", 5, 6, 0),
            ProceduralMeshes.Cylinder("cylinder", 0.5f, 3f, 16, 0),
            ProceduralMeshes.Sphere("sphere", 1.5f, 24, 12, 0),
        ];

        foreach (MeshData shape in shapes)
        {
            AssertOutwardWinding(shape);
            Assert.All(shape.Vertices, v =>
            {
                Assert.Equal(1f, v.Normal.Length(), 1e-4f);
                Assert.Equal(1f, new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z).Length(), 1e-4f);
            });
            Assert.All(shape.Vertices, v => Assert.True(Vector3.Min(v.Position, shape.BoundsMin) == shape.BoundsMin && Vector3.Max(v.Position, shape.BoundsMax) == shape.BoundsMax));
        }

        Assert.Equal(new Vector3(-1, -1.5f, -2), shapes[0].BoundsMin);
        Assert.Equal(new Vector3(0.5f, 3f, 0.5f), shapes[2].BoundsMax);
    }

    [Fact]
    public void ABoxRepeatsItsTextureByTheUvScale()
    {
        MeshData box = ProceduralMeshes.Box("b", new Vector3(2, 2, 2), 0, uvScale: 3f);

        Assert.Equal(6f, box.Vertices.Max(v => v.Uv0.X), 1e-5f);        // 2 m wide, 3 repeats per meter
    }

    private static ModelData Model(string name, int textures, Vector3 at, LightData? light = null)
    {
        var texture = new TextureData("t", PixelFormat.R8G8B8A8Unorm, 1, 1, [new byte[4]], false, SamplerData.Default);
        return new ModelData
        {
            Name = name,
            Meshes = [ProceduralMeshes.Box(name, Vector3.One, materialIndex(textures))],
            Instances = [new MeshInstance(0, Matrix4x4.CreateTranslation(at))],
            Materials = [new MaterialData { Name = name, BaseColorTexture = textures > 0 ? new TextureRef(textures - 1, 0, Vector2.Zero, Vector2.One, 0) : null }],
            Textures = [.. Enumerable.Repeat(texture, textures)],
            Lights = light is null ? [] : [light],
            BoundsMin = at - new Vector3(0.5f),
            BoundsMax = at + new Vector3(0.5f),
        };

        static int materialIndex(int _) => 0;
    }

    [Fact]
    public void CombiningShiftsMeshMaterialAndTextureIndices()
    {
        ModelData a = Model("a", textures: 2, Vector3.Zero);
        ModelData b = Model("b", textures: 3, new Vector3(10, 0, 0));

        ModelData merged = ModelMerge.Combine("both", [(a, Matrix4x4.Identity), (b, Matrix4x4.CreateTranslation(0, 5, 0))]);

        Assert.Equal(2, merged.Meshes.Count);
        Assert.Equal(2, merged.Materials.Count);
        Assert.Equal(5, merged.Textures.Count);
        Assert.Equal(1, merged.Meshes[1].MaterialIndex);                          // b's material comes after a's
        Assert.Equal(1, merged.Materials[0].BaseColorTexture!.Value.Texture);     // a's texture 1
        Assert.Equal(2 + 2, merged.Materials[1].BaseColorTexture!.Value.Texture); // b's texture 2, after a's two
        Assert.Equal(1, merged.Instances[1].Mesh);
        Assert.Equal(new Vector3(10, 5, 0), merged.Instances[1].World.Translation);
    }

    [Fact]
    public void CombiningMovesLightsAndRecomputesBounds()
    {
        var light = new LightData("lamp", LightKind.Spot, Vector3.One, 5f, 0f, 0.1f, 0.4f, new Vector3(1, 2, 3), -Vector3.UnitZ);
        ModelData a = Model("a", 0, Vector3.Zero, light);

        ModelData merged = ModelMerge.Combine("m", [(a, Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(10, 0, 0))]);

        LightData moved = Assert.Single(merged.Lights);
        Assert.Equal(3f + 10f, moved.Position.X, 1e-4f);          // (1,2,3) rotated a quarter turn about Y is (3,2,-1), then +10 in x
        Assert.Equal(-1f, moved.Position.Z, 1e-4f);
        Assert.Equal(-1f, moved.Direction.X, 1e-4f);              // -Z rotated a quarter turn about Y points to -X
        Assert.Equal(1f, moved.Direction.Length(), 1e-5f);
        Assert.Equal(9.5f, merged.BoundsMin.X, 1e-4f);
        Assert.Equal(10.5f, merged.BoundsMax.X, 1e-4f);
    }

    [Fact]
    public void PlaceOnGroundScalesToTheHeightAndCentersTheFootprint()
    {
        var model = new ModelData
        {
            Name = "m",
            Meshes = [ProceduralMeshes.Box("b", new Vector3(0.02f, 0.04f, 0.01f), 0)],
            Instances = [new MeshInstance(0, Matrix4x4.CreateTranslation(5, 7, 9))],
            Materials = [new MaterialData()],
            Textures = [],
            Lights = [],
            BoundsMin = new Vector3(4.99f, 6.98f, 8.995f),
            BoundsMax = new Vector3(5.01f, 7.02f, 9.005f),
        };

        Matrix4x4 place = ModelMerge.PlaceOnGround(model, 3f, -2f, 2f);
        ModelData placed = ModelMerge.Combine("p", [(model, place)]);

        Assert.Equal(0f, placed.BoundsMin.Y, 1e-4f);                                // stands on the ground
        Assert.Equal(2f, placed.BoundsMax.Y, 1e-3f);                                // two meters tall
        Assert.Equal(3f, (placed.BoundsMin.X + placed.BoundsMax.X) / 2f, 1e-3f);   // centered on x = 3
        Assert.Equal(-2f, (placed.BoundsMin.Z + placed.BoundsMax.Z) / 2f, 1e-3f);
    }
}
