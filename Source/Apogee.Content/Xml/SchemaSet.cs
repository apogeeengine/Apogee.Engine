using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Apogee.Content.FileSystem;

namespace Apogee.Content.Xml;

/// <summary>
/// XSD, the parts content definitions need: checking XML against a schema and turning it into data (<see cref="Decode"/>). A set is every <c>.xsd</c> file loaded into it (and the
/// ones they include), so that a package can bring the schema of the definitions it adds, and the children it adds to an open element of another's.
/// <para>
/// Supported: element and attribute declarations (<c>type</c>, <c>ref</c> for elements, <c>default</c>, <c>fixed</c>, <c>use</c>, <c>minOccurs</c>, <c>maxOccurs</c>), <c>complexType</c> with
/// <c>sequence</c>, <c>choice</c> and <c>all</c> (the order of children is not checked; a choice's members each repeat as they say, so a choice repeated stands for any order),
/// <c>group</c> and <c>attributeGroup</c>, <c>complexContent</c> and <c>simpleContent</c> extensions, <c>any</c> and <c>anyAttribute</c>, <c>simpleType</c> restrictions
/// (<c>enumeration</c>, <c>minInclusive</c>, <c>maxInclusive</c>, <c>minExclusive</c>, <c>maxExclusive</c>, <c>pattern</c>, <c>minLength</c>, <c>maxLength</c>, <c>length</c>) and lists, the
/// built-in types, <c>annotation</c>, and <c>include</c>/<c>import</c> of a file by its location (no namespaces: names are plain). Anything else is a <see cref="SchemaException"/>
/// that names it, so a schema never means less than it says.
/// </para>
/// </summary>
public sealed partial class SchemaSet
{
    private readonly Dictionary<string, ParticleDecl> _elements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SimpleTypeDef> _simpleTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComplexTypeDef> _complexTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupDef> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupDef> _attributeGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _origins = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);

    /// <summary>The names of the global elements, sorted: the kinds of definition and open-content children the schemas describe.</summary>
    public IReadOnlyList<string> ElementNames => [.. _elements.Keys.Order(StringComparer.Ordinal)];

    public bool HasElement(string name) => _elements.ContainsKey(name);

    /// <summary>The <c>xs:documentation</c> of a global element, if it has one.</summary>
    public string? DocumentationOf(string element) => _elements.TryGetValue(element, out ParticleDecl? declaration) ? declaration.Documentation : null;

    // ---- Loading --------------------------------------------------------------------------------------------------------------------

    /// <summary>Loads the schema at <paramref name="path"/> (a virtual path) and the files it includes, which are found beside it. A file that is loaded already is not loaded again.</summary>
    /// <exception cref="SchemaException">The schema is invalid or uses something outside the supported subset.</exception>
    /// <exception cref="XmlParseException">A file is not well-formed XML.</exception>
    public void LoadFile(VirtualFileSystem files, string path)
    {
        string canonical = VfsPath.Parse(path);
        if (!_loaded.Add(canonical))
        {
            return;
        }

        ParseDocument(XmlNode.Parse(files.ReadAllText(canonical), canonical), canonical, files);
    }

    /// <summary>Loads every <c>.xsd</c> under a folder (sorted, folders last-in-first-out as <see cref="VirtualFileSystem.EnumerateFiles"/> lists them).</summary>
    public void LoadFolder(VirtualFileSystem files, string folder)
    {
        foreach (string path in files.EnumerateFiles(folder, "*.xsd", recursive: true))
        {
            LoadFile(files, path);
        }
    }

    /// <summary>Loads a schema from text. <paramref name="files"/> is where its includes are looked for (none: an include is an error).</summary>
    public void LoadText(string xsd, string origin = "schema.xsd", VirtualFileSystem? files = null) => ParseDocument(XmlNode.Parse(xsd, origin), origin, files);

    private void ParseDocument(XmlNode root, string origin, VirtualFileSystem? files)
    {
        if (root.LocalName != "schema")
        {
            throw new SchemaException(origin, root.Line, $"the root element is <{root.Name}>, not an xs:schema.");
        }

        foreach (XmlNode child in root.Children)
        {
            switch (child.LocalName)
            {
                case "annotation":
                    break;
                case "element":
                    ParticleDecl element = ParseElement(child, origin, 1, 1, inChoice: false);
                    if (element.Name.Length == 0 || element.Ref is not null)
                    {
                        throw new SchemaException(origin, child.Line, "a global element needs a name.");
                    }

                    Add(_elements, "element", element.Name, element, origin, child.Line);
                    break;
                case "complexType":
                    ComplexTypeDef complex = ParseComplexType(child, origin);
                    Add(_complexTypes, "type", RequireName(child, origin), complex, origin, child.Line);
                    break;
                case "simpleType":
                    SimpleTypeDef simple = ParseSimpleType(child, origin);
                    Add(_simpleTypes, "type", RequireName(child, origin), simple, origin, child.Line);
                    break;
                case "group":
                    GroupDef group = ParseGroup(child, origin);
                    Add(_groups, "group", group.Name, group, origin, child.Line);
                    break;
                case "attributeGroup":
                    GroupDef attributes = ParseAttributeGroup(child, origin);
                    Add(_attributeGroups, "attributeGroup", attributes.Name, attributes, origin, child.Line);
                    break;
                case "include" or "import":
                    LoadIncluded(child, origin, files);
                    break;
                default:
                    throw Unsupported(child, origin);
            }
        }
    }

    private void LoadIncluded(XmlNode node, string origin, VirtualFileSystem? files)
    {
        string location = node.Attribute("schemaLocation") ?? throw new SchemaException(origin, node.Line, $"<{node.Name}> needs a schemaLocation.");
        if (node.LocalName == "import" && node.HasAttribute("namespace"))
        {
            throw new SchemaException(origin, node.Line, "namespaces are not supported: schemas use plain names.");
        }

        if (files is null || !VfsPath.IsVirtual(origin))
        {
            throw new SchemaException(origin, node.Line, $"cannot include '{location}': this schema was not loaded from a file system.");
        }

        string target = VfsPath.Normalize(VfsPath.Combine(VfsPath.DirectoryOf(origin), location));
        try
        {
            LoadFile(files, target);
        }
        catch (System.IO.FileNotFoundException)
        {
            throw new SchemaException(origin, node.Line, $"the included schema '{location}' is not there (looked for {target}).");
        }
    }

    private void Add<T>(Dictionary<string, T> table, string kind, string name, T value, string origin, int line)
    {
        string key = kind + ":" + name;
        if (_origins.TryGetValue(key, out string? first))
        {
            throw new SchemaException(origin, line, $"{kind} '{name}' is declared twice (the first is in {first}).");
        }

        _origins[key] = origin;
        table[name] = value;
    }

    private static string RequireName(XmlNode node, string origin) =>
        node.Attribute("name") is { Length: > 0 } name ? name : throw new SchemaException(origin, node.Line, $"a global <{node.Name}> needs a name.");

    private static SchemaException Unsupported(XmlNode node, string origin) =>
        new(origin, node.Line, $"<{node.Name}> is not supported. The engine reads element, attribute, complexType, simpleType (restriction and list), group, attributeGroup, any, include and annotation.");

    // ---- Simple types ---------------------------------------------------------------------------------------------------------------

    private static SimpleTypeDef ParseSimpleType(XmlNode node, string origin)
    {
        var type = new SimpleTypeDef { Origin = origin, Line = node.Line };
        foreach (XmlNode child in node.Children)
        {
            switch (child.LocalName)
            {
                case "annotation":
                    type.Documentation = Documentation(child);
                    break;
                case "restriction":
                    type.BaseName = child.Attribute("base") ?? throw new SchemaException(origin, child.Line, "a restriction needs a base type.");
                    foreach (XmlNode facet in child.Children)
                    {
                        ParseFacet(facet, type, origin);
                    }

                    break;
                case "list":
                    type.IsList = true;
                    type.BaseName = child.Attribute("itemType") ?? throw new SchemaException(origin, child.Line, "a list needs an itemType.");
                    break;
                default:
                    throw Unsupported(child, origin);
            }
        }

        if (type.BaseName is null)
        {
            throw new SchemaException(origin, node.Line, "a simpleType needs a restriction or a list.");
        }

        return type;
    }

    private static void ParseFacet(XmlNode facet, SimpleTypeDef type, string origin)
    {
        string value = facet.Attribute("value") ?? "";
        switch (facet.LocalName)
        {
            case "enumeration":
                type.Enumeration.Add(value);
                break;
            case "minInclusive" or "minExclusive":
                type.Min = Number(facet, value, origin);
                type.MinExclusive = facet.LocalName == "minExclusive";
                break;
            case "maxInclusive" or "maxExclusive":
                type.Max = Number(facet, value, origin);
                type.MaxExclusive = facet.LocalName == "maxExclusive";
                break;
            case "pattern":
                type.Patterns.Add(value);
                break;
            case "minLength":
                type.MinLength = Count(facet, value, origin);
                break;
            case "maxLength":
                type.MaxLength = Count(facet, value, origin);
                break;
            case "length":
                type.Length = Count(facet, value, origin);
                break;
            case "annotation" or "whiteSpace" or "totalDigits" or "fractionDigits":
                break;
            default:
                throw Unsupported(facet, origin);
        }
    }

    private static double Number(XmlNode node, string value, string origin) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : throw new SchemaException(origin, node.Line, $"'{value}' is not a number.");

    private static int Count(XmlNode node, string value, string origin) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? count : throw new SchemaException(origin, node.Line, $"'{value}' is not a count.");

    // ---- Complex types --------------------------------------------------------------------------------------------------------------

    private ComplexTypeDef ParseComplexType(XmlNode node, string origin)
    {
        var type = new ComplexTypeDef { Name = node.Attribute("name") ?? "", Origin = origin, Line = node.Line };
        ParseTypeContent(node, type, origin);
        return type;
    }

    /// <summary>The members a complexType and its extensions share: content models, attributes, groups.</summary>
    private void ParseTypeContent(XmlNode container, ComplexTypeDef type, string origin)
    {
        foreach (XmlNode child in container.Children)
        {
            switch (child.LocalName)
            {
                case "annotation":
                    type.Documentation ??= Documentation(child);
                    break;
                case "sequence" or "choice" or "all":
                    ParseCompositor(child, type.Particles, 1, 1, inChoice: false, origin);
                    break;
                case "group":
                    type.Particles.Add(ParseGroupReference(child, origin, 1, 1, inChoice: false));
                    break;
                case "attribute":
                    type.Attributes.Add(ParseAttribute(child, origin));
                    break;
                case "attributeGroup":
                    type.AttributeGroupRefs.Add(child.Attribute("ref") ?? throw new SchemaException(origin, child.Line, "an attributeGroup inside a type needs a ref."));
                    break;
                case "anyAttribute":
                    type.AnyAttribute = true;
                    break;
                case "complexContent":
                    XmlNode extension = child.Children.Find(c => c.LocalName == "extension")
                        ?? throw new SchemaException(origin, child.Line, "complexContent needs an extension (a restriction is not supported).");
                    type.BaseName = extension.Attribute("base") ?? throw new SchemaException(origin, extension.Line, "an extension needs a base type.");
                    ParseTypeContent(extension, type, origin);
                    break;
                case "simpleContent":
                    XmlNode simple = child.Children.Find(c => c.LocalName == "extension")
                        ?? throw new SchemaException(origin, child.Line, "simpleContent needs an extension (a restriction is not supported).");
                    type.SimpleContent = true;
                    type.TextTypeName = simple.Attribute("base") ?? throw new SchemaException(origin, simple.Line, "an extension needs a base type.");
                    ParseTypeContent(simple, type, origin);
                    break;
                default:
                    throw Unsupported(child, origin);
            }
        }
    }

    /// <summary>
    /// The elements of a sequence, choice or all. Each gets the counts of the groups around it: the minimums and maximums multiply (a repeated sequence repeats its members), except that the
    /// members of a choice have no minimum, as any one may be the one chosen.
    /// </summary>
    private void ParseCompositor(XmlNode node, List<ParticleDecl> into, int groupMin, int groupMax, bool inChoice, string origin)
    {
        (int min, int max) = Combine(Occurs(node, origin), groupMin, groupMax, inChoice: false);
        List<XmlNode> members = [.. node.Children.Where(c => c.LocalName != "annotation")];
        bool choice = inChoice || node.LocalName == "choice" && members.Count > 1;
        foreach (XmlNode child in members)
        {
            switch (child.LocalName)
            {
                case "element":
                    into.Add(ParseElement(child, origin, min, max, choice));
                    break;
                case "sequence" or "choice" or "all":
                    ParseCompositor(child, into, min, max, choice, origin);
                    break;
                case "group":
                    into.Add(ParseGroupReference(child, origin, min, max, choice));
                    break;
                case "any":
                    (int anyMin, int anyMax) = Combine(Occurs(child, origin), min, max, choice);
                    into.Add(new ParticleDecl { IsAny = true, Min = anyMin, Max = anyMax, Origin = origin, Line = child.Line });
                    break;
                default:
                    throw Unsupported(child, origin);
            }
        }
    }

    private static ParticleDecl ParseGroupReference(XmlNode node, string origin, int groupMin, int groupMax, bool inChoice)
    {
        (int min, int max) = Combine(Occurs(node, origin), groupMin, groupMax, inChoice);
        return new ParticleDecl
        {
            GroupRef = node.Attribute("ref") ?? throw new SchemaException(origin, node.Line, "a group inside a type needs a ref."),
            Min = min,
            Max = max,
            Origin = origin,
            Line = node.Line,
        };
    }

    private GroupDef ParseGroup(XmlNode node, string origin)
    {
        var group = new GroupDef { Name = RequireName(node, origin), Origin = origin, Line = node.Line };
        foreach (XmlNode child in node.Children)
        {
            switch (child.LocalName)
            {
                case "annotation":
                    break;
                case "sequence" or "choice" or "all":
                    ParseCompositor(child, group.Particles, 1, 1, inChoice: false, origin);
                    break;
                default:
                    throw Unsupported(child, origin);
            }
        }

        return group;
    }

    private static GroupDef ParseAttributeGroup(XmlNode node, string origin)
    {
        var group = new GroupDef { Name = RequireName(node, origin), Origin = origin, Line = node.Line };
        foreach (XmlNode child in node.Children)
        {
            switch (child.LocalName)
            {
                case "annotation":
                    break;
                case "attribute":
                    group.Attributes.Add(ParseAttribute(child, origin));
                    break;
                case "attributeGroup":
                    group.AttributeGroupRefs.Add(child.Attribute("ref") ?? throw new SchemaException(origin, child.Line, "an attributeGroup inside a group needs a ref."));
                    break;
                default:
                    throw Unsupported(child, origin);
            }
        }

        return group;
    }

    private static AttributeDecl ParseAttribute(XmlNode node, string origin)
    {
        if (node.HasAttribute("ref"))
        {
            throw new SchemaException(origin, node.Line, "an attribute with a ref is not supported: declare the attribute where it is used (or use an attributeGroup).");
        }

        string use = node.Attribute("use") ?? "optional";
        var attribute = new AttributeDecl
        {
            Name = node.Attribute("name") is { Length: > 0 } name ? name : throw new SchemaException(origin, node.Line, "an attribute needs a name."),
            TypeName = node.Attribute("type"),
            Required = use == "required",
            Prohibited = use == "prohibited",
            Default = node.Attribute("default") ?? node.Attribute("fixed"),
            Origin = origin,
            Line = node.Line,
        };

        foreach (XmlNode child in node.Children)
        {
            switch (child.LocalName)
            {
                case "annotation":
                    attribute.Documentation = Documentation(child);
                    break;
                case "simpleType":
                    attribute.Inline = ParseSimpleType(child, origin);
                    break;
                default:
                    throw Unsupported(child, origin);
            }
        }

        return attribute;
    }

    private ParticleDecl ParseElement(XmlNode node, string origin, int groupMin, int groupMax, bool inChoice)
    {
        if (node.HasAttribute("substitutionGroup"))
        {
            throw new SchemaException(origin, node.Line, "substitutionGroup is not supported.");
        }

        (int min, int max) = Combine(Occurs(node, origin), groupMin, groupMax, inChoice);
        string? reference = node.Attribute("ref");
        var element = new ParticleDecl
        {
            Name = reference ?? node.Attribute("name") ?? throw new SchemaException(origin, node.Line, "an element needs a name or a ref."),
            Ref = reference,
            TypeName = node.Attribute("type"),
            Default = node.Attribute("default") ?? node.Attribute("fixed"),
            Min = min,
            Max = max,
            Origin = origin,
            Line = node.Line,
        };

        foreach (XmlNode child in node.Children)
        {
            switch (child.LocalName)
            {
                case "annotation":
                    element.Documentation = Documentation(child);
                    foreach (string word in Appinfo(child))
                    {
                        element.Map |= word == "map";
                        element.Defaults |= word == "defaults";
                    }

                    break;
                case "complexType":
                    element.InlineComplex = ParseComplexType(child, origin);
                    break;
                case "simpleType":
                    element.InlineSimple = ParseSimpleType(child, origin);
                    break;
                default:
                    throw Unsupported(child, origin);
            }
        }

        return element;
    }

    // ---- Small readers --------------------------------------------------------------------------------------------------------------

    /// <summary>The counts of a particle: <c>minOccurs</c> (default 1) and <c>maxOccurs</c> (default 1; -1 is <c>unbounded</c>).</summary>
    private static (int Min, int Max) Occurs(XmlNode node, string origin)
    {
        int min = 1;
        int max = 1;
        if (node.Attribute("minOccurs") is { } minText && !int.TryParse(minText, NumberStyles.None, CultureInfo.InvariantCulture, out min))
        {
            throw new SchemaException(origin, node.Line, $"minOccurs '{minText}' is not a count.");
        }

        if (node.Attribute("maxOccurs") is { } maxText)
        {
            if (maxText == "unbounded")
            {
                max = -1;
            }
            else if (!int.TryParse(maxText, NumberStyles.None, CultureInfo.InvariantCulture, out max))
            {
                throw new SchemaException(origin, node.Line, $"maxOccurs '{maxText}' is not a count or 'unbounded'.");
            }
        }

        return (min, max);
    }

    /// <summary>Counts of something inside a group of counts: minimums and maximums multiply (unbounded stays unbounded), and a member of a choice has no minimum.</summary>
    internal static (int Min, int Max) Combine((int Min, int Max) own, int groupMin, int groupMax, bool inChoice)
    {
        int min = inChoice ? 0 : own.Min * groupMin;
        int max = own.Max == 0 || groupMax == 0 ? 0 : own.Max == -1 || groupMax == -1 ? -1 : own.Max * groupMax;
        return (min, max);
    }

    private static string? Documentation(XmlNode annotation)
    {
        XmlNode? documentation = annotation.Children.Find(c => c.LocalName == "documentation");
        return documentation is { Text.Length: > 0 } ? documentation.Text : null;
    }

    /// <summary>The words of the <c>xs:appinfo</c> of an annotation (<c>map defaults</c>).</summary>
    private static IEnumerable<string> Appinfo(XmlNode annotation) =>
        annotation.Children.Where(c => c.LocalName == "appinfo").SelectMany(c => c.Text.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
}
