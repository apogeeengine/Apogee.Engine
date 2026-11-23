using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Ecs;
using Apogee.Reflection;

namespace Apogee.Host.Editing;

/// <summary>
/// An edit that changes an entity and what is below it, whatever it makes or destroys there: reverting what an instance changed, unlinking it from its prefab, making a prefab of entities. The command
/// takes the branch as it was and as it is after <paramref name="change"/>, and undoing destroys what the change made and brings back what it destroyed (under the ids they had, so the selection and the
/// other steps of the history still point at them); redoing does the reverse. The fields of entities outside the branch that named something the change destroyed are put back too.
/// </summary>
/// <remarks>
/// It undoes what happened to <em>entities</em>. A file that <paramref name="change"/> wrote (<see cref="PrefabCommands.CreatePrefab"/> makes one) stays: the prefab is an asset of the game and is not
/// deleted by an undo. Applying overrides to a prefab (<c>World.ApplyOverrides</c>) is not made a command for that reason: it writes the user's file, which a step of the history does not take back; a tool that
/// applies clears (or seals) its history, as it does when a prefab changes under it (<see cref="PrefabUpdate"/>).
/// </remarks>
public sealed unsafe class SubtreeEditCommand(WorldRef worldRef, string name, Entity root, Action<World> change) : IEditCommand
{
    // The bytes of one component of an entity outside the branch, which names an entity of it.
    private sealed record Outside(Entity Owner, Entity Component, byte[] Before, byte[] After);

    private World world => worldRef.Current;

    private WorldSnapshot? _before;
    private WorldSnapshot? _after;
    private List<Entity> _made = [];
    private List<Entity> _lost = [];
    private List<Outside> _outside = [];

    /// <inheritdoc/>
    public string Name => name;

    /// <summary>The entity the change is about (it stays alive: a change that destroys it is not one of these).</summary>
    public Entity Root => root;

    /// <inheritdoc/>
    public void Apply()
    {
        World world = this.world;
        if (_before is null)
        {
            List<Entity> branch = world.Subtree(root);
            _before = world.SnapshotTree(root);
            _outside = Referrers(world, branch);
            change(world);
            if (!world.IsAlive(root))
            {
                throw new InvalidOperationException($"{name} destroyed the entity it is about: it cannot be undone");
            }

            _after = world.SnapshotTree(root);
            var after = new HashSet<Entity>(world.Subtree(root));
            var before = new HashSet<Entity>(branch);
            _made = [.. after.Where(entity => !before.Contains(entity))];
            _lost = [.. branch.Where(entity => !after.Contains(entity))];
            _outside = [.. _outside.Where(entry => world.IsAlive(entry.Owner)).Select(entry => entry with { After = Read(world, entry.Owner, entry.Component, entry.Before.Length) })];
            return;
        }

        // Redone: what the change destroyed goes again, and what it made comes back under its ids.
        foreach (Entity gone in _lost)
        {
            if (world.IsAlive(gone))
            {
                world.Destroy(gone);
            }
        }

        world.RestoreBranch(_after!);
        Write(world, _outside, static entry => entry.After);
    }

    /// <inheritdoc/>
    public void Undo()
    {
        World world = this.world;
        foreach (Entity made in _made)
        {
            if (world.IsAlive(made))
            {
                world.Destroy(made);
            }
        }

        world.RestoreBranch(_before!);
        Write(world, _outside, static entry => entry.Before);
    }

    /// <inheritdoc/>
    public bool TryMerge(IEditCommand following) => false;

    // The components outside the branch with an Entity field that names an entity of it, as they are now.
    private static List<Outside> Referrers(World world, List<Entity> branch)
    {
        var inside = new HashSet<ulong>(branch.Select(static entity => entity.Id));
        var found = new List<Outside>();
        foreach (Entity entity in world.UserEntities())
        {
            if (inside.Contains(entity.Id))
            {
                continue;
            }

            foreach ((Entity id, ComponentInfo info) in world.ComponentsOf(entity))
            {
                if (info.IsTag)
                {
                    continue;
                }

                byte* data = world.GetPointer(entity, id);
                if (data is not null && info.Fields.Any(field => field.Kind == FieldKind.Entity && inside.Contains(*(ulong*)(data + field.Offset))))
                {
                    byte[] bytes = Read(world, entity, id, info.Size);
                    found.Add(new Outside(entity, id, bytes, bytes));
                }
            }
        }

        return found;
    }

    private static byte[] Read(World world, Entity entity, Entity component, int size)
    {
        byte[] bytes = new byte[size];
        byte* data = world.GetPointer(entity, component);
        if (data is not null)
        {
            new ReadOnlySpan<byte>(data, size).CopyTo(bytes);
        }

        return bytes;
    }

    private static void Write(World world, List<Outside> entries, Func<Outside, byte[]> pick)
    {
        foreach (Outside entry in entries)
        {
            if (!world.IsAlive(entry.Owner))
            {
                continue;
            }

            byte[] bytes = pick(entry);
            fixed (byte* data = bytes)
            {
                world.SetPointer(entry.Owner, entry.Component, bytes.Length, data);
            }
        }
    }
}

/// <summary>Makes an instance of a prefab as one step: undoing destroys it, redoing brings it back under the ids it had.</summary>
public sealed class InstantiatePrefabCommand(WorldRef worldRef, string reference, Entity parent = default) : IEditCommand
{
    private readonly CreateEntitiesCommand _create = new(worldRef, "Instantiate " + reference, () => [worldRef.Current.Instantiate(reference, parent)]);

    /// <inheritdoc/>
    public string Name => _create.Name;

    /// <summary>The root of the instance (set by the first <see cref="Apply"/>).</summary>
    public Entity Instance => _create.Entities.Count > 0 ? _create.Entities[0] : default;

    /// <inheritdoc/>
    public void Apply() => _create.Apply();

    /// <inheritdoc/>
    public void Undo() => _create.Undo();

    /// <inheritdoc/>
    public bool TryMerge(IEditCommand following) => false;
}

/// <summary>The edits of the overrides API (<c>World.PrefabOverrides</c> and what follows it) as commands of an <see cref="EditHistory"/>.</summary>
public static class PrefabCommands
{
    /// <summary>Puts the given overrides of an instance back to what its prefab says (<see cref="World.RevertOverrides"/>).</summary>
    public static IEditCommand Revert(WorldRef worldRef, Entity root, IEnumerable<PrefabOverride> overrides)
    {
        PrefabOverride[] chosen = [.. overrides];
        return new SubtreeEditCommand(worldRef, chosen.Length == 1 ? "Revert Override" : "Revert Overrides", root, world => world.RevertOverrides(root, chosen));
    }

    /// <summary>Puts everything the instance changed back to what its prefab says (<see cref="World.RevertAllOverrides"/>).</summary>
    public static IEditCommand RevertAll(WorldRef worldRef, Entity root) =>
        new SubtreeEditCommand(worldRef, "Revert All Overrides", root, world => world.RevertAllOverrides(root));

    /// <summary>Turns an instance into plain entities (<see cref="World.BreakPrefabLink"/>).</summary>
    public static IEditCommand BreakLink(WorldRef worldRef, Entity root, bool completely = false) =>
        new SubtreeEditCommand(worldRef, completely ? "Unpack Completely" : "Unpack Prefab", root, world => world.BreakPrefabLink(root, completely));

    /// <summary>Makes a new prefab file of the entities and an instance of it (<see cref="World.CreatePrefab"/>). Undoing makes them plain entities again and leaves the file.</summary>
    public static IEditCommand CreatePrefab(WorldRef worldRef, Entity root, string path) =>
        new SubtreeEditCommand(worldRef, "Create Prefab", root, world => world.CreatePrefab(root, path));

    /// <summary>Makes an instance of a prefab under <paramref name="parent"/> (the top level when none).</summary>
    public static InstantiatePrefabCommand Instantiate(WorldRef worldRef, string reference, Entity parent = default) => new(worldRef, reference, parent);
}
