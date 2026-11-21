using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Apogee.Content.Xml;
using Apogee.Core.Logging;
using Apogee.Reflection;

namespace Apogee.Ecs;

// Instances follow their prefab (docs/manual/content/prefabs.md, "When the prefab changes").
//
// When a prefab file changes (PrefabLibrary.Reload for a file edited by hand or a hot reload, PrefabLibrary.Written for one an editor saved), the library tells every world that follows it, and the world
// writes down which prefab changed and the document it had before. Nothing else happens until ApplyPrefabUpdates, which the host calls at a point where changing entities is safe (a frame boundary): all
// that changed in between is one update.
//
// An update brings an instance up to date in four steps, none of which changes anything until the last one, so that an instance that cannot be updated is exactly as it was:
//   1. WHAT THE INSTANCE IS NOW. The instance is saved as a scene document (the code of SaveScene: its overrides are the difference between the live entities and what the OLD prefab expands to, what it
//      could not apply before is written first, and every entity gets a number). Entity fields that name entities outside the instance get numbers too (SavePlan.Externals), so they are not lost.
//   2. WHAT IT SHOULD BE. That document is planned as a load is (ScenePlan), with the NEW prefab. So an override survives where its target still exists, one whose target is gone is kept aside (the load
//      does that), what the prefab gained is made, and the budgets and the nesting limit are the load's. An update is a load that is not made into new entities.
//   3. CHECKS. The plan must not have a prefab that contains itself, and every entity of the instance must have a number in the document (otherwise something would be lost).
//   4. RECONCILE. The entities that are in both (matched by their number: a member of the prefab by its place in the instance, an added entity by the number it was written with) are the same entities,
//      so their ids, and everything that refers to them, stay; the plan's components, names and parents are made theirs. What the plan has and the world does not is created, and what the world has and the
//      plan does not (a member the prefab dropped, with what was added below it, which the plan kept aside) is destroyed.
//
// Because step 2 is the load, the result is what saving the scene before the change and loading it after would give (PrefabRefreshTests checks that on random edits).
public sealed unsafe partial class World
{
    private readonly Lock _updatesLock = new();
    private readonly Dictionary<uint, XmlNode?> _pendingBefore = [];   // prefab handle -> the document the instances were made from (null: it had none)
    private readonly List<uint> _pendingOrder = [];                    // the handles in the order they were announced
    private readonly HashSet<ulong> _madeWhileWaiting = [];            // instance roots made after a change was announced: made from the new documents, nothing to update
    private PrefabLibrary? _followed;
    private Dictionary<uint, XmlNode?>? _asOf;                         // while set, prefabs are read as they were (see ResolvePrefab)
    private bool _applying;                                            // ApplyPrefabUpdates is running

    // What the documents of updates number their entities with. What an instance could not apply is kept as it was written, numbers included, and what is kept of several instances lives in one world:
    // each update numbers from where the last stopped, high above what a scene numbers, so that two of them never use one number for two things.
    private int _numbered = 1_000_000_000;

    /// <summary>
    /// Raised after <see cref="ApplyPrefabUpdates"/> has made the instances of changed prefabs follow them, with what it did: the prefabs, the instances, the entities it made and destroyed, and the
    /// instances it could not update. A tool that holds entities (a selection, an undo history) reacts to it: see <see cref="PrefabUpdate"/>. Raised on the thread that applied the update; a handler must not throw.
    /// </summary>
    public event Action<PrefabUpdate>? PrefabsChanged;

    /// <summary>The path of each prefab whose instances were just brought up to date, one emission each (after <see cref="PrefabsChanged"/>): for a script that shows or caches what a prefab made.</summary>
    [Signal]
    public Signal<string> PrefabChanged { get; } = new();

    /// <summary>
    /// A host sets this to keep <see cref="ApplyPrefabUpdates"/> from being called by whatever notices a file change (a hot reload), while it is in the middle of something that must not see entities
    /// change under it (a drag, a modal edit); the change waits, and the host applies it when it is ready. Instances made while one waits are not changed by it.
    /// </summary>
    public bool HoldPrefabUpdates { get; set; }

    /// <summary>Whether a prefab changed and <see cref="ApplyPrefabUpdates"/> has not yet made its instances follow it.</summary>
    public bool HasPendingPrefabUpdates
    {
        get
        {
            lock (_updatesLock)
            {
                return _pendingBefore.Count > 0;
            }
        }
    }

    // Makes this world follow a library: what it announces is remembered here. A world follows one library at a time.
    private PrefabLibrary Follow(PrefabLibrary library)
    {
        if (!ReferenceEquals(_followed, library))
        {
            StopFollowingPrefabs();
            library.Changed += OnPrefabChanged;
            _followed = library;
        }

        _prefabs = library;
        return library;
    }

    private void StopFollowingPrefabs()
    {
        if (_followed is { } library)
        {
            library.Changed -= OnPrefabChanged;
            _followed = null;
        }

        lock (_updatesLock)
        {
            _pendingBefore.Clear();
            _pendingOrder.Clear();
            _madeWhileWaiting.Clear();
        }
    }

    // The library says a prefab has a new document: written down, with the one the instances were made from. The first change of a prefab since the last update is the one that has it (a later one only
    // changes what the instances will be made into). May be called from another thread than the world's: it only touches what is locked.
    private void OnPrefabChanged(uint handle)
    {
        XmlNode? previous = _followed?.PreviousDocument(handle);
        lock (_updatesLock)
        {
            if (_pendingBefore.TryAdd(handle, previous))
            {
                _pendingOrder.Add(handle);
            }
        }
    }

    // Instances made while an update waits come from the new documents: an update would take what they have for changes made to them.
    private void NoteMadeWhileUpdatesWait(List<Entity> created)
    {
        lock (_updatesLock)
        {
            if (_pendingBefore.Count == 0)
            {
                return;
            }

            foreach (Entity entity in created)
            {
                if (Has<PrefabInstance>(entity))
                {
                    _madeWhileWaiting.Add(entity.Id);
                }
            }
        }
    }

    /// <summary>
    /// Makes every instance of a prefab that changed follow it, in place: the entities that are still in the prefab keep their ids (so a selection, a reference from elsewhere and a script's variable
    /// stay valid), what the instance changed is kept where its target still exists (a field set to another value stays that value; the rest follows the prefab), what the prefab gained is made, what it
    /// lost is destroyed, and what the instance changed on something the prefab lost is kept aside in the instance and comes back with it. An instance that cannot be updated (the new prefab nests too
    /// deep, expands to more than <see cref="MaxLoadEntities"/> entities…) is left exactly as it was and reported in <see cref="PrefabUpdate.Failed"/>. Nothing is lost: a scene saved after an update is
    /// what saving it before, changing the prefab and loading it would be.
    /// </summary>
    /// <remarks>
    /// Call it from the thread that owns the world, where entities may change (a frame boundary), after <see cref="PrefabLibrary.Reload(string)"/> for every file that changed: one call is one update for all
    /// of them. It is not undoable; see <see cref="PrefabUpdate"/> for what an editor does about its history. Instances made after the change was announced are made from the new prefab and are left alone.
    /// </remarks>
    /// <returns>What was done, or null when no prefab changed since the last call (or another call is running).</returns>
    public PrefabUpdate? ApplyPrefabUpdates()
    {
        Dictionary<uint, XmlNode?> before;
        List<uint> order;
        HashSet<ulong> skip;
        lock (_updatesLock)
        {
            if (_pendingBefore.Count == 0 || _applying)
            {
                return null;
            }

            before = new Dictionary<uint, XmlNode?>(_pendingBefore);
            order = [.. _pendingOrder];
            skip = [.. _madeWhileWaiting];
            _pendingBefore.Clear();
            _pendingOrder.Clear();
            _madeWhileWaiting.Clear();
            _applying = true;
        }

        PrefabLibrary library = Prefabs;
        List<string> keys = [.. order.Select(handle => library.PathOf(handle) ?? library.RefOf(handle)?.ToString() ?? $"prefab {handle}")];
        var update = new PrefabUpdate(keys);
        try
        {
            UpdateInstances(before, skip, update);
        }
        finally
        {
            _applying = false;
        }

        if (update.Touched)
        {
            Log.Info("Prefab", $"{update.Updated.Count} instance(s) brought up to date with {string.Join(", ", keys)}" + (update.Failed.Count > 0 ? $"; {update.Failed.Count} left as they were" : ""));
        }

        PrefabsChanged?.Invoke(update);
        foreach (string key in keys)
        {
            PrefabChanged.Emit(key);
        }

        return update;
    }

    private void UpdateInstances(IReadOnlyDictionary<uint, XmlNode?> before, HashSet<ulong> skip, PrefabUpdate update)
    {
        var changed = new HashSet<string>(update.Prefabs, StringComparer.Ordinal);
        foreach (Entity root in AffectedInstances(before, changed, skip))
        {
            UpdateInstance(root, before, update);
        }
    }

    // The roots of the instances made, directly or through other prefabs, from one of the changed ones, that are not inside another such instance (its update carries them): in creation order.
    private List<Entity> AffectedInstances(IReadOnlyDictionary<uint, XmlNode?> before, HashSet<string> changed, HashSet<ulong> skip)
    {
        var dependent = new Dictionary<uint, bool>();
        var found = new List<Entity>();
        _asOf = new Dictionary<uint, XmlNode?>(before);
        try
        {
            foreach (Entity entity in UserEntities())
            {
                // A nested instance is a member of the one it is in (and a scene's own instance is not a member of anything): only the outermost ones are updated.
                if (!TryGet(entity, out PrefabInstance link) || IsPrefabMember(entity) || skip.Contains(entity.Id))
                {
                    continue;
                }

                if (!dependent.TryGetValue(link.Prefab, out bool depends))
                {
                    dependent[link.Prefab] = depends = UsesAny(link.Prefab, changed);
                }

                if (depends)
                {
                    found.Add(entity);
                }
            }
        }
        finally
        {
            _asOf = null;
        }

        var ids = new HashSet<ulong>(found.Select(static entity => entity.Id));
        found.RemoveAll(entity => HasAncestorIn(entity, ids));
        return found;
    }

    // Whether the prefab, expanded as it was before the change (_asOf), is made of one of the prefab files: the dependency record of the expansion (Expansion.Prefabs) says which files it read, those that
    // were missing included. An expansion that cannot be made says nothing, and is taken to depend: the update reports what is wrong.
    private bool UsesAny(uint prefab, HashSet<string> files)
    {
        if (ExistingLibrary?.RefOf(prefab) is not { IsEmpty: false } reference)
        {
            return false;
        }

        var element = new XmlNode(SceneEntity);
        element.SetAttribute("prefab", reference.ToString());
        try
        {
            return NewExpander(new ExpansionBudget(MaxLoadEntities)).Expand(element, "1").Prefabs.Any(files.Contains);
        }
        catch (FormatException)
        {
            return true;
        }
    }

    private bool HasAncestorIn(Entity entity, HashSet<ulong> ids)
    {
        for (Entity above = GetParent(entity); !above.IsNull; above = GetParent(above))
        {
            if (ids.Contains(above.Id))
            {
                return true;
            }
        }

        return false;
    }

    // Updates one instance, or leaves it as it was and says why.
    private void UpdateInstance(Entity root, IReadOnlyDictionary<uint, XmlNode?> before, PrefabUpdate update)
    {
        try
        {
            InstanceUpdate plan = PlanUpdate(root, before);
            Reconcile(plan, update);
            update.Updated.Add(root);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            Log.Error("Prefab", $"the instance '{GetName(root) ?? root.Id.ToString(CultureInfo.InvariantCulture)}' could not be brought up to date with its prefab and is left as it was: {e.Message}");
            update.Failed.Add(new PrefabUpdateFailure(root, e.Message));
        }
    }

    /// <summary>What an update of one instance is going to do, worked out and checked before anything is changed.</summary>
    private sealed class InstanceUpdate(Entity root, string rootAddress, ScenePlan target, Dictionary<string, Entity> living, Dictionary<string, Entity> outside)
    {
        public Entity Root { get; } = root;

        /// <summary>The root's address in the document.</summary>
        public string RootAddress { get; } = rootAddress;

        /// <summary>What the instance should be: the document of what it is, planned with the new prefabs.</summary>
        public ScenePlan Target { get; } = target;

        /// <summary>The entities of the instance by their address in that document (including the added ones).</summary>
        public Dictionary<string, Entity> Living { get; } = living;

        /// <summary>The entities outside the instance that its fields name, by the number the document gave them.</summary>
        public Dictionary<string, Entity> Outside { get; } = outside;
    }

    // Steps 1 to 3 (see the top of the file): nothing is changed, and an instance that cannot be updated throws.
    private InstanceUpdate PlanUpdate(Entity root, IReadOnlyDictionary<uint, XmlNode?> before)
    {
        // 1. What the instance is now, read with the documents it was made from.
        var outside = new Dictionary<string, Entity>(StringComparer.Ordinal);
        int number = _numbered;
        SavePlan saved;
        XmlNode document;
        _asOf = new Dictionary<uint, XmlNode?>(before);
        try
        {
            saved = PlanSave([root], _ => ++number, default, outside);
            document = WriteScene(saved, [root]);
        }
        finally
        {
            _asOf = null;
            _numbered = number;
        }

        var living = new Dictionary<string, Entity>(StringComparer.Ordinal);
        foreach ((ulong id, string address) in saved.AddressOf)
        {
            if (!outside.ContainsKey(address) && !living.TryAdd(address, new Entity(id)))
            {
                throw new InvalidOperationException($"two entities of the instance have the address {address}");
            }
        }

        // An entity the document does not hold would be taken for one the prefab dropped and destroyed, with what it has.
        foreach (Entity member in Subtree(root))
        {
            if (!saved.AddressOf.ContainsKey(member.Id))
            {
                throw new InvalidOperationException($"the entity '{GetName(member) ?? member.Id.ToString(CultureInfo.InvariantCulture)}' is not part of what the instance and its prefab say, so it cannot be told from one the prefab dropped");
            }
        }

        // 2. What it should be: the load of that document, with the documents as they are now.
        ScenePlan target = ScenePlan.Build(document, NewExpander(new ExpansionBudget(MaxLoadEntities)));

        // 3. Checks.
        if (target.Items.FirstOrDefault(static item => item.IsCut) is { } cut)
        {
            throw new FormatException(cut.MissingReason ?? "a prefab contains itself");
        }

        if (!saved.AddressOf.TryGetValue(root.Id, out string? rootAddress) || !target.Addresses.TryGetValue(rootAddress, out int at) || at != 0)
        {
            throw new InvalidOperationException("the instance's root is not what its prefab makes first");
        }

        return new InstanceUpdate(root, rootAddress, target, living, outside);
    }

    // Step 4: makes the entities of the instance what the plan says. Nothing here throws for a reason the plan could have found.
    private void Reconcile(InstanceUpdate update, PrefabUpdate report)
    {
        DefinePlanned(update.Target);
        var made = new List<Entity>();
        List<Entity> bound = Bind(update.Target, update.Living, made);
        Reparent(update.Target, bound, default);

        // What the plan did not bind is what the prefab dropped (with what was added below it, which the plan kept aside): it goes before names are given, so that its names are free.
        var madeIds = new HashSet<ulong>(made.Select(static entity => entity.Id));
        var kept = new HashSet<ulong>(bound.Select(static entity => entity.Id).Where(id => !madeIds.Contains(id)));
        List<Entity> dropped = [.. update.Living.Values.Where(entity => !kept.Contains(entity.Id)).OrderBy(static entity => entity.Id)];
        report.Destroyed.AddRange(dropped);
        foreach (Entity gone in dropped)
        {
            if (IsAlive(gone)) // one below another that went is gone with it
            {
                Destroy(gone);
            }
        }

        Fill(update.Target, bound, kept, update.Outside);
        report.Created.AddRange(made);
        LetGoOfReferencesToDropped(update, dropped, bound);
    }

    // An entity outside the instance that names one the prefab dropped: the field is empty now (the entity is gone) and remembers what it named, as the load of the same document would leave it, so that the
    // reference is written again and is there again when the prefab has the entity back. The entities of the instance were read again from the document, which did that for them.
    private unsafe void LetGoOfReferencesToDropped(InstanceUpdate update, List<Entity> dropped, List<Entity> bound)
    {
        if (dropped.Count == 0)
        {
            return;
        }

        var named = new Dictionary<ulong, DanglingRef>();
        string below = update.RootAddress + "/";
        foreach ((string address, Entity entity) in update.Living)
        {
            if (dropped.Contains(entity))
            {
                named[entity.Id] = address.StartsWith(below, StringComparison.Ordinal) ? new DanglingRef(update.Root.Id, address[below.Length..]) : new DanglingRef(0, address);
            }
        }

        var inside = new HashSet<ulong>(bound.Select(static entity => entity.Id));
        foreach (Entity entity in UserEntities())
        {
            if (inside.Contains(entity.Id))
            {
                continue;
            }

            foreach ((Entity id, ComponentInfo info) in ComponentsOf(entity))
            {
                if (info.IsTag)
                {
                    continue;
                }

                foreach (FieldDef field in info.Fields)
                {
                    if (field.Kind != FieldKind.Entity)
                    {
                        continue;
                    }

                    ulong* at = (ulong*)(GetPointer(entity, id) + field.Offset);
                    if (*at != 0 && named.TryGetValue(*at, out DanglingRef? reference))
                    {
                        *at = 0;
                        Modified(entity, id);
                        RememberReference(entity, info.Name, field.Name, reference);
                    }
                }
            }
        }
    }

    private void RememberReference(Entity entity, string component, string field, DanglingRef reference)
    {
        _kept.TryGetValue(entity.Id, out KeptData? held);
        var refs = new Dictionary<(string Component, string Field), DanglingRef>(held?.Refs ?? new Dictionary<(string, string), DanglingRef>()) { [(component, field)] = reference };
        _kept[entity.Id] = new KeptData { Unapplied = held?.Unapplied ?? [], RawPrefab = held?.RawPrefab, Assets = held?.Assets, Refs = refs };
    }
}
