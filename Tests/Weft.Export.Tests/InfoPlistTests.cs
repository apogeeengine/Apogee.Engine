using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;
using Weft.Content.Packages;
using Xunit;

namespace Weft.Export.Tests;

public class InfoPlistTests
{
    private static GameManifest Manifest(string attributes = "")
    {
        string xml = $"""<Game name="Fixture Game" organization="Test Studio" version="1.2.3" executable="FixtureGame" identifier="org.test.fixturegame" {attributes}/>""";
        Assert.True(GameManifest.TryParse(xml, "game.wgam", out GameManifest? manifest, out IReadOnlyList<string> problems), string.Join("\n", problems));
        return manifest!;
    }

    /// <summary>The dictionary of a plist as keys and values: a string as its text, <c>true</c> and <c>false</c> as booleans.</summary>
    private static Dictionary<string, object> Read(string plist)
    {
        XDocument document = XDocument.Parse(plist);
        Assert.Equal("plist", document.Root!.Name.LocalName);
        Assert.Equal("1.0", (string?)document.Root.Attribute("version"));
        XElement dict = Assert.Single(document.Root.Elements());
        Assert.Equal("dict", dict.Name.LocalName);

        XElement[] items = [.. dict.Elements()];
        Assert.Equal(0, items.Length % 2);
        var values = new Dictionary<string, object>();
        for (int i = 0; i < items.Length; i += 2)
        {
            Assert.Equal("key", items[i].Name.LocalName);
            XElement value = items[i + 1];
            values.Add(items[i].Value, value.Name.LocalName switch
            {
                "string" => value.Value,
                "true" => true,
                "false" => false,
                _ => throw new InvalidOperationException(value.Name.LocalName),
            });
        }

        return values;
    }

    [Fact]
    public void ThePlistSaysWhatTheManifestSays()
    {
        string plist = InfoPlist.Build(Manifest("""copyright="Copyright 2026 Test Studio" """), hasIcon: true, "12.3");

        Dictionary<string, object> values = Read(plist);

        Assert.Equal("Fixture Game", values["CFBundleName"]);
        Assert.Equal("Fixture Game", values["CFBundleDisplayName"]);
        Assert.Equal("org.test.fixturegame", values["CFBundleIdentifier"]);
        Assert.Equal("FixtureGame", values["CFBundleExecutable"]);
        Assert.Equal("1.2.3", values["CFBundleVersion"]);
        Assert.Equal("1.2.3", values["CFBundleShortVersionString"]);
        Assert.Equal("APPL", values["CFBundlePackageType"]);
        Assert.Equal("game.icns", values["CFBundleIconFile"]);
        Assert.Equal("6.0", values["CFBundleInfoDictionaryVersion"]);
        Assert.Equal("12.3", values["LSMinimumSystemVersion"]);
        Assert.Equal(true, values["NSHighResolutionCapable"]);
        Assert.Equal("public.app-category.games", values["LSApplicationCategoryType"]);
        Assert.Equal("Copyright 2026 Test Studio", values["NSHumanReadableCopyright"]);
        Assert.Equal(Weft.Core.EngineVersion.Text, values["WeftEngineVersion"]);
        Assert.Equal(14, values.Count);
    }

    [Fact]
    public void ThePlistIsTheXmlFormatWithItsDocumentTypeAndNoByteOrderMark()
    {
        string plist = InfoPlist.Build(Manifest(), hasIcon: false, "11.0");

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n", plist, StringComparison.Ordinal);
        Assert.EndsWith("</plist>\n", plist, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", plist, StringComparison.Ordinal);
        Assert.NotEqual('﻿', plist[0]);
        Assert.Contains("<true />", plist, StringComparison.Ordinal);
    }

    [Fact]
    public void AGameWithoutAnIconOrACopyrightHasNeitherKey()
    {
        Dictionary<string, object> values = Read(InfoPlist.Build(Manifest(), hasIcon: false, "11.0"));

        Assert.DoesNotContain("CFBundleIconFile", values.Keys);
        Assert.DoesNotContain("NSHumanReadableCopyright", values.Keys);
    }

    [Fact]
    public void TextWithMarkupInItIsEscaped()
    {
        const string Xml = """<Game name="Cats &amp; Dogs &lt;3" organization="Test Studio" version="1.2.3" executable="FixtureGame" identifier="org.test.fixturegame" copyright="(c) Fish &amp; Chips &lt;Ltd&gt; &quot;2026&quot;"/>""";
        Assert.True(GameManifest.TryParse(Xml, "game.wgam", out GameManifest? manifest, out _));

        string plist = InfoPlist.Build(manifest!, hasIcon: false, "11.0");

        Assert.Contains("<string>Cats &amp; Dogs &lt;3</string>", plist, StringComparison.Ordinal);
        Assert.DoesNotContain("Cats & Dogs", plist, StringComparison.Ordinal);
        Assert.DoesNotContain("<Ltd>", plist, StringComparison.Ordinal);
        Dictionary<string, object> values = Read(plist); // and a parser reads back exactly what the game said
        Assert.Equal("Cats & Dogs <3", values["CFBundleName"]);
        Assert.Equal("(c) Fish & Chips <Ltd> \"2026\"", values["NSHumanReadableCopyright"]);
    }

    [Fact]
    public void TheFileIsWrittenAsUtf8()
    {
        GameManifest manifest = Manifest("""copyright="Copyright 2026 Zoë" """);

        byte[] bytes = new UTF8Encoding(false).GetBytes(InfoPlist.Build(manifest, hasIcon: false, "11.0"));

        Assert.Contains("Zoë", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal((byte)'<', bytes[0]);
    }

    [Theory]
    [InlineData(0x000B0000u, "11.0")]
    [InlineData(0x000C0300u, "12.3")]
    [InlineData(0x000D0301u, "13.3.1")]
    [InlineData(0x000A0F00u, "10.15")]
    public void VersionsAreWrittenAsMajorMinorAndPatchWhenThereIsOne(uint version, string text)
    {
        Assert.Equal(text, MachOBuildVersion.Format(version));
    }

    [Fact]
    public void TheBundleVersionIsThreeIntegersBecauseTheManifestAcceptsNothingElse()
    {
        // CFBundleVersion is one to three integers separated by dots. game.wgam's version only is major.minor.patch (a suffix such as -beta is a problem in the manifest), so the plist never has one.
        const string Suffix = """<Game name="Fixture Game" organization="Test Studio" version="1.2.3-beta" executable="FixtureGame" identifier="org.test.fixturegame"/>""";
        Assert.False(GameManifest.TryParse(Suffix, "game.wgam", out _, out _));

        Dictionary<string, object> values = Read(InfoPlist.Build(Manifest(), hasIcon: false, "11.0"));

        Assert.Matches(@"^\d+\.\d+\.\d+$", (string)values["CFBundleVersion"]);
    }

    [Fact]
    public void ThePackageInfoIsAnApplicationWithNoCreatorCode()
    {
        Assert.Equal("APPL????", InfoPlist.PackageInfo);
    }
}
