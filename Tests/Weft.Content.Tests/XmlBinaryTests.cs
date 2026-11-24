using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Content.Xml;
using Weft.Core.Assets;
using Xunit;

namespace Weft.Content.Tests;

/// <summary>The cooked encoding of XML documents (<see cref="XmlBinary"/>) and the reader that takes it or text (<see cref="XmlFile"/>).</summary>
public class XmlBinaryTests
{
    private const string Sample = """
        <Scene version="1" uid="uid://4k2m9x7q1w3ze" name="Test">
          <Entity id="1" name="crate">
            <Transform position="1 2 3" rotation="0 0 0 1"/>
            <Notes>  some   text  </Notes>
          </Entity>
          <Entity id="2" name="crate">
            <Transform position="4 5 6" rotation="0 0 0 1"/>
          </Entity>
          <Empty/>
        </Scene>
        """;

    private static void AssertSame(XmlNode expected, XmlNode actual, string? origin)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Line, actual.Line);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Attributes, actual.Attributes);
        Assert.Equal(origin, actual.Origin);
        Assert.Equal(expected.Children.Count, actual.Children.Count);
        for (int i = 0; i < expected.Children.Count; i++)
        {
            AssertSame(expected.Children[i], actual.Children[i], origin);
        }
    }

    /// <summary>A header with the counts a test wants to claim, then the given bytes.</summary>
    private static byte[] Claim(uint strings, uint nodes, params byte[] rest)
    {
        var bytes = new byte[16 + rest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x424D5857);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), XmlBinary.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), strings);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), nodes);
        rest.CopyTo(bytes, 16);
        return bytes;
    }

    private static XmlNode Chain(int levels)
    {
        var root = new XmlNode("n", 1);
        XmlNode current = root;
        for (int i = 1; i < levels; i++)
        {
            var child = new XmlNode("n", 1);
            current.Children.Add(child);
            current = child;
        }

        return root;
    }

    [Fact]
    public void ATreeRoundTripsWithAttributesOrderTextAndLines()
    {
        XmlNode tree = XmlNode.Parse(Sample, "scene.wscn");
        tree.Children[2].SetAttribute("z", "last");
        tree.Children[2].SetAttribute("a", "first"); // written after "z": the order is kept, not sorted

        byte[] bytes = XmlBinary.Write(tree);
        XmlNode read = XmlBinary.Read(bytes, "res://cooked.wscn");

        Assert.True(XmlBinary.IsBinary(bytes));
        AssertSame(tree, read, "res://cooked.wscn");
        Assert.Equal(["z", "a"], read.Children[2].Attributes.Select(a => a.Name));
        Assert.Equal(tree.ToXml(), read.ToXml());
        Assert.Equal(6, read.Children[1].Line);
        Assert.Equal("some   text", read.Children[0].Child("Notes")!.Text);

        // A tree made in code, with no line and no origin, round trips as well, and so does the smallest document.
        var small = new XmlNode("Only");
        AssertSame(small, XmlBinary.Read(XmlBinary.Write(small), "only.xml"), "only.xml");
    }

    [Fact]
    public void EscapesAndUnicodeSurvive()
    {
        XmlNode tree = XmlNode.Parse("<Crate id=\"a&amp;b\" note=\"say &quot;hi&quot;\" tab=\"x&#9;y&#10;z\" accents=\"café üñî 日本語 😀\"><Tag>x &lt; y</Tag><Items><Item n=\"1\"/><Item n=\"2\"/></Items><Élément é=\"è\"/></Crate>");

        XmlNode read = XmlBinary.Read(XmlBinary.Write(tree), "unicode.xml");

        AssertSame(tree, read, "unicode.xml");
        Assert.Equal("a&b", read.Attribute("id"));
        Assert.Equal("say \"hi\"", read.Attribute("note"));
        Assert.Equal("x\ty\nz", read.Attribute("tab"));
        Assert.Equal("café üñî 日本語 😀", read.Attribute("accents"));
        Assert.Equal("è", read.Child("Élément")!.Attribute("é"));
        Assert.Equal(tree.ToXml(), read.ToXml());
        Assert.Equal(tree.ToXml(), XmlNode.Parse(read.ToXml()).ToXml()); // and the text of the cooked tree reads back as the same document
    }

    [Fact]
    public void WritingIsDeterministic()
    {
        byte[] first = XmlBinary.Write(XmlNode.Parse(Sample));
        byte[] second = XmlBinary.Write(XmlNode.Parse(Sample));
        byte[] third = XmlBinary.Write(XmlBinary.Read(first, "again.xml")); // writing what was read changes nothing

        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void ACookedFileIsSmallerThanItsText()
    {
        var scene = new XmlNode("Scene", 1);
        scene.Attributes.Add(new XmlProperty("version", "1"));
        for (int i = 0; i < 1000; i++)
        {
            var entity = new XmlNode("Entity", 2 + (i * 4));
            entity.Attributes.Add(new XmlProperty("id", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            entity.Attributes.Add(new XmlProperty("name", "crate"));
            var transform = new XmlNode("Transform", 3 + (i * 4));
            transform.Attributes.Add(new XmlProperty("position", $"{i}.5 0 {i * 2}"));
            transform.Attributes.Add(new XmlProperty("rotation", "0 0 0 1"));
            entity.Children.Add(transform);
            var renderer = new XmlNode("ModelRenderer", 4 + (i * 4));
            renderer.Attributes.Add(new XmlProperty("model", "res://models/crate.glb|uid://4k2m9x7q1w3ze"));
            entity.Children.Add(renderer);
            scene.Children.Add(entity);
        }

        byte[] bytes = XmlBinary.Write(scene);
        int text = Encoding.UTF8.GetByteCount(scene.ToXml());

        Assert.True(bytes.Length < text / 2, $"{bytes.Length} bytes cooked, {text} as text");
        AssertSame(scene, XmlBinary.Read(bytes, "big.wscn"), "big.wscn");
    }

    [Fact]
    public void XmlFileReadsTextAndBinaryAlike()
    {
        using var folder = new TempFolder();
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(folder.Root, writable: true));
        XmlNode tree = XmlNode.Parse(Sample, "res://scene.wscn");
        files.WriteAllText("res://scene.wscn", Sample);
        files.WriteAllBytes("res://cooked.wscn", XmlBinary.Write(tree));
        files.WriteAllBytes("res://bom.wscn", [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(Sample)]);

        XmlNode fromText = XmlFile.Read(files, "res://scene.wscn");
        XmlNode fromBinary = XmlFile.Read(files, "res://cooked.wscn");
        XmlNode fromBom = XmlFile.Read(files, "res://bom.wscn");

        AssertSame(tree, fromText, "res://scene.wscn");
        AssertSame(tree, fromBinary, "res://cooked.wscn");
        AssertSame(tree, fromBom, "res://bom.wscn");
        Assert.Equal(fromText.ToXml(), fromBinary.ToXml());
        AssertSame(tree, XmlFile.Read(Encoding.UTF8.GetBytes(Sample), "res://scene.wscn"), "res://scene.wscn");
        Assert.Throws<FileNotFoundException>(() => XmlFile.Read(files, "res://missing.wscn"));

        // A text file that is not XML is still the text parser's error, with the path and the line.
        files.WriteAllText("res://bad.wscn", "<a>\n<b></a>");
        Assert.StartsWith("res://bad.wscn:2:", Assert.Throws<XmlParseException>(() => XmlFile.Read(files, "res://bad.wscn")).Message);
    }

    [Fact]
    public void ACookedDocumentsUidIsReadFromItsRoot()
    {
        AssetUid uid = AssetUid.New();
        byte[] cooked = XmlBinary.Write(XmlNode.Parse(AuthoredAssets.SetUid("<Scene version=\"1\"><Entity uid=\"uid://zzzzzzzzzzzzz\"/></Scene>", uid)));

        Assert.Equal(uid, AuthoredAssets.ReadUid(cooked));
        using var stream = new MemoryStream(cooked);
        Assert.Equal(uid, AuthoredAssets.ReadUid(stream));
        using var forward = new NonSeekable(cooked);
        Assert.Equal(uid, AuthoredAssets.ReadUid(forward));
        Assert.Equal(AssetUid.None, AuthoredAssets.ReadUid(XmlBinary.Write(XmlNode.Parse("<Scene version=\"1\"/>"))));
        Assert.Equal(AssetUid.None, AuthoredAssets.ReadUid(XmlBinary.Write(XmlNode.Parse("<Scene uid=\"nonsense\"/>"))));
        Assert.Equal(AssetUid.None, AuthoredAssets.ReadUid(cooked.AsSpan(0, 20))); // damaged: no UID, no exception
    }

    [Fact]
    public void ANewerVersionOrAFlagIsRefused()
    {
        byte[] good = XmlBinary.Write(XmlNode.Parse(Sample));
        Assert.Equal(1, XmlBinary.Version);

        byte[] newer = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(newer.AsSpan(4), 2);
        ContentException error = Assert.Throws<ContentException>(() => XmlBinary.Read(newer, "res://a.wscn"));
        Assert.Contains("newer engine", error.Message, StringComparison.Ordinal);
        Assert.Contains("cook again", error.Message, StringComparison.Ordinal);
        Assert.StartsWith("res://a.wscn:", error.Message, StringComparison.Ordinal);
        Assert.Throws<ContentException>(() => XmlFile.Read(newer, "res://a.wscn")); // not mistaken for text

        byte[] flagged = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(flagged.AsSpan(6), 1);
        Assert.Throws<ContentException>(() => XmlBinary.Read(flagged, "a.wscn"));

        byte[] zero = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(zero.AsSpan(4), 0);
        Assert.Throws<ContentException>(() => XmlBinary.Read(zero, "a.wscn"));

        byte[] magic = (byte[])good.Clone();
        magic[0] ^= 0xFF;
        Assert.False(XmlBinary.IsBinary(magic));
        Assert.Throws<ContentException>(() => XmlBinary.Read(magic, "a.wscn"));
        Assert.Throws<ContentException>(() => XmlBinary.Read([], "a.wscn"));
        Assert.Throws<ContentException>(() => XmlBinary.Read("<a/>"u8, "a.wscn"));
        Assert.False(XmlBinary.IsBinary("<a/>"u8));
        Assert.False(XmlBinary.IsBinary("WXM"u8));
        Assert.False(XmlBinary.IsBinary([0xEF, 0xBB, 0xBF, (byte)'<']));
    }

    [Fact]
    public void TruncatedOrDamagedBytesAreAContentError()
    {
        byte[] good = XmlBinary.Write(XmlNode.Parse(Sample));

        // Every prefix is missing something, so none of them reads.
        for (int length = 0; length < good.Length; length++)
        {
            byte[] prefix = good[..length];
            Assert.Throws<ContentException>(() => XmlBinary.Read(prefix, "cut.wscn"));
        }

        // One extra byte is damage too.
        Assert.Throws<ContentException>(() => XmlBinary.Read([.. good, 0], "extra.wscn"));

        // Flipped bytes read as something else or are refused; nothing but a ContentException may come out.
        var random = new Random(20261114);
        for (int i = 0; i < 1000; i++)
        {
            byte[] damaged = (byte[])good.Clone();
            int flips = 1 + random.Next(3);
            for (int f = 0; f < flips; f++)
            {
                damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
            }

            try
            {
                XmlBinary.Read(damaged, "flipped.wscn");
            }
            catch (ContentException)
            {
            }

            try
            {
                _ = AuthoredAssets.ReadUid(damaged);
            }
            catch (Exception e)
            {
                Assert.Fail($"ReadUid threw {e.GetType().Name} for flip set {i}");
            }
        }

        // Bytes that never were a file.
        var noise = new byte[512];
        for (int i = 0; i < 200; i++)
        {
            random.NextBytes(noise);
            noise[0] = 0x57;
            noise[1] = 0x58;
            noise[2] = 0x4D;
            noise[3] = 0x42;
            try
            {
                XmlBinary.Read(noise, "noise.wscn");
            }
            catch (ContentException)
            {
            }
        }
    }

    [Fact]
    public void ACountThatDoesNotFitInTheFileIsRefusedBeforeAnythingIsAllocatedForIt()
    {
        byte[] table = [0, 1, (byte)'a']; // the strings "" and "a"
        byte[][] files =
        [
            Claim(uint.MaxValue, 1, 0),                                                   // strings
            Claim(1, uint.MaxValue, 0, 0, 0, 0, 0, 0),                                    // elements
            Claim(0, 1, 0),                                                               // no string 0
            Claim(1, 0, 0),                                                               // no elements
            Claim(2, 1, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 1, 2),                          // a string longer than the file
            Claim(2, 1, [.. table, 1, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0, 0]),          // attributes
            Claim(2, 1, [.. table, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F]),          // children
            Claim(2, 1, [.. table, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]),    // a number of more than 32 bits
            Claim(2, 1, [.. table, 1, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x1F, 0, 0]),          // a number of exactly 33 bits
        ];

        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (byte[] file in files)
        {
            Assert.Throws<ContentException>(() => XmlBinary.Read(file, "huge.wscn"));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 256 * 1024, $"{allocated} bytes allocated for {files.Length} files of under 40 bytes");
    }

    [Fact]
    public void AnElementWithNoNameOrAnIndexOutOfTheTableIsRefused()
    {
        // strings: "" and "a"; the root's name is string 0 (empty), string 5 (not there), or string 1 and a child count that is not backed by a node.
        byte[] table = [0, 1, (byte)'a'];
        Assert.Throws<ContentException>(() => XmlBinary.Read(Claim(2, 1, [.. table, 0, 0, 0, 0, 0]), "n.xml"));
        Assert.Throws<ContentException>(() => XmlBinary.Read(Claim(2, 1, [.. table, 5, 0, 0, 0, 0]), "n.xml"));
        Assert.Throws<ContentException>(() => XmlBinary.Read(Claim(2, 1, [.. table, 1, 0, 0, 0, 1]), "n.xml"));
        Assert.Throws<ContentException>(() => XmlBinary.Read(Claim(2, 2, [.. table, 1, 0, 0, 0, 0]), "n.xml")); // says two elements, has one
        Assert.Throws<ContentException>(() => XmlBinary.Read(Claim(2, 1, [.. table, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0]), "n.xml")); // two roots

        XmlNode ok = XmlBinary.Read(Claim(2, 1, [.. table, 1, 7, 0, 0, 0]), "n.xml");
        Assert.Equal("a", ok.Name);
        Assert.Equal(7, ok.Line);

        // A string that is not UTF-8.
        Assert.Throws<ContentException>(() => XmlBinary.Read(Claim(2, 1, [0, 1, 0xFF, 1, 0, 0, 0, 0]), "n.xml"));
    }

    [Fact]
    public void NestingDeeperThanTheLimitIsRefused()
    {
        // The text parser allows MaxDepth open elements and an empty one inside; the cooked form allows the same and not more, whichever way it is made.
        XmlNode atLimit = Chain(XmlNode.MaxDepth + 1);
        byte[] bytes = XmlBinary.Write(atLimit);
        Assert.Equal(XmlNode.MaxDepth + 1, DepthOf(XmlBinary.Read(bytes, "deep.xml")));

        string text = string.Concat(Enumerable.Repeat("<n>", XmlNode.MaxDepth)) + "<n/>" + string.Concat(Enumerable.Repeat("</n>", XmlNode.MaxDepth));
        Assert.Equal(XmlNode.MaxDepth + 1, DepthOf(XmlNode.Parse(text)));
        Assert.Equal(DepthOf(XmlNode.Parse(text)), DepthOf(XmlBinary.Read(XmlBinary.Write(XmlNode.Parse(text)), "deep.xml")));

        Assert.Throws<ContentException>(() => XmlBinary.Write(Chain(XmlNode.MaxDepth + 2)));
        Assert.Throws<ContentException>(() => XmlBinary.Write(Chain(5000)));

        // A file that nests deeper than the writer would allow (made by hand) is refused by the reader before it recurses far.
        int levels = 5000;
        var body = new List<byte>();
        for (int i = 0; i < levels; i++)
        {
            body.AddRange([1, 0, 0, 0, i == levels - 1 ? (byte)0 : (byte)1]);
        }

        Assert.Throws<ContentException>(() => XmlBinary.Read(Claim(2, (uint)levels, [.. new byte[] { 0, 1, (byte)'n' }, .. body]), "deep.xml"));
    }

    [Fact]
    public void WhatCannotBeCookedIsRefused()
    {
        Assert.Throws<ContentException>(() => XmlBinary.Write(new XmlNode("")));
        Assert.Throws<ContentException>(() => XmlBinary.Write(new XmlNode("a", -1)));

        var unnamed = new XmlNode("a");
        unnamed.Attributes.Add(new XmlProperty("", "x"));
        Assert.Throws<ContentException>(() => XmlBinary.Write(unnamed));

        var lone = new XmlNode("a") { Text = "\ud800" };
        Assert.Throws<ContentException>(() => XmlBinary.Write(lone));
        Assert.Throws<ArgumentNullException>(() => XmlBinary.Write(null!));
    }

    private static int DepthOf(XmlNode node) => 1 + (node.Children.Count == 0 ? 0 : node.Children.Max(DepthOf));

    /// <summary>A stream that can only be read forward (as a network or pack stream may be).</summary>
    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
