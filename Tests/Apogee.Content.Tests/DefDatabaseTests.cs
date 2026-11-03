using System.Linq;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;
using Xunit;

namespace Apogee.Content.Tests;

public class DefMergeTests
{
    private static string Merge(string original, string patch) => DefDatabase.Merge(XmlNode.Parse(original), XmlNode.Parse(patch)).ToXml();

    [Fact]
    public void AttributesAndTextOfThePatchReplaceTheOriginalsAndAddNewOnes()
    {
        Assert.Equal(
            "<Crate id=\"a\" weight=\"20\" colour=\"red\">new</Crate>\n",
            Merge("<Crate id=\"a\" weight=\"10\">old</Crate>", "<Crate id=\"a\" patch=\"true\" weight=\"20\" colour=\"red\">new</Crate>"));
        Assert.Equal("<Crate id=\"a\" weight=\"10\">old</Crate>\n", Merge("<Crate id=\"a\" weight=\"10\">old</Crate>", "<Crate id=\"a\" patch=\"true\"/>"));
    }

    [Fact]
    public void ChildrenAreMatchedByNameAndIdOrByNameAndPlace()
    {
        Assert.Equal(
            "<M id=\"m\">\n  <Wheel id=\"front\" r=\"2\"/>\n  <Wheel id=\"rear\" r=\"1\"/>\n</M>\n",
            Merge("<M id=\"m\"><Wheel id=\"front\" r=\"1\"/><Wheel id=\"rear\" r=\"1\"/></M>", "<M id=\"m\" patch=\"true\"><Wheel id=\"front\" r=\"2\"/></M>"));
        Assert.Equal(
            "<M id=\"m\">\n  <Unit n=\"1\"/>\n  <Unit n=\"20\"/>\n  <Unit n=\"3\"/>\n</M>\n",
            Merge("<M id=\"m\"><Unit n=\"1\"/><Unit n=\"2\"/><Unit n=\"3\"/></M>", "<M id=\"m\" patch=\"true\"><Unit/><Unit n=\"20\"/></M>"));
    }

    [Fact]
    public void AChildThatMatchesNothingIsAddedAtTheEndAndRemoveTakesAChildAway()
    {
        Assert.Equal(
            "<M id=\"m\">\n  <A/>\n  <C/>\n  <D x=\"1\"/>\n</M>\n",
            Merge("<M id=\"m\"><A/><B/><C/></M>", "<M id=\"m\" patch=\"true\"><B remove=\"true\"/><D x=\"1\" patch=\"true\"/></M>"));
        Assert.Equal(
            "<M id=\"m\">\n  <Unit n=\"9\"/>\n  <Unit n=\"3\"/>\n</M>\n",
            Merge("<M id=\"m\"><Unit n=\"1\"/><Unit n=\"2\"/><Unit n=\"3\"/></M>", "<M id=\"m\" patch=\"true\"><Unit n=\"9\"/><Unit remove=\"true\"/></M>"));
    }

    [Fact]
    public void MergingGoesDeepAndNeitherInputChanges()
    {
        XmlNode original = XmlNode.Parse("<M id=\"m\"><Engine><Part id=\"p\" w=\"1\"/><Part id=\"q\"/></Engine></M>");
        XmlNode patch = XmlNode.Parse("<M id=\"m\" patch=\"true\"><Engine><Part id=\"p\" w=\"2\"/><Part id=\"q\" remove=\"true\"/></Engine></M>");
        string before = original.ToXml();
        string patchBefore = patch.ToXml();

        string merged = DefDatabase.Merge(original, patch).ToXml();

        Assert.Equal("<M id=\"m\">\n  <Engine>\n    <Part id=\"p\" w=\"2\"/>\n  </Engine>\n</M>\n", merged);
        Assert.Equal(before, original.ToXml());
        Assert.Equal(patchBefore, patch.ToXml());
    }

    [Fact]
    public void AnAppendedChildIsAddedAtTheEndWhateverIsThere()
    {
        Assert.Equal(
            "<M id=\"m\">\n  <Tag>a</Tag>\n  <Tag>b</Tag>\n  <Tag>c</Tag>\n</M>\n",
            Merge("<M id=\"m\"><Tag>a</Tag><Tag>b</Tag></M>", "<M id=\"m\" patch=\"true\"><Tag append=\"true\">c</Tag></M>"));
        // The other children of the same patch still count their places without it.
        Assert.Equal(
            "<M id=\"m\">\n  <Tag>A</Tag>\n  <Tag>b</Tag>\n  <Tag>c</Tag>\n</M>\n",
            Merge("<M id=\"m\"><Tag>a</Tag><Tag>b</Tag></M>", "<M id=\"m\" patch=\"true\"><Tag>A</Tag><Tag append=\"true\">c</Tag></M>"));
    }

    [Fact]
    public void PlacesCountThroughChildrenTheSamePatchRemoved()
    {
        // "The second Unit" of the patch is the second of the original even though the first was removed by this patch.
        Assert.Equal(
            "<M id=\"m\">\n  <Unit n=\"2\"/>\n  <Unit n=\"30\"/>\n</M>\n",
            Merge("<M id=\"m\"><Unit n=\"1\"/><Unit n=\"2\"/><Unit n=\"3\"/></M>", "<M id=\"m\" patch=\"true\"><Unit remove=\"true\"/><Unit/><Unit n=\"30\"/></M>"));
    }

    [Fact]
    public void ThePatchAndRemoveAttributesAreNotKeptInWhatIsAdded()
    {
        string merged = Merge("<M id=\"m\"/>", "<M id=\"m\" patch=\"true\"><New patch=\"true\" a=\"1\"><Inner remove=\"false\" b=\"2\"/></New></M>");

        Assert.Equal("<M id=\"m\">\n  <New a=\"1\">\n    <Inner b=\"2\"/>\n  </New>\n</M>\n", merged);
    }
}

public class DefDatabaseTests
{
    private const string CrateSchema = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="Crate">
            <xs:complexType>
              <xs:sequence><xs:element name="Tag" type="xs:string" minOccurs="0" maxOccurs="unbounded"/></xs:sequence>
              <xs:attribute name="id" type="xs:string" use="required"/>
              <xs:attribute name="weight" type="xs:decimal" default="1"/>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private static (VirtualFileSystem Files, TempFolder Base, TempFolder Mod) Layout()
    {
        var @base = new TempFolder();
        var mod = new TempFolder();
        var files = new VirtualFileSystem();
        files.Mount("mod://base", new DirectorySource(@base.Root));
        files.Mount("mod://mod", new DirectorySource(mod.Root));
        return (files, @base, mod);
    }

    private static readonly DefFolder[] s_folders = [new("base", "mod://base/data"), new("mod", "mod://mod/data")];

    private static SchemaSet Schemas()
    {
        var set = new SchemaSet();
        set.LoadText(CrateSchema, "crate.xsd");
        return set;
    }

    [Fact]
    public void FilesAreReadInLoadOrderAndEachIsADefinitionOrAListOfThem()
    {
        (VirtualFileSystem files, TempFolder baseGame, TempFolder mod) = Layout();
        using (baseGame)
        using (mod)
        {
            baseGame.Write("data/b_crates.xml", "<Crates><Crate id=\"two\"/><Crate id=\"three\" weight=\"3\"/></Crates>");
            baseGame.Write("data/a_crate.xml", "<Crate id=\"one\" weight=\"2.5\"/>");
            baseGame.Write("data/sub/deep.xml", "<Crate id=\"deep\"/>");
            baseGame.Write("data/readme.txt", "ignored");
            mod.Write("data/extra.xml", "<Crate id=\"modded\"/>");

            var database = DefDatabase.Load(files, s_folders, Schemas());

            Assert.Empty(database.Problems);
            Assert.Equal(["one", "two", "three", "deep", "modded"], database.All.Select(d => d.Id));
            Assert.Equal(["Crate"], database.Types);
            Assert.Equal(5, database.OfType("Crate").Count);
            Assert.Equal("{id: \"one\", weight: 2.5}", database.Get("Crate", "one")!.Value.ToString());
            Assert.Equal("{id: \"two\", weight: 1}", database.Get("Crate", "two")!.Value.ToString());
            Assert.True(database.Get("Crate", "one")!.Declared);
            Assert.Null(database.Get("Crate", "nothing"));
            Assert.Null(database.Get("Barrel", "one"));
            Assert.Equal(new DefSource("base", "mod://base/data/a_crate.xml", 1, DefRole.Definition), database.Get("Crate", "one")!.Defined);
        }
    }

    [Fact]
    public void ALaterFileReplacesADefinitionWithAWarningThatNamesBoth()
    {
        (VirtualFileSystem files, TempFolder baseGame, TempFolder mod) = Layout();
        using (baseGame)
        using (mod)
        {
            baseGame.Write("data/crates.xml", "<Crates><Crate id=\"a\" weight=\"5\"/><Crate id=\"b\"/></Crates>");
            mod.Write("data/crates.xml", "<Crate id=\"a\" weight=\"9\"/>");

            var database = DefDatabase.Load(files, s_folders, Schemas());

            Assert.Equal(["a", "b"], database.All.Select(d => d.Id)); // the replaced one keeps its place
            Assert.Equal("{id: \"a\", weight: 9}", database.Get("Crate", "a")!.Value.ToString());
            SchemaProblem warning = Assert.Single(database.Problems);
            Assert.Equal(ProblemSeverity.Warning, warning.Severity);
            Assert.Contains("replaces Crate 'a' defined in mod://base/data/crates.xml (package 'base')", warning.Message);
            Assert.Equal(("mod://mod/data/crates.xml", 1), (warning.File, warning.Line));
            Assert.False(database.HasErrors);
            Assert.Equal(DefRole.Replacement, database.Get("Crate", "a")!.Defined.Role);
        }
    }

    [Fact]
    public void APatchChangesADefinitionAndKeepsEveryFileThatMadeIt()
    {
        (VirtualFileSystem files, TempFolder baseGame, TempFolder mod) = Layout();
        using (baseGame)
        using (mod)
        {
            baseGame.Write("data/crate.xml", "<Crate id=\"a\" weight=\"5\"><Tag>old</Tag><Tag>heavy</Tag></Crate>");
            mod.Write("data/patches.xml", "<Patches>\n<Crate id=\"a\" patch=\"true\" weight=\"6\"><Tag>new</Tag><Tag remove=\"true\"/></Crate>\n</Patches>");

            var database = DefDatabase.Load(files, s_folders, Schemas());

            Assert.Empty(database.Problems);
            Def def = database.Get("Crate", "a")!;
            Assert.Equal("{id: \"a\", weight: 6, Tag: [\"new\"]}", def.Value.ToString());
            Assert.Equal(
                [new DefSource("base", "mod://base/data/crate.xml", 1, DefRole.Definition), new DefSource("mod", "mod://mod/data/patches.xml", 2, DefRole.Patch)],
                def.Sources);
        }
    }

    [Fact]
    public void ARemoveTakesADefinitionOutAndAPatchOrRemoveOfNothingIsAWarning()
    {
        (VirtualFileSystem files, TempFolder baseGame, TempFolder mod) = Layout();
        using (baseGame)
        using (mod)
        {
            baseGame.Write("data/crates.xml", "<Crates><Crate id=\"a\"/><Crate id=\"b\"/></Crates>");
            mod.Write("data/changes.xml", "<Changes><Crate id=\"a\" remove=\"true\"/><Crate id=\"ghost\" patch=\"true\" weight=\"2\"/><Crate id=\"phantom\" remove=\"true\"/></Changes>");

            var database = DefDatabase.Load(files, s_folders, Schemas());

            Assert.Equal(["b"], database.All.Select(d => d.Id));
            Assert.Equal(["patches Crate 'ghost', which does not exist.", "removes Crate 'phantom', which does not exist."], database.Problems.Select(p => p.Message));
            Assert.All(database.Problems, p => Assert.Equal(ProblemSeverity.Warning, p.Severity));
        }
    }

    [Fact]
    public void ProblemsNameTheFileTheLineAndTheDefinition()
    {
        (VirtualFileSystem files, TempFolder baseGame, TempFolder mod) = Layout();
        using (baseGame)
        using (mod)
        {
            baseGame.Write("data/crates.xml", "<Crates>\n<Crate id=\"a\" weight=\"heavy\"/>\n<Crate weight=\"2\"/>\n<Crate id=\"c\" colour=\"red\"/>\n</Crates>");
            baseGame.Write("data/broken.xml", "<Crates>\n<Crate id=\"x\">\n</Crates>");

            var database = DefDatabase.Load(files, s_folders, Schemas());

            Assert.True(database.HasErrors);
            string[] problems = [.. database.Problems.Select(p => $"{p.File}:{p.Line}: {p.Path}: {p.Message}")];
            Assert.Contains("mod://base/data/broken.xml:3: : the file is not well-formed XML: The 'Crate' start tag on line 2 position 2 does not match the end tag of 'Crates'. Line 3, position 3.", problems);
            Assert.Contains("mod://base/data/crates.xml:3: Crate: a definition (<Crate>) needs an id.", problems);
            Assert.Contains("mod://base/data/crates.xml:2: Crate.weight (a): 'heavy' is not a number", problems);
            Assert.Contains("mod://base/data/crates.xml:4: Crate (c): 'colour' is not an attribute of <Crate> (it has: id, weight).", problems);
            Assert.Equal(["a", "c"], database.All.Select(d => d.Id)); // the definitions are there, whatever is wrong with them
        }
    }

    [Fact]
    public void AProblemInAPatchedDefinitionNamesTheFileOfTheLineItIsOn()
    {
        (VirtualFileSystem files, TempFolder baseGame, TempFolder mod) = Layout();
        using (baseGame)
        using (mod)
        {
            baseGame.Write("data/crate.xml", "<Crate id=\"a\" weight=\"5\"/>");
            mod.Write("data/patch.xml", "<Patches>\n\n<Crate id=\"a\" patch=\"true\"><Seat/></Crate>\n</Patches>");

            var database = DefDatabase.Load(files, s_folders, Schemas());

            SchemaProblem problem = Assert.Single(database.Problems);
            Assert.Equal(("mod://mod/data/patch.xml", 3), (problem.File, problem.Line));
            Assert.Contains("<Seat> is not allowed in <Crate>", problem.Message);
        }
    }

    [Fact]
    public void AKindWithNoSchemaIsReadAsPlainXmlWithOneWarning()
    {
        (VirtualFileSystem files, TempFolder baseGame, TempFolder mod) = Layout();
        using (baseGame)
        using (mod)
        {
            baseGame.Write("data/barrels.xml", "<Barrels><Barrel id=\"a\" volume=\"40\"><Hoop>iron</Hoop></Barrel><Barrel id=\"b\"/></Barrels>");

            var database = DefDatabase.Load(files, s_folders, Schemas());

            SchemaProblem warning = Assert.Single(database.Problems);
            Assert.Equal(ProblemSeverity.Warning, warning.Severity);
            Assert.Contains("no schema describes <Barrel>", warning.Message);
            Assert.False(database.Get("Barrel", "a")!.Declared);
            Assert.Equal("{id: \"a\", volume: \"40\", Hoop: \"iron\"}", database.Get("Barrel", "a")!.Value.ToString());
        }
    }

    [Fact]
    public void WithoutSchemasEverythingIsPlainXmlAndQuiet()
    {
        (VirtualFileSystem files, TempFolder baseGame, TempFolder mod) = Layout();
        using (baseGame)
        using (mod)
        {
            baseGame.Write("data/crate.xml", "<Crate id=\"a\" weight=\"5\"/>");

            var database = DefDatabase.Load(files, s_folders);

            Assert.Empty(database.Problems);
            Assert.Equal("{id: \"a\", weight: \"5\"}", database.Get("Crate", "a")!.Value.ToString());
        }
    }

    [Fact]
    public void DifferentTypesMayShareAnId()
    {
        (VirtualFileSystem files, TempFolder baseGame, TempFolder mod) = Layout();
        using (baseGame)
        using (mod)
        {
            baseGame.Write("data/things.xml", "<Things><Crate id=\"a\"/><Barrel id=\"a\"/></Things>");

            var database = DefDatabase.Load(files, s_folders);

            Assert.Equal(["Barrel", "Crate"], database.Types);
            Assert.NotNull(database.Get("Crate", "a"));
            Assert.NotNull(database.Get("Barrel", "a"));
            Assert.DoesNotContain(database.Problems, p => p.Severity == ProblemSeverity.Error);
        }
    }

    [Fact]
    public void AMissingFolderIsJustEmpty()
    {
        var files = new VirtualFileSystem();
        files.Mount("res://", new DirectorySource(System.IO.Path.GetTempPath()));

        var database = DefDatabase.Load(files, [new DefFolder("base", "res://apogee-no-such-folder")]);

        Assert.Empty(database.All);
        Assert.Empty(database.Problems);
    }
}
