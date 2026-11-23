using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Content.Xml;
using Apogee.Core.Assets;
using Apogee.Core.Logging;
using Apogee.Interop;
using Apogee.Native.Flecs;
using Apogee.Reflection;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Ecs;

/// <summary>Marks an entity the editor made for itself (its camera, a gizmo's anchor): it is not part of the scene, so it is not listed, saved or snapshotted.</summary>
[Component]
public struct EditorOnly;

// What an editor needs of a world: the hierarchy, the components of an entity, copies, and whole-world snapshots. "User entities" are the ones the game and the editor made: not Flecs' own
// entities (modules, systems, observers, its builtin components), not the entities of registered components and their reflection data, and not the ones marked EditorOnly.
/// <summary>
/// One component of an entity as it was: its bytes (null for a tag) and, for each of its asset fields (<see cref="FieldKind.AssetRef"/>, in field order), the <see cref="AssetRef"/> the handle named, or
/// null when it named nothing or nothing that can be saved. A handle only means something in the world (and run) it came from; the reference is what another world can use.
/// </summary>
internal readonly record struct CapturedComponent(ComponentInfo Info, byte[]? Data, AssetRef?[]? Assets);

/// <summary>
/// One entity as it was: its id (the generation too), name, parent and its components, and, for the root of a prefab instance, what its element held that could not be applied (kept as written, never
/// changed: the nodes may be shared with the world and other snapshots).
/// </summary>
internal sealed record EntityState(ulong Id, string? Name, ulong Parent, CapturedComponent[] Components, KeptData? Kept = null);

/// <summary>
/// The user entities of a world as they were (<see cref="Snapshot"/>), or a part of them (<see cref="SnapshotTree"/>): ids, names, parents and component bytes. Restoring it brings the entities back
/// under the same ids, so a selection, an undo history or a reference between entities stays valid. A snapshot names its components by their description, not by their id in the world it
/// was taken from, so it can be restored into another world (a new session after a play run): the entities come back under the same ids there too. Fields that hold an asset (a model of a
/// <c>ModelRenderer</c>) are kept as the <see cref="AssetRef"/> they named: restored into the world they came from they are the same handles, into another world they are that world's handles for the same
/// assets.
/// </summary>
public sealed class WorldSnapshot
{
    internal WorldSnapshot(List<EntityState> entities, object origin)
    {
        Entities = entities;
        Origin = origin;
    }

    internal List<EntityState> Entities { get; }

    /// <summary>For each prefab the entities hold instances or members of (by its reference text), the document those entities were made from; null where there was none. What <see cref="World.Restore"/> compares with what the library has now.</summary>
    internal IReadOnlyDictionary<string, XmlNode?>? Documents { get; init; }

    /// <summary>The numbers that updates of prefab instances had used in the world this was taken from (see <c>World.CaptureCounter</c>): kept-aside data in it may hold them.</summary>
    internal int Numbered { get; init; }

    /// <summary>Identifies the world the snapshot was taken from (see <c>World._origin</c>).</summary>
    internal object Origin { get; }

    /// <summary>The ids of the entities in it.</summary>
    public IEnumerable<Entity> Ids => Entities.Select(e => new Entity(e.Id));

    public int Count => Entities.Count;
}

public sealed unsafe partial class World
{
    /// <summary>The components this world has registered (the structs of the registry that were used, and the ones scripts defined), in name order.</summary>
    public IReadOnlyList<ComponentInfo> Components => [.. _infoById.Values.OrderBy(c => c.Name, StringComparer.Ordinal)];

    /// <summary>The component id of <paramref name="info"/> in this world, when it has been registered.</summary>
    public Entity ComponentId(ComponentInfo info) => _componentsByName.GetValueOrDefault(info.Name);

    /// <summary>
    /// Whether the entity is one the game or an editor made (see above): alive, not Flecs' own, not a component or the data that describes one, not a system or an observer, not marked
    /// <see cref="EditorOnly"/>, and not under such an entity.
    /// </summary>
    public bool IsUserEntity(Entity entity)
    {
        if (entity.IsNull || (entity.Id & 0xFFFFFFFFul) < Flecs.FirstUserEntityId || !IsAlive(entity))
        {
            return false;
        }

        ulong id = entity.Id;
        if (_infoById.ContainsKey(entity)
            || Flecs.HasId(Native, id, Flecs.ComponentId) || Flecs.HasId(Native, id, Flecs.System) || Flecs.HasId(Native, id, Flecs.Observer) || Flecs.HasId(Native, id, Flecs.Module)
            || Flecs.HasId(Native, id, _editorOnly.Id))
        {
            return false;
        }

        // Flecs makes entities under a component for its fields (the reflection data) and under a module for what the module has: not the user's.
        Entity parent = GetParent(entity);
        return parent.IsNull || IsUserEntity(parent);
    }

    /// <summary>The children of an entity in creation order (the order of their ids), user entities only.</summary>
    [Method]
    public List<Entity> Children(Entity parent)
    {
        var children = new List<Entity>();
        if (IsAlive(parent))
        {
            Iter it = Flecs.Children(Native, parent.Id);
            while (Flecs.ChildrenNext(&it))
            {
                for (int i = 0; i < it.Count; i++)
                {
                    var child = new Entity(it.Entities[i]);
                    if (IsUserEntity(child))
                    {
                        children.Add(child);
                    }
                }
            }
        }

        children.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return children;
    }

    /// <summary>The user entities that have no parent, in creation order.</summary>
    [Method]
    public List<Entity> Roots()
    {
        var roots = new List<Entity>();
        Entities all = Flecs.GetEntities(Native);
        for (int i = 0; i < all.AliveCount; i++)
        {
            var entity = new Entity(all.Ids[i]);
            if (IsUserEntity(entity) && GetParent(entity).IsNull)
            {
                roots.Add(entity);
            }
        }

        roots.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return roots;
    }

    /// <summary>Every user entity, in creation order (the order of their ids): what an editor searches when it looks for the one under the pointer.</summary>
    public List<Entity> UserEntities()
    {
        var found = new List<Entity>();
        Entities all = Flecs.GetEntities(Native);
        for (int i = 0; i < all.AliveCount; i++)
        {
            var entity = new Entity(all.Ids[i]);
            if (IsUserEntity(entity))
            {
                found.Add(entity);
            }
        }

        found.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return found;
    }

    /// <summary>The entity and everything below it, parents before children.</summary>
    public List<Entity> Subtree(Entity root)
    {
        var all = new List<Entity>();
        var pending = new Stack<Entity>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            Entity current = pending.Pop();
            all.Add(current);
            List<Entity> children = Children(current);
            for (int i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }

        return all;
    }

    /// <summary>The components (and tags) the entity has that the registry describes, with their descriptions: what an inspector lists. Relationship pairs and Flecs' own are left out.</summary>
    public List<(Entity Id, ComponentInfo Info)> ComponentsOf(Entity entity)
    {
        var found = new List<(Entity, ComponentInfo)>();
        if (!IsAlive(entity))
        {
            return found;
        }

        Apogee.Native.Flecs.Type* type = Flecs.GetType(Native, entity.Id);
        if (type is null)
        {
            return found;
        }

        for (int i = 0; i < type->Count; i++)
        {
            var id = new Entity(type->Array[i]);
            if (_infoById.TryGetValue(id, out ComponentInfo? info))
            {
                found.Add((id, info));
            }
        }

        return found;
    }

    /// <summary>A name for a copy: "name (copy)", and "name (copy 2)" when that is taken among the new parent's children.</summary>
    private string? CopyName(Entity source, Entity parent)
    {
        string? name = GetName(source);
        if (name is null)
        {
            return null;
        }

        string candidate = name + " (copy)";
        for (int n = 2; IsNameTaken(candidate, parent, default); n++)
        {
            candidate = $"{name} (copy {n})";
        }

        return candidate;
    }

    // ---- Names ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The name, or "name 2", "name 3"… (the first of those that is free) when <paramref name="parent"/> (the top level when none) already has a child of that name other than
    /// <paramref name="except"/>: what <see cref="SetName"/> gives an entity, so that a name is unique among siblings. It looks at the children of that one parent, as Flecs keeps them: not at the
    /// whole world, so the entities of the engine (there is one called "Empty") do not count. Names are compared as they are written: "Lamp" and "lamp" are two names.
    /// </summary>
    /// <param name="name">The name wanted. An empty one is no name, and is returned as it is.</param>
    /// <param name="parent">The parent the entity has or will have; none for the top level.</param>
    /// <param name="except">The entity that is being named, whose own current name does not count.</param>
    public string UniqueName(string name, Entity parent = default, Entity except = default) => UniqueName(name, parent, except, null);

    internal string UniqueName(string name, Entity parent, Entity except, NameHints? hints)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || !IsNameTaken(name, parent, except))
        {
            return name;
        }

        // Within one load nothing is freed: the suffixes already given for this name under this parent are still taken, and the search goes on after them (many entities of one name are not quadratic).
        int n = hints?.Next(parent, name) ?? 2;
        string candidate = $"{name} {n}";
        while (IsNameTaken(candidate, parent, except))
        {
            n++;
            candidate = $"{name} {n}";
        }

        hints?.Used(parent, name, n);
        return candidate;
    }

    // Whether a child of the parent (a top-level entity when none) has the name, apart from `except`. Any entity counts, an editor's helper or one of the engine's own that Flecs keeps at the top level
    // (a component, a system) as much as a user's: Flecs keeps one entity of a name in a scope, and a second one would take the first's place in its index and leave it unfound.
    private bool IsNameTaken(string name, Entity parent, Entity except)
    {
        Entity found = FindChildNamed(parent, name);
        return !found.IsNull && found != except;
    }

    // The child of the parent (the top level when none) that has the name, from the name index Flecs keeps for that one scope: not ecs_lookup, which also searches the engine's modules (where "Empty"
    // is) and reads a path.
    private Entity FindChildNamed(Entity parent, string name)
    {
        if (name.Length > 0 && name[0] == '#' && name.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            // Flecs reads such a name as an entity id: the siblings are looked at one by one.
            foreach (Entity sibling in parent.IsNull ? Roots() : Children(parent))
            {
                if (GetName(sibling) == name)
                {
                    return sibling;
                }
            }

            return default;
        }

        fixed (byte* text = Utf8.ToZeroTerminated(name))
        {
            return new Entity(Flecs.LookupChild(Native, parent.Id, text));
        }
    }

    /// <summary>
    /// A copy of the entity with all its components, and of everything below it, under <paramref name="parent"/> (the original's parent by default). Names get " (copy)" added so they stay unique.
    /// Returns the copy of <paramref name="source"/>.
    /// </summary>
    public Entity Clone(Entity source, Entity? parent = null)
    {
        Entity under = parent ?? GetParent(source);

        // A member's id means something only inside the instance it is a member of: a copy keeps it only when that instance is copied too.
        var branch = new HashSet<ulong>(Subtree(source).Select(entity => entity.Id));
        Entity copy = CloneOne(source, under, branch);
        foreach (Entity child in Children(source))
        {
            CloneInto(child, copy, branch);
        }

        return copy;
    }

    private void CloneInto(Entity source, Entity parent, HashSet<ulong> branch)
    {
        Entity copy = CloneOne(source, parent, branch);
        foreach (Entity child in Children(source))
        {
            CloneInto(child, copy, branch);
        }
    }

    private Entity CloneOne(Entity source, Entity parent, HashSet<ulong> branch)
    {
        string? name = CopyName(source, parent);
        Entity copy = CreateEntity(name: null);
        if (!parent.IsNull)
        {
            SetParent(copy, parent);
        }

        if (name is not null)
        {
            SetName(copy, name);
        }

        foreach ((Entity id, ComponentInfo info) in ComponentsOf(source))
        {
            if (info.Name == nameof(PrefabMember) && !(TryGet(source, out PrefabMember member) && OwnerOf(source, member.Prefab) is { IsNull: false } owner && branch.Contains(owner.Id)))
            {
                continue; // a copy of a member whose instance is not copied: a plain entity
            }

            if (info.IsTag)
            {
                Add(copy, id);
            }
            else
            {
                // The bytes are taken out of the source's table first: setting them on the copy changes tables (the copy may join the source's, which grows and moves its memory), and a pointer into one
                // would read what is no longer there.
                byte[] bytes = new byte[info.Size];
                new ReadOnlySpan<byte>(GetPointer(source, id), info.Size).CopyTo(bytes);
                fixed (byte* data = bytes)
                {
                    SetPointer(copy, id, info.Size, data);
                }
            }
        }

        if (_kept.TryGetValue(source.Id, out KeptData? kept))
        {
            _kept[copy.Id] = kept; // never changed: shared
        }

        return copy;
    }

    // ---- Snapshots ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Takes every user entity (<see cref="IsUserEntity"/>) as it is now.</summary>
    public WorldSnapshot Snapshot()
    {
        var states = new List<EntityState>();
        Entities all = Flecs.GetEntities(Native);
        var ids = new List<Entity>();
        for (int i = 0; i < all.AliveCount; i++)
        {
            var entity = new Entity(all.Ids[i]);
            if (IsUserEntity(entity))
            {
                ids.Add(entity);
            }
        }

        ids.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        foreach (Entity entity in ids)
        {
            states.Add(Capture(entity));
        }

        return new WorldSnapshot(states, _origin) { Documents = DocumentsOf(ids), Numbered = _numbered };
    }

    /// <summary>Takes one entity and everything below it, parents first.</summary>
    public WorldSnapshot SnapshotTree(Entity root)
    {
        List<Entity> tree = Subtree(root);
        return new WorldSnapshot([.. tree.Select(Capture)], _origin) { Documents = DocumentsOf(tree), Numbered = _numbered };
    }

    private EntityState Capture(Entity entity)
    {
        List<(Entity Id, ComponentInfo Info)> components = ComponentsOf(entity);
        var captured = new CapturedComponent[components.Count];
        for (int i = 0; i < captured.Length; i++)
        {
            (Entity id, ComponentInfo info) = components[i];
            byte[]? data = null;
            if (!info.IsTag)
            {
                data = new byte[info.Size];
                new ReadOnlySpan<byte>(GetPointer(entity, id), info.Size).CopyTo(data);
            }

            captured[i] = new CapturedComponent(info, data, data is null ? null : CaptureAssets(info, data));
        }

        return new EntityState(entity.Id, GetName(entity), GetParent(entity).Id, captured, _kept.GetValueOrDefault(entity.Id));
    }

    // The references the asset fields of one component's bytes name (null when it has none of those fields), for restoring into another world.
    private AssetRef?[]? CaptureAssets(ComponentInfo info, byte[] data)
    {
        AssetRef?[]? refs = null;
        int next = 0;
        foreach (FieldDef field in info.Fields)
        {
            if (field.Kind != FieldKind.AssetRef)
            {
                continue;
            }

            refs ??= new AssetRef?[info.Fields.Count(f => f.Kind == FieldKind.AssetRef)];
            uint handle = BitConverter.ToUInt32(data, field.Offset);
            refs[next++] = handle == 0 ? null : QuietAssetRefOf(field, handle);
        }

        return refs;
    }

    /// <summary>
    /// Puts the world back as the snapshot says: user entities that are not in it are destroyed, the ones that are come back under their ids (made alive again when they were destroyed) with their
    /// components, names and parents as they were.
    /// </summary>
    public void Restore(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var wanted = new HashSet<ulong>(snapshot.Entities.Select(e => e.Id));
        Entities all = Flecs.GetEntities(Native);
        var extras = new List<Entity>();
        for (int i = 0; i < all.AliveCount; i++)
        {
            var entity = new Entity(all.Ids[i]);
            if (!wanted.Contains(entity.Id) && IsUserEntity(entity))
            {
                extras.Add(entity);
            }
        }

        foreach (Entity extra in extras)
        {
            if (IsAlive(extra))
            {
                Destroy(extra);
            }

            _kept.Remove(extra.Id);
        }

        RestoreEntities(snapshot);
        QueueUpdatesForRestored(snapshot);
    }

    // Sets each asset field of a component's bytes to this world's handle for the reference it named; 0 when there is none to name (said once per field) or this world cannot find it.
    private void RebindAssets(ComponentInfo info, byte[] data, AssetRef?[] assets)
    {
        int next = 0;
        foreach (FieldDef field in info.Fields)
        {
            if (field.Kind != FieldKind.AssetRef)
            {
                continue;
            }

            AssetRef? named = assets[next++];
            uint handle = BitConverter.ToUInt32(data, field.Offset);
            uint rebound = 0;
            if (named is { } reference)
            {
                rebound = HandleOfRef(info, field, reference);
            }
            else if (handle != 0)
            {
                // It named something in the world it came from, but nothing that can be named in another one.
                if (_assetTypesWarned.Add($"restore:{info.Name}.{field.Name}"))
                {
                    Log.Warn("Scene", $"{info.Name}.{field.Name} named an asset with no path or UID (handle {handle}), which another world cannot find: left empty");
                }
            }

            BitConverter.TryWriteBytes(data.AsSpan(field.Offset, sizeof(uint)), rebound);
        }
    }

    /// <summary>
    /// Brings back the entities of the snapshot under their ids, overwriting what is there, and destroys nothing: what an undo of a deletion does. A snapshot of another world comes back with
    /// this world's handles for the assets it named.
    /// </summary>
    public void RestoreEntities(WorldSnapshot snapshot) => RestoreEntitiesCore(snapshot);

    /// <summary>
    /// <see cref="RestoreEntities"/> for a branch that is brought back after prefabs may have changed (an undo or a redo of an edit of an instance): the instances in it were made from the documents the
    /// snapshot recorded, and when a prefab is not the same now, <see cref="ApplyPrefabUpdates"/> has an update to make for them, as after <see cref="Restore"/>.
    /// </summary>
    public void RestoreBranch(WorldSnapshot snapshot)
    {
        RestoreEntitiesCore(snapshot);
        QueueUpdatesForRestored(snapshot);
    }

    private void RestoreEntitiesCore(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Alive again, with the names cleared first (a name has to be unique among siblings, and two entities may have swapped theirs) and taken out of their parents: the parents are set one by one after, and
        // two entities that swapped places (A under B before, B under A in the snapshot) would be each other's parent for a moment, which Flecs does not survive.
        foreach (EntityState state in snapshot.Entities)
        {
            var entity = new Entity(state.Id);
            if (!IsAlive(entity))
            {
                Flecs.MakeAlive(Native, state.Id);
            }

            Flecs.RemoveId(Native, state.Id, Flecs.MakePair(Flecs.IdentifierId, Flecs.Name));
            if (GetParent(entity) is { IsNull: false } current)
            {
                Flecs.RemoveId(Native, state.Id, Flecs.MakePair(Flecs.ChildOf, current.Id));
            }
        }

        bool sameWorld = ReferenceEquals(snapshot.Origin, _origin);
        foreach (EntityState state in snapshot.Entities)
        {
            var entity = new Entity(state.Id);
            var wanted = new HashSet<Entity>();
            foreach (CapturedComponent captured in state.Components)
            {
                ComponentInfo info = captured.Info;
                byte[]? data = captured.Data;
                Entity id = RegisterComponent(info);   // the same id when this world has it, a new component of the same layout when it has not
                wanted.Add(id);
                if (data is null)
                {
                    Add(entity, id);
                }
                else
                {
                    // Another world has other handles: its own for the assets the fields named (the snapshot keeps its bytes, so a copy is changed).
                    if (!sameWorld && captured.Assets is not null)
                    {
                        data = (byte[])data.Clone();
                        RebindAssets(info, data, captured.Assets);
                    }

                    fixed (byte* bytes = data)
                    {
                        SetPointer(entity, id, data.Length, bytes);
                    }
                }
            }

            foreach ((Entity id, _) in ComponentsOf(entity))
            {
                if (!wanted.Contains(id))
                {
                    Remove(entity, id);
                }
            }

            if (state.Kept is { } kept)
            {
                _kept[state.Id] = kept;
            }
            else
            {
                _kept.Remove(state.Id);
            }
        }

        foreach (EntityState state in snapshot.Entities)
        {
            SetParent(new Entity(state.Id), new Entity(state.Parent));
        }

        foreach (EntityState state in snapshot.Entities)
        {
            if (state.Name is not null)
            {
                SetName(new Entity(state.Id), state.Name);
            }
        }
    }
}
