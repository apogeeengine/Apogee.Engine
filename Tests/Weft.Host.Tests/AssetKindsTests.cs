using System;
using Weft.Ecs;
using Weft.Host.Editing;
using Weft.Native.Tests;
using Weft.Reflection;
using Xunit;
using Flecs = Weft.Native.Flecs.Ecs;

namespace Weft.Host.Tests;

/// <summary>A component with an asset field, for a scene that names a model that is not there.</summary>
[Component]
public struct Dressed
{
    public int Count;

    [Property(Asset = "model")]
    public uint Model;
}

/// <summary>Which files are which kind of asset, and the edit that drops the text of a reference that did not load.</summary>
public sealed class AssetKindsTests
{
    [Fact]
    public void TheEngineKindsAreRegisteredWithTheExtensionsOfTheirImporters()
    {
        Assert.Equal([".glb", ".gltf"], AssetKinds.ExtensionsOf("model"));
        Assert.Equal([".png", ".jpg", ".jpeg"], AssetKinds.ExtensionsOf("texture"));
        Assert.Equal([".ascene"], AssetKinds.ExtensionsOf("scene"));
        Assert.Empty(AssetKinds.ExtensionsOf("kindnobodyregistered"));
        Assert.Equal("Model", AssetKinds.LabelOf("model"));
        Assert.Equal("kindnobodyregistered", AssetKinds.LabelOf("kindnobodyregistered"));
        Assert.Contains(AssetKinds.All, k => k.Type == "texture");
    }

    [Fact]
    public void AFileHasTheKindOfItsExtensionWhateverTheCase()
    {
        Assert.Equal("model", AssetKinds.TypeOfPath("res://models/car.glb"));
        Assert.Equal("model", AssetKinds.TypeOfPath("mod://id/CAR.GLTF"));
        Assert.Equal("texture", AssetKinds.TypeOfPath(@"C:\game\wall.Png"));
        Assert.Equal("scene", AssetKinds.TypeOfPath("level.ascene"));
        Assert.Null(AssetKinds.TypeOfPath("res://models/car.glb.import.xml"));     // a sidecar is no asset
        Assert.Null(AssetKinds.TypeOfPath("res://models/car"));
        Assert.Null(AssetKinds.TypeOfPath("res://models/.glb"));                    // a name that only starts with a dot has no extension
        Assert.Null(AssetKinds.TypeOfPath("res://dir.glb/car"));
        Assert.Null(AssetKinds.TypeOfPath("res://notes.txt"));
    }

    [Fact]
    public void AKindCanBeRegisteredAndReplacedButItsExtensionsBelongToOneType()
    {
        int before = AssetKinds.Version;
        AssetKinds.Register("kindtest", [".KindTest", ".kt2"], "Kind test");
        Assert.True(AssetKinds.Version > before);
        Assert.Equal([".kindtest", ".kt2"], AssetKinds.ExtensionsOf("kindtest"));
        Assert.Equal("kindtest", AssetKinds.TypeOfPath("a/b.KINDTEST"));
        Assert.Equal("Kind test", AssetKinds.LabelOf("kindtest"));

        // Registering the type again replaces it: the old extension is free.
        AssetKinds.Register("kindtest", [".kt3"], "Kind test");
        Assert.Null(AssetKinds.TypeOfPath("a/b.kindtest"));
        Assert.Equal("kindtest", AssetKinds.TypeOfPath("a/b.kt3"));

        // An extension of another type is refused, and nothing of the attempt is kept.
        InvalidOperationException clash = Assert.Throws<InvalidOperationException>(() => AssetKinds.Register("kindclash", [".kt4", ".glb"], "Clash"));
        Assert.Contains("model", clash.Message, StringComparison.Ordinal);
        Assert.Empty(AssetKinds.ExtensionsOf("kindclash"));
        Assert.Null(AssetKinds.TypeOfPath("a/b.kt4"));
        Assert.Equal("model", AssetKinds.TypeOfPath("a/b.glb"));

        Assert.Throws<ArgumentException>(() => AssetKinds.Register("kindbad", ["kt5"], "Bad"));
        Assert.Throws<ArgumentException>(() => AssetKinds.Register("kindbad", ["."], "Bad"));
        Assert.Throws<ArgumentException>(() => AssetKinds.Register("", [".kt6"], "Bad"));
        Assert.DoesNotContain(AssetKinds.All, k => k.Type == "kindbad");
    }

    [Fact]
    public void ForgettingTheTextOfAReferenceThatDidNotLoadIsUndoable()
    {
        NativeRequirements.Require(typeof(Flecs).Assembly, "flecs");
        using var world = new World();
        world.RegisterAssetType("model", _ => null, _ => 0);
        Entity lost = world.LoadScene("<Scene version=\"1\"><Entity id=\"1\" name=\"lost\"><Dressed Count=\"7\" Model=\"res://models/gone.glb\"/></Entity></Scene>")[0];
        var history = new EditHistory();

        Assert.Null(UnresolvedAssetCommand.Forget(world, lost, "Dressed", "Count", "forget"));      // nothing to forget
        UnresolvedAssetCommand forget = UnresolvedAssetCommand.Forget(world, lost, "Dressed", "Model", "forget")!;
        history.Execute(forget);
        Assert.Null(world.UnresolvedAssetOf(lost, "Dressed", "Model"));
        Assert.DoesNotContain("gone.glb", world.SaveScene(), StringComparison.Ordinal);

        history.Undo();
        Assert.Equal("res://models/gone.glb", world.UnresolvedAssetOf(lost, "Dressed", "Model"));
        Assert.Contains("gone.glb", world.SaveScene(), StringComparison.Ordinal);

        history.Redo();
        Assert.Null(world.UnresolvedAssetOf(lost, "Dressed", "Model"));
        Assert.Equal(["forget"], history.Steps);
    }
}
