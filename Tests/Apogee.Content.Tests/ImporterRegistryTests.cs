using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Apogee.Content.FileSystem;
using Apogee.Content.Import;
using Xunit;

namespace Apogee.Content.Tests;

/// <summary>The registry of importers: order, what an importer reads, no artifact, the fingerprint in the key, and the artifacts of a package.</summary>
public class ImporterRegistryTests
{
    /// <summary>Takes <c>.txt</c> files: the artifact is the text in capitals. A file that starts with "skip" is not an asset, and "include X" makes X a dependency.</summary>
    private sealed class UpperImporter(string fingerprint = "1", string extension = ".txt") : IAssetImporter
    {
        public List<string> Imported { get; } = [];

        public string Name => "upper";

        public IReadOnlyList<string> Extensions { get; } = [extension];

        public string Fingerprint { get; } = fingerprint;

        public ImportResult? Import(ImportContext context)
        {
            string text = Encoding.UTF8.GetString(context.Bytes);
            Imported.Add(context.Source);
            if (text.StartsWith("skip", StringComparison.Ordinal))
            {
                return null;
            }

            if (text.StartsWith("include ", StringComparison.Ordinal))
            {
                text += Encoding.UTF8.GetString(context.ReadFile("res://" + text["include ".Length..].Trim()));
            }

            return new ImportResult(Encoding.UTF8.GetBytes(text.ToUpperInvariant()), ".up");
        }
    }

    private sealed class Folder : IDisposable
    {
        private readonly TempFolder _folder = new();

        public Folder()
        {
            Files = new VirtualFileSystem();
            Files.Mount("res://", new DirectorySource(_folder.Root, writable: true));
        }

        public VirtualFileSystem Files { get; }

        public void Write(string path, string text)
        {
            string full = Path.Combine(_folder.Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        public void Dispose() => _folder.Dispose();
    }

    [Fact]
    public void AnImporterTakesItsExtensionsAndWritesAnArtifactWithAnEntry()
    {
        using var folder = new Folder();
        folder.Write("a.txt", "hello");
        folder.Write("b.md", "not taken");

        ImportReport report = new AssetImporter(folder.Files, [new UpperImporter()]).Run();

        Assert.Equal(["a.txt"], report.Imported);
        Assert.Equal("HELLO", folder.Files.ReadAllText("res://imported/a.txt.up"));
        Assert.Contains("source=\"a.txt\"", folder.Files.ReadAllText("res://imported/index.xml"));
    }

    [Fact]
    public void AnAssetThatIsNotOneHasNoArtifactAndNoEntryAndIsNotAskedAgainByTheIndex()
    {
        using var folder = new Folder();
        folder.Write("library.txt", "skip me");
        folder.Write("a.txt", "x");

        ImportReport report = new AssetImporter(folder.Files, [new UpperImporter()]).Run();

        Assert.Equal(["a.txt"], report.Imported);
        Assert.False(folder.Files.Exists("res://imported/library.txt.up"));
        Assert.DoesNotContain("library.txt", folder.Files.ReadAllText("res://imported/index.xml"));
    }

    [Fact]
    public void WhatAnEarlierImporterReadIsNotImportedOnItsOwnByALaterOne()
    {
        using var folder = new Folder();
        folder.Write("main.a", "include part.b");
        folder.Write("part.b", "part");
        var first = new UpperImporter(extension: ".a");
        var second = new UpperImporter(extension: ".b");

        ImportReport report = new AssetImporter(folder.Files, [first, second]).Run();

        Assert.Equal(["main.a"], report.Imported);
        Assert.Empty(second.Imported);
        Assert.Contains("<Dependency path=\"part.b\"/>", folder.Files.ReadAllText("res://imported/index.xml"));
    }

    [Fact]
    public void ChangingAnImportersFingerprintImportsEverythingItMadeAgain()
    {
        using var folder = new Folder();
        folder.Write("a.txt", "x");
        new AssetImporter(folder.Files, [new UpperImporter("1")]).Run();

        Assert.Equal(["a.txt"], new AssetImporter(folder.Files, [new UpperImporter("1")]).Run().UpToDate);
        Assert.Equal(["a.txt"], new AssetImporter(folder.Files, [new UpperImporter("2")]).Run().Imported);
    }

    [Fact]
    public void AnAssetsStatusSaysWhetherItWasImportedAndStillMatchesItsArtifact()
    {
        using var folder = new Folder();
        folder.Write("a.txt", "one");
        folder.Write("notes.md", "not an asset");
        folder.Write(".hidden.txt", "hidden");
        var importer = new AssetImporter(folder.Files, [new UpperImporter()]);

        Assert.Equal(AssetStatus.NotImported, importer.StatusOf("a.txt"));
        Assert.Equal(AssetStatus.NotImportable, importer.StatusOf("notes.md"));
        Assert.Equal(AssetStatus.NotImportable, importer.StatusOf(".hidden.txt"));
        Assert.Equal(AssetStatus.NotImportable, importer.StatusOf("missing.txt"));

        importer.Run();
        Assert.Equal(AssetStatus.UpToDate, importer.StatusOf("a.txt"));

        folder.Write("a.txt", "two");                                    // the source changed
        Assert.Equal(AssetStatus.Stale, importer.StatusOf("a.txt"));
        importer.Run();
        Assert.Equal(AssetStatus.UpToDate, importer.StatusOf("a.txt"));

        folder.Write("a.txt.import.xml", "<Import/>");                   // so did its settings
        Assert.Equal(AssetStatus.Stale, importer.StatusOf("a.txt"));

        folder.Files.Delete("res://imported/a.txt.up");                  // the artifact is gone
        Assert.Equal(AssetStatus.NotImported, importer.StatusOf("a.txt"));
    }

    [Fact]
    public void OneAssetCanBeImportedAgainWithoutTouchingTheRest()
    {
        using var folder = new Folder();
        folder.Write("a.txt", "one");
        folder.Write("b.txt", "two");
        var upper = new UpperImporter();
        var importer = new AssetImporter(folder.Files, [upper]);
        importer.Run();
        upper.Imported.Clear();

        ImportReport report = importer.Reimport("a.txt");

        Assert.Equal(["a.txt"], report.Imported);
        Assert.Equal(["a.txt"], upper.Imported);                         // b.txt was not read again
        Assert.Equal(AssetStatus.UpToDate, importer.StatusOf("b.txt"));
        Assert.Equal(2, AssetImporter.LoadIndex(folder.Files).Count);

        ImportReport refused = importer.Reimport("notes.md");
        Assert.Equal("notes.md", Assert.Single(refused.Failed).Source);
    }

    [Fact]
    public void APackagesImportedFolderReplacesItsSourcesForTheLoader()
    {
        using var mod = new TempFolder();
        var modFiles = new VirtualFileSystem();
        modFiles.Mount("res://", new DirectorySource(mod.Root, writable: true));
        byte[] png = Png.Encode(4, 4, new byte[4 * 4 * 4]);
        File.WriteAllBytes(Path.Combine(mod.Root, "tex.png"), png);
        Assert.True(new AssetImporter(modFiles).Run().Success);
        File.Delete(Path.Combine(mod.Root, "tex.png")); // only the artifact is shipped

        var game = new VirtualFileSystem();
        game.Mount("mod://cool", new DirectorySource(mod.Root));
        var loader = new ContentLoader(game);

        Assert.Equal("mod://cool/imported/tex.png.ktx2", loader.ArtifactPathFor("mod://cool/tex.png"));
        Assert.Equal(4, loader.LoadTexture("mod://cool/tex.png", true, TextureCaps.None).Width);
        Assert.Null(loader.ArtifactPathFor("mod://other/tex.png"));
    }

    [Fact]
    public void ShaderVariantsInTheSidecarAreCheckedAgainstTheSchema()
    {
        ImportSettings settings = ImportSettings.Parse("<Import><Variant><Define name=\"FAST\"/><Define name=\"QUALITY\" value=\"3\"/></Variant><Variant/></Import>", "x.import.xml");

        Assert.Equal(2, settings.Variants.Count);
        Assert.Equal(new Dictionary<string, string> { ["FAST"] = "1", ["QUALITY"] = "3" }, settings.Variants[0].Defines);
        Assert.Empty(settings.Variants[1].Defines);
        Assert.Throws<ContentException>(() => ImportSettings.Parse("<Import><Variant><Define value=\"1\"/></Variant></Import>", "x.import.xml")); // a define needs a name
    }
}
