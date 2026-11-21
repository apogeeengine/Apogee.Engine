using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using Apogee.Content;
using Apogee.Content.Assets;
using Apogee.Content.FileSystem;
using Apogee.Content.Xml;
using Apogee.Core.Assets;
using Apogee.Core.Logging;
using Apogee.Reflection;

namespace Apogee.Ecs;

// Scenes: the user entities of a world as XML text (a `.ascene` file), read and written through the engine's XmlNode and field by field from the registry's descriptions, so no reflection is
// involved and it works in a NativeAOT program. The format is ours, not Flecs' own serialization, which would carry the entities of the types and systems the engine registers.
//
//   <?xml version="1.0" encoding="utf-8"?>
//   <Scene version="1">
//     <Components>                                                          <!-- only the ones a script defined: the file defines them again when it is loaded -->
//       <Component name="Ticker"><Field name="count" kind="Int32"/><Field name="look" kind="AssetRef" assetType="model"/></Component>
//     </Components>
//     <Entity id="1" name="ground">                                          <!-- one element per component, one attribute per field; a child <Entity> is a child entity -->
//       <Transform Position="0 -0.5 0" Rotation="0 0 0 1" Scale="1 1 1"/>
//       <MainCamera/>
//       <Entity id="2" name="crate"><Ticker count="3" target="1"/></Entity>
//     </Entity>
//   </Scene>
//
// "id" is the entity's number in the file (what Entity-typed fields refer to, and what a patch matches on: DefDatabase.Merge pairs children by name and id), not its id in a world.
// An <Entity prefab="…"> is an instance of a prefab: it holds only what differs from the prefab (World.Prefabs.cs, Prefabs/ScenePlan.cs to load, Prefabs/PrefabDiff.cs to save), and a document that has
// one is version 2. docs/manual/content/scenes.md is the reference; Scene/scene.xsd describes the structure.
public sealed unsafe partial class World
{
    private const string SceneRoot = "Scene";
    private const string SceneEntity = "Entity";
    private const int SceneVersion = 1;           // what a scene without instances is written as
    private const int SceneVersionWithInstances = 2;   // a scene that holds an instance: an engine that does not know instances refuses it instead of loading them empty

    private readonly Dictionary<string, (Func<uint, AssetRef?> ToRef, Func<AssetRef, uint> FromRef)> _assetTypes = new(StringComparer.Ordinal);
    private readonly object _origin = new();   // what a snapshot says about the world it came from (see WorldSnapshot)
    private readonly HashSet<string> _assetTypesWarned = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sceneIgnored = new(StringComparer.Ordinal) { "WorldTransform" };

    /// <summary>
    /// Says how the fields of an asset type (<see cref="FieldDef.AssetType"/>, such as <c>model</c> for <c>ModelRenderer.Model</c>) are saved. Such a field is a number in memory, a handle that only means
    /// something in this run (a model's number in the library); a scene holds the <see cref="AssetRef"/> it names, the path and the UID, which is what lets it load in another run, and after the file moved.
    /// </summary>
    /// <param name="type">The asset type, as <c>[Property(Asset = "…")]</c> spells it.</param>
    /// <param name="toRef">The reference a handle names, or null when it names nothing that can be saved (a model made in code has no path).</param>
    /// <param name="fromRef">The handle of this run for a reference (loading what it names when needed), or 0 when there is none. When it throws, the failure is logged, the field stays 0 and the scene loads on.</param>
    public void RegisterAssetType(string type, Func<uint, AssetRef?> toRef, Func<AssetRef, uint> fromRef)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(toRef);
        ArgumentNullException.ThrowIfNull(fromRef);
        _assetTypes[type] = (toRef, fromRef);
    }

    /// <summary>
    /// The reference (path and UID) that the handle of an asset type names, through the converters of <see cref="RegisterAssetType"/>: what a scene would save for it, and what an editor shows.
    /// Null for handle 0, for a type nobody registered and for a handle that names nothing that can be saved (a model made in code has no path). It writes no warning.
    /// </summary>
    /// <param name="assetType">The asset type, as <c>[Property(Asset = "…")]</c> spells it (<see cref="FieldDef.AssetType"/>).</param>
    /// <param name="handle">The handle of this run, as stored in the field.</param>
    public AssetRef? AssetRefOf(string assetType, uint handle) =>
        handle != 0 && assetType is not null && _assetTypes.TryGetValue(assetType, out var registered) && registered.ToRef(handle) is { IsEmpty: false } named ? named : null;

    /// <summary>
    /// The handle of this run for a reference, through the converters of <see cref="RegisterAssetType"/>: loading what it names when the type needs to, as a scene does. 0 for an empty reference, for a type
    /// nobody registered, and for an asset that cannot be found or loaded (the last is logged as a warning, as in a scene).
    /// </summary>
    /// <param name="assetType">The asset type, as <c>[Property(Asset = "…")]</c> spells it (<see cref="FieldDef.AssetType"/>).</param>
    /// <param name="reference">What the handle should name.</param>
    public uint AssetHandleOf(string assetType, AssetRef reference)
    {
        if (reference.IsEmpty || assetType is null || !_assetTypes.TryGetValue(assetType, out var registered))
        {
            return 0;
        }

        try
        {
            return registered.FromRef(reference);
        }
        catch (Exception e) when (e is IOException or ContentException or FormatException or InvalidOperationException or ArgumentException)
        {
            Log.Warn("Scene", $"{reference} could not be loaded as a {assetType}: {e.Message}");
            return 0;
        }
    }

    /// <summary>The files scene files are read from and written to (<see cref="LoadSceneFile"/>, <see cref="SaveSceneFile(string)"/>); set by whatever hosts the world. Null in a bare world, which has no files.</summary>
    public VirtualFileSystem? Files { get; set; }

    /// <summary>Which file a <c>uid://</c> names (<see cref="LoadSceneFile"/> accepts one in place of a path). Null: only paths are understood.</summary>
    public AssetDatabase? Assets { get; set; }

    /// <summary>Leaves a component out of scenes: what is derived (<c>WorldTransform</c>, which is always left out) or only means something in this run (physics bodies).</summary>
    public void IgnoreInScenes(string component) => _sceneIgnored.Add(component);

    /// <summary>The user entities (<see cref="IsUserEntity"/>) as scene text (XML). It has no <c>uid</c>: text is a copy, and a copy must not carry the identity of a file.</summary>
    [Method]
    public string SaveScene() => SceneText(SaveSceneDocument(Roots()));

    /// <summary>One entity and everything below it as scene text: a prefab, a copy for the clipboard.</summary>
    public string SaveScene(Entity root) => SceneText(SaveSceneDocument([root]));

    private static string SceneText(XmlNode document) => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + document.ToXml();

    /// <summary>
    /// The entities in <paramref name="roots"/>, each with everything below it, as a <c>Scene</c> element: what <see cref="SaveScene()"/> writes as text, for code that wants the tree (to add
    /// a <c>uid</c> to, to patch with <c>DefDatabase.Merge</c>, to cook). An instance of a prefab is written as one element with only what differs from its prefab (<see cref="PrefabInstance"/>);
    /// a scene that holds one is version 2.
    /// </summary>
    public XmlNode SaveSceneDocument(IReadOnlyList<Entity> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        // The entity's number in the file is its place in the order the document is walked, parents before children, instances' own entities left out (they are named by their place in the instance).
        // A node is nested in the node of its parent, so the nesting follows the order and the order is what a loaded scene is created in: a saved scene saves again to the same text.
        int number = 0;
        SavePlan plan = PlanSave(roots, _ => ++number, default);
        (List<XmlNode> top, XmlNode? schemas) = WritePlanned(plan, roots);
        var scene = new XmlNode(SceneRoot);
        scene.SetAttribute("version", (plan.Instances.Count > 0 ? SceneVersionWithInstances : SceneVersion).ToString(CultureInfo.InvariantCulture));
        if (schemas is not null)
        {
            scene.Children.Add(schemas);
        }

        scene.Children.AddRange(top);
        return scene;
    }

    // ---- Saving: what is written, and where each entity goes -------------------------------------------------------------------------------

    // An instance found while planning: the prefab as the document names it, what that expands to with no overrides, and which live entity is which entity of the expansion.
    internal sealed class InstanceSave
    {
        public InstanceSave(string address, string reference, Expansion expansion, Dictionary<ulong, ExpandedEntity> pairs)
        {
            Address = address;
            Reference = reference;
            Expansion = expansion;
            Pairs = pairs;
            LiveOf = new Dictionary<ExpandedEntity, Entity>(pairs.Count, ReferenceEqualityComparer.Instance);
            foreach ((ulong id, ExpandedEntity expected) in pairs)
            {
                LiveOf[expected] = new Entity(id);
            }
        }

        public string Address { get; }

        public string Reference { get; }

        public Expansion Expansion { get; }

        /// <summary>The expected entity each live entity of the instance is, by entity id (the instance's root and every member that is still where its prefab puts it).</summary>
        public Dictionary<ulong, ExpandedEntity> Pairs { get; }

        public Dictionary<ExpandedEntity, Entity> LiveOf { get; }
    }

    // What walking the entities to write found out, before anything is written (a field may refer to an entity that comes later, so every address is known first).
    internal sealed class SavePlan(Func<Entity, int> number, Entity fileRoot, PrefabExpander expander)
    {
        /// <summary>The next number the document gives an entity it holds.</summary>
        public Func<Entity, int> Number { get; } = number;

        /// <summary>When saving a prefab file, its root: written as a plain entity even when it is itself an instance.</summary>
        public Entity FileRoot { get; } = fileRoot;

        public PrefabExpander Expander { get; } = expander;

        /// <summary>The address entity fields use for each entity written: its number, or its place in its instance.</summary>
        public Dictionary<ulong, string> AddressOf { get; } = [];

        /// <summary>The children of each entity written, as they were when planned.</summary>
        public Dictionary<ulong, List<Entity>> Kids { get; } = [];

        /// <summary>The instances to write as one element, by the entity id of their root.</summary>
        public Dictionary<ulong, InstanceSave> Instances { get; } = [];

        /// <summary>The entities the document numbers, in order.</summary>
        public List<Entity> Numbered { get; } = [];

        /// <summary>The handle of each prefab key met while pairing (asking the library looks for the file).</summary>
        public Dictionary<string, uint> Handles { get; } = new(StringComparer.Ordinal);

        /// <summary>For each instance root, the elements its instance element could not apply, copied and renumbered for this document (see <see cref="PrepareKept"/>).</summary>
        public Dictionary<ulong, List<XmlNode>> KeptNodes { get; } = [];

        /// <summary>The new number of each entity id the kept elements add (by the id they had), for the fields that name them.</summary>
        public Dictionary<string, string> KeptIds { get; } = new(StringComparer.Ordinal);
    }

    private uint HandleOfKey(SavePlan plan, string key)
    {
        if (!plan.Handles.TryGetValue(key, out uint handle))
        {
            plan.Handles[key] = handle = HandleOfKey(key);
        }

        return handle;
    }

    // Walks the entities of `roots` and everything below them in the order they are written. `number` gives the number of an entity the document holds itself; `fileRoot` is the one entity that is
    // never an instance (the root of a prefab file being saved).
    private SavePlan PlanSave(IReadOnlyList<Entity> roots, Func<Entity, int> number, Entity fileRoot)
    {
        var plan = new SavePlan(number, fileRoot, NewExpander(new ExpansionBudget(MaxLoadEntities)));
        PruneKept();
        foreach (Entity root in roots)
        {
            PlanEntity(plan, root);
        }

        PrepareKept(plan);
        return plan;
    }

    // An entity the document holds itself: a plain entity, an instance's root, or an entity added to an instance.
    private void PlanEntity(SavePlan plan, Entity entity)
    {
        string address = plan.Number(entity).ToString(CultureInfo.InvariantCulture);
        plan.AddressOf[entity.Id] = address;
        plan.Numbered.Add(entity);
        if (entity != plan.FileRoot && PlanInstance(plan, entity, address) is { } instance)
        {
            plan.Instances[entity.Id] = instance;
            PlanMembers(plan, instance, instance.Expansion.Root, isRoot: true);
            return;
        }

        List<Entity> kids = Children(entity);
        plan.Kids[entity.Id] = kids;
        foreach (Entity child in kids)
        {
            PlanEntity(plan, child);
        }
    }

    // The entities of an instance, in the order the instance element is written (see WriteInstance): each member of the prefab that is still there has its place in the instance for an address, and the
    // entities added below it, which the document holds, are numbered when its <Override> is reached; the ones added to the root come last. That order is the one a loaded scene will have its
    // entities made in, so the first save of a world whose entity ids were reused (they are not in creation order) writes the numbers a second save does.
    private void PlanMembers(SavePlan plan, InstanceSave instance, ExpandedEntity expected, bool isRoot)
    {
        Entity live = instance.LiveOf[expected];
        foreach (ExpandedEntity member in expected.Children.Concat(expected.Added))
        {
            if (!instance.LiveOf.TryGetValue(member, out Entity liveMember))
            {
                continue;
            }

            plan.AddressOf[liveMember.Id] = member.Address;
            if (PrefabDiff.RelativeAddress(instance.Address, member) is not null)
            {
                foreach (Entity added in AddedBelow(plan, instance, liveMember))
                {
                    PlanEntity(plan, added);
                }
            }

            PlanMembers(plan, instance, member, isRoot: false);
        }

        if (isRoot)
        {
            foreach (Entity added in AddedBelow(plan, instance, live))
            {
                PlanEntity(plan, added);
            }
        }
    }

    // The instance an entity is the root of, paired with what its prefab expands to now; null when it is not an instance or its prefab cannot be named (it is then written as a plain entity).
    private InstanceSave? PlanInstance(SavePlan plan, Entity entity, string address)
    {
        if (!TryGet(entity, out PrefabInstance link))
        {
            return null;
        }

        string reference;
        if (link.Prefab == 0 && _kept.TryGetValue(entity.Id, out KeptData? kept) && kept.RawPrefab is { } raw)
        {
            reference = raw; // an instance whose prefab attribute was not a reference at all: written back as it was
        }
        else if (ExistingLibrary?.RefOf(link.Prefab) is { IsEmpty: false } named)
        {
            reference = named.ToString();
        }
        else
        {
            if (_assetTypesWarned.Add($"instance:{link.Prefab}"))
            {
                Log.Warn("Scene", $"an entity is the root of an instance of the prefab with handle {link.Prefab}, which this world's library does not know: it is saved as a plain entity");
            }

            return null;
        }

        var element = new XmlNode("Entity");
        element.SetAttribute("prefab", reference);

        // Each instance has the budget an Instantiate or a load had for it: a world made of many large instances can be saved, and a prefab that grew since cannot make one expansion unbounded.
        plan.Expander.UseBudget(new ExpansionBudget(MaxLoadEntities));
        Expansion expansion = plan.Expander.Expand(element, address);
        var pairs = new Dictionary<ulong, ExpandedEntity>();
        Pair(plan, entity, expansion.Root, pairs);
        return new InstanceSave(address, reference, expansion, pairs);
    }

    // Pairs the live entity with the expected one, then their children: a live child is the expected child with the same prefab member tag (id and file), when it is the same kind of entity (an instance
    // of the same prefab, or neither). Each expected child is taken once, so a copy of a member is not paired. Whatever is not paired is an addition (live) or a removal (expected); a member moved under
    // another parent by code is both, which is how it is written.
    private void Pair(SavePlan plan, Entity live, ExpandedEntity expected, Dictionary<ulong, ExpandedEntity> pairs)
    {
        pairs[live.Id] = expected;
        List<Entity> kids = Children(live);
        plan.Kids[live.Id] = kids;
        if (expected.Children.Count + expected.Added.Count == 0)
        {
            return;
        }

        var byTag = new Dictionary<(uint Prefab, int Id), ExpandedEntity>();
        foreach (ExpandedEntity member in expected.Children)
        {
            AddByTag(plan, byTag, member);
        }

        foreach (ExpandedEntity member in expected.Added)
        {
            AddByTag(plan, byTag, member);
        }

        foreach (Entity child in kids)
        {
            if (TryGet(child, out PrefabMember tag)
                && tag.Id <= int.MaxValue
                && byTag.Remove((tag.Prefab, (int)tag.Id), out ExpandedEntity? match)
                && IsSameKind(plan, child, match))
            {
                Pair(plan, child, match, pairs);
            }
        }
    }

    private void AddByTag(SavePlan plan, Dictionary<(uint Prefab, int Id), ExpandedEntity> byTag, ExpandedEntity member)
    {
        if (member.MemberId is { } id && member.MemberPrefab is { } key)
        {
            byTag.TryAdd((HandleOfKey(plan, key), id), member);
        }
    }

    // Whether a live entity is the kind of entity the expansion has there: the root of an instance of the same prefab, or not the root of one.
    private bool IsSameKind(SavePlan plan, Entity live, ExpandedEntity expected)
    {
        bool isInstance = TryGet(live, out PrefabInstance link);
        return isInstance == expected.IsInstanceRoot && (!isInstance || link.Prefab == HandleOfKey(plan, expected.InstancePrefab!));
    }

    // The nodes of the plan, written. A plain entity is its components and then its children; an instance is WriteInstance.
    private (List<XmlNode> Top, XmlNode? Schemas) WritePlanned(SavePlan plan, IReadOnlyList<Entity> roots)
    {
        var scripts = new SortedDictionary<string, ComponentInfo>(StringComparer.Ordinal);
        var top = new List<XmlNode>(roots.Count);
        foreach (Entity root in roots)
        {
            top.Add(WriteNumbered(plan, root, scripts));
        }

        return (top, SchemasOf(scripts));
    }

    private XmlNode WriteNumbered(SavePlan plan, Entity entity, SortedDictionary<string, ComponentInfo> scripts)
    {
        if (plan.Instances.TryGetValue(entity.Id, out InstanceSave? instance))
        {
            return WriteInstance(plan, entity, instance, scripts);
        }

        XmlNode node = StartElement(plan, entity);

        // In the order of their names: an entity's components are in the order of their ids in this world, which another world numbers in the order it met them, so the same scene would be written in
        // another order by the world that loaded it.
        foreach ((Entity id, ComponentInfo info) in ComponentsOf(entity).OrderBy(static c => c.Info.Name, StringComparer.Ordinal))
        {
            if (IsSceneSkipped(info.Name))
            {
                continue;
            }

            if (info.ClrType is null)
            {
                scripts[info.Name] = info;
            }

            node.Children.Add(WriteComponent(entity, id, info, plan));
        }

        foreach (Entity child in plan.Kids[entity.Id])
        {
            node.Children.Add(WriteNumbered(plan, child, scripts));
        }

        return node;
    }

    private XmlNode StartElement(SavePlan plan, Entity entity)
    {
        var node = new XmlNode(SceneEntity);
        node.SetAttribute("id", plan.AddressOf[entity.Id]);
        if (GetName(entity) is { } name)
        {
            node.SetAttribute("name", name);
        }

        return node;
    }

    // An instance as one element, in the order that makes saving twice write the same text and loading it give the world that was saved: what could not be applied when it was loaded, the patches of the
    // root, an <Override> for each entity of the prefab that changed (in the prefab's order, parents first, a removed entity at its place), and the entities added directly to the root.
    private XmlNode WriteInstance(SavePlan plan, Entity root, InstanceSave instance, SortedDictionary<string, ComponentInfo> scripts)
    {
        XmlNode node = StartElement(plan, root);
        node.SetAttribute("prefab", instance.Reference);

        // What could not be applied comes first: an instance element is applied in the order it is written, so what the live entities say (everything after) is applied last and wins over an old element that
        // has become applicable since (a patch of a component the entity lacked when the scene was loaded, and has now).
        if (plan.KeptNodes.TryGetValue(root.Id, out List<XmlNode>? kept))
        {
            node.Children.AddRange(kept);
        }

        var diff = new PrefabDiff(this, plan, instance.Expansion, instance.Address, scripts);
        node.Children.AddRange(diff.ComponentPatches(root, instance.Expansion.Root));
        WriteOverrides(plan, instance, diff, instance.Expansion.Root, node, scripts);
        foreach (Entity added in AddedBelow(plan, instance, root))
        {
            node.Children.Add(WriteNumbered(plan, added, scripts));
        }

        return node;
    }

    // The children of a live entity of an instance that the prefab does not put there.
    private static IEnumerable<Entity> AddedBelow(SavePlan plan, InstanceSave instance, Entity live)
    {
        foreach (Entity child in plan.Kids[live.Id])
        {
            if (!instance.Pairs.ContainsKey(child.Id))
            {
                yield return child;
            }
        }
    }

    // The <Override> elements for the entities below `expected`, depth first. An entity the live instance no longer has is a removal (and what was below it goes with it); one that has no address
    // an element can name (a prefab entity with no usable id) cannot be changed from outside and is left as it is.
    private void WriteOverrides(SavePlan plan, InstanceSave instance, PrefabDiff diff, ExpandedEntity expected, XmlNode into, SortedDictionary<string, ComponentInfo> scripts)
    {
        foreach (ExpandedEntity member in expected.Children.Concat(expected.Added))
        {
            string? relative = diff.RelativeAddress(member);
            if (!instance.LiveOf.TryGetValue(member, out Entity live))
            {
                if (relative is not null)
                {
                    var removal = new XmlNode("Override");
                    removal.SetAttribute("entity", relative);
                    removal.SetAttribute("remove", "true");
                    into.Children.Add(removal);
                }

                continue;
            }

            if (relative is not null)
            {
                var change = new XmlNode("Override");
                change.SetAttribute("entity", relative);
                if (diff.NameChange(live, member) is { } name)
                {
                    change.SetAttribute("name", name);
                }

                change.Children.AddRange(diff.ComponentPatches(live, member));
                foreach (Entity added in AddedBelow(plan, instance, live))
                {
                    change.Children.Add(WriteNumbered(plan, added, scripts));
                }

                if (change.Attributes.Count > 1 || change.Children.Count > 0)
                {
                    into.Children.Add(change);
                }
            }

            WriteOverrides(plan, instance, diff, member, into, scripts);
        }
    }

    // One component of an entity as an element: a tag is empty, otherwise one attribute per field (a field with nothing to say is left out).
    internal XmlNode WriteComponent(Entity entity, Entity id, ComponentInfo info, SavePlan plan)
    {
        var component = new XmlNode(info.Name);
        if (!info.IsTag)
        {
            byte* data = GetPointer(entity, id);
            foreach (FieldDef field in info.Fields)
            {
                if (FieldTextOf(entity, info, field, data + field.Offset, plan) is { } text)
                {
                    component.SetAttribute(field.Name, text);
                }
            }
        }

        return component;
    }

    // The <Components> element for the script-defined components that were written, in name order; null when there are none.
    private static XmlNode? SchemasOf(SortedDictionary<string, ComponentInfo> scripts)
    {
        if (scripts.Count == 0)
        {
            return null;
        }

        var schemas = new XmlNode("Components");
        foreach ((string name, ComponentInfo info) in scripts)
        {
            var schema = new XmlNode("Component");
            schema.SetAttribute("name", name);
            foreach (FieldDef field in info.Fields)
            {
                var entry = new XmlNode("Field");
                entry.SetAttribute("name", field.Name);
                entry.SetAttribute("kind", field.Kind.ToString());
                if (field.Kind == FieldKind.AssetRef && field.AssetType is not null)
                {
                    entry.SetAttribute("assetType", field.AssetType);
                }

                schema.Children.Add(entry);
            }

            schemas.Children.Add(schema);
        }

        return schemas;
    }

    // Whether a document never holds this component: derived or only meaningful in this run (IgnoreInScenes), and the two that link an entity to its prefab, which the engine makes.
    internal bool IsSceneSkipped(string component) => _sceneIgnored.Contains(component) || component is nameof(PrefabInstance) or nameof(PrefabMember);

    // The text of a field, or null when the attribute is left out: an Entity outside what is written, a handle of 0, an asset the type cannot name. A field that is 0 because what it named could not be made
    // (an entity of an instance whose prefab is missing, an asset that did not load) is written as what it named, so that it names it again when it can.
    private string? FieldTextOf(Entity entity, ComponentInfo info, FieldDef field, byte* at, SavePlan plan)
    {
        switch (field.Kind)
        {
            case FieldKind.Entity:
            {
                // A reference to another entity of the document is its address; to anything else, nothing.
                ulong target = *(ulong*)at;
                return plan.AddressOf.TryGetValue(target, out string? address) ? address : target == 0 ? DanglingText(entity, info.Name, field.Name, plan) : null;
            }

            case FieldKind.AssetRef:
            {
                // The handle means nothing in another run: what it names goes instead, as "path|uid".
                uint handle = *(uint*)at;
                return AssetRefOf(info, field, handle)?.ToString() ?? (handle == 0 ? UnresolvedAsset(entity, info.Name, field.Name) : null);
            }

            default:
                return FieldText.Format(field, at);
        }
    }

    // The reference text of an asset field that is 0 because the asset did not load; null when the field is just empty.
    internal string? UnresolvedAsset(Entity entity, string component, string field) =>
        _kept.TryGetValue(entity.Id, out KeptData? kept) && kept.Assets is { } assets && assets.TryGetValue((component, field), out string? text) ? text : null;

    // The address an entity field that names no entity was written as, through the numbers of the document being written; null when what it named is not in it (anymore).
    internal string? DanglingText(Entity entity, string component, string field, SavePlan plan)
    {
        if (!_kept.TryGetValue(entity.Id, out KeptData? kept) || kept.Refs is not { } refs || !refs.TryGetValue((component, field), out DanglingRef? named))
        {
            return null;
        }

        if (named.Root != 0)
        {
            return plan.AddressOf.TryGetValue(named.Root, out string? root) ? root + "/" + named.Text : null;
        }

        return KeptAddress(plan, named.Text);
    }

    // An address that starts with the number of an entity a kept element adds (`7`, or `7/2` inside an instance it adds), for this document: the new number of that entity, and what follows it as it was.
    // Null when no kept element adds that entity.
    private static string? KeptAddress(SavePlan plan, string text)
    {
        int slash = text.IndexOf('/', StringComparison.Ordinal);
        if (!plan.KeptIds.TryGetValue(slash < 0 ? text : text[..slash], out string? number))
        {
            return null;
        }

        return slash < 0 ? number : number + text[slash..];
    }

    // ---- Saving: what an instance element could not apply

    // Forgets what is kept for entities that are gone (a destroyed branch, a scene closed): the ids carry their generation, so nothing else would ever look at it.
    private void PruneKept()
    {
        if (_kept.Count == 0)
        {
            return;
        }

        foreach (ulong id in new List<ulong>(_kept.Keys))
        {
            if (!IsAlive(new Entity(id)))
            {
                _kept.Remove(id);
            }
        }
    }

    // The elements each instance of the plan could not apply, copied for this document: the entities they add are numbered after everything the document has (they are not made, so they only need
    // numbers nothing else has), and the entity fields in them are written as what they named, through the numbers of this save. Without this a kept element would keep the numbering of the save that
    // kept it, while the scene numbers its entities again at every save, and could name another entity when it applies.
    private void PrepareKept(SavePlan plan)
    {
        foreach (ulong rootId in plan.Instances.Keys)
        {
            if (_kept.TryGetValue(rootId, out KeptData? kept) && kept.Unapplied.Count > 0)
            {
                plan.KeptNodes[rootId] = [.. kept.Unapplied.Select(static node => node.Clone())];
            }
        }

        foreach (List<XmlNode> nodes in plan.KeptNodes.Values)
        {
            foreach (XmlNode node in nodes)
            {
                RenumberKept(plan, node);
            }
        }

        foreach (List<XmlNode> nodes in plan.KeptNodes.Values)
        {
            foreach (XmlNode node in nodes)
            {
                RewriteKept(plan, node);
            }
        }
    }

    private static void RenumberKept(SavePlan plan, XmlNode node)
    {
        if (node.Name is not ("Entity" or "Override"))
        {
            return;
        }

        if (node.Name == "Entity" && node.Attribute("id") is { } old)
        {
            string fresh = plan.Number(default).ToString(CultureInfo.InvariantCulture);
            plan.KeptIds.TryAdd(old, fresh);
            node.SetAttribute("id", fresh);
        }

        foreach (XmlNode child in node.Children)
        {
            RenumberKept(plan, child);
        }
    }

    private void RewriteKept(SavePlan plan, XmlNode node)
    {
        if (node.Name is "Entity" or "Override")
        {
            foreach (XmlNode child in node.Children)
            {
                RewriteKept(plan, child);
            }

            return;
        }

        for (int i = 0; i < node.Attributes.Count; i++)
        {
            XmlProperty attribute = node.Attributes[i];
            if (attribute.Value.Length > 0 && IsEntityFieldOf(node.Name, attribute.Name))
            {
                node.Attributes[i] = attribute with { Value = KeptText(plan, attribute.Value) };
            }
        }
    }

    // An entity field of a kept element for this document: the entity it named when it was loaded (by its new address, and what followed it), or the new number of an entity the kept elements add;
    // empty when it names nothing that is in the document.
    private static string KeptText(SavePlan plan, string text)
    {
        if (text[0] != KeptData.Token)
        {
            return KeptAddress(plan, text) ?? "";
        }

        int slash = text.IndexOf('/', StringComparison.Ordinal);
        ReadOnlySpan<char> id = slash < 0 ? text.AsSpan(1) : text.AsSpan(1, slash - 1);
        if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong entity) || !plan.AddressOf.TryGetValue(entity, out string? address))
        {
            return "";
        }

        return slash < 0 ? address : address + text[slash..];
    }

    // What the handle of an AssetRef field names; null for 0, for a type nobody registered (said once) and for what the type cannot name.
    private AssetRef? AssetRefOf(ComponentInfo info, FieldDef field, uint handle)
    {
        if (handle == 0)
        {
            return null;
        }

        if (field.AssetType is not { } type || !_assetTypes.TryGetValue(type, out var registered))
        {
            WarnAssetTypeOnce(info, field);
            return null;
        }

        return registered.ToRef(handle) is { IsEmpty: false } named ? named : null;
    }

    // The same without the warning, for what is not saved (a snapshot): null for a type nobody registered and for what the type cannot name.
    internal AssetRef? QuietAssetRefOf(FieldDef field, uint handle) =>
        field.AssetType is { } type && _assetTypes.TryGetValue(type, out var registered) && registered.ToRef(handle) is { IsEmpty: false } named ? named : null;

    // The handle of this run for the text of a reference; 0 for no text, for a type nobody registered (said once), and for an asset that cannot be loaded (logged).
    private uint HandleOf(ComponentInfo info, FieldDef field, string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        if (field.AssetType is not { } type || !_assetTypes.TryGetValue(type, out var registered))
        {
            WarnAssetTypeOnce(info, field);
            return 0;
        }

        if (!AssetRef.TryParse(text, out AssetRef named))
        {
            throw new FormatException($"'{text}' is not an asset reference (a path, path|uid://… or uid://…)");
        }

        return Resolve(info, field, registered.FromRef, named);
    }

    // The handle of this run for a reference that was already read: what a snapshot of another world holds. 0 for a type nobody registered (said once) and for what cannot be found or loaded (logged).
    private uint HandleOfRef(ComponentInfo info, FieldDef field, AssetRef named)
    {
        if (field.AssetType is not { } type || !_assetTypes.TryGetValue(type, out var registered))
        {
            WarnAssetTypeOnce(info, field);
            return 0;
        }

        return Resolve(info, field, registered.FromRef, named);
    }

    private static uint Resolve(ComponentInfo info, FieldDef field, Func<AssetRef, uint> fromRef, AssetRef named)
    {
        try
        {
            uint handle = fromRef(named);
            if (handle == 0)
            {
                Log.Warn("Scene", $"{info.Name}.{field.Name}: {named} names no {field.AssetType} that this world can find: left empty");
            }

            return handle;
        }
        catch (Exception e) when (e is IOException or ContentException or FormatException or InvalidOperationException or ArgumentException)
        {
            Log.Warn("Scene", $"{info.Name}.{field.Name}: {named} could not be loaded: {e.Message}");
            return 0;
        }
    }

    private void WarnAssetTypeOnce(ComponentInfo info, FieldDef field)
    {
        string key = $"{info.Name}.{field.Name}";
        if (_assetTypesWarned.Add(key))
        {
            Log.Warn("Scene", field.AssetType is null
                ? $"{key} is an asset field with no asset type: scenes cannot save what it names"
                : $"{key} has the asset type '{field.AssetType}', which this world has no RegisterAssetType for: scenes cannot save what it names");
        }
    }

    /// <summary>
    /// Creates the entities of scene text, under <paramref name="parent"/> when given, and returns them in the order of the file (parents first). A component the file names that this world cannot
    /// find, and a field it does not have, are logged and skipped; components that scripts defined are defined again from the file. Throws <see cref="FormatException"/> when the text is not a scene.
    /// An <c>&lt;Entity prefab="…"&gt;</c> is an instance of a prefab: its entities are made from the prefab file with the element's changes applied (<see cref="PrefabInstance"/>); one the world cannot read is left empty and its element kept for saving. The whole load is checked against <see cref="MaxLoadEntities"/> before any entity is made.
    /// </summary>
    [Method]
    public List<Entity> LoadScene(string text, Entity parent = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        XmlNode root;
        try
        {
            root = XmlNode.Parse(text, "scene");
        }
        catch (XmlParseException e)
        {
            // XmlNode reports a ContentException; a scene text that cannot be read is a FormatException, as it always was, for the callers that tell the two apart.
            throw new FormatException($"the scene is not valid XML: {e.Message}", e);
        }

        return LoadScene(root, parent);
    }

    /// <summary>
    /// Creates the entities of a <c>Scene</c> element (<see cref="SaveSceneDocument"/>, or a file read with <see cref="XmlNode.Parse"/>, possibly patched), under <paramref name="parent"/> when given.
    /// See <see cref="LoadScene(string, Entity)"/>.
    /// </summary>
    public List<Entity> LoadScene(XmlNode root, Entity parent = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Name != SceneRoot)
        {
            throw new FormatException($"the document is not a scene: its root is <{root.Name}>, not <{SceneRoot}>");
        }

        if (root.Attribute("version") is { } versionText)
        {
            if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            {
                throw new FormatException($"the scene has the version '{versionText}', which is not a number");
            }

            if (version > SceneVersionWithInstances)
            {
                throw new FormatException($"the scene is version {version}; this engine reads up to {SceneVersionWithInstances}");
            }
        }

        return Load(root, parent);
    }

    // Plans the document (see ScenePlan: plain entities and instances, expanded, against one budget), then makes what the plan says. A document that is too big or too nested fails in the plan, before
    // any entity is made.
    private List<Entity> Load(XmlNode document, Entity parent)
    {
        var budget = new ExpansionBudget(MaxLoadEntities);
        return Create(ScenePlan.Build(document, NewExpander(budget)), parent);
    }

    // Makes the entities of a plan in its order and returns them. Every entity is made first so that entity fields can refer to the ones that come later; the parents next (they decide whether a name is
    // free), then the names and components, and last the links to prefabs.
    private List<Entity> Create(ScenePlan plan, Entity parent)
    {
        foreach (XmlNode schemas in plan.Schemas)
        {
            DefineSceneComponents(schemas);
        }

        List<PlannedEntity> items = plan.Items;
        var created = new List<Entity>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            created.Add(CreateEntity());
        }

        for (int i = 0; i < created.Count; i++)
        {
            Entity under = items[i].Parent >= 0 ? created[items[i].Parent] : parent;
            if (!under.IsNull)
            {
                SetParent(created[i], under);
            }
        }

        var byAddress = new Dictionary<string, Entity>(plan.Addresses.Count, StringComparer.Ordinal);
        foreach ((string address, int at) in plan.Addresses)
        {
            byAddress[address] = created[at];
        }

        PruneKept();
        var handles = new Dictionary<string, uint>(StringComparer.Ordinal);
        var notes = new LoadNotes();
        var nameHints = new NameHints();
        for (int i = 0; i < created.Count; i++)
        {
            Entity entity = created[i];
            PlannedEntity item = items[i];
            if (item.Name is { Length: > 0 } entityName)
            {
                TrySetName(entity, entityName, nameHints);
            }

            notes.Clear();
            foreach (XmlNode component in item.Components)
            {
                ReadComponent(entity, component, byAddress, notes);
            }

            LinkToPrefabs(entity, item, handles, byAddress, notes);
        }

        return created;
    }

    // What ties an entity to the prefabs it came from: its id in the prefab file that holds it, the instance it is the root of, and what its instance element could not apply (kept as written, as copies:
    // the nodes belong to documents the library shares).
    private void LinkToPrefabs(Entity entity, PlannedEntity item, Dictionary<string, uint> handles, Dictionary<string, Entity> byAddress, LoadNotes notes)
    {
        uint HandleOf(string key)
        {
            if (!handles.TryGetValue(key, out uint handle))
            {
                handles[key] = handle = HandleOfKey(key);
            }

            return handle;
        }

        if (item.MemberId is { } id && item.MemberPrefab is { } file)
        {
            Set(entity, new PrefabMember { Id = (uint)id, Prefab = HandleOf(file) });
        }

        if (item.InstancePrefab is { } prefab)
        {
            uint handle = HandleOf(prefab);
            Set(entity, new PrefabInstance { Prefab = handle });

            // The root of an instance that is not inside another prefab's file is the root entity of its own prefab, the one that file calls by its id (what saving the prefab keeps).
            if (item.MemberId is null && item.InstanceRootId > 0)
            {
                Set(entity, new PrefabMember { Id = (uint)item.InstanceRootId, Prefab = handle });
            }
        }

        // What the document said that the entity cannot show (see KeptData). The elements are copies, with the entity fields in them tied to the entities they named, so that they follow them.
        List<XmlNode>? unapplied = null;
        if (item.Unapplied is { Count: > 0 } held)
        {
            unapplied = [.. held.Select(static node => node.Clone())];
            foreach (XmlNode node in unapplied)
            {
                TieToEntities(node, byAddress);
            }
        }

        if (unapplied is not null || item.RawPrefab is not null || notes.Assets is not null || notes.Refs is not null)
        {
            _kept[entity.Id] = new KeptData { Unapplied = unapplied ?? [], RawPrefab = item.RawPrefab, Assets = notes.Assets, Refs = notes.Refs };
        }
    }

    /// <summary>What reading the components of one entity found that its fields cannot hold (see <see cref="KeptData"/>).</summary>
    private sealed class LoadNotes
    {
        public Dictionary<(string, string), string>? Assets { get; private set; }

        public Dictionary<(string, string), DanglingRef>? Refs { get; private set; }

        public void Clear()
        {
            Assets = null;
            Refs = null;
        }

        public void Asset(string component, string field, string text) => (Assets ??= [])[(component, field)] = text;

        public void Ref(string component, string field, DanglingRef named) => (Refs ??= [])[(component, field)] = named;
    }

    // Rewrites the entity fields of a kept element as the entities they name (KeptData.Token), so that they are written as those entities' addresses whatever the scene is numbered later.
    private void TieToEntities(XmlNode node, Dictionary<string, Entity> byAddress)
    {
        if (node.Name is "Entity" or "Override")
        {
            foreach (XmlNode child in node.Children)
            {
                TieToEntities(child, byAddress);
            }

            return;
        }

        for (int i = 0; i < node.Attributes.Count; i++)
        {
            XmlProperty attribute = node.Attributes[i];
            if (EntityAddress.IsValid(attribute.Value) && IsEntityFieldOf(node.Name, attribute.Name) && NameOf(attribute.Value, byAddress) is { } named)
            {
                node.Attributes[i] = attribute with { Value = KeptData.Token + named.Root.ToString(CultureInfo.InvariantCulture) + (named.Text.Length > 0 ? "/" + named.Text : "") };
            }
        }
    }

    // The entity an address names in the document being loaded, or the instance root it is below (an instance that is missing, or a part of one that is not there): the entity (text empty) or the entity and
    // what follows its address. Null when nothing it starts with is an entity.
    private static DanglingRef? NameOf(string address, Dictionary<string, Entity> byAddress)
    {
        if (byAddress.TryGetValue(address, out Entity found))
        {
            return new DanglingRef(found.Id, "");
        }

        for (int slash = address.LastIndexOf('/'); slash > 0; slash = address.LastIndexOf('/', slash - 1))
        {
            if (byAddress.TryGetValue(address[..slash], out Entity parent))
            {
                return new DanglingRef(parent.Id, address[(slash + 1)..]);
            }
        }

        return null;
    }

    /// <summary>
    /// Creates the entities of the scene file at <paramref name="path"/> (a path, or <c>uid://…</c> for the file that has that UID, wherever it moved to), under <paramref name="parent"/> when given.
    /// The file is read as <see cref="XmlFile"/> reads it, so a scene cooked into a pack loads like the text one. From a script this reads without the per-mod confinement of <c>fs.read_text</c>,
    /// as <c>models.load</c> and <c>ui:load_document</c> do. See <see cref="LoadScene(string, Entity)"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The world has no <see cref="Files"/>.</exception>
    /// <exception cref="ContentException">The path is a <c>uid://</c> that no file has.</exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="FormatException">The file is not a scene.</exception>
    [Method]
    public List<Entity> LoadSceneFile(string path, Entity parent = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        VirtualFileSystem files = Files ?? throw new InvalidOperationException("this world has no files to load a scene from");
        string resolved = ResolveScenePath(path);
        XmlNode root;
        try
        {
            root = XmlFile.Read(files, resolved);
        }
        catch (ContentException e)
        {
            throw new FormatException($"the scene {resolved} cannot be read: {e.Message}", e);
        }

        return LoadScene(root, parent);
    }

    /// <summary>
    /// Writes every user entity as the scene file at <paramref name="path"/>. The file keeps its UID: when one is already there its <c>uid</c> is written again, and a new file gets a new one,
    /// so saving over a scene never changes what references to it mean (<see cref="SaveScene()"/> makes text with no UID, for copies).
    /// </summary>
    /// <exception cref="InvalidOperationException">The world has no <see cref="Files"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">The path is not in a writable source.</exception>
    public void SaveSceneFile(string path) => WriteSceneFile(path, SaveSceneDocument(Roots()));

    /// <summary>One entity and everything below it as the scene file at <paramref name="path"/>. See <see cref="SaveSceneFile(string)"/>.</summary>
    public void SaveSceneFile(string path, Entity root) => WriteSceneFile(path, SaveSceneDocument([root]));

    private void WriteSceneFile(string path, XmlNode document)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        VirtualFileSystem files = Files ?? throw new InvalidOperationException("this world has no files to save a scene to");
        string resolved = ResolveScenePath(path);

        AssetUid uid = KeptUid(files, resolved, out bool minted);
        document.SetAttribute("uid", uid.ToString());
        files.WriteAllText(resolved, SceneText(document));
        if (minted)
        {
            Assets?.Refresh(); // the database remembers what it found: this file has a UID now
        }
    }

    // The UID of the document already at the path, or a new one (minted) when there is none.
    private static AssetUid KeptUid(VirtualFileSystem files, string resolved, out bool minted)
    {
        AssetUid uid = files.FileExists(resolved) ? AuthoredAssets.ReadUid(files.ReadAllBytes(resolved)) : AssetUid.None;
        minted = uid.IsNone;
        return minted ? AssetUid.New() : uid;
    }

    private string ResolveScenePath(string path) => Assets is { } assets ? assets.ResolvePath(path) : path;

    // The name of a loaded entity: a free one when a sibling has it (one of the world's, when the scene is loaded into a populated one, or one named before it in the same scene).
    private void TrySetName(Entity entity, string name, NameHints hints) => SetName(entity, UniqueName(name, GetParent(entity), entity, hints));

    private void DefineSceneComponents(XmlNode schemas)
    {
        foreach (XmlNode schema in schemas.Children)
        {
            if (schema.Name != "Component" || schema.Attribute("name") is not { Length: > 0 } name)
            {
                Log.Warn("Scene", $"{schema.Origin}:{schema.Line}: <{schema.Name}> in <Components> is not a <Component name=\"…\">: skipped");
                continue;
            }

            if (!_componentsByName.ContainsKey(name) && TypeRegistry.FindComponent(name) is not null)
            {
                continue; // a struct of the engine or the game: its layout is the code's
            }

            var fields = new List<(string Name, FieldKind Kind)>();
            Dictionary<string, string>? assetTypes = null;
            foreach (XmlNode field in schema.Children)
            {
                string? fieldName = field.Attribute("name");
                if (field.Name != "Field" || fieldName is null || !Enum.TryParse(field.Attribute("kind"), out FieldKind kind) || !Enum.IsDefined(kind))
                {
                    Log.Warn("Scene", $"{field.Origin}:{field.Line}: component {name} has a field the engine does not know: skipped");
                    continue;
                }

                fields.Add((fieldName, kind));
                if (kind == FieldKind.AssetRef && field.Attribute("assetType") is { Length: > 0 } assetType)
                {
                    (assetTypes ??= new Dictionary<string, string>(StringComparer.Ordinal))[fieldName] = assetType;
                }
            }

            try
            {
                DefineComponent(name, fields, assetTypes: assetTypes);
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
            {
                Log.Warn("Scene", $"{schema.Origin}:{schema.Line}: component {name} in the scene does not match the one in this world: {e.Message}");
            }
        }
    }

    private void ReadComponent(Entity entity, XmlNode node, Dictionary<string, Entity> byAddress, LoadNotes notes)
    {
        string name = node.Name;
        if (name is nameof(PrefabInstance) or nameof(PrefabMember))
        {
            Log.Warn("Scene", $"{node.Origin}:{node.Line}: {name} is made by the engine, not read from a file: skipped");
            return;
        }

        Entity id = FindComponent(name);
        if (id.IsNull && TypeRegistry.FindComponent(name) is { } known)
        {
            id = RegisterComponent(known);
        }

        if (id.IsNull || !_infoById.TryGetValue(id, out ComponentInfo? info))
        {
            Log.Warn("Scene", $"{node.Origin}:{node.Line}: the scene has a component '{name}' that this world does not have: skipped");
            return;
        }

        if (info.IsTag)
        {
            Add(entity, id);
            return;
        }

        // A field the element does not name stays zero.
        byte[] data = new byte[info.Size];
        fixed (byte* bytes = data)
        {
            foreach (XmlProperty attribute in node.Attributes)
            {
                FieldDef? def = info.FindField(attribute.Name);
                if (def is null)
                {
                    Log.Warn("Scene", $"{node.Origin}:{node.Line}: component {name} has no field '{attribute.Name}': skipped");
                    continue;
                }

                try
                {
                    ReadField(info, def, attribute.Value, bytes + def.Offset, byAddress, notes);
                }
                catch (Exception e) when (e is FormatException or InvalidOperationException)
                {
                    Log.Warn("Scene", $"{node.Origin}:{node.Line}: {name}.{attribute.Name} in the scene is not a {def.Kind}: {e.Message}");
                }
            }

            SetPointer(entity, id, info.Size, bytes);
        }
    }

    private void ReadField(ComponentInfo info, FieldDef field, string text, byte* at, Dictionary<string, Entity> byAddress, LoadNotes notes)
    {
        switch (field.Kind)
        {
            case FieldKind.Entity:
            {
                // The address of an entity of the document (its number, or its place in an instance); text that is no address, and one that names nothing, is no reference (the same as the attribute left out).
                ulong target = 0;
                if (EntityAddress.IsValid(text))
                {
                    if (byAddress.TryGetValue(text, out Entity found))
                    {
                        target = found.Id;
                    }
                    else
                    {
                        // Nothing there: an instance that is missing, or a part of one that is not, or an entity a kept element would add. What it named is kept, so that it names it when it can.
                        notes.Ref(info.Name, field.Name, NameOf(text, byAddress) ?? new DanglingRef(0, text));
                    }
                }
                else if (text.Length > 0 && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    // Older engines read any whole number as an id: 0, a sign or a leading zero is not an address now.
                    Log.Warn("Scene", $"{info.Name}.{field.Name} holds '{text}', a number that is not an entity id (ids are whole numbers from 1 up, without a sign or leading zeros): it names no entity");
                }

                *(ulong*)at = target;
                break;
            }

            case FieldKind.AssetRef:
            {
                // A path alone is a reference too, which is how a scene written before UIDs holds a model. One that cannot be loaded leaves the field 0, and is kept as it was written.
                uint handle = 0;
                try
                {
                    handle = HandleOf(info, field, text);
                }
                finally
                {
                    *(uint*)at = handle;
                    if (handle == 0 && text.Length > 0)
                    {
                        notes.Asset(info.Name, field.Name, text);
                    }
                }

                break;
            }

            default:
                FieldText.Parse(field, text, at);
                break;
        }
    }
}
