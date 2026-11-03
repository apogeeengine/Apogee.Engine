using System.Linq;
using Apogee.Content.Xml;
using Xunit;

namespace Apogee.Content.Tests;

public class XmlNodeTests
{
    [Fact]
    public void ItReadsTheTreeWithAttributesInOrderTextAndLines()
    {
        XmlNode root = XmlNode.Parse("""
            <Crate id="wooden" weight="12.5">
              <!-- comments are for people -->
              <Tag>heavy</Tag>
              <Tag>fragile</Tag>
              <Notes>  some   text  </Notes>
              <Empty/>
            </Crate>
            """, "crate.xml");

        Assert.Equal("Crate", root.Name);
        Assert.Equal(1, root.Line);
        Assert.Equal([new XmlProperty("id", "wooden"), new XmlProperty("weight", "12.5")], root.Attributes);
        Assert.Equal(["Tag", "Tag", "Notes", "Empty"], root.Children.Select(c => c.Name));
        Assert.Equal([3, 4, 5, 6], root.Children.Select(c => c.Line));
        Assert.Equal("heavy", root.Children[0].Text);
        Assert.Equal("some   text", root.Children[2].Text);
        Assert.Equal("", root.Children[3].Text);
        Assert.Equal("wooden", root.Attribute("id"));
        Assert.Null(root.Attribute("missing"));
        Assert.Same(root.Children[0], root.Child("Tag"));
    }

    [Fact]
    public void EntitiesAndCdataAreDecoded()
    {
        XmlNode root = XmlNode.Parse("<a t=\"x &amp; y &lt;z&gt;\">1 &lt; 2<![CDATA[ & <raw>]]></a>");

        Assert.Equal("x & y <z>", root.Attribute("t"));
        Assert.Equal("1 < 2 & <raw>", root.Text.Replace("  ", " "));
    }

    [Fact]
    public void ADeclaredPrefixKeepsItsNameAndHasALocalName()
    {
        XmlNode root = XmlNode.Parse("<xs:schema xmlns:xs=\"urn:x\" xmlns:xsi=\"urn:y\" xsi:noNamespaceSchemaLocation=\"a.xsd\"><xs:element name=\"A\"/></xs:schema>");

        Assert.Equal("xs:schema", root.Name);
        Assert.Equal("schema", root.LocalName);
        Assert.Equal("a.xsd", root.Attribute("xsi:noNamespaceSchemaLocation"));
        Assert.Same(root.Children[0], root.Child("element"));
    }

    [Fact]
    public void ADocumentTypeIsRefusedSoNothingLoadsFromOutsideTheFile()
    {
        var error = Assert.Throws<XmlParseException>(() => XmlNode.Parse("<!DOCTYPE a [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><a>&x;</a>", "evil.xml"));

        Assert.Equal("evil.xml", error.Origin);
        Assert.Contains("evil.xml:", error.Message);
    }

    [Fact]
    public void BrokenXmlSaysWhichLine()
    {
        var error = Assert.Throws<XmlParseException>(() => XmlNode.Parse("<a>\n  <b>\n</a>", "bad.xml"));

        Assert.Equal(3, error.Line);
        Assert.StartsWith("bad.xml:3:", error.Message);
        Assert.Throws<XmlParseException>(() => XmlNode.Parse("", "empty.xml"));
        Assert.Throws<XmlParseException>(() => XmlNode.Parse("<a/><b/>", "two.xml"));
    }

    [Fact]
    public void ItWritesBackTheSameTree()
    {
        string text = new XmlNode("Crate").ToXml();
        Assert.Equal("<Crate/>\n", text);

        XmlNode root = XmlNode.Parse("<Crate id=\"a&amp;b\" note=\"say &quot;hi&quot;\"><Tag>x &lt; y</Tag><Items><Item n=\"1\"/><Item n=\"2\"/></Items></Crate>");
        string written = root.ToXml();

        Assert.Equal(
            "<Crate id=\"a&amp;b\" note=\"say &quot;hi&quot;\">\n  <Tag>x &lt; y</Tag>\n  <Items>\n    <Item n=\"1\"/>\n    <Item n=\"2\"/>\n  </Items>\n</Crate>\n",
            written);
        Assert.Equal(written, XmlNode.Parse(written).ToXml());
    }

    [Fact]
    public void ACloneIsIndependent()
    {
        XmlNode root = XmlNode.Parse("<a x=\"1\"><b/></a>");
        XmlNode copy = root.Clone();

        copy.SetAttribute("x", "2");
        copy.SetAttribute("y", "3");
        copy.Children.Clear();

        Assert.Equal("1", root.Attribute("x"));
        Assert.Null(root.Attribute("y"));
        Assert.Single(root.Children);
        Assert.True(copy.RemoveAttribute("y"));
        Assert.False(copy.RemoveAttribute("y"));
    }
}

public class SchemaTests
{
    private const string Header = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">";

    private static SchemaSet Schema(string body)
    {
        var set = new SchemaSet();
        set.LoadText(Header + body + "</xs:schema>", "test.xsd");
        return set;
    }

    private static DecodeResult Decode(SchemaSet set, string xml) => set.Decode(XmlNode.Parse(xml, "def.xml"), "def.xml");

    private static string[] Messages(DecodeResult result) => [.. result.Problems.Select(p => p.Message)];

    private const string Crate = """
        <xs:element name="Crate">
          <xs:complexType>
            <xs:attribute name="id" type="xs:string" use="required"/>
            <xs:attribute name="weight" type="xs:decimal" default="1"/>
            <xs:attribute name="stackable" type="xs:boolean" default="true"/>
            <xs:attribute name="slots" type="xs:int"/>
          </xs:complexType>
        </xs:element>
        """;

    [Fact]
    public void AttributesAreTypedAndTheDefaultsOfTheOnesLeftOutAreFilledIn()
    {
        SchemaSet set = Schema(Crate);

        DecodeResult result = Decode(set, "<Crate id=\"wooden\" weight=\"12.5\"/>");

        Assert.True(result.Declared);
        Assert.Empty(result.Problems);
        Assert.Equal("{id: \"wooden\", weight: 12.5, stackable: true}", result.Value.ToString());
        Assert.Equal("{id: \"a\", weight: 1, stackable: false, slots: 4}", Decode(set, "<Crate id=\"a\" stackable=\"0\" slots=\"4\"/>").Value.ToString());
    }

    [Fact]
    public void ProblemsSayWhereAndWhatAndTheValueIsStillThere()
    {
        SchemaSet set = Schema(Crate);

        DecodeResult result = Decode(set, "<Crate\n weight=\"heavy\" slots=\"2.5\" colour=\"red\" stackable=\"maybe\"/>");

        Assert.True(result.HasErrors);
        Assert.Equal(
            [
                "<Crate> needs the attribute 'id'.",
                "'heavy' is not a number",
                "'maybe' is not a boolean (true or false)",
                "'2.5' is not a whole number",
                "'colour' is not an attribute of <Crate> (it has: id, weight, stackable, slots).",
            ],
            Messages(result));
        Assert.All(result.Problems, p => Assert.Equal(("def.xml", 1), (p.File, p.Line)));
        Assert.Equal(["Crate", "Crate.weight", "Crate.stackable", "Crate.slots", "Crate"], result.Problems.Select(p => p.Path));
        Assert.Contains("weight: \"heavy\"", result.Value.ToString()); // what could not be converted stays as text
        Assert.Contains("def.xml:1:", result.Problems[0].ToString());
    }

    [Fact]
    public void TheAttributesOfTheContentSystemAndOfXmlAreAllowedEverywhere()
    {
        SchemaSet set = Schema(Crate);

        DecodeResult result = Decode(set, "<Crate xmlns:xsi=\"urn:x\" xsi:noNamespaceSchemaLocation=\"c.xsd\" patch=\"true\" remove=\"false\" id=\"a\"/>");

        Assert.Empty(result.Problems);
        Assert.Equal("{id: \"a\", weight: 1, stackable: true}", result.Value.ToString());
    }

    [Fact]
    public void AnElementWithNoDeclarationIsDecodedGenericallyWithoutProblems()
    {
        SchemaSet set = Schema(Crate);

        DecodeResult result = Decode(set, "<Barrel volume=\"40\"><Part>lid</Part><Part>base</Part><Name>old</Name></Barrel>");

        Assert.False(result.Declared);
        Assert.Empty(result.Problems);
        Assert.Equal("{volume: \"40\", Part: [\"lid\", \"base\"], Name: \"old\"}", result.Value.ToString());
        Assert.Equal("{id: \"b\", weight: 1, stackable: true}", set.Decode(XmlNode.Parse("<Other id=\"b\"/>"), null, "Crate").Value.ToString());
    }

    [Fact]
    public void ChildElementsAreMembersAndRepeatableOnesAreLists()
    {
        SchemaSet set = Schema("""
            <xs:element name="Machine">
              <xs:complexType>
                <xs:sequence>
                  <xs:element name="Engine"><xs:complexType><xs:attribute name="power" type="xs:int"/></xs:complexType></xs:element>
                  <xs:element name="Wheel" maxOccurs="unbounded"><xs:complexType><xs:attribute name="radius" type="xs:double"/></xs:complexType></xs:element>
                  <xs:element name="Note" type="xs:string" minOccurs="0"/>
                </xs:sequence>
                <xs:attribute name="name" type="xs:string"/>
              </xs:complexType>
            </xs:element>
            """);

        DecodeResult result = Decode(set, "<Machine name=\"truck\"><Engine power=\"90\"/><Wheel radius=\"0.5\"/><Wheel radius=\"0.8\"/><Note>old</Note></Machine>");

        Assert.Empty(result.Problems);
        Assert.Equal("{name: \"truck\", Engine: {power: 90}, Wheel: [{radius: 0.5}, {radius: 0.8}], Note: \"old\"}", result.Value.ToString());
        Assert.Equal("{name: \"x\", Engine: {power: 1}, Wheel: [{radius: 2}]}", Decode(set, "<Machine name=\"x\"><Wheel radius=\"2\"/><Engine power=\"1\"/></Machine>").Value.ToString()); // order is free
    }

    [Fact]
    public void HowOftenAnElementMayAppearIsChecked()
    {
        SchemaSet set = Schema("""
            <xs:element name="Machine">
              <xs:complexType>
                <xs:sequence>
                  <xs:element name="Engine" type="xs:string"/>
                  <xs:element name="Wheel" type="xs:string" minOccurs="2" maxOccurs="4"/>
                </xs:sequence>
              </xs:complexType>
            </xs:element>
            """);

        Assert.Equal(
            ["<Machine> needs a <Engine>.", "<Machine> needs at least 2 <Wheel> (it has 1)."],
            Messages(Decode(set, "<Machine><Wheel>a</Wheel></Machine>")));
        Assert.Equal(
            ["<Machine> has 2 <Engine>, it may have one.", "<Machine> has 5 <Wheel>, at most 4."],
            Messages(Decode(set, "<Machine><Engine>a</Engine><Engine>b</Engine><Wheel>1</Wheel><Wheel>2</Wheel><Wheel>3</Wheel><Wheel>4</Wheel><Wheel>5</Wheel></Machine>")));
        Assert.Equal(
            ["<Seat> is not allowed in <Machine> (allowed: Engine, Wheel)."],
            Messages(Decode(set, "<Machine><Engine>a</Engine><Wheel>1</Wheel><Wheel>2</Wheel><Seat/></Machine>")));
    }

    [Fact]
    public void AnElementThatHoldsOnlyARepeatedElementIsThatList()
    {
        SchemaSet set = Schema("""
            <xs:element name="Fleet">
              <xs:complexType>
                <xs:sequence>
                  <xs:element name="Units">
                    <xs:complexType>
                      <xs:sequence><xs:element name="Unit" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:attribute name="id" type="xs:string"/></xs:complexType></xs:element></xs:sequence>
                    </xs:complexType>
                  </xs:element>
                </xs:sequence>
              </xs:complexType>
            </xs:element>
            """);

        Assert.Equal("{Units: [{id: \"a\"}, {id: \"b\"}]}", Decode(set, "<Fleet><Units><Unit id=\"a\"/><Unit id=\"b\"/></Units></Fleet>").Value.ToString());
        Assert.Equal("{Units: []}", Decode(set, "<Fleet><Units/></Fleet>").Value.ToString());
    }

    [Fact]
    public void AChoiceRepeatedStandsForAnyOrder()
    {
        SchemaSet set = Schema("""
            <xs:element name="Plan">
              <xs:complexType>
                <xs:choice minOccurs="0" maxOccurs="unbounded">
                  <xs:element name="Sow" type="xs:string" maxOccurs="unbounded"/>
                  <xs:element name="Reap" type="xs:string" maxOccurs="unbounded"/>
                </xs:choice>
              </xs:complexType>
            </xs:element>
            """);

        DecodeResult result = Decode(set, "<Plan><Sow>a</Sow><Reap>b</Reap><Sow>c</Sow></Plan>");

        Assert.Empty(result.Problems);
        Assert.Equal("{Sow: [\"a\", \"c\"], Reap: [\"b\"]}", result.Value.ToString());
        Assert.Empty(Decode(set, "<Plan/>").Problems);
    }

    [Fact]
    public void ARequiredChoiceOfOneThingStillNeedsIt()
    {
        SchemaSet set = Schema("""
            <xs:element name="Gate"><xs:complexType><xs:choice><xs:element name="Lock" type="xs:string"/></xs:choice></xs:complexType></xs:element>
            """);

        Assert.Equal(["<Gate> needs a <Lock>."], Messages(Decode(set, "<Gate/>")));
    }

    [Fact]
    public void SimpleTypesRestrictValuesWithFacets()
    {
        SchemaSet set = Schema("""
            <xs:simpleType name="Percent"><xs:restriction base="xs:int"><xs:minInclusive value="0"/><xs:maxInclusive value="100"/></xs:restriction></xs:simpleType>
            <xs:simpleType name="Gear"><xs:restriction base="xs:string"><xs:enumeration value="low"/><xs:enumeration value="high"/></xs:restriction></xs:simpleType>
            <xs:simpleType name="Code"><xs:restriction base="xs:string"><xs:pattern value="[A-Z]{2}[0-9]+"/><xs:maxLength value="5"/></xs:restriction></xs:simpleType>
            <xs:simpleType name="Positive"><xs:restriction base="xs:decimal"><xs:minExclusive value="0"/></xs:restriction></xs:simpleType>
            <xs:element name="Gauge">
              <xs:complexType>
                <xs:attribute name="level" type="Percent"/>
                <xs:attribute name="gear" type="Gear"/>
                <xs:attribute name="code" type="Code"/>
                <xs:attribute name="rate" type="Positive"/>
                <xs:attribute name="small" type="xs:byte"/>
                <xs:attribute name="count" type="xs:unsignedByte"/>
              </xs:complexType>
            </xs:element>
            """);

        Assert.Empty(Decode(set, "<Gauge level=\"100\" gear=\"low\" code=\"AB12\" rate=\"0.5\" small=\"-128\" count=\"255\"/>").Problems);
        Assert.Equal(
            [
                "101 is above the maximum 100",
                "'mid' is not one of: low, high",
                "'ab12' does not match the pattern '[A-Z]{2}[0-9]+'",
                "0 is not above the minimum 0",
                "128 is above the maximum 127",
                "-1 is below the minimum 0",
            ],
            Messages(Decode(set, "<Gauge level=\"101\" gear=\"mid\" code=\"ab12\" rate=\"0\" small=\"128\" count=\"-1\"/>")));
        Assert.Equal(["'AB12345' has 7 characters, more than 5"], Messages(Decode(set, "<Gauge code=\"AB12345\"/>")));
    }

    [Fact]
    public void ARestrictionOfARestrictionKeepsTheFacetsOfBoth()
    {
        SchemaSet set = Schema("""
            <xs:simpleType name="Small"><xs:restriction base="xs:int"><xs:maxInclusive value="10"/></xs:restriction></xs:simpleType>
            <xs:simpleType name="SmallPositive"><xs:restriction base="Small"><xs:minInclusive value="1"/></xs:restriction></xs:simpleType>
            <xs:element name="N"><xs:complexType><xs:attribute name="v" type="SmallPositive"/></xs:complexType></xs:element>
            """);

        Assert.Empty(Decode(set, "<N v=\"5\"/>").Problems);
        Assert.Equal(["11 is above the maximum 10"], Messages(Decode(set, "<N v=\"11\"/>")));
        Assert.Equal(["0 is below the minimum 1"], Messages(Decode(set, "<N v=\"0\"/>")));
    }

    [Fact]
    public void AListTypeIsAListOfItsItems()
    {
        SchemaSet set = Schema("""
            <xs:simpleType name="Floats"><xs:list itemType="xs:float"/></xs:simpleType>
            <xs:simpleType name="Triple"><xs:restriction base="Floats"><xs:length value="3"/></xs:restriction></xs:simpleType>
            <xs:element name="Point"><xs:complexType><xs:attribute name="pos" type="Triple"/><xs:attribute name="tags"><xs:simpleType><xs:list itemType="xs:string"/></xs:simpleType></xs:attribute></xs:complexType></xs:element>
            """);

        Assert.Equal("{pos: [1, 2.5, -3], tags: [\"a\", \"b\"]}", Decode(set, "<Point pos=\"1  2.5 -3\" tags=\"a b\"/>").Value.ToString());
        Assert.Equal(["'1 2' has 2 items, not 3"], Messages(Decode(set, "<Point pos=\"1 2\"/>")));
        Assert.Equal(["'x' is not a number"], Messages(Decode(set, "<Point pos=\"1 x 3\"/>")));
    }

    [Fact]
    public void AnElementWithASimpleTypeIsItsValue()
    {
        SchemaSet set = Schema("""
            <xs:element name="Speed" type="xs:double" default="10"/>
            <xs:element name="Holder"><xs:complexType><xs:sequence><xs:element ref="Speed"/></xs:sequence></xs:complexType></xs:element>
            """);

        Assert.Equal("4.5", Decode(set, "<Speed>4.5</Speed>").Value.ToString());
        Assert.Equal("10", Decode(set, "<Speed/>").Value.ToString());
        Assert.Equal("{Speed: 2}", Decode(set, "<Holder><Speed>2</Speed></Holder>").Value.ToString());
        Assert.Equal(["'fast' is not a number"], Messages(Decode(set, "<Speed>fast</Speed>")));
        Assert.Equal(["<Speed> holds a value, not elements."], Messages(Decode(set, "<Speed><x/></Speed>")));
    }

    [Fact]
    public void AnExtensionAddsToItsBaseAndGroupsAreSharedParts()
    {
        SchemaSet set = Schema("""
            <xs:attributeGroup name="Identity"><xs:attribute name="id" type="xs:string" use="required"/><xs:attribute name="label" type="xs:string"/></xs:attributeGroup>
            <xs:group name="Parts"><xs:sequence><xs:element name="Part" type="xs:string" minOccurs="0" maxOccurs="unbounded"/></xs:sequence></xs:group>
            <xs:complexType name="Thing"><xs:attributeGroup ref="Identity"/><xs:group ref="Parts"/></xs:complexType>
            <xs:complexType name="Vehicle">
              <xs:complexContent>
                <xs:extension base="Thing"><xs:sequence><xs:element name="Seat" type="xs:int" minOccurs="0"/></xs:sequence><xs:attribute name="speed" type="xs:double" default="5"/></xs:extension>
              </xs:complexContent>
            </xs:complexType>
            <xs:element name="Car" type="Vehicle"/>
            <xs:element name="Cart" type="Thing"/>
            """);

        Assert.Equal("{id: \"c\", label: \"red\", speed: 5, Part: [\"a\", \"b\"], Seat: 4}", Decode(set, "<Car id=\"c\" label=\"red\"><Part>a</Part><Seat>4</Seat><Part>b</Part></Car>").Value.ToString());
        Assert.Equal("{id: \"x\", Part: [\"p\"]}", Decode(set, "<Cart id=\"x\"><Part>p</Part></Cart>").Value.ToString());
        Assert.Equal(["<Cart> needs the attribute 'id'."], Messages(Decode(set, "<Cart/>")));
        Assert.Equal(["'speed' is not an attribute of <Cart> (it has: id, label)."], Messages(Decode(set, "<Cart id=\"x\" speed=\"1\"/>")));
    }

    [Fact]
    public void SimpleContentIsTextWithAttributes()
    {
        SchemaSet set = Schema("""
            <xs:element name="Price">
              <xs:complexType><xs:simpleContent><xs:extension base="xs:decimal"><xs:attribute name="currency" type="xs:string" default="EUR"/></xs:extension></xs:simpleContent></xs:complexType>
            </xs:element>
            <xs:element name="Plain">
              <xs:complexType><xs:simpleContent><xs:extension base="xs:int"/></xs:simpleContent></xs:complexType>
            </xs:element>
            """);

        Assert.Equal("{currency: \"EUR\", value: 9.5}", Decode(set, "<Price>9.5</Price>").Value.ToString());
        Assert.Equal("{currency: \"USD\", value: 3}", Decode(set, "<Price currency=\"USD\">3</Price>").Value.ToString());
        Assert.Equal("7", Decode(set, "<Plain>7</Plain>").Value.ToString());
        Assert.Equal(["'many' is not a number"], Messages(Decode(set, "<Price>many</Price>")));
    }

    [Fact]
    public void AnOpenElementTakesItsChildrenFromTheElementsNamedAfterIt()
    {
        SchemaSet set = Schema("""
            <xs:element name="Vehicle">
              <xs:complexType><xs:sequence><xs:element name="components"><xs:complexType><xs:sequence><xs:any minOccurs="0" maxOccurs="unbounded"/></xs:sequence></xs:complexType></xs:element></xs:sequence></xs:complexType>
            </xs:element>
            <xs:element name="components.motor"><xs:complexType><xs:attribute name="power" type="xs:int" default="50"/></xs:complexType></xs:element>
            """);

        DecodeResult result = Decode(set, "<Vehicle><components><motor power=\"90\"/><lamp colour=\"red\"/><motor/></components></Vehicle>");

        Assert.Empty(result.Problems);
        Assert.Equal("{components: {motor: [{power: 90}, {power: 50}], lamp: {colour: \"red\"}}}", result.Value.ToString());
        Assert.Equal(["'extra' is not an attribute of <motor> (it has: power)."], Messages(Decode(set, "<Vehicle><components><motor power=\"1\" extra=\"2\"/></components></Vehicle>")));
    }

    [Fact]
    public void AnyAttributeKeepsTheAttributesItDoesNotKnowAsText()
    {
        SchemaSet set = Schema("""
            <xs:element name="Meta"><xs:complexType><xs:attribute name="id" type="xs:string"/><xs:anyAttribute/></xs:complexType></xs:element>
            """);

        Assert.Equal("{id: \"a\", note: \"hi\", level: \"3\"}", Decode(set, "<Meta id=\"a\" note=\"hi\" level=\"3\"/>").Value.ToString());
    }

    [Fact]
    public void TheMapConventionMakesAContainerOfEntriesARecordByKey()
    {
        SchemaSet set = Schema("""
            <xs:element name="Prices">
              <xs:annotation><xs:appinfo>map</xs:appinfo></xs:annotation>
              <xs:complexType>
                <xs:sequence><xs:element name="entry" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:attribute name="key" type="xs:string" use="required"/><xs:attribute name="value" type="xs:decimal" use="required"/></xs:complexType></xs:element></xs:sequence>
              </xs:complexType>
            </xs:element>
            <xs:element name="Stats">
              <xs:annotation><xs:appinfo>map</xs:appinfo></xs:annotation>
              <xs:complexType>
                <xs:sequence><xs:element name="entry" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:attribute name="key" type="xs:string"/><xs:attribute name="min" type="xs:int"/><xs:attribute name="max" type="xs:int"/></xs:complexType></xs:element></xs:sequence>
              </xs:complexType>
            </xs:element>
            """);

        Assert.Equal("{wheat: 2.5, corn: 3}", Decode(set, "<Prices><entry key=\"wheat\" value=\"2.5\"/><entry key=\"corn\" value=\"3\"/></Prices>").Value.ToString());
        Assert.Equal("{speed: {min: 0, max: 9}}", Decode(set, "<Stats><entry key=\"speed\" min=\"0\" max=\"9\"/></Stats>").Value.ToString());
        Assert.Equal(
            ["the key 'a' is used twice in <Prices>."],
            Messages(Decode(set, "<Prices><entry key=\"a\" value=\"1\"/><entry key=\"a\" value=\"2\"/></Prices>")));
    }

    [Fact]
    public void TheDefaultsConventionDecodesWhatIsLeftOutAsEmpty()
    {
        SchemaSet set = Schema("""
            <xs:element name="Unit">
              <xs:complexType>
                <xs:sequence>
                  <xs:element name="Limits" minOccurs="0">
                    <xs:annotation><xs:appinfo>defaults</xs:appinfo></xs:annotation>
                    <xs:complexType><xs:attribute name="max" type="xs:int" default="100"/></xs:complexType>
                  </xs:element>
                  <xs:element name="Tag" type="xs:string" minOccurs="0" maxOccurs="unbounded">
                    <xs:annotation><xs:appinfo>defaults</xs:appinfo></xs:annotation>
                  </xs:element>
                  <xs:element name="Other" type="xs:string" minOccurs="0"/>
                </xs:sequence>
              </xs:complexType>
            </xs:element>
            """);

        Assert.Equal("{Limits: {max: 100}, Tag: []}", Decode(set, "<Unit/>").Value.ToString());
        Assert.Equal("{Limits: {max: 7}, Tag: [\"a\"]}", Decode(set, "<Unit><Limits max=\"7\"/><Tag>a</Tag></Unit>").Value.ToString());
    }

    [Fact]
    public void AnIncludeIsReadFromBesideTheSchemaAndOnlyOnce()
    {
        using var folder = new TempFolder();
        folder.Write("types.xsd", Header + "<xs:simpleType name=\"Id\"><xs:restriction base=\"xs:string\"><xs:pattern value=\"[a-z_]+\"/></xs:restriction></xs:simpleType></xs:schema>");
        folder.Write("main.xsd", Header + "<xs:include schemaLocation=\"types.xsd\"/><xs:element name=\"A\"><xs:complexType><xs:attribute name=\"id\" type=\"Id\"/></xs:complexType></xs:element></xs:schema>");
        folder.Write("sub/other.xsd", Header + "<xs:include schemaLocation=\"../types.xsd\"/><xs:element name=\"B\"><xs:complexType><xs:attribute name=\"id\" type=\"Id\"/></xs:complexType></xs:element></xs:schema>");
        var files = new Apogee.Content.FileSystem.VirtualFileSystem();
        files.Mount("res://", new Apogee.Content.FileSystem.DirectorySource(folder.Root));
        var set = new SchemaSet();

        set.LoadFolder(files, "res://");

        Assert.Equal(["A", "B"], set.ElementNames);
        Assert.Empty(Decode(set, "<A id=\"ok_id\"/>").Problems);
        Assert.Equal(["'Bad1' does not match the pattern '[a-z_]+'"], Messages(Decode(set, "<B id=\"Bad1\"/>")));
    }

    [Fact]
    public void ASchemaThatIsWrongOrAsksForMoreThanTheSubsetSaysSoWithTheLine()
    {
        static SchemaException Fails(string body)
        {
            var set = new SchemaSet();
            return Assert.Throws<SchemaException>(() => set.LoadText(Header + "\n" + body + "</xs:schema>", "bad.xsd"));
        }

        Assert.Contains("bad.xsd:2: <xs:key> is not supported", Fails("<xs:element name=\"A\"><xs:key name=\"k\"/></xs:element>").Message);
        Assert.Contains("<xs:union> is not supported", Fails("<xs:simpleType name=\"U\"><xs:union memberTypes=\"xs:int xs:string\"/></xs:simpleType>").Message);
        Assert.Contains("<xs:redefine> is not supported", Fails("<xs:redefine schemaLocation=\"x.xsd\"/>").Message);
        Assert.Contains("substitutionGroup is not supported", Fails("<xs:element name=\"A\" substitutionGroup=\"B\"/>").Message);
        Assert.Contains("namespaces are not supported", Fails("<xs:import namespace=\"urn:x\" schemaLocation=\"x.xsd\"/>").Message);
        Assert.Contains("element 'A' is declared twice", Fails("<xs:element name=\"A\"/>\n<xs:element name=\"A\"/>").Message);
        Assert.Contains("bad.xsd:3:", Fails("<xs:element name=\"A\"/>\n<xs:element name=\"A\"/>").Message);
        Assert.Contains("minOccurs 'x' is not a count", Fails("<xs:element name=\"A\"><xs:complexType><xs:sequence><xs:element name=\"B\" minOccurs=\"x\"/></xs:sequence></xs:complexType></xs:element>").Message);
        Assert.Contains("needs a name", Fails("<xs:element type=\"xs:int\"/>").Message);
        Assert.Contains("cannot include 'x.xsd'", Fails("<xs:include schemaLocation=\"x.xsd\"/>").Message);
        Assert.Throws<SchemaException>(() => new SchemaSet().LoadText("<notaschema/>", "x.xsd"));
    }

    [Fact]
    public void ABrokenReferenceIsAProblemWhereItIsUsed()
    {
        SchemaSet set = Schema("""
            <xs:element name="A"><xs:complexType><xs:attribute name="x" type="Missing"/></xs:complexType></xs:element>
            <xs:element name="B" type="AlsoMissing"/>
            <xs:element name="C"><xs:complexType><xs:sequence><xs:element ref="Nowhere"/></xs:sequence></xs:complexType></xs:element>
            <xs:simpleType name="Loop"><xs:restriction base="Loop"/></xs:simpleType>
            <xs:element name="D"><xs:complexType><xs:attribute name="v" type="Loop"/></xs:complexType></xs:element>
            <xs:simpleType name="BadPattern"><xs:restriction base="xs:string"><xs:pattern value="[a-"/></xs:restriction></xs:simpleType>
            <xs:element name="E"><xs:complexType><xs:attribute name="v" type="BadPattern"/></xs:complexType></xs:element>
            """);

        Assert.Contains("the type 'Missing' of the attribute 'x' is not defined", Messages(Decode(set, "<A x=\"1\"/>")).Single());
        Assert.Equal(["the schemas have no type 'AlsoMissing'."], Messages(Decode(set, "<B/>")));
        Assert.Equal(["the schemas have no element 'Nowhere'."], Messages(Decode(set, "<C><Nowhere/></C>")));
        Assert.Contains("restricts itself", Messages(Decode(set, "<D v=\"1\"/>")).Single());
        Assert.Contains("not a valid regular expression", Messages(Decode(set, "<E v=\"1\"/>")).Single());
    }

    [Fact]
    public void ADocumentationIsKept()
    {
        SchemaSet set = Schema("<xs:element name=\"Crate\"><xs:annotation><xs:documentation>A box.</xs:documentation></xs:annotation><xs:complexType/></xs:element><xs:element name=\"Bare\"/>");

        Assert.Equal("A box.", set.DocumentationOf("Crate"));
        Assert.Null(set.DocumentationOf("Bare"));
        Assert.Null(set.DocumentationOf("Nope"));
    }
    [Fact]
    public void AVector3AttributeIsThreeNumbers()
    {
        SchemaSet set = Schema("<xs:element name=\"Spot\"><xs:complexType><xs:attribute name=\"at\" type=\"xs:vector3\" default=\"0 1 0\"/></xs:complexType></xs:element>");

        DecodeResult spaced = Decode(set, "<Spot at=\"1 2.5 -3\"/>");
        DecodeResult commas = Decode(set, "<Spot at=\" 4, 5,6 \"/>");
        DecodeResult missing = Decode(set, "<Spot/>");

        Assert.Empty(spaced.Problems);
        Assert.Equal("{at: vec(1, 2.5, -3)}", spaced.Value.ToString());
        Assert.Equal("{at: vec(4, 5, 6)}", commas.Value.ToString());
        Assert.Equal("{at: vec(0, 1, 0)}", missing.Value.ToString());
        Assert.Contains("is not a vector", Messages(Decode(set, "<Spot at=\"1 2\"/>")).Single());
        Assert.Contains("is not a vector", Messages(Decode(set, "<Spot at=\"1 2 x\"/>")).Single());
    }
}
