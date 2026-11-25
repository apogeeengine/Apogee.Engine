using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Weft.Content.Assets;
using Weft.Graphics.Shaders;
using Weft.Rendering.Shaders;
using Xunit;

namespace Weft.Rendering.Tests;

/// <summary>Weft.Graphics writes compiled shaders and cannot see the table of file formats (Weft.Content); the row of the table is what its file starts with.</summary>
public class ShaderFormatRowTests
{
    [Fact]
    public void ABundleStartsWithTheMagicAndVersionOfItsRow()
    {
        var shader = new CompiledShader("module", new Dictionary<string, byte[]> { ["vs"] = [1, 2, 3] }, "{}", "careful", [], fromCache: false);
        byte[] bundle = ShaderBundle.Write("module", [("", shader)]);

        Assert.Equal(AssetFormats.Shader.Magic, BinaryPrimitives.ReadUInt32LittleEndian(bundle));
        Assert.Equal(AssetFormats.Shader.Version, BinaryPrimitives.ReadInt32LittleEndian(bundle.AsSpan(4)));
        Assert.Same(AssetFormats.Shader, AssetFormats.FindMagic(bundle));
    }

    [Fact]
    public void TheShaderImporterTakesTheSlangRow()
    {
        Assert.Equal([AssetFormats.Slang.Extension], new ShaderImporter().Extensions);
    }
}
