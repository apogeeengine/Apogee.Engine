using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Apogee.Content;
using Apogee.Content.Xml;
using Apogee.Core.Logging;

namespace Apogee.Ecs;

// Expanding an instance of a prefab (docs/manual/content/prefabs.md, "Nested prefabs" and "Overrides"): pure XML in, a tree of ExpandedEntity out, no world involved.
//
// An instance element is <Entity id name prefab="…"> in a scene or in another prefab file. It holds, in this order of meaning:
//   * component elements        patches of the root of the instance (<Light Intensity="40"/>), or whole components with append="true", or remove="true"
//   * <Entity> elements         entities added below the root
//   * <Override entity="addr">  the same two things for another entity of the instance, found by ADDRESS (EntityAddress), wherever the prefab author has since moved it
//
// The rules that keep hostile or stale content harmless, in the order the code applies them:
//   1. The prefab is looked up by the resolver and checked once (PrefabDocument.Check). Not found, not readable, not a prefab, or a root that is itself an instance: the instance is MISSING.
//   2. A prefab that is already being expanded (a -> b -> a) or is nested deeper than MaxDepth: MISSING. A missing instance is its root entity alone; everything its element holds is kept in Unapplied.
//   3. The prefab's own tree is made first, and the instances inside it are expanded (with their own overrides, in the prefab's scope) as part of making it: inner overrides apply before outer ones.
//   4. The instance's own overrides apply, strictly: an address that finds nothing, a patch of a component the entity does not have, an unknown attribute: Unapplied (logged, kept as written), never
//      applied anyway and never thrown away. Merge only ever sees a component both sides have.
//   5. Sibling names are made unique in tree order as World.TrySetName will do when the entities are created.
//   6. Every entity is counted against the budget just before it is made: an expansion over the limit stops with FormatException having made at most `Limit` nodes.
//
// Scopes: an Entity-typed field means an entity of the document that holds it. In a prefab file that is an id of the file, which becomes an address under wherever the file was instanced; in a scene it
// is an address already. Fields of overrides and added entities are in their own document's scope, like everything else written in that document (rescoped when the document is a prefab file).
internal sealed class PrefabExpander
{
    /// <summary>How many prefabs may be nested in one another along one chain, the outermost counted: a prefab that is the 17th is missing.</summary>
    public const int MaxDepth = 16;

    /// <summary>How many entities may be above one in an expansion: what <see cref="XmlNode.MaxDepth"/> already allows a document, so the code that walks a tree never runs out of stack.</summary>
    public const int MaxTreeDepth = XmlNode.MaxDepth;

    private const string Category = "Prefab";

    private readonly PrefabResolver _resolver;
    private readonly Func<string, string, bool> _isEntityField;
    private readonly ExpansionBudget _budget;
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private int _synthetic;

    /// <summary>The prefab files an expander has read and checked, by key: what is known about one file.</summary>
    private sealed record Source(string Key, XmlNode? Document, XmlNode? Root, int RootId, XmlNode? Schemas, string? Problem);

    /// <summary>
    /// Where the ids of one document point: <paramref name="Base"/> is the address that ids of the document are written under (empty in a scene), <paramref name="RootId"/> the id of the document's
    /// root entity (0 in a scene), <paramref name="Key"/> the prefab file (null in a scene).
    /// </summary>
    private sealed record Scope(string Base, int RootId, string? Key)
    {
        public static readonly Scope Scene = new("", 0, null);

        /// <summary>The address text of an Entity field of this document under <see cref="Base"/>; the root's id means the root itself. Text that is no address is left alone (it names nothing).</summary>
        public string Rescope(string text)
        {
            if (Base.Length == 0 || !EntityAddress.IsValid(text))
            {
                return text;
            }

            return RootId > 0 && text == RootId.ToString(CultureInfo.InvariantCulture) ? Base : $"{Base}/{text}";
        }
    }

    /// <param name="resolver">Finds a prefab by the text of a <c>prefab</c> attribute.</param>
    /// <param name="isEntityField">Whether a field of a component of the engine or the game holds an entity (<c>(component, field)</c>); the <c>&lt;Components&gt;</c> of the documents used are added to it.</param>
    /// <param name="budget">How many entities may still be made: one for a whole load, so that the scene's own entities can be counted in it too.</param>
    public PrefabExpander(PrefabResolver resolver, Func<string, string, bool> isEntityField, ExpansionBudget budget)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(isEntityField);
        ArgumentNullException.ThrowIfNull(budget);
        _resolver = resolver;
        _isEntityField = isEntityField;
        _budget = budget;
    }

    /// <summary>
    /// Expands one instance element: the prefab it names, with its patches, overrides and added entities applied. The element is in a document whose entity fields are already addresses (a scene).
    /// </summary>
    /// <param name="instance">An <c>&lt;Entity prefab="…"&gt;</c> element.</param>
    /// <param name="address">The address of the instance's root (<see cref="EntityAddress"/>): the entities of the prefab are <c>address/id</c>.</param>
    /// <param name="enclosingSchemas">The <c>&lt;Components&gt;</c> element of the document the element is in, so that entity fields of components it defines are known.</param>
    /// <exception cref="FormatException">The expansion would make more entities than the budget allows (nothing of it is kept).</exception>
    public Expansion Expand(XmlNode instance, string address, XmlNode? enclosingSchemas = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Name != "Entity" || !instance.HasAttribute("prefab"))
        {
            throw new ArgumentException("an instance element is an <Entity> with a prefab attribute", nameof(instance));
        }

        if (!EntityAddress.IsValid(address))
        {
            throw new ArgumentException($"'{address}' is not an entity address", nameof(address));
        }

        var run = new Run(this);
        run.UseSchemas("", enclosingSchemas);
        ExpandedEntity root = run.ExpandInstance(instance, address, null, Scope.Scene, 0);
        Run.MakeNamesUnique(root);
        return new Expansion(root, run.Schemas, run.Index);
    }

    // ---- Documents ---------------------------------------------------------------------------------------------------------------------

    // What the resolver says a reference is, checked once per document: a Source with a Problem when it cannot be used. A document read again (a prefab that was saved) is checked again.
    private Source Open(string reference)
    {
        ResolvedPrefab resolved;
        try
        {
            resolved = _resolver(reference);
        }
        catch (Exception e) when (e is FormatException or IOException or ContentException or ArgumentException or InvalidOperationException)
        {
            return new Source(reference, null, null, 0, null, e.Message);
        }

        if (_sources.TryGetValue(resolved.Key, out Source? known) && ReferenceEquals(known.Document, resolved.Document))
        {
            return known;
        }

        Source made = Read(resolved);
        _sources[resolved.Key] = made;
        return made;
    }

    private static Source Read(ResolvedPrefab resolved)
    {
        if (resolved.Document is not { } document)
        {
            return new Source(resolved.Key, null, null, 0, null, resolved.Problem ?? "there is no such prefab");
        }

        try
        {
            PrefabDocument.Check(document, resolved.Key, warnNextId: false);
        }
        catch (FormatException e)
        {
            return new Source(resolved.Key, document, null, 0, null, e.Message);
        }

        XmlNode root = PrefabDocument.RootEntity(document);
        if (root.HasAttribute("prefab"))
        {
            // The entity would be the root of two instances at once (this prefab's and the other's), and one entity has one PrefabInstance.
            return new Source(resolved.Key, document, null, 0, null, "its root entity is an instance of another prefab: put it under an entity of its own");
        }

        int rootId = PrefabDocument.TryParseId(root.Attribute("id"), out int id) ? id : 0; // Check made sure of it
        return new Source(resolved.Key, document, root, rootId, document.Child("Components"), null);
    }

    private static string Where(XmlNode node) => $"{node.Origin ?? "prefab"}:{node.Line}";

    // ---- One expansion -----------------------------------------------------------------------------------------------------------------

    // The state of one Expand call: the prefabs being expanded (for cycles), every address made, the schemas used.
    private sealed class Run(PrefabExpander owner)
    {
        private readonly List<string> _stack = [];
        private readonly Dictionary<string, HashSet<string>> _entityFields = new(StringComparer.Ordinal);
        private readonly HashSet<string> _schemasUsed = new(StringComparer.Ordinal);

        public Dictionary<string, ExpandedEntity> Index { get; } = new(StringComparer.Ordinal);

        public List<XmlNode> Schemas { get; } = [];

        // ---- Making the tree

        // The instance an element describes, in `scope` (the document that holds the element). `id` is the element's id there, `address` its address.
        public ExpandedEntity ExpandInstance(XmlNode element, string address, int? id, Scope scope, int depth)
        {
            string reference = element.Attribute("prefab")!;
            Source source = owner.Open(reference);
            string key = source.Problem is null ? source.Key : reference;
            string? problem = source.Problem ?? StackProblem(source.Key);
            if (problem is not null)
            {
                return MissingInstance(element, address, id, scope, key, reference, problem, depth);
            }

            UseSchemas(source.Key, source.Schemas);
            var inner = new Scope(address, source.RootId, source.Key);
            ExpandedEntity root;
            _stack.Add(source.Key);
            try
            {
                // The root is the prefab's root entity at the instance's address; as an element of this document it has this element's id.
                root = BuildPlain(source.Root!, address, scope.Key is null ? null : id, scope.Key, inner, depth);
            }
            finally
            {
                _stack.RemoveAt(_stack.Count - 1);
            }

            root.InstancePrefab = source.Key;
            root.InstanceRootId = source.RootId;
            Index.TryAdd($"{root.Address}/{source.RootId}", root); // the other name of the root
            ApplyInstanceElement(root, element, scope);
            return root;
        }

        // Why a prefab on its way in cannot be expanded here, or null: it is being expanded already, or this is the MaxDepth+1st level.
        private string? StackProblem(string key)
        {
            int at = _stack.IndexOf(key);
            if (at >= 0)
            {
                string chain = string.Join(" → ", [.. _stack.GetRange(at, _stack.Count - at), key]);
                return $"{key} contains itself ({chain})";
            }

            return _stack.Count >= MaxDepth ? $"prefabs are nested more than {MaxDepth} deep" : null;
        }

        // The root alone, with every child element of the instance element kept as written.
        private ExpandedEntity MissingInstance(XmlNode element, string address, int? id, Scope scope, string key, string reference, string problem, int depth)
        {
            Warn(element, $"<Entity '{element.Attribute("name")}'> is an instance of the prefab {reference}, which cannot be used: {problem}. It is left empty, and what its element holds is kept");
            ExpandedEntity entity = Make(element, address, scope.Key is null ? null : id, scope.Key, depth, problem);
            entity.InstancePrefab = key;
            entity.Unapplied.AddRange(element.Children);
            return entity;
        }

        // A plain entity element of a document, and everything below it. `childScope` is where the ids of its children are written; for an instance root that is the instance's own prefab.
        private ExpandedEntity BuildPlain(XmlNode element, string address, int? memberId, string? memberPrefab, Scope childScope, int depth)
        {
            ExpandedEntity entity = Make(element, address, memberId, memberPrefab, depth, null);
            foreach (XmlNode child in element.Children)
            {
                switch (child.Name)
                {
                    case "Entity":
                        Attach(entity, entity.Children, BuildChild(child, childScope, depth + 1));
                        break;
                    case "Override":
                        Warn(child, "an <Override> belongs inside an instance of a prefab (an <Entity prefab=\"…\">): skipped");
                        break;
                    default:
                        AddComponent(entity, child, childScope);
                        break;
                }
            }

            return entity;
        }

        // An <Entity> element of a document in `scope`: an instance or a plain entity, at the address its id gives it.
        private ExpandedEntity BuildChild(XmlNode element, Scope scope, int depth)
        {
            if (depth > MaxTreeDepth)
            {
                throw new FormatException($"{Where(element)}: the prefabs make entities nested more than {MaxTreeDepth} deep");
            }

            int? id = ReadId(element);
            string address = Claim(element, id is { } known ? EntityAddress.Combine(scope.Base, known) : "");
            return element.HasAttribute("prefab")
                ? ExpandInstance(element, address, id, scope, depth)
                : BuildPlain(element, address, scope.Key is null ? null : id, scope.Key, scope, depth);
        }

        private static int? ReadId(XmlNode element)
        {
            if (element.Attribute("id") is not { } text)
            {
                return null;
            }

            if (PrefabDocument.TryParseId(text, out int id))
            {
                return id;
            }

            Warn(element, $"the entity id '{text}' is not a number from 1 up: nothing can refer to it");
            return null;
        }

        // The address an entity element will have: the one its id gives it. An entity with none, or with one that another entity already has, gets a made-up one that starts with ~ (an address a document
        // cannot write), so every entity has a unique address and, for an instance, what is below it (its own scope) still resolves without meeting anyone else's. Decided before the entity or anything
        // below it is made, and only registered by Make.
        private string Claim(XmlNode element, string address)
        {
            if (address.Length > 0 && Index.ContainsKey(address))
            {
                Warn(element, $"the entity address {address} is used twice: references to it go to the first, and this one cannot be referred to");
                address = "";
            }

            return address.Length == 0 ? $"~{++owner._synthetic}" : address;
        }

        // The one place an entity is made: counted first (so nothing is allocated past the budget), then registered at its claimed address.
        private ExpandedEntity Make(XmlNode element, string address, int? memberId, string? memberPrefab, int depth, string? missing)
        {
            owner._budget.Take();
            var entity = new ExpandedEntity
            {
                Address = address,
                MemberId = memberId,
                MemberPrefab = memberId is null ? null : memberPrefab,
                Name = element.Attribute("name") is { Length: > 0 } name ? name : null,
                Depth = depth,
                IsMissing = missing is not null,
                MissingReason = missing,
            };
            Index[address] = entity;
            return entity;
        }

        private static void Attach(ExpandedEntity parent, List<ExpandedEntity> list, ExpandedEntity child)
        {
            child.Parent = parent;
            list.Add(child);
        }

        // ---- Components

        // A component element of a document, as the entity has it: rescoped, and written whole. Two with one name: the later wins, as when a scene is loaded.
        private void AddComponent(ExpandedEntity entity, XmlNode component, Scope scope)
        {
            if (IsEngineMade(component))
            {
                return;
            }

            if (entity.Component(component.Name) is not null)
            {
                Warn(component, $"the entity has {component.Name} twice: the later one is used");
            }

            entity.SetComponent(Prepare(component, scope));
        }

        // PrefabInstance and PrefabMember are made by the engine, whatever a file says.
        private static bool IsEngineMade(XmlNode component)
        {
            if (component.Name is nameof(PrefabInstance) or nameof(PrefabMember))
            {
                Warn(component, $"{component.Name} is made by the engine, not read from a file: skipped");
                return true;
            }

            return false;
        }

        // The component element as the tree keeps it: Entity fields rescoped, and none of the words Merge reserves (patch, remove, append). The same node when there is nothing to change: documents are shared.
        private XmlNode Prepare(XmlNode component, Scope scope)
        {
            List<XmlProperty>? kept = null;
            for (int i = 0; i < component.Attributes.Count; i++)
            {
                XmlProperty attribute = component.Attributes[i];
                XmlProperty? replacement = attribute.Name is "patch" or "remove" or "append"
                    ? null
                    : IsEntityField(component.Name, attribute.Name) ? attribute with { Value = scope.Rescope(attribute.Value) } : attribute;
                if (kept is null && replacement == attribute)
                {
                    continue;
                }

                kept ??= [.. component.Attributes.GetRange(0, i)];
                if (replacement is { } value)
                {
                    kept.Add(value);
                }
            }

            if (kept is null)
            {
                return component;
            }

            XmlNode copy = component.Clone();
            copy.Attributes.Clear();
            copy.Attributes.AddRange(kept);
            return copy;
        }

        private bool IsEntityField(string component, string field) =>
            _entityFields.TryGetValue(component, out HashSet<string>? fields) && fields.Contains(field) || owner._isEntityField(component, field);

        // The Entity fields of the components a document defines, once per document.
        public void UseSchemas(string key, XmlNode? schemas)
        {
            if (schemas is null || !_schemasUsed.Add(key))
            {
                return;
            }

            Schemas.Add(schemas);
            foreach (XmlNode component in schemas.Children)
            {
                if (component.Name != "Component" || component.Attribute("name") is not { } name)
                {
                    continue;
                }

                foreach (XmlNode field in component.Children)
                {
                    if (field.Name == "Field" && field.Attribute("kind") == "Entity" && field.Attribute("name") is { } fieldName)
                    {
                        if (!_entityFields.TryGetValue(name, out HashSet<string>? fields))
                        {
                            _entityFields[name] = fields = new HashSet<string>(StringComparer.Ordinal);
                        }

                        fields.Add(fieldName);
                    }
                }
            }
        }

        // ---- Applying an instance element

        // What the element of the instance says, in the order it says it.
        private void ApplyInstanceElement(ExpandedEntity root, XmlNode element, Scope scope)
        {
            if (element.Attribute("name") is { Length: > 0 } name)
            {
                root.Name = name;
            }

            foreach (XmlNode child in element.Children)
            {
                switch (child.Name)
                {
                    case "Entity":
                        Attach(root, root.Added, BuildChild(child, scope, root.Depth + 1));
                        break;
                    case "Override":
                        ApplyOverride(root, child, scope);
                        break;
                    default:
                        if (!IsEngineMade(child) && ApplyComponent(root, child, scope) is { } problem)
                        {
                            Keep(root, child, problem);
                        }

                        break;
                }
            }
        }

        // One <Override entity="…">: all of it applies to the entity it addresses, or the part that cannot is kept (the whole element when the address or an attribute is the problem).
        private void ApplyOverride(ExpandedEntity root, XmlNode over, Scope scope)
        {
            string? problem = OverrideProblem(over);
            ExpandedEntity? target = problem is null ? FindTarget(root, over.Attribute("entity") ?? "", out problem) : null;
            if (target is null)
            {
                Keep(root, over, problem!);
                return;
            }

            if (over.Attribute("remove") == "true")
            {
                RemoveTarget(root, target, over);
                return;
            }

            if (over.Attribute("name") is { Length: > 0 } name)
            {
                target.Name = name;
            }

            XmlNode? unapplied = null;
            foreach (XmlNode child in over.Children)
            {
                if (child.Name == "Entity")
                {
                    Attach(target, target.Added, BuildChild(child, scope, target.Depth + 1));
                }
                else if (!IsEngineMade(child) && ApplyComponent(target, child, scope) is { } why)
                {
                    Warn(child, $"{why}: the override is kept as written but not applied");
                    unapplied ??= EmptyOverrideLike(over);
                    unapplied.Children.Add(child);
                }
            }

            if (unapplied is not null)
            {
                root.Unapplied.Add(unapplied);
            }
        }

        // An <Override> may only have entity, name and remove; anything else is something this engine does not know (a newer one wrote it), so none of the element is applied.
        private static string? OverrideProblem(XmlNode over)
        {
            foreach (XmlProperty attribute in over.Attributes)
            {
                if (attribute.Name is not ("entity" or "name" or "remove"))
                {
                    return $"an <Override> has an attribute '{attribute.Name}' this engine does not know";
                }
            }

            return null;
        }

        private static XmlNode EmptyOverrideLike(XmlNode over)
        {
            var copy = new XmlNode(over.Name, over.Line) { Origin = over.Origin };
            if (over.Attribute("entity") is { } entity)
            {
                copy.SetAttribute("entity", entity);
            }

            return copy;
        }

        private static void RemoveTarget(ExpandedEntity root, ExpandedEntity target, XmlNode over)
        {
            if (!target.Remove())
            {
                Keep(root, over, "the root of an instance cannot be removed by its own override (remove the instance)");
            }
            else if (over.Children.Count > 0)
            {
                Warn(over, "an <Override> that removes an entity has other content: ignored, the entity is gone");
            }
        }

        // The entity an address (relative to the instance, "" for the root) names, found by address and not by the hierarchy; null with the reason when there is none. A segment that is the id of the
        // root of an instance (the entity itself) is another name for that root.
        private ExpandedEntity? FindTarget(ExpandedEntity root, string relative, out string? problem)
        {
            problem = null;
            if (relative.Length == 0)
            {
                return root;
            }

            if (!EntityAddress.TryParse(relative, out int[] segments))
            {
                problem = $"'{relative}' is not an entity address (ids from 1 up, separated by /)";
                return null;
            }

            ExpandedEntity current = root;
            foreach (int id in segments)
            {
                if (current.IsInstanceRoot && id == current.InstanceRootId)
                {
                    continue;
                }

                if (!Index.TryGetValue(EntityAddress.Combine(current.Address, id), out ExpandedEntity? next) || next.IsRemoved)
                {
                    problem = $"the instance has no entity {relative}";
                    return null;
                }

                current = next;
            }

            return current;
        }

        // One component element applied to an entity; null when it was, otherwise why not (it then stays in Unapplied, never half applied).
        private string? ApplyComponent(ExpandedEntity target, XmlNode patch, Scope scope)
        {
            XmlNode prepared = Prepare(patch, scope);
            if (patch.Attribute("remove") == "true")
            {
                return target.RemoveComponent(patch.Name) ? null : $"the entity has no {patch.Name} to remove";
            }

            if (patch.Attribute("append") == "true")
            {
                target.SetComponent(prepared); // whole: if the prefab has the component too, the instance's is the one that counts
                return null;
            }

            if (target.Component(patch.Name) is not { } existing)
            {
                return $"the entity has no {patch.Name} to patch (a component the prefab does not have is added whole, with append=\"true\")";
            }

            target.SetComponent(DefDatabase.Merge(existing, prepared));
            return null;
        }

        private static void Keep(ExpandedEntity root, XmlNode element, string problem)
        {
            Warn(element, $"{problem}: <{element.Name}> is kept as written but not applied");
            root.Unapplied.Add(element);
        }

        private static void Warn(XmlNode node, string message) => Log.Warn(Category, $"{Where(node)}: {message}");

        // ---- Names

        // Siblings get unique names in the order a loader creates them (members first, then added), by the rule of World.TrySetName: a taken name gets " 2", " 3"… The root is left as it is: its siblings
        // are the loader's.
        public static void MakeNamesUnique(ExpandedEntity entity)
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
            NameInOrder(entity.Children, taken);
            NameInOrder(entity.Added, taken);
            foreach (ExpandedEntity child in entity.Children)
            {
                MakeNamesUnique(child);
            }

            foreach (ExpandedEntity child in entity.Added)
            {
                MakeNamesUnique(child);
            }
        }

        private static void NameInOrder(List<ExpandedEntity> siblings, HashSet<string> taken)
        {
            foreach (ExpandedEntity child in siblings)
            {
                if (child.Name is { } name)
                {
                    child.Name = FreeName(taken, name);
                }
            }
        }

        private static string FreeName(HashSet<string> taken, string name)
        {
            string candidate = name;
            for (int n = 2; !taken.Add(candidate); n++)
            {
                candidate = $"{name} {n}";
            }

            return candidate;
        }
    }
}
