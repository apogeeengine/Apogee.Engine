using System;
using System.IO;
using System.Linq;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;
using Apogee.Scripting;
using Xunit;

namespace Apogee.Scripting.Tests;

public class XmlLibraryTests
{
    [Fact]
    public void DecodeGivesTheElementWithAttributesInOrderChildrenTextAndLines()
    {
        using var f = new VmFixture();

        f.Run("""
            local node = xml.decode('<Crate id="a" weight="2">\n  <Tag>x</Tag>\n  <Tag>y</Tag>\n</Crate>')
            print(node.name, node.attrs.id, node.attrs.weight, table.concat(node.order, ","), #node.children, node.children[2].text, node.children[2].line, node.line)
            """);

        Assert.Equal("Crate\ta\t2\tid,weight\t2\ty\t3\t1", f.Output.Single().Text);
    }

    [Fact]
    public void EncodeWritesAnElementAndDecodeRoundTripsIt()
    {
        using var f = new VmFixture();

        f.Run("""
            local text = xml.encode({
                name = "Crate",
                attrs = { weight = 12.5, id = "wooden & co", stackable = true },
                order = { "id" },
                children = { { name = "Tag", text = "x < y" }, { name = "Empty" } },
            })
            print(text)
            local back = xml.decode(text)
            print(back.attrs.id, back.attrs.weight, back.attrs.stackable, back.children[1].text)
            """);

        Assert.Equal("<Crate id=\"wooden &amp; co\" stackable=\"true\" weight=\"12.5\">\n  <Tag>x &lt; y</Tag>\n  <Empty/>\n</Crate>\n", f.Output[0].Text);
        Assert.Equal("wooden & co\t12.5\ttrue\tx < y", f.Output[1].Text);
    }

    [Fact]
    public void AttributesWithoutAnOrderAreSortedSoTheTextIsTheSameEveryTime()
    {
        using var f = new VmFixture();

        f.Run("print(xml.encode({ name = 'a', attrs = { c = '3', a = '1', b = '2' } }))");

        Assert.Equal("<a a=\"1\" b=\"2\" c=\"3\"/>\n", f.Output.Single().Text);
    }

    [Fact]
    public void BadXmlAndBadTablesAreScriptErrorsThatSayWhat()
    {
        using var f = new VmFixture();

        Assert.Contains("xml:3:", f.Fails("xml.decode('<a>\\n<b>\\n</a>')").Message);
        Assert.Contains("must be a string", f.Fails("xml.decode(5)").Message);
        Assert.Contains("an element needs a name", f.Fails("xml.encode({})").Message);
        Assert.Contains("'1bad' is not a valid element name", f.Fails("xml.encode({ name = '1bad' })").Message);
        Assert.Contains("'no good' is not a valid attribute name", f.Fails("xml.encode({ name = 'a', attrs = { ['no good'] = 'x' } })").Message);
        Assert.Contains("the attribute 'x' must be text", f.Fails("xml.encode({ name = 'a', attrs = { x = {} } })").Message);
        Assert.Contains("an element is a table", f.Fails("xml.encode('text')").Message);
    }

    [Fact]
    public void DeepNestingIsRefusedBothWays()
    {
        using var f = new VmFixture();

        Assert.Contains("nested more than", f.Fails("xml.decode(string.rep('<a>', 200) .. string.rep('</a>', 200))").Message);
        Assert.Contains("nested more than", f.Fails("local n = { name = 'a' } for i = 1, 200 do n = { name = 'a', children = { n } } end xml.encode(n)").Message);
        f.Run("local n = { name = 'a' } for i = 1, 100 do n = { name = 'a', children = { n } } end print(#xml.encode(n) > 0)");
        Assert.Equal("true", f.Output.Last().Text);
    }
}

/// <summary>A VM with a definition database over two folders (a base game and a mod), as a game would load it.</summary>
public sealed class DefsFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apogee-defs-" + Guid.NewGuid().ToString("N"));

    public DefsFixture()
    {
        Directory.CreateDirectory(Path.Combine(_root, "base", "data"));
        Directory.CreateDirectory(Path.Combine(_root, "mod", "data"));
        Files = new VirtualFileSystem();
        Files.Mount("mod://base", new DirectorySource(Path.Combine(_root, "base")));
        Files.Mount("mod://mod", new DirectorySource(Path.Combine(_root, "mod")));
        Fixture = new VmFixture();
    }

    public VirtualFileSystem Files { get; }

    public VmFixture Fixture { get; }

    public void Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public DefDatabase Load(SchemaSet? schemas = null) =>
        Fixture.Vm.Defs = DefDatabase.Load(Files, [new DefFolder("base", "mod://base/data"), new DefFolder("mod", "mod://mod/data")], schemas);

    public void Dispose()
    {
        Fixture.Dispose();
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

public class DefsLibraryTests
{
    private const string Schema = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="Crate">
            <xs:complexType>
              <xs:sequence><xs:element name="Tag" type="xs:string" minOccurs="0" maxOccurs="unbounded"/></xs:sequence>
              <xs:attribute name="id" type="xs:string" use="required"/>
              <xs:attribute name="weight" type="xs:decimal" default="1"/>
              <xs:attribute name="stackable" type="xs:boolean" default="false"/>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private static SchemaSet Schemas()
    {
        var set = new SchemaSet();
        set.LoadText(Schema, "crate.xsd");
        return set;
    }

    [Fact]
    public void GetGivesTheDecodedDefinitionAsATable()
    {
        using var d = new DefsFixture();
        d.Write("base/data/crates.xml", "<Crates><Crate id=\"wooden\" weight=\"12.5\"><Tag>a</Tag><Tag>b</Tag></Crate><Crate id=\"steel\" stackable=\"true\"/></Crates>");
        d.Load(Schemas());

        d.Fixture.Run("""
            local c = defs.get("Crate", "wooden")
            print(c.id, c.weight, c.stackable, #c.Tag, c.Tag[2])
            print(defs.get("Crate", "steel").stackable, defs.get("Crate", "steel").weight)
            print(defs.get("Crate", "nothing"), defs.get("Barrel", "wooden"))
            """);

        Assert.Equal("wooden\t12.5\tfalse\t2\tb", d.Fixture.Output[0].Text);
        Assert.Equal("true\t1", d.Fixture.Output[1].Text);
        Assert.Equal("nil\tnil", d.Fixture.Output[2].Text);
    }

    [Fact]
    public void AllIsInLoadOrderAndTypesAreSorted()
    {
        using var d = new DefsFixture();
        d.Write("base/data/a.xml", "<Things><Crate id=\"one\"/><Barrel id=\"b\"/><Crate id=\"two\"/></Things>");
        d.Write("mod/data/b.xml", "<Crate id=\"three\"/>");
        d.Load(Schemas());

        d.Fixture.Run("""
            local ids = {}
            for _, crate in defs.all("Crate") do table.insert(ids, crate.id) end
            print(table.concat(ids, ","), #defs.all("Nothing"), table.concat(defs.types(), ","))
            """);

        Assert.Equal("one,two,three\t0\tBarrel,Crate", d.Fixture.Output.Single().Text);
    }

    [Fact]
    public void TheTablesAreReadOnlyAllTheWayDownAndTheSameForEveryone()
    {
        using var d = new DefsFixture();
        d.Write("base/data/a.xml", "<Crate id=\"a\"><Tag>x</Tag></Crate>");
        d.Load(Schemas());

        d.Fixture.Run("""
            local c = defs.get("Crate", "a")
            print(c == defs.get("Crate", "a"), defs.all("Crate")[1] == c)
            print(pcall(function() c.weight = 5 end))
            print(pcall(function() c.Tag[1] = "y" end))
            print(pcall(function() table.insert(c.Tag, "z") end))
            """);

        Assert.Equal("true\ttrue", d.Fixture.Output[0].Text);
        Assert.StartsWith("false\t", d.Fixture.Output[1].Text);
        Assert.Contains("readonly", d.Fixture.Output[1].Text);
        Assert.StartsWith("false\t", d.Fixture.Output[2].Text);
        Assert.StartsWith("false\t", d.Fixture.Output[3].Text);
    }

    [Fact]
    public void SourcesAndProblemsAreReadable()
    {
        using var d = new DefsFixture();
        d.Write("base/data/a.xml", "<Crate id=\"a\" weight=\"heavy\"/>");
        d.Write("mod/data/p.xml", "<Patches>\n<Crate id=\"a\" patch=\"true\" weight=\"3\"/>\n</Patches>");
        d.Write("mod/data/q.xml", "<Crate id=\"b\"/>");
        d.Load(Schemas());

        d.Fixture.Run("""
            for _, s in defs.sources("Crate", "a") do print(s.package, s.path, s.line, s.role) end
            print(#defs.sources("Crate", "nothing"))
            local problems = defs.problems()
            print(#problems)
            """);

        Assert.Equal("base\tmod://base/data/a.xml\t1\tdefinition", d.Fixture.Output[0].Text);
        Assert.Equal("mod\tmod://mod/data/p.xml\t2\tpatch", d.Fixture.Output[1].Text);
        Assert.Equal("0", d.Fixture.Output[2].Text);
        Assert.Equal("0", d.Fixture.Output[3].Text); // the base game's weight "heavy" was patched to 3 by the mod: problems are found after merging
    }

    [Fact]
    public void ProblemsCarryTheirSeverityFileLineAndMessage()
    {
        using var d = new DefsFixture();
        d.Write("base/data/a.xml", "<Crates>\n<Crate id=\"a\" weight=\"heavy\"/>\n</Crates>");
        d.Write("mod/data/b.xml", "<Crate id=\"a\" weight=\"5\"/>");
        d.Load(Schemas());

        d.Fixture.Run("""
            for _, p in defs.problems() do print(p.severity, p.file, p.line, p.message) end
            """);

        Assert.Single(d.Fixture.Output);
        Assert.StartsWith("warning\tmod://mod/data/b.xml\t1\treplaces Crate 'a' defined in mod://base/data/a.xml", d.Fixture.Output[0].Text);
    }

    [Fact]
    public void ReplacingTheDefinitionsGivesScriptsTheNewOnes()
    {
        using var d = new DefsFixture();
        d.Write("base/data/a.xml", "<Crate id=\"a\" weight=\"1\"/>");
        d.Load(Schemas());
        d.Fixture.Run("print(defs.get('Crate', 'a').weight)");

        d.Write("base/data/a.xml", "<Crate id=\"a\" weight=\"2\"/>");
        d.Load(Schemas());
        d.Fixture.Run("print(defs.get('Crate', 'a').weight)");

        Assert.Equal("1", d.Fixture.Output[0].Text);
        Assert.Equal("2", d.Fixture.Output[1].Text);
    }

    [Fact]
    public void WithoutDefinitionsTheLibraryFindsNothing()
    {
        using var f = new VmFixture();

        f.Run("print(defs.get('Crate', 'a'), #defs.all('Crate'), #defs.types(), #defs.problems(), #defs.sources('Crate', 'a'))");

        Assert.Equal("nil\t0\t0\t0\t0", f.Output.Single().Text);
        Assert.Contains("must be a string", f.Fails("defs.get(1, 'a')").Message);
    }

    [Fact]
    public void ADefinitionWithNoSchemaIsAPlainTable()
    {
        using var d = new DefsFixture();
        d.Write("base/data/a.xml", "<Barrel id=\"a\" volume=\"40\"><Hoop>iron</Hoop><Hoop>steel</Hoop></Barrel>");
        d.Load();

        d.Fixture.Run("local b = defs.get('Barrel', 'a') print(b.volume, #b.Hoop, b.Hoop[1])");

        Assert.Equal("40\t2\tiron", d.Fixture.Output.Single().Text);
    }

    [Fact]
    public void TheDefinitionsDeclareBothLibraries()
    {
        using var f = new VmFixture();
        string text = DefinitionWriter.Write().ReplaceLineEndings("\n");

        Assert.Contains("type XmlElement = { name: string, attrs: { [string]: string }, order: { string }, children: { XmlElement }, text: string, line: number }", text);
        Assert.Contains("declare xml: {\n", text);
        Assert.Contains("    decode: (text: string) -> XmlElement,\n", text);
        Assert.Contains("declare defs: {\n", text);
        Assert.Contains("    get: (type: string, id: string) -> any?,\n", text);
        Assert.Contains("    problems: () -> { DefProblem },\n", text);
        Assert.Contains("--- Every definition of a type, in load order", text);
    }
}
