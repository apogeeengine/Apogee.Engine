using System.Collections.Generic;
using Apogee.Reflection;

namespace Apogee.Ecs;

/// <summary>An instance that could not be brought up to date with its prefab, and why. It is exactly as it was.</summary>
/// <param name="Instance">The root of the instance.</param>
/// <param name="Message">Why it was left alone (the prefabs it needs nest too deep, expand to too many entities, or what it holds cannot be told apart from the prefab's).</param>
public readonly record struct PrefabUpdateFailure(Entity Instance, string Message);

/// <summary>
/// What <see cref="World.ApplyPrefabUpdates"/> did after prefab files changed: which files, which instances were brought up to date, and which entities that made or took away. It is what a tool
/// needs to react to a change it did not make; see <see cref="World.PrefabsChanged"/>.
/// </summary>
/// <remarks>
/// An editor treats the update as a change from outside, which is not undoable: the entities in <see cref="Destroyed"/> are gone (a command in the history that names one now names nothing, and one that
/// would bring such an entity back would bring it back as a plain entity), and the ones in <see cref="Created"/> are new. Keeping the history is safe for what the update did not touch; ending
/// it (or not undoing past it) is the safe choice for the instances in <see cref="Updated"/>.
/// </remarks>
public sealed class PrefabUpdate
{
    internal PrefabUpdate(IReadOnlyList<string> prefabs)
    {
        Prefabs = prefabs;
    }

    /// <summary>The prefab files that changed: the keys the library knows them by (their paths), in the order they were announced.</summary>
    public IReadOnlyList<string> Prefabs { get; }

    /// <summary>The roots of the instances that were brought up to date: every instance that is made, directly or through another prefab, from one of <see cref="Prefabs"/>. Their entities keep their ids.</summary>
    public List<Entity> Updated { get; } = [];

    /// <summary>The entities the update made: what the prefabs gained.</summary>
    public List<Entity> Created { get; } = [];

    /// <summary>The entities the update destroyed: what the prefabs lost, with what the instances had added below it (that is kept aside in the instance, and comes back with the entity).</summary>
    public List<Entity> Destroyed { get; } = [];

    /// <summary>The instances that were left as they were, and why.</summary>
    public List<PrefabUpdateFailure> Failed { get; } = [];

    /// <summary>Whether any instance was updated or left alone: false for a change nothing was made from.</summary>
    public bool Touched => Updated.Count > 0 || Failed.Count > 0;
}
