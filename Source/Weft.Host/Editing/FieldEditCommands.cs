using System;
using System.Collections.Generic;
using Weft.Ecs;
using Weft.Reflection;

namespace Weft.Host.Editing;

/// <summary>
/// The commands of an inspector edit that reaches several entities: some bytes of a component (a field, or the whole value) written on each of them, so that the other bytes of each keep their own
/// values. One entity gives a plain <see cref="SetComponentCommand"/> (which merges with the next edit of the same component); several give a <see cref="CompositeCommand"/> of them, which merges with a
/// following one of the same targets (see <see cref="CompositeCommand.TryMerge"/>), so a drag over a field of ten entities is one step.
/// </summary>
public static class FieldEditCommands
{
    /// <summary>
    /// The command for writing <paramref name="value"/> at <paramref name="offset"/> into <paramref name="component"/> on each of <paramref name="entities"/> that has it (the others are left out). It
    /// has a part for every one of them, also those whose bytes already are the value, so that the frames of one drag all have the same targets and merge; null when no entity would change.
    /// </summary>
    public static IEditCommand? SetBytes(WorldRef worldRef, IReadOnlyList<Entity> entities, Entity component, int offset, ReadOnlySpan<byte> value, string name) =>
        SetRegions(worldRef, entities, component, [(offset, value.ToArray())], name);

    /// <summary>Like <see cref="SetBytes"/> for several places of the component at once: what a drawer of a whole component changed.</summary>
    public static IEditCommand? SetRegions(WorldRef worldRef, IReadOnlyList<Entity> entities, Entity component, IReadOnlyList<(int Offset, byte[] Value)> regions, string name)
    {
        ArgumentNullException.ThrowIfNull(worldRef);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(regions);
        World world = worldRef.Current;
        ComponentInfo info = world.GetComponentInfo(component) ?? throw new ArgumentException("not a registered component", nameof(component));
        foreach ((int offset, byte[] value) in regions)
        {
            if (offset < 0 || offset + value.Length > info.Size)
            {
                throw new ArgumentOutOfRangeException(nameof(regions), $"{value.Length} bytes at {offset} do not fit in {info.Name} ({info.Size} bytes).");
            }
        }

        var parts = new List<IEditCommand>(entities.Count);
        bool changed = false;
        foreach (Entity entity in entities)
        {
            if (!world.IsAlive(entity) || !world.Has(entity, component))
            {
                continue;
            }

            byte[] before = SetComponentCommand.Read(world, entity, component, info.Size);
            byte[] after = (byte[])before.Clone();
            foreach ((int offset, byte[] value) in regions)
            {
                value.CopyTo(after, offset);
            }

            changed |= !before.AsSpan().SequenceEqual(after);
            parts.Add(new SetComponentCommand(worldRef, entity, component, before, after, name));
        }

        if (!changed)
        {
            return null;
        }

        return parts.Count == 1 ? parts[0] : new CompositeCommand(name, parts);
    }
}
