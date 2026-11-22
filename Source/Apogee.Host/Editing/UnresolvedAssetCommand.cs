using Apogee.Ecs;
using Apogee.Reflection;

namespace Apogee.Host.Editing;

/// <summary>
/// Drops the reference text that a loaded scene held for an asset field that could not be loaded (see <see cref="World.UnresolvedAssetOf"/>), and puts it back on undo. It goes together with the edit
/// that clears or replaces such a field: without it the next save would write the old reference again, as if the edit had not happened.
/// </summary>
public sealed class UnresolvedAssetCommand(WorldRef worldRef, Entity entity, string component, string field, string? text, string name) : IEditCommand
{
    private string? _before;

    /// <inheritdoc />
    public string Name => name;

    /// <summary>
    /// The command that forgets the text of <paramref name="field"/> of <paramref name="component"/> on <paramref name="entity"/>; null when there is none to forget.
    /// </summary>
    public static UnresolvedAssetCommand? Forget(WorldRef worldRef, Entity entity, string component, string field, string name) =>
        worldRef.Current.UnresolvedAssetOf(entity, component, field) is null ? null : new UnresolvedAssetCommand(worldRef, entity, component, field, null, name);

    /// <inheritdoc />
    public void Apply()
    {
        World world = worldRef.Current;
        _before = world.UnresolvedAssetOf(entity, component, field);
        world.SetUnresolvedAsset(entity, component, field, text);
    }

    /// <inheritdoc />
    public void Undo() => worldRef.Current.SetUnresolvedAsset(entity, component, field, _before);

    /// <inheritdoc />
    public bool TryMerge(IEditCommand following) => false;
}
