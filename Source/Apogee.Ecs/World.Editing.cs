using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Native.Flecs;
using Apogee.Reflection;
using Flecs = Apogee.Native.Flecs.Ecs;

namespace Apogee.Ecs;

/// <summary>Marks an entity the editor made for itself (its camera, a gizmo's anchor): it is not part of the scene, so it is not listed, saved or snapshotted.</summary>
[Component]
public struct EditorOnly;

// What an editor needs of a world: the hierarchy, the components of an entity, copies, and whole-world snapshots. "User entities" are the ones the game and the editor made: not Flecs' own
// entities (modules, systems, observers, its builtin components), not the entities of registered components and their reflection data, and not the ones marked EditorOnly.
/// <summary>One entity as it was: its id (the generation too), name, parent and the bytes of its components.</summary>
internal sealed record EntityState(ulong Id, string? Name, ulong Parent, (ComponentInfo Info, byte[]? Data)[] Components);

/// <summary>
/// The user entities of a world as they were (<see cref="Snapshot"/>), or a part of them (<see cref="SnapshotTree"/>): ids, names, parents and component bytes. Restoring it brings the entities back
/// under the same ids, so a selection, an undo history or a reference between entities stays valid. A snapshot names its components by their description, not by their id in the world it
/// was taken from, so it can be restored into another world (a new session after a play run): the entities come back under the same ids there too.
/// </summary>
public sealed class WorldSnapshot
{
    internal WorldSnapshot(List<EntityState> entities) => Entities = entities;

    internal List<EntityState> Entities { get; }

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
        for (int n = 2; !IsNameFree(candidate, parent); n++)
        {
            candidate = $"{name} (copy {n})";
        }

        return candidate;
    }

    private bool IsNameFree(string name, Entity parent)
    {
        Entity found = parent.IsNull ? Lookup(name) : Lookup(GetName(parent) is null ? name : PathOf(parent) + "." + name);
        return found.IsNull;
    }

    private string PathOf(Entity entity)
    {
        var names = new Stack<string>();
        for (Entity current = entity; !current.IsNull; current = GetParent(current))
        {
            names.Push(GetName(current) ?? current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join('.', names);
    }

    /// <summary>
    /// A copy of the entity with all its components, and of everything below it, under <paramref name="parent"/> (the original's parent by default). Names get " (copy)" added so they stay unique.
    /// Returns the copy of <paramref name="source"/>.
    /// </summary>
    public Entity Clone(Entity source, Entity? parent = null)
    {
        Entity under = parent ?? GetParent(source);
        Entity copy = CloneOne(source, under);
        foreach (Entity child in Children(source))
        {
            CloneInto(child, copy);
        }

        return copy;
    }

    private void CloneInto(Entity source, Entity parent)
    {
        Entity copy = CloneOne(source, parent);
        foreach (Entity child in Children(source))
        {
            CloneInto(child, copy);
        }
    }

    private Entity CloneOne(Entity source, Entity parent)
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
            if (info.IsTag)
            {
                Add(copy, id);
            }
            else
            {
                SetPointer(copy, id, info.Size, GetPointer(source, id));
            }
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

        return new WorldSnapshot(states);
    }

    /// <summary>Takes one entity and everything below it, parents first.</summary>
    public WorldSnapshot SnapshotTree(Entity root) => new([.. Subtree(root).Select(Capture)]);

    private EntityState Capture(Entity entity)
    {
        List<(Entity Id, ComponentInfo Info)> components = ComponentsOf(entity);
        var captured = new (ComponentInfo, byte[]?)[components.Count];
        for (int i = 0; i < captured.Length; i++)
        {
            (Entity id, ComponentInfo info) = components[i];
            byte[]? data = null;
            if (!info.IsTag)
            {
                data = new byte[info.Size];
                new ReadOnlySpan<byte>(GetPointer(entity, id), info.Size).CopyTo(data);
            }

            captured[i] = (info, data);
        }

        return new EntityState(entity.Id, GetName(entity), GetParent(entity).Id, captured);
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
        }

        RestoreEntities(snapshot);
    }

    /// <summary>Brings back the entities of the snapshot under their ids, overwriting what is there, and destroys nothing: what an undo of a deletion does.</summary>
    public void RestoreEntities(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Alive again, with the names cleared first (a name has to be unique among siblings, and two entities may have swapped theirs).
        foreach (EntityState state in snapshot.Entities)
        {
            var entity = new Entity(state.Id);
            if (!IsAlive(entity))
            {
                Flecs.MakeAlive(Native, state.Id);
            }

            Flecs.RemoveId(Native, state.Id, Flecs.MakePair(Flecs.IdentifierId, Flecs.Name));
        }

        foreach (EntityState state in snapshot.Entities)
        {
            var entity = new Entity(state.Id);
            var wanted = new HashSet<Entity>();
            foreach ((ComponentInfo info, byte[]? data) in state.Components)
            {
                Entity id = RegisterComponent(info);   // the same id when this world has it, a new component of the same layout when it has not
                wanted.Add(id);
                if (data is null)
                {
                    Add(entity, id);
                }
                else
                {
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
