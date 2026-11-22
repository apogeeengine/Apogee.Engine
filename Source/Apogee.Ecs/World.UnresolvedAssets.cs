using System.Collections.Generic;
using Apogee.Reflection;

namespace Apogee.Ecs;

// What a loaded scene said about an asset field that could not be loaded (the field is 0, and saving writes the reference text again, see KeptData.Assets), made visible to an editor: it shows the text
// of a broken reference, and an edit that replaces the field has to drop the text too, or saving would bring the old reference back.
public sealed unsafe partial class World
{
    /// <summary>
    /// The reference text that a loaded document held for an asset field that could not be loaded (<c>res://models/gone.glb|uid://…</c>), or null when the field named nothing, or loaded. Such a field is 0
    /// in memory, and saving the scene writes this text back so that the reference comes back when the file does.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="component">The component's name.</param>
    /// <param name="field">The field's name.</param>
    public string? UnresolvedAssetOf(Entity entity, string component, string field) => UnresolvedAsset(entity, component, field);

    /// <summary>
    /// Replaces what <see cref="UnresolvedAssetOf"/> returns, or with null forgets it: an editor that clears or replaces a broken reference calls it so that the old text is not written by the next save, and
    /// calls it again with the old text to undo. Returns whether anything changed.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="component">The component's name.</param>
    /// <param name="field">The field's name.</param>
    /// <param name="text">The reference text to keep for the field, or null for none.</param>
    public bool SetUnresolvedAsset(Entity entity, string component, string field, string? text)
    {
        string? current = UnresolvedAsset(entity, component, field);
        if (current == text)
        {
            return false;
        }

        _kept.TryGetValue(entity.Id, out KeptData? kept);
        Dictionary<(string Component, string Field), string> assets = kept?.Assets is { } existing ? new(existing) : [];
        if (text is null)
        {
            assets.Remove((component, field));
        }
        else
        {
            assets[(component, field)] = text;
        }

        // KeptData is never changed once made (a snapshot or a clone shares it), so the entity gets a new one that holds everything else as it was.
        var next = new KeptData
        {
            Unapplied = kept?.Unapplied ?? [],
            RawPrefab = kept?.RawPrefab,
            Assets = assets.Count == 0 ? null : assets,
            Refs = kept?.Refs,
        };
        if (next.IsEmpty)
        {
            _kept.Remove(entity.Id);
        }
        else
        {
            _kept[entity.Id] = next;
        }

        return true;
    }
}
