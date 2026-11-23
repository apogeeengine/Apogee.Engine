using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Apogee.Content.Xml;

namespace Apogee.Ecs;

/// <summary>
/// A prefab file being edited by <see cref="World.ApplyOverrides"/>: the document as the disk has it right now (an editable copy), the exact bytes it was read as (to put back), and the ids it hands out.
/// Nothing is written until every edit of every file of one apply has worked.
/// </summary>
internal sealed class PrefabEditFile
{
    private long _next;

    public PrefabEditFile(uint handle, string key, string path, XmlNode document, byte[] original, bool reformats)
    {
        Handle = handle;
        Key = key;
        Path = path;
        Document = document;
        Original = original;
        Reformats = reformats;
        RootId = int.Parse(PrefabDocument.RootEntity(document).Attribute("id")!, CultureInfo.InvariantCulture);
        _next = PrefabDocument.NextIdOf(document);
    }

    /// <summary>The library's handle of the file.</summary>
    public uint Handle { get; }

    /// <summary>The key (path) the file is known by.</summary>
    public string Key { get; }

    /// <summary>The file's path.</summary>
    public string Path { get; }

    /// <summary>The document to edit: a copy read from the disk.</summary>
    public XmlNode Document { get; }

    /// <summary>Whether the file is not laid out the way the engine writes a prefab (comments, indentation, attribute order…): writing it changes more than what was applied.</summary>
    public bool Reformats { get; }

    /// <summary>The bytes the file had when it was read.</summary>
    public byte[] Original { get; }

    /// <summary>The id of the file's root entity.</summary>
    public int RootId { get; }

    /// <summary>Whether an edit changed the document.</summary>
    public bool Changed { get; set; }

    /// <summary>The next id of the file's numbering, taken (it is never given again).</summary>
    public int NewId()
    {
        if (_next > int.MaxValue)
        {
            throw new InvalidOperationException($"{Path} has used every entity id it can");
        }

        Changed = true;
        return (int)_next++;
    }

    /// <summary>Writes <c>nextId</c> into the document once the edits are done.</summary>
    public void Finish()
    {
        if (Changed)
        {
            Document.SetAttribute("nextId", Math.Max(_next, PrefabDocument.NextIdOf(Document)).ToString(CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>Where in a prefab file an override goes: the entity's element, and the element that holds the change.</summary>
internal sealed class EditSite
{
    public EditSite(PrefabEditFile file, string fileInstance, XmlNode element, XmlNode? parent, string? overrideAddress)
    {
        File = file;
        FileInstance = fileInstance;
        Element = element;
        Parent = parent;
        OverrideAddress = overrideAddress;
    }

    /// <summary>The file that is written.</summary>
    public PrefabEditFile File { get; }

    /// <summary>The address (in the document the overrides were read from) of the instance of <see cref="File"/>: where its ids are placed.</summary>
    public string FileInstance { get; }

    /// <summary>The entity's element in the file: an entity, or an instance of another prefab.</summary>
    public XmlNode Element { get; }

    /// <summary>The element that holds <see cref="Element"/> in the file; null for the root.</summary>
    public XmlNode? Parent { get; }

    /// <summary>
    /// When set, <see cref="Element"/> is an instance of another prefab and the entity is inside it, at this address: changes are written in an <c>&lt;Override&gt;</c> of the element. Null: the
    /// element is the entity (a plain entity, or the root of the instance the element is).
    /// </summary>
    public string? OverrideAddress { get; }

    /// <summary>Whether the element is an instance of another prefab.</summary>
    public bool IsInstance => Element.HasAttribute("prefab");
}

// The edits that apply an override to a prefab file, as XML, with the file's ids and the layout the world writes (docs/manual/content/prefabs.md, "Applying an override"). Nothing here knows
// of a world or a disk: every refusal is an InvalidOperationException before anything is written.
internal sealed class PrefabFileEdit(Func<string, PrefabEditFile> open)
{
    private const string EntityElement = "Entity";
    private const string OverrideElement = "Override";

    /// <summary>
    /// Finds where the entity at <paramref name="segments"/> (an address in the instance of <paramref name="file"/>, which is at <paramref name="fileInstance"/> in the document the overrides come from) is
    /// written. With <see cref="PrefabApplyTarget.SourcePrefab"/> the search goes into the nested prefabs on the way, to the file that defines the entity; with
    /// <see cref="PrefabApplyTarget.InstancePrefab"/> it stops at <paramref name="file"/>. <paramref name="intoRoot"/>: an entity that is the root of a nested instance is the root of that prefab
    /// (for what lives in the prefab: components, children) rather than the instance's element (for what the element names: the entity itself).
    /// </summary>
    public EditSite Locate(PrefabEditFile file, string fileInstance, int[] segments, PrefabApplyTarget target, bool intoRoot)
    {
        if (segments.Length == 0)
        {
            return new EditSite(file, fileInstance, PrefabDocument.RootEntity(file.Document), null, null);
        }

        (XmlNode? element, XmlNode? parent) = Find(file.Document, segments[0]);
        if (element is null)
        {
            throw new InvalidOperationException($"{file.Path} has no entity {segments[0]}: the instance holds a change to something the prefab file does not say it has");
        }

        bool isInstance = element.HasAttribute("prefab");
        string here = fileInstance + "/" + segments[0].ToString(CultureInfo.InvariantCulture);
        if (segments.Length == 1)
        {
            if (!isInstance || target == PrefabApplyTarget.InstancePrefab || !intoRoot)
            {
                return new EditSite(file, fileInstance, element, parent, null);
            }

            return Locate(Nested(element), here, [], target, intoRoot);
        }

        if (!isInstance)
        {
            throw new InvalidOperationException($"entity {segments[0]} of {file.Path} is not an instance of a prefab, so it has nothing at {string.Join('/', segments)}");
        }

        if (target == PrefabApplyTarget.InstancePrefab)
        {
            return new EditSite(file, fileInstance, element, parent, EntityAddress.Format(segments.AsSpan(1)));
        }

        return Locate(Nested(element), here, segments[1..], target, intoRoot);
    }

    private PrefabEditFile Nested(XmlNode instance) => open(instance.Attribute("prefab")!);

    // The element of the entity with an id anywhere in a prefab document (an entity added by an override of an instance included), and the element that holds it.
    private static (XmlNode? Element, XmlNode? Parent) Find(XmlNode container, int id)
    {
        foreach (XmlNode child in container.Children)
        {
            if (child.Name == EntityElement)
            {
                if (PrefabDocument.TryParseId(child.Attribute("id"), out int found) && found == id)
                {
                    return (child, container);
                }

                if (Find(child, id) is { Element: not null } below)
                {
                    return below;
                }
            }
            else if (child.Name == OverrideElement && Find(child, id) is { Element: not null } inside)
            {
                return inside;
            }
        }

        return (null, null);
    }

    // ---- The changes

    /// <summary>Sets one field of a component. <paramref name="text"/> is in the scope of the file.</summary>
    public static void SetField(EditSite site, string component, string field, string text)
    {
        XmlNode holder = Holder(site);
        XmlNode? patch = ComponentOf(holder, component);
        if (patch is null)
        {
            if (!IsPatchHolder(site))
            {
                throw new InvalidOperationException($"{site.File.Path} has no {component} on that entity: it is given by another file between the instance and this prefab, which has to be changed instead");
            }

            patch = new XmlNode(component);
            holder.Children.Insert(ComponentPlace(holder), patch);
        }

        if (patch.Attribute("remove") == "true")
        {
            throw new InvalidOperationException($"{site.File.Path} removes {component} from that entity");
        }

        // What the file says already is not written again: an apply of it changes nothing, not even the layout.
        if (patch.Attribute(field) != text)
        {
            patch.SetAttribute(field, text);
            site.File.Changed = true;
        }
    }

    /// <summary>Gives the entity a component (written whole, in the scope of the file). Script-defined components bring their schema.</summary>
    public static void AddComponent(EditSite site, XmlNode component, XmlNode? schema)
    {
        XmlNode holder = Holder(site);
        XmlNode? existing = ComponentOf(holder, component.Name);
        if (IsPatchHolder(site))
        {
            if (existing is not null)
            {
                holder.Children.Remove(existing);
            }

            component.SetAttribute("append", "true");
        }
        else if (existing is not null)
        {
            throw new InvalidOperationException($"{site.File.Path} already has a {component.Name} on that entity");
        }

        holder.Children.Insert(ComponentPlace(holder), component);
        AddSchema(site.File, schema);
        site.File.Changed = true;
    }

    /// <summary>Takes a component off the entity.</summary>
    public static void RemoveComponent(EditSite site, string component)
    {
        XmlNode holder = Holder(site);
        XmlNode? existing = ComponentOf(holder, component);
        if (IsPatchHolder(site))
        {
            if (existing is not null)
            {
                holder.Children.Remove(existing);
            }

            var removal = new XmlNode(component);
            removal.SetAttribute("remove", "true");
            holder.Children.Insert(ComponentPlace(holder), removal);
        }
        else if (existing is null || !holder.Children.Remove(existing))
        {
            throw new InvalidOperationException($"{site.File.Path} has no {component} on that entity: it is given by another file between the instance and this prefab, which has to be changed instead");
        }

        site.File.Changed = true;
    }

    /// <summary>Renames the entity.</summary>
    public static void SetName(EditSite site, string name)
    {
        XmlNode holder = Holder(site);
        if (holder.Attribute("name") != name)
        {
            holder.SetAttribute("name", name);
            site.File.Changed = true;
        }
    }

    /// <summary>Takes the entity (with everything below it) out of the prefab: its element goes, or an override that removes it is written.</summary>
    public static void RemoveEntity(EditSite site)
    {
        if (site.OverrideAddress is not null)
        {
            XmlNode change = EnsureOverride(site.Element, site.OverrideAddress);
            change.Children.Clear();
            change.Attributes.Clear();
            change.SetAttribute("entity", site.OverrideAddress);
            change.SetAttribute("remove", "true");
        }
        else if (site.Parent is null || !site.Parent.Children.Remove(site.Element))
        {
            throw new InvalidOperationException($"{site.File.Path}: the root of a prefab cannot be removed from it");
        }

        site.File.Changed = true;
    }

    /// <summary>Adds an entity element (its ids already the file's) below the entity.</summary>
    public static void AddEntity(EditSite site, XmlNode entity)
    {
        Holder(site).Children.Add(entity);
        site.File.Changed = true;
    }

    // ---- Layout

    // Whether changes are written as patches (an instance's element or one of its overrides) rather than in the entity's own components.
    private static bool IsPatchHolder(EditSite site) => site.IsInstance;

    // The element that holds the changes: the entity's own, or the override of an instance's element that is about it.
    private static XmlNode Holder(EditSite site) => site.OverrideAddress is not null ? EnsureOverride(site.Element, site.OverrideAddress) : site.Element;

    private static XmlNode? ComponentOf(XmlNode holder, string component) =>
        holder.Children.Find(child => child.Name == component && child.Name is not (EntityElement or OverrideElement));

    // Components come before overrides and entities.
    private static int ComponentPlace(XmlNode holder)
    {
        int place = holder.Children.FindIndex(static child => child.Name is EntityElement or OverrideElement);
        return place < 0 ? holder.Children.Count : place;
    }

    private static XmlNode EnsureOverride(XmlNode instance, string address)
    {
        XmlNode? found = instance.Children.Find(child => child.Name == OverrideElement && child.Attribute("entity") == address);
        if (found is not null)
        {
            return found;
        }

        var change = new XmlNode(OverrideElement);
        change.SetAttribute("entity", address);

        // Overrides after the components and before the entities added below the root.
        int place = instance.Children.FindIndex(static child => child.Name == EntityElement);
        instance.Children.Insert(place < 0 ? instance.Children.Count : place, change);
        return change;
    }

    /// <summary>Defines every component of a <c>Components</c> element in the file (the ones a script defined that the entities written into it use).</summary>
    public static void AddSchemas(PrefabEditFile file, XmlNode? schemas)
    {
        if (schemas is null)
        {
            return;
        }

        foreach (XmlNode schema in schemas.Children)
        {
            AddSchema(file, schema);
        }
    }

    // What a component's definition says, whatever the order of its fields: the world keeps them in an order of its own.
    private static string Signature(XmlNode schema) =>
        string.Join(';', schema.Children.Where(static field => field.Name == "Field").Select(static field => $"{field.Attribute("name")}:{field.Attribute("kind")}:{field.Attribute("assetType")}").Order(StringComparer.Ordinal));

    // Defines a script's component in the file: the file holds what its entities use.
    private static void AddSchema(PrefabEditFile file, XmlNode? schema)
    {
        if (schema is null)
        {
            return;
        }

        string name = schema.Attribute("name") ?? "";
        XmlNode? components = file.Document.Child("Components");
        if (components is null)
        {
            components = new XmlNode("Components");
            file.Document.Children.Insert(0, components);
        }

        XmlNode? existing = components.Children.Find(child => child.Name == "Component" && child.Attribute("name") == name);
        if (existing is null)
        {
            components.Children.Add(schema.Clone());
        }
        else if (Signature(existing) != Signature(schema))
        {
            throw new InvalidOperationException($"{file.Path} defines the component {name} differently from this world: it would not be read as the component the entities have");
        }
    }
}

// Entity-field text between two scopes: the document the overrides were read from (the instance at one address, its entities below) and a prefab file (its ids).
internal sealed class FileScope(string instance, int rootId)
{
    /// <summary>
    /// The text of an Entity field in the scope of the file, or null when it names something that is not in the file (an entity outside the instance, or one the instance added): a prefab cannot
    /// name what it does not hold. The empty text (no entity) stays.
    /// </summary>
    public string? ToFile(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        if (text == instance)
        {
            return rootId.ToString(CultureInfo.InvariantCulture);
        }

        string prefix = instance + "/";
        return text.StartsWith(prefix, StringComparison.Ordinal) && EntityAddress.IsValid(text[prefix.Length..]) ? text[prefix.Length..] : null;
    }
}
