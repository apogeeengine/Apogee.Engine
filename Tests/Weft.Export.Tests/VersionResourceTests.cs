using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Weft.Content.Packages;
using Weft.Export.Windows;
using Xunit;

namespace Weft.Export.Tests;

/// <summary>The <c>EngineVersion</c> string of the version resource: the engine an executable is made of, beside the game's own versions.</summary>
public class VersionResourceTests
{
    private static GameManifest Manifest()
    {
        Assert.True(GameManifest.TryParse("""<Game name="Fixture Game" organization="Test Studio" version="1.2.3" executable="FixtureGame"/>""", "game.wgam", out GameManifest? manifest, out IReadOnlyList<string> problems), string.Join("\n", problems));
        return manifest!;
    }

    /// <summary>Whether the resource has a String block with this key and value: both NUL-terminated UTF-16, with up to one NUL of padding to 4 bytes between them.</summary>
    private static bool HasString(byte[] resource, string key, string value) =>
        Regex.IsMatch(Encoding.Unicode.GetString(resource), Regex.Escape(key) + "\0\0?" + Regex.Escape(value) + "\0", RegexOptions.CultureInvariant);

    [Fact]
    public void TheResourceHasAnEngineVersionStringWithTheEnginesVersion() =>
        Assert.True(HasString(VersionResource.Build(Manifest()), "EngineVersion", Weft.Core.EngineVersion.Text));

    [Fact]
    public void TheGamesOwnVersionsAreNotTheEngines()
    {
        byte[] resource = VersionResource.Build(Manifest());

        Assert.True(HasString(resource, "FileVersion", "1.2.3.0"));
        Assert.True(HasString(resource, "ProductVersion", "1.2.3.0"));
        Assert.False(HasString(resource, "FileVersion", Weft.Core.EngineVersion.Text));
    }
}
