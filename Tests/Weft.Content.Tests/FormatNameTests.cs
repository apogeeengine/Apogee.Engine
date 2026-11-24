using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Content.Import;
using Weft.Content.Packages;
using Weft.Content.Packs;
using Weft.Content.Xml;
using Weft.Core.Graphics;
using Xunit;

namespace Weft.Content.Tests;

/// <summary>The names of the engine's files: the w-prefixed extensions and the four-letter magics, and that the names they replaced are nothing now.</summary>
public class FormatNameTests
{
    private static string Ascii(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes[..4]);

    private sealed class DatImporter : IAssetImporter
    {
        public string Name => "dat";

        public IReadOnlyList<string> Extensions { get; } = [".dat"];

        public string Fingerprint => "1";

        public ImportResult? Import(ImportContext context) => new(context.Bytes.ToArray(), ".up");
    }

    [Fact]
    public void TheNewExtensionsAreTheEnginesNames()
    {
        Assert.Equal([".wscn", ".wprf"], AuthoredAssets.Extensions);
        Assert.Equal(".wimp", ImportSidecar.Suffix);
        Assert.Equal("game.wgam", GameManifest.FileName);
        Assert.True(AuthoredAssets.IsAuthored("res://scenes/start.wscn"));
        Assert.True(AuthoredAssets.IsAuthored("res://prefabs/lamp.WPRF"));
    }

    [Fact]
    public void OldExtensionsAreNotAssets()
    {
        Assert.False(AuthoredAssets.IsAuthored("res://scenes/start.ascene"));
        Assert.False(AuthoredAssets.IsAuthored("res://prefabs/lamp.aprefab"));
        Assert.False(AuthoredAssets.IsAuthored("res://scenes/start.xml"));

        // A game folder with the old manifest name has no manifest, and says nothing about it.
        using var folder = new TempFolder();
        folder.Write("game.xml", "<Game name=\"Old\" organization=\"Test\" version=\"1.0.0\"/>");
        Assert.Null(GameManifest.Load(folder.Root, out IReadOnlyList<string> problems));
        Assert.Empty(problems);
        folder.Write("game.wgam", "<Game name=\"New\" organization=\"Test\" version=\"1.0.0\"/>");
        Assert.Equal("New", GameManifest.Load(folder.Root, out _)?.Name);
    }

    [Fact]
    public void ModXmlIsNotAManifest()
    {
        using var res = new TempFolder();
        using var user = new TempFolder();
        res.Write("packages/old/mod.xml", "<Mod id=\"old\" version=\"1.0.0\"/>");
        res.Write("packages/current/mod.wmod", "<Mod id=\"current\" version=\"1.0.0\"/>");
        using (var archive = ZipFile.Open(Path.Combine(Directory.CreateDirectory(Path.Combine(user.Root, "mods")).FullName, "zipped_old.zip"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("mod.xml").Open());
            writer.Write("<Mod id=\"zipped_old\" version=\"1.0.0\"/>");
        }

        using (var archive = ZipFile.Open(Path.Combine(user.Root, "mods", "zipped_new.zip"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("mod.wmod").Open());
            writer.Write("<Mod id=\"zipped_new\" version=\"1.0.0\"/>");
        }

        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(res.Root));
        files.Mount("user://", new DirectorySource(user.Root, writable: true));

        using var set = PackageSet.Load(files);

        Assert.Equal(["current", "zipped_new"], set.LoadOrder.Select(p => p.Id));
        Assert.Contains(set.Problems, p => p.Contains("old has no mod.wmod", StringComparison.Ordinal));
        Assert.Contains(set.Problems, p => p.Contains("zipped_old.zip has no mod.wmod at its root", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOldSidecarSettingsFileIsJustAFile()
    {
        using var folder = new TempFolder();
        folder.Write("a.dat", "text");
        folder.Write("a.dat.import.xml", "<Import uid=\"uid://4k2m9x7q1w3ze\"/>");
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(folder.Root, writable: true));

        ImportReport report = new AssetImporter(files, [new DatImporter()]).Run();

        Assert.True(report.Success);
        Assert.Equal(["a.dat"], report.Imported);
        string sidecar = files.ReadAllText("res://a.dat.wimp");
        Assert.DoesNotContain("4k2m9x7q1w3ze", sidecar, StringComparison.Ordinal);
        Assert.Equal("<Import uid=\"uid://4k2m9x7q1w3ze\"/>", files.ReadAllText("res://a.dat.import.xml"));
        Assert.True(files.FileExists("res://imported/a.dat.up"));
    }

    [Fact]
    public void CookedXmlStartsWithWxmb()
    {
        byte[] cooked = XmlBinary.Write(XmlNode.Parse("<Scene version=\"1\"/>", "start.wscn"));

        Assert.Equal("WXMB", Ascii(cooked));
        Assert.True(XmlBinary.IsBinary(cooked));
        Assert.False(XmlBinary.IsBinary("APXB...."u8));
        Assert.Equal("Scene", XmlFile.Read(cooked, "start.wscn").Name);
    }

    [Fact]
    public void PackStartsWithWpak()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Root, "game.wpak");
        using (var writer = new PackWriter(path))
        {
            writer.Add("a.txt", "a"u8);
            writer.Finish();
        }

        Assert.Equal("WPAK", Ascii(File.ReadAllBytes(path)));
        using PackSource pack = PackSource.Open(path);
        Assert.Equal(1, pack.FileCount);
    }

    [Fact]
    public void ThePartsOfAPackAreNamedWithTheNewExtension()
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Root, "game.wpak");
        using (var writer = new PackWriter(path, new PackWriterOptions { SplitBytes = 20_000 }))
        {
            for (int i = 0; i < 8; i++)
            {
                byte[] noise = new byte[8_000];
                new Random(i).NextBytes(noise);
                writer.Add($"f{i}.bin", noise);
            }

            Assert.True(writer.Finish().Count >= 3);
        }

        Assert.True(File.Exists(Path.Combine(folder.Root, "game_001.wpak")));
        Assert.Equal([], Directory.EnumerateFiles(folder.Root).Where(f => !f.EndsWith(".wpak", StringComparison.Ordinal)));
        Assert.All(Directory.EnumerateFiles(folder.Root), f => Assert.Equal("WPAK", Ascii(File.ReadAllBytes(f))));
    }

    [Fact]
    public void ModelStartsWithWmdl()
    {
        var mesh = new MeshData("m", [new Vertex(), new Vertex(), new Vertex()], [0, 1, 2], 0, Vector3.Zero, Vector3.One);
        var model = new ModelData { Name = "m", Meshes = [mesh], Instances = [new MeshInstance(0, Matrix4x4.Identity)], Materials = [], Textures = [], Lights = [], BoundsMin = Vector3.Zero, BoundsMax = Vector3.One };

        byte[] file = ModelFile.Write(model);

        Assert.Equal("WMDL", Ascii(file));
        Assert.True(ModelFile.IsModelFile(file));
        Assert.False(ModelFile.IsModelFile([.. "AMDL"u8, .. file[4..]]));
    }
}
