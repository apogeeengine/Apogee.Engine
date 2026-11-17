using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Ecs;
using Apogee.Reflection;

namespace Apogee.Editor.Editing;

/// <summary>
/// The entities the editor has selected, in the order they were picked, and the <see cref="Active"/> one: the last picked, which the inspector shows and the gizmo sits on. The selection is not an
/// undo step. It is kept as ids, so an entity that was destroyed drops out when read and is back when an undo brings it back under the same id, and it follows the world through Play and Stop
/// (the world is asked for each time it is read).
/// </summary>
public sealed class Selection(Func<World> world)
{
    private readonly List<Entity> _items = [];
    private Entity? _active;

    /// <summary>Raised when the selection changed (not when a selected entity merely stopped existing).</summary>
    public event Action? Changed;

    /// <summary>Counts every change: a panel that keeps something derived from the selection compares it to see whether to rebuild.</summary>
    public int Version { get; private set; }

    /// <summary>The selected entities that are alive, in the order they were selected.</summary>
    public IReadOnlyList<Entity> Items
    {
        get
        {
            World w = world();
            var alive = new List<Entity>(_items.Count);
            foreach (Entity entity in _items)
            {
                if (w.IsAlive(entity))
                {
                    alive.Add(entity);
                }
            }

            return alive;
        }
    }

    /// <summary>The last entity that was picked, or (when it is gone) the last one still selected; none when nothing is.</summary>
    public Entity? Active
    {
        get
        {
            World w = world();
            if (_active is { } active && w.IsAlive(active))
            {
                return active;
            }

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (w.IsAlive(_items[i]))
                {
                    return _items[i];
                }
            }

            return null;
        }
    }

    /// <summary>How many selected entities are alive.</summary>
    public int Count => Items.Count;

    public bool IsEmpty => Active is null;

    public bool Contains(Entity entity) => _items.Contains(entity) && world().IsAlive(entity);

    /// <summary>Selects only <paramref name="entity"/>, and makes it the active one.</summary>
    public void Set(Entity entity) => Set([entity], entity);

    /// <summary>
    /// Selects exactly <paramref name="entities"/> (duplicates and null entities are dropped). <paramref name="active"/> must be one of them to count; by default the last is the active one.
    /// </summary>
    public void Set(IEnumerable<Entity> entities, Entity? active = null)
    {
        var next = new List<Entity>();
        foreach (Entity entity in entities)
        {
            if (!entity.IsNull && !next.Contains(entity))
            {
                next.Add(entity);
            }
        }

        Entity? nextActive = active is { } a && next.Contains(a) ? a : next.Count > 0 ? next[^1] : null;
        if (next.Count == _items.Count && nextActive == _active && next.SequenceEqual(_items))
        {
            return;
        }

        _items.Clear();
        _items.AddRange(next);
        _active = nextActive;
        Raise();
    }

    /// <summary>Adds <paramref name="entity"/> (if it is not in already) and makes it the active one.</summary>
    public void Add(Entity entity)
    {
        if (entity.IsNull)
        {
            return;
        }

        if (!_items.Contains(entity))
        {
            _items.Add(entity);
        }
        else if (_active == entity)
        {
            return;
        }

        _active = entity;
        Raise();
    }

    /// <summary>Takes <paramref name="entity"/> out of the selection. When it was the active one the last selected takes over.</summary>
    public void Remove(Entity entity)
    {
        if (!_items.Remove(entity))
        {
            return;
        }

        if (_active == entity)
        {
            _active = _items.Count > 0 ? _items[^1] : null;
        }

        Raise();
    }

    /// <summary>Control-click: selects <paramref name="entity"/> when it is not selected (and makes it active), deselects it when it is.</summary>
    public void Toggle(Entity entity)
    {
        if (_items.Contains(entity))
        {
            Remove(entity);
        }
        else
        {
            Add(entity);
        }
    }

    /// <summary>Makes <paramref name="entity"/> the active one when it is selected; nothing otherwise.</summary>
    public void SetActive(Entity entity)
    {
        if (_items.Contains(entity) && _active != entity)
        {
            _active = entity;
            Raise();
        }
    }

    public void Clear()
    {
        if (_items.Count == 0 && _active is null)
        {
            return;
        }

        _items.Clear();
        _active = null;
        Raise();
    }

    /// <summary>
    /// Shift-click: selects the entities from <paramref name="from"/> to <paramref name="to"/> in <paramref name="visibleOrder"/> (the order the hierarchy shows them: nodes under a collapsed parent are not in
    /// it), replacing the selection, and makes <paramref name="to"/> the active one. When <paramref name="from"/> is not in the list it is the same as <see cref="Set(Entity)"/> on <paramref name="to"/>;
    /// when <paramref name="to"/> is not, nothing changes.
    /// </summary>
    public void SetRange(IReadOnlyList<Entity> visibleOrder, Entity from, Entity to)
    {
        int last = IndexOf(visibleOrder, to);
        if (last < 0)
        {
            return;
        }

        int first = IndexOf(visibleOrder, from);
        if (first < 0)
        {
            Set(to);
            return;
        }

        int low = Math.Min(first, last);
        int high = Math.Max(first, last);
        var range = new List<Entity>(high - low + 1);
        for (int i = low; i <= high; i++)
        {
            range.Add(visibleOrder[i]);
        }

        Set(range, to);
    }

    /// <summary>
    /// What a move, a copy or a delete acts on: the selected entities that have no selected ancestor (a branch is carried by its top; its selected descendants would move twice, or be copied twice).
    /// In selection order.
    /// </summary>
    public IReadOnlyList<Entity> TopLevel() => TopLevelOf(world(), Items);

    /// <summary>The same for any list of entities: the alive ones that have no ancestor in the list, in list order, each once.</summary>
    public static IReadOnlyList<Entity> TopLevelOf(World w, IEnumerable<Entity> entities)
    {
        var items = new List<Entity>();
        foreach (Entity entity in entities)
        {
            if (w.IsAlive(entity) && !items.Contains(entity))
            {
                items.Add(entity);
            }
        }

        var set = new HashSet<Entity>(items);
        var top = new List<Entity>(items.Count);
        foreach (Entity entity in items)
        {
            bool covered = false;
            for (Entity parent = w.GetParent(entity); !parent.IsNull; parent = w.GetParent(parent))
            {
                if (set.Contains(parent))
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
            {
                top.Add(entity);
            }
        }

        return top;
    }

    private static int IndexOf(IReadOnlyList<Entity> list, Entity entity)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == entity)
            {
                return i;
            }
        }

        return -1;
    }

    private void Raise()
    {
        Version++;
        Changed?.Invoke();
    }
}
