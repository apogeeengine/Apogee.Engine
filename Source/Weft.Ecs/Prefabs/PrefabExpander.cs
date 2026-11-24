using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Weft.Content;
using Weft.Content.Xml;
using Weft.Core.Logging;

namespace Weft.Ecs;

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
//   6. Everything is counted against the budget just before it is done (ExpansionBudget): each entity, and each unit of other work (a component or override handled, an attribute copied, a node merged,
//      a name tried). An expansion over a limit stops with FormatException having done at most that much, however the files nest: a prefab file's content is repeated for every instance of it, so
//      counting entities alone would not bound the time or memory of a load.
//
// Scopes: an Entity-typed field means an entity of the document that holds it. In a prefab file that is an id of the file, which becomes an address under wherever the file was instanced; in a scene it
// is an address already. Fields of overrides and added entities are in their own document's scope, like everything else written in that document (rescoped when the document is a prefab file).
//
// Nodes: documents are shared between every instance of a prefab, so a node in an ExpandedEntity may be the document's own. Nothing here changes a node that was read (Merge and Prepare copy), and
// nothing downstream may.
internal sealed class PrefabExpander
{
    /// <summary>How many prefabs may be nested in one another along one chain, the outermost counted: a prefab that is the 17th is missing.</summary>
    public const int MaxDepth = 16;

    /// <summary>How many entities may be above one in an expansion: what <see cref="XmlNode.MaxDepth"/> already allows a document, so the code that walks a tree never runs out of stack.</summary>
    public const int MaxTreeDepth = XmlNode.MaxDepth;

    /// <summary>How many warnings an expander writes to the log: the same problem in a prefab file is the same for every instance, and a hostile file could have millions. After this one line says so.</summary>
    public const int MaxWarnings = 200;

    private const string Category = "Prefab";

    private readonly PrefabResolver _resolver;
    private readonly Func<string, string, bool> _isEntityField;
    private ExpansionBudget _budget;
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private readonly HashSet<XmlNode> _warned = new(ReferenceEqualityComparer.Instance);
    private int _synthetic;
    private int _warnings;

    /// <summary>The prefab files an expander has read and checked, by key: what is known about one file.</summary>
    private sealed record Source(string Key, XmlNode? Document, XmlNode? Root, int RootId, XmlNode? Schemas, string? Problem);

    /// <summary>
    /// Where the ids of one document point: <paramref name="Base"/> is the address that ids of the document are written under (empty in a scene), <paramref name="RootId"/> the id of the document's
    /// root entity (0 in a scene), <paramref name="Key"/> the prefab file (null in a scene).
    /// </summary>
    private sealed record Scope(string Base, int RootId, string? Key)
    {
        public static readonly Scope Scene = new("", 0, null);

        /// <summary>
        /// The address text of an Entity field of this document under <see cref="Base"/>; the root's id means the root itself. In a prefab file, text that is no address becomes empty (it names nothing, and
        /// must not be able to spell one of the made-up <c>~n</c> addresses); in a scene a made-up address is emptied the same way and any other text is left for the loader.
        /// </summary>
        public string Rescope(string text)
        {
            if (text.Length == 0)
            {
                return text;
            }

            if (Base.Length == 0)
            {
                return text[0] == '~' ? "" : text;
            }

            if (!EntityAddress.IsValid(text))
            {
                return "";
            }

            return RootId > 0 && text == RootId.ToString(CultureInfo.InvariantCulture) ? Base : $"{Base}/{text}";
        }
    }

    /// <param name="resolver">Finds a prefab by the text of a <c>prefab</c> attribute.</param>
    /// <param name="isEntityField">Whether a field of a component of the engine or the game holds an entity (<c>(component, field)</c>); the <c>&lt;Components&gt;</c> of the documents used are added to it.</param>
    /// <param name="budget">What may still be spent: one for a whole load, so that the scene's own entities can be counted in it too. Take a new one for each load or <c>Instantiate</c>.</param>
    public PrefabExpander(PrefabResolver resolver, Func<string, string, bool> isEntityField, ExpansionBudget budget)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(isEntityField);
        ArgumentNullException.ThrowIfNull(budget);
        _resolver = resolver;
        _isEntityField = isEntityField;
        _budget = budget;
    }

    /// <summary>How many warnings were not written because of <see cref="MaxWarnings"/>.</summary>
    public int SuppressedWarnings { get; private set; }

    /// <summary>
    /// Expands one instance element: the prefab it names, with its patches, overrides and added entities applied. By default the element is in a document whose entity fields are already addresses (a
    /// scene); <paramref name="options"/> says it is in a prefab file instead.
    /// </summary>
    /// <param name="instance">An <c>&lt;Entity prefab="…"&gt;</c> element.</param>
    /// <param name="address">The address of the instance's root (<see cref="EntityAddress"/>, at most <see cref="EntityAddress.MaxSegments"/> segments): the entities of the prefab are <c>address/id</c>.</param>
    /// <param name="enclosingSchemas">The <c>&lt;Components&gt;</c> element of the document the element is in, so that entity fields of components it defines are known.</param>
    /// <param name="options">Where the element is, when not in a scene.</param>
    /// <exception cref="FormatException">The expansion would make more entities than the budget allows, or ask for more work than it does, or nest entities more than <see cref="MaxTreeDepth"/> deep (nothing of it is kept).</exception>
    public Expansion Expand(XmlNode instance, string address, XmlNode? enclosingSchemas = null, ExpandOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Name != "Entity" || !instance.HasAttribute("prefab"))
        {
            throw new ArgumentException("an instance element is an <Entity> with a prefab attribute", nameof(instance));
        }

        if (!EntityAddress.IsValid(address))
        {
            throw new ArgumentException($"'{address}' is not an entity address of at most {EntityAddress.MaxSegments} segments", nameof(address));
        }

        options ??= ExpandOptions.Scene;
        if (options.ScopeKey is null != (options.ScopeBase.Length == 0) || options.StartDepth is < 0 or > MaxTreeDepth)
        {
            throw new ArgumentException("the scope of an instance in a prefab file has both a key and a base address, and the depth is from 0 to " + MaxTreeDepth.ToString(CultureInfo.InvariantCulture), nameof(options));
        }

        var scope = options.ScopeKey is null ? Scope.Scene : new Scope(options.ScopeBase, options.ScopeRootId, options.ScopeKey);
        var run = new Run(this);
        run.UseSchemas("", enclosingSchemas);
        ExpandedEntity root = run.ExpandInstance(instance, address, run.ReadId(instance), scope, options.StartDepth);
        run.MakeNamesUnique(root);
        return new Expansion(root, run.Schemas, run.Index, run.Prefabs);
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

    // The reserved words are on when they say true (an xs:boolean may also be written 1).
    private static bool IsSet(string? value) => value is "true" or "1";

    /// <summary>Takes a new budget for the expansions that follow (a save gives each instance its own, as each load and <c>Instantiate</c> had); what was warned about stays warned about.</summary>
    public void UseBudget(ExpansionBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        _budget = budget;
    }

    private static string Where(XmlNode node) => $"{node.Origin ?? "prefab"}:{node.Line}";

    // A warning about a node of a document: once per node (the same line of a prefab file is the same problem for every instance of it) and at most MaxWarnings in all.
    private void Warn(XmlNode node, string message)
    {
        if (!_warned.Add(node))
        {
            return;
        }

        if (_warnings >= MaxWarnings)
        {
            if (SuppressedWarnings++ == 0)
            {
                Log.Warn(Category, $"more than {MaxWarnings} warnings about prefabs: the others are not written");
            }

            return;
        }

        _warnings++;
        Log.Warn(Category, $"{Where(node)}: {message}");
    }

    // How much a Merge or a Clone of the node costs: the node, its attributes and everything below it.
    private static long Size(XmlNode node)
    {
        long size = 1 + node.Attributes.Count;
        foreach (XmlNode child in node.Children)
        {
            size += Size(child);
        }

        return size;
    }

    // ---- One expansion -----------------------------------------------------------------------------------------------------------------

    // The state of one Expand call: the prefabs being expanded (for cycles), every address made, the schemas and prefabs used.
    private sealed class Run(PrefabExpander owner)
    {
        private readonly List<string> _stack = [];
        private readonly Dictionary<string, HashSet<string>> _entityFields = new(StringComparer.Ordinal);
        private readonly HashSet<string> _schemasUsed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _prefabsUsed = new(StringComparer.Ordinal);

        public Dictionary<string, ExpandedEntity> Index { get; } = new(StringComparer.Ordinal);

        public List<XmlNode> Schemas { get; } = [];

        public List<string> Prefabs { get; } = [];

        // Whether what is being applied is written in the document the expansion was asked for (not in a prefab file reached by nesting): only that document's leftovers are the caller's to keep.
        private bool Outermost => _stack.Count == 0;

        private void Warn(XmlNode node, string message) => owner.Warn(node, message);

        private void Work(long units) => owner._budget.TakeWork(units);

        // ---- Making the tree

        // The instance an element describes, in `scope` (the document that holds the element). `id` is the element's id there, `address` its address.
        public ExpandedEntity ExpandInstance(XmlNode element, string address, int? id, Scope scope, int depth)
        {
            string reference = element.Attribute("prefab")!;
            Source source = owner.Open(reference);
            string key = source.Key;
            if (_prefabsUsed.Add(key))
            {
                Prefabs.Add(key);
            }

            string? problem = source.Problem ?? StackProblem(key) ?? AddressProblem(address);
            if (problem is not null)
            {
                return MissingInstance(element, address, id, scope, key, reference, problem, depth, cut: source.Problem is null);
            }

            UseSchemas(key, source.Schemas);
            var inner = new Scope(address, source.RootId, key);
            ExpandedEntity root;
            _stack.Add(key);
            try
            {
                // The root is the prefab's root entity at the instance's address; as an element of this document it has this element's id.
                root = BuildPlain(source.Root!, address, scope.Key is null ? null : id, scope.Key, inner, depth);
            }
            finally
            {
                _stack.RemoveAt(_stack.Count - 1);
            }

            root.InstancePrefab = key;
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

        // The entities of an instance are at `address/id`: that must still be an address.
        private static string? AddressProblem(string address)
        {
            int segments = 1;
            foreach (char c in address)
            {
                if (c == '/')
                {
                    segments++;
                }
            }

            return segments >= EntityAddress.MaxSegments ? $"its entities would have addresses of more than {EntityAddress.MaxSegments} segments" : null;
        }

        // The root alone. When the expansion is of the element's own document, every child element of the instance element is kept as written.
        private ExpandedEntity MissingInstance(XmlNode element, string address, int? id, Scope scope, string key, string reference, string problem, int depth, bool cut)
        {
            Warn(element, $"<Entity '{element.Attribute("name")}'> is an instance of the prefab {reference}, which cannot be used: {problem}. It is left empty, and what its element holds is kept");
            ExpandedEntity entity = Make(element, address, scope.Key is null ? null : id, scope.Key, depth, problem);
            entity.InstancePrefab = key;
            entity.IsCut = cut;
            if (Outermost)
            {
                Work(1 + element.Children.Count);
                entity.Unapplied.AddRange(element.Children);
            }

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
                        Work(1);
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

        public int? ReadId(XmlNode element)
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

        // An entity an instance element or override of the expansion's own document adds (an entity of a prefab file is part of the prefab and re-derived each time).
        private void AddEntity(ExpandedEntity target, XmlNode element, Scope scope)
        {
            Work(1);
            ExpandedEntity added = BuildChild(element, scope, target.Depth + 1);
            if (Outermost)
            {
                added.AddedBy = element;
            }

            Attach(target, target.Added, added);
        }

        // ---- Components

        // A component element of a document, as the entity has it: rescoped, and written whole. Two with one name: the later wins, as when a scene is loaded.
        private void AddComponent(ExpandedEntity entity, XmlNode component, Scope scope)
        {
            Work(1 + component.Attributes.Count);
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
        private bool IsEngineMade(XmlNode component)
        {
            if (component.Name is nameof(PrefabInstance) or nameof(PrefabMember))
            {
                Warn(component, $"{component.Name} is made by the engine, not read from a file: skipped");
                return true;
            }

            return false;
        }

        // The component element as the tree keeps it: Entity fields rescoped, and none of the words Merge reserves (patch, remove, append). The same node when there is nothing to change: documents are
        // shared. When there is, a copy of the element itself with the changed attributes; its children (a component has none to speak of) are the original's, shared and not changed by anyone.
        // The caller has charged for the attributes.
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

            var copy = new XmlNode(component.Name, component.Line) { Origin = component.Origin, Text = component.Text };
            copy.Attributes.AddRange(kept);
            copy.Children.AddRange(component.Children);
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
                        AddEntity(root, child, scope);
                        break;
                    case "Override":
                        ApplyOverride(root, child, scope);
                        break;
                    default:
                        Work(1 + child.Attributes.Count);
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
            Work(1 + over.Attributes.Count);
            string? problem = OverrideProblem(over);
            ExpandedEntity? target = problem is null ? FindTarget(root, over.Attribute("entity")!, out problem) : null;
            if (target is null)
            {
                Keep(root, over, problem!);
                return;
            }

            if (IsSet(over.Attribute("remove")))
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
                    AddEntity(target, child, scope);
                    continue;
                }

                Work(1 + child.Attributes.Count);
                if (!IsEngineMade(child) && ApplyComponent(target, child, scope) is { } why)
                {
                    Warn(child, $"{why}: the override is kept as written but not applied");
                    unapplied ??= OverrideWithEntityOf(over, over.Attribute("entity")!);
                    unapplied.Children.Add(child);
                }
            }

            if (unapplied is not null && Outermost)
            {
                root.Unapplied.Add(unapplied);
            }
        }

        // An <Override> may only have entity, name and remove, and must say which entity; anything else is something this engine does not know (a newer one wrote it), so none of the element is applied.
        private static string? OverrideProblem(XmlNode over)
        {
            foreach (XmlProperty attribute in over.Attributes)
            {
                if (attribute.Name is not ("entity" or "name" or "remove"))
                {
                    return $"an <Override> has an attribute '{attribute.Name}' this engine does not know";
                }
            }

            return over.HasAttribute("entity") ? null : "an <Override> has no entity attribute (the root of an instance is addressed by its id in the prefab, or patched by component elements of the instance itself)";
        }

        private static XmlNode OverrideWithEntityOf(XmlNode like, string entity)
        {
            var copy = new XmlNode("Override", like.Line) { Origin = like.Origin };
            copy.SetAttribute("entity", entity);
            return copy;
        }

        // The entity goes, with everything below it. What the expansion's own document added below it goes too, so it is kept as written, under an override that names the same place (which is
        // still gone, so it stays aside until the entity is back).
        private void RemoveTarget(ExpandedEntity root, ExpandedEntity target, XmlNode over)
        {
            if (target.Parent is not { } parent)
            {
                Keep(root, over, "the root of an instance cannot be removed by its own override (remove the instance)");
                return;
            }

            Work(parent.Children.Count + parent.Added.Count); // taking it out of the list of its siblings
            if (Outermost)
            {
                KeepAddedBelow(root, target);
            }

            target.Remove();
            if (over.Children.Count > 0)
            {
                Warn(over, "an <Override> that removes an entity has other content: ignored, the entity is gone");
            }
        }

        private void KeepAddedBelow(ExpandedEntity root, ExpandedEntity removed)
        {
            string prefix = root.Address + "/";
            foreach (ExpandedEntity entity in removed.Subtree())
            {
                // Topmost only: what is added below an added entity is part of its element.
                if (entity.AddedBy is not { } element || entity.Parent is not { AddedBy: null } parent || !parent.Address.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                Warn(element, $"the entity {parent.Address} it was added under is removed by another override: it is kept as written but not made");
                XmlNode held = OverrideWithEntityOf(element, parent.Address[prefix.Length..]);
                held.Children.Add(element);
                root.Unapplied.Add(held);
            }
        }

        // The entity an address (relative to the instance) names, found by address and not by the hierarchy; null with the reason when there is none. A segment that is the id of the
        // root of an instance (the entity itself) is another name for that root.
        private ExpandedEntity? FindTarget(ExpandedEntity root, string relative, out string? problem)
        {
            problem = null;
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
            if (IsSet(patch.Attribute("remove")))
            {
                return target.RemoveComponent(patch.Name) ? null : $"the entity has no {patch.Name} to remove";
            }

            if (IsSet(patch.Attribute("append")))
            {
                target.SetComponent(prepared); // whole: if the prefab has the component too, the instance's is the one that counts
                return null;
            }

            if (target.Component(patch.Name) is not { } existing)
            {
                return $"the entity has no {patch.Name} to patch (a component the prefab does not have is added whole, with append=\"true\")";
            }

            Work(Size(existing) + Size(prepared)); // Merge copies both
            target.SetComponent(DefDatabase.Merge(existing, prepared));
            return null;
        }

        // An element that could not be applied: said, and kept in the root's Unapplied when the document is the expansion's own.
        private void Keep(ExpandedEntity root, XmlNode element, string problem)
        {
            Work(1);
            Warn(element, $"{problem}: <{element.Name}> is kept as written but not applied");
            if (Outermost)
            {
                root.Unapplied.Add(element);
            }
        }

        // ---- Names

        // Siblings get unique names in the order a loader creates them (members first, then added), by the rule of World.TrySetName: a taken name gets " 2", " 3"… The root is left as it is: its siblings
        // are the loader's. Every name tried is counted: many siblings with one name are quadratic, as they are in the loader.
        public void MakeNamesUnique(ExpandedEntity entity)
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

        private void NameInOrder(List<ExpandedEntity> siblings, HashSet<string> taken)
        {
            foreach (ExpandedEntity child in siblings)
            {
                if (child.Name is { } name)
                {
                    child.Name = FreeName(taken, name);
                }
            }
        }

        private string FreeName(HashSet<string> taken, string name)
        {
            string candidate = name;
            for (int n = 2; !taken.Add(candidate); n++)
            {
                Work(1);
                candidate = $"{name} {n}";
            }

            return candidate;
        }
    }
}

/// <summary>Where the instance element given to <see cref="PrefabExpander.Expand"/> is, when it is not in a scene: in a prefab file that is itself being expanded or edited.</summary>
internal sealed record ExpandOptions
{
    /// <summary>Expand an element of a scene.</summary>
    public static readonly ExpandOptions Scene = new();

    /// <summary>The key of the prefab file the element is in; null for a scene. The patches, overrides and added entities the element holds are written in that file's scope and are rescoped, and the root gets the element's id as a member of that file.</summary>
    public string? ScopeKey { get; init; }

    /// <summary>The address the ids of that file are written under (where the file is instanced); empty for a scene.</summary>
    public string ScopeBase { get; init; } = "";

    /// <summary>The id of the root entity of that file: the id that means the instance itself in an Entity field.</summary>
    public int ScopeRootId { get; init; }

    /// <summary>How many entities are above the instance's root in the whole expansion this one is part of, so that the nesting limit counts from the top (0 for a scene's own entity).</summary>
    public int StartDepth { get; init; }
}
