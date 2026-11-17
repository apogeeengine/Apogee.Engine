using System;
using System.Collections.Generic;
using Apogee.Ecs;
using Apogee.Reflection;

namespace Apogee.Host.Editing;

/// <summary>
/// Sets the local <see cref="Transform"/> of several entities at once: a gizmo drag over a selection. It is one step, and the frames of one drag merge into it (the command keeps the first "before" of every
/// entity and the last "after"), as <see cref="SetComponentCommand"/> does for one entity. Only a command with the same name merges: a rotation after a move is a step of its own.
/// </summary>
public sealed class SetTransformsCommand : IEditCommand
{
    private readonly WorldRef _worldRef;
    private readonly Entity[] _entities;
    private readonly Transform[] _before;
    private Transform[] _after;
    private readonly string _name;

    /// <summary>The command for each entity going from its <c>Before</c> to its <c>After</c> (the same entity at most once).</summary>
    public SetTransformsCommand(WorldRef worldRef, IReadOnlyList<(Entity Entity, Transform Before, Transform After)> changes, string name)
    {
        ArgumentNullException.ThrowIfNull(worldRef);
        ArgumentNullException.ThrowIfNull(changes);
        _worldRef = worldRef;
        _name = name;
        _entities = new Entity[changes.Count];
        _before = new Transform[changes.Count];
        _after = new Transform[changes.Count];
        var seen = new HashSet<Entity>();
        for (int i = 0; i < changes.Count; i++)
        {
            if (!seen.Add(changes[i].Entity))
            {
                throw new ArgumentException($"Entity {changes[i].Entity} is listed twice.", nameof(changes));
            }

            _entities[i] = changes[i].Entity;
            _before[i] = changes[i].Before;
            _after[i] = changes[i].After;
        }
    }

    public string Name => _name;

    /// <summary>The entities this command moves.</summary>
    public IReadOnlyList<Entity> Entities => _entities;

    /// <summary>
    /// The command for <paramref name="after"/> being set on entities as they are now (their current transform is the "before"); null when the entities already have those transforms, or none has a
    /// <see cref="Transform"/> (those are left out).
    /// </summary>
    public static SetTransformsCommand? Create(WorldRef worldRef, IEnumerable<(Entity Entity, Transform After)> after, string name)
    {
        World world = worldRef.Current;
        var changes = new List<(Entity, Transform, Transform)>();
        bool any = false;
        foreach ((Entity entity, Transform value) in after)
        {
            if (!world.TryGet(entity, out Transform current))
            {
                continue;
            }

            any |= !Same(current, value);
            changes.Add((entity, current, value));
        }

        return any ? new SetTransformsCommand(worldRef, changes, name) : null;
    }

    private static bool Same(in Transform a, in Transform b) => a.Position == b.Position && a.Rotation == b.Rotation && a.Scale == b.Scale;

    /// <summary>An entity that no longer exists (it was deleted after this step) is skipped, so undoing an older step does not bring a dead entity back as an empty shell.</summary>
    private void Write(Transform[] values)
    {
        World world = _worldRef.Current;
        for (int i = 0; i < _entities.Length; i++)
        {
            if (world.IsAlive(_entities[i]))
            {
                world.Set(_entities[i], values[i]);
            }
        }
    }

    public void Apply() => Write(_after);

    public void Undo() => Write(_before);

    public bool TryMerge(IEditCommand following)
    {
        if (following is not SetTransformsCommand other || other._entities.Length != _entities.Length || other._name != _name)
        {
            return false;
        }

        // The same set of entities, in whatever order: the value of each in the other command replaces its "after" here.
        var index = new Dictionary<Entity, int>(_entities.Length);
        for (int i = 0; i < _entities.Length; i++)
        {
            index[_entities[i]] = i;
        }

        foreach (Entity entity in other._entities)
        {
            if (!index.ContainsKey(entity))
            {
                return false;
            }
        }

        var merged = (Transform[])_after.Clone();
        for (int i = 0; i < other._entities.Length; i++)
        {
            merged[index[other._entities[i]]] = other._after[i];
        }

        _after = merged;
        return true;
    }
}
