using System;
using System.Collections.Generic;
using System.IO;
using Apogee.Content;
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

    // The components in order, one per name; a removed one leaves a hole until there are more holes than components, so that removing is cheap. Looking one up by name scans while there are few
    // and uses an index after, so an entity with very many components costs no more than linear.
    private const int IndexAbove = 8;
    private readonly List<XmlNode?> _components = [];
    private Dictionary<string, int>? _componentIndex;
    private int _componentCount;

    /// <summary>The components, in order, one per name. The nodes may be shared with a cached document: they are never changed.</summary>
    public IEnumerable<XmlNode> Components
    {
        get
        {
            foreach (XmlNode? component in _components)
            {
                if (component is not null)
                {
                    yield return component;
                }
            }
        }
    }

    /// <summary>How many components the entity has.</summary>
    public int ComponentCount => _componentCount;

    /// <summary>The entities the prefab file put below this one (for an instance root: its prefab's root children, which are their own prefab's members), in file order.</summary>
    public List<ExpandedEntity> Children { get; } = [];

    /// <summary>The entities an instance element or one of its overrides added below this one, in the order they were written; after <see cref="Children"/>.</summary>
    public List<ExpandedEntity> Added { get; } = [];

    /// <summary>
    /// For the root of the instance an expansion was asked for: the elements of its instance element that could not be applied (an override of an entity the prefab no longer has, a field patch of a
    /// component it no longer has, a bad address, an added entity whose parent the same element removed), as they were written, to be saved again as they are. Empty for any other entity, including
    /// the roots of instances nested in prefab files: what a prefab file's own instance elements could not apply belongs to that file (it is logged, and worked out again at every expansion), not to
    /// the scene. The nodes are the document's own, shared and never changed: clone one before storing it anywhere that outlives the document.
    /// </summary>
    public List<XmlNode> Unapplied { get; } = [];

    /// <summary>True for an instance whose prefab could not be used (it is missing, unreadable, contains itself or nests too deep): the root alone, with everything its element held in <see cref="Unapplied"/>.</summary>
    public bool IsMissing { get; init; }

    /// <summary>Why <see cref="IsMissing"/>, for a message.</summary>
    public string? MissingReason { get; init; }

    /// <summary>How many entities are above this one in the expansion (0 for the root).</summary>
    public int Depth { get; init; }

    /// <summary>The element of the expansion's own document (an instance element or one of its overrides) this entity was added from; null for an entity that came from a prefab. What keeps it if its parent is removed.</summary>
    public XmlNode? AddedBy { get; set; }

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
    public XmlNode? Component(string name) => IndexOfComponent(name) is var at and >= 0 ? _components[at] : null;

    /// <summary>Puts the component in: it replaces the one with the same name (keeping its place), else goes last.</summary>
    public void SetComponent(XmlNode component)
    {
        int at = IndexOfComponent(component.Name);
        if (at >= 0)
        {
            _components[at] = component;
            return;
        }

        _components.Add(component);
        _componentCount++;
        if (_componentIndex is not null)
        {
            _componentIndex[component.Name] = _components.Count - 1;
        }
        else if (_components.Count > IndexAbove)
        {
            RebuildComponentIndex();
        }
    }

    /// <summary>Takes the component away; false when there is none with this name.</summary>
    public bool RemoveComponent(string name)
    {
        int at = IndexOfComponent(name);
        if (at < 0)
        {
            return false;
        }

        _components[at] = null;
        _componentCount--;
        _componentIndex?.Remove(name);
        if (_components.Count > 2 * _componentCount + IndexAbove)
        {
            _components.RemoveAll(c => c is null); // more holes than components: close them (once per that many removals, so removing stays cheap)
            RebuildComponentIndex();
        }

        return true;
    }

    private int IndexOfComponent(string name)
    {
        if (_componentIndex is not null)
        {
            return _componentIndex.TryGetValue(name, out int at) ? at : -1;
        }

        for (int i = 0; i < _components.Count; i++)
        {
            if (_components[i]?.Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    private void RebuildComponentIndex()
    {
        if (_components.Count <= IndexAbove)
        {
            _componentIndex = null;
            return;
        }

        _componentIndex = new Dictionary<string, int>(_components.Count, StringComparer.Ordinal);
        for (int i = 0; i < _components.Count; i++)
        {
            if (_components[i] is { } component)
            {
                _componentIndex[component.Name] = i;
            }
        }
    }

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
    public Expansion(ExpandedEntity root, List<XmlNode> schemas, Dictionary<string, ExpandedEntity> index, List<string> prefabs)
    {
        Root = root;
        Schemas = schemas;
        Index = index;
        Prefabs = prefabs;
    }

    /// <summary>
    /// The key (<see cref="ResolvedPrefab.Key"/>) of every prefab an instance in the expansion names, in the order they were first met: the files it depends on, including ones that were missing,
    /// cut off by a cycle or over the depth limit (the file may appear later). What a refresh needs to know which instances a changed prefab touches.
    /// </summary>
    public IReadOnlyList<string> Prefabs { get; }

    /// <summary>The root of the instance that was asked for.</summary>
    public ExpandedEntity Root { get; }

    /// <summary>
    /// The <c>&lt;Components&gt;</c> elements of every document the expansion used (the enclosing document's first, then each prefab file's, once), so a loader can define the components scripts made
    /// before it reads any field.
    /// </summary>
    public List<XmlNode> Schemas { get; }

    /// <summary>
    /// Every entity of the tree by address, including the other names of an instance root (<c>address/rootId</c>), and including entities an override removed (they are <see cref="ExpandedEntity.IsRemoved"/>).
    /// What an Entity-typed field in the tree resolves against: look up only text that passes <see cref="EntityAddress.IsValid"/> (the expander leaves nothing else in a field of a prefab, and the
    /// made-up <c>~n</c> addresses of entities nothing can name are not valid text), never text a file wrote that is not an address.
    /// </summary>
    public IReadOnlyDictionary<string, ExpandedEntity> Index { get; }
}

/// <summary>
/// What expansions may spend, shared by the expansions of one load (a scene with a thousand instances spends one budget): <see cref="Limit"/> entities, and <see cref="WorkLimit"/> units of other work
/// (every component element handled, override applied, attribute copied, node merged or cloned, name tried). Both are counted as the thing is about to be done, so an expansion that is too big
/// stops with a <see cref="FormatException"/> having built at most that much, whatever its documents say and however they nest. A budget is spent, not refilled: a load (or each
/// <c>Instantiate</c>) takes a new one, and an expansion that failed leaves it spent.
/// </summary>
internal sealed class ExpansionBudget
{
    /// <summary>What a load may make unless told otherwise.</summary>
    public const int DefaultLimit = 262_144;

    /// <summary>How many units of work an entity of the limit pays for: enough for a few dozen components and overrides each, nowhere near enough to multiply a file's content by its instances without end.</summary>
    public const int WorkPerEntity = 64;

    public ExpansionBudget(int limit = DefaultLimit, int used = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(used);
        Limit = limit;
        Used = used;
        WorkLimit = (long)limit * WorkPerEntity;
    }

    /// <summary>The most entities.</summary>
    public int Limit { get; }

    /// <summary>The entities counted so far (never above <see cref="Limit"/>).</summary>
    public int Used { get; private set; }

    /// <summary>The most units of other work: <see cref="WorkPerEntity"/> for each entity of the limit.</summary>
    public long WorkLimit { get; }

    /// <summary>The units of work counted so far (never above <see cref="WorkLimit"/>).</summary>
    public long WorkUsed { get; private set; }

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

    /// <summary>Counts <paramref name="units"/> of work about to be done.</summary>
    /// <exception cref="FormatException">There would be more than <see cref="WorkLimit"/>.</exception>
    public void TakeWork(long units)
    {
        if (units > WorkLimit - WorkUsed)
        {
            throw new FormatException($"the prefabs ask for more than {WorkLimit} steps of work (components and overrides, over all their instances): refused, so that a few small files cannot keep a load busy without end");
        }

        WorkUsed += units;
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

/// <summary>
/// Finds the prefab a <c>prefab="…"</c> attribute names. It may throw <see cref="FormatException"/>, <see cref="IOException"/>, <see cref="ContentException"/>, <see cref="ArgumentException"/> or
/// <see cref="InvalidOperationException"/> for a file that cannot be read (the instance is then missing, with the exception's message); any other exception ends the whole load.
/// </summary>
internal delegate ResolvedPrefab PrefabResolver(string reference);
