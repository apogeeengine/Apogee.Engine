using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Content.FileSystem;

namespace Apogee.Content.Xml;

/// <summary>A folder of definitions: where it is in the file system, and the package it belongs to (the first thing that loads is the game's own).</summary>
public sealed record DefFolder(string Package, string Path);

/// <summary>What a file did to a definition.</summary>
public enum DefRole
{
    /// <summary>It defined it (the first file to do so).</summary>
    Definition,

    /// <summary>It defined it again: the definition now is this file's, and the earlier one is gone.</summary>
    Replacement,

    /// <summary>It changed it (<c>patch="true"</c>).</summary>
    Patch,
}

/// <summary>A file that made a definition: <see cref="Line"/> is where the definition starts in it.</summary>
public sealed record DefSource(string Package, string Path, int Line, DefRole Role);

/// <summary>One definition: an element with an <c>id</c>, as it is after every file that touched it, and what it decodes to.</summary>
public sealed class Def
{
    internal Def(string type, string id, XmlNode node, DefSource source)
    {
        Type = type;
        Id = id;
        Node = node;
        Sources = [source];
        Value = new DefRecord();
    }

    /// <summary>The element's name: <c>Crate</c> in <c>&lt;Crate id="wooden"/&gt;</c>.</summary>
    public string Type { get; }

    public string Id { get; }

    /// <summary>The definition's XML with its patches merged in.</summary>
    public XmlNode Node { get; internal set; }

    /// <summary>Decoded against the schemas (<see cref="SchemaSet.Decode"/>), or generically when there is no schema for the type.</summary>
    public DefValue Value { get; internal set; }

    /// <summary>True when a schema described the type.</summary>
    public bool Declared { get; internal set; }

    /// <summary>The files that made the definition, in the order they were loaded: the one that defined it, then its patches. A mod patching a definition can find the files beside the original from here.</summary>
    public List<DefSource> Sources { get; }

    /// <summary>The file that defines it now.</summary>
    public DefSource Defined => Sources.Find(s => s.Role != DefRole.Patch)!;
}

/// <summary>
/// The definitions of a game and its mods: every <c>.xml</c> file of the content folders, read in load order (a folder's files sorted, then its folders, each the same way), each
/// file holding one definition (a root element with an <c>id</c>) or a list of them (the root's children). A definition is identified by its type (the element's name) and its id.
/// A later file <em>replaces</em> a definition with the same id (with a warning that names both files), <em>patches</em> it with <c>patch="true"</c> (see <see cref="Merge"/>), or
/// <em>removes</em> it with <c>remove="true"</c>. Each definition is then checked against the schemas and decoded; what is wrong is in <see cref="Problems"/>, and the definition is
/// still there, with whatever could be made of it.
/// </summary>
public sealed class DefDatabase
{
    private readonly List<Def> _defs = [];
    private readonly Dictionary<(string Type, string Id), Def> _byKey = [];
    private readonly List<SchemaProblem> _problems = [];

    private DefDatabase()
    {
    }

    /// <summary>Every definition, in the order they were first defined.</summary>
    public IReadOnlyList<Def> All => _defs;

    /// <summary>The types that have definitions, sorted.</summary>
    public IReadOnlyList<string> Types => [.. _defs.Select(d => d.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public IReadOnlyList<SchemaProblem> Problems => _problems;

    public bool HasErrors => _problems.Any(p => p.Severity == ProblemSeverity.Error);

    public IReadOnlyList<Def> OfType(string type) => [.. _defs.Where(d => d.Type == type)];

    public Def? Get(string type, string id) => _byKey.GetValueOrDefault((type, id));

    /// <summary>Reads the definitions in <paramref name="folders"/> (in this order) from <paramref name="files"/> and checks them against <paramref name="schemas"/> (none: they are read as plain XML).</summary>
    public static DefDatabase Load(VirtualFileSystem files, IReadOnlyList<DefFolder> folders, SchemaSet? schemas = null)
    {
        var database = new DefDatabase();
        foreach (DefFolder folder in folders)
        {
            foreach (string path in FilesInOrder(files, folder.Path))
            {
                database.ReadFile(files, folder, path);
            }
        }

        database.Decode(schemas);
        return database;
    }

    private static IEnumerable<string> FilesInOrder(VirtualFileSystem files, string folder)
    {
        foreach (string file in files.EnumerateFiles(folder, "*.xml"))
        {
            yield return file;
        }

        foreach (string directory in files.EnumerateDirectories(folder))
        {
            foreach (string file in FilesInOrder(files, directory))
            {
                yield return file;
            }
        }
    }

    private void ReadFile(VirtualFileSystem files, DefFolder folder, string path)
    {
        XmlNode root;
        try
        {
            root = XmlNode.Parse(files.ReadAllText(path), path);
        }
        catch (XmlParseException e)
        {
            _problems.Add(new SchemaProblem(ProblemSeverity.Error, "", path, e.Line, "the file is not well-formed XML: " + e.Reason));
            return;
        }

        IEnumerable<XmlNode> nodes = root.HasAttribute("id") ? [root] : root.Children;
        foreach (XmlNode node in nodes)
        {
            if (node.Attribute("id") is not { Length: > 0 } id)
            {
                _problems.Add(new SchemaProblem(ProblemSeverity.Error, node.Name, path, node.Line, $"a definition (<{node.Name}>) needs an id."));
                continue;
            }

            Apply(node, id, folder.Package, path);
        }
    }

    private void Apply(XmlNode node, string id, string package, string path)
    {
        (string, string) key = (node.Name, id);
        string label = $"{node.Name} '{id}'";
        _byKey.TryGetValue(key, out Def? existing);
        if (node.Attribute("remove") == "true")
        {
            if (existing is null)
            {
                Warn(node, path, $"removes {label}, which does not exist.");
                return;
            }

            _defs.Remove(existing);
            _byKey.Remove(key);
            return;
        }

        if (node.Attribute("patch") == "true")
        {
            if (existing is null)
            {
                Warn(node, path, $"patches {label}, which does not exist.");
                return;
            }

            existing.Node = Merge(existing.Node, node);
            existing.Sources.Add(new DefSource(package, path, node.Line, DefRole.Patch));
            return;
        }

        var def = new Def(node.Name, id, node, new DefSource(package, path, node.Line, existing is null ? DefRole.Definition : DefRole.Replacement));
        if (existing is not null)
        {
            DefSource before = existing.Defined;
            Warn(node, path, $"replaces {label} defined in {before.Path} (package '{before.Package}').");
            _defs[_defs.IndexOf(existing)] = def;
        }
        else
        {
            _defs.Add(def);
        }

        _byKey[key] = def;
    }

    private void Warn(XmlNode node, string path, string message) => _problems.Add(new SchemaProblem(ProblemSeverity.Warning, node.Name, path, node.Line, message));

    private void Decode(SchemaSet? schemas)
    {
        var undescribed = new HashSet<string>(StringComparer.Ordinal);
        foreach (Def def in _defs)
        {
            if (schemas is null)
            {
                def.Value = Generic(def.Node);
                continue;
            }

            DecodeResult result = schemas.Decode(def.Node, def.Defined.Path);
            def.Value = result.Value;
            def.Declared = result.Declared;
            _problems.AddRange(result.Problems.Select(p => p with { Path = $"{p.Path} ({def.Id})" }));
            if (!result.Declared && undescribed.Add(def.Type))
            {
                _problems.Add(new SchemaProblem(ProblemSeverity.Warning, def.Type, def.Defined.Path, def.Defined.Line, $"no schema describes <{def.Type}>; its definitions are read as plain XML."));
            }
        }
    }

    private static DefValue Generic(XmlNode node) => new SchemaSet().Decode(node).Value;

    // ---- Patching -------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="patch"/> merged into <paramref name="original"/> (neither changes): an attribute of the patch replaces the original's (and adds it when it has none), its text
    /// replaces the text, and its children are matched with the original's children by name and <c>id</c> or, without an <c>id</c>, by name and place (the second <c>&lt;Unit/&gt;</c>
    /// of a patch is the second of the original). A matched child is merged the same way, one with <c>remove="true"</c> removes its match, and one that matches nothing is added at the end.
    /// A child with <c>append="true"</c> is never matched: it is always added at the end (how to add a third <c>&lt;Tag&gt;</c> to a definition that has two). The <c>patch</c>, <c>remove</c> and
    /// <c>append</c> attributes are not kept.
    /// </summary>
    public static XmlNode Merge(XmlNode original, XmlNode patch)
    {
        XmlNode result = original.Clone();
        foreach (XmlProperty attribute in patch.Attributes)
        {
            if (attribute.Name is not ("patch" or "remove" or "append"))
            {
                result.SetAttribute(attribute.Name, attribute.Value);
            }
        }

        if (patch.Text.Length > 0)
        {
            result.Text = patch.Text;
        }

        List<XmlNode> slots = [.. result.Children];
        var removed = new HashSet<int>();
        var added = new List<XmlNode>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (XmlNode child in patch.Children)
        {
            int place = seen.GetValueOrDefault(child.Name);
            seen[child.Name] = place + 1;

            bool append = child.Attribute("append") == "true";
            int match = append ? -1 : FindMatch(slots, removed, child, place);
            bool remove = child.Attribute("remove") == "true";
            if (match < 0)
            {
                if (!remove)
                {
                    added.Add(Merge(new XmlNode(child.Name, child.Line) { Origin = child.Origin }, child));
                }
            }
            else if (remove)
            {
                removed.Add(match);
            }
            else
            {
                slots[match] = Merge(slots[match], child);
            }
        }

        result.Children.Clear();
        result.Children.AddRange(slots.Where((_, i) => !removed.Contains(i)));
        result.Children.AddRange(added);
        return result;
    }

    /// <summary>
    /// The index of the child of <paramref name="slots"/> that <paramref name="child"/> patches: the one with its name and id, or the <paramref name="place"/>th of its name (children
    /// removed earlier in this patch keep their places, so that the numbering of a patch means what its author counted); -1 when there is none, or it was removed.
    /// </summary>
    private static int FindMatch(List<XmlNode> slots, HashSet<int> removed, XmlNode child, int place)
    {
        string? id = child.Attribute("id");
        int index = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Name != child.Name)
            {
                continue;
            }

            if (id is not null ? slots[i].Attribute("id") == id : index == place)
            {
                return removed.Contains(i) ? -1 : i;
            }

            index++;
        }

        return -1;
    }
}
