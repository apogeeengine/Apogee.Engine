using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Weft.Content.Xml;
using Weft.Core.Logging;
using Weft.Reflection;

namespace Weft.Ecs;

// Instances follow their prefab (docs/manual/content/prefabs.md, "When the prefab changes").
//
// When a prefab file changes (PrefabLibrary.Reload for a file edited by hand or a hot reload, PrefabLibrary.Written for one an editor saved), the library tells every world that follows it, and the world
// writes down which prefab changed and the document it had before. Nothing else happens until ApplyPrefabUpdates, which the host calls at a point where changing entities is safe (a frame boundary): all
// that changed in between is one update. Until then everything is still made from the old documents (a load, an Instantiate, a Clone, a restored snapshot, a save), so that what is made while an update
// waits is what the update expects to find.
//
// An update brings an instance up to date in four steps, none of which changes anything until the last one, so that an instance that cannot be updated is exactly as it was:
//   1. WHAT THE INSTANCE IS NOW. The instance is saved as a scene document (the code of SaveScene: its overrides are the difference between the live entities and what the OLD prefab expands to, what it
//      could not apply before is written first, and every entity gets a number). Entity fields that name entities outside the instance get numbers too (SavePlan.Externals), so they are not lost.
//   2. WHAT IT SHOULD BE. That document is planned as a load is (ScenePlan), with the NEW prefab. So an override survives where its target still exists, one whose target is gone is kept aside (the load
//      does that), what the prefab gained is made, and the budgets and the nesting limit are the load's. An update is a load that is not made into new entities.
//   3. CHECKS. The plan must not have a prefab that contains itself, and every entity of the instance must have a number in the document (otherwise something would be lost).
//   4. RECONCILE. The entities that are in both (matched by their number: a member of the prefab by its place in the instance, an added entity by the number it was written with) are the same entities,
//      so their ids, and everything that refers to them, stay; the plan's components, names and parents are made theirs. What the plan has and the world does not is created, and what the world has and the
//      plan does not (a member the prefab dropped, with what was added below it, which the plan kept aside) is destroyed. Should anything throw, the instance is put back as it was (a snapshot of it is
//      taken first) and reported as failed.
//
// Because step 2 is the load, the result is what saving the scene before the change and loading it after would give (PrefabRefreshTests checks that on random edits). What an update does to the rest
// of the world is done once at the end, for all instances: an entity field outside them that named an entity that was dropped lets go of it (and remembers it), and one that waits for an entity the
// update has made (a field that was emptied before, or never had its target) is tied to it.
public sealed unsafe partial class World
{
    private const int FirstCaptureNumber = 1_000_000_000;

    private readonly Lock _updatesLock = new();
    private readonly Dictionary<uint, XmlNode?> _pendingBefore = [];   // prefab handle -> the document the instances were made from (null: it had none)
    private readonly List<uint> _pendingOrder = [];                    // the handles in the order they were announced
    private readonly HashSet<ulong> _upToDate = [];                    // instance roots that were saved over their own prefab: they have what the file has now
    private PrefabLibrary? _followed;
    private Dictionary<uint, XmlNode?>? _asOf;                         // while set, prefabs are read as they were (see ResolvePrefab)
    private bool _applying;                                            // ApplyPrefabUpdates is running
    private HashSet<ulong>? _upToDateNow;                              // the instances ApplyPrefabUpdates leaves alone because they were saved over their prefab
    private bool _asCurrent;                                           // an instance of that kind is being read: its prefabs are read as they are, not as they were
    private Aftermath? _aftermath;                                     // what the instances updated so far did to the world, while ApplyPrefabUpdates runs
    private Action<CaptureEdit>? _captureEdit;                         // what changes the saved form of an instance before it is updated from it (a revert, an apply)

    private DanglingRef? DroppedBy(ulong entity) => _aftermath is { } aftermath && aftermath.Dropped.TryGetValue(entity, out DanglingRef? named) ? named : null;

    // What the documents of updates number their entities with. What an instance could not apply is kept as it was written, and what is kept of several instances lives in one world: each update numbers
    // from where the last stopped, high above what a scene numbers, so that two of them never use one number for two things. A restored snapshot brings the counter of the world it was taken in.
    private int _numbered = FirstCaptureNumber;

    /// <summary>The number the next update of an instance starts after (see the comment above).</summary>
    internal int CaptureCounter => _numbered;

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
    /// change under it (a drag, a modal edit); the change waits, and the host applies it when it is ready. What is made while it waits comes from the documents the instances were made from.
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
            _upToDate.Clear();
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

    // The instance has what its prefab says now (it was saved over the file), so the update leaves it alone.
    private void MarkUpToDate(Entity root)
    {
        lock (_updatesLock)
        {
            _upToDate.Add(root.Id);
        }
    }

    // The document the entities of a prefab were made from, when that is not the library's current one: the document before a change that waits to be applied (or that an update reads), `document` null when
    // there was none. False when the entities are made from the current one.
    private bool DocumentAsOf(uint handle, out XmlNode? document)
    {
        if (_asCurrent)
        {
            document = null;
            return false;
        }

        if (_asOf is not null)
        {
            return _asOf.TryGetValue(handle, out document);
        }

        if (!_applying)
        {
            lock (_updatesLock)
            {
                // A prefab that had no document is made from the one it has now (an instance made from it is left alone by the update: see NoteMadeWhileUpdatesWait).
                return _pendingBefore.TryGetValue(handle, out document) && document is not null;
            }
        }

        document = null;
        return false;
    }

    // An instance of a prefab that had no document, made while that waits to be applied, is made from the document it has: the update, which would take it for an instance that is missing, leaves it alone.
    private void NoteMadeWhileUpdatesWait(List<Entity> created)
    {
        lock (_updatesLock)
        {
            if (_pendingBefore.Count == 0 || !_pendingBefore.ContainsValue(null))
            {
                return;
            }

            foreach (Entity entity in created)
            {
                if (TryGet(entity, out PrefabInstance instance) && _pendingBefore.TryGetValue(instance.Prefab, out XmlNode? before) && before is null)
                {
                    _upToDate.Add(entity.Id);
                }
            }
        }
    }

    // ---- Snapshots

    // The documents the entities were made from, by the reference of each prefab they hold instances or members of.
    private Dictionary<string, XmlNode?>? DocumentsOf(IEnumerable<Entity> entities)
    {
        if (ExistingLibrary is not { } library)
        {
            return null;
        }

        Dictionary<string, XmlNode?>? found = null;
        var seen = new HashSet<uint>();

        void Note(uint handle)
        {
            if (handle != 0 && seen.Add(handle) && library.RefOf(handle)?.ToString() is { } key)
            {
                (found ??= new Dictionary<string, XmlNode?>(StringComparer.Ordinal))[key] = DocumentAsOf(handle, out XmlNode? asOf) ? asOf : CurrentDocument(library, handle);
            }
        }

        foreach (Entity entity in entities)
        {
            if (TryGet(entity, out PrefabInstance instance))
            {
                Note(instance.Prefab);
            }

            if (TryGet(entity, out PrefabMember member))
            {
                Note(member.Prefab);
            }
        }

        return found;
    }

    private static XmlNode? CurrentDocument(PrefabLibrary library, uint handle)
    {
        try
        {
            return library.Document(handle);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool SameDocument(XmlNode? a, XmlNode? b) => ReferenceEquals(a, b) || (a is not null && b is not null && a.ToXml() == b.ToXml());

    // After a snapshot was restored: its entities were made from the documents it recorded, which are not the library's when a prefab changed since (a Stop after a prefab was edited during Play). What
    // was in the world is gone, so what was waiting for those prefabs is replaced by what these entities are: an update brings them to the current documents.
    private void QueueUpdatesForRestored(WorldSnapshot snapshot)
    {
        _numbered = Math.Max(_numbered, snapshot.Numbered);
        if (snapshot.Documents is not { Count: > 0 } documents || _followed is not { } library)
        {
            return;
        }

        foreach ((string key, XmlNode? then) in documents)
        {
            uint handle = HandleOfKey(key);
            if (handle == 0)
            {
                continue;
            }

            bool same = SameDocument(then, CurrentDocument(library, handle));
            lock (_updatesLock)
            {
                if (same)
                {
                    if (_pendingBefore.Remove(handle))
                    {
                        _pendingOrder.Remove(handle);
                    }
                }
                else if (_pendingBefore.TryGetValue(handle, out _))
                {
                    _pendingBefore[handle] = then;
                }
                else
                {
                    _pendingBefore[handle] = then;
                    _pendingOrder.Add(handle);
                }
            }
        }
    }

    // ---- Applying

    /// <summary>
    /// Makes every instance of a prefab that changed follow it, in place: the entities that are still in the prefab keep their ids (so a selection, a reference from elsewhere and a script's variable
    /// stay valid), what the instance changed is kept where its target still exists (a field set to another value stays that value; the rest follows the prefab), what the prefab gained is made, what it
    /// lost is destroyed, and what the instance changed on something the prefab lost is kept aside in the instance and comes back with it. An instance that cannot be updated (the new prefab nests too
    /// deep, expands to more than <see cref="MaxLoadEntities"/> entities, something throws while it is changed…) is left exactly as it was and reported in <see cref="PrefabUpdate.Failed"/>. Nothing is
    /// lost: a scene saved after an update is what saving it before, changing the prefab and loading it would be.
    /// </summary>
    /// <remarks>
    /// Call it from the thread that owns the world, where entities may change (a frame boundary), after <see cref="PrefabLibrary.Reload(string, int)"/> for every file that changed: one call is one update for all
    /// of them. It is not undoable; see <see cref="PrefabUpdate"/> for what an editor does about its history. What was made after the change was announced was made from the documents the instances were
    /// made from, and is brought up to date with them; an instance that was saved over its own prefab (<see cref="SavePrefabFile"/>) already has what the file says.
    /// </remarks>
    /// <returns>What was done, or null when no prefab changed since the last call (or another call is running).</returns>
    public PrefabUpdate? ApplyPrefabUpdates()
    {
        Dictionary<uint, XmlNode?> before;
        List<uint> order;
        HashSet<ulong> upToDate;
        lock (_updatesLock)
        {
            if (_pendingBefore.Count == 0 || _applying)
            {
                return null;
            }

            before = new Dictionary<uint, XmlNode?>(_pendingBefore);
            order = [.. _pendingOrder];
            upToDate = [.. _upToDate];
            _pendingBefore.Clear();
            _pendingOrder.Clear();
            _upToDate.Clear();
            _applying = true;
        }

        PrefabLibrary library = Prefabs;
        List<string> keys = [.. order.Select(handle => library.PathOf(handle) ?? library.RefOf(handle)?.ToString() ?? $"prefab {handle}")];
        var update = new PrefabUpdate(keys);
        var aftermath = new Aftermath();
        _aftermath = aftermath;
        _upToDateNow = upToDate;
        bool started = false;
        try
        {
            foreach (Entity root in AffectedInstances(before, new HashSet<string>(keys, StringComparer.Ordinal), upToDate))
            {
                started = true;
                UpdateInstance(root, before, update, aftermath);
            }

            started = true;
            FinishReferences(aftermath);
        }
        catch when (!started)
        {
            // Nothing was changed: what was announced is still waiting.
            lock (_updatesLock)
            {
                foreach (uint handle in order)
                {
                    if (_pendingBefore.TryAdd(handle, before[handle]))
                    {
                        _pendingOrder.Add(handle);
                    }
                }
            }

            throw;
        }
        finally
        {
            _applying = false;
            _aftermath = null;
            _upToDateNow = null;
        }

        if (update.Touched)
        {
            Log.Info("Prefab", $"{update.Updated.Count} instance(s) brought up to date with {string.Join(", ", keys)}" + (update.Failed.Count > 0 ? $"; {update.Failed.Count} left as they were" : ""));
        }

        if (!_quietUpdates)
        {
            RaiseUpdated(update);
        }

        return update;
    }

    // Set while a call that may put its change back (an apply) runs its updates: it says what it came to when it is done, and an apply that was put back changed nothing to tell about.
    private bool _quietUpdates;

    // Tells what an update did: the event, and the signal of each prefab.
    private void RaiseUpdated(PrefabUpdate update)
    {
        PrefabsChanged?.Invoke(update);
        foreach (string key in update.Prefabs)
        {
            PrefabChanged.Emit(key);
        }
    }

    /// <summary>What the updates of all instances leave to be done to the rest of the world, collected so that it is done once.</summary>
    private sealed class Aftermath
    {
        /// <summary>What each updated instance is: its plan and the entity of each item of it.</summary>
        public List<(ScenePlan Plan, List<Entity> Bound)> Planned { get; } = [];

        /// <summary>The entities that were dropped, by id, and what each was called: the nearest entity above it that stays and what follows its address.</summary>
        public Dictionary<ulong, DanglingRef> Dropped { get; } = [];

        /// <summary>The entities of the updated instances by an entity above them and what follows its address, for the fields that wait for them.</summary>
        public Dictionary<(ulong Root, string Text), Entity> Reach { get; } = [];

        /// <summary>The entities of the updated instances that have a number alone for an address (what a kept element adds), by it.</summary>
        public Dictionary<string, Entity> Numbered { get; } = new(StringComparer.Ordinal);
    }

    // The roots of the instances made, directly or through other prefabs, from one of the changed ones, that are not inside another such instance (its update carries them): in creation order.
    private List<Entity> AffectedInstances(IReadOnlyDictionary<uint, XmlNode?> before, HashSet<string> changed, HashSet<ulong> upToDate)
    {
        var dependent = new Dictionary<uint, bool>();
        var found = new List<Entity>();
        _asOf = new Dictionary<uint, XmlNode?>(before);
        try
        {
            foreach (Entity entity in UserEntities())
            {
                // A nested instance is a member of the one it is in (and a scene's own instance is not a member of anything): only the outermost ones are updated.
                if (!TryGet(entity, out PrefabInstance link) || IsPrefabMember(entity) || upToDate.Contains(entity.Id))
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

    // What no update recovers from; everything else that goes wrong in an instance leaves it as it was.
    private static bool IsRecoverable(Exception e) => e is not OutOfMemoryException;

    // Updates one instance, or leaves it as it was and says why.
    private void UpdateInstance(Entity root, IReadOnlyDictionary<uint, XmlNode?>? before, PrefabUpdate update, Aftermath aftermath)
    {
        InstanceUpdate plan;
        try
        {
            plan = PlanUpdate(root, before);
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            Failed(root, e, update);
            return;
        }

        WorldSnapshot backup = SnapshotTree(root);
        var made = new List<Entity>();
        List<Entity> dropped;
        try
        {
            dropped = Reconcile(plan, made, out List<Entity> bound);
            aftermath.Planned.Add((plan.Target, bound));
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            PutBack(backup, made);
            Failed(root, e, update);
            return;
        }

        update.Updated.Add(root);
        update.Created.AddRange(made);
        update.Destroyed.AddRange(dropped);
        var droppedSet = new HashSet<Entity>(dropped);
        foreach (Entity gone in dropped)
        {
            aftermath.Dropped[gone.Id] = NameBelowSurvivor(plan.AddressOf[gone.Id], plan.Living, droppedSet);
        }
    }

    private void Failed(Entity root, Exception e, PrefabUpdate update)
    {
        Log.Error("Prefab", $"the instance '{GetName(root) ?? root.Id.ToString(CultureInfo.InvariantCulture)}' could not be brought up to date with its prefab and is left as it was: {e.Message}");
        update.Failed.Add(new PrefabUpdateFailure(root, e.Message));
    }

    // Undoes a reconcile that threw: the entities of the instance come back as they were taken (alive again when destroyed, under their parents, with their components, names and what is kept aside), and what
    // it made goes.
    private void PutBack(WorldSnapshot backup, List<Entity> made)
    {
        RestoreEntities(backup);
        foreach (Entity entity in made)
        {
            if (IsAlive(entity))
            {
                Destroy(entity);
            }
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

        /// <summary>The address in the document of an entity of the instance, by its id.</summary>
        public Dictionary<ulong, string> AddressOf { get; } = living.ToDictionary(static pair => pair.Value.Id, static pair => pair.Key);
    }

    // Steps 1 to 3 (see the top of the file): nothing is changed, and an instance that cannot be updated throws.
    private InstanceUpdate PlanUpdate(Entity root, IReadOnlyDictionary<uint, XmlNode?>? before)
    {
        // 1. What the instance is now, read with the documents it was made from.
        var outside = new Dictionary<string, Entity>(StringComparer.Ordinal);
        int number = _numbered;
        SavePlan saved;
        XmlNode document;
        _asOf = before is null ? null : new Dictionary<uint, XmlNode?>(before); // no documents given: the ones the entities were made from (what a load reads while a change waits)
        try
        {
            saved = PlanSave([root], _ => checked(++number), default, outside);
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

        // What the caller changes in the document before it is planned again: the overrides that are reverted or applied (World.PrefabOverrides.cs).
        _captureEdit?.Invoke(new CaptureEdit(root, saved, document, living));

        // 2. What it should be: the load of that document, with the documents as they are now, at the depth the instance has in the world.
        ScenePlan target = ScenePlan.Build(document, NewExpander(new ExpansionBudget(MaxLoadEntities)), DepthOf(root));

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

    // How many entities are above this one.
    private int DepthOf(Entity entity)
    {
        int depth = 0;
        for (Entity above = GetParent(entity); !above.IsNull; above = GetParent(above))
        {
            depth++;
        }

        return depth;
    }

    // Step 4: makes the entities of the instance what the plan says; returns what the prefab dropped (destroyed), and, in `made`, what it made. May throw: the caller puts the instance back.
    private List<Entity> Reconcile(InstanceUpdate update, List<Entity> made, out List<Entity> bound)
    {
        DefinePlanned(update.Target);
        bound = Bind(update.Target, update.Living, made);
        Reparent(update.Target, bound, default);

        // What the plan did not bind is what the prefab dropped (with what was added below it, which the plan kept aside). Their names are free before names are given, but they are destroyed only once nothing
        // can throw any more: a destroyed entity that is brought back may find its place taken by one made after (the index of ids is not changed by bringing one back).
        var madeIds = new HashSet<ulong>(made.Select(static entity => entity.Id));
        var kept = new HashSet<ulong>(bound.Select(static entity => entity.Id).Where(id => !madeIds.Contains(id)));
        List<Entity> dropped = [.. update.Living.Values.Where(entity => !kept.Contains(entity.Id)).OrderBy(static entity => entity.Id)];
        foreach (Entity gone in dropped)
        {
            SetName(gone, "");
        }

        Fill(update.Target, bound, kept, update.Outside);
        foreach (Entity gone in dropped)
        {
            if (IsAlive(gone)) // one below another that went is gone with it
            {
                Destroy(gone);
            }
        }

        return dropped;
    }

    // ---- What is left to do to the rest of the world, once

    private void FinishReferences(Aftermath aftermath)
    {
        if (aftermath.Dropped.Count > 0)
        {
            RetargetKeptReferences(aftermath.Dropped);
            LetGoOfReferencesToDropped(aftermath.Dropped);
        }

        // Fields that wait for an entity (before the updates, or because one of them dropped what they named) are tied to the entities the updates made.
        if (aftermath.Dropped.Count > 0 || _kept.Values.Any(static kept => kept.Refs is not null))
        {
            foreach ((ScenePlan plan, List<Entity> bound) in aftermath.Planned)
            {
                NoteReachable(aftermath, plan, bound);
            }

            ReconnectReferences(aftermath);
        }
    }

    // Notes, for every entity of an updated instance, how a field that waits for it names it: by an entity above it (an instance root) and what follows that one's address, or by a number alone.
    private static void NoteReachable(Aftermath aftermath, ScenePlan target, List<Entity> bound)
    {
        var byAddress = new Dictionary<string, Entity>(target.Addresses.Count, StringComparer.Ordinal);
        foreach ((string address, int at) in target.Addresses)
        {
            byAddress[address] = bound[at];
        }

        foreach ((string address, Entity entity) in byAddress)
        {
            int slash = address.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                aftermath.Numbered.TryAdd(address, entity); // a copy of an instance has its kept elements' numbers too: the first one is the number's
                continue;
            }

            for (; slash > 0; slash = address.IndexOf('/', slash + 1))
            {
                if (byAddress.TryGetValue(address[..slash], out Entity above))
                {
                    aftermath.Reach.TryAdd((above.Id, address[(slash + 1)..]), entity);
                }
            }
        }
    }

    // An entity field outside the updated instances that names an entity an update destroyed: it is empty now (the entity is gone) and remembers what it named, as the load of the same document leaves
    // it, so that the reference is written again and is there again when the prefab has the entity back. The entities of the instances were read again from their document, which did that for them.
    private void LetGoOfReferencesToDropped(Dictionary<ulong, DanglingRef> named)
    {
        var fieldsOf = new Dictionary<ComponentInfo, FieldDef[]>();
        foreach (Entity entity in UserEntities())
        {
            foreach ((Entity id, ComponentInfo info) in ComponentsOf(entity))
            {
                if (info.IsTag)
                {
                    continue;
                }

                if (!fieldsOf.TryGetValue(info, out FieldDef[]? fields))
                {
                    fieldsOf[info] = fields = [.. info.Fields.Where(static field => field.Kind == FieldKind.Entity)];
                }

                foreach (FieldDef field in fields)
                {
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

    // The elements kept aside in any instance of the world may name an entity that was just destroyed (KeptData.Token and its id): they name it by its place instead, as a load of the same text leaves them.
    private void RetargetKeptReferences(Dictionary<ulong, DanglingRef> named)
    {
        foreach ((ulong owner, KeptData held) in _kept.ToList())
        {
            List<XmlNode>? changed = null;
            for (int i = 0; i < held.Unapplied.Count; i++)
            {
                if (!NamesAny(held.Unapplied[i], named))
                {
                    continue;
                }

                XmlNode copy = held.Unapplied[i].Clone(); // what is kept is shared with snapshots and clones: never changed
                Retarget(copy, named);
                changed ??= [.. held.Unapplied];
                changed[i] = copy;
            }

            if (changed is not null)
            {
                _kept[owner] = new KeptData { Unapplied = changed, RawPrefab = held.RawPrefab, Assets = held.Assets, Refs = held.Refs };
            }
        }
    }

    // The id a field of a kept element names by KeptData.Token, and what follows it; false for any other text.
    private static bool TryReadToken(string value, out ulong entity, out string rest)
    {
        entity = 0;
        rest = "";
        if (value.Length < 2 || value[0] != KeptData.Token)
        {
            return false;
        }

        int slash = value.IndexOf('/', StringComparison.Ordinal);
        ReadOnlySpan<char> id = slash < 0 ? value.AsSpan(1) : value.AsSpan(1, slash - 1);
        rest = slash < 0 ? "" : value[slash..];
        return ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out entity);
    }

    private static bool NamesAny(XmlNode node, Dictionary<ulong, DanglingRef> named)
    {
        if (node.Name is "Entity" or "Override")
        {
            return node.Children.Any(child => NamesAny(child, named));
        }

        return node.Attributes.Any(attribute => TryReadToken(attribute.Value, out ulong entity, out _) && named.ContainsKey(entity));
    }

    private static void Retarget(XmlNode node, Dictionary<ulong, DanglingRef> named)
    {
        if (node.Name is "Entity" or "Override")
        {
            foreach (XmlNode child in node.Children)
            {
                Retarget(child, named);
            }

            return;
        }

        for (int i = 0; i < node.Attributes.Count; i++)
        {
            XmlProperty attribute = node.Attributes[i];
            if (TryReadToken(attribute.Value, out ulong entity, out string rest) && named.TryGetValue(entity, out DanglingRef? now))
            {
                string text = now.Root != 0 ? KeptData.Token + now.Root.ToString(CultureInfo.InvariantCulture) + "/" + now.Text + rest : now.Text + rest;
                node.Attributes[i] = attribute with { Value = text };
            }
        }
    }

    // What each dropped entity was called: the nearest entity above it in the document that stays (the instance's root, or an instance added below it) and what follows that one's address; one that is
    // below none (an entity a kept element adds) by its number alone.
    private static DanglingRef NameBelowSurvivor(string address, Dictionary<string, Entity> living, HashSet<Entity> dropped)
    {
        for (int slash = address.LastIndexOf('/'); slash > 0; slash = address.LastIndexOf('/', slash - 1))
        {
            if (living.TryGetValue(address[..slash], out Entity above) && !dropped.Contains(above))
            {
                return new DanglingRef(above.Id, address[(slash + 1)..]);
            }
        }

        return new DanglingRef(0, address);
    }

    private void RememberReference(Entity entity, string component, string field, DanglingRef reference)
    {
        _kept.TryGetValue(entity.Id, out KeptData? held);
        var refs = new Dictionary<(string Component, string Field), DanglingRef>(held?.Refs ?? new Dictionary<(string, string), DanglingRef>()) { [(component, field)] = reference };
        _kept[entity.Id] = new KeptData { Unapplied = held?.Unapplied ?? [], RawPrefab = held?.RawPrefab, Assets = held?.Assets, Refs = refs };
    }

    // A field that waits for an entity (it is empty, and remembers what it named) when the update has made that entity: it is tied to it, as a load of the saved scene would have done.
    private void ReconnectReferences(Aftermath aftermath)
    {
        foreach ((ulong owner, KeptData held) in _kept.ToList())
        {
            if (held.Refs is not { Count: > 0 } refs || !IsAlive(new Entity(owner)))
            {
                continue;
            }

            Dictionary<(string Component, string Field), DanglingRef>? left = null;
            foreach (((string component, string field), DanglingRef waiting) in refs)
            {
                Entity target;
                bool found = waiting.Root != 0 ? aftermath.Reach.TryGetValue((waiting.Root, waiting.Text), out target) : TryFindByNumber(aftermath, waiting.Text, out target);
                if (found && IsAlive(target) && TryTie(new Entity(owner), component, field, target))
                {
                    left ??= new Dictionary<(string, string), DanglingRef>(refs);
                    left.Remove((component, field));
                }
            }

            if (left is not null)
            {
                var rest = new KeptData { Unapplied = held.Unapplied, RawPrefab = held.RawPrefab, Assets = held.Assets, Refs = left.Count > 0 ? left : null };
                if (rest.IsEmpty)
                {
                    _kept.Remove(owner);
                }
                else
                {
                    _kept[owner] = rest;
                }
            }
        }
    }

    // The entity an update made that a number names (what a kept element adds), or one inside it (the number, then the address below it).
    private static bool TryFindByNumber(Aftermath aftermath, string text, out Entity found)
    {
        int slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return aftermath.Numbered.TryGetValue(text, out found);
        }

        found = default;
        return aftermath.Numbered.TryGetValue(text[..slash], out Entity above) && aftermath.Reach.TryGetValue((above.Id, text[(slash + 1)..]), out found);
    }

    // Makes an entity field that is empty name the entity; false when the entity has no such field, or it names something.
    private bool TryTie(Entity owner, string component, string field, Entity target)
    {
        Entity id = FindComponent(component);
        if (id.IsNull || GetComponentInfo(id) is not { IsTag: false } info || info.FindField(field) is not { Kind: FieldKind.Entity } def)
        {
            return false;
        }

        byte* data = GetPointer(owner, id);
        if (data is null)
        {
            return false;
        }

        ulong* at = (ulong*)(data + def.Offset);
        if (*at != 0)
        {
            return false;
        }

        *at = target.Id;
        Modified(owner, id);
        return true;
    }
}
