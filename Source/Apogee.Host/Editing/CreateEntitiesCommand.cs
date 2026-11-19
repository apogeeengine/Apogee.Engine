using System;
using System.Collections.Generic;
using System.Linq;
using Apogee.Ecs;
using Apogee.Reflection;

namespace Apogee.Host.Editing;

/// <summary>
/// Makes several entities, each with its branch (pasted from the clipboard, duplicated), as one step. <paramref name="create"/> returns the roots it made; undoing destroys them, and redoing brings
/// them back under the ids they had, so the selection and the other steps of the history still point at them.
/// </summary>
public sealed class CreateEntitiesCommand(WorldRef worldRef, string name, Func<IReadOnlyList<Entity>> create) : IEditCommand
{
    private World world => worldRef.Current;

    private List<WorldSnapshot>? _made;
    private Entity[] _roots = [];

    /// <summary>The roots this made (set by the first <see cref="Apply"/>).</summary>
    public IReadOnlyList<Entity> Entities => _roots;

    public string Name => name;

    public void Apply()
    {
        if (_made is null)
        {
            _roots = [.. create()];
            _made = [.. _roots.Select(world.SnapshotTree)];
        }
        else
        {
            foreach (WorldSnapshot branch in _made)
            {
                world.RestoreEntities(branch);
            }
        }
    }

    public void Undo()
    {
        foreach (Entity root in _roots)
        {
            if (world.IsAlive(root))
            {
                world.Destroy(root);
            }
        }
    }

    public bool TryMerge(IEditCommand following) => false;
}
