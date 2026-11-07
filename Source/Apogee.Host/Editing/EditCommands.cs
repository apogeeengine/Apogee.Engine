using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Core.Numerics;
using Apogee.Ecs;
using Apogee.Reflection;

namespace Apogee.Host.Editing;

/// <summary>
/// The world a command acts on, looked up each time it runs: an editor replaces its world (a play run, then back to editing) and the history has to follow it. A <see cref="World"/> converts to one
/// that is always that world.
/// </summary>
public sealed class WorldRef(Func<World> get)
{
    public World Current => get();

    public static implicit operator WorldRef(World world) => new(() => world);
}

// The edits an editor makes to a world, as commands. Each keeps what it needs to undo itself: component bytes before and after, or a snapshot of an entity and its branch (which brings it back under
// the same id, so the selection and the other commands in the history still point at it).

/// <summary>Several commands that are one step.</summary>
public sealed class CompositeCommand(string name, IReadOnlyList<IEditCommand> commands) : IEditCommand
{
    public string Name => name;

    public void Apply()
    {
        foreach (IEditCommand command in commands)
        {
            command.Apply();
        }
    }

    public void Undo()
    {
        for (int i = commands.Count - 1; i >= 0; i--)
        {
            commands[i].Undo();
        }
    }

    public bool TryMerge(IEditCommand following) => false;
}

/// <summary>Changes the value of a component on an entity (an inspector field). The changes of one gesture merge: the command keeps the first "before" and the last "after".</summary>
public sealed class SetComponentCommand(WorldRef worldRef, Entity entity, Entity component, byte[] before, byte[] after, string name) : IEditCommand
{
    private World world => worldRef.Current;

    private byte[] _after = after;

    public string Name => name;

    /// <summary>The command for what is there now becoming <paramref name="after"/>; null when nothing changed.</summary>
    public static SetComponentCommand? Create(WorldRef worldRef, Entity entity, Entity component, ReadOnlySpan<byte> after, string name)
    {
        World world = worldRef.Current;
        ComponentInfo info = world.GetComponentInfo(component) ?? throw new ArgumentException("not a registered component", nameof(component));
        byte[] before = Read(world, entity, component, info.Size);
        return before.AsSpan().SequenceEqual(after) ? null : new SetComponentCommand(worldRef, entity, component, before, after.ToArray(), name);
    }

    internal static unsafe byte[] Read(World world, Entity entity, Entity component, int size)
    {
        byte[] bytes = new byte[size];
        byte* data = world.GetPointer(entity, component);
        if (data is not null)
        {
            new ReadOnlySpan<byte>(data, size).CopyTo(bytes);
        }

        return bytes;
    }

    private unsafe void Write(byte[] bytes)
    {
        fixed (byte* data = bytes)
        {
            world.SetPointer(entity, component, bytes.Length, data);
        }
    }

    public void Apply() => Write(_after);

    public void Undo() => Write(before);

    public bool TryMerge(IEditCommand following)
    {
        if (following is SetComponentCommand other && other.Targets(entity, component))
        {
            _after = other._after;
            return true;
        }

        return false;
    }

    private bool Targets(Entity otherEntity, Entity otherComponent) => entity == otherEntity && component == otherComponent;
}

/// <summary>Adds a component (zeroed, or with the given initial bytes; a tag has none).</summary>
public sealed class AddComponentCommand(WorldRef worldRef, Entity entity, Entity component, byte[]? initial = null) : IEditCommand
{
    private World world => worldRef.Current;

    public string Name => $"Add {world.GetComponentInfo(component)?.Name}";

    public unsafe void Apply()
    {
        ComponentInfo info = world.GetComponentInfo(component)!;
        if (info.IsTag)
        {
            world.Add(entity, component);
        }
        else
        {
            byte[] bytes = initial ?? new byte[info.Size];
            fixed (byte* data = bytes)
            {
                world.SetPointer(entity, component, info.Size, data);
            }
        }
    }

    public void Undo() => world.Remove(entity, component);

    public bool TryMerge(IEditCommand following) => false;
}

/// <summary>Removes a component, keeping its value for the undo.</summary>
public sealed class RemoveComponentCommand(WorldRef worldRef, Entity entity, Entity component) : IEditCommand
{
    private World world => worldRef.Current;

    private byte[]? _value;

    public string Name => $"Remove {world.GetComponentInfo(component)?.Name}";

    public unsafe void Apply()
    {
        ComponentInfo info = world.GetComponentInfo(component)!;
        _value = info.IsTag ? null : SetComponentCommand.Read(world, entity, component, info.Size);
        world.Remove(entity, component);
    }

    public unsafe void Undo()
    {
        ComponentInfo info = world.GetComponentInfo(component)!;
        if (_value is null)
        {
            world.Add(entity, component);
            return;
        }

        fixed (byte* data = _value)
        {
            world.SetPointer(entity, component, info.Size, data);
        }
    }

    public bool TryMerge(IEditCommand following) => false;
}

/// <summary>
/// Makes an entity (with <paramref name="create"/>: a new one, a clone, a model dropped into the viewport) and removes it again on undo. Redoing brings it back under the id it had.
/// </summary>
public sealed class CreateEntityCommand(WorldRef worldRef, string name, Func<Entity> create) : IEditCommand
{
    private World world => worldRef.Current;

    private WorldSnapshot? _made;

    /// <summary>The entity this made (set by the first <see cref="Apply"/>).</summary>
    public Entity Entity { get; private set; }

    public string Name => name;

    public void Apply()
    {
        if (_made is null)
        {
            Entity = create();
            _made = world.SnapshotTree(Entity);
        }
        else
        {
            world.RestoreEntities(_made);
        }
    }

    public void Undo()
    {
        if (world.IsAlive(Entity))
        {
            world.Destroy(Entity);
        }
    }

    public bool TryMerge(IEditCommand following) => false;
}

/// <summary>Deletes an entity and its branch, and brings them back under the same ids on undo.</summary>
public sealed class DestroyEntityCommand(WorldRef worldRef, Entity entity) : IEditCommand
{
    private World world => worldRef.Current;

    private readonly string _name = $"Delete {worldRef.Current.GetName(entity) ?? "entity"}";
    private WorldSnapshot? _branch;

    public string Name => _name;

    public void Apply()
    {
        _branch ??= world.SnapshotTree(entity);
        if (world.IsAlive(entity))
        {
            world.Destroy(entity);
        }
    }

    public void Undo() => world.RestoreEntities(_branch!);

    public bool TryMerge(IEditCommand following) => false;
}

/// <summary>Gives an entity a new parent (none: a root), keeping it where it is in the world by default.</summary>
public sealed class ReparentCommand(WorldRef worldRef, Entity entity, Entity newParent, bool keepWorldTransform = true) : IEditCommand
{
    private World world => worldRef.Current;

    private Entity _oldParent;
    private byte[]? _oldTransform;
    private byte[]? _newTransform;
    private bool _first = true;
    private readonly string _name = $"Move {worldRef.Current.GetName(entity) ?? "entity"}";

    public string Name => _name;

    /// <summary>Whether <paramref name="entity"/> may go under <paramref name="parent"/>: not under itself or one of its own descendants, and the parent has to be a user entity (or none).</summary>
    public static bool IsAllowed(World world, Entity entity, Entity parent)
    {
        if (parent.IsNull)
        {
            return true;
        }

        if (!world.IsUserEntity(parent))
        {
            return false;
        }

        for (Entity current = parent; !current.IsNull; current = world.GetParent(current))
        {
            if (current == entity)
            {
                return false;
            }
        }

        return true;
    }

    public unsafe void Apply()
    {
        if (!IsAllowed(world, entity, newParent))
        {
            throw new InvalidOperationException("An entity cannot become a child of itself or of one of its descendants.");
        }

        Entity transformId = world.Component<Transform>();
        if (_first)
        {
            _first = false;
            _oldParent = world.GetParent(entity);
            _oldTransform = world.Has<Transform>(entity) ? SetComponentCommand.Read(world, entity, transformId, sizeof(Transform)) : null;
            if (keepWorldTransform && _oldTransform is not null)
            {
                Trs placed = world.ComputeWorldTransform(entity);
                Trs inParent = newParent.IsNull ? Trs.Identity : world.ComputeWorldTransform(newParent);
                var local = Transform.From(placed.InLocalOf(inParent));
                _newTransform = new byte[sizeof(Transform)];
                new ReadOnlySpan<byte>(&local, sizeof(Transform)).CopyTo(_newTransform);
            }
            else
            {
                _newTransform = _oldTransform;
            }
        }

        world.SetParent(entity, newParent);
        Write(transformId, _newTransform);
    }

    public void Undo()
    {
        world.SetParent(entity, _oldParent);
        Write(world.Component<Transform>(), _oldTransform);
    }

    private unsafe void Write(Entity transformId, byte[]? bytes)
    {
        if (bytes is null)
        {
            return;
        }

        fixed (byte* data = bytes)
        {
            world.SetPointer(entity, transformId, bytes.Length, data);
        }
    }

    public bool TryMerge(IEditCommand following) => false;
}

/// <summary>Renames an entity. Names have to be unique among siblings, so the caller checks <see cref="IsFree"/>.</summary>
public sealed class RenameCommand(WorldRef worldRef, Entity entity, string newName) : IEditCommand
{
    private World world => worldRef.Current;

    private string? _old;

    public string Name => "Rename";

    /// <summary>Whether no other child of <paramref name="entity"/>'s parent (or no other root) has this name.</summary>
    public static bool IsFree(World world, Entity entity, string name)
    {
        Entity parent = world.GetParent(entity);
        IEnumerable<Entity> siblings = parent.IsNull ? world.Roots() : world.Children(parent);
        return !siblings.Any(s => s != entity && world.GetName(s) == name);
    }

    public void Apply()
    {
        _old = world.GetName(entity);
        world.SetName(entity, newName);
    }

    public void Undo()
    {
        if (_old is not null)
        {
            world.SetName(entity, _old);
        }
    }

    public bool TryMerge(IEditCommand following) => false;
}
