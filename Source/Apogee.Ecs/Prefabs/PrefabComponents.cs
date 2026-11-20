using Apogee.Reflection;

namespace Apogee.Ecs;

#pragma warning disable CA1051 // The fields of a component are its memory layout.

/// <summary>
/// Marks the root of a prefab instance: the entity <see cref="World.Instantiate(string, Entity)"/> returned. <see cref="Prefab"/> is a handle of the world's <see cref="PrefabLibrary"/>; a snapshot
/// holds the <see cref="Apogee.Core.Assets.AssetRef"/> it names instead (the <c>prefab</c> asset type), so it means the same prefab in another run. It is never written as a component: a scene writes the instance as one
/// <c>&lt;Entity prefab="…"&gt;</c> element with only what differs from its prefab.
/// </summary>
[Component]
public struct PrefabInstance
{
    /// <summary>The prefab the instance was made from, a handle of <see cref="World.Prefabs"/>.</summary>
    [Property(ReadOnly = true, Asset = "prefab")]
    public uint Prefab;
}

/// <summary>
/// Marks an entity that came from a prefab file (or was saved into one): <see cref="Id"/> is its number in that file, the number that stays the same each time the file is saved, and
/// <see cref="Prefab"/> is the file the number belongs to. It is never written to a scene file.
/// </summary>
[Component]
public struct PrefabMember
{
    /// <summary>The entity's id in the prefab file, one or more.</summary>
    [Property(ReadOnly = true)]
    public uint Id;

    /// <summary>The prefab file <see cref="Id"/> belongs to, a handle of <see cref="World.Prefabs"/>.</summary>
    [Property(ReadOnly = true, Asset = "prefab")]
    public uint Prefab;
}
