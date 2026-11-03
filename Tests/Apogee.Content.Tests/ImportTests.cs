using Apogee.Core.Graphics;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Xunit;

namespace Apogee.Content.Tests;

public class ModelFileTests
{
    /// <summary>A model with a mesh that has normals, uvs and tangents, a material with a texture, a light and two instances.</summary>
    private static ModelData SampleModel()
    {
        var builder = new GltfBuilder
        {
            Images = "[{\"uri\":\"" + GltfBuilder.PngUri(8, 4, (x, y) => ((byte)(x * 30), (byte)(y * 60), 128, 255)) + "\"}]",
            Textures = "[{\"source\":0}]",
            Materials = "[{\"name\":\"paint\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.2,0.4,0.6,1],\"metallicFactor\":0.3,\"roughnessFactor\":0.7,\"baseColorTexture\":{\"index\":0}},\"doubleSided\":true,\"emissiveFactor\":[1,0,0]}]",
            Nodes = "[{\"mesh\":0},{\"mesh\":0,\"translation\":[2,0,0]}]",
            Scenes = "[{\"nodes\":[0,1]}]",
        };
        builder.AddMesh(GltfBuilder.Quad, indices: [0, 1, 2, 0, 2, 3], normals: [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], uvs: [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY], material: 0);
        return GltfLoader.Load(builder.ToBytes(), "", new ContentOptions { TextureCaps = TextureCaps.None }, "sample");
    }

    private static void AssertSame(ModelData expected, ModelData actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal((expected.BoundsMin, expected.BoundsMax), (actual.BoundsMin, actual.BoundsMax));
        Assert.Equal(expected.Meshes.Count, actual.Meshes.Count);
        for (int i = 0; i < expected.Meshes.Count; i++)
        {
            Assert.Equal(expected.Meshes[i].Vertices, actual.Meshes[i].Vertices);
            Assert.Equal(expected.Meshes[i].Indices, actual.Meshes[i].Indices);
            Assert.Equal((expected.Meshes[i].Name, expected.Meshes[i].MaterialIndex, expected.Meshes[i].BoundsMin, expected.Meshes[i].BoundsMax), (actual.Meshes[i].Name, actual.Meshes[i].MaterialIndex, actual.Meshes[i].BoundsMin, actual.Meshes[i].BoundsMax));
        }

        Assert.Equal(expected.Instances, actual.Instances);
        Assert.Equal(expected.Lights, actual.Lights);
        Assert.Equal(expected.Materials.Count, actual.Materials.Count);
        for (int i = 0; i < expected.Materials.Count; i++)
        {
            MaterialData a = expected.Materials[i];
            MaterialData b = actual.Materials[i];
            Assert.Equal((a.Name, a.BaseColorFactor, a.BaseColorTexture, a.MetallicFactor, a.RoughnessFactor, a.EmissiveFactor, a.AlphaMode, a.AlphaCutoff, a.DoubleSided, a.Unlit, a.NormalScale, a.OcclusionStrength),
                (b.Name, b.BaseColorFactor, b.BaseColorTexture, b.MetallicFactor, b.RoughnessFactor, b.EmissiveFactor, b.AlphaMode, b.AlphaCutoff, b.DoubleSided, b.Unlit, b.NormalScale, b.OcclusionStrength));
            Assert.Equal((a.MetallicRoughnessTexture, a.NormalTexture, a.OcclusionTexture, a.EmissiveTexture), (b.MetallicRoughnessTexture, b.NormalTexture, b.OcclusionTexture, b.EmissiveTexture));
        }

        Assert.Equal(expected.Textures.Count, actual.Textures.Count);
        for (int i = 0; i < expected.Textures.Count; i++)
        {
            TextureData a = expected.Textures[i];
            TextureData b = actual.Textures[i];
            Assert.Equal((a.Name, a.Format, a.Width, a.Height, a.Srgb, a.Sampler), (b.Name, b.Format, b.Width, b.Height, b.Srgb, b.Sampler));
            Assert.Equal(a.Mips.Count, b.Mips.Count);
            for (int level = 0; level < a.Mips.Count; level++)
            {
                Assert.Equal(a.Mips[level], b.Mips[level]);
            }
        }
    }

    [Fact]
    public void AModelComesBackBitForBit()
    {
        ModelData model = SampleModel();

        byte[] file = ModelFile.Write(model);
        ModelData back = ModelFile.Read(file);

        Assert.True(ModelFile.IsModelFile(file));
        AssertSame(model, back);
        Assert.Equal(2, back.Instances.Count);
        Assert.Single(back.Textures);
    }

    [Fact]
    public void TheFileIsDeterministic() => Assert.Equal(ModelFile.Write(SampleModel()), ModelFile.Write(SampleModel()));

    [Fact]
    public void AMeshThatIsNotATriangleListKeepsItsIndices()
    {
        var mesh = new MeshData("strip", [new Vertex(), new Vertex(), new Vertex(), new Vertex()], [0, 1, 2, 3], 0, Vector3.Zero, Vector3.One);
        var model = new ModelData { Name = "m", Meshes = [mesh], Instances = [new MeshInstance(0, Matrix4x4.Identity)], Materials = [], Textures = [], Lights = [], BoundsMin = Vector3.Zero, BoundsMax = Vector3.One };

        ModelData back = ModelFile.Read(ModelFile.Write(model));

        Assert.Equal(new uint[] { 0, 1, 2, 3 }, back.Meshes[0].Indices);
    }

    [Fact]
    public void WhatIsNotAModelOrIsDamagedIsAContentException()
    {
        byte[] file = ModelFile.Write(SampleModel());

        Assert.Contains("is not a model file", Assert.Throws<ContentException>(() => ModelFile.Read("junk junk"u8, "x.amodel")).Message);
        byte[] future = [.. file];
        future[4] = 9;
        Assert.Contains("version 9", Assert.Throws<ContentException>(() => ModelFile.Read(future, "x.amodel")).Message);
        Assert.Throws<ContentException>(() => ModelFile.Read(file.AsSpan(0, file.Length / 2), "cut.amodel"));
        byte[] damaged = [.. file];
        damaged[file.Length / 2] ^= 0xFF;
        try
        {
            ModelFile.Read(damaged, "damaged.amodel"); // a flipped byte may land in a float and still read; it must never crash otherwise
        }
        catch (ContentException)
        {
        }
    }
}

public class ImporterTests
{
    private sealed class Project : IDisposable
    {
        private readonly TempFolder _folder = new();

        public Project()
        {
            Files = new VirtualFileSystem();
            Files.Mount("res://", new DirectorySource(_folder.Root, writable: true));
        }

        public VirtualFileSystem Files { get; }

        public string Root => _folder.Root;

        public void Write(string path, byte[] bytes)
        {
            string full = Path.Combine(_folder.Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        public void Write(string path, string text) => Write(path, Encoding.UTF8.GetBytes(text));

        public ImportReport Import(bool force = false) => new AssetImporter(Files).Run(force);

        public void Dispose() => _folder.Dispose();
    }

    private static byte[] Png(int width, int height, int seed = 0)
    {
        byte[] rgba = new byte[width * height * 4];
        new Random(seed).NextBytes(rgba);
        return Apogee.Content.Png.Encode(width, height, rgba);
    }

    /// <summary>A glTF with an external buffer and an external texture, as a pair of files.</summary>
    private static (string Gltf, byte[] Bin) ModelWithFiles()
    {
        var builder = new GltfBuilder
        {
            Images = "[{\"uri\":\"wood.png\"}]",
            Textures = "[{\"source\":0}]",
            Materials = "[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}]",
        };
        string json = Encoding.UTF8.GetString(builder.AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2], material: 0).ToBytes());
        Match match = Regex.Match(json, "\"uri\":\"data:application/octet-stream;base64,([^\"]+)\"");
        return (json.Replace(match.Value, "\"uri\":\"car.bin\"", StringComparison.Ordinal), Convert.FromBase64String(match.Groups[1].Value));
    }

    private static void Car(Project project)
    {
        (string gltf, byte[] bin) = ModelWithFiles();
        project.Write("models/car.gltf", gltf);
        project.Write("models/car.bin", bin);
        project.Write("models/wood.png", Png(16, 16, 1));
    }

    [Fact]
    public void ModelsAndImagesBecomeArtifactsWithAnIndex()
    {
        using var project = new Project();
        Car(project);
        project.Write("textures/ground.png", Png(32, 16, 2));
        project.Write("readme.txt", "not an asset");

        ImportReport report = project.Import();

        Assert.True(report.Success);
        Assert.Equal(["models/car.gltf", "textures/ground.png"], report.Imported); // wood.png belongs to the model
        Assert.True(project.Files.FileExists("res://imported/models/car.gltf.amodel"));
        Assert.True(project.Files.FileExists("res://imported/textures/ground.png.ktx2"));
        Assert.False(project.Files.Exists("res://imported/models/wood.png.ktx2"));
        string index = project.Files.ReadAllText("res://imported/index.xml");
        Assert.Contains("source=\"models/car.gltf\"", index);
        Assert.Contains("<Dependency path=\"models/car.bin\"/>", index);
        Assert.Contains("<Dependency path=\"models/wood.png\"/>", index);

        TextureData ground = Ktx2Loader.Load("g", project.Files.ReadAllBytes("res://imported/textures/ground.png.ktx2"), true, TextureCaps.None, SamplerData.Default);
        Assert.Equal((32, 16, 6), (ground.Width, ground.Height, ground.Mips.Count));
    }

    [Fact]
    public void NothingIsImportedAgainWhileItsKeyIsTheSame()
    {
        using var project = new Project();
        Car(project);
        project.Import();
        DateTime written = File.GetLastWriteTimeUtc(Path.Combine(project.Root, "imported", "models", "car.gltf.amodel"));

        ImportReport second = project.Import();

        Assert.Empty(second.Imported);
        Assert.Equal(["models/car.gltf"], second.UpToDate);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(project.Root, "imported", "models", "car.gltf.amodel")));
        Assert.Equal(["models/car.gltf"], project.Import(force: true).Imported);
    }

    [Fact]
    public void ChangingTheSourceItsDependenciesOrItsSettingsImportsItAgain()
    {
        using var project = new Project();
        Car(project);
        project.Write("textures/ground.png", Png(8, 8, 2));
        project.Import();

        project.Write("models/wood.png", Png(16, 16, 99)); // a texture the model reads
        Assert.Equal(["models/car.gltf"], project.Import().Imported);

        project.Write("models/car.bin", File.ReadAllBytes(Path.Combine(project.Root, "models", "car.bin")).Reverse().ToArray()); // the buffer
        ImportReport buffer = project.Import();
        Assert.Contains("models/car.gltf", buffer.Imported.Concat(buffer.Failed.Select(f => f.Source)));

        project.Write("textures/ground.png", Png(8, 8, 3)); // the image itself
        Assert.Contains("textures/ground.png", project.Import().Imported);

        project.Write("textures/ground.png.import.xml", "<Import srgb=\"false\" mips=\"false\"/>"); // its settings
        ImportReport settings = project.Import();
        Assert.Equal(["textures/ground.png"], settings.Imported);
        TextureData ground = Ktx2Loader.Load("g", project.Files.ReadAllBytes("res://imported/textures/ground.png.ktx2"), false, TextureCaps.None, SamplerData.Default);
        Assert.Equal((Apogee.Core.Graphics.PixelFormat.R8G8B8A8Unorm, 1), (ground.Format, ground.Mips.Count));
    }

    [Fact]
    public void AnArtifactWhoseSourceIsGoneIsDeleted()
    {
        using var project = new Project();
        project.Write("textures/a.png", Png(8, 8, 1));
        project.Write("textures/b.png", Png(8, 8, 2));
        project.Import();

        File.Delete(Path.Combine(project.Root, "textures", "b.png"));
        ImportReport report = project.Import();

        Assert.Equal(["imported/textures/b.png.ktx2"], report.Removed);
        Assert.False(project.Files.Exists("res://imported/textures/b.png.ktx2"));
        Assert.True(project.Files.Exists("res://imported/textures/a.png.ktx2"));
        Assert.DoesNotContain("b.png", project.Files.ReadAllText("res://imported/index.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedImportIsReportedKeepsTheOldArtifactAndDoesNotStopTheRest()
    {
        using var project = new Project();
        project.Write("textures/good.png", Png(8, 8, 1));
        project.Write("textures/bad.png", Png(8, 8, 2));
        project.Import();
        string oldArtifact = project.Files.ReadAllText("res://imported/index.xml");

        project.Write("textures/bad.png", "this is not a png");
        project.Write("textures/good.png", Png(8, 8, 5));
        project.Write("textures/odd.png", Png(4, 4, 6));
        project.Write("textures/odd.png.import.xml", "<Import srgb=\"maybe\"/>");
        ImportReport report = project.Import();

        Assert.False(report.Success);
        Assert.Equal(["textures/bad.png", "textures/odd.png"], report.Failed.Select(f => f.Source).Order());
        Assert.Contains(report.Failed, f => f.Source == "textures/odd.png" && f.Message.Contains("'maybe' is not a boolean", StringComparison.Ordinal));
        Assert.Equal(["textures/good.png"], report.Imported);
        Assert.True(project.Files.Exists("res://imported/textures/bad.png.ktx2")); // what the game had is still there
        Assert.Contains("bad.png", project.Files.ReadAllText("res://imported/index.xml"), StringComparison.Ordinal);
        Assert.NotEqual("", oldArtifact);
    }

    [Fact]
    public void HiddenFoldersAndTheCacheItselfAreNotImported()
    {
        using var project = new Project();
        project.Write(".git/x.png", Png(4, 4));
        project.Write("imported/stray.png", Png(4, 4));
        project.Write("a/.hidden/y.png", Png(4, 4));
        project.Write("real.png", Png(4, 4));

        Assert.Equal(["real.png"], project.Import().Imported);
    }

    [Fact]
    public void TheLoaderTakesTheArtifactAndFallsBackToTheSource()
    {
        using var project = new Project();
        Car(project);
        project.Write("textures/ground.png", Png(16, 16, 4));
        project.Write("models/car.gltf.import.xml", "<Import compression=\"none\"/>"); // RGBA8, so that the textures come back exactly
        project.Write("textures/ground.png.import.xml", "<Import compression=\"none\"/>");
        var loader = new ContentLoader(project.Files);

        ModelData fromSource = loader.LoadModel("res://models/car.gltf", new ContentOptions { TextureCaps = TextureCaps.None });
        TextureData textureFromSource = loader.LoadTexture("res://textures/ground.png", srgb: true, TextureCaps.None);
        project.Import();
        loader.Refresh();
        ModelData imported = loader.LoadModel("res://models/car.gltf");
        TextureData textureImported = loader.LoadTexture("res://textures/ground.png", srgb: true, TextureCaps.None);

        Assert.Equal(fromSource.Meshes[0].Vertices, imported.Meshes[0].Vertices);
        Assert.Equal(fromSource.Meshes[0].Indices, imported.Meshes[0].Indices);
        Assert.Equal(fromSource.Textures[0].Mips[0], imported.Textures[0].Mips[0]);
        Assert.Equal(textureFromSource.Mips.Count, textureImported.Mips.Count);
        Assert.Equal(textureFromSource.Mips[0], textureImported.Mips[0]);

        // With the source gone only the artifact can be loaded: proof that it was the one used.
        File.Delete(Path.Combine(project.Root, "models", "car.gltf"));
        File.Delete(Path.Combine(project.Root, "textures", "ground.png"));
        Assert.Single(loader.LoadModel("res://models/car.gltf").Meshes);
        Assert.Equal(16, loader.LoadTexture("res://textures/ground.png", true, TextureCaps.None).Width);
    }

    [Fact]
    public void FilesThatWereNeverImportedLoadFromTheirSourcesWhereverTheyAre()
    {
        using var project = new Project();
        using var mod = new TempFolder();
        mod.Write("tex.png", "x");
        File.WriteAllBytes(Path.Combine(mod.Root, "tex.png"), Png(8, 8));
        project.Files.Mount("mod://cool", new DirectorySource(mod.Root));
        var loader = new ContentLoader(project.Files);

        Assert.Equal(8, loader.LoadTexture("mod://cool/tex.png", true, TextureCaps.None).Width);
        Assert.Contains("nothing.glb", Assert.Throws<ContentException>(() => loader.LoadModel("res://nothing.glb")).Message);
    }

    [Fact]
    public void ADamagedIndexIsAsGoodAsNoneAndEverythingIsImportedAgain()
    {
        using var project = new Project();
        project.Write("a.png", Png(4, 4));
        project.Import();
        project.Write("imported/index.xml", "<Imports><Import");

        Assert.Equal(["a.png"], project.Import().Imported);
    }

    [Fact]
    public void SettingsAreCheckedAgainstTheirSchema()
    {
        Assert.Equal(new ImportSettings(), ImportSettings.Parse("<Import/>", "x.import.xml"));
        ImportSettings settings = ImportSettings.Parse("<Import srgb=\"false\" mips=\"false\" optimize=\"false\"/>", "x.import.xml");
        Assert.Equal((false, false, false), (settings.Srgb, settings.Mips, settings.Optimize));
        Assert.Contains("'quality' is not an attribute of <Import>", Assert.Throws<ContentException>(() => ImportSettings.Parse("<Import quality=\"9\"/>", "x.import.xml")).Message);
    }
}
