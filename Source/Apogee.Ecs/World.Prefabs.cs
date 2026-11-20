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
using Apogee.Reflection;

namespace Apogee.Ecs;

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

    // What the instance elements of loaded documents held that could not be applied (an override of an entity the prefab no longer has…), by the entity id of the instance root: written back as they
    // were when the scene is saved. The nodes are copies, never changed.
    private readonly Dictionary<ulong, List<XmlNode>> _unapplied = [];

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
        get => _prefabs ??= new PrefabLibrary(Files ?? throw new InvalidOperationException("this world has no files to load prefabs from"), Assets);
        set => _prefabs = value ?? throw new ArgumentNullException(nameof(value));
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
                if (!resolved.TryGetValue(reference, out ResolvedPrefab? known))
                {
                    resolved[reference] = known = ResolvePrefab(reference);
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
        ScenePlan plan = ScenePlan.Build(document, NewExpander(budget), budget);
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
    /// <exception cref="InvalidOperationException">The world has no <see cref="Files"/>, or the tree holds an instance of the prefab being written (the file would contain itself).</exception>
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

        if (!IsUserEntity(root))
        {
            throw new ArgumentException("the root of a prefab must be an entity of the world", nameof(root));
        }

        PrefabLibrary library = Prefabs;
        uint handle = library.Load(resolved);

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

        // A prefab that holds an instance of itself, or of one that holds it, could not be read back as anything but missing instances.
        if (library.PathOf(handle) is { } target)
        {
            foreach (InstanceSave instance in plan.Instances.Values)
            {
                if (instance.Expansion.Prefabs.Contains(target))
                {
                    throw new InvalidOperationException($"{target} cannot hold an instance of itself (directly or through another prefab): saving it would make a prefab that contains itself");
                }
            }
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
        files.WriteAllText(resolved, SceneText(document));
        if (minted)
        {
            Assets?.Refresh(); // the database remembers what it found: this file has a UID now
        }

        // The file is there now (a new one named no file until this point): its handle is the one the entities hold. What the file numbers is a member of it now (an instance written inside it too:
        // it is an entity of this file, whatever file it was the root of); what is in an instance is the instance's prefab's, and stays as it was.
        handle = library.Load(resolved);
        foreach (Entity entity in plan.Numbered)
        {
            if (!TryGet(entity, out PrefabMember member) || member.Prefab == 0 || member.Prefab == handle || entity == root || plan.Instances.ContainsKey(entity.Id))
            {
                Set(entity, new PrefabMember { Id = uint.Parse(plan.AddressOf[entity.Id], CultureInfo.InvariantCulture), Prefab = handle });
            }
        }

        library.Written(handle);
    }
}
