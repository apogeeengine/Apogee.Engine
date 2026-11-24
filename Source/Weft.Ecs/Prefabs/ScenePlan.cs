using System;
using System.Collections.Generic;
using System.Globalization;
using Weft.Content.Xml;
using Weft.Core.Assets;
using Weft.Core.Logging;

namespace Weft.Ecs;

/// <summary>One entity a load is going to make: everything it needs, worked out from the document before any entity exists.</summary>
internal sealed class PlannedEntity
{
    /// <summary>The address entity fields of the document use for it (<see cref="EntityAddress"/>); empty when nothing can refer to it.</summary>
    public string Address { get; init; } = "";

    /// <summary>The name its element gives it, before it is made unique among its siblings.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// The component elements, in order. They are the document's own nodes (or the prefab's, shared by every instance of it): read, never changed. Entity fields in them are addresses of the
    /// document being loaded.
    /// </summary>
    public IReadOnlyList<XmlNode> Components { get; init; } = [];

    /// <summary>The index in <see cref="ScenePlan.Items"/> of the entity it goes under; -1 for the caller's parent.</summary>
    public int Parent { get; init; } = -1;

    /// <summary>The entity's id in the prefab file that holds it (<see cref="PrefabMember"/>), or null when it is not from a prefab file.</summary>
    public int? MemberId { get; init; }

    /// <summary>The key of the prefab file <see cref="MemberId"/> belongs to.</summary>
    public string? MemberPrefab { get; init; }

    /// <summary>The key of the prefab this entity is the root of (<see cref="PrefabInstance"/>); null for any other entity.</summary>
    public string? InstancePrefab { get; init; }

    /// <summary>The id of the root entity in <see cref="InstancePrefab"/>'s own file (0 when it could not be used).</summary>
    public int InstanceRootId { get; init; }

    /// <summary>For an instance whose <c>prefab</c> attribute is not an asset reference: the text as it was written, which is written back as it was.</summary>
    public string? RawPrefab { get; init; }

    /// <summary>Why the instance could not be made from its prefab (it is the root alone), or null.</summary>
    public string? MissingReason { get; init; }

    /// <summary>Whether the instance is missing because its prefab contains itself or is nested too deep (<see cref="ExpandedEntity.IsCut"/>), not because it is absent.</summary>
    public bool IsCut { get; init; }

    /// <summary>What the instance element holds that could not be applied, to keep as it was written (see <see cref="ExpandedEntity.Unapplied"/>); the nodes are shared and must be cloned before they are stored.</summary>
    public IReadOnlyList<XmlNode>? Unapplied { get; init; }
}

/// <summary>
/// What loading a scene (or instantiating a prefab) will make, in the order the entities are created: the plain entity elements of the document and the instances, expanded (see
/// <see cref="PrefabExpander"/>) into the entities their prefabs, overrides and added entities make. It is pure XML work, and the expander's <see cref="ExpansionBudget"/> bounds what the instances make
/// (the document's own entities are the size of the document and are not counted: they cannot multiply), so a document that is too big
/// fails here and leaves the world untouched.
/// </summary>
/// <remarks>
/// Every entity has an address in the document (<see cref="PlannedEntity.Address"/>): a scene's own entities by their <c>id</c>, the entities of an instance below the address of the instance. An
/// entity field is the address of what it names. When two entities of the document claim one address the first keeps it (and the other cannot be referred to), and an instance element whose id
/// is missing, bad or taken gets an address nothing else uses, so that what is in it never meets another entity's.
/// </remarks>
internal sealed class ScenePlan
{
    private const string Category = "Scene";
    private const int MaxClaimWarnings = 50;   // a hostile document could repeat one address for every entity

    private readonly PrefabExpander _expander;
    private readonly XmlNode? _schemas;
    private readonly HashSet<XmlNode> _schemasSeen = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, XmlNode> _reserved = new(StringComparer.Ordinal);   // the plain entity element that owns each of the document's own ids
    private int _fresh = int.MaxValue;
    private int _claimWarnings;

    private ScenePlan(PrefabExpander expander, XmlNode? schemas)
    {
        _expander = expander;
        _schemas = schemas;
    }

    /// <summary>The entities to make, parents before children, in the order of the document.</summary>
    public List<PlannedEntity> Items { get; } = [];

    /// <summary>The entity (an index of <see cref="Items"/>) at each address that can be referred to.</summary>
    public Dictionary<string, int> Addresses { get; } = new(StringComparer.Ordinal);

    /// <summary>The <c>Components</c> elements to define components from, the document's first and then each prefab's, each once.</summary>
    public List<XmlNode> Schemas { get; } = [];

    /// <summary>Plans the loading of the <c>Entity</c> elements directly under <paramref name="document"/>.</summary>
    /// <exception cref="FormatException">The document would make more entities than the budget allows, or ask for more work, or nest entities too deep.</exception>
    /// <param name="startDepth">How many entities are above the document's top ones (an update of an instance deep in a scene plans it at its own depth, as a load of the whole scene would).</param>
    public static ScenePlan Build(XmlNode document, PrefabExpander expander, int startDepth = 0)
    {
        var plan = new ScenePlan(expander, document.Child("Components"));
        if (plan._schemas is not null)
        {
            plan.UseSchemas(plan._schemas);
        }

        plan.Reserve(document);
        plan.Walk(document, -1, startDepth);
        return plan;
    }

    private void UseSchemas(XmlNode schemas)
    {
        if (_schemasSeen.Add(schemas))
        {
            Schemas.Add(schemas);
        }
    }

    // The ids of the plain entity elements of the document (not what is inside an instance element) come first: they are the numbers the document was last written with, so they win over an id an
    // instance's element holds that is older (an added entity kept aside for a prefab that was missing keeps the number it had then, which the scene may have given to another entity since).
    private void Reserve(XmlNode container)
    {
        foreach (XmlNode element in container.Children)
        {
            if (element.Name != "Entity" || element.HasAttribute("prefab"))
            {
                continue;
            }

            if (PrefabDocument.TryParseId(element.Attribute("id"), out int id))
            {
                _reserved.TryAdd(id.ToString(CultureInfo.InvariantCulture), element);
            }

            Reserve(element);
        }
    }

    private static void Warn(XmlNode node, string message) => Log.Warn(Category, $"{node.Origin}:{node.Line}: {message}");

    // The <Entity> children of a node, in order, each with what is below it. `depth` is how many entities of the document are above.
    private void Walk(XmlNode container, int parent, int depth)
    {
        foreach (XmlNode element in container.Children)
        {
            if (element.Name != "Entity")
            {
                continue;
            }

            if (element.HasAttribute("prefab"))
            {
                AddInstance(element, parent, depth);
            }
            else
            {
                AddPlain(element, parent, depth);
            }
        }
    }

    private void AddPlain(XmlNode element, int parent, int depth)
    {
        var components = new List<XmlNode>();
        foreach (XmlNode child in element.Children)
        {
            if (child.Name == "Override")
            {
                Warn(child, "an <Override> belongs inside an instance of a prefab (an <Entity prefab=\"…\">): skipped");
            }
            else if (child.Name != "Entity")
            {
                components.Add(child);
            }
        }

        string address = "";
        if (element.Attribute("id") is { } idText)
        {
            if (PrefabDocument.TryParseId(idText, out int id))
            {
                address = id.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                Warn(element, $"the entity id '{idText}' is not a number from 1 up: nothing can refer to it");
            }
        }

        int at = Items.Count;
        Items.Add(new PlannedEntity { Address = address, Name = element.Attribute("name"), Components = components, Parent = parent });
        if (address.Length > 0)
        {
            // The first plain element with an id has it (reserved); a later one with the same id cannot be referred to.
            if (ReferenceEquals(_reserved[address], element))
            {
                Addresses[address] = at;
            }
            else if (_claimWarnings++ < MaxClaimWarnings)
            {
                Warn(element, $"the entity id {address} is used twice: references to it go to the first, and this one cannot be referred to");
            }
        }

        Walk(element, at, depth + 1);
    }

    private void AddInstance(XmlNode element, int parent, int depth)
    {
        string reference = element.Attribute("prefab")!;
        if (!AssetRef.TryParse(reference, out AssetRef parsed) || parsed.IsEmpty)
        {
            // A prefab that cannot even be named (a damaged UID, text that is no reference) is a prefab that is missing: the instance is its root alone, the reference as it was written is kept with it
            // and what the element holds is kept aside, so that fixing the reference (or the file) brings everything back, and saving in between costs nothing.
            Warn(element, $"<Entity '{element.Attribute("name")}'> names the prefab '{reference}', which is not an asset reference (a path, uid://… or both): it is left empty, and what its element holds is kept");
            string unnamed = AddressOfInstance(element);
            int root = Items.Count;
            Items.Add(new PlannedEntity
            {
                Address = unnamed,
                Name = element.Attribute("name"),
                Parent = parent,
                InstancePrefab = reference,
                RawPrefab = reference,
                MissingReason = "its reference is not an asset reference",
                Unapplied = element.Children.Count > 0 ? [.. element.Children] : null,
            });
            Claim(element, unnamed, root);
            return;
        }

        string address = AddressOfInstance(element);
        Expansion expansion = _expander.Expand(element, address, _schemas, new ExpandOptions { StartDepth = depth });
        foreach (XmlNode schemas in expansion.Schemas)
        {
            UseSchemas(schemas);
        }

        var indexOf = new Dictionary<ExpandedEntity, int>(ReferenceEqualityComparer.Instance);
        foreach (ExpandedEntity entity in expansion.Root.Subtree())
        {
            int at = Items.Count;
            indexOf[entity] = at;
            Items.Add(new PlannedEntity
            {
                Address = entity.Address,
                Name = entity.Name,
                Components = [.. entity.Components],
                Parent = entity.Parent is { } above && indexOf.TryGetValue(above, out int aboveAt) ? aboveAt : parent,
                MemberId = entity.MemberId,
                MemberPrefab = entity.MemberPrefab,
                InstancePrefab = entity.InstancePrefab,
                InstanceRootId = entity.InstanceRootId,
                MissingReason = entity.IsMissing ? entity.MissingReason : null,
                IsCut = entity.IsCut,
                Unapplied = entity.Unapplied.Count > 0 ? entity.Unapplied : null,
            });
        }

        // The entities are known by their addresses; an alias of an instance root (address/rootId) names the root.
        foreach (ExpandedEntity entity in expansion.Root.Subtree())
        {
            Claim(element, entity.Address, indexOf[entity]);
        }

        foreach ((string key, ExpandedEntity entity) in expansion.Index)
        {
            if (!entity.IsRemoved && key != entity.Address && indexOf.TryGetValue(entity, out int at))
            {
                Claim(element, key, at);
            }
        }
    }

    // The address of an instance element: its id when that is usable and free, else one nothing else has.
    private string AddressOfInstance(XmlNode element)
    {
        if (element.Attribute("id") is { } idText)
        {
            if (!PrefabDocument.TryParseId(idText, out int id))
            {
                Warn(element, $"the entity id '{idText}' is not a number from 1 up: nothing can refer to it");
            }
            else
            {
                string address = id.ToString(CultureInfo.InvariantCulture);
                if (!Addresses.ContainsKey(address) && !_reserved.ContainsKey(address))
                {
                    return address;
                }

                Warn(element, $"the entity id {id} is used twice: references to it go to the first, and this instance cannot be referred to");
            }
        }

        // Counting down from the largest id: no document numbers that far, and a made-up address is one nobody wrote.
        string fresh;
        do
        {
            fresh = (_fresh--).ToString(CultureInfo.InvariantCulture);
        }
        while (Addresses.ContainsKey(fresh) || _reserved.ContainsKey(fresh));

        return fresh;
    }

    // Records that the entity at `at` is the one an address names. Text that is no address (an entity with no usable id) names nothing; the first to claim one keeps it.
    private void Claim(XmlNode element, string address, int at)
    {
        if (!EntityAddress.IsValid(address))
        {
            return;
        }

        if ((_reserved.ContainsKey(address) || !Addresses.TryAdd(address, at)) && _claimWarnings++ < MaxClaimWarnings)
        {
            Warn(element, $"the entity address {address} is used twice: references to it go to the first, and this one cannot be referred to");
        }
    }
}
