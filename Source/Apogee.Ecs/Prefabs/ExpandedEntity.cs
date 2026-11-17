using System;
using System.Collections.Generic;
using Apogee.Content.Xml;

namespace Apogee.Ecs;

/// <summary>
/// One entity of what an instance of a prefab expands to, before any entity exists in a world: its address, where its id comes from, its name and its components as XML (every field written, in the scope
/// of the document the expansion is for), and the entities below it. The expander makes the tree; a loader creates entities from it. The <see cref="XmlNode"/>s in <see cref="Components"/> may be the
/// ones of the prefab's cached document, so they are never changed.
/// </summary>
internal sealed class ExpandedEntity
{
    /// <summary>
    /// The entity's address (<see cref="EntityAddress"/>), the one every Entity-typed field in the tree uses. Empty for an entity nothing can refer to: one with no usable id, or one whose address another
    /// entity of the expansion already has (said in the log).
    /// </summary>
    public string Address { get; init; } = "";

    /// <summary>
    /// The entity's id in the document that holds its element: a prefab file, for what a prefab's file gave it (<see cref="MemberPrefab"/> is that file), or null for an entity of a scene (the document
    /// the expansion was asked for is not a prefab file). A nested instance's root has the id its element has in the outer file.
    /// </summary>
    public int? MemberId { get; init; }

    /// <summary>The key (<see cref="ResolvedPrefab.Key"/>) of the prefab file <see cref="MemberId"/> belongs to; null with no id.</summary>
    public string? MemberPrefab { get; init; }

    /// <summary>The key of the prefab this entity is the root of; null for an entity that is not an instance root. A missing instance has the key the reference gave.</summary>
    public string? InstancePrefab { get; set; }

    /// <summary>The id of the root entity in <see cref="InstancePrefab"/>'s own file (0 for a missing instance): <c>address/id</c> is accepted as another name for the root.</summary>
    public int InstanceRootId { get; set; }

    /// <summary>The name: the file's, an override's, made unique among its siblings as the scene loader does. Null: unnamed.</summary>
    public string? Name { get; set; }

    /// <summary>The components, in order, one per name.</summary>
    public List<XmlNode> Components { get; } = [];

    /// <summary>The entities the prefab file put below this one (for an instance root: its prefab's root children, which are their own prefab's members), in file order.</summary>
    public List<ExpandedEntity> Children { get; } = [];

    /// <summary>The entities an instance element or one of its overrides added below this one, in the order they were written; after <see cref="Children"/>.</summary>
    public List<ExpandedEntity> Added { get; } = [];

    /// <summary>
    /// For an instance root: the elements of its instance element that could not be applied (an override of an entity the prefab no longer has, a field patch of a component it no longer has, a bad
    /// address), as they were written, to be saved again as they are. Empty for any other entity.
    /// </summary>
    public List<XmlNode> Unapplied { get; } = [];

    /// <summary>True for an instance whose prefab could not be used (it is missing, unreadable, contains itself or nests too deep): the root alone, with everything its element held in <see cref="Unapplied"/>.</summary>
    public bool IsMissing { get; init; }

    /// <summary>Why <see cref="IsMissing"/>, for a message.</summary>
    public string? MissingReason { get; init; }

    /// <summary>How many entities are above this one in the expansion (0 for the root).</summary>
    public int Depth { get; init; }

    /// <summary>The entity this one is below, null for the root of the expansion and for one that was removed.</summary>
    public ExpandedEntity? Parent { get; set; }

    /// <summary>Set on an entity an override removed, and on everything below it: it is no longer in the tree and an address that reaches it finds nothing.</summary>
    public bool IsRemoved { get; set; }

    /// <summary>Whether the entity is the root of an instance (<see cref="InstancePrefab"/> is set).</summary>
    public bool IsInstanceRoot => InstancePrefab is not null;

    /// <summary>The entity and everything below it, parents first, <see cref="Children"/> before <see cref="Added"/>: the order a loader creates them in.</summary>
    public IEnumerable<ExpandedEntity> Subtree()
    {
        var pending = new Stack<ExpandedEntity>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            ExpandedEntity entity = pending.Pop();
            yield return entity;
            for (int i = entity.Added.Count - 1; i >= 0; i--)
            {
                pending.Push(entity.Added[i]);
            }

            for (int i = entity.Children.Count - 1; i >= 0; i--)
            {
                pending.Push(entity.Children[i]);
            }
        }
    }

    /// <summary>The component with this element name, or null.</summary>
    public XmlNode? Component(string name) => Components.Find(c => c.Name == name);

    /// <summary>Puts the component in: it replaces the one with the same name (keeping its place), else goes last.</summary>
    public void SetComponent(XmlNode component)
    {
        int at = Components.FindIndex(c => c.Name == component.Name);
        if (at < 0)
        {
            Components.Add(component);
        }
        else
        {
            Components[at] = component;
        }
    }

    /// <summary>Takes the component away; false when there is none with this name.</summary>
    public bool RemoveComponent(string name) => Components.RemoveAll(c => c.Name == name) > 0;

    /// <summary>Takes the entity out of its parent and marks it, and everything below it, removed. False for the root, which has no parent.</summary>
    public bool Remove()
    {
        if (Parent is not { } parent)
        {
            return false;
        }

        if (!parent.Children.Remove(this))
        {
            parent.Added.Remove(this);
        }

        Parent = null;
        foreach (ExpandedEntity entity in Subtree())
        {
            entity.IsRemoved = true;
        }

        return true;
    }
}

/// <summary>What an expansion made: the tree, the component schemas it needs, and every address in it.</summary>
internal sealed class Expansion
{
    public Expansion(ExpandedEntity root, List<XmlNode> schemas, Dictionary<string, ExpandedEntity> index)
    {
        Root = root;
        Schemas = schemas;
        Index = index;
    }

    /// <summary>The root of the instance that was asked for.</summary>
    public ExpandedEntity Root { get; }

    /// <summary>
    /// The <c>&lt;Components&gt;</c> elements of every document the expansion used (the enclosing document's first, then each prefab file's, once), so a loader can define the components scripts made
    /// before it reads any field.
    /// </summary>
    public List<XmlNode> Schemas { get; }

    /// <summary>
    /// Every entity of the tree by address, including the other names of an instance root (<c>address/rootId</c>), and including entities an override removed (they are <see cref="ExpandedEntity.IsRemoved"/>).
    /// What an Entity-typed field in the tree resolves against.
    /// </summary>
    public IReadOnlyDictionary<string, ExpandedEntity> Index { get; }
}

/// <summary>
/// How many entities expansions may make, shared by the expansions of one load (a scene with a thousand instances spends one budget). Counting happens as each entity is about to be made, so an expansion
/// that is too big stops with at most <see cref="Limit"/> entities of tree built, whatever its documents say.
/// </summary>
internal sealed class ExpansionBudget
{
    /// <summary>What a load may make unless told otherwise.</summary>
    public const int DefaultLimit = 262_144;

    public ExpansionBudget(int limit = DefaultLimit, int used = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(used);
        Limit = limit;
        Used = used;
    }

    /// <summary>The most entities.</summary>
    public int Limit { get; }

    /// <summary>The entities counted so far (never above <see cref="Limit"/>).</summary>
    public int Used { get; private set; }

    /// <summary>Counts one entity about to be made.</summary>
    /// <exception cref="FormatException">There would be more than <see cref="Limit"/>.</exception>
    public void Take()
    {
        if (Used >= Limit)
        {
            throw new FormatException($"the prefabs expand to more than {Limit} entities: refused before any entity is made");
        }

        Used++;
    }
}

/// <summary>What the expander is told about a prefab reference.</summary>
/// <param name="Key">
/// The name of the file however it was written (<c>path</c>, <c>uid://…</c>, both): equal for the same file, so that a prefab that contains itself is found. A <c>Prefabs.Load</c> accepts it. For a reference
/// that names no file, the reference itself.
/// </param>
/// <param name="Document">The <c>Prefab</c> element, or null when there is none.</param>
/// <param name="Problem">Why there is no document, for a message.</param>
internal sealed record ResolvedPrefab(string Key, XmlNode? Document, string? Problem = null);

/// <summary>Finds the prefab a <c>prefab="…"</c> attribute names. May throw for a file that cannot be read (the instance is then missing, with the exception's message).</summary>
internal delegate ResolvedPrefab PrefabResolver(string reference);
