using Apogee.Core.Assets;
using Apogee.Reflection;
using Apogee.Scripting;
using Xunit;

namespace Apogee.Scripting.Tests;

/// <summary>A class with asset references, compiled by the real generator: the generated code builds under the AOT analyzers with warnings as errors.</summary>
[ApogeeClass(Creatable = true)]
public partial class Gallery : EngineObject
{
    /// <summary>The picture on the wall.</summary>
    [Property]
    public AssetRef Picture { get; set; }

    [Property]
    public AssetRef? Spare { get; set; }

    [Method]
    public AssetRef Pick(AssetRef first, AssetRef? second = null) => second ?? first;
}

public class AssetRefBridgeTests
{
    private const string Uid = "uid://4k2m9x7q1w3ze";

    [Fact]
    public void AnAssetRefIsAStringToScripts()
    {
        using var f = new VmFixture();
        var gallery = new Gallery();
        f.Vm.SetGlobal("gallery", gallery);

        Assert.Equal("", f.Run("return gallery.picture")[0].ToStringValue());
        Assert.True(f.Run("return gallery.spare == nil")[0].ToBool());

        f.Run($"gallery.picture = 'res://models/car.glb|{Uid}' gallery.spare = '{Uid}'");
        Assert.Equal(new AssetRef("res://models/car.glb", AssetUid.Parse(Uid)), gallery.Picture);
        Assert.Equal(AssetRef.FromUid(AssetUid.Parse(Uid)), gallery.Spare);
        Assert.Equal($"res://models/car.glb|{Uid}", f.Run("return gallery.picture")[0].ToStringValue());
        Assert.Equal(Uid, f.Run("return gallery.spare")[0].ToStringValue());

        f.Run("gallery.spare = nil");
        Assert.Null(gallery.Spare);
        Assert.Equal("res://a.png", f.Run("return gallery:pick('res://a.png')")[0].ToStringValue());
        Assert.Equal("res://b.png", f.Run("return gallery:pick('res://a.png', 'res://b.png')")[0].ToStringValue());
    }

    [Fact]
    public void ABadAssetReferenceIsAScriptErrorAndChangesNothing()
    {
        using var f = new VmFixture();
        var gallery = new Gallery { Picture = AssetRef.FromPath("res://keep.glb") };
        f.Vm.SetGlobal("gallery", gallery);

        Assert.Contains("is not an asset reference", f.Fails($"gallery.picture = '|{Uid}'").Message);
        Assert.Contains("is not an asset reference", f.Fails("gallery.picture = 'uid://not-a-uid'").Message);
        Assert.Equal("res://keep.glb", gallery.Picture.ToString());
    }

    [Fact]
    public void TheDefinitionsSayString()
    {
        using (var vm = new VmFixture())
        {
            LuauVm.RegisterEcsNatives();
        }

        string text = DefinitionWriter.Write([TypeRegistry.GetClass("EngineObject"), TypeRegistry.GetClass("Gallery")], [], []).ReplaceLineEndings("\n");
        Assert.Contains("    --- The picture on the wall.\n    picture: string\n    spare: string?\n", text);
        Assert.Contains("function pick(self, first: string, second: string?): string", text);
    }
}
