using System;
using System.Collections.Generic;
using System.Linq;
using Weft.Ecs;
using Weft.Host.Editing;
using Weft.Reflection;
using Weft.Rendering;

namespace Weft.Editor.Inspector;

/// <summary>
/// What the inspector does to the selected entities, with no drawing: which components it shows, whether the entities disagree on a field, and the edits (a field written on every selected entity, a
/// component added, removed or reset to its default) as commands on the history. With several entities selected the inspector shows the active entity's components that <b>all</b> of them have, and
/// an edit writes only the bytes of the field that was edited, so each entity keeps its own values for the others. The panel and <see cref="InspectorView"/> call it; tests call it directly.
/// </summary>
public sealed unsafe class InspectorEditor(EditorProject project)
{
    /// <summary>The selected entities that are alive, in the order they were picked (the active one is among them).</summary>
    public IReadOnlyList<Entity> Targets => project.Selected.Items;

    /// <summary>
    /// The components of <paramref name="active"/> that every one of <paramref name="targets"/> has: what the inspector lists. With one target that is all of the active entity's components that the
    /// registry describes.
    /// </summary>
    public List<(Entity Id, ComponentInfo Info)> SharedComponents(Entity active, IReadOnlyList<Entity> targets)
    {
        World world = project.World;
        List<(Entity Id, ComponentInfo Info)> shown = world.ComponentsOf(active);
        if (targets.Count > 1)
        {
            shown.RemoveAll(component => targets.Any(other => other != active && !world.Has(other, component.Id)));
        }

        return shown;
    }

    /// <summary>
    /// Reads <paramref name="component"/> of <paramref name="active"/> into <paramref name="bytes"/> (a copy) and tells for each of its fields whether some of <paramref name="targets"/> has other bytes
    /// there (<paramref name="mixed"/>, one entry per field in order). False when the active entity no longer has the component.
    /// </summary>
    public bool Read(Entity active, IReadOnlyList<Entity> targets, Entity component, ComponentInfo info, out byte[] bytes, out bool[] mixed)
    {
        World world = project.World;
        byte* current = world.GetPointer(active, component);
        mixed = new bool[info.Fields.Count];
        if (current is null)
        {
            bytes = [];
            return false;
        }

        bytes = new ReadOnlySpan<byte>(current, info.Size).ToArray();
        foreach (Entity other in targets)
        {
            if (other == active)
            {
                continue;
            }

            byte* theirs = world.GetPointer(other, component);
            if (theirs is null)
            {
                continue;
            }

            for (int i = 0; i < mixed.Length; i++)
            {
                FieldDef field = info.Fields[i];
                int size = field.Kind.SizeOf();
                mixed[i] |= !new ReadOnlySpan<byte>(theirs + field.Offset, size).SequenceEqual(bytes.AsSpan(field.Offset, size));
            }
        }

        return true;
    }

    /// <summary>Writes <paramref name="value"/> as the bytes of <paramref name="field"/> on every target, as one step (the next edit of the same field, before <see cref="Seal"/>, joins it). False when nothing changed.</summary>
    public bool WriteField(IReadOnlyList<Entity> targets, Entity component, ComponentInfo info, FieldDef field, ReadOnlySpan<byte> value)
    {
        string name = $"Edit {info.Name}.{field.Name}";
        IEditCommand? set = FieldEditCommands.SetBytes(project.WorldRef, targets, component, field.Offset, value, name);
        if (field.Kind != FieldKind.AssetRef)
        {
            return Execute(set);
        }

        // An asset field that a loaded scene could not fill is 0 and still has the text the scene named, which a save writes again. Whoever edits the field replaces that text, so it goes in the same step
        // (and comes back with undo): clearing a broken reference has to survive a save.
        World world = project.World;
        List<IEditCommand> parts = set is null ? [] : [set];
        foreach (Entity target in targets)
        {
            if (world.IsAlive(target) && world.Has(target, component) && UnresolvedAssetCommand.Forget(project.WorldRef, target, info.Name, field.Name, name) is { } forget)
            {
                parts.Add(forget);
            }
        }

        return parts.Count switch
        {
            0 => false,
            1 => Execute(parts[0]),
            _ => Execute(new CompositeCommand(name, parts)),
        };
    }

    /// <summary>Writes several places of the component on every target as one step: what a drawer of a whole component changed. False when nothing changed.</summary>
    public bool WriteRegions(IReadOnlyList<Entity> targets, Entity component, ComponentInfo info, IReadOnlyList<(int Offset, byte[] Value)> regions) =>
        Execute(FieldEditCommands.SetRegions(project.WorldRef, targets, component, regions, $"Edit {info.Name}"));

    /// <summary>Ends the gesture: the next edit is a step of its own.</summary>
    public void Seal() => project.History.Seal();

    /// <summary>
    /// The components that can still be added: every registered one (the registry's and the world's own), left out when all targets have it, and the ones that are not for a person to add
    /// (<see cref="WorldTransform"/>, <see cref="EditorOnly"/>), matching <paramref name="filter"/> as a part of the name, in name order.
    /// </summary>
    public List<ComponentInfo> AddCandidates(IReadOnlyList<Entity> targets, string filter)
    {
        World world = project.World;
        HashSet<string>? everyoneHas = null;
        foreach (Entity target in targets)
        {
            HashSet<string> names = [.. world.ComponentsOf(target).Select(c => c.Info.Name)];
            if (everyoneHas is null)
            {
                everyoneHas = names;
            }
            else
            {
                everyoneHas.IntersectWith(names);
            }
        }

        var candidates = new SortedDictionary<string, ComponentInfo>(StringComparer.Ordinal);
        foreach (ComponentInfo info in TypeRegistry.Components.Concat(world.Components))
        {
            if (everyoneHas?.Contains(info.Name) != true && info.Name is not (nameof(WorldTransform) or nameof(EditorOnly)) && info.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                candidates[info.Name] = info;
            }
        }

        return [.. candidates.Values];
    }

    /// <summary>
    /// Adds the component to every target that does not have it, starting from <see cref="ComponentDefaults"/>, as one step. False when every target already has it.
    /// </summary>
    public bool AddComponent(IReadOnlyList<Entity> targets, ComponentInfo info)
    {
        World world = project.World;
        Entity id = world.RegisterComponent(info);
        byte[]? initial = ComponentDefaults.ValueFor(info);
        List<IEditCommand> parts = [.. targets.Where(t => !world.Has(t, id)).Select(t => (IEditCommand)new AddComponentCommand(project.WorldRef, t, id, initial))];
        return Finish(parts, $"Add {info.Name}");
    }

    /// <summary>Removes the component from every target that has it (an entity's <see cref="Transform"/> is never removed here), as one step.</summary>
    public bool RemoveComponent(IReadOnlyList<Entity> targets, Entity component, ComponentInfo info)
    {
        World world = project.World;
        if (info.Name == nameof(Transform))
        {
            return false;
        }

        List<IEditCommand> parts = [.. targets.Where(t => world.Has(t, component)).Select(t => (IEditCommand)new RemoveComponentCommand(project.WorldRef, t, component))];
        return Finish(parts, $"Remove {info.Name}");
    }

    /// <summary>Puts the default of the component (<see cref="ComponentDefaults"/>, or zeros) on every target that has it, as one step. False for a tag, or when they already have it.</summary>
    public bool ResetComponent(IReadOnlyList<Entity> targets, Entity component, ComponentInfo info)
    {
        byte[]? value = ComponentDefaults.ValueFor(info);
        if (value is null)
        {
            return false;
        }

        return Reset(FieldEditCommands.SetBytes(project.WorldRef, targets, component, 0, value, $"Reset {info.Name}"));
    }

    /// <summary>Puts the default of one field back on every target that has the component, as one step; the other fields keep their values.</summary>
    public bool ResetField(IReadOnlyList<Entity> targets, Entity component, ComponentInfo info, FieldDef field)
    {
        byte[]? value = ComponentDefaults.ValueFor(info);
        if (value is null)
        {
            return false;
        }

        return Reset(FieldEditCommands.SetBytes(project.WorldRef, targets, component, field.Offset, value.AsSpan(field.Offset, field.Kind.SizeOf()), $"Reset {info.Name}.{field.Name}"));
    }

    private bool Reset(IEditCommand? command)
    {
        bool done = Execute(command);
        Seal();
        return done;
    }

    private bool Finish(List<IEditCommand> parts, string name)
    {
        if (parts.Count == 0)
        {
            return false;
        }

        project.History.Execute(parts.Count == 1 ? parts[0] : new CompositeCommand(name, parts));
        project.History.Seal();
        return true;
    }

    private bool Execute(IEditCommand? command)
    {
        if (command is null)
        {
            return false;
        }

        project.History.Execute(command);
        return true;
    }
}
