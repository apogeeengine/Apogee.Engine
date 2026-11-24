using System;
using System.IO;
using Weft.Content;
using Weft.Content.FileSystem;
using Weft.Content.Import;
using Weft.Core.Assets;
using Xunit;

namespace Weft.Rendering.Tests;

/// <summary>How <see cref="ModelLibrary.Load"/> finds a model: by path, by <c>uid://</c>, and in a game that shipped only the imported artifacts. No GPU involved: ids exist before there is one.</summary>
public sealed class ModelLibraryPathTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("weft-model-paths").FullName;
    private readonly VirtualFileSystem _files = new();

    public ModelLibraryPathTests() => _files.Mount("res://", new DirectorySource(_folder));

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string path, string text)
    {
        string full = Path.Combine(_folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public void AUidAndAPathShareOneId()
    {
        var uid = AssetUid.New();
        Write("models/car.glb", "stands in for a model");
        Write("models/car.glb.import.xml", $"<Import uid=\"{uid}\"/>");
        using var library = new ModelLibrary(_files, new ContentLoader(_files));

        uint id = library.Load("res://models/car.glb");

        Assert.NotEqual(0u, id);
        Assert.Equal(id, library.Load(uid.ToString()));
        Assert.Equal("res://models/car.glb", library.PathOf(id));
        Assert.Throws<ContentException>(() => library.Load(AssetUid.New().ToString()));
        Assert.Throws<ContentException>(() => library.Load("uid://not-a-uid"));
    }

    [Fact]
    public void AnImportedModelLoadsByItsSourcePathWhenOnlyTheArtifactIsThere()
    {
        // What a cooked pack holds: the index and the artifact, not the source.
        Write("imported/index.xml", "<Imports version=\"3\"><Import source=\"models/boat.glb\" artifact=\"imported/models/boat.glb.apmodel\" key=\"k\"/></Imports>");
        Write("imported/models/boat.glb.apmodel", "artifact");
        using var library = new ModelLibrary(_files, new ContentLoader(_files));

        Assert.NotEqual(0u, library.Load("res://models/boat.glb"));
        Assert.Throws<FileNotFoundException>(() => library.Load("res://models/other.glb"));
    }
}
