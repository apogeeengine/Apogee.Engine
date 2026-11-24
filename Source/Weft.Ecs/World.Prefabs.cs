using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using Weft.Content;
using Weft.Content.Assets;
using Weft.Content.FileSystem;
using Weft.Content.Xml;
using Weft.Core.Assets;
using Weft.Core.Logging;
using Weft.Reflection;

namespace Weft.Ecs;

// Prefabs: a `.aprefab` file is one entity and what is below it, as XML, like a scene (docs/manual/content/prefabs.md; Scene/prefab.xsd).
//
//   <Prefab version="1" uid="uid://…" nextId="4">
//     <Entity id="1" name="Lamp"><Transform …/><Entity id="2" name="Bulb">…</Entity></Entity>
//   </Prefab>
//
// An entity's "id" is its number in the file and stays the same each time the file is saved (a scene's ids are renumbered on every save): what something that refers to an entity of a prefab can rely
// on. Instantiating makes plain entities, with PrefabMember{Id, Prefab} on each and PrefabInstance on the root. A scene (or another prefab) holds an instance as one element with only what it changes:
// Prefabs/PrefabExpander.cs makes the entities of one, Prefabs/ScenePlan.cs plans a load, Prefabs/PrefabDiff.cs works out the changes when it is saved (World.Scene.cs writes them).
public sealed unsafe partial class World
{
    private PrefabLibrary? _prefabs;
    private PrefabLibrary? _bare;   // what a world with no files makes handles in, so that an instance it cannot expand still names its prefab

    // What loaded documents said that the entities cannot show (see KeptData), by entity id: the elements an instance could not apply (an override of an entity the prefab no longer has…), which are
    // written back when the scene is saved, and the references of fields that did not load.
    private readonly Dictionary<ulong, KeptData> _kept = [];

    private void RegisterPrefabAssetType() =>
        RegisterAssetType("prefab", handle => ExistingLibrary?.RefOf(handle), reference => LoadingLibrary.Load(reference));

    /// <summary>How many prefabs may be nested in one another along one chain (a prefab inside one inside another…): the 17th is not expanded and its instance is empty.</summary>
    public const int MaxPrefabDepth = PrefabExpander.MaxDepth;

    private int _maxLoadEntities = ExpansionBudget.DefaultLimit;

    /// <summary>
    /// The most entities one load (<see cref="LoadScene(string, Entity)"/>, <see cref="LoadSceneFile"/>) or one <see cref="Instantiate(string, Entity)"/> may make, counting the entities of every instance it
    /// expands (262,144 by default). A document that would make more is a <see cref="FormatException"/> and nothing is made; the expansion also stops at 64 steps of work for each of them, so a few small
    /// files cannot keep a load busy without end however their prefabs nest. Saving a scene expands its instances again, with the same limit.
    /// </summary>
    public int MaxLoadEntities
    {
        get => _maxLoadEntities;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxLoadEntities = value;
        }
    }

    /// <summary>
    /// The prefab files this world knows, and the handles that <see cref="PrefabInstance"/> and <see cref="PrefabMember"/> hold. Made from <see cref="Files"/> and <see cref="Assets"/> when first asked
    /// for, so set those first; a host that shares one library between worlds (so that their handles agree) sets it. The <c>prefab</c> asset type (<c>[Property(Asset = "prefab")]</c>) is saved
    /// through it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The world has no <see cref="Files"/> to make a library from.</exception>
    public PrefabLibrary Prefabs
    {
        get => _prefabs ?? Follow(new PrefabLibrary(Files ?? throw new InvalidOperationException("this world has no files to load prefabs from"), Assets));
        set => Follow(value ?? throw new ArgumentNullException(nameof(value)));
    }

    // The library to take handles from when loading: the world's or, for a world with no files (it cannot make one), an empty one that only remembers what was asked for, so that an instance whose
    // prefab cannot be read still says which prefab it is and is saved as that instance.
    private PrefabLibrary LoadingLibrary => _prefabs ?? (Files is null ? _bare ??= new PrefabLibrary(new VirtualFileSystem(), null) : Prefabs);

    // The library that already gave handles in this world, if any.
    private PrefabLibrary? ExistingLibrary => _prefabs ?? _bare;

    // The handle of this run for a prefab key (a path, or the reference an unusable prefab was asked for); 0 for text that is not a reference.
    private uint HandleOfKey(string key) => AssetRef.TryParse(key, out AssetRef reference) ? LoadingLibrary.Load(reference) : 0;

    // An expander for one load or save: a prefab is looked up once for each way it is spelled, however many instances name it.
    private PrefabExpander NewExpander(ExpansionBudget budget)
    {
        var resolved = new Dictionary<string, ResolvedPrefab>(StringComparer.Ordinal);
        return new PrefabExpander(
            reference =>
            {
                // What an instance that is up to date is read as is not what the others are read as (see _asCurrent): another entry for it.
                string key = _asCurrent ? "\0" + reference : reference;
                if (!resolved.TryGetValue(key, out ResolvedPrefab? known))
                {
                    resolved[key] = known = ResolvePrefab(reference);
                }

                return known;
            },
            IsEntityFieldOf,
            budget);
    }

    // The prefab a `prefab` attribute names, for the expander: its document, and a key that is the file's path however the reference was spelled (so that a prefab that contains itself through its UID is
    // found). What cannot be used is told as a problem, not thrown: that instance is left empty and the load goes on.
    private ResolvedPrefab ResolvePrefab(string reference)
    {
        PrefabLibrary library = LoadingLibrary;
        uint handle = library.Load(reference);
        string key = library.PathOf(handle) ?? library.RefOf(handle)?.ToString() ?? reference;

        // What is made (or saved) while a change waits to be applied, and what an update in place reads, is the prefab as the entities were made: the document the library had before the change.
        if (DocumentAsOf(handle, out XmlNode? before))
        {
            return before is null ? new ResolvedPrefab(key, null, "there was no such prefab yet") : new ResolvedPrefab(key, before);
        }

        try
        {
            return new ResolvedPrefab(key, library.Require(handle));
        }
        catch (Exception e) when (e is FileNotFoundException or ContentException or FormatException)
        {
            return new ResolvedPrefab(key, null, e.Message);
        }
    }

    // Whether a field of a component of the engine or the game (registered here, or in the registry) holds an entity: what the expander rescopes.
    private bool IsEntityFieldOf(string component, string field) => InfoByName(component)?.FindField(field) is { Kind: FieldKind.Entity };

    internal ComponentInfo? InfoByName(string name)
    {
        Entity id = FindComponent(name);
        return id.IsNull ? TypeRegistry.FindComponent(name) : _infoById.GetValueOrDefault(id);
    }

    internal bool HasAssetType(string type) => _assetTypes.ContainsKey(type);

    /// <summary>
    /// Makes the entities of a prefab file under <paramref name="parent"/> (the world's top level when none) and returns the root, which has <see cref="PrefabInstance"/>; every entity made has
    /// <see cref="PrefabMember"/> with its id in the file. The entities are plain: the file's components are copied, and changing the instance changes nothing in the file. A prefab inside the prefab
    /// is made too, with its own changes (it is an instance in the same way). From a script this reads without the per-mod confinement of <c>fs.read_text</c>, as <c>load_scene_file</c> does.
    /// </summary>
    /// <param name="reference">The prefab: a path, <c>uid://…</c> (for the file that has that UID, wherever it moved to) or <c>path|uid://…</c>.</param>
    /// <param name="parent">Where the root goes; none puts it at the top level.</param>
    /// <exception cref="InvalidOperationException">The world has no <see cref="Files"/>.</exception>
    /// <exception cref="ContentException">The reference is a <c>uid://</c> that no file has.</exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="FormatException">The reference is not valid, the file is not a prefab, or it would make more than <see cref="MaxLoadEntities"/> entities.</exception>
    [Method]
    public Entity Instantiate(string reference, Entity parent = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        return Instantiate(Prefabs.Load(reference), parent);
    }

    /// <summary>Makes the entities of the prefab with this handle of <see cref="Prefabs"/>. See <see cref="Instantiate(string, Entity)"/>.</summary>
    /// <exception cref="ArgumentException">The handle is not one of the library's.</exception>
    public Entity Instantiate(uint prefab, Entity parent = default)
    {
        // The prefab itself must be usable: an instance inside a scene may be missing and be made empty, but a call that asks for this one prefab says why it cannot be had.
        PrefabLibrary library = Prefabs;
        _ = library.Require(prefab);
        var element = new XmlNode(SceneEntity);
        element.SetAttribute("prefab", (library.RefOf(prefab) ?? throw new ArgumentException($"{prefab} is not a prefab handle of this library", nameof(prefab))).ToString());
        var document = new XmlNode(SceneRoot);
        document.Children.Add(element);

        // A new budget for each call. What the prefab makes is planned before anything is made: when it cannot be had (its root is an instance of another prefab) or is too big, nothing is.
        var budget = new ExpansionBudget(MaxLoadEntities);
        ScenePlan plan = ScenePlan.Build(document, NewExpander(budget));
        if (plan.Items[0].MissingReason is { } reason)
        {
            throw new FormatException($"the prefab cannot be instantiated: {reason}");
        }

        return Create(plan, parent)[0];
    }

    /// <summary>The prefab an instance root was made from, as the text of its <see cref="AssetRef"/> (<c>res://prefabs/lamp.aprefab|uid://…</c>); null for any other entity.</summary>
    [Method]
    public string? PrefabOf(Entity entity) =>
        IsAlive(entity) && TryGet(entity, out PrefabInstance instance) ? ExistingLibrary?.RefOf(instance.Prefab)?.ToString() : null;

    /// <summary>Whether the entity is the root of a prefab instance (<see cref="PrefabInstance"/>).</summary>
    public bool IsPrefabInstance(Entity entity) => IsAlive(entity) && Has<PrefabInstance>(entity);

    /// <summary>The instance an entity belongs to: the nearest entity above it (or itself) that is an instance root; null when it is not part of one.</summary>
    public Entity InstanceOf(Entity member)
    {
        for (Entity current = member; !current.IsNull && IsAlive(current); current = GetParent(current))
        {
            if (Has<PrefabInstance>(current))
            {
                return current;
            }
        }

        return default;
    }

    /// <summary>
    /// Whether the entity is part of an instance without being its root: it came from the instance's prefab (or from a prefab inside it) and is named by its place there, which is why it cannot be moved
    /// under another parent. It can be renamed, changed, have components added and removed, be deleted, and have children added to it. The root of an instance, and every other entity, are not members.
    /// </summary>
    public bool IsPrefabMember(Entity entity) =>
        IsAlive(entity) && TryGet(entity, out PrefabMember member) && OwnerOf(entity, member.Prefab) is { IsNull: false } owner && owner != entity;

    // The instance a member's id belongs to: the nearest entity at or above it that is an instance of the file the id is in; none when there is none.
    private Entity OwnerOf(Entity entity, uint prefab)
    {
        for (Entity current = entity; !current.IsNull && IsAlive(current); current = GetParent(current))
        {
            if (TryGet(current, out PrefabInstance instance) && instance.Prefab == prefab)
            {
                return current;
            }
        }

        return default;
    }

    /// <summary>
    /// Writes <paramref name="root"/> and everything below it as the prefab file at <paramref name="path"/>. The ids in the file stay the same from one save to the next: an entity that came from this
    /// file (<see cref="PrefabMember"/> of it) keeps its id, and a new one takes the file's <c>nextId</c> and raises it, so an id that was deleted is never used again. The file keeps its UID
    /// (a new one gets a new UID). Every entity written is marked as a member of the file, with its id there, so that saving again keeps it. Entity fields that point outside what is saved are left out.
    /// An instance of another prefab inside the tree is written as it is in a scene: one element with only what differs from its prefab, numbered in this file. The root itself is written as a plain
    /// entity even when it is an instance (a prefab saved from an instance of itself, or of another prefab, holds its entities).
    /// </summary>
    /// <param name="path">A path ending in <c>.aprefab</c>, or <c>uid://…</c> for the file that has that UID.</param>
    /// <param name="root">The one root entity of the file.</param>
    /// <exception cref="InvalidOperationException">The world has no <see cref="Files"/>, or the tree holds an instance of the prefab being written (the file would contain itself), or what would be written is not a prefab that loads (nothing is written).</exception>
    /// <exception cref="UnauthorizedAccessException">The path is not in a writable source.</exception>
    /// <exception cref="ArgumentException">The path does not end in <c>.aprefab</c>, or the entity is not a user entity.</exception>
    public void SavePrefabFile(string path, Entity root)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        VirtualFileSystem files = Files ?? throw new InvalidOperationException("this world has no files to save a prefab to");
        string resolved = ResolveScenePath(path);
        if (!resolved.EndsWith(".aprefab", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"a prefab file's name ends in .aprefab: '{resolved}'", nameof(path));
        }

        PrefabLibrary library = Prefabs;
        PrefabFileBuild build = BuildPrefabFile(files, library, resolved, library.Load(resolved), root);
        files.WriteAllText(resolved, SceneText(build.Document));
        if (build.MintedUid)
        {
            Assets?.Refresh(); // the database remembers what it found: this file has a UID now
        }

        // The file is there now (a new one named no file until this point): its handle is the one the entities hold.
        uint handle = library.Load(resolved);
        TagSaved(build, root, handle);
        library.Written(handle);

        // The instance this was saved from has what the file has now (what was added to it is the file's), and an update from the file it had would make that again beside it.
        if (TryGet(root, out PrefabInstance saved) && saved.Prefab == handle)
        {
            MarkUpToDate(root);
        }
    }

    // A prefab file worked out from entities and not yet written: the document, and what is needed to tag the entities once it is.
    private sealed record PrefabFileBuild(string Path, XmlNode Document, SavePlan Plan, bool MintedUid);

    // Everything SavePrefabFile does before the write: the checks, the ids, the document, and that it is a prefab that loads. Nothing is written and nothing in the world changes.
    private PrefabFileBuild BuildPrefabFile(VirtualFileSystem files, PrefabLibrary library, string resolved, uint handle, Entity root)
    {
        if (!IsUserEntity(root))
        {
            throw new ArgumentException("the root of a prefab must be an entity of the world", nameof(root));
        }

        // What the file already has: the highest number ever given, which a deleted entity still counts for.
        long next = 1;
        if (files.FileExists(resolved))
        {
            try
            {
                XmlNode existing = XmlFile.Read(files, resolved);
                if (existing.Name == PrefabDocument.RootName)
                {
                    next = PrefabDocument.NextIdOf(existing);
                }
            }
            catch (ContentException)
            {
                // a file that cannot be read is replaced; its UID is kept by KeptUid
            }
        }

        // The entities that came from this file keep their number (the first of two with the same number does: a copy has its original's); the others get new ones, as they are met.
        var kept = new Dictionary<ulong, int>();
        var taken = new HashSet<int>();
        foreach (Entity entity in Subtree(root))
        {
            if (TryGet(entity, out PrefabMember member) && member.Prefab == handle && member.Id is >= 1 and <= int.MaxValue && taken.Add((int)member.Id))
            {
                kept[entity.Id] = (int)member.Id;
                next = Math.Max(next, (long)member.Id + 1);
            }
        }

        SavePlan plan = PlanSave(
            [root],
            entity =>
            {
                if (kept.TryGetValue(entity.Id, out int id))
                {
                    return id;
                }

                return next > int.MaxValue ? throw new InvalidOperationException("the prefab has used every entity id it can") : (int)next++;
            },
            root);

        // A prefab that holds an instance of itself, or of one that holds it, could not be read back as anything but missing instances. A file that is not there yet is named by the path it will have:
        // an instance of it (or of a prefab that already names it) is a missing instance until it is written, and would be a loop after.
        string target = library.PathOf(handle) ?? resolved;
        foreach (InstanceSave instance in plan.Instances.Values)
        {
            if (instance.Expansion.Prefabs.Any(key => key == target || (AssetRef.TryParse(key, out AssetRef named) && named.HasPath && string.Equals(named.Path, target, StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException($"{target} cannot hold an instance of itself (directly or through another prefab): saving it would make a prefab that contains itself");
            }
        }

        // The root is written as a plain entity, which has no place for what an instance element could not apply: that would be lost, and it is said.
        if (_kept.TryGetValue(root.Id, out KeptData? rootKept) && rootKept.Unapplied.Count > 0)
        {
            Log.Warn("Prefab", $"{resolved}: the root is an instance whose element held {rootKept.Unapplied.Count} change(s) that could not be applied: a prefab's root is an ordinary entity, so they are not saved");
        }

        (List<XmlNode> top, XmlNode? schemas) = WritePlanned(plan, [root]);
        AssetUid uid = KeptUid(files, resolved, out bool minted);
        var document = new XmlNode(PrefabDocument.RootName);
        document.SetAttribute("version", PrefabDocument.Version.ToString(CultureInfo.InvariantCulture));
        document.SetAttribute("uid", uid.ToString());
        document.SetAttribute("nextId", next.ToString(CultureInfo.InvariantCulture));
        if (schemas is not null)
        {
            document.Children.Add(schemas);
        }

        document.Children.AddRange(top);

        // A file that cannot be read back would make every instance of it empty: it is not written.
        try
        {
            PrefabDocument.Check(document, resolved, warnNextId: false);
        }
        catch (FormatException e)
        {
            throw new InvalidOperationException($"{resolved} would not load as a prefab, so it was not written: {e.Message}", e);
        }

        return new PrefabFileBuild(resolved, document, plan, minted);
    }

    // After the file was written: what it numbers is a member of it now (an instance written inside it too: it is an entity of this file, whatever file it was the root of); what is in an instance
    // is the instance's prefab's, and stays as it was.
    private void TagSaved(PrefabFileBuild build, Entity root, uint handle)
    {
        foreach (Entity entity in build.Plan.Numbered)
        {
            if (!IsAlive(entity))
            {
                continue; // an update that ran while the file was written took it away
            }

            if (!TryGet(entity, out PrefabMember member) || member.Prefab == 0 || member.Prefab == handle || entity == root || build.Plan.Instances.ContainsKey(entity.Id))
            {
                Set(entity, new PrefabMember { Id = uint.Parse(build.Plan.AddressOf[entity.Id], CultureInfo.InvariantCulture), Prefab = handle });
            }
        }
    }

    /// <summary>
    /// Makes the entities of a prefab file as the file itself: its own entities, under no parent, with the root returned. Unlike <see cref="Instantiate(uint, Entity)"/> the root is not an
    /// instance (nothing is made <em>of</em> the prefab: these are its entities), every entity of the file is a <see cref="PrefabMember"/> of it with its id there (so <see cref="SavePrefabContents"/>
    /// keeps the ids), and a prefab inside it is an instance, with the changes the file holds for it, as it is in a scene. This is what a tool that edits one prefab on its own (a prefab mode: see
    /// <c>Weft.Host.Editing.PrefabStage</c>) loads into a world of its own. The entities of the file are ordinary entities there (<see cref="IsPrefabMember"/> is false for them), so they can be moved
    /// under another parent, while what is inside an instance it holds is a member of that instance's prefab, as anywhere.
    /// </summary>
    /// <param name="prefab">The prefab, a handle of <see cref="Prefabs"/>.</param>
    /// <returns>The root entity of the file.</returns>
    /// <exception cref="ArgumentException">The handle is not one of the library's.</exception>
    /// <exception cref="ContentException">The prefab was named by a <c>uid://</c> that no file has.</exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="FormatException">The file is not a prefab, its root is itself an instance of another prefab (it holds no entities of its own to edit), or it would make more than <see cref="MaxLoadEntities"/> entities. Nothing is made.</exception>
    public Entity LoadPrefabContents(uint prefab)
    {
        PrefabLibrary library = Prefabs;
        XmlNode document = library.Require(prefab);
        string origin = library.PathOf(prefab) ?? library.RefOf(prefab)?.ToString() ?? $"prefab {prefab}";

        // The file read as a scene whose entities have the file's ids: planned before anything is made, so that a file that is too big, or whose root is an instance of another prefab, makes nothing.
        ScenePlan plan = ScenePlan.Build(document, NewExpander(new ExpansionBudget(MaxLoadEntities)));
        if (plan.Items[0].InstancePrefab is not null)
        {
            throw new FormatException($"the root of {origin} is an instance of another prefab: it holds no entities of its own to edit");
        }

        List<Entity> created = Create(plan, default);
        for (int i = 0; i < created.Count; i++)
        {
            // What the file numbers (its own entities, the roots of the instances it holds, and what an instance's changes add) has the file's ids. What is inside an instance is its prefab's.
            PlannedEntity item = plan.Items[i];
            if (item.MemberId is null && PrefabDocument.TryParseId(item.Address, out int id))
            {
                Set(created[i], new PrefabMember { Id = (uint)id, Prefab = prefab });
            }
        }

        return created[0];
    }

    /// <summary>Whether the prefab's file can be written: it is in a source that can be written and is not read-only on the disk (for a link, the file it leads to). False when the prefab has no file.</summary>
    /// <param name="prefab">The prefab, a handle of <see cref="Prefabs"/>.</param>
    public bool CanWritePrefab(uint prefab)
    {
        PrefabLibrary library = Prefabs;
        return Files is { } files && library.PathOf(prefab) is { } path && files.CanWrite(path) && !IsReadOnlyOnDisk(files, path);
    }

    /// <summary>
    /// Writes the entities made by <see cref="LoadPrefabContents"/> (and what was done to them) back to the prefab's file, with the ids the file's entities have kept (new entities take <c>nextId</c> and
    /// are members of the file from then on), by the write <see cref="ApplyOverrides"/> uses: the file is checked first and read back after, and the instances of the prefab (and of the prefabs that
    /// nest it) follow it in every world that shares the library once their <see cref="ApplyPrefabUpdates"/> runs. A file that the entities say nothing new about is not written at all (a hand-written
    /// layout, with its comments, stays). When this throws nothing was announced to any world, and the file is as it was (see the <see cref="IOException"/> for the one case where it cannot be).
    /// </summary>
    /// <param name="prefab">The prefab the entities were loaded from.</param>
    /// <param name="root">The root entity <see cref="LoadPrefabContents"/> returned, with what is below it now.</param>
    /// <exception cref="InvalidOperationException">An update of this world is running; the file is not what the library read (it changed on the disk: <see cref="PrefabLibrary.Reload(string, int)"/> it); what would be written holds the prefab itself, directly or through another, or is not a prefab that loads; the file could not be read back (it was put back); the world has no files or the prefab no file.</exception>
    /// <exception cref="UnauthorizedAccessException">The file is not in a source that can be written, or is read-only (<see cref="CanWritePrefab"/>).</exception>
    /// <exception cref="IOException">A file that was written could not be put back after a failure: the message says so.</exception>
    public void SavePrefabContents(uint prefab, Entity root)
    {
        if (_applying)
        {
            throw new InvalidOperationException("a prefab update is running: wait until it is done");
        }

        VirtualFileSystem files = Files ?? throw new InvalidOperationException("this world has no files to write a prefab to");
        PrefabLibrary library = Prefabs;
        string path = library.PathOf(prefab) ?? throw new InvalidOperationException($"the prefab {library.RefOf(prefab)?.ToString() ?? prefab.ToString(CultureInfo.InvariantCulture)} is not a file this world can find: there is nothing to write");

        // A file that cannot be written is refused whatever the entities say: saving it is a mistake even when it would change nothing.
        if (!files.CanWrite(path))
        {
            throw new UnauthorizedAccessException($"{path} is not in a source that can be written");
        }

        if (IsReadOnlyOnDisk(files, path))
        {
            throw new UnauthorizedAccessException($"{path} is read-only");
        }

        PrefabFileBuild build = BuildPrefabFile(files, library, path, prefab, root);

        // The file as the disk has it now, which must be the one the library holds: what an apply is held to.
        PrefabEditFile opened = OpenForEdit(files, library, [], path);
        if (build.Document.ToXml() == opened.Document.ToXml())
        {
            TagSaved(build, root, prefab);
            return; // the file says all of this already: it keeps the way it is laid out
        }

        // The checks of an apply, for the file as it will be, and then its write.
        if (library.Problem(prefab, build.Document, path, MaxLoadEntities, new Dictionary<uint, XmlNode> { [prefab] = build.Document }) is { } problem)
        {
            throw new InvalidOperationException($"{path} would not be a prefab that loads, so nothing was written: {problem}");
        }

        var file = new PrefabEditFile(prefab, opened.Key, path, build.Document, opened.Original, opened.Reformats) { Changed = true };
        var prepared = new PreparedApply(files, library, [file], [], [], [new PrefabFileWrite(path, file.Original, file.Reformats)]);
        CommitApply(root, prepared);
        if (build.MintedUid)
        {
            Assets?.Refresh();
        }

        TagSaved(build, root, prefab);
    }
}
