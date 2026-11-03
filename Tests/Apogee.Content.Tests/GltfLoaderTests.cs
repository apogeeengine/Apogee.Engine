using Apogee.Core.Graphics;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Apogee.Native.Cgltf;
using Apogee.Native.Ktx;
using Apogee.Native.Meshopt;
using Apogee.Native.Stb;
using Apogee.Native.Tests;
using Xunit;
using CgltfApi = Apogee.Native.Cgltf.Cgltf;
using KtxApi = Apogee.Native.Ktx.Ktx;
using MeshoptApi = Apogee.Native.Meshopt.Meshopt;

namespace Apogee.Content.Tests;

public class GltfLoaderTests
{
    public GltfLoaderTests()
    {
        NativeRequirements.Require(typeof(CgltfApi).Assembly, "cgltf");
        NativeRequirements.Require(typeof(Stbi).Assembly, "stb_image");
        NativeRequirements.Require(typeof(MeshoptApi).Assembly, "meshoptimizer");
        NativeRequirements.Require(typeof(KtxApi).Assembly, "ktx");
    }

    private static ModelData Load(GltfBuilder builder, ContentOptions? options = null) => GltfLoader.Load(builder.ToBytes(), "", options, "test");

    private static void AssertNear(Matrix4x4 expected, Matrix4x4 actual)
    {
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(expected.Get(i), actual.Get(i), 1e-4f);
        }
    }

    [Fact]
    public void ATriangleBecomesAMeshWithAFlatNormalAndTheDefaultMaterial()
    {
        ModelData model = Load(new GltfBuilder().AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2]));

        MeshData mesh = Assert.Single(model.Meshes);
        Assert.Equal(3, mesh.Vertices.Length);
        Assert.Equal(3, mesh.Indices.Length);
        Assert.All(mesh.Vertices, v => Assert.Equal(Vector3.UnitZ, v.Normal));      // counter-clockwise seen from +Z
        Assert.Equal(Vector3.Zero, mesh.BoundsMin);
        Assert.Equal(new Vector3(1, 1, 0), mesh.BoundsMax);

        MeshInstance instance = Assert.Single(model.Instances);
        Assert.Equal(Matrix4x4.Identity, instance.World);
        Assert.Equal("default", model.Materials[mesh.MaterialIndex].Name);
        Assert.Equal(new Vector3(1, 1, 0), model.BoundsMax);
    }

    [Fact]
    public void TheNodeHierarchyGivesEachInstanceItsWorldMatrix()
    {
        var builder = new GltfBuilder
        {
            Nodes = """
                [{"translation":[1,2,3],"children":[1]},
                 {"scale":[2,2,2],"mesh":0}]
                """,
        };
        ModelData model = Load(builder.AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2]));

        MeshInstance instance = Assert.Single(model.Instances);
        // Row vectors: scale first, then the parent's translation.
        AssertNear(Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(1, 2, 3), instance.World);
        Assert.Equal(new Vector3(1, 2, 3), model.BoundsMin);
        Assert.Equal(new Vector3(3, 4, 3), model.BoundsMax);
    }

    [Fact]
    public void AMeshUsedByTwoNodesIsLoadedOnce()
    {
        var builder = new GltfBuilder { Nodes = "[{\"mesh\":0,\"translation\":[0,0,0]},{\"mesh\":0,\"translation\":[5,0,0]}]", Scenes = "[{\"nodes\":[0,1]}]" };
        ModelData model = Load(builder.AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2]));

        Assert.Single(model.Meshes);
        Assert.Equal(2, model.Instances.Count);
        Assert.Equal(5f, model.Instances[1].World.Translation.X);
        Assert.Equal(new Vector3(6, 1, 0), model.BoundsMax);
    }

    [Fact]
    public void MaterialFactorsAndExtensionsAreRead()
    {
        var builder = new GltfBuilder
        {
            ExtensionsUsed = "[\"KHR_materials_emissive_strength\",\"KHR_materials_unlit\"]",
            Materials = """
                [{"name":"gold",
                  "pbrMetallicRoughness":{"baseColorFactor":[1,0.8,0.2,0.5],"metallicFactor":0.9,"roughnessFactor":0.3},
                  "emissiveFactor":[0.5,0.25,0],
                  "alphaMode":"MASK","alphaCutoff":0.3,"doubleSided":true,
                  "extensions":{"KHR_materials_emissive_strength":{"emissiveStrength":4},"KHR_materials_unlit":{}}}]
                """,
        };
        ModelData model = Load(builder.AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2], material: 0));

        MaterialData material = model.Materials[model.Meshes[0].MaterialIndex];
        Assert.Equal("gold", material.Name);
        Assert.Equal(new Vector4(1, 0.8f, 0.2f, 0.5f), material.BaseColorFactor);
        Assert.Equal(0.9f, material.MetallicFactor, 1e-5f);
        Assert.Equal(0.3f, material.RoughnessFactor, 1e-5f);
        Assert.Equal(new Vector3(2f, 1f, 0f), material.EmissiveFactor);            // emissive factor times the strength extension
        Assert.Equal(AlphaMode.Mask, material.AlphaMode);
        Assert.Equal(0.3f, material.AlphaCutoff, 1e-5f);
        Assert.True(material.DoubleSided);
        Assert.True(material.Unlit);
    }

    [Fact]
    public void TexturesAreDecodedInTheColorSpaceOfTheirUse()
    {
        string uri = GltfBuilder.PngUri(4, 4, (x, y) => ((byte)(x * 60), (byte)(y * 60), 128, 255));
        var builder = new GltfBuilder
        {
            ExtensionsUsed = "[\"KHR_texture_transform\"]",
            Images = $"[{{\"uri\":\"{uri}\"}}]",
            Samplers = "[{\"magFilter\":9728,\"minFilter\":9728,\"wrapS\":33071,\"wrapT\":33648}]",
            Textures = "[{\"source\":0,\"sampler\":0}]",
            Materials = """
                [{"pbrMetallicRoughness":{"baseColorTexture":{"index":0,"extensions":{"KHR_texture_transform":{"offset":[0.25,0.5],"scale":[2,3],"rotation":1.5}}}},
                  "normalTexture":{"index":0,"scale":0.7}}]
                """,
        };
        ModelData model = Load(builder.AddMesh(GltfBuilder.Quad, indices: [0, 1, 2, 0, 2, 3], uvs: [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], material: 0));

        MaterialData material = model.Materials[0];
        // The same image used as base color (sRGB) and as a normal map (linear) is two textures.
        Assert.Equal(2, model.Textures.Count);
        TextureData baseColor = model.Textures[material.BaseColorTexture!.Value.Texture];
        TextureData normal = model.Textures[material.NormalTexture!.Value.Texture];
        Assert.Equal(PixelFormat.R8G8B8A8Srgb, baseColor.Format);
        Assert.Equal(PixelFormat.R8G8B8A8Unorm, normal.Format);
        Assert.Equal(3, baseColor.Mips.Count);                                      // 4x4 -> 2x2 -> 1x1
        Assert.Equal(new SamplerData(TextureFilter.Nearest, false, TextureWrap.ClampToEdge, TextureWrap.MirroredRepeat), baseColor.Sampler);

        TextureRef reference = material.BaseColorTexture!.Value;
        Assert.Equal(new Vector2(0.25f, 0.5f), reference.Offset);
        Assert.Equal(new Vector2(2, 3), reference.Scale);
        Assert.Equal(1.5f, reference.Rotation, 1e-5f);
        Assert.True(reference.HasTransform);
        Assert.Equal(0.7f, material.NormalScale, 1e-5f);
    }

    [Fact]
    public void ANormalMappedMeshGetsTangentsFromItsUvs()
    {
        var builder = new GltfBuilder
        {
            Images = $"[{{\"uri\":\"{GltfBuilder.PngUri(2, 2, (_, _) => (128, 128, 255, 255))}\"}}]",
            Textures = "[{\"source\":0}]",
            Materials = "[{\"normalTexture\":{\"index\":0}}]",
        };
        Vector3[] normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        ModelData model = Load(builder.AddMesh(GltfBuilder.Quad, indices: [0, 1, 2, 0, 2, 3], normals: normals, uvs: [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], material: 0));

        // u grows along +X, v along +Y: the tangent is +X; the bitangent (cross(normal, tangent)) is +Y, so the handedness is +1.
        Assert.All(model.Meshes[0].Vertices, v =>
        {
            Assert.Equal(1f, v.Tangent.X, 1e-4f);
            Assert.Equal(0f, v.Tangent.Y, 1e-4f);
            Assert.Equal(1f, v.Tangent.W);
        });
    }

    [Fact]
    public void AFlippedUvMappingFlipsTheHandedness()
    {
        var builder = new GltfBuilder
        {
            Images = $"[{{\"uri\":\"{GltfBuilder.PngUri(2, 2, (_, _) => (128, 128, 255, 255))}\"}}]",
            Textures = "[{\"source\":0}]",
            Materials = "[{\"normalTexture\":{\"index\":0}}]",
        };
        Vector3[] normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        ModelData model = Load(builder.AddMesh(GltfBuilder.Quad, indices: [0, 1, 2, 0, 2, 3], normals: normals, uvs: [new(0, 1), new(1, 1), new(1, 0), new(0, 0)], material: 0));

        Assert.All(model.Meshes[0].Vertices, v => Assert.Equal(-1f, v.Tangent.W));
    }

    [Fact]
    public void DuplicateVerticesAreMergedUnlessOptimizationIsOff()
    {
        // A quad as two separate triangles: six vertices, four distinct.
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 0, 0), new(1, 1, 0), new(0, 1, 0)];
        Vector3[] normals = [.. Enumerable.Repeat(Vector3.UnitZ, 6)];

        ModelData merged = Load(new GltfBuilder().AddMesh(positions, normals: normals));
        ModelData raw = Load(new GltfBuilder().AddMesh(positions, normals: normals), new ContentOptions { OptimizeMeshes = false });

        Assert.Equal(4, merged.Meshes[0].Vertices.Length);
        Assert.Equal(6, merged.Meshes[0].Indices.Length);
        Assert.Equal(6, raw.Meshes[0].Vertices.Length);

        // The same two triangles, whatever the order.
        static string[] Triangles(MeshData m) => [.. Enumerable.Range(0, m.Indices.Length / 3).Select(t => string.Join('|', m.Indices[(t * 3)..(t * 3 + 3)].Select(i => m.Vertices[i].Position).Order(new PositionComparer()))).Order()];
        Assert.Equal(Triangles(raw.Meshes[0]), Triangles(merged.Meshes[0]));
    }

    private sealed class PositionComparer : System.Collections.Generic.IComparer<Vector3>
    {
        public int Compare(Vector3 a, Vector3 b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z);
    }

    [Fact]
    public void StripsAndFansBecomeTriangleLists()
    {
        Vector3[] four = GltfBuilder.Quad;
        Vector3[] normals = [.. Enumerable.Repeat(Vector3.UnitZ, 4)];       // with normals the vertices stay shared, so the indices are visible

        ModelData strip = Load(new GltfBuilder().AddMesh(four, normals: normals, mode: 5), new ContentOptions { OptimizeMeshes = false });
        ModelData fan = Load(new GltfBuilder().AddMesh(four, normals: normals, mode: 6), new ContentOptions { OptimizeMeshes = false });

        // A strip of four vertices is two triangles, the second one flipped so both face the same way; a fan shares vertex 0.
        Assert.Equal(new uint[] { 0, 1, 2, 2, 1, 3 }, strip.Meshes[0].Indices);
        Assert.Equal(new uint[] { 0, 1, 2, 0, 2, 3 }, fan.Meshes[0].Indices);
    }

    [Fact]
    public void LightsKeepTheirNodeTransforms()
    {
        var builder = new GltfBuilder
        {
            ExtensionsUsed = "[\"KHR_lights_punctual\"]",
            Nodes = """
                [{"mesh":0},
                 {"translation":[1,2,3],"extensions":{"KHR_lights_punctual":{"light":0}}},
                 {"rotation":[0.7071068,0,0,0.7071068],"extensions":{"KHR_lights_punctual":{"light":1}}}]
                """,
            Scenes = "[{\"nodes\":[0,1,2]}]",
            Extensions = """
                ,"extensions":{"KHR_lights_punctual":{"lights":[
                  {"name":"lamp","type":"point","color":[1,0.5,0.25],"intensity":50,"range":10},
                  {"name":"sun","type":"directional","intensity":3}]}}
                """,
        };
        ModelData model = Load(builder.AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2]));

        Assert.Equal(2, model.Lights.Count);
        LightData lamp = model.Lights.Single(l => l.Name == "lamp");
        Assert.Equal(LightKind.Point, lamp.Kind);
        Assert.Equal(new Vector3(1, 2, 3), lamp.Position);
        Assert.Equal(new Vector3(1, 0.5f, 0.25f), lamp.Color);
        Assert.Equal(50f, lamp.Intensity);
        Assert.Equal(10f, lamp.Range);

        // A light shines down its node's -Z; rotating the node 90 degrees about X turns that into +Y.
        LightData sun = model.Lights.Single(l => l.Name == "sun");
        Assert.Equal(LightKind.Directional, sun.Kind);
        Assert.Equal(0f, sun.Direction.X, 1e-4f);
        Assert.Equal(1f, sun.Direction.Y, 1e-4f);
        Assert.Equal(0f, sun.Direction.Z, 1e-4f);
        Assert.Equal(Vector3.One, sun.Color);
    }

    [Fact]
    public void FilesLoadWithTheirExternalBufferAndImage()
    {
        string directory = Directory.CreateTempSubdirectory("apogee-gltf").FullName;
        try
        {
            var builder = new GltfBuilder
            {
                Images = "[{\"uri\":\"tex%20ture.png\"}]",
                Textures = "[{\"source\":0}]",
                Materials = "[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}]",
            };
            string json = Encoding.UTF8.GetString(builder.AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2], material: 0).ToBytes());

            // Move the embedded buffer out into a .bin next to the file.
            int start = json.IndexOf("data:application/octet-stream;base64,", StringComparison.Ordinal);
            int end = json.IndexOf('"', start);
            byte[] bin = Convert.FromBase64String(json[(start + "data:application/octet-stream;base64,".Length)..end]);
            File.WriteAllBytes(Path.Combine(directory, "model.bin"), bin);
            File.WriteAllText(Path.Combine(directory, "model.gltf"), json[..start] + "model.bin" + json[end..]);
            File.WriteAllBytes(Path.Combine(directory, "tex ture.png"), Png.Encode(2, 2, new byte[16]));

            ModelData model = GltfLoader.Load(Path.Combine(directory, "model.gltf"));

            Assert.Equal("model", model.Name);
            Assert.Equal(3, model.Meshes[0].Vertices.Length);
            Assert.Single(model.Textures);
            Assert.Equal("tex ture.png", model.Textures[0].Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ModelsCanBeReadThroughACustomFileSource()
    {
        var builder = new GltfBuilder();
        byte[] file = builder.AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2]).ToBytes();
        string? asked = null;

        ModelData model = GltfLoader.Load("virtual/path/scene.gltf", new ContentOptions { ReadFile = path => { asked = path; return file; } });

        Assert.Equal("virtual/path/scene.gltf", asked);
        Assert.Equal("scene", model.Name);
        Assert.Single(model.Meshes);
    }

    [Fact]
    public void BrokenInputIsAContentExceptionThatSaysWhat()
    {
        Assert.Contains("not a readable glTF", Assert.Throws<ContentException>(() => GltfLoader.Load("junk"u8, "", null, "junk")).Message);
        Assert.Contains("missing.gltf", Assert.Throws<ContentException>(() => GltfLoader.Load(Path.Combine(Path.GetTempPath(), "apogee-does-not-exist", "missing.gltf"))).Message);

        var missingImage = new GltfBuilder
        {
            Images = "[{\"uri\":\"gone.png\"}]",
            Textures = "[{\"source\":0}]",
            Materials = "[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}]",
        };
        var error = Assert.Throws<ContentException>(() => Load(missingImage.AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2], material: 0)));
        Assert.Contains("gone.png", error.Message);

        Assert.Throws<ContentException>(() => Load(new GltfBuilder().AddMesh(GltfBuilder.Triangle, indices: [0, 1, 7])));
    }

    [Fact]
    public void PrimitivesThatAreNotTrianglesAreSkipped()
    {
        ModelData model = Load(new GltfBuilder().AddMesh(GltfBuilder.Triangle, mode: 1));

        Assert.Empty(model.Meshes);
        Assert.Empty(model.Instances);
    }

    [Fact]
    public void KtxTexturesInAModelAreTranscodedForTheGpu()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ktx2", "cyan_rgb_reference_basis.ktx2");
        string uri = "data:image/ktx2;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
        var builder = new GltfBuilder
        {
            ExtensionsUsed = "[\"KHR_texture_basisu\"]",
            Images = $"[{{\"uri\":\"{uri}\"}}]",
            Textures = "[{\"extensions\":{\"KHR_texture_basisu\":{\"source\":0}}}]",
            Materials = "[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}]",
        };

        ModelData bc = Load(builder.AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2], material: 0), new ContentOptions { TextureCaps = new TextureCaps(Bc: true, Astc: false) });

        Assert.Equal(PixelFormat.Bc7SrgbBlock, Assert.Single(bc.Textures).Format);
    }
}

internal static class MatrixExtensions
{
    public static float Get(this Matrix4x4 m, int index) => index switch
    {
        0 => m.M11, 1 => m.M12, 2 => m.M13, 3 => m.M14,
        4 => m.M21, 5 => m.M22, 6 => m.M23, 7 => m.M24,
        8 => m.M31, 9 => m.M32, 10 => m.M33, 11 => m.M34,
        12 => m.M41, 13 => m.M42, 14 => m.M43, _ => m.M44,
    };
}
