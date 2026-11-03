using Apogee.Core.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Xunit;

namespace Apogee.Content.Tests;

/// <summary>Textures cooked to Basis Universal UASTC in KTX 2, and what they come back as.</summary>
public class TextureCompressionTests
{
    /// <summary>A smooth image with some detail, the kind UASTC is for (noise is its worst case).</summary>
    private static byte[] Gradient(int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                rgba[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                rgba[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                rgba[i + 2] = (byte)(((x / 8) + (y / 8)) % 2 == 0 ? 200 : 60);
                rgba[i + 3] = 255;
            }
        }

        return rgba;
    }

    private static TextureData Texture(int width, int height, bool srgb)
    {
        byte[] level0 = Gradient(width, height);
        return new TextureData("gradient", srgb ? PixelFormat.R8G8B8A8Srgb : PixelFormat.R8G8B8A8Unorm, width, height, ImageDecoder.BuildMipChain(level0, width, height, srgb), srgb, SamplerData.Default);
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (i % 4 != 3)
            {
                sum += Math.Abs(a[i] - b[i]);
            }
        }

        return sum / (a.Length * 0.75);
    }

    private static int BlockBytes(int width, int height) => ((width + 3) / 4) * ((height + 3) / 4) * 16;

    [Theory]
    [InlineData(64, 32)]
    [InlineData(37, 91)]
    public void AUastcTextureIsTranscodedToWhateverTheGpuSamplesAndStaysClose(int width, int height)
    {
        TextureData source = Texture(width, height, srgb: true);
        byte[] file = BasisEncoder.Encode(source, normalMap: false);

        TextureData rgba = Ktx2Loader.Load("t", file, true, TextureCaps.None, SamplerData.Default);
        TextureData bc = Ktx2Loader.Load("t", file, true, new TextureCaps(Bc: true, Astc: true), SamplerData.Default);
        TextureData astc = Ktx2Loader.Load("t", file, true, new TextureCaps(Bc: false, Astc: true), SamplerData.Default);

        Assert.Equal(PixelFormat.R8G8B8A8Srgb, rgba.Format);
        Assert.Equal(PixelFormat.Bc7SrgbBlock, bc.Format);
        Assert.Equal(PixelFormat.Astc4X4SrgbBlock, astc.Format);
        foreach (TextureData texture in new[] { rgba, bc, astc })
        {
            Assert.Equal((width, height, source.Mips.Count), (texture.Width, texture.Height, texture.Mips.Count));
        }

        for (int level = 0; level < source.Mips.Count; level++)
        {
            int w = Math.Max(1, width >> level);
            int h = Math.Max(1, height >> level);
            Assert.Equal(BlockBytes(w, h), bc.Mips[level].Length);
            Assert.Equal(BlockBytes(w, h), astc.Mips[level].Length);
            double difference = MeanDifference(source.Mips[level], rgba.Mips[level]);
            Assert.True(difference < (level == 0 ? 3 : 20), $"level {level} differs by {difference:F2}"); // the checkered detail of small levels is what 4x4 blocks lose first
        }

        Assert.True(file.Length < source.Mips.Sum(m => m.Length) / 3, "UASTC with RDO and zstd is much smaller than RGBA8");
    }

    [Fact]
    public void TheColorSpaceIsInTheFileAndNotInTheCaller()
    {
        byte[] srgb = BasisEncoder.Encode(Texture(16, 16, srgb: true), normalMap: false);
        byte[] linear = BasisEncoder.Encode(Texture(16, 16, srgb: false), normalMap: false);
        byte[] rgba8 = Ktx2Writer.Write(Texture(16, 16, srgb: true), Ktx2Writer.ImportLevel);

        Assert.True(Ktx2Loader.IsSrgb("a", srgb));
        Assert.False(Ktx2Loader.IsSrgb("b", linear));
        Assert.True(Ktx2Loader.IsSrgb("c", rgba8));
        Assert.Equal(PixelFormat.Bc7UnormBlock, Ktx2Loader.Load("b", linear, false, new TextureCaps(Bc: true, Astc: false), SamplerData.Default).Format);
        Assert.Throws<ContentException>(() => Ktx2Loader.IsSrgb("x", new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void ANormalMapIsEncodedAccuratelyAndIsDeterministic()
    {
        TextureData texture = Texture(32, 32, srgb: false);

        byte[] normal = BasisEncoder.Encode(texture, normalMap: true);
        byte[] color = BasisEncoder.Encode(texture, normalMap: false);

        Assert.Equal(normal, BasisEncoder.Encode(texture, normalMap: true));
        Assert.Equal(color, BasisEncoder.Encode(texture, normalMap: false));
        Assert.NotEqual(normal, color);
        double normalError = MeanDifference(texture.Mips[0], Ktx2Loader.Load("n", normal, false, TextureCaps.None, SamplerData.Default).Mips[0]);
        double colorError = MeanDifference(texture.Mips[0], Ktx2Loader.Load("c", color, false, TextureCaps.None, SamplerData.Default).Mips[0]);
        Assert.True(normalError <= colorError, $"normal map error {normalError:F3} against color {colorError:F3}");
    }

    [Fact]
    public void WhatIsNotRgba8IsRefused()
    {
        var bc = new TextureData("bc", PixelFormat.Bc7UnormBlock, 4, 4, [new byte[16]], false, SamplerData.Default);
        var wrong = new TextureData("wrong", PixelFormat.R8G8B8A8Unorm, 8, 8, [new byte[10]], false, SamplerData.Default);

        Assert.Contains("only RGBA8", Assert.Throws<ContentException>(() => BasisEncoder.Encode(bc, false)).Message);
        Assert.Contains("level 0", Assert.Throws<ContentException>(() => BasisEncoder.Encode(wrong, false)).Message);
    }

    private sealed class Project : IDisposable
    {
        private readonly TempFolder _folder = new();

        public Project()
        {
            Files = new VirtualFileSystem();
            Files.Mount("res://", new DirectorySource(_folder.Root, writable: true));
        }

        public VirtualFileSystem Files { get; }

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

    private static uint VkFormatOf(byte[] ktx2) => BitConverter.ToUInt32(ktx2, 12);

    [Fact]
    public void ImagesAreImportedAsUastcAndTheSidecarChoosesTheRest()
    {
        using var project = new Project();
        project.Write("color.png", Png.Encode(40, 24, Gradient(40, 24)));
        project.Write("rough.png", Png.Encode(40, 24, Gradient(40, 24)));
        project.Write("rough.png.import.xml", "<Import srgb=\"false\"/>");
        project.Write("normal.png", Png.Encode(40, 24, Gradient(40, 24)));
        project.Write("normal.png.import.xml", "<Import normalMap=\"true\"/>");
        project.Write("plain.png", Png.Encode(40, 24, Gradient(40, 24)));
        project.Write("plain.png.import.xml", "<Import compression=\"none\"/>");
        project.Write("tiny.png", Png.Encode(2, 2, Gradient(2, 2)));

        Assert.True(project.Import().Success);

        byte[] Artifact(string name) => project.Files.ReadAllBytes($"res://imported/{name}.png.ktx2");
        Assert.True(Ktx2Loader.IsSrgb("color", Artifact("color")));
        Assert.Equal(0u, VkFormatOf(Artifact("color")));  // Basis data has no vkFormat
        Assert.False(Ktx2Loader.IsSrgb("rough", Artifact("rough")));
        Assert.False(Ktx2Loader.IsSrgb("normal", Artifact("normal")));  // a normal map is linear whatever srgb says
        Assert.Equal(0u, VkFormatOf(Artifact("normal")));
        Assert.Equal(43u, VkFormatOf(Artifact("plain")));  // RGBA8, as before
        Assert.Equal(43u, VkFormatOf(Artifact("tiny")));  // smaller than a block: not worth compressing
        Assert.True(Artifact("color").Length < Artifact("plain").Length);
        Assert.Equal(Artifact("color"), BasisEncoder.Encode(ImageDecoder.Decode("color.png", Png.Encode(40, 24, Gradient(40, 24)), true, SamplerData.Default, true), false));
    }

    [Fact]
    public void ChangingTheCompressionInTheSidecarImportsAgain()
    {
        using var project = new Project();
        project.Write("a.png", Png.Encode(16, 16, Gradient(16, 16)));
        project.Import();
        Assert.Equal(["a.png"], project.Import().UpToDate);

        project.Write("a.png.import.xml", "<Import compression=\"none\"/>");

        Assert.Equal(["a.png"], project.Import().Imported);
        Assert.Equal(43u, VkFormatOf(project.Files.ReadAllBytes("res://imported/a.png.ktx2")));
    }

    [Fact]
    public void AnImportedTextureLoadsForTheGpuItIsAskedFor()
    {
        using var project = new Project();
        project.Write("ground.png", Png.Encode(32, 32, Gradient(32, 32)));
        project.Import();
        var loader = new ContentLoader(project.Files);

        TextureData bc = loader.LoadTexture("res://ground.png", srgb: false, new TextureCaps(Bc: true, Astc: false));
        TextureData none = loader.LoadTexture("res://ground.png", srgb: false, TextureCaps.None);

        Assert.Equal(PixelFormat.Bc7SrgbBlock, bc.Format); // the file says sRGB, whatever the caller thought
        Assert.Equal(PixelFormat.R8G8B8A8Srgb, none.Format);
        Assert.True(none.Srgb);
    }

    private static ModelData ModelWithNormalMap()
    {
        string Image(int seed) => GltfBuilder.PngUri(16, 16, (x, y) => ((byte)(x * 16), (byte)(y * 16), (byte)(seed * 40), 255));
        var builder = new GltfBuilder
        {
            Images = $"[{{\"uri\":\"{Image(1)}\"}},{{\"uri\":\"{Image(2)}\"}},{{\"uri\":\"{Image(3)}\"}}]",
            Textures = "[{\"source\":0},{\"source\":1},{\"source\":2}]",
            Materials = "[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicRoughnessTexture\":{\"index\":1}},\"normalTexture\":{\"index\":2}}]",
        };
        builder.AddMesh(GltfBuilder.Quad, indices: [0, 1, 2, 0, 2, 3], normals: [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], uvs: [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY], material: 0);
        return GltfLoader.Load(builder.ToBytes(), "", new ContentOptions { TextureCaps = TextureCaps.None }, "pbr");
    }

    [Fact]
    public void AModelsTexturesAreCompressedAndKeepTheirColorSpaces()
    {
        ModelData model = ModelWithNormalMap();
        byte[] plain = ModelFile.Write(model);
        byte[] compressed = ModelFile.Write(model, TextureCompression.Uastc);

        ModelData none = ModelFile.Read(compressed, "m");
        ModelData bc = ModelFile.Read(compressed, "m", new TextureCaps(Bc: true, Astc: true));

        Assert.True(compressed.Length < plain.Length);
        Assert.Equal(model.Textures.Select(t => t.Srgb), bc.Textures.Select(t => t.Srgb));
        Assert.Equal([PixelFormat.Bc7SrgbBlock, PixelFormat.Bc7UnormBlock, PixelFormat.Bc7UnormBlock], bc.Textures.Select(t => t.Format));
        Assert.Equal([PixelFormat.R8G8B8A8Srgb, PixelFormat.R8G8B8A8Unorm, PixelFormat.R8G8B8A8Unorm], none.Textures.Select(t => t.Format));
        Assert.True(MeanDifference(model.Textures[0].Mips[0], none.Textures[0].Mips[0]) < 4);
        Assert.Equal(model.Meshes[0].Vertices, none.Meshes[0].Vertices);
    }

    [Fact]
    public void TheLoaderTranscodesAnImportedModelForTheCapsInItsOptions()
    {
        using var project = new Project();
        project.Write("pbr.glb", ModelGlb());
        Assert.True(project.Import().Success);
        var loader = new ContentLoader(project.Files);

        ModelData bc = loader.LoadModel("res://pbr.glb", new ContentOptions { TextureCaps = new TextureCaps(Bc: true, Astc: false) });
        ModelData plain = loader.LoadModel("res://pbr.glb");

        Assert.All(bc.Textures, t => Assert.True(t.Format is PixelFormat.Bc7SrgbBlock or PixelFormat.Bc7UnormBlock));
        Assert.All(plain.Textures, t => Assert.True(t.Format is PixelFormat.R8G8B8A8Srgb or PixelFormat.R8G8B8A8Unorm));
    }

    private static byte[] ModelGlb()
    {
        var builder = new GltfBuilder
        {
            Images = $"[{{\"uri\":\"{GltfBuilder.PngUri(16, 16, (x, y) => ((byte)(x * 16), (byte)(y * 16), 90, 255))}\"}}]",
            Textures = "[{\"source\":0}]",
            Materials = "[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}]",
        };
        builder.AddMesh(GltfBuilder.Triangle, indices: [0, 1, 2], material: 0);
        return builder.ToBytes();
    }
}
