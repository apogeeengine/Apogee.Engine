using System.Collections.Generic;
using Apogee.Content.Xml;

namespace Apogee.Ecs;

/// <summary>
/// What a field of an entity named when its document was loaded, and could not be made: an entity that was never created (inside an instance whose prefab is missing, or an entity a kept-aside override
/// would add), written as the entity it was relative to and what is left. It is written back through the numbers the document has when it is saved, so the reference comes back when what it names does.
/// </summary>
/// <param name="Root">The entity whose address the text continues (an instance root); 0 when the text names no entity that exists, and is only the number of an entity a kept-aside element adds.</param>
/// <param name="Text">What follows <paramref name="Root"/>'s address (<c>2/3</c> below the instance at <c>5</c> is root 5, text <c>2/3</c>), or the number as it was written when there is no root.</param>
internal sealed record DanglingRef(ulong Root, string Text);

/// <summary>
/// What the world remembers about an entity because of the document it came from, though the entity itself cannot show it, and which saving must give back: the elements of an instance that could not be
/// applied, the reference text of an instance whose prefab attribute was no reference at all, the references of asset fields that did not load (the field is 0, and it is not an override of the prefab
/// to be so) and the references of entity fields to entities that were never made. Values are never changed once made, so a snapshot or a clone shares them.
/// </summary>
internal sealed class KeptData
{
    /// <summary>The elements the instance element held that could not be applied. Entity fields in them name entities by <see cref="Token"/> where they named one that exists; the nodes are the world's own copies.</summary>
    public IReadOnlyList<XmlNode> Unapplied { get; init; } = [];

    /// <summary>The <c>prefab</c> attribute as it was written, for an instance whose reference could not be read; null otherwise.</summary>
    public string? RawPrefab { get; init; }

    /// <summary>The reference text of each asset field (component, field) that named something that could not be loaded, which is why the field is 0.</summary>
    public IReadOnlyDictionary<(string Component, string Field), string>? Assets { get; init; }

    /// <summary>What each entity field (component, field) that names no entity was written as.</summary>
    public IReadOnlyDictionary<(string Component, string Field), DanglingRef>? Refs { get; init; }

    /// <summary>Whether there is nothing to remember.</summary>
    public bool IsEmpty => Unapplied.Count == 0 && RawPrefab is null && Assets is null && Refs is null;

    /// <summary>Starts an entity field's text inside kept elements that names an existing entity: this character, the entity id, and what follows its address (<c>/2/3</c>). No document can spell it.</summary>
    public const char Token = '\uE000';
}
