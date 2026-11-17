using System;
using System.Collections.Generic;
using System.Globalization;
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
// on. Instantiating makes plain entities, with PrefabMember{Id, Prefab} on each and PrefabInstance on the root.
public sealed unsafe partial class World
{
    private PrefabLibrary? _prefabs;

    private void RegisterPrefabAssetType() =>
        RegisterAssetType("prefab", handle => _prefabs?.RefOf(handle), reference => Prefabs.Load(reference));

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

    /// <summary>
    /// Makes the entities of a prefab file under <paramref name="parent"/> (the world's top level when none) and returns the root, which has <see cref="PrefabInstance"/>; every entity made has
    /// <see cref="PrefabMember"/> with its id in the file. The entities are plain: the file's components are copied, and changing the instance changes nothing in the file. From a script this
    /// reads without the per-mod confinement of <c>fs.read_text</c>, as <c>load_scene_file</c> does.
    /// </summary>
    /// <param name="reference">The prefab: a path, <c>uid://…</c> (for the file that has that UID, wherever it moved to) or <c>path|uid://…</c>.</param>
    /// <param name="parent">Where the root goes; none puts it at the top level.</param>
    /// <exception cref="InvalidOperationException">The world has no <see cref="Files"/>.</exception>
    /// <exception cref="ContentException">The reference is a <c>uid://</c> that no file has.</exception>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    /// <exception cref="FormatException">The reference is not valid, or the file is not a prefab.</exception>
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
        XmlNode document = Prefabs.Require(prefab);
        List<Entity> created = LoadEntities(document, parent, out List<XmlNode> nodes);
        if (created.Count == 0)
        {
            throw new FormatException("the root entity of the prefab names another prefab, which this version does not expand");
        }

        for (int i = 0; i < created.Count; i++)
        {
            // The ids were checked with the document.
            if (PrefabDocument.TryParseId(nodes[i].Attribute("id"), out int id))
            {
                Set(created[i], new PrefabMember { Id = (uint)id, Prefab = prefab });
            }
        }

        Set(created[0], new PrefabInstance { Prefab = prefab });
        return created[0];
    }

    /// <summary>The prefab an instance root was made from, as the text of its <see cref="AssetRef"/> (<c>res://prefabs/lamp.aprefab|uid://…</c>); null for any other entity.</summary>
    [Method]
    public string? PrefabOf(Entity entity) =>
        IsAlive(entity) && TryGet(entity, out PrefabInstance instance) ? _prefabs?.RefOf(instance.Prefab)?.ToString() : null;

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
    /// Writes <paramref name="root"/> and everything below it as the prefab file at <paramref name="path"/>. The ids in the file stay the same from one save to the next: an entity that came from this
    /// file (<see cref="PrefabMember"/> of it) keeps its id, and a new one takes the file's <c>nextId</c> and raises it, so an id that was deleted is never used again. The file keeps its UID
    /// (a new one gets a new UID). Every entity written is marked as a member of the file, with its id there, so that saving again keeps it. Entity fields that point outside what is saved are left out, and
    /// an instance of another prefab inside is written as plain entities.
    /// </summary>
    /// <param name="path">A path ending in <c>.aprefab</c>, or <c>uid://…</c> for the file that has that UID.</param>
    /// <param name="root">The one root entity of the file.</param>
    /// <exception cref="InvalidOperationException">The world has no <see cref="Files"/>.</exception>
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
        List<Entity> order = Subtree(root);

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

        // The entities that came from this file keep their number (the first of two with the same number does: a copy has its original's); the others get new ones.
        var ids = new int[order.Count];
        var taken = new HashSet<int>();
        for (int i = 0; i < order.Count; i++)
        {
            if (TryGet(order[i], out PrefabMember member) && member.Prefab == handle && member.Id is >= 1 and <= int.MaxValue && taken.Add((int)member.Id))
            {
                ids[i] = (int)member.Id;
                next = Math.Max(next, (long)member.Id + 1);
            }
        }

        for (int i = 0; i < order.Count; i++)
        {
            if (ids[i] == 0)
            {
                if (next > int.MaxValue)
                {
                    throw new InvalidOperationException("the prefab has used every entity id it can");
                }

                ids[i] = (int)next++;
            }
        }

        var local = new Dictionary<ulong, int>();
        for (int i = 0; i < order.Count; i++)
        {
            local[order[i].Id] = ids[i];
        }

        (List<XmlNode> top, XmlNode? schemas) = WriteEntities(order, local);
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

        // The file is there now (a new one named no file until this point): its handle is the one the entities hold.
        handle = library.Load(resolved);
        for (int i = 0; i < order.Count; i++)
        {
            if (!TryGet(order[i], out PrefabMember member) || member.Prefab == 0 || member.Prefab == handle)
            {
                Set(order[i], new PrefabMember { Id = (uint)ids[i], Prefab = handle });
            }
        }

        library.Written(handle);
    }
}
