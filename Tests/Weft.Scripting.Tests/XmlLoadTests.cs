using System;
using System.Collections.Generic;
using System.IO;
using Weft.Content.FileSystem;
using Weft.Content.Xml;
using Weft.Native.Tests;
using Weft.Reflection;
using Xunit;
using Lua = Weft.Native.Luau.Lua;

namespace Weft.Scripting.Tests;

/// <summary><c>xml.load</c>: an XML file of the game read into the table <c>xml.decode</c> gives, whether the file is text or cooked (<see cref="XmlBinary"/>).</summary>
public sealed class XmlLoadTests : IDisposable
{
    private const string Text = "<Crates>\n  <Crate id=\"wooden\" weight=\"2\"><Tag>x &lt; y</Tag><Tag>café</Tag></Crate>\n</Crates>\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "weft-xmlload-" + Guid.NewGuid().ToString("N"));
    private readonly LuauVm _vm;

    public XmlLoadTests()
    {
        NativeRequirements.Require(typeof(Lua).Assembly, "luau");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "text.xml"), Text);
        File.WriteAllBytes(Path.Combine(_root, "cooked.xml"), XmlBinary.Write(XmlNode.Parse(Text, "src")));
        File.WriteAllText(Path.Combine(_root, "broken.xml"), "<a>\n<b>\n</a>");
        File.WriteAllBytes(Path.Combine(_root, "cut.xml"), XmlBinary.Write(XmlNode.Parse(Text, "src"))[..^2]);
        File.WriteAllText(Path.Combine(_root, "secret.xml"), "<Secret/>");
        var files = new VirtualFileSystem();
        files.Mount("mod://game", new DirectorySource(_root));
        _vm = new LuauVm(new LuauVmOptions
        {
            FileSystem = files,
            CanAccessFile = (_, path, _) => !path.EndsWith("secret.xml", StringComparison.Ordinal),
            Output = (_, _, _) => { },
        });
    }

    private IReadOnlyList<Value> Run(string source) => _vm.Execute(_vm.BaseOwner, "=test", source);

    [Fact]
    public void ATextFileAndItsCookedFormGiveTheSameTable()
    {
        IReadOnlyList<Value> result = Run("""
            local text = assert(xml.load("mod://game/text.xml"))
            local cooked = assert(xml.load("mod://game/cooked.xml"))
            local crate = cooked.children[1]
            return xml.encode(text) == xml.encode(cooked), cooked.name, crate.attrs.weight, table.concat(crate.order, ","), crate.children[1].text, crate.children[2].text, crate.line
            """);

        Assert.True(result[0].ToBool());
        Assert.Equal(["Crates", "2", "id,weight", "x < y", "café"], [result[1].ToStringValue(), result[2].ToStringValue(), result[3].ToStringValue(), result[4].ToStringValue(), result[5].ToStringValue()]);
        Assert.Equal(2, result[6].ToInt32()); // the line of the source, kept by the cooked form
    }

    [Fact]
    public void AFileThatCannotBeReadIsNilAndAMessageThatNamesIt()
    {
        IReadOnlyList<Value> result = Run("""
            local _, missing = xml.load("mod://game/nothing.xml")
            local _, broken = xml.load("mod://game/broken.xml")
            local _, cut = xml.load("mod://game/cut.xml")
            local _, denied = xml.load("mod://game/secret.xml")
            local _, invalid = xml.load("not a path")
            return missing, broken, cut, denied, invalid
            """);

        Assert.Equal("mod://game/nothing.xml: not found", result[0].ToStringValue());
        Assert.StartsWith("mod://game/broken.xml:3:", result[1].ToStringValue(), StringComparison.Ordinal);
        Assert.StartsWith("mod://game/cut.xml:", result[2].ToStringValue(), StringComparison.Ordinal);
        Assert.Equal("mod://game/secret.xml: access denied", result[3].ToStringValue());
        Assert.Contains("not a valid path", result[4].ToStringValue());
    }

    [Fact]
    public void AnArgumentThatIsNotAStringIsAnErrorAndWithoutAFileSystemItSaysSo()
    {
        Assert.Contains("the path must be a string", Assert.Throws<ScriptException>(() => Run("xml.load(5)")).Message);

        using var bare = new LuauVm(new LuauVmOptions { Output = (_, _, _) => { } });
        Assert.Equal("there is no file system", bare.Execute(bare.BaseOwner, "=t", "local ok, why = xml.load('res://x.xml') return why")[0].ToStringValue());
    }

    public void Dispose()
    {
        _vm.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a failure.
        }
    }
}
