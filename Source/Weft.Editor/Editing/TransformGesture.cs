using System;
using System.Collections.Generic;
using System.Numerics;
using Weft.Core.Numerics;
using Weft.Ecs;
using Weft.Reflection;

namespace Weft.Editor.Editing;

/// <summary>
/// One gizmo drag over a selection, with no ImGui in it: it captures the members' world placements and the pivot when it begins, and turns the pivot's new placement into the local transform of every member. Every
/// call works from the captured start, so the result does not depend on how many frames the drag took (nothing accumulates, and a frame that is skipped changes nothing).
/// </summary>
public sealed class TransformGesture
{
    private readonly World _world;
    private readonly Entity[] _members;
    private readonly Trs[] _startWorlds;
    private readonly PivotMode _mode;

    private TransformGesture(World world, Entity[] members, Trs[] startWorlds, Trs pivot, PivotMode mode)
    {
        _world = world;
        _members = members;
        _startWorlds = startWorlds;
        Pivot = pivot;
        _mode = mode;
    }

    /// <summary>The pivot when the drag began; the gizmo is drawn here.</summary>
    public Trs Pivot { get; }

    /// <summary>The entities the drag moves: the selection's top level (a branch is carried by its top) that have a <see cref="Transform"/>.</summary>
    public IReadOnlyList<Entity> Members => _members;

    /// <summary>
    /// Captures a drag over <paramref name="selected"/> (already reduced to its top level by the caller, in selection order), or null when none of them has a <see cref="Transform"/>.
    /// <paramref name="active"/> chooses the member the active pivot and the local axes come from: the active entity when it is a member, else the member that holds it (its nearest selected ancestor), else the
    /// last member. <paramref name="boundsOf"/> gives an entity's bounds in its own space (null when it has no model).
    /// </summary>
    public static TransformGesture? Begin(World world, IReadOnlyList<Entity> selected, Entity? active, PivotMode mode, bool localAxes, Func<Entity, (Vector3 Min, Vector3 Max)?> boundsOf)
    {
        var members = new List<Entity>();
        foreach (Entity entity in selected)
        {
            if (world.IsAlive(entity) && world.Has<Transform>(entity))
            {
                members.Add(entity);
            }
        }

        if (members.Count == 0)
        {
            return null;
        }

        var worlds = new Trs[members.Count];
        var boxes = new WorldBox?[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            worlds[i] = world.ComputeWorldTransform(members[i]);
            boxes[i] = boundsOf(members[i]) is { } local ? GroupTransform.BoxOf(worlds[i], local.Min, local.Max) : null;
        }

        int activeIndex = ActiveIndex(world, members, active);
        Trs pivot = GroupTransform.Pivot(worlds, boxes, activeIndex, mode, localAxes);
        return new TransformGesture(world, [.. members], worlds, pivot, mode);
    }

    private static int ActiveIndex(World world, List<Entity> members, Entity? active)
    {
        for (Entity current = active ?? default; !current.IsNull; current = world.GetParent(current))
        {
            int index = members.IndexOf(current);
            if (index >= 0)
            {
                return index;
            }
        }

        return members.Count - 1;
    }

    /// <summary>
    /// The new local <see cref="Transform"/> of every member that is still alive when the pivot is at <paramref name="pivotAfter"/>: each member's world placement from
    /// <see cref="GroupTransform.Apply"/>, taken into its parent's space (the parent is not a member, so it has not moved).
    /// </summary>
    public List<(Entity Entity, Transform Local)> Evaluate(in Trs pivotAfter)
    {
        var result = new List<(Entity, Transform)>(_members.Length);
        for (int i = 0; i < _members.Length; i++)
        {
            Entity entity = _members[i];
            if (!_world.IsAlive(entity))
            {
                continue;
            }

            Trs placed = GroupTransform.Apply(Pivot, pivotAfter, _startWorlds[i], _mode);
            Entity parent = _world.GetParent(entity);
            Trs local = parent.IsNull ? placed : placed.InLocalOf(_world.ComputeWorldTransform(parent));
            result.Add((entity, Transform.From(local)));
        }

        return result;
    }
}
